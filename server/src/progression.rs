use std::collections::{HashMap, HashSet};
use std::sync::OnceLock;
use std::time::Duration;

use serde::de::{Error as DeError, IgnoredAny, MapAccess, Visitor};
use serde::{Deserialize, Deserializer};
use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::action_ids::{normalize_authored_action_id, AuthoredActionId};
use crate::combat::{
    AuthoredStatusPayload, DamageType, StackPolicy, StatusApplication, StatusDispelType,
    StatusEffectKind, StatusPolarity, StatusStackGroupDefault,
};
use crate::combat_build_v2::{MASTERY_DAMAGE_BONUS, MASTERY_TRAIT_ID};
use crate::inventory::{
    equipment_combat_discipline_id_for_owner, equipment_modifier_totals_for_owner,
};
use crate::melee::sync_melee_attack_modifier_catalog;
use crate::relations::TARGET_AUDIENCE_HOSTILE;
use crate::spells::{is_on_named_cooldown, stamp_named_cooldown_for_duration};

#[allow(unused_imports)]
use crate::match_contract::match_combat_build_v2 as _;
#[allow(unused_imports)]
use crate::match_contract::match_discipline_configuration_v2 as _;
#[allow(unused_imports)]
use crate::match_contract::match_perk_selection_v2 as _;
#[allow(unused_imports)]
use crate::match_contract::match_selected_specialization_v2 as _;
#[allow(unused_imports)]
use crate::match_contract::match_spell_selection_v2 as _;
#[allow(unused_imports)]
use crate::match_contract::match_technique_selection_v2 as _;
#[allow(unused_imports)]
use crate::match_contract::match_trait_selection_v2 as _;
#[allow(unused_imports)]
use crate::player::player as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::progression::ability_catalog as _;
#[allow(unused_imports)]
use crate::progression::action_bar_slot_catalog as _;
#[allow(unused_imports)]
use crate::progression::action_presentation_catalog as _;
#[allow(unused_imports)]
use crate::progression::active_combat_build_discipline as _;
#[allow(unused_imports)]
use crate::progression::active_combat_mode as _;
#[allow(unused_imports)]
use crate::progression::auto_attack_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_mode_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_rule_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_vfx_cue_catalog as _;
#[allow(unused_imports)]
use crate::progression::melee_ability_catalog as _;
#[allow(unused_imports)]
use crate::progression::melee_gap_close_catalog as _;
#[allow(unused_imports)]
use crate::progression::resource_catalog as _;
#[allow(unused_imports)]
use crate::progression::stat_scaling_catalog as _;

pub(crate) const PROGRESSION_CATALOG_JSON: &str =
    include_str!(concat!(env!("OUT_DIR"), "/progression_catalog.shared.json"));
const RULE_DEFAULT_GLOBAL_COOLDOWN_MS: &str = "DEFAULT_GLOBAL_COOLDOWN_MS";
const FALLBACK_DEFAULT_GLOBAL_COOLDOWN_MS: u64 = 1500;
const MAX_DEFAULT_GLOBAL_COOLDOWN_MS: u64 = 60_000;
pub(crate) const COMBAT_PROFILE_ARCHER_BOW: &str = "ARCHER_BOW";
pub(crate) const COMBAT_PROFILE_DAGGERS: &str = "DAGGERS";
pub(crate) const COMBAT_PROFILE_STAFF: &str = "STAFF";
pub(crate) const COMBAT_PROFILE_SWORD_AND_SHIELD: &str = "SWORD_AND_SHIELD";
pub(crate) const COMBAT_PROFILE_TWO_HANDED_SWORD: &str = "TWO_HANDED_SWORD";
pub(crate) const RESOURCE_KIND_STAMINA: &str = "STAMINA";
pub(crate) const COMBAT_MODE_SHORT_DRAW: &str = "SHORT_DRAW";
pub(crate) const COMBAT_MODE_FULL_DRAW: &str = "FULL_DRAW";
pub(crate) const COMBAT_MODE_READY: &str = "READY";
pub(crate) const COMBAT_MODE_STEALTHED: &str = "STEALTHED";
pub(crate) const AUTO_ATTACK_MOVEMENT_ALLOW_MOVING: &str = "ALLOW_MOVING";
pub(crate) const AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE: &str =
    "RESET_CADENCE_ON_VOLUNTARY_MOVE";
const ABILITY_KIND_COMBAT_MODE_TOGGLE: &str = "COMBAT_MODE_TOGGLE";
#[cfg(test)]
const ARCHER_DRAW_MODE_TOGGLE_ABILITY_ID: &str = "ARCHER_DRAW_MODE_TOGGLE";
const ARCHER_MAVERICK_ABILITY_ID: &str = "ARCHER_MAVERICK";
const ARCHER_POINT_BLANK_ABILITY_ID: &str = "ARCHER_POINT_BLANK";
const ARCHER_CAREFUL_AIM_ABILITY_ID: &str = "ARCHER_CAREFUL_AIM";
pub(crate) const ARCHER_HEARTSEEKER_ABILITY_ID: &str = "ARCHER_HEARTSEEKER";
const ARCHER_PERFORATION_ABILITY_ID: &str = "ARCHER_PERFORATION";
pub(crate) const DAGGER_BLADE_TWISTING_ABILITY_ID: &str = "DAGGER_BLADE_TWISTING";
// Keep the original wire ID stable so existing action-bar assignments survive the rename.
const DAGGER_SHROUD_ABILITY_ID: &str = "DAGGER_STEALTH";
const SUBTLETY_FLEET_FOOTED_ABILITY_ID: &str = "SUBTLETY_FLEET_FOOTED";
const SUBTLETY_LINGERING_SHADE_ABILITY_ID: &str = "SUBTLETY_LINGERING_SHADE";
const SUBTLETY_OPPORTUNIST_ABILITY_ID: &str = "SUBTLETY_OPPORTUNIST";
const SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID: &str = "SUBTLETY_SURPRISE_ATTACKS";
const SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID: &str = "SUBTLETY_TACTICAL_ADVANTAGE";
const RUIN_FLAMING_WEAPON_ABILITY_ID: &str = "RUIN_FLAMING_WEAPON";
const BLIGHT_TOXIC_WEAPON_ABILITY_ID: &str = "BLIGHT_TOXIC_WEAPON";
const RUIN_WILDFIRE_ABILITY_ID: &str = "RUIN_WILDFIRE";
const SOULSTEALER_ABILITY_ID: &str = "SPELL_SOULSTEALER";
const RUIN_FURNACE_ABILITY_ID: &str = "RUIN_FURNACE";
const RUIN_ACCELERATION_ABILITY_ID: &str = "RUIN_ACCELERATION";
const RUIN_QUICKENING_ABILITY_ID: &str = "RUIN_QUICKENING";
const RUIN_CHAIN_REACTION_ABILITY_ID: &str = "RUIN_CHAIN_REACTION";
const RUIN_RIME_ABILITY_ID: &str = "RUIN_RIME";
const RUIN_FRACTURE_ABILITY_ID: &str = "RUIN_FRACTURE";
const RUIN_POTENTIAL_ABILITY_ID: &str = "RUIN_POTENTIAL";
const DIVINITY_FAITH_ABILITY_ID: &str = "DIVINITY_FAITH";
const PRIMAL_ADAPTATION_ABILITY_ID: &str = "PRIMAL_ADAPTATION";
const PRIMAL_PHOTOSYNTHESIS_ABILITY_ID: &str = "PRIMAL_PHOTOSYNTHESIS";
const PRIMAL_SLIPSTREAM_ABILITY_ID: &str = "PRIMAL_SLIPSTREAM";
// Keep the original wire ID stable so existing perk selections survive the rename.
const DAGGER_CADENCE_ABILITY_ID: &str = "DAGGER_CRESCENDO";
const DAGGER_RESTLESS_BLADES_ABILITY_ID: &str = "DAGGER_RESTLESS_BLADES";
const PLAYER_PASSIVE_RUNTIME_INVENTORY: [&str; 27] = [
    PRIMAL_ADAPTATION_ABILITY_ID,
    PRIMAL_PHOTOSYNTHESIS_ABILITY_ID,
    PRIMAL_SLIPSTREAM_ABILITY_ID,
    DIVINITY_FAITH_ABILITY_ID,
    RUIN_FLAMING_WEAPON_ABILITY_ID,
    RUIN_FURNACE_ABILITY_ID,
    RUIN_ACCELERATION_ABILITY_ID,
    RUIN_QUICKENING_ABILITY_ID,
    RUIN_CHAIN_REACTION_ABILITY_ID,
    RUIN_WILDFIRE_ABILITY_ID,
    RUIN_POTENTIAL_ABILITY_ID,
    RUIN_FRACTURE_ABILITY_ID,
    BLIGHT_TOXIC_WEAPON_ABILITY_ID,
    "WARRIOR_RESTLESS",
    "WARRIOR_BLOODLUST",
    ARCHER_MAVERICK_ABILITY_ID,
    ARCHER_POINT_BLANK_ABILITY_ID,
    ARCHER_CAREFUL_AIM_ABILITY_ID,
    ARCHER_PERFORATION_ABILITY_ID,
    SUBTLETY_OPPORTUNIST_ABILITY_ID,
    SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID,
    SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID,
    SUBTLETY_FLEET_FOOTED_ABILITY_ID,
    SUBTLETY_LINGERING_SHADE_ABILITY_ID,
    "DAGGER_FIGHTING_SPIRIT",
    DAGGER_CADENCE_ABILITY_ID,
    DAGGER_RESTLESS_BLADES_ABILITY_ID,
];

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct AllocatedStatTotals {
    pub might: u32,
    pub insight: u32,
    pub finesse: u32,
    pub quickness: u32,
    pub fortitude: u32,
}

#[derive(Deserialize)]
struct ProgressionCatalogFile {
    #[serde(default)]
    combat_modes: Vec<CombatModeDefinition>,
    #[serde(default)]
    resources: Vec<ResourceDefinition>,
    #[serde(default)]
    combat_rules: Vec<CombatRuleDefinition>,
    #[serde(default)]
    stat_scalings: Vec<StatScalingDefinition>,
    #[serde(default)]
    abilities: Vec<AbilityDefinition>,
    auto_attacks: Vec<AutoAttackDefinition>,
    #[serde(default)]
    auto_attack_replacements: Vec<AutoAttackReplacementDefinition>,
    action_presentations: Vec<ActionPresentationDefinition>,
    #[serde(default)]
    combat_vfx_cues: Vec<CombatVfxCueDefinition>,
    #[serde(default)]
    slots: Vec<ActionBarSlotDefinition>,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct CombatModeDefinition {
    combat_discipline_id: String,
    mode_id: String,
    display_name: String,
    is_default: bool,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
struct ResourceDefinition {
    resource_kind: String,
    display_name: String,
    color_hex: String,
    base_max: f32,
    max_per_insight: f32,
    base_regen_per_second: f32,
    regen_per_insight: f32,
    #[serde(default)]
    gain_multiplier_per_insight: f32,
    flat_decay_per_second: f32,
    #[serde(default)]
    out_of_combat_flat_decay_per_second: f32,
    decay_per_current_point_per_second: f32,
    gain_per_damage_taken: f32,
    #[serde(default)]
    gain_per_damage_dealt: f32,
    gain_per_melee_hit: f32,
    gain_per_spell_cast: f32,
    starts_full: bool,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
struct CombatRuleDefinition {
    combat_rule_id: String,
    scalar_value: f32,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
struct StatScalingDefinition {
    stat_scaling_id: String,
    stat_kind: String,
    effect_kind: String,
    scalar_value: f32,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct AbilityDefinition {
    ability_id: String,
    actor_scope: String,
    #[serde(default)]
    selection_kind: String,
    #[serde(default)]
    combat_discipline_id: Option<String>,
    #[serde(default)]
    spell_school_id: Option<String>,
    gameplay: AbilityGameplayDefinition,
    action_id: String,
    display_name: String,
    resource_kind: String,
    #[serde(default)]
    resource_cost: f32,
    #[serde(default)]
    ability_tags: Vec<String>,
    #[serde(default)]
    effects: Vec<AbilityEffectDefinition>,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct AbilityGameplayDefinition {
    kind: String,
    base_damage: Option<i32>,
    #[serde(default)]
    damage_type: Option<String>,
    #[serde(default)]
    target_health_damage_scaling: Option<TargetHealthDamageScalingDefinition>,
    applies_stagger: Option<bool>,
    range: Option<f32>,
    #[serde(default)]
    minimum_range: Option<f32>,
    cooldown_ms: Option<u64>,
    #[serde(default)]
    duration_ms: Option<u64>,
    #[serde(default)]
    break_on_attack: bool,
    #[serde(default)]
    break_on_direct_damage: bool,
    #[serde(default)]
    disabled_target_damage_bonus: f32,
    #[serde(default)]
    behind_target_damage_bonus: f32,
    #[serde(default)]
    isolated_damage_bonus: f32,
    #[serde(default)]
    isolated_ally_radius_meters: f32,
    #[serde(default)]
    point_blank_damage_bonus: f32,
    #[serde(default)]
    point_blank_full_bonus_range_meters: f32,
    #[serde(default)]
    point_blank_zero_bonus_range_meters: f32,
    #[serde(default)]
    stationary_target_damage_bonus: f32,
    #[serde(default)]
    stationary_target_window_ms: u64,
    #[serde(default)]
    stationary_target_max_displacement_meters: f32,
    #[serde(default)]
    stationary_target_auto_crit: bool,
    #[serde(default)]
    projectile_piercing: bool,
    #[serde(default)]
    dodge_recharge_time_reduction: f32,
    #[serde(default)]
    movement_return: Option<MovementReturnDefinition>,
    #[serde(default)]
    stealth_attack_stun_ms: u64,
    #[serde(default)]
    melee_fire_on_hit: Option<MeleeFireOnHitDefinition>,
    #[serde(default)]
    melee_poison_on_hit: Option<MeleePoisonOnHitDefinition>,
    #[serde(default)]
    fire_spell_ignite: Option<FireSpellIgniteDefinition>,
    #[serde(default)]
    consume_target_status: Option<ConsumeTargetStatusRule>,
    #[serde(default)]
    soulstealer_empowered_damage_bonus: f32,
    #[serde(default)]
    blade_twisting_bleed_damage_ratio: f32,
    #[serde(default)]
    blade_twisting_bleed_duration_ms: u64,
    #[serde(default)]
    blade_twisting_bleed_tick_interval_ms: u64,
    #[serde(default)]
    fire_damage_taken_mana_restore_ratio: f32,
    #[serde(default)]
    critical_strike_cooldown_reduction_ms: u64,
    #[serde(default)]
    movement_spell_cast_time_reduction: f32,
    #[serde(default)]
    movement_spell_cast_time_buff_duration_ms: u64,
    #[serde(default)]
    critical_spell_proc_action_id: String,
    #[serde(default)]
    auto_attack_proc_action_id: String,
    #[serde(default)]
    auto_attack_proc_chance: f32,
    #[serde(default)]
    frozen_melee_first_hit_damage_bonus: f32,
    #[serde(default)]
    noncritical_lightning_spell_crit_chance_bonus: f32,
    #[serde(default)]
    mana_regen_bonus: f32,
    #[serde(default)]
    adaptation_resistance_per_stack: f32,
    #[serde(default)]
    adaptation_duration_ms: u64,
    #[serde(default)]
    adaptation_max_stacks: u32,
    #[serde(default)]
    stationary_mana_regen_per_stack: f32,
    #[serde(default)]
    stationary_first_stack_delay_ms: u64,
    #[serde(default)]
    stationary_stack_interval_ms: u64,
    #[serde(default)]
    stationary_max_stacks: u32,
    #[serde(default)]
    other_movement_cooldown_reduction_ms: u64,
    uses_global_cooldown: Option<bool>,
    #[serde(default)]
    global_cooldown_ms: Option<u64>,
    parry_behavior: Option<String>,
    block_behavior: Option<String>,
    airborne_targeting_mode: Option<String>,
    // LOS is a targeting rule: absent means true for every hostile targeted
    // action; opt-out is authored explicitly per owner sign-off (S4).
    #[serde(default)]
    requires_target_los: Option<bool>,
    #[serde(default)]
    gap_close: Option<GapCloseDefinition>,
    #[serde(default)]
    melee_timed_movement: Option<MeleeTimedMovementDefinition>,
    #[serde(default)]
    melee_evasive_leap: Option<MeleeEvasiveLeapDefinition>,
    #[serde(default)]
    melee_channel: Option<MeleeChannelDefinition>,
    #[serde(default)]
    melee_impact_area: Option<MeleeImpactAreaDefinition>,
    #[serde(default)]
    melee_targeting: Option<MeleeTargetingDefinition>,
    #[serde(default)]
    melee_impact_effects: Vec<MeleeImpactEffectDefinition>,
    #[serde(default)]
    cast_time_ms: Option<u64>,
    #[serde(default)]
    cast_mobility: String,
    #[serde(default)]
    targeting: String,
    #[serde(default)]
    target_audience: String,
    requires_target: Option<bool>,
    #[serde(default)]
    aim_radius: Option<f32>,
    resource_cost: Option<f32>,
    #[serde(default)]
    primary_resource_gain_on_cast: f32,
    #[serde(default)]
    arms_auto_attack_on_cast: bool,
    #[serde(default)]
    delivery: Option<serde_json::Value>,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum ConsumeTargetStatusSourceScope {
    ApplierOnly,
    ApplierTeam,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum ConsumeTargetStatusFrequency {
    OncePerActionPerTarget,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum ConsumeTargetStatusStackMode {
    One,
    All,
}

impl ConsumeTargetStatusStackMode {
    pub(crate) fn maximum_stacks(self) -> Option<u32> {
        match self {
            Self::One => Some(1),
            Self::All => None,
        }
    }
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(deny_unknown_fields)]
pub(crate) struct ConsumeTargetStatusRule {
    pub status_kind: StatusEffectKind,
    pub status_stack_group: String,
    pub stack_mode: ConsumeTargetStatusStackMode,
    pub source_scope: ConsumeTargetStatusSourceScope,
    pub frequency: ConsumeTargetStatusFrequency,
    #[serde(deserialize_with = "deserialize_authored_f32")]
    pub damage_bonus_per_stack: f32,
}

impl ConsumeTargetStatusRule {
    fn validate(&self, ability_id: &str, ability_kind: &str) -> Result<(), String> {
        if !matches!(ability_kind, "MELEE" | "SPELL") {
            return Err(format!(
                "ability '{ability_id}' consume_target_status is only supported for MELEE or SPELL gameplay"
            ));
        }
        if self.status_kind != StatusEffectKind::Vulnerable {
            return Err(format!(
                "ability '{ability_id}' consume_target_status.status_kind must be VULNERABLE"
            ));
        }
        if self.status_stack_group.trim().is_empty() {
            return Err(format!(
                "ability '{ability_id}' consume_target_status.status_stack_group must not be empty"
            ));
        }
        if !self.damage_bonus_per_stack.is_finite() || self.damage_bonus_per_stack <= 0.0 {
            return Err(format!(
                "ability '{ability_id}' consume_target_status.damage_bonus_per_stack must be finite and positive"
            ));
        }
        Ok(())
    }
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MovementReturnDefinition {
    window_ms: u64,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeFireOnHitDefinition {
    bonus_damage: i32,
    burn_duration_ms: u64,
    burn_tick_interval_ms: u64,
    burn_tick_damage: i32,
    burn_max_stacks: u32,
    burn_status_stack_group: String,
    burn_dispel_types: Vec<StatusDispelType>,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleePoisonOnHitDefinition {
    #[serde(deserialize_with = "deserialize_authored_f32")]
    proc_chance: f32,
    poison_duration_ms: u64,
    poison_tick_interval_ms: u64,
    poison_tick_damage: i32,
    poison_max_stacks: u32,
    poison_status_stack_group: String,
    poison_dispel_types: Vec<StatusDispelType>,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct FireSpellIgniteDefinition {
    radius_meters: f32,
    burn_duration_ms: u64,
    burn_tick_interval_ms: u64,
    burn_tick_damage: i32,
    burn_max_stacks: u32,
    burn_status_stack_group: String,
    burn_dispel_types: Vec<StatusDispelType>,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct TargetHealthDamageScalingDefinition {
    min_multiplier: f32,
    max_multiplier: f32,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
// "Movement delivery" is the runtime domain name. The authored JSON field is
// gameplay.delivery on MOVEMENT abilities, not a separate movement_delivery key.
struct MovementDeliveryDefinition {
    kind: String,
    cooldown_ms: u64,
    uses_global_cooldown: bool,
    #[serde(default)]
    global_cooldown_ms: Option<u64>,
    cast_time_ms: u64,
    cast_mobility: String,
    targeting: String,
    #[serde(default)]
    target_audience: String,
    requires_target: bool,
    #[serde(default)]
    resource_cost: f32,
    #[serde(default)]
    arms_auto_attack_on_cast: bool,
    speed: f32,
    max_distance: f32,
    damage: i32,
    #[serde(default)]
    damage_type: String,
    radius: f32,
    block_behavior: String,
    #[serde(default)]
    parry_behavior: String,
    /// Self-directed kinds steer off the caster's own facing instead of a
    /// target, so they author the same movement vocabulary the timed-movement
    /// block already uses. Unused by DASH_TO_TARGET.
    #[serde(default)]
    direction: String,
    #[serde(default)]
    collision_policy: String,
    #[serde(default)]
    facing_policy: String,
    #[serde(default)]
    arrival: MovementDeliveryArrivalDefinition,
    #[serde(default)]
    impact_effects: Vec<MovementDeliveryImpactEffectDefinition>,
}

#[derive(Clone, Default, Deserialize)]
#[serde(deny_unknown_fields)]
struct MovementDeliveryArrivalDefinition {
    buffer: f32,
    epsilon: f32,
}

#[derive(Clone, Deserialize)]
#[serde(tag = "kind", rename_all = "SCREAMING_SNAKE_CASE", deny_unknown_fields)]
enum MovementDeliveryImpactEffectDefinition {
    ApplyStatus {
        status: MovementDeliveryImpactStatusDefinition,
    },
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MovementDeliveryImpactStatusDefinition {
    kind: String,
    duration_ms: u64,
    #[serde(default)]
    status_stack_group: Option<String>,
    #[serde(default, deserialize_with = "deserialize_authored_f32")]
    slow_pct: f32,
    #[serde(default)]
    tick_damage: i32,
    #[serde(default)]
    damage_type: String,
    #[serde(default)]
    tick_heal: i32,
    #[serde(default)]
    tick_interval_ms: u64,
    #[serde(default, deserialize_with = "deserialize_authored_f32")]
    modifier_scalar: f32,
    #[serde(default)]
    absorb_amount: i32,
    #[serde(default)]
    absorb_cap: i32,
    #[serde(default = "default_one_status_stack")]
    max_stacks: u32,
    #[serde(default = "default_status_stack_policy")]
    stack_policy: StackPolicy,
    #[serde(default)]
    dispel_types: Vec<StatusDispelType>,
}

#[derive(Clone, Deserialize)]
#[serde(tag = "kind", rename_all = "SCREAMING_SNAKE_CASE", deny_unknown_fields)]
enum MeleeImpactEffectDefinition {
    Knockback {
        #[serde(deserialize_with = "deserialize_authored_f32")]
        distance_meters: f32,
    },
    ApplyStatus {
        status: MeleeImpactStatusDefinition,
    },
    ApplyStatusOnHit {
        hit_index: u32,
        status: MeleeImpactStatusDefinition,
    },
    RemoveStatus {
        #[serde(default)]
        polarity: Option<StatusPolarity>,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
        #[serde(default = "default_one_status_stack")]
        max_count: u32,
    },
    RefreshRandomStatus {
        hit_index: u32,
        #[serde(default)]
        polarity: Option<StatusPolarity>,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeImpactStatusDefinition {
    kind: String,
    duration_ms: u64,
    #[serde(default)]
    status_stack_group: Option<String>,
    #[serde(default, deserialize_with = "deserialize_authored_f32")]
    slow_pct: f32,
    #[serde(default)]
    tick_damage: i32,
    #[serde(default)]
    damage_type: String,
    #[serde(default)]
    tick_heal: i32,
    #[serde(default)]
    tick_interval_ms: u64,
    #[serde(default, deserialize_with = "deserialize_authored_f32")]
    modifier_scalar: f32,
    #[serde(default)]
    absorb_amount: i32,
    #[serde(default)]
    absorb_cap: i32,
    #[serde(default = "default_one_status_stack")]
    max_stacks: u32,
    #[serde(default = "default_status_stack_policy")]
    stack_policy: StackPolicy,
    #[serde(default)]
    dispel_types: Vec<StatusDispelType>,
}

fn default_one_status_stack() -> u32 {
    1
}

fn default_status_stack_policy() -> StackPolicy {
    StackPolicy::Refresh
}

fn deserialize_authored_f32<'de, D>(deserializer: D) -> Result<f32, D::Error>
where
    D: Deserializer<'de>,
{
    struct AuthoredF32Visitor;

    impl<'de> Visitor<'de> for AuthoredF32Visitor {
        type Value = f32;

        fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            formatter.write_str("a finite f32-compatible JSON number")
        }

        fn visit_f64<E>(self, value: f64) -> Result<Self::Value, E>
        where
            E: DeError,
        {
            Ok(value as f32)
        }

        fn visit_i64<E>(self, value: i64) -> Result<Self::Value, E>
        where
            E: DeError,
        {
            Ok(value as f32)
        }

        fn visit_u64<E>(self, value: u64) -> Result<Self::Value, E>
        where
            E: DeError,
        {
            Ok(value as f32)
        }

        fn visit_str<E>(self, value: &str) -> Result<Self::Value, E>
        where
            E: DeError,
        {
            value
                .parse::<f32>()
                .map_err(|_| E::custom("invalid f32-compatible number string"))
        }

        fn visit_map<M>(self, mut map: M) -> Result<Self::Value, M::Error>
        where
            M: MapAccess<'de>,
        {
            let mut parsed = None;
            while let Some(key) = map.next_key::<String>()? {
                if key == "$serde_json::private::Number" {
                    let value = map.next_value::<String>()?;
                    parsed =
                        Some(value.parse::<f32>().map_err(|_| {
                            M::Error::custom("invalid f32-compatible number string")
                        })?);
                } else {
                    let _ = map.next_value::<IgnoredAny>()?;
                }
            }
            parsed.ok_or_else(|| M::Error::custom("expected serde_json arbitrary-precision number"))
        }
    }

    deserializer.deserialize_any(AuthoredF32Visitor)
}

fn status_application_from_definition(
    status: &MeleeImpactStatusDefinition,
    default: StatusStackGroupDefault,
) -> StatusApplication {
    status_application_from_parts(
        status.kind.as_str(),
        status.duration_ms,
        status.status_stack_group.clone(),
        status.slow_pct,
        status.tick_damage,
        status.damage_type.as_str(),
        status.tick_heal,
        status.tick_interval_ms,
        status.modifier_scalar,
        status.absorb_amount,
        status.absorb_cap,
        status.max_stacks,
        status.stack_policy,
        status.dispel_types.clone(),
        default,
    )
}

fn movement_status_application_from_definition(
    status: &MovementDeliveryImpactStatusDefinition,
    default: StatusStackGroupDefault,
) -> StatusApplication {
    status_application_from_parts(
        status.kind.as_str(),
        status.duration_ms,
        status.status_stack_group.clone(),
        status.slow_pct,
        status.tick_damage,
        status.damage_type.as_str(),
        status.tick_heal,
        status.tick_interval_ms,
        status.modifier_scalar,
        status.absorb_amount,
        status.absorb_cap,
        status.max_stacks,
        status.stack_policy,
        status.dispel_types.clone(),
        default,
    )
}

#[allow(clippy::too_many_arguments)]
fn status_application_from_parts(
    kind: &str,
    duration_ms: u64,
    status_stack_group: Option<String>,
    slow_pct: f32,
    tick_damage: i32,
    damage_type: &str,
    tick_heal: i32,
    tick_interval_ms: u64,
    modifier_scalar: f32,
    absorb_amount: i32,
    absorb_cap: i32,
    max_stacks: u32,
    stack_policy: StackPolicy,
    dispel_types: Vec<StatusDispelType>,
    default: StatusStackGroupDefault,
) -> StatusApplication {
    let normalized = normalize_identifier(kind);
    let kind = StatusEffectKind::from_wire(normalized.as_str())
        .unwrap_or_else(|| panic!("unknown status kind '{kind}'"));
    let mut authored = AuthoredStatusPayload::new_with_absorb(
        kind,
        slow_pct,
        tick_damage,
        tick_heal,
        tick_interval_ms,
        modifier_scalar,
        absorb_amount,
        absorb_cap,
    );
    authored.damage_type = DamageType::from_wire(damage_type);
    let payload = authored.payload();
    StatusApplication::new(
        payload,
        Duration::from_millis(duration_ms),
        status_stack_group,
        default,
        max_stacks,
        stack_policy,
    )
    .with_dispel_types(dispel_types)
}

fn authored_status_stack_group_default(kind: &str) -> StatusStackGroupDefault {
    match normalize_identifier(kind).as_str() {
        "STAGGER" => StatusStackGroupDefault::Global("STAGGER"),
        "ROOT" => StatusStackGroupDefault::Global("ROOT"),
        "DOT" => StatusStackGroupDefault::InstanceScopedActionSuffix("DOT"),
        "HOT" => StatusStackGroupDefault::InstanceScopedActionSuffix("HOT"),
        "STUN" => StatusStackGroupDefault::ActionSuffix("STUN"),
        "FREEZE" => StatusStackGroupDefault::ActionSuffix("FREEZE"),
        "INTIMIDATED" => StatusStackGroupDefault::ActionSuffix("INTIMIDATED"),
        "CONFUSION" => StatusStackGroupDefault::ActionSuffix("CONFUSION"),
        "KNOCKDOWN" => StatusStackGroupDefault::ActionSuffix("KNOCKDOWN"),
        "SLOW" => StatusStackGroupDefault::ActionSuffix("SLOW"),
        "MOVE_SPEED" => StatusStackGroupDefault::ActionSuffix("MOVE_SPEED"),
        "MOVE_SLOW_IMMUNITY" => StatusStackGroupDefault::ActionSuffix("MOVE_SLOW_IMMUNITY"),
        "MOVEMENT_IMPAIRING_IMMUNITY" => {
            StatusStackGroupDefault::ActionSuffix("MOVEMENT_IMPAIRING_IMMUNITY")
        }
        "STUN_IMMUNITY" => StatusStackGroupDefault::ActionSuffix("STUN_IMMUNITY"),
        "SILENCE" => StatusStackGroupDefault::ActionSuffix("SILENCE"),
        "DAMAGE_AMP" => StatusStackGroupDefault::ActionSuffix("DAMAGE_AMP"),
        "DIRECT_DAMAGE_AMP" => StatusStackGroupDefault::ActionSuffix("DIRECT_DAMAGE_AMP"),
        "HEALING_TAKEN_REDUCTION" => {
            StatusStackGroupDefault::ActionSuffix("HEALING_TAKEN_REDUCTION")
        }
        "MANA_REGEN" => StatusStackGroupDefault::ActionSuffix("MANA_REGEN"),
        "STAMINA_REGEN" => StatusStackGroupDefault::ActionSuffix("STAMINA_REGEN"),
        "MAX_HEALTH" => StatusStackGroupDefault::ActionSuffix("MAX_HEALTH"),
        "MAGIC_RESISTANCE" => StatusStackGroupDefault::ActionSuffix("MAGIC_RESISTANCE"),
        "THORNS" => StatusStackGroupDefault::ActionSuffix("THORNS"),
        "VENGEANCE_AURA" => StatusStackGroupDefault::ActionSuffix("VENGEANCE_AURA"),
        "DAMAGE_TAKEN_FROM_SOURCE_AMP" => {
            StatusStackGroupDefault::ActionSuffix("DAMAGE_TAKEN_FROM_SOURCE_AMP")
        }
        "MELEE_ATTACK_MODIFIER" => StatusStackGroupDefault::ActionSuffix("MELEE_ATTACK_MODIFIER"),
        "ATTACK_SPEED" => StatusStackGroupDefault::ActionSuffix("ATTACK_SPEED"),
        "CAST_SPEED" => StatusStackGroupDefault::ActionSuffix("CAST_SPEED"),
        _ => StatusStackGroupDefault::EffectKind,
    }
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct MovementDeliveryRuntime {
    pub ability_id: String,
    pub action_id: String,
    pub kind: String,
    pub cooldown_ms: u64,
    pub uses_global_cooldown: bool,
    pub global_cooldown_ms: u64,
    pub cast_time_ms: u64,
    pub cast_mobility: String,
    pub targeting: String,
    pub target_audience: String,
    pub requires_target: bool,
    pub requires_target_los: bool,
    pub resource_cost: f32,
    pub arms_auto_attack_on_cast: bool,
    pub speed: f32,
    pub max_distance: f32,
    pub damage: i32,
    pub damage_type: String,
    pub radius: f32,
    pub block_behavior: String,
    pub parry_behavior: String,
    pub direction: String,
    pub collision_policy: String,
    pub facing_policy: String,
    pub arrival_buffer: f32,
    pub arrival_epsilon: f32,
    pub impact_effects: Vec<MovementDeliveryImpactEffectRuntime>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct MovementDeliveryImpactEffectRuntime {
    pub status: StatusApplication,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) enum MeleeImpactEffectRuntime {
    Knockback {
        distance_meters: f32,
    },
    ApplyStatus {
        status: StatusApplication,
    },
    ApplyStatusOnHit {
        hit_index: u32,
        status: StatusApplication,
    },
    RemoveStatus {
        polarity: Option<StatusPolarity>,
        dispel_types: Vec<StatusDispelType>,
        max_count: u32,
    },
    RefreshRandomStatus {
        hit_index: u32,
        polarity: Option<StatusPolarity>,
        dispel_types: Vec<StatusDispelType>,
    },
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct MeleeTimedMovementRuntime {
    pub ability_id: String,
    pub kind: String,
    pub start_delay_ms: u64,
    pub direction: String,
    pub distance: f32,
    pub speed: f32,
    pub collision_policy: String,
    pub facing_policy: String,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct MeleeEvasiveLeapRuntime {
    pub duration_ms: u64,
    pub arc_height: f32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct MeleeChannelRuntime {
    pub duration_ms: u64,
    pub first_tick_delay_ms: u64,
    pub tick_interval_ms: u64,
    pub cancel_on_movement: bool,
    pub use_authored_hit_windows: bool,
    /// Channel ends early when the player releases the action key.
    pub holdable: bool,
    /// Resource charged per projectile release, not per impact: you pay when
    /// the shot leaves, whether or not it connects. 0 = free to sustain.
    pub resource_cost_per_release: f32,
    /// Resource the per-release cost draws from. Empty falls back to the
    /// ability's own kind. A martial ability stays STAMINA-costed while its
    /// channel drains a different pool, so sustain cost can be priced apart
    /// from press cost without breaking the martial/stamina invariant.
    pub resource_kind_per_release: &'static str,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct MeleeFireOnHitRuntime {
    pub bonus_damage: i32,
    pub burn_duration: Duration,
    pub burn_tick_interval: Duration,
    pub burn_tick_damage: i32,
    pub burn_max_stacks: u32,
    pub burn_status_stack_group: String,
    pub burn_dispel_types: Vec<StatusDispelType>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct MeleePoisonOnHitRuntime {
    pub proc_chance: f32,
    pub poison_duration: Duration,
    pub poison_tick_interval: Duration,
    pub poison_tick_damage: i32,
    pub poison_max_stacks: u32,
    pub poison_status_stack_group: String,
    pub poison_dispel_types: Vec<StatusDispelType>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct AutoAttackMeleeProcRuntime {
    pub action_id: String,
    pub combat_discipline_id: String,
    pub proc_chance: f32,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct FireSpellIgniteRuntime {
    pub radius_meters: f32,
    pub burn_duration: Duration,
    pub burn_tick_interval: Duration,
    pub burn_tick_damage: i32,
    pub burn_max_stacks: u32,
    pub burn_status_stack_group: String,
    pub burn_dispel_types: Vec<StatusDispelType>,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct GapCloseDefinition {
    kind: String,
    destination: String,
    speed: Option<f32>,
    arrival_buffer: f32,
    arrival_epsilon: f32,
    impact_range: f32,
    collision_policy: String,
    require_arrival_for_swing: bool,
    #[serde(default)]
    requires_target_facing: bool,
    #[serde(default)]
    activate_outside_impact_reach: bool,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeTimedMovementDefinition {
    kind: String,
    start_delay_ms: u64,
    direction: String,
    distance: f32,
    speed: f32,
    collision_policy: String,
    facing_policy: String,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeEvasiveLeapDefinition {
    duration_ms: u64,
    arc_height: f32,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeChannelDefinition {
    duration_ms: u64,
    first_tick_delay_ms: u64,
    tick_interval_ms: u64,
    cancel_on_movement: bool,
    #[serde(default)]
    use_authored_hit_windows: bool,
    #[serde(default)]
    holdable: bool,
    #[serde(default)]
    resource_cost_per_release: f32,
    #[serde(default)]
    resource_kind_per_release: String,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeImpactAreaDefinition {
    radius: f32,
    damage_multiplier: f32,
    #[serde(default)]
    hit_index: Option<u32>,
    #[serde(default)]
    include_primary_target: bool,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeTargetingDefinition {
    kind: String,
    #[serde(default)]
    requires_target: Option<bool>,
    #[serde(default)]
    angle_degrees: Option<f32>,
    #[serde(default)]
    width: Option<f32>,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct AutoAttackDefinition {
    combat_discipline_id: String,
    #[serde(default)]
    mode_id: String,
    action_id: String,
    base_damage: i32,
    #[serde(default)]
    damage_type: String,
    range: f32,
    cooldown_ms: u64,
    #[serde(default = "default_auto_attack_movement_policy")]
    movement_policy: String,
    uses_global_cooldown: bool,
    #[serde(default)]
    global_cooldown_ms: Option<u64>,
    parry_behavior: String,
    block_behavior: String,
    airborne_targeting_mode: String,
    applies_stagger: bool,
    #[serde(default)]
    requires_target_los: Option<bool>,
}

fn default_auto_attack_movement_policy() -> String {
    AUTO_ATTACK_MOVEMENT_ALLOW_MOVING.to_string()
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct AutoAttackReplacementDefinition {
    replacement_id: String,
    combat_discipline_id: String,
    authored_melee_strike_id: String,
    base_damage: i32,
    #[serde(default)]
    damage_type: String,
    range: f32,
    cooldown_ms: u64,
    uses_global_cooldown: bool,
    #[serde(default)]
    global_cooldown_ms: Option<u64>,
    parry_behavior: String,
    block_behavior: String,
    airborne_targeting_mode: String,
    applies_stagger: bool,
    #[serde(default)]
    requires_target_los: Option<bool>,
    #[serde(default)]
    grants_primary_resource_on_hit: bool,
    #[serde(default)]
    expires_ms: u64,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
struct AbilityEffectDefinition {
    trigger: String,
    kind: String,
    #[serde(default)]
    amount: f32,
}

#[derive(Clone, Deserialize)]
struct ActionPresentationDefinition {
    presentation_kind: String,
    presentation_id: String,
    display_name: String,
    #[serde(default)]
    description: String,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct CombatVfxCueDefinition {
    owner_kind: String,
    owner_id: String,
    // Author-time slot key (design doc §3.4), written to the JSON by the Unity generator/writer
    // (SpellCueCatalogWriter) so per-slot overrides and legacy-cue replacement can key on a stable
    // slot identity. It is deliberately NOT synced to the runtime CombatVfxCueCatalog table
    // (sync_combat_vfx_cue_catalog ignores it) and is unread at runtime — but this struct is
    // `deny_unknown_fields`, so the field MUST be declared here or the whole catalog fails to parse.
    #[serde(default)]
    #[allow(dead_code)]
    slot: String,
    trigger: String,
    #[serde(default)]
    hit_index: Option<u32>,
    anchor: String,
    vfx_id: String,
    #[serde(default)]
    attach_mode: String,
    #[serde(default)]
    vfx_role: String,
    #[serde(default)]
    lifecycle: String,
    #[serde(default)]
    projectile_sequence_index: Option<u32>,
    #[serde(default)]
    start_delay_ms: u64,
    #[serde(default)]
    scale: Option<f32>,
    #[serde(default)]
    duration_ms: u64,
    sort_order: u32,
}

#[derive(Clone, Debug, Default)]
struct CombatVfxPresentationManifest {
    projectile_body_by_ability: HashMap<(String, u32), CombatVfxProjectileBodySelection>,
    projectile_body_by_spell: HashMap<(String, u32), CombatVfxProjectileBodySelection>,
    projectile_trail_by_ability: HashMap<(String, u32), CombatVfxProjectileBodySelection>,
    projectile_trail_by_spell: HashMap<(String, u32), CombatVfxProjectileBodySelection>,
}

#[derive(Clone, Debug)]
struct CombatVfxProjectileBodySelection {
    vfx_id: String,
    selected_sort_order: u32,
    authored_count: usize,
}

impl CombatVfxPresentationManifest {
    fn build(catalog: &ProgressionCatalogFile) -> Self {
        let mut manifest = Self::default();
        for cue in &catalog.combat_vfx_cues {
            if normalize_identifier(cue.trigger.as_str()) != "SPELL_RELEASE" {
                continue;
            }

            let role = normalize_identifier(cue.vfx_role.as_str());
            if role != "PROJECTILE_BODY" && role != "PROJECTILE_TRAIL" {
                continue;
            }

            let owner_kind = normalize_identifier(cue.owner_kind.as_str());
            let owner_id = normalize_identifier(cue.owner_id.as_str());
            let sequence_index = cue.projectile_sequence_index.unwrap_or(0);
            let vfx_id = normalize_identifier(cue.vfx_id.as_str());
            let selections = match (role.as_str(), owner_kind.as_str()) {
                ("PROJECTILE_BODY", "ABILITY") => &mut manifest.projectile_body_by_ability,
                ("PROJECTILE_BODY", "SPELL") => &mut manifest.projectile_body_by_spell,
                ("PROJECTILE_TRAIL", "ABILITY") => &mut manifest.projectile_trail_by_ability,
                ("PROJECTILE_TRAIL", "SPELL") => &mut manifest.projectile_trail_by_spell,
                _ => continue,
            };
            Self::insert_projectile_body_selection(
                selections,
                owner_id,
                sequence_index,
                vfx_id,
                cue.sort_order,
            );
        }

        manifest
    }

    fn projectile_body_vfx_id_for_spell(
        &self,
        ability_id: &str,
        spell_kind: &str,
        projectile_sequence_index: u32,
    ) -> Option<&str> {
        let normalized_ability_id = normalize_identifier(ability_id);
        let normalized_spell_kind = normalize_identifier(spell_kind);
        self.projectile_body_by_ability
            .get(&(normalized_ability_id, projectile_sequence_index))
            .or_else(|| {
                self.projectile_body_by_spell
                    .get(&(normalized_spell_kind, projectile_sequence_index))
            })
            .map(|selection| selection.vfx_id.as_str())
    }

    fn projectile_trail_vfx_id_for_spell(
        &self,
        ability_id: &str,
        spell_kind: &str,
        projectile_sequence_index: u32,
    ) -> Option<&str> {
        let normalized_ability_id = normalize_identifier(ability_id);
        let normalized_spell_kind = normalize_identifier(spell_kind);
        self.projectile_trail_by_ability
            .get(&(normalized_ability_id, projectile_sequence_index))
            .or_else(|| {
                self.projectile_trail_by_spell
                    .get(&(normalized_spell_kind, projectile_sequence_index))
            })
            .map(|selection| selection.vfx_id.as_str())
    }

    #[cfg(test)]
    fn selected_projectile_body_cue_count(
        &self,
        ability_id: &str,
        spell_kind: &str,
        projectile_sequence_index: u32,
    ) -> usize {
        let normalized_ability_id = normalize_identifier(ability_id);
        let normalized_spell_kind = normalize_identifier(spell_kind);
        self.projectile_body_by_ability
            .get(&(normalized_ability_id, projectile_sequence_index))
            .or_else(|| {
                self.projectile_body_by_spell
                    .get(&(normalized_spell_kind, projectile_sequence_index))
            })
            .map_or(0, |selection| selection.authored_count)
    }

    #[cfg(test)]
    fn selected_projectile_trail_cue_count(
        &self,
        ability_id: &str,
        spell_kind: &str,
        projectile_sequence_index: u32,
    ) -> usize {
        let normalized_ability_id = normalize_identifier(ability_id);
        let normalized_spell_kind = normalize_identifier(spell_kind);
        self.projectile_trail_by_ability
            .get(&(normalized_ability_id, projectile_sequence_index))
            .or_else(|| {
                self.projectile_trail_by_spell
                    .get(&(normalized_spell_kind, projectile_sequence_index))
            })
            .map_or(0, |selection| selection.authored_count)
    }

    fn insert_projectile_body_selection(
        selections: &mut HashMap<(String, u32), CombatVfxProjectileBodySelection>,
        owner_id: String,
        projectile_sequence_index: u32,
        vfx_id: String,
        sort_order: u32,
    ) {
        let selection = selections
            .entry((owner_id, projectile_sequence_index))
            .or_insert(CombatVfxProjectileBodySelection {
                vfx_id: vfx_id.clone(),
                selected_sort_order: sort_order,
                authored_count: 0,
            });
        selection.authored_count = selection.authored_count.saturating_add(1);
        if sort_order < selection.selected_sort_order {
            selection.vfx_id = vfx_id;
            selection.selected_sort_order = sort_order;
        }
    }
}

#[derive(Clone, Deserialize)]
struct ActionBarSlotDefinition {
    slot_id: String,
    ui_row: u32,
    ui_col: u32,
    slot_group: String,
    #[serde(default)]
    accepts_tags: Vec<String>,
    sort_order: u32,
}

fn progression_catalog() -> &'static ProgressionCatalogFile {
    static CATALOG: OnceLock<ProgressionCatalogFile> = OnceLock::new();
    CATALOG.get_or_init(|| {
        let catalog: ProgressionCatalogFile = serde_json::from_str(PROGRESSION_CATALOG_JSON)
            .expect("progression_catalog.shared.json must remain valid and schema-compatible");
        validate_progression_catalog_authoring_contract(&catalog);
        catalog
    })
}

fn combat_vfx_presentation_manifest() -> &'static CombatVfxPresentationManifest {
    static MANIFEST: OnceLock<CombatVfxPresentationManifest> = OnceLock::new();
    MANIFEST.get_or_init(|| CombatVfxPresentationManifest::build(progression_catalog()))
}

fn validate_progression_catalog_authoring_contract(catalog: &ProgressionCatalogFile) {
    let authored_status_ids = authored_status_presentation_ids(catalog);
    for presentation in &catalog.action_presentations {
        let kind = normalize_identifier(presentation.presentation_kind.as_str());
        match kind.as_str() {
            "SPELL" => panic!(
                "SPELL presentation '{}' is derived from SPELL ability gameplay; author the ABILITY presentation instead",
                presentation.presentation_id
            ),
            "STATUS" => {
                let id = normalize_identifier(presentation.presentation_id.as_str());
                assert!(
                    authored_status_ids.contains(id.as_str()),
                    "STATUS presentation '{}' must reference a known status kind or authored status stack group",
                    presentation.presentation_id
                );
            }
            _ => {}
        }
    }

    for cue in &catalog.combat_vfx_cues {
        assert!(
            cue.scale.is_none(),
            "combat VFX cue '{}' authors scale in progression_catalog.shared.json; prefab scale belongs in CombatVFXRegistry",
            cue.vfx_id
        );
    }
}

fn authored_status_presentation_ids(catalog: &ProgressionCatalogFile) -> HashSet<String> {
    let mut ids = known_status_kind_ids();
    ids.insert("PHOTOSYNTHESIS".to_string());
    for ability in &catalog.abilities {
        if let Some(delivery) = ability.gameplay.delivery.as_ref() {
            collect_status_stack_groups(delivery, &mut ids);
        }
        for effect in &ability.gameplay.melee_impact_effects {
            match effect {
                MeleeImpactEffectDefinition::Knockback { .. } => {}
                MeleeImpactEffectDefinition::ApplyStatus { status }
                | MeleeImpactEffectDefinition::ApplyStatusOnHit { status, .. } => {
                    collect_optional_status_stack_group(
                        status.status_stack_group.as_deref(),
                        &mut ids,
                    );
                }
                MeleeImpactEffectDefinition::RemoveStatus { .. } => {}
                MeleeImpactEffectDefinition::RefreshRandomStatus { .. } => {}
            }
        }
        if let Some(melee_fire_on_hit) = ability.gameplay.melee_fire_on_hit.as_ref() {
            collect_optional_status_stack_group(
                Some(melee_fire_on_hit.burn_status_stack_group.as_str()),
                &mut ids,
            );
        }
        if let Some(melee_poison_on_hit) = ability.gameplay.melee_poison_on_hit.as_ref() {
            collect_optional_status_stack_group(
                Some(melee_poison_on_hit.poison_status_stack_group.as_str()),
                &mut ids,
            );
        }
        if let Some(fire_spell_ignite) = ability.gameplay.fire_spell_ignite.as_ref() {
            collect_optional_status_stack_group(
                Some(fire_spell_ignite.burn_status_stack_group.as_str()),
                &mut ids,
            );
        }
    }
    ids
}

fn known_status_kind_ids() -> HashSet<String> {
    [
        StatusEffectKind::Root,
        StatusEffectKind::Stun,
        StatusEffectKind::Freeze,
        StatusEffectKind::Intimidated,
        StatusEffectKind::Fear,
        StatusEffectKind::Confusion,
        StatusEffectKind::Stagger,
        StatusEffectKind::Knockdown,
        StatusEffectKind::Slow,
        StatusEffectKind::MoveSpeed,
        StatusEffectKind::Dot,
        StatusEffectKind::Hot,
        StatusEffectKind::MoveSlowImmunity,
        StatusEffectKind::MovementImpairingImmunity,
        StatusEffectKind::StunImmunity,
        StatusEffectKind::Silence,
        StatusEffectKind::DamageAmp,
        StatusEffectKind::DirectDamageAmp,
        StatusEffectKind::DamageTakenReduction,
        StatusEffectKind::PhysicalDamageReduction,
        StatusEffectKind::HealingTakenReduction,
        StatusEffectKind::DamageDealtReduction,
        StatusEffectKind::ManaRegen,
        StatusEffectKind::StaminaRegen,
        StatusEffectKind::MaxHealth,
        StatusEffectKind::MagicResistance,
        StatusEffectKind::Adaptation,
        StatusEffectKind::Doused,
        StatusEffectKind::KnockbackResistance,
        StatusEffectKind::Thorns,
        StatusEffectKind::VengeanceAura,
        StatusEffectKind::DamageTakenFromSourceAmp,
        StatusEffectKind::Hemorrhage,
        StatusEffectKind::Hemorrhaging,
        StatusEffectKind::MeleeAttackModifier,
        StatusEffectKind::AttackSpeed,
        StatusEffectKind::CastSpeed,
        StatusEffectKind::TemporaryHitpoints,
        StatusEffectKind::Berserking,
        StatusEffectKind::BattleTrance,
        StatusEffectKind::TargetedAbilityAvoidance,
        StatusEffectKind::AllAbilityAvoidance,
        StatusEffectKind::MirrorImage,
        StatusEffectKind::Vulnerable,
        StatusEffectKind::Cruelty,
        StatusEffectKind::Fulmination,
        StatusEffectKind::Quickening,
        StatusEffectKind::Rime,
        StatusEffectKind::SoulStolen,
        StatusEffectKind::BlightEmpowered,
        StatusEffectKind::Contagious,
        StatusEffectKind::OffBalance,
        StatusEffectKind::Reckoning,
        StatusEffectKind::DamageRedirect,
    ]
    .into_iter()
    .map(|kind| kind.as_str().to_string())
    .collect()
}

fn collect_status_stack_groups(value: &serde_json::Value, ids: &mut HashSet<String>) {
    match value {
        serde_json::Value::Object(map) => {
            if let Some(status_stack_group) = map.get("status_stack_group").and_then(|v| v.as_str())
            {
                let normalized = normalize_identifier(status_stack_group);
                if !normalized.is_empty() {
                    insert_status_stack_group_presentation_ids(ids, normalized);
                }
            }
            for nested in map.values() {
                collect_status_stack_groups(nested, ids);
            }
        }
        serde_json::Value::Array(items) => {
            for item in items {
                collect_status_stack_groups(item, ids);
            }
        }
        _ => {}
    }
}

fn collect_optional_status_stack_group(
    status_stack_group: Option<&str>,
    ids: &mut HashSet<String>,
) {
    if let Some(status_stack_group) = status_stack_group {
        let normalized = normalize_identifier(status_stack_group);
        if !normalized.is_empty() {
            insert_status_stack_group_presentation_ids(ids, normalized);
        }
    }
}

fn insert_status_stack_group_presentation_ids(ids: &mut HashSet<String>, normalized: String) {
    if let Some((base, _)) = normalized.split_once(":{") {
        if !base.is_empty() {
            ids.insert(base.to_string());
        }
    }
    ids.insert(normalized);
}

#[table(accessor = combat_mode_catalog, public)]
pub struct CombatModeCatalog {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub combat_discipline_id: String,
    pub mode_id: String,
    pub display_name: String,
    pub is_default: bool,
    pub sort_order: u32,
}

#[table(accessor = active_combat_mode, public)]
pub struct ActiveCombatMode {
    #[primary_key]
    pub owner: Identity,
    pub combat_discipline_id: String,
    pub mode_id: String,
    pub changed_at: Timestamp,
}

#[table(accessor = active_combat_build_discipline, public)]
pub struct ActiveCombatBuildDiscipline {
    #[primary_key]
    pub owner: Identity,
    pub combat_discipline_id: String,
    pub updated_at: Timestamp,
}

#[table(accessor = resource_catalog, public)]
pub struct ResourceCatalog {
    #[primary_key]
    pub resource_kind: String,
    pub display_name: String,
    pub color_hex: String,
    pub base_max: f32,
    pub max_per_insight: f32,
    pub base_regen_per_second: f32,
    pub regen_per_insight: f32,
    pub gain_multiplier_per_insight: f32,
    pub flat_decay_per_second: f32,
    pub out_of_combat_flat_decay_per_second: f32,
    pub decay_per_current_point_per_second: f32,
    pub gain_per_damage_taken: f32,
    pub gain_per_damage_dealt: f32,
    pub gain_per_melee_hit: f32,
    pub gain_per_spell_cast: f32,
    pub starts_full: bool,
    pub sort_order: u32,
}

#[table(accessor = combat_rule_catalog, public)]
pub struct CombatRuleCatalog {
    #[primary_key]
    pub combat_rule_id: String,
    pub scalar_value: f32,
    pub sort_order: u32,
}

#[table(accessor = stat_scaling_catalog, public)]
pub struct StatScalingCatalog {
    #[primary_key]
    pub stat_scaling_id: String,
    pub stat_kind: String,
    pub effect_kind: String,
    pub scalar_value: f32,
    pub sort_order: u32,
}

#[derive(Clone)]
#[table(accessor = ability_catalog, public)]
pub struct AbilityCatalog {
    #[primary_key]
    pub ability_id: String,
    pub actor_scope: String,
    #[index(btree)]
    pub combat_discipline_id: String,
    pub spell_school_id: String,
    pub selection_kind: String,
    pub ability_kind: String,
    pub action_id: String,
    pub display_name: String,
    pub resource_kind: String,
    pub resource_cost: f32,
    pub ability_tags: String,
    pub sort_order: u32,
}

#[table(accessor = melee_ability_catalog, public)]
pub struct MeleeAbilityCatalog {
    #[primary_key]
    pub ability_id: String,
    pub action_id: String,
    pub base_damage: i32,
    pub damage_type: String,
    pub target_health_damage_scaling_min_multiplier: f32,
    pub target_health_damage_scaling_max_multiplier: f32,
    pub applies_stagger: bool,
    pub range: f32,
    pub minimum_range: f32,
    pub cooldown_ms: u64,
    pub uses_global_cooldown: bool,
    pub global_cooldown_ms: u64,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub airborne_targeting_mode: String,
    pub targeting_kind: String,
    pub target_audience: String,
    pub requires_target: bool,
    pub requires_target_los: bool,
    pub targeting_radius: f32,
    pub targeting_range: f32,
    pub targeting_angle_degrees: f32,
    pub impact_area_radius: f32,
    pub impact_area_damage_multiplier: f32,
    pub impact_area_hit_index: i32,
    pub impact_area_include_primary_target: bool,
    #[default(0.0f32)]
    pub targeting_width: f32,
    #[default(0u64)]
    pub channel_duration_ms: u64,
    #[default(0u64)]
    pub channel_first_tick_delay_ms: u64,
    #[default(0u64)]
    pub channel_tick_interval_ms: u64,
    #[default(false)]
    pub channel_cancel_on_movement: bool,
    #[default(false)]
    pub channel_use_authored_hit_windows: bool,
}

#[table(accessor = melee_gap_close_catalog, public)]
pub struct MeleeGapCloseCatalog {
    #[primary_key]
    pub ability_id: String,
    pub kind: String,
    pub destination: String,
    pub speed: f32,
    pub arrival_buffer: f32,
    pub arrival_epsilon: f32,
    pub impact_range: f32,
    pub collision_policy: String,
    pub require_arrival_for_swing: bool,
    pub requires_target_facing: bool,
    pub activate_outside_impact_reach: bool,
}

#[table(accessor = auto_attack_catalog, public)]
pub struct AutoAttackCatalog {
    #[primary_key]
    pub key: String,
    pub combat_discipline_id: String,
    pub mode_id: String,
    pub action_id: String,
    pub base_damage: i32,
    pub damage_type: String,
    pub range: f32,
    pub cooldown_ms: u64,
    pub movement_policy: String,
    pub uses_global_cooldown: bool,
    pub global_cooldown_ms: u64,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub airborne_targeting_mode: String,
    pub applies_stagger: bool,
    pub requires_target_los: bool,
}

#[table(accessor = auto_attack_replacement_catalog)]
pub struct AutoAttackReplacementCatalog {
    #[primary_key]
    pub replacement_id: String,
    pub combat_discipline_id: String,
    pub authored_melee_strike_id: String,
    pub base_damage: i32,
    pub damage_type: String,
    pub range: f32,
    pub cooldown_ms: u64,
    pub uses_global_cooldown: bool,
    pub global_cooldown_ms: u64,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub airborne_targeting_mode: String,
    pub applies_stagger: bool,
    pub requires_target_los: bool,
    pub grants_primary_resource_on_hit: bool,
    pub expires_ms: u64,
    pub sort_order: u32,
}

#[table(accessor = action_presentation_catalog, public)]
pub struct ActionPresentationCatalog {
    #[primary_key]
    pub key: String,
    pub presentation_kind: String,
    pub presentation_id: String,
    pub display_name: String,
    pub description: String,
    pub sort_order: u32,
}

#[table(accessor = combat_vfx_cue_catalog, public)]
pub struct CombatVfxCueCatalog {
    #[primary_key]
    pub key: String,
    pub owner_kind: String,
    pub owner_id: String,
    pub trigger: String,
    pub hit_index: i32,
    pub anchor: String,
    pub vfx_id: String,
    pub attach_mode: String,
    pub vfx_role: String,
    pub lifecycle: String,
    pub projectile_sequence_index: i32,
    pub start_delay_ms: u64,
    pub scale: f32,
    pub duration_ms: u64,
    pub sort_order: u32,
}

#[table(accessor = action_bar_slot_catalog, public)]
pub struct ActionBarSlotCatalog {
    #[primary_key]
    pub slot_id: String,
    pub ui_row: u32,
    pub ui_col: u32,
    pub slot_group: String,
    pub accepts_tags: String,
    pub sort_order: u32,
}

#[reducer]
pub fn publish_progression_catalogs(ctx: &ReducerContext) -> Result<(), String> {
    sync_progression_catalogs(ctx);
    Ok(())
}

fn shroud_ability_definition() -> &'static AbilityDefinition {
    ability_definition(DAGGER_SHROUD_ABILITY_ID)
        .expect("Subtlety Shroud ability must remain authored")
}

fn shroud_is_active(active: &ActiveCombatMode) -> bool {
    active.combat_discipline_id == COMBAT_PROFILE_DAGGERS && active.mode_id == COMBAT_MODE_STEALTHED
}

fn shroud_has_expired(changed_at: Timestamp, now: Timestamp, duration_ms: u64) -> bool {
    now >= changed_at + Duration::from_millis(duration_ms.max(1))
}

fn exit_active_shroud(ctx: &ReducerContext, owner: Identity, now: Timestamp) -> bool {
    let Some(active) = ctx.db.active_combat_mode().owner().find(owner) else {
        return false;
    };
    if !shroud_is_active(&active) {
        return false;
    }

    upsert_active_combat_mode(
        ctx,
        ActiveCombatMode {
            owner,
            combat_discipline_id: COMBAT_PROFILE_DAGGERS.to_string(),
            mode_id: COMBAT_MODE_READY.to_string(),
            changed_at: now,
        },
    );
    true
}

pub(crate) fn arm_surprise_attacks_from_shroud(
    ctx: &ReducerContext,
    owner: Identity,
    action_instance_id: &str,
    now: Timestamp,
) -> bool {
    let shrouded = ctx
        .db
        .active_combat_mode()
        .owner()
        .find(owner)
        .is_some_and(|active| shroud_is_active(&active));
    if !shrouded
        || !player_has_selected_passive_ability(ctx, owner, SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID)
        || subtlety_surprise_attack_stun_duration().is_zero()
    {
        return false;
    }
    crate::combat::arm_surprise_attack_runtime(ctx, owner, action_instance_id, now);
    true
}

pub(crate) fn break_shroud_on_attack(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> bool {
    if !shroud_ability_definition().gameplay.break_on_attack {
        return false;
    }
    exit_active_shroud(ctx, owner, now)
}

pub(crate) fn break_shroud_on_direct_damage(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> bool {
    if !shroud_ability_definition().gameplay.break_on_direct_damage {
        return false;
    }
    exit_active_shroud(ctx, owner, now)
}

pub(crate) fn expire_shrouds(ctx: &ReducerContext, now: Timestamp) -> usize {
    let duration_ms = shroud_ability_definition()
        .gameplay
        .duration_ms
        .expect("Subtlety Shroud must define duration_ms");
    let expired_owners: Vec<_> = ctx
        .db
        .active_combat_mode()
        .iter()
        .filter(shroud_is_active)
        .filter(|active| shroud_has_expired(active.changed_at, now, duration_ms))
        .map(|active| active.owner)
        .collect();
    for owner in &expired_owners {
        exit_active_shroud(ctx, *owner, now);
    }
    expired_owners.len()
}

#[reducer]
pub fn set_combat_mode(ctx: &ReducerContext, mode_id: String) -> Result<(), String> {
    let owner = ctx.sender();
    let combat_discipline_id = derived_combat_discipline_id_for_owner(ctx, owner)
        .ok_or_else(|| "owner has no resolved combat profile".to_string())?;
    let mode_id = normalize_identifier(mode_id.as_str());
    if !combat_mode_is_valid_for_profile(ctx, combat_discipline_id.as_str(), mode_id.as_str()) {
        return Err(format!(
            "combat mode '{}' is not valid for profile '{}'",
            mode_id, combat_discipline_id
        ));
    }

    if let Some(active) = ctx.db.active_combat_mode().owner().find(owner) {
        if active.combat_discipline_id == combat_discipline_id && active.mode_id == mode_id {
            return Ok(());
        }
    }

    let entering_shroud =
        combat_discipline_id == COMBAT_PROFILE_DAGGERS && mode_id == COMBAT_MODE_STEALTHED;
    if entering_shroud {
        let shroud = shroud_ability_definition();
        if active_selectable_ability_for_ability_id(ctx, owner, shroud.ability_id.as_str())
            .is_none()
        {
            return Err("Shroud is not assigned on the current discipline action bar".to_string());
        }
        let cooldown_ms = shroud
            .gameplay
            .cooldown_ms
            .expect("Subtlety Shroud must define cooldown_ms");
        let cooldown_key = AuthoredActionId::new(shroud.action_id.as_str());
        if is_on_named_cooldown(ctx, owner, cooldown_key.as_str(), ctx.timestamp) {
            return Err("Shroud is on cooldown".to_string());
        }
        stamp_named_cooldown_for_duration(
            ctx,
            owner,
            cooldown_key.as_str(),
            Duration::from_millis(cooldown_ms),
            ctx.timestamp,
        );
    }

    upsert_active_combat_mode(
        ctx,
        ActiveCombatMode {
            owner,
            combat_discipline_id,
            mode_id,
            changed_at: ctx.timestamp,
        },
    );
    Ok(())
}

#[reducer]
pub fn activate_combat_build_discipline(
    ctx: &ReducerContext,
    combat_discipline_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    activate_frozen_combat_discipline(ctx, owner, combat_discipline_id.as_str())
}

fn frozen_combat_build_exists(ctx: &ReducerContext, owner: Identity) -> bool {
    ctx.db.match_combat_build_v2().owner().find(owner).is_some()
}

fn owner_requires_frozen_combat_build(ctx: &ReducerContext, owner: Identity) -> bool {
    ctx.db.player().identity().find(owner).is_some()
        && ctx
            .db
            .player_state()
            .player_id()
            .find(owner)
            .is_none_or(|state| !state.is_dummy)
}

fn frozen_build_contains_discipline(
    ctx: &ReducerContext,
    owner: Identity,
    combat_discipline_id: &str,
) -> bool {
    let combat_discipline_id = normalize_identifier(combat_discipline_id);
    ctx.db
        .match_selected_specialization_v2()
        .owner()
        .filter(owner)
        .any(|selected| selected.combat_discipline_id == combat_discipline_id)
}

pub(crate) fn activate_frozen_combat_discipline(
    ctx: &ReducerContext,
    owner: Identity,
    combat_discipline_id: &str,
) -> Result<(), String> {
    let combat_discipline_id = normalize_identifier(combat_discipline_id);
    if !frozen_combat_build_exists(ctx, owner) {
        return Err("owner has no frozen match combat build".to_string());
    }
    if !frozen_build_contains_discipline(ctx, owner, combat_discipline_id.as_str()) {
        return Err(format!(
            "combat discipline '{combat_discipline_id}' is not selected in the frozen match build"
        ));
    }
    let configuration = ctx
        .db
        .match_discipline_configuration_v2()
        .owner()
        .filter(owner)
        .find(|row| row.combat_discipline_id == combat_discipline_id)
        .ok_or_else(|| {
            format!("combat discipline '{combat_discipline_id}' has no frozen weapon configuration")
        })?;
    let main_hand_item_id = configuration.main_hand_item_id.as_deref().ok_or_else(|| {
        format!("combat discipline '{combat_discipline_id}' has no materialized main-hand weapon")
    })?;

    let changed_discipline = ctx
        .db
        .active_combat_build_discipline()
        .owner()
        .find(owner)
        .is_none_or(|active| active.combat_discipline_id != combat_discipline_id);
    if !changed_discipline {
        return Ok(());
    }

    crate::spells::fizzle_active_cast_for_interrupt(ctx, owner, ctx.timestamp);
    crate::auto_attack::clear_auto_attack_for_owner(ctx, owner);
    crate::melee::clear_queued_melee_followup(ctx, owner);
    crate::combat::clear_potential_state_for_owner(ctx, owner);

    crate::inventory::equip_materialized_combat_build_weapon_configuration(
        ctx,
        owner,
        combat_discipline_id.as_str(),
        configuration.main_hand_item_def_id.as_str(),
        configuration.main_hand_color_id.as_str(),
        configuration.off_hand_item_def_id.as_str(),
        configuration.off_hand_color_id.as_str(),
        main_hand_item_id,
        configuration.off_hand_item_id.as_deref(),
    )?;

    upsert_active_combat_build_discipline(
        ctx,
        ActiveCombatBuildDiscipline {
            owner,
            combat_discipline_id,
            updated_at: ctx.timestamp,
        },
    );
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
    Ok(())
}

pub(crate) fn sync_progression_catalogs(ctx: &ReducerContext) {
    sync_combat_mode_catalog(ctx);
    sync_resource_catalog(ctx);
    sync_combat_rule_catalog(ctx);
    sync_stat_scaling_catalog(ctx);
    sync_ability_catalog(ctx);
    sync_melee_ability_catalog(ctx);
    sync_melee_gap_close_catalog(ctx);
    sync_melee_attack_modifier_catalog(ctx);
    sync_auto_attack_catalog(ctx);
    sync_auto_attack_replacement_catalog(ctx);
    sync_action_presentation_catalog(ctx);
    sync_combat_vfx_cue_catalog(ctx);
    sync_action_bar_slot_catalog(ctx);
}

pub(crate) fn ensure_default_progression_for_identity(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<(), String> {
    let Some(_player) = ctx.db.player().identity().find(owner) else {
        return Err("player row not found".to_string());
    };
    if !frozen_combat_build_exists(ctx, owner) && owner_requires_frozen_combat_build(ctx, owner) {
        return Err("player has no frozen match combat build".to_string());
    }
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
    Ok(())
}

fn resolved_combat_discipline_id_for_ability_definition(
    ability: &AbilityDefinition,
) -> Option<String> {
    let combat_discipline_id = ability
        .combat_discipline_id
        .as_deref()
        .map(normalize_identifier)
        .unwrap_or_default();
    if combat_discipline_id.is_empty() {
        None
    } else {
        Some(combat_discipline_id)
    }
}

pub(crate) fn derived_combat_discipline_id_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<String> {
    if let Some(profile_id) = equipment_combat_discipline_id_for_owner(ctx, owner)
        .filter(|profile_id| combat_profile_exists(ctx, profile_id.as_str()))
    {
        return Some(profile_id);
    }
    if let Some(active) = ctx.db.active_combat_build_discipline().owner().find(owner) {
        if combat_profile_exists(ctx, active.combat_discipline_id.as_str()) {
            return Some(active.combat_discipline_id);
        }
    }
    None
}

pub(crate) fn sync_active_combat_mode_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    if let Some(combat_discipline_id) = derived_combat_discipline_id_for_owner(ctx, owner) {
        normalize_active_combat_mode_for_profile(ctx, owner, combat_discipline_id.as_str(), now);
    } else if ctx.db.active_combat_mode().owner().find(owner).is_some() {
        ctx.db.active_combat_mode().owner().delete(owner);
        crate::survival::on_survival_combat_mode_changed(ctx, owner);
    }
}

pub(crate) fn active_stat_totals_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> AllocatedStatTotals {
    equipment_modifier_totals_for_owner(ctx, owner).allocated_stat_totals()
}

pub(crate) fn primary_resource_kind_for_owner(
    _ctx: &ReducerContext,
    _owner: Identity,
) -> Option<String> {
    Some(RESOURCE_KIND_STAMINA.to_string())
}

pub(crate) fn sync_progression_for_equipment_change(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    sync_active_combat_mode_for_owner(ctx, owner, now);
}

pub(crate) fn resolved_auto_attack_mode_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    combat_discipline_id: &str,
) -> String {
    let combat_discipline_id = normalize_identifier(combat_discipline_id);
    if !combat_profile_has_modes(ctx, combat_discipline_id.as_str()) {
        return String::new();
    }

    if let Some(active) = ctx.db.active_combat_mode().owner().find(owner) {
        if active.combat_discipline_id == combat_discipline_id
            && combat_mode_is_valid_for_profile(
                ctx,
                combat_discipline_id.as_str(),
                active.mode_id.as_str(),
            )
        {
            return active.mode_id;
        }
    }

    default_combat_mode_for_profile(ctx, combat_discipline_id.as_str()).unwrap_or_default()
}

fn normalize_active_combat_mode_for_profile(
    ctx: &ReducerContext,
    owner: Identity,
    combat_discipline_id: &str,
    now: Timestamp,
) {
    let combat_discipline_id = normalize_identifier(combat_discipline_id);
    if combat_discipline_id.is_empty()
        || !combat_profile_has_modes(ctx, combat_discipline_id.as_str())
    {
        if ctx.db.active_combat_mode().owner().find(owner).is_some() {
            ctx.db.active_combat_mode().owner().delete(owner);
            crate::survival::on_survival_combat_mode_changed(ctx, owner);
        }
        return;
    }

    if let Some(active) = ctx.db.active_combat_mode().owner().find(owner) {
        if active.combat_discipline_id == combat_discipline_id
            && combat_mode_is_valid_for_profile(
                ctx,
                combat_discipline_id.as_str(),
                active.mode_id.as_str(),
            )
        {
            return;
        }
    }

    let Some(mode_id) = default_combat_mode_for_profile(ctx, combat_discipline_id.as_str()) else {
        return;
    };
    upsert_active_combat_mode(
        ctx,
        ActiveCombatMode {
            owner,
            combat_discipline_id,
            mode_id,
            changed_at: now,
        },
    );
}

pub(crate) fn combat_profile_has_modes(ctx: &ReducerContext, combat_discipline_id: &str) -> bool {
    let combat_discipline_id = normalize_identifier(combat_discipline_id);
    let has_modes = ctx
        .db
        .combat_mode_catalog()
        .combat_discipline_id()
        .filter(&combat_discipline_id)
        .next()
        .is_some();
    has_modes
}

fn combat_mode_is_valid_for_profile(
    ctx: &ReducerContext,
    combat_discipline_id: &str,
    mode_id: &str,
) -> bool {
    ctx.db
        .combat_mode_catalog()
        .key()
        .find(combat_mode_key(combat_discipline_id, mode_id))
        .is_some()
}

fn combat_profile_exists(ctx: &ReducerContext, combat_discipline_id: &str) -> bool {
    let _ = ctx;
    combat_profile_exists_for_authoring(combat_discipline_id)
}

fn combat_profile_exists_for_authoring(combat_discipline_id: &str) -> bool {
    matches!(
        normalize_identifier(combat_discipline_id).as_str(),
        COMBAT_PROFILE_DAGGERS
            | COMBAT_PROFILE_TWO_HANDED_SWORD
            | COMBAT_PROFILE_SWORD_AND_SHIELD
            | COMBAT_PROFILE_ARCHER_BOW
            | COMBAT_PROFILE_STAFF
    )
}

fn default_combat_mode_for_profile(
    ctx: &ReducerContext,
    combat_discipline_id: &str,
) -> Option<String> {
    let combat_discipline_id = normalize_identifier(combat_discipline_id);
    let mut modes: Vec<_> = ctx
        .db
        .combat_mode_catalog()
        .combat_discipline_id()
        .filter(&combat_discipline_id)
        .collect();
    modes.sort_by_key(|row| row.sort_order);
    modes
        .iter()
        .find(|row| row.is_default)
        .or_else(|| modes.first())
        .map(|row| row.mode_id.clone())
}

fn upsert_active_combat_mode(ctx: &ReducerContext, row: ActiveCombatMode) {
    let owner = row.owner;
    if ctx
        .db
        .active_combat_mode()
        .owner()
        .find(row.owner)
        .is_some()
    {
        ctx.db.active_combat_mode().owner().update(row);
    } else {
        ctx.db.active_combat_mode().insert(row);
    }
    crate::survival::on_survival_combat_mode_changed(ctx, owner);
}

fn upsert_active_combat_build_discipline(ctx: &ReducerContext, row: ActiveCombatBuildDiscipline) {
    if ctx
        .db
        .active_combat_build_discipline()
        .owner()
        .find(row.owner)
        .is_some()
    {
        ctx.db.active_combat_build_discipline().owner().update(row);
    } else {
        ctx.db.active_combat_build_discipline().insert(row);
    }
}

pub(crate) fn player_has_selected_passive_ability(
    ctx: &ReducerContext,
    owner: Identity,
    ability_id: &str,
) -> bool {
    if !frozen_combat_build_exists(ctx, owner) {
        return false;
    }
    let ability_id = normalize_identifier(ability_id);
    ctx.db
        .match_perk_selection_v2()
        .owner()
        .filter(owner)
        .find(|selection| selection.ability_id == ability_id)
        .is_some_and(|selection| {
            frozen_specialization_is_selected(ctx, owner, selection.specialization_id.as_str())
        })
}

pub(crate) fn player_has_selected_technique_ability(
    ctx: &ReducerContext,
    owner: Identity,
    ability_id: &str,
) -> bool {
    if !frozen_combat_build_exists(ctx, owner) {
        return false;
    }
    let ability_id = normalize_identifier(ability_id);
    ctx.db
        .match_technique_selection_v2()
        .owner()
        .filter(owner)
        .find(|selection| selection.ability_id == ability_id)
        .is_some_and(|selection| {
            frozen_specialization_is_selected(ctx, owner, selection.specialization_id.as_str())
        })
}

fn frozen_specialization_is_selected(
    ctx: &ReducerContext,
    owner: Identity,
    specialization_id: &str,
) -> bool {
    let specialization_id = normalize_identifier(specialization_id);
    ctx.db
        .match_selected_specialization_v2()
        .owner()
        .filter(owner)
        .any(|selected| selected.specialization_id == specialization_id)
}

pub(crate) fn mastery_outgoing_damage_multiplier(ctx: &ReducerContext, owner: Identity) -> f32 {
    let mastery_is_projected = ctx
        .db
        .match_combat_build_v2()
        .owner()
        .find(owner)
        .is_some_and(|build| build.mastery_active);
    let mastery_is_selected = ctx
        .db
        .match_trait_selection_v2()
        .owner()
        .filter(owner)
        .any(|selection| selection.ability_id == MASTERY_TRAIT_ID);
    if mastery_is_projected && mastery_is_selected {
        1.0 + MASTERY_DAMAGE_BONUS
    } else {
        1.0
    }
}

/// Tests durable ownership of an active inside the frozen build without
/// granting permission to invoke it from the wrong current action bar. This is
/// used only to reconcile persistent state created by an earlier authorized
/// cast.
pub(crate) fn player_build_contains_active_ability(
    ctx: &ReducerContext,
    owner: Identity,
    ability_id: &str,
) -> bool {
    if !frozen_combat_build_exists(ctx, owner) {
        return false;
    }
    let ability_id = normalize_identifier(ability_id);
    ctx.db
        .match_technique_selection_v2()
        .owner()
        .filter(owner)
        .any(|selection| {
            selection.ability_id == ability_id
                && frozen_specialization_is_selected(
                    ctx,
                    owner,
                    selection.specialization_id.as_str(),
                )
        })
        || ctx
            .db
            .match_spell_selection_v2()
            .owner()
            .filter(owner)
            .any(|selection| {
                selection.ability_id == ability_id
                    && frozen_specialization_is_selected(
                        ctx,
                        owner,
                        selection.specialization_id.as_str(),
                    )
            })
}

pub(crate) fn action_id_is_selectable_action_bar_action(
    ctx: &ReducerContext,
    action_id: &AuthoredActionId,
) -> bool {
    ctx.db
        .ability_catalog()
        .iter()
        .any(|row| row.action_id == action_id.as_str())
}

fn resolved_combat_discipline_id_for_ability_catalog(
    _ctx: &ReducerContext,
    ability: &AbilityCatalog,
) -> Option<String> {
    let combat_discipline_id = normalize_identifier(ability.combat_discipline_id.as_str());
    if combat_discipline_id.is_empty() {
        None
    } else {
        Some(combat_discipline_id)
    }
}

fn ability_catalog_matches_combat_profile(
    ctx: &ReducerContext,
    ability: &AbilityCatalog,
    combat_discipline_id: &str,
) -> bool {
    let normalized_combat_discipline_id = normalize_identifier(combat_discipline_id);
    resolved_combat_discipline_id_for_ability_catalog(ctx, ability)
        .is_some_and(|ability_profile_id| ability_profile_id == normalized_combat_discipline_id)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum FrozenActiveAuthorizationDenial {
    NoFrozenBuild,
    NoActiveDiscipline,
    ActiveDisciplineNotSelected,
    WrongWeapon,
    UnselectedFeature,
    InvalidCatalogMetadata,
}

impl FrozenActiveAuthorizationDenial {
    fn as_str(self) -> &'static str {
        match self {
            Self::NoFrozenBuild => "NO_FROZEN_BUILD",
            Self::NoActiveDiscipline => "NO_ACTIVE_DISCIPLINE",
            Self::ActiveDisciplineNotSelected => "ACTIVE_DISCIPLINE_NOT_SELECTED",
            Self::WrongWeapon => "WRONG_WEAPON",
            Self::UnselectedFeature => "UNSELECTED_FEATURE",
            Self::InvalidCatalogMetadata => "INVALID_CATALOG_METADATA",
        }
    }
}

fn authorize_v2_active_selection(
    spell_is_selected: bool,
    technique_is_selected: bool,
    technique_weapon_is_equipped: bool,
) -> Result<(), FrozenActiveAuthorizationDenial> {
    if spell_is_selected || (technique_is_selected && technique_weapon_is_equipped) {
        Ok(())
    } else if technique_is_selected {
        Err(FrozenActiveAuthorizationDenial::WrongWeapon)
    } else {
        Err(FrozenActiveAuthorizationDenial::UnselectedFeature)
    }
}

fn frozen_active_ability_for_request(
    ctx: &ReducerContext,
    owner: Identity,
    request_matches: impl Fn(&AbilityCatalog) -> bool,
) -> Result<AbilityCatalog, FrozenActiveAuthorizationDenial> {
    if !frozen_combat_build_exists(ctx, owner) {
        return Err(FrozenActiveAuthorizationDenial::NoFrozenBuild);
    }
    let active_discipline_id = ctx
        .db
        .active_combat_build_discipline()
        .owner()
        .find(owner)
        .map(|row| normalize_identifier(row.combat_discipline_id.as_str()))
        .filter(|id| !id.is_empty())
        .ok_or(FrozenActiveAuthorizationDenial::NoActiveDiscipline)?;
    if !frozen_build_contains_discipline(ctx, owner, active_discipline_id.as_str()) {
        return Err(FrozenActiveAuthorizationDenial::ActiveDisciplineNotSelected);
    }

    let requested_abilities: Vec<_> = ctx
        .db
        .ability_catalog()
        .iter()
        .filter(|ability| request_matches(ability))
        .collect();
    if requested_abilities.is_empty() {
        return Err(FrozenActiveAuthorizationDenial::InvalidCatalogMetadata);
    }

    for ability in &requested_abilities {
        let selected_spell = ctx
            .db
            .match_spell_selection_v2()
            .owner()
            .filter(owner)
            .find(|selection| selection.ability_id == ability.ability_id)
            .filter(|selection| {
                frozen_specialization_is_selected(ctx, owner, selection.specialization_id.as_str())
            });
        let selected_technique = ctx
            .db
            .match_technique_selection_v2()
            .owner()
            .filter(owner)
            .find(|selection| selection.ability_id == ability.ability_id)
            .filter(|selection| {
                frozen_specialization_is_selected(ctx, owner, selection.specialization_id.as_str())
            });
        match authorize_v2_active_selection(
            selected_spell.is_some(),
            selected_technique.is_some(),
            selected_technique
                .as_ref()
                .is_some_and(|selection| selection.combat_discipline_id == active_discipline_id),
        ) {
            Ok(()) => return Ok(ability.clone()),
            Err(FrozenActiveAuthorizationDenial::WrongWeapon) => {
                return Err(FrozenActiveAuthorizationDenial::WrongWeapon)
            }
            Err(_) => {}
        }
    }

    Err(FrozenActiveAuthorizationDenial::UnselectedFeature)
}

fn log_frozen_active_denial(
    owner: Identity,
    requested_kind: &str,
    requested_id: &str,
    denial: FrozenActiveAuthorizationDenial,
) {
    log::warn!(
        "[COMBAT_BUILD_AUTH] decision=DENY owner={} request_kind={} request_id={} reason={}",
        owner.to_hex(),
        requested_kind,
        requested_id,
        denial.as_str()
    );
}

pub(crate) fn active_selectable_ability_for_authored_action(
    ctx: &ReducerContext,
    owner: Identity,
    authored_action_id: &AuthoredActionId,
) -> Option<AbilityCatalog> {
    if frozen_combat_build_exists(ctx, owner) {
        return match frozen_active_ability_for_request(ctx, owner, |ability| {
            ability.action_id == authored_action_id.as_str()
        }) {
            Ok(ability) => Some(ability),
            Err(denial) => {
                log_frozen_active_denial(
                    owner,
                    "AUTHORED_ACTION",
                    authored_action_id.as_str(),
                    denial,
                );
                None
            }
        };
    }
    if owner_requires_frozen_combat_build(ctx, owner) {
        log_frozen_active_denial(
            owner,
            "AUTHORED_ACTION",
            authored_action_id.as_str(),
            FrozenActiveAuthorizationDenial::NoFrozenBuild,
        );
        return None;
    }
    let combat_discipline_id = derived_combat_discipline_id_for_owner(ctx, owner)?;
    ctx.db.ability_catalog().iter().find(|ability| {
        ability.action_id == authored_action_id.as_str()
            && ability_catalog_matches_combat_profile(ctx, ability, combat_discipline_id.as_str())
    })
}

pub(crate) fn active_selectable_ability_for_ability_id(
    ctx: &ReducerContext,
    owner: Identity,
    ability_id: &str,
) -> Option<AbilityCatalog> {
    let normalized_ability_id = normalize_identifier(ability_id);
    if frozen_combat_build_exists(ctx, owner) {
        return match frozen_active_ability_for_request(ctx, owner, |ability| {
            ability.ability_id == normalized_ability_id
        }) {
            Ok(ability) => Some(ability),
            Err(denial) => {
                log_frozen_active_denial(owner, "ABILITY", normalized_ability_id.as_str(), denial);
                None
            }
        };
    }
    if owner_requires_frozen_combat_build(ctx, owner) {
        log_frozen_active_denial(
            owner,
            "ABILITY",
            normalized_ability_id.as_str(),
            FrozenActiveAuthorizationDenial::NoFrozenBuild,
        );
        return None;
    }
    let combat_discipline_id = derived_combat_discipline_id_for_owner(ctx, owner)?;
    ctx.db
        .ability_catalog()
        .ability_id()
        .find(normalized_ability_id)
        .filter(|ability| {
            ability_catalog_matches_combat_profile(ctx, ability, combat_discipline_id.as_str())
        })
}

pub(crate) fn active_action_bar_assignment_debug_summary(
    ctx: &ReducerContext,
    owner: Identity,
) -> String {
    let techniques: Vec<String> = ctx
        .db
        .match_technique_selection_v2()
        .owner()
        .filter(owner)
        .map(|selection| {
            format!(
                "{}:{}:{}",
                selection.combat_discipline_id, selection.bar_order, selection.ability_id
            )
        })
        .collect();
    let spells: Vec<String> = ctx
        .db
        .match_spell_selection_v2()
        .owner()
        .filter(owner)
        .map(|selection| format!("{}:{}", selection.bar_order, selection.ability_id))
        .collect();

    format!(
        "match_technique_bar=[{}] match_spell_bar=[{}]",
        techniques.join(","),
        spells.join(",")
    )
}

pub(crate) fn primary_resource_gain_on_action_accept(ability_id: &str) -> f32 {
    ability_definition(ability_id)
        .map(|definition| {
            definition
                .effects
                .iter()
                .filter(|effect| normalize_identifier(effect.trigger.as_str()) == "ON_ACCEPTED")
                .filter(|effect| {
                    normalize_identifier(effect.kind.as_str()) == "GRANT_PRIMARY_RESOURCE"
                })
                .map(|effect| effect.amount.max(0.0))
                .sum()
        })
        .unwrap_or(0.0)
}

pub(crate) fn melee_impact_effects_for_ability_id(
    ability_id: &str,
) -> Vec<MeleeImpactEffectRuntime> {
    ability_definition(ability_id)
        .filter(|definition| ability_gameplay_kind(definition) == "MELEE")
        .map(|definition| {
            definition
                .gameplay
                .melee_impact_effects
                .iter()
                .map(|effect| match effect {
                    MeleeImpactEffectDefinition::Knockback { distance_meters } => {
                        MeleeImpactEffectRuntime::Knockback {
                            distance_meters: *distance_meters,
                        }
                    }
                    MeleeImpactEffectDefinition::ApplyStatus { status } => {
                        MeleeImpactEffectRuntime::ApplyStatus {
                            status: status_application_from_definition(
                                status,
                                authored_status_stack_group_default(status.kind.as_str()),
                            ),
                        }
                    }
                    MeleeImpactEffectDefinition::ApplyStatusOnHit { hit_index, status } => {
                        MeleeImpactEffectRuntime::ApplyStatusOnHit {
                            hit_index: *hit_index,
                            status: status_application_from_definition(
                                status,
                                authored_status_stack_group_default(status.kind.as_str()),
                            ),
                        }
                    }
                    MeleeImpactEffectDefinition::RemoveStatus {
                        polarity,
                        dispel_types,
                        max_count,
                    } => MeleeImpactEffectRuntime::RemoveStatus {
                        polarity: *polarity,
                        dispel_types: dispel_types.clone(),
                        max_count: *max_count,
                    },
                    MeleeImpactEffectDefinition::RefreshRandomStatus {
                        hit_index,
                        polarity,
                        dispel_types,
                    } => MeleeImpactEffectRuntime::RefreshRandomStatus {
                        hit_index: *hit_index,
                        polarity: *polarity,
                        dispel_types: dispel_types.clone(),
                    },
                })
                .collect()
        })
        .unwrap_or_default()
}

pub(crate) fn melee_timed_movement_for_ability_id(
    ability_id: &str,
) -> Option<MeleeTimedMovementRuntime> {
    let definition = ability_definition(ability_id)?;
    if ability_gameplay_kind(definition) != "MELEE" {
        return None;
    }
    let movement = definition.gameplay.melee_timed_movement.as_ref()?;
    Some(MeleeTimedMovementRuntime {
        ability_id: normalize_identifier(definition.ability_id.as_str()),
        kind: normalize_identifier(movement.kind.as_str()),
        start_delay_ms: movement.start_delay_ms,
        direction: normalize_identifier(movement.direction.as_str()),
        distance: movement.distance.max(0.0),
        speed: movement.speed.max(0.0),
        collision_policy: normalize_identifier(movement.collision_policy.as_str()),
        facing_policy: normalize_identifier(movement.facing_policy.as_str()),
    })
}

pub(crate) fn melee_evasive_leap_for_ability_id(
    ability_id: &str,
) -> Option<MeleeEvasiveLeapRuntime> {
    let definition = ability_definition(ability_id)?;
    if ability_gameplay_kind(definition) != "MELEE" {
        return None;
    }
    let leap = definition.gameplay.melee_evasive_leap.as_ref()?;
    Some(MeleeEvasiveLeapRuntime {
        duration_ms: leap.duration_ms,
        arc_height: leap.arc_height.max(0.0),
    })
}

pub(crate) fn melee_channel_for_ability_id(ability_id: &str) -> Option<MeleeChannelRuntime> {
    let definition = ability_definition(ability_id)?;
    if ability_gameplay_kind(definition) != "MELEE" {
        return None;
    }
    let channel = definition.gameplay.melee_channel.as_ref()?;
    Some(melee_channel_runtime_from_definition(channel))
}

fn melee_channel_runtime_from_definition(channel: &MeleeChannelDefinition) -> MeleeChannelRuntime {
    MeleeChannelRuntime {
        duration_ms: channel.duration_ms,
        first_tick_delay_ms: channel.first_tick_delay_ms,
        tick_interval_ms: channel.tick_interval_ms,
        cancel_on_movement: channel.cancel_on_movement,
        use_authored_hit_windows: channel.use_authored_hit_windows,
        holdable: channel.holdable,
        resource_cost_per_release: channel.resource_cost_per_release.max(0.0),
        resource_kind_per_release: match normalize_identifier(
            channel.resource_kind_per_release.as_str(),
        )
        .as_str()
        {
            "MANA" => "MANA",
            "STAMINA" => "STAMINA",
            _ => "",
        },
    }
}

/// Melee channel for the ability that exposes `authored_action_id` on
/// `combat_profile`. `sync_melee_definitions` publishes from the manifest,
/// which has no ability rows, so the channel has to be found from this side.
pub(crate) fn melee_channel_for_authored_strike(
    combat_profile: &str,
    authored_action_id: &str,
) -> Option<MeleeChannelRuntime> {
    let combat_profile = normalize_identifier(combat_profile);
    let authored_action_id = normalize_identifier(authored_action_id);
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            ability_gameplay_kind(ability) == "MELEE"
                && normalize_identifier(ability.action_id.as_str()) == authored_action_id
                && resolved_combat_discipline_id_for_ability_definition(ability)
                    .is_some_and(|ability_profile| ability_profile == combat_profile)
        })
        .and_then(|ability| ability.gameplay.melee_channel.as_ref())
        .map(melee_channel_runtime_from_definition)
}

fn movement_delivery_runtime_from_definition(
    definition: &AbilityDefinition,
    movement: &MovementDeliveryDefinition,
) -> MovementDeliveryRuntime {
    MovementDeliveryRuntime {
        ability_id: normalize_identifier(definition.ability_id.as_str()),
        action_id: AuthoredActionId::new(definition.action_id.as_str()).into_string(),
        kind: normalize_identifier(movement.kind.as_str()),
        cooldown_ms: movement.cooldown_ms,
        uses_global_cooldown: movement.uses_global_cooldown,
        global_cooldown_ms: resolved_global_cooldown_ms(
            movement.uses_global_cooldown,
            movement.global_cooldown_ms,
        ),
        cast_time_ms: movement.cast_time_ms,
        cast_mobility: normalize_identifier(movement.cast_mobility.as_str()),
        targeting: normalize_identifier(movement.targeting.as_str()),
        target_audience: normalize_optional_target_audience(movement.target_audience.as_str()),
        requires_target: movement.requires_target,
        // Authored at gameplay level (LOS is a targeting rule, not a delivery
        // tunable), same spot as melee and spell abilities.
        requires_target_los: definition.gameplay.requires_target_los.unwrap_or(true),
        resource_cost: movement.resource_cost.max(0.0),
        arms_auto_attack_on_cast: movement.arms_auto_attack_on_cast,
        speed: movement.speed,
        max_distance: movement.max_distance,
        damage: movement.damage,
        damage_type: normalize_damage_type(movement.damage_type.as_str()),
        radius: movement.radius,
        block_behavior: normalize_identifier(movement.block_behavior.as_str()),
        parry_behavior: normalize_identifier(movement.parry_behavior.as_str()),
        direction: normalize_identifier(movement.direction.as_str()),
        collision_policy: normalize_identifier(movement.collision_policy.as_str()),
        facing_policy: normalize_identifier(movement.facing_policy.as_str()),
        arrival_buffer: movement.arrival.buffer,
        arrival_epsilon: movement.arrival.epsilon,
        impact_effects: movement
            .impact_effects
            .iter()
            .map(|effect| match effect {
                MovementDeliveryImpactEffectDefinition::ApplyStatus { status } => {
                    MovementDeliveryImpactEffectRuntime {
                        status: movement_status_application_from_definition(
                            status,
                            authored_status_stack_group_default(status.kind.as_str()),
                        ),
                    }
                }
            })
            .collect(),
    }
}

fn movement_delivery_definition(
    ability_id: &str,
    gameplay: &AbilityGameplayDefinition,
) -> Option<MovementDeliveryDefinition> {
    let delivery = gameplay.delivery.as_ref()?;
    Some(
        serde_json::from_value(delivery.clone()).unwrap_or_else(|err| {
            panic!("movement ability '{ability_id}' has invalid gameplay.delivery: {err}")
        }),
    )
}

pub(crate) fn movement_delivery_for_ability_id(
    ability_id: &str,
) -> Option<MovementDeliveryRuntime> {
    let definition = ability_definition(ability_id)?;
    if ability_gameplay_kind(definition) != "MOVEMENT" {
        return None;
    }
    let movement = movement_delivery_definition(ability_id, &definition.gameplay)?;
    Some(movement_delivery_runtime_from_definition(
        definition, &movement,
    ))
}

pub(crate) fn movement_delivery_for_action_id(action_id: &str) -> Option<MovementDeliveryRuntime> {
    let normalized_action_id = AuthoredActionId::new(action_id).into_string();
    progression_catalog()
        .abilities
        .iter()
        .filter(|definition| ability_gameplay_kind(definition) == "MOVEMENT")
        .find(|definition| {
            AuthoredActionId::new(definition.action_id.as_str()).as_str() == normalized_action_id
        })
        .and_then(|definition| {
            let movement =
                movement_delivery_definition(definition.ability_id.as_str(), &definition.gameplay)?;
            Some(movement_delivery_runtime_from_definition(
                definition, &movement,
            ))
        })
}

pub(crate) fn action_id_is_movement_ability(action_id: &str) -> bool {
    let normalized_action_id = AuthoredActionId::new(action_id).into_string();
    progression_catalog().abilities.iter().any(|definition| {
        if AuthoredActionId::new(definition.action_id.as_str()).as_str() != normalized_action_id {
            return false;
        }

        let gameplay_kind = ability_gameplay_kind(definition);
        match gameplay_kind.as_str() {
            "MOVEMENT" => true,
            "MELEE" => {
                definition.gameplay.gap_close.is_some()
                    || definition.gameplay.melee_timed_movement.is_some()
                    || definition.gameplay.melee_evasive_leap.is_some()
            }
            "SPELL" => definition
                .gameplay
                .delivery
                .as_ref()
                .and_then(|delivery| delivery.get("kind"))
                .and_then(serde_json::Value::as_str)
                .is_some_and(|kind| normalize_identifier(kind) == "SELF_TELEPORT"),
            _ => false,
        }
    })
}

pub(crate) fn projectile_body_vfx_id_for_spell(
    ability_id: &str,
    spell_kind: &str,
    projectile_sequence_index: u32,
) -> Option<String> {
    combat_vfx_presentation_manifest()
        .projectile_body_vfx_id_for_spell(ability_id, spell_kind, projectile_sequence_index)
        .map(str::to_string)
}

pub(crate) fn projectile_trail_vfx_id_for_spell(
    ability_id: &str,
    spell_kind: &str,
    projectile_sequence_index: u32,
) -> Option<String> {
    combat_vfx_presentation_manifest()
        .projectile_trail_vfx_id_for_spell(ability_id, spell_kind, projectile_sequence_index)
        .map(str::to_string)
}

pub(crate) fn effective_resource_kind_for_ability(
    ctx: &ReducerContext,
    owner: Identity,
    ability: &AbilityCatalog,
) -> Option<String> {
    let authored = normalize_identifier(ability.resource_kind.as_str());
    if !authored.is_empty() {
        return Some(authored);
    }

    primary_resource_kind_for_owner(ctx, owner)
}

pub(crate) fn stat_scaling_value(effect_kind: &str) -> f32 {
    let normalized_effect_kind = normalize_identifier(effect_kind);
    progression_catalog()
        .stat_scalings
        .iter()
        .find(|definition| {
            normalize_identifier(definition.effect_kind.as_str()) == normalized_effect_kind
        })
        .map(|definition| definition.scalar_value)
        .unwrap_or(0.0)
}

pub(crate) fn combat_rule_value(combat_rule_id: &str) -> f32 {
    let normalized_rule_id = normalize_identifier(combat_rule_id);
    progression_catalog()
        .combat_rules
        .iter()
        .find(|definition| {
            normalize_identifier(definition.combat_rule_id.as_str()) == normalized_rule_id
        })
        .map(|definition| definition.scalar_value)
        .unwrap_or(0.0)
}

pub(crate) fn default_global_cooldown_ms() -> u64 {
    let configured = combat_rule_value(RULE_DEFAULT_GLOBAL_COOLDOWN_MS);
    if configured.is_finite() && configured > 0.0 {
        (configured.round() as u64).clamp(1, MAX_DEFAULT_GLOBAL_COOLDOWN_MS)
    } else {
        FALLBACK_DEFAULT_GLOBAL_COOLDOWN_MS
    }
}

fn resolved_global_cooldown_ms(
    uses_global_cooldown: bool,
    authored_global_cooldown_ms: Option<u64>,
) -> u64 {
    if !uses_global_cooldown {
        return 0;
    }
    authored_global_cooldown_ms.unwrap_or_else(default_global_cooldown_ms)
}

fn sync_combat_mode_catalog(ctx: &ReducerContext) {
    validate_combat_mode_catalog();
    let expected: HashSet<_> = progression_catalog()
        .combat_modes
        .iter()
        .map(combat_mode_catalog_key)
        .collect();

    for definition in &progression_catalog().combat_modes {
        let key = combat_mode_catalog_key(definition);
        let row = CombatModeCatalog {
            key: key.clone(),
            combat_discipline_id: normalize_identifier(definition.combat_discipline_id.as_str()),
            mode_id: normalize_identifier(definition.mode_id.as_str()),
            display_name: definition.display_name.clone(),
            is_default: definition.is_default,
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .combat_mode_catalog()
            .key()
            .find(key.clone())
            .is_some()
        {
            ctx.db.combat_mode_catalog().key().update(row);
        } else {
            ctx.db.combat_mode_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .combat_mode_catalog()
        .iter()
        .map(|row| row.key)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.combat_mode_catalog().key().delete(key);
    }
}

fn sync_resource_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .resources
        .iter()
        .map(|definition| normalize_identifier(definition.resource_kind.as_str()))
        .collect();

    for definition in &progression_catalog().resources {
        let resource_kind = normalize_identifier(definition.resource_kind.as_str());
        let row = ResourceCatalog {
            resource_kind: resource_kind.clone(),
            display_name: definition.display_name.clone(),
            color_hex: definition.color_hex.trim().to_string(),
            base_max: definition.base_max.max(0.0),
            max_per_insight: definition.max_per_insight.max(0.0),
            base_regen_per_second: definition.base_regen_per_second.max(0.0),
            regen_per_insight: definition.regen_per_insight.max(0.0),
            gain_multiplier_per_insight: definition.gain_multiplier_per_insight.max(0.0),
            flat_decay_per_second: definition.flat_decay_per_second.max(0.0),
            out_of_combat_flat_decay_per_second: definition
                .out_of_combat_flat_decay_per_second
                .max(0.0),
            decay_per_current_point_per_second: definition
                .decay_per_current_point_per_second
                .max(0.0),
            gain_per_damage_taken: definition.gain_per_damage_taken.max(0.0),
            gain_per_damage_dealt: definition.gain_per_damage_dealt.max(0.0),
            gain_per_melee_hit: definition.gain_per_melee_hit.max(0.0),
            gain_per_spell_cast: definition.gain_per_spell_cast.max(0.0),
            starts_full: definition.starts_full,
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .resource_catalog()
            .resource_kind()
            .find(resource_kind.clone())
            .is_some()
        {
            ctx.db.resource_catalog().resource_kind().update(row);
        } else {
            ctx.db.resource_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .resource_catalog()
        .iter()
        .map(|row| row.resource_kind)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.resource_catalog().resource_kind().delete(key);
    }
}

fn sync_combat_rule_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .combat_rules
        .iter()
        .map(|definition| normalize_identifier(definition.combat_rule_id.as_str()))
        .collect();

    for definition in &progression_catalog().combat_rules {
        let combat_rule_id = normalize_identifier(definition.combat_rule_id.as_str());
        let row = CombatRuleCatalog {
            combat_rule_id: combat_rule_id.clone(),
            scalar_value: definition.scalar_value.max(0.0),
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .combat_rule_catalog()
            .combat_rule_id()
            .find(combat_rule_id.clone())
            .is_some()
        {
            ctx.db.combat_rule_catalog().combat_rule_id().update(row);
        } else {
            ctx.db.combat_rule_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .combat_rule_catalog()
        .iter()
        .map(|row| row.combat_rule_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.combat_rule_catalog().combat_rule_id().delete(key);
    }
}

fn sync_stat_scaling_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .stat_scalings
        .iter()
        .map(|definition| normalize_identifier(definition.stat_scaling_id.as_str()))
        .collect();

    for definition in &progression_catalog().stat_scalings {
        let stat_scaling_id = normalize_identifier(definition.stat_scaling_id.as_str());
        let row = StatScalingCatalog {
            stat_scaling_id: stat_scaling_id.clone(),
            stat_kind: normalize_identifier(definition.stat_kind.as_str()),
            effect_kind: normalize_identifier(definition.effect_kind.as_str()),
            scalar_value: definition.scalar_value.max(0.0),
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .stat_scaling_catalog()
            .stat_scaling_id()
            .find(stat_scaling_id.clone())
            .is_some()
        {
            ctx.db.stat_scaling_catalog().stat_scaling_id().update(row);
        } else {
            ctx.db.stat_scaling_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .stat_scaling_catalog()
        .iter()
        .map(|row| row.stat_scaling_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.stat_scaling_catalog().stat_scaling_id().delete(key);
    }
}

fn sync_ability_catalog(ctx: &ReducerContext) {
    validate_ability_catalog();

    let expected: HashSet<_> = progression_catalog()
        .abilities
        .iter()
        .map(|definition| normalize_identifier(definition.ability_id.as_str()))
        .collect();

    for definition in &progression_catalog().abilities {
        let ability_id = normalize_identifier(definition.ability_id.as_str());
        let row = AbilityCatalog {
            ability_id: ability_id.clone(),
            actor_scope: normalize_identifier(definition.actor_scope.as_str()),
            combat_discipline_id: definition
                .combat_discipline_id
                .as_deref()
                .map(normalize_identifier)
                .unwrap_or_default(),
            spell_school_id: definition
                .spell_school_id
                .as_deref()
                .map(normalize_identifier)
                .unwrap_or_default(),
            selection_kind: normalize_identifier(definition.selection_kind.as_str()),
            ability_kind: ability_gameplay_kind(definition),
            action_id: AuthoredActionId::new(definition.action_id.as_str()).into_string(),
            display_name: definition.display_name.clone(),
            resource_kind: normalize_identifier(definition.resource_kind.as_str()),
            resource_cost: definition.resource_cost.max(0.0),
            ability_tags: encode_tags(&definition.ability_tags),
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .ability_catalog()
            .ability_id()
            .find(ability_id.clone())
            .is_some()
        {
            ctx.db.ability_catalog().ability_id().update(row);
        } else {
            ctx.db.ability_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .ability_catalog()
        .iter()
        .map(|row| row.ability_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.ability_catalog().ability_id().delete(key);
    }
}

fn sync_melee_ability_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .abilities
        .iter()
        .filter(|definition| ability_gameplay_kind(definition) == "MELEE")
        .map(|definition| normalize_identifier(definition.ability_id.as_str()))
        .collect();

    for definition in progression_catalog()
        .abilities
        .iter()
        .filter(|definition| ability_gameplay_kind(definition) == "MELEE")
    {
        let ability_id = normalize_identifier(definition.ability_id.as_str());
        let impact_area = definition.gameplay.melee_impact_area.as_ref();
        let targeting = resolved_melee_targeting_for_catalog(&definition.gameplay);
        let uses_global_cooldown = required_melee_field(
            definition.gameplay.uses_global_cooldown,
            &ability_id,
            "uses_global_cooldown",
        );
        let row = MeleeAbilityCatalog {
            ability_id: ability_id.clone(),
            action_id: AuthoredActionId::new(definition.action_id.as_str()).into_string(),
            base_damage: required_melee_field(
                definition.gameplay.base_damage,
                &ability_id,
                "base_damage",
            ),
            damage_type: normalize_damage_type(
                definition
                    .gameplay
                    .damage_type
                    .as_deref()
                    .unwrap_or("PHYSICAL"),
            ),
            target_health_damage_scaling_min_multiplier: definition
                .gameplay
                .target_health_damage_scaling
                .as_ref()
                .map(|scaling| scaling.min_multiplier)
                .unwrap_or(1.0),
            target_health_damage_scaling_max_multiplier: definition
                .gameplay
                .target_health_damage_scaling
                .as_ref()
                .map(|scaling| scaling.max_multiplier)
                .unwrap_or(1.0),
            applies_stagger: required_melee_field(
                definition.gameplay.applies_stagger,
                &ability_id,
                "applies_stagger",
            ),
            range: required_melee_field(definition.gameplay.range, &ability_id, "range"),
            minimum_range: definition.gameplay.minimum_range.unwrap_or(0.0).max(0.0),
            cooldown_ms: required_melee_field(
                definition.gameplay.cooldown_ms,
                &ability_id,
                "cooldown_ms",
            ),
            uses_global_cooldown,
            global_cooldown_ms: resolved_global_cooldown_ms(
                uses_global_cooldown,
                definition.gameplay.global_cooldown_ms,
            ),
            parry_behavior: normalize_identifier(required_melee_string_field(
                definition.gameplay.parry_behavior.as_deref(),
                &ability_id,
                "parry_behavior",
            )),
            block_behavior: normalize_identifier(required_melee_string_field(
                definition.gameplay.block_behavior.as_deref(),
                &ability_id,
                "block_behavior",
            )),
            airborne_targeting_mode: normalize_identifier(required_melee_string_field(
                definition.gameplay.airborne_targeting_mode.as_deref(),
                &ability_id,
                "airborne_targeting_mode",
            )),
            targeting_kind: targeting.kind,
            target_audience: normalize_optional_target_audience(
                definition.gameplay.target_audience.as_str(),
            ),
            requires_target: targeting.requires_target,
            requires_target_los: definition.gameplay.requires_target_los.unwrap_or(true),
            targeting_radius: targeting.radius,
            targeting_range: targeting.range,
            targeting_angle_degrees: targeting.angle_degrees,
            impact_area_radius: impact_area.map(|area| area.radius).unwrap_or(0.0),
            impact_area_damage_multiplier: impact_area
                .map(|area| area.damage_multiplier)
                .unwrap_or(0.0),
            impact_area_hit_index: impact_area
                .and_then(|area| area.hit_index)
                .map(|hit_index| hit_index.min(i32::MAX as u32) as i32)
                .unwrap_or(-1),
            impact_area_include_primary_target: impact_area
                .map(|area| area.include_primary_target)
                .unwrap_or(false),
            targeting_width: targeting.width,
            channel_duration_ms: definition
                .gameplay
                .melee_channel
                .as_ref()
                .map(|channel| channel.duration_ms)
                .unwrap_or(0),
            channel_first_tick_delay_ms: definition
                .gameplay
                .melee_channel
                .as_ref()
                .map(|channel| channel.first_tick_delay_ms)
                .unwrap_or(0),
            channel_tick_interval_ms: definition
                .gameplay
                .melee_channel
                .as_ref()
                .map(|channel| channel.tick_interval_ms)
                .unwrap_or(0),
            channel_cancel_on_movement: definition
                .gameplay
                .melee_channel
                .as_ref()
                .is_some_and(|channel| channel.cancel_on_movement),
            channel_use_authored_hit_windows: definition
                .gameplay
                .melee_channel
                .as_ref()
                .is_some_and(|channel| channel.use_authored_hit_windows),
        };
        if ctx
            .db
            .melee_ability_catalog()
            .ability_id()
            .find(ability_id.clone())
            .is_some()
        {
            ctx.db.melee_ability_catalog().ability_id().update(row);
        } else {
            ctx.db.melee_ability_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .melee_ability_catalog()
        .iter()
        .map(|row| row.ability_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.melee_ability_catalog().ability_id().delete(key);
    }
}

fn sync_melee_gap_close_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .abilities
        .iter()
        .filter(|definition| ability_gameplay_kind(definition) == "MELEE")
        .filter(|definition| definition.gameplay.gap_close.is_some())
        .map(|definition| normalize_identifier(definition.ability_id.as_str()))
        .collect();

    for definition in progression_catalog()
        .abilities
        .iter()
        .filter(|definition| ability_gameplay_kind(definition) == "MELEE")
    {
        let ability_id = normalize_identifier(definition.ability_id.as_str());
        let Some(gap_close) = definition.gameplay.gap_close.as_ref() else {
            if ctx
                .db
                .melee_gap_close_catalog()
                .ability_id()
                .find(ability_id.clone())
                .is_some()
            {
                ctx.db
                    .melee_gap_close_catalog()
                    .ability_id()
                    .delete(ability_id);
            }
            continue;
        };

        let row = MeleeGapCloseCatalog {
            ability_id: ability_id.clone(),
            kind: normalize_identifier(gap_close.kind.as_str()),
            destination: normalize_identifier(gap_close.destination.as_str()),
            speed: gap_close.speed.unwrap_or(0.0).max(0.0),
            arrival_buffer: gap_close.arrival_buffer.max(0.0),
            arrival_epsilon: gap_close.arrival_epsilon.max(0.0),
            impact_range: gap_close.impact_range.max(0.0),
            collision_policy: normalize_identifier(gap_close.collision_policy.as_str()),
            require_arrival_for_swing: gap_close.require_arrival_for_swing,
            requires_target_facing: gap_close.requires_target_facing,
            activate_outside_impact_reach: gap_close.activate_outside_impact_reach,
        };
        if ctx
            .db
            .melee_gap_close_catalog()
            .ability_id()
            .find(ability_id.clone())
            .is_some()
        {
            ctx.db.melee_gap_close_catalog().ability_id().update(row);
        } else {
            ctx.db.melee_gap_close_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .melee_gap_close_catalog()
        .iter()
        .map(|row| row.ability_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.melee_gap_close_catalog().ability_id().delete(key);
    }
}

fn required_melee_field<T: Copy>(value: Option<T>, ability_id: &str, field: &str) -> T {
    value.unwrap_or_else(|| panic!("MELEE ability {ability_id} must declare {field}"))
}

fn required_melee_string_field<'a>(
    value: Option<&'a str>,
    ability_id: &str,
    field: &str,
) -> &'a str {
    value.unwrap_or_else(|| panic!("MELEE ability {ability_id} must declare {field}"))
}

struct ResolvedMeleeTargetingCatalogFields {
    kind: String,
    requires_target: bool,
    radius: f32,
    range: f32,
    angle_degrees: f32,
    width: f32,
}

fn resolved_melee_targeting_for_catalog(
    gameplay: &AbilityGameplayDefinition,
) -> ResolvedMeleeTargetingCatalogFields {
    let targeting = gameplay.melee_targeting.as_ref();
    let kind = normalize_identifier(
        targeting
            .map(|targeting| targeting.kind.as_str())
            .unwrap_or("TARGET"),
    );

    ResolvedMeleeTargetingCatalogFields {
        requires_target: targeting
            .and_then(|targeting| targeting.requires_target)
            .unwrap_or(kind == "TARGET"),
        radius: if kind == "CASTER_RADIUS" {
            gameplay.range.unwrap_or(0.0)
        } else {
            0.0
        },
        range: gameplay.range.unwrap_or(0.0),
        angle_degrees: targeting
            .and_then(|targeting| targeting.angle_degrees)
            .unwrap_or(0.0),
        width: targeting
            .and_then(|targeting| targeting.width)
            .unwrap_or(0.0),
        kind,
    }
}

fn sync_auto_attack_catalog(ctx: &ReducerContext) {
    validate_auto_attack_catalog();
    let expected: HashSet<_> = progression_catalog()
        .auto_attacks
        .iter()
        .map(auto_attack_catalog_key)
        .collect();

    for definition in &progression_catalog().auto_attacks {
        let key = auto_attack_catalog_key(definition);
        let row = AutoAttackCatalog {
            key: key.clone(),
            combat_discipline_id: normalize_identifier(definition.combat_discipline_id.as_str()),
            mode_id: normalize_identifier(definition.mode_id.as_str()),
            action_id: AuthoredActionId::new(definition.action_id.as_str()).into_string(),
            base_damage: definition.base_damage,
            damage_type: normalize_damage_type(definition.damage_type.as_str()),
            range: definition.range,
            cooldown_ms: definition.cooldown_ms,
            movement_policy: normalize_identifier(definition.movement_policy.as_str()),
            uses_global_cooldown: definition.uses_global_cooldown,
            global_cooldown_ms: resolved_global_cooldown_ms(
                definition.uses_global_cooldown,
                definition.global_cooldown_ms,
            ),
            parry_behavior: normalize_identifier(definition.parry_behavior.as_str()),
            block_behavior: normalize_identifier(definition.block_behavior.as_str()),
            airborne_targeting_mode: normalize_identifier(
                definition.airborne_targeting_mode.as_str(),
            ),
            applies_stagger: definition.applies_stagger,
            requires_target_los: definition.requires_target_los.unwrap_or(true),
        };
        if ctx
            .db
            .auto_attack_catalog()
            .key()
            .find(key.clone())
            .is_some()
        {
            ctx.db.auto_attack_catalog().key().update(row);
        } else {
            ctx.db.auto_attack_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .auto_attack_catalog()
        .iter()
        .map(|row| row.key)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.auto_attack_catalog().key().delete(key);
    }
}

fn sync_auto_attack_replacement_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .auto_attack_replacements
        .iter()
        .map(|definition| normalize_identifier(definition.replacement_id.as_str()))
        .collect();

    for definition in &progression_catalog().auto_attack_replacements {
        let replacement_id = normalize_identifier(definition.replacement_id.as_str());
        let row = AutoAttackReplacementCatalog {
            replacement_id: replacement_id.clone(),
            combat_discipline_id: normalize_identifier(definition.combat_discipline_id.as_str()),
            authored_melee_strike_id: AuthoredActionId::new(
                definition.authored_melee_strike_id.as_str(),
            )
            .into_string(),
            base_damage: definition.base_damage,
            damage_type: normalize_damage_type(definition.damage_type.as_str()),
            range: definition.range,
            cooldown_ms: definition.cooldown_ms,
            uses_global_cooldown: definition.uses_global_cooldown,
            global_cooldown_ms: resolved_global_cooldown_ms(
                definition.uses_global_cooldown,
                definition.global_cooldown_ms,
            ),
            parry_behavior: normalize_identifier(definition.parry_behavior.as_str()),
            block_behavior: normalize_identifier(definition.block_behavior.as_str()),
            airborne_targeting_mode: normalize_identifier(
                definition.airborne_targeting_mode.as_str(),
            ),
            applies_stagger: definition.applies_stagger,
            requires_target_los: definition.requires_target_los.unwrap_or(true),
            grants_primary_resource_on_hit: definition.grants_primary_resource_on_hit,
            expires_ms: definition.expires_ms,
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .auto_attack_replacement_catalog()
            .replacement_id()
            .find(replacement_id.clone())
            .is_some()
        {
            ctx.db
                .auto_attack_replacement_catalog()
                .replacement_id()
                .update(row);
        } else {
            ctx.db.auto_attack_replacement_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .auto_attack_replacement_catalog()
        .iter()
        .map(|row| row.replacement_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db
            .auto_attack_replacement_catalog()
            .replacement_id()
            .delete(key);
    }
}

fn sync_action_presentation_catalog(ctx: &ReducerContext) {
    let explicit_rows: Vec<_> = progression_catalog()
        .action_presentations
        .iter()
        .map(action_presentation_row_from_definition)
        .collect();
    let derived_spell_rows = derived_spell_action_presentation_rows(progression_catalog());
    let expected: HashSet<_> = explicit_rows
        .iter()
        .chain(derived_spell_rows.iter())
        .map(|row| row.key.clone())
        .collect();

    for row in explicit_rows
        .into_iter()
        .chain(derived_spell_rows.into_iter())
    {
        let key = row.key.clone();
        if ctx
            .db
            .action_presentation_catalog()
            .key()
            .find(key.clone())
            .is_some()
        {
            ctx.db.action_presentation_catalog().key().update(row);
        } else {
            ctx.db.action_presentation_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .action_presentation_catalog()
        .iter()
        .map(|row| row.key)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.action_presentation_catalog().key().delete(key);
    }
}

fn sync_combat_vfx_cue_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .combat_vfx_cues
        .iter()
        .map(combat_vfx_cue_key)
        .collect();

    for definition in &progression_catalog().combat_vfx_cues {
        let key = combat_vfx_cue_key(definition);
        let attach_mode = normalize_identifier(definition.attach_mode.as_str());
        let vfx_role = normalize_identifier(definition.vfx_role.as_str());
        let lifecycle = normalize_identifier(definition.lifecycle.as_str());
        let row = CombatVfxCueCatalog {
            key: key.clone(),
            owner_kind: normalize_identifier(definition.owner_kind.as_str()),
            owner_id: normalize_identifier(definition.owner_id.as_str()),
            trigger: normalize_identifier(definition.trigger.as_str()),
            hit_index: definition
                .hit_index
                .map(|hit_index| hit_index.min(i32::MAX as u32) as i32)
                .unwrap_or(-1),
            anchor: normalize_identifier(definition.anchor.as_str()),
            vfx_id: normalize_identifier(definition.vfx_id.as_str()),
            attach_mode: if attach_mode.is_empty() {
                "SPAWN_WORLD".to_string()
            } else {
                attach_mode
            },
            vfx_role: if vfx_role.is_empty() {
                "ONE_SHOT".to_string()
            } else {
                vfx_role
            },
            lifecycle: if lifecycle.is_empty() {
                "DURATION".to_string()
            } else {
                lifecycle
            },
            projectile_sequence_index: definition
                .projectile_sequence_index
                .map(|index| index.min(i32::MAX as u32) as i32)
                .unwrap_or(-1),
            start_delay_ms: definition.start_delay_ms,
            scale: definition.scale.unwrap_or(1.0).max(0.0),
            duration_ms: definition.duration_ms,
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .combat_vfx_cue_catalog()
            .key()
            .find(key.clone())
            .is_some()
        {
            ctx.db.combat_vfx_cue_catalog().key().update(row);
        } else {
            ctx.db.combat_vfx_cue_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .combat_vfx_cue_catalog()
        .iter()
        .map(|row| row.key)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.combat_vfx_cue_catalog().key().delete(key);
    }
}

fn sync_action_bar_slot_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .slots
        .iter()
        .map(|definition| canonical_action_bar_slot_id(definition.slot_id.as_str()))
        .collect();

    for definition in &progression_catalog().slots {
        let slot_id = canonical_action_bar_slot_id(definition.slot_id.as_str());
        let row = ActionBarSlotCatalog {
            slot_id: slot_id.clone(),
            ui_row: definition.ui_row,
            ui_col: definition.ui_col,
            slot_group: normalize_identifier(definition.slot_group.as_str()),
            accepts_tags: encode_tags(&definition.accepts_tags),
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .action_bar_slot_catalog()
            .slot_id()
            .find(slot_id.clone())
            .is_some()
        {
            ctx.db.action_bar_slot_catalog().slot_id().update(row);
        } else {
            ctx.db.action_bar_slot_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .action_bar_slot_catalog()
        .iter()
        .map(|row| row.slot_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.action_bar_slot_catalog().slot_id().delete(key);
    }
}

fn ability_definition(ability_id: &str) -> Option<&'static AbilityDefinition> {
    let ability_id = normalize_identifier(ability_id);
    progression_catalog()
        .abilities
        .iter()
        .find(|definition| normalize_identifier(definition.ability_id.as_str()) == ability_id)
}

pub(crate) fn consume_target_status_rule_for_ability_id(
    ability_id: &str,
) -> Option<ConsumeTargetStatusRule> {
    ability_definition(ability_id)?
        .gameplay
        .consume_target_status
        .clone()
}

pub(crate) fn authored_ability_actor_scope(ability_id: &str) -> Option<&'static str> {
    ability_definition(ability_id).map(|definition| definition.actor_scope.as_str())
}

pub(crate) fn authored_ability_resource(ability_id: &str) -> Option<(&'static str, f32)> {
    ability_definition(ability_id)
        .map(|definition| (definition.resource_kind.as_str(), definition.resource_cost))
}

pub(crate) fn authored_npc_spell_ability_id(action_id: &str) -> Option<&'static str> {
    let action_id = normalize_identifier(action_id);
    progression_catalog()
        .abilities
        .iter()
        .filter(|ability| normalize_identifier(ability.actor_scope.as_str()) == "NPC")
        .filter(|ability| ability_gameplay_kind(ability) == "SPELL")
        .filter(|ability| normalize_identifier(ability.action_id.as_str()) == action_id)
        .min_by_key(|ability| ability.sort_order)
        .map(|ability| ability.ability_id.as_str())
}

fn auto_attack_catalog_key(definition: &AutoAttackDefinition) -> String {
    auto_attack_catalog_key_for(
        definition.combat_discipline_id.as_str(),
        definition.mode_id.as_str(),
        AuthoredActionId::new(definition.action_id.as_str()).as_str(),
    )
}

fn auto_attack_catalog_key_for(
    combat_discipline_id: &str,
    mode_id: &str,
    action_id: &str,
) -> String {
    let combat_discipline_id = normalize_identifier(combat_discipline_id);
    let mode_id = normalize_identifier(mode_id);
    let action_id = AuthoredActionId::new(action_id).into_string();
    if mode_id.is_empty() {
        format!("{combat_discipline_id}:{action_id}")
    } else {
        format!("{combat_discipline_id}:{mode_id}:{action_id}")
    }
}

fn combat_mode_catalog_key(definition: &CombatModeDefinition) -> String {
    combat_mode_key(
        definition.combat_discipline_id.as_str(),
        definition.mode_id.as_str(),
    )
}

fn combat_mode_key(combat_discipline_id: &str, mode_id: &str) -> String {
    format!(
        "{}:{}",
        normalize_identifier(combat_discipline_id),
        normalize_identifier(mode_id)
    )
}

fn validate_combat_mode_catalog() {
    let mut keys = HashSet::new();
    for mode in &progression_catalog().combat_modes {
        let profile_id = normalize_identifier(mode.combat_discipline_id.as_str());
        let mode_id = normalize_identifier(mode.mode_id.as_str());
        assert!(
            !profile_id.is_empty(),
            "combat mode profile must not be empty"
        );
        assert!(!mode_id.is_empty(), "combat mode id must not be empty");
        assert!(combat_profile_exists_for_authoring(profile_id.as_str()));
        assert!(
            keys.insert(combat_mode_key(profile_id.as_str(), mode_id.as_str())),
            "duplicate combat mode '{}' for profile '{}'",
            mode_id,
            profile_id
        );
    }

    assert_eq!(
        progression_catalog()
            .combat_modes
            .iter()
            .find(
                |mode| normalize_identifier(mode.combat_discipline_id.as_str())
                    == COMBAT_PROFILE_ARCHER_BOW
                    && normalize_identifier(mode.mode_id.as_str()) == COMBAT_MODE_FULL_DRAW
            )
            .map(|mode| mode.is_default),
        Some(true),
        "ARCHER_BOW must default to FULL_DRAW"
    );

    assert_eq!(
        progression_catalog()
            .combat_modes
            .iter()
            .find(
                |mode| normalize_identifier(mode.combat_discipline_id.as_str())
                    == COMBAT_PROFILE_DAGGERS
                    && normalize_identifier(mode.mode_id.as_str()) == COMBAT_MODE_READY
            )
            .map(|mode| mode.is_default),
        Some(true),
        "DAGGERS must default to READY"
    );
}

fn validate_auto_attack_catalog() {
    let known_modes: HashSet<_> = progression_catalog()
        .combat_modes
        .iter()
        .map(combat_mode_catalog_key)
        .collect();
    let mut keys = HashSet::new();
    let mut archer_modes = HashSet::new();
    for attack in &progression_catalog().auto_attacks {
        let key = auto_attack_catalog_key(attack);
        assert!(
            keys.insert(key.clone()),
            "duplicate auto attack row '{key}'"
        );

        let mode_id = normalize_identifier(attack.mode_id.as_str());
        let combat_discipline_id = normalize_identifier(attack.combat_discipline_id.as_str());
        assert!(
            combat_profile_exists_for_authoring(combat_discipline_id.as_str()),
            "auto attack '{}' references unknown combat discipline '{}'",
            attack.action_id,
            attack.combat_discipline_id
        );
        if !mode_id.is_empty() {
            let mode_key = combat_mode_key(combat_discipline_id.as_str(), mode_id.as_str());
            assert!(
                known_modes.contains(mode_key.as_str()),
                "auto attack '{}' mode '{}' references unknown combat mode",
                attack.action_id,
                attack.mode_id
            );
        }

        let movement_policy = normalize_identifier(attack.movement_policy.as_str());
        assert!(
            movement_policy == AUTO_ATTACK_MOVEMENT_ALLOW_MOVING
                || movement_policy == AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE,
            "auto attack '{}' has unsupported movement_policy '{}'",
            attack.action_id,
            attack.movement_policy
        );
        validate_authored_global_cooldown_ms(
            attack.action_id.as_str(),
            Some(attack.uses_global_cooldown),
            attack.global_cooldown_ms,
        );

        if combat_discipline_id == COMBAT_PROFILE_ARCHER_BOW
            && AuthoredActionId::new(attack.action_id.as_str()).as_str() == "AUTO_ATTACK_1"
        {
            archer_modes.insert(mode_id);
        }
    }

    assert!(
        archer_modes.contains(COMBAT_MODE_SHORT_DRAW)
            && archer_modes.contains(COMBAT_MODE_FULL_DRAW),
        "ARCHER_BOW AUTO_ATTACK_1 must define SHORT_DRAW and FULL_DRAW rows"
    );

    for replacement in &progression_catalog().auto_attack_replacements {
        validate_authored_global_cooldown_ms(
            replacement.replacement_id.as_str(),
            Some(replacement.uses_global_cooldown),
            replacement.global_cooldown_ms,
        );
    }
}

fn validate_ability_catalog() {
    for ability in &progression_catalog().abilities {
        let ability_id = normalize_identifier(ability.ability_id.as_str());
        let actor_scope =
            validated_ability_actor_scope(ability_id.as_str(), ability.actor_scope.as_str());
        let combat_discipline_id = ability
            .combat_discipline_id
            .as_deref()
            .map(normalize_identifier)
            .unwrap_or_default();
        let spell_school_id = ability
            .spell_school_id
            .as_deref()
            .map(normalize_identifier)
            .unwrap_or_default();

        if actor_scope != "NPC" {
            assert!(
                combat_profile_exists_for_authoring(combat_discipline_id.as_str()),
                "player ability '{ability_id}' references unknown combat discipline '{combat_discipline_id}'"
            );
            assert!(
                matches!(
                    normalize_identifier(ability.selection_kind.as_str()).as_str(),
                    "ACTIVE" | "PASSIVE" | "INTRINSIC"
                ),
                "player ability '{ability_id}' must declare a canonical selection kind"
            );
            // A Form may own a Spell. Its school describes spell mechanics and
            // presentation independently of its owning weapon discipline.
            if combat_discipline_id == COMBAT_PROFILE_STAFF
                || (!spell_school_id.is_empty() && ability_gameplay_kind(ability) == "SPELL")
            {
                assert!(
                    matches!(
                        spell_school_id.as_str(),
                        "BLIGHT" | "MORTALITY" | "RUIN" | "DIVINITY" | "ARCANA" | "PRIMAL"
                    ),
                    "ability '{ability_id}' must declare one canonical spell school"
                );
            } else {
                assert!(
                    spell_school_id.is_empty(),
                    "non-Staff non-spell ability '{ability_id}' must not declare a spell school"
                );
            }
        }

        let ability_kind = ability_gameplay_kind(ability);
        if let Some(rule) = ability.gameplay.consume_target_status.as_ref() {
            rule.validate(ability_id.as_str(), ability_kind.as_str())
                .unwrap_or_else(|err| panic!("{err}"));
        }
        validate_blade_twisting_tuning(
            ability_id.as_str(),
            ability_kind.as_str(),
            combat_discipline_id.as_str(),
            &ability.gameplay,
        );
        let disabled_target_damage_bonus = ability.gameplay.disabled_target_damage_bonus;
        let behind_target_damage_bonus = ability.gameplay.behind_target_damage_bonus;
        let isolated_damage_bonus = ability.gameplay.isolated_damage_bonus;
        let isolated_ally_radius_meters = ability.gameplay.isolated_ally_radius_meters;
        let point_blank_damage_bonus = ability.gameplay.point_blank_damage_bonus;
        let point_blank_full_bonus_range_meters =
            ability.gameplay.point_blank_full_bonus_range_meters;
        let point_blank_zero_bonus_range_meters =
            ability.gameplay.point_blank_zero_bonus_range_meters;
        let stationary_target_damage_bonus = ability.gameplay.stationary_target_damage_bonus;
        let stationary_target_window_ms = ability.gameplay.stationary_target_window_ms;
        let stationary_target_max_displacement_meters =
            ability.gameplay.stationary_target_max_displacement_meters;
        let stationary_target_auto_crit = ability.gameplay.stationary_target_auto_crit;
        let projectile_piercing = ability.gameplay.projectile_piercing;
        let dodge_recharge_time_reduction = ability.gameplay.dodge_recharge_time_reduction;
        let movement_return = ability.gameplay.movement_return.as_ref();
        let stealth_attack_stun_ms = ability.gameplay.stealth_attack_stun_ms;
        let melee_fire_on_hit = ability.gameplay.melee_fire_on_hit.as_ref();
        let melee_poison_on_hit = ability.gameplay.melee_poison_on_hit.as_ref();
        let fire_spell_ignite = ability.gameplay.fire_spell_ignite.as_ref();
        let fire_damage_taken_mana_restore_ratio =
            ability.gameplay.fire_damage_taken_mana_restore_ratio;
        let critical_strike_cooldown_reduction_ms =
            ability.gameplay.critical_strike_cooldown_reduction_ms;
        let movement_spell_cast_time_reduction =
            ability.gameplay.movement_spell_cast_time_reduction;
        let movement_spell_cast_time_buff_duration_ms =
            ability.gameplay.movement_spell_cast_time_buff_duration_ms;
        let critical_spell_proc_action_id =
            normalize_identifier(ability.gameplay.critical_spell_proc_action_id.as_str());
        let auto_attack_proc_action_id =
            normalize_identifier(ability.gameplay.auto_attack_proc_action_id.as_str());
        let auto_attack_proc_chance = ability.gameplay.auto_attack_proc_chance;
        let frozen_melee_first_hit_damage_bonus =
            ability.gameplay.frozen_melee_first_hit_damage_bonus;
        let noncritical_lightning_spell_crit_chance_bonus = ability
            .gameplay
            .noncritical_lightning_spell_crit_chance_bonus;
        let mana_regen_bonus = ability.gameplay.mana_regen_bonus;
        let adaptation_resistance_per_stack = ability.gameplay.adaptation_resistance_per_stack;
        let adaptation_duration_ms = ability.gameplay.adaptation_duration_ms;
        let adaptation_max_stacks = ability.gameplay.adaptation_max_stacks;
        let stationary_mana_regen_per_stack = ability.gameplay.stationary_mana_regen_per_stack;
        let stationary_first_stack_delay_ms = ability.gameplay.stationary_first_stack_delay_ms;
        let stationary_stack_interval_ms = ability.gameplay.stationary_stack_interval_ms;
        let stationary_max_stacks = ability.gameplay.stationary_max_stacks;
        let other_movement_cooldown_reduction_ms =
            ability.gameplay.other_movement_cooldown_reduction_ms;
        assert!(
            disabled_target_damage_bonus.is_finite()
                && (0.0..=1.0).contains(&disabled_target_damage_bonus),
            "ability '{ability_id}' must author disabled_target_damage_bonus between 0 and 1"
        );
        assert!(
            isolated_damage_bonus.is_finite() && (0.0..=1.0).contains(&isolated_damage_bonus),
            "ability '{ability_id}' must author isolated_damage_bonus between 0 and 1"
        );
        assert!(
            isolated_ally_radius_meters.is_finite() && isolated_ally_radius_meters >= 0.0,
            "ability '{ability_id}' must author a finite non-negative isolated_ally_radius_meters"
        );
        if isolated_damage_bonus > 0.0 || isolated_ally_radius_meters > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author isolated damage tuning for PASSIVE gameplay"
            );
            assert!(
                isolated_damage_bonus > 0.0 && isolated_ally_radius_meters > 0.0,
                "ability '{ability_id}' must author complete isolated damage tuning"
            );
        }
        assert!(
            point_blank_damage_bonus.is_finite() && (0.0..=1.0).contains(&point_blank_damage_bonus),
            "ability '{ability_id}' must author point_blank_damage_bonus between 0 and 1"
        );
        assert!(
            point_blank_full_bonus_range_meters.is_finite()
                && point_blank_full_bonus_range_meters >= 0.0
                && point_blank_zero_bonus_range_meters.is_finite()
                && point_blank_zero_bonus_range_meters >= 0.0,
            "ability '{ability_id}' must author finite non-negative Point Blank ranges"
        );
        if point_blank_damage_bonus > 0.0
            || point_blank_full_bonus_range_meters > 0.0
            || point_blank_zero_bonus_range_meters > 0.0
        {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author Point Blank tuning for PASSIVE gameplay"
            );
            assert!(
                point_blank_damage_bonus > 0.0
                    && point_blank_full_bonus_range_meters > 0.0
                    && point_blank_zero_bonus_range_meters > point_blank_full_bonus_range_meters,
                "ability '{ability_id}' must author complete ordered Point Blank tuning"
            );
        }
        assert!(
            stationary_target_damage_bonus.is_finite()
                && (0.0..=1.0).contains(&stationary_target_damage_bonus),
            "ability '{ability_id}' must author stationary_target_damage_bonus between 0 and 1"
        );
        assert!(
            stationary_target_max_displacement_meters.is_finite()
                && stationary_target_max_displacement_meters >= 0.0,
            "ability '{ability_id}' must author a finite non-negative stationary displacement threshold"
        );
        if stationary_target_damage_bonus > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author stationary target damage tuning for PASSIVE gameplay"
            );
        }
        if stationary_target_damage_bonus > 0.0
            || stationary_target_window_ms > 0
            || stationary_target_max_displacement_meters > 0.0
            || stationary_target_auto_crit
        {
            assert!(
                (stationary_target_damage_bonus > 0.0 || stationary_target_auto_crit)
                    && stationary_target_window_ms > 0
                    && stationary_target_max_displacement_meters > 0.0,
                "ability '{ability_id}' must author complete stationary target tuning"
            );
        }
        if projectile_piercing {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author projectile_piercing for PASSIVE gameplay"
            );
        }
        assert!(
            mana_regen_bonus.is_finite() && mana_regen_bonus >= 0.0,
            "ability '{ability_id}' must author a finite non-negative mana_regen_bonus"
        );
        if mana_regen_bonus > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author mana_regen_bonus for PASSIVE gameplay"
            );
        }
        if ability_id == DIVINITY_FAITH_ABILITY_ID {
            assert_eq!(spell_school_id, "DIVINITY", "Faith must remain Divinity");
            assert_eq!(ability_kind, "PASSIVE", "Faith must remain passive");
            assert!(ability
                .ability_tags
                .iter()
                .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
            assert!((mana_regen_bonus - 2.0).abs() < 0.0001);
        }
        assert!(
            adaptation_resistance_per_stack.is_finite()
                && (0.0..=1.0).contains(&adaptation_resistance_per_stack),
            "ability '{ability_id}' must author adaptation_resistance_per_stack between 0 and 1"
        );
        if adaptation_resistance_per_stack > 0.0
            || adaptation_duration_ms > 0
            || adaptation_max_stacks > 0
        {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author Adaptation tuning for PASSIVE gameplay"
            );
            assert!(
                adaptation_resistance_per_stack > 0.0
                    && adaptation_duration_ms > 0
                    && adaptation_max_stacks > 0,
                "ability '{ability_id}' must author complete Adaptation tuning"
            );
        }
        if ability_id == PRIMAL_ADAPTATION_ABILITY_ID {
            assert_eq!(spell_school_id, "PRIMAL");
            assert_eq!(ability_kind, "PASSIVE");
            assert!((adaptation_resistance_per_stack - 0.02).abs() < 0.0001);
            assert_eq!(adaptation_duration_ms, 10_000);
            assert_eq!(adaptation_max_stacks, 10);
        }
        assert!(
            stationary_mana_regen_per_stack.is_finite()
                && stationary_mana_regen_per_stack >= 0.0,
            "ability '{ability_id}' must author a finite non-negative stationary_mana_regen_per_stack"
        );
        if stationary_mana_regen_per_stack > 0.0
            || stationary_first_stack_delay_ms > 0
            || stationary_stack_interval_ms > 0
            || stationary_max_stacks > 0
        {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author Photosynthesis tuning for PASSIVE gameplay"
            );
            assert!(
                stationary_mana_regen_per_stack > 0.0
                    && stationary_first_stack_delay_ms > 0
                    && stationary_stack_interval_ms > 0
                    && stationary_max_stacks > 0,
                "ability '{ability_id}' must author complete Photosynthesis tuning"
            );
        }
        if ability_id == PRIMAL_PHOTOSYNTHESIS_ABILITY_ID {
            assert_eq!(spell_school_id, "PRIMAL");
            assert_eq!(ability_kind, "PASSIVE");
            assert!((stationary_mana_regen_per_stack - 1.0).abs() < 0.0001);
            assert_eq!(stationary_first_stack_delay_ms, 2_000);
            assert_eq!(stationary_stack_interval_ms, 2_000);
            assert_eq!(stationary_max_stacks, 5);
        }
        if other_movement_cooldown_reduction_ms > 0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author movement cooldown reduction for PASSIVE gameplay"
            );
            assert!(
                other_movement_cooldown_reduction_ms <= 60_000,
                "ability '{ability_id}' movement cooldown reduction must not exceed 60000"
            );
        }
        if ability_id == PRIMAL_SLIPSTREAM_ABILITY_ID {
            assert_eq!(spell_school_id, "PRIMAL");
            assert_eq!(ability_kind, "PASSIVE");
            assert_eq!(other_movement_cooldown_reduction_ms, 2_000);
        }
        if disabled_target_damage_bonus > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author disabled_target_damage_bonus for PASSIVE gameplay"
            );
        }
        assert!(
            behind_target_damage_bonus.is_finite()
                && (0.0..=1.0).contains(&behind_target_damage_bonus),
            "ability '{ability_id}' must author behind_target_damage_bonus between 0 and 1"
        );
        if behind_target_damage_bonus > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author behind_target_damage_bonus for PASSIVE gameplay"
            );
        }
        assert!(
            dodge_recharge_time_reduction.is_finite()
                && (0.0..1.0).contains(&dodge_recharge_time_reduction),
            "ability '{ability_id}' must author dodge_recharge_time_reduction between 0 inclusive and 1 exclusive"
        );
        if dodge_recharge_time_reduction > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author dodge_recharge_time_reduction for PASSIVE gameplay"
            );
        }
        if let Some(movement_return) = movement_return {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author movement_return for PASSIVE gameplay"
            );
            assert!(
                (1..=10_000).contains(&movement_return.window_ms),
                "ability '{ability_id}' must author movement_return.window_ms between 1 and 10000"
            );
        }
        if stealth_attack_stun_ms > 0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author stealth_attack_stun_ms for PASSIVE gameplay"
            );
        }
        if let Some(melee_fire_on_hit) = melee_fire_on_hit {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author melee_fire_on_hit for PASSIVE gameplay"
            );
            assert!(
                melee_fire_on_hit.bonus_damage > 0,
                "ability '{ability_id}' must author positive melee_fire_on_hit.bonus_damage"
            );
            assert!(
                (1..=60_000).contains(&melee_fire_on_hit.burn_duration_ms),
                "ability '{ability_id}' must author melee_fire_on_hit.burn_duration_ms between 1 and 60000"
            );
            assert!(
                (1..=melee_fire_on_hit.burn_duration_ms)
                    .contains(&melee_fire_on_hit.burn_tick_interval_ms),
                "ability '{ability_id}' must author a positive burn tick interval no longer than its duration"
            );
            assert!(
                melee_fire_on_hit.burn_tick_damage > 0,
                "ability '{ability_id}' must author positive melee_fire_on_hit.burn_tick_damage"
            );
            assert!(
                (2..=100).contains(&melee_fire_on_hit.burn_max_stacks),
                "ability '{ability_id}' must author melee_fire_on_hit.burn_max_stacks between 2 and 100"
            );
            assert!(
                !normalize_identifier(melee_fire_on_hit.burn_status_stack_group.as_str())
                    .is_empty(),
                "ability '{ability_id}' must author melee_fire_on_hit.burn_status_stack_group"
            );
        }
        if ability_id == RUIN_FLAMING_WEAPON_ABILITY_ID {
            let melee_fire_on_hit =
                melee_fire_on_hit.expect("Flaming Weapon must author melee_fire_on_hit tuning");
            assert_eq!(
                spell_school_id, "RUIN",
                "Flaming Weapon must remain a Ruin passive"
            );
            assert_eq!(
                ability_kind, "PASSIVE",
                "Flaming Weapon must remain passive"
            );
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Flaming Weapon must carry the PASSIVE ability tag"
            );
            assert_eq!(melee_fire_on_hit.bonus_damage, 5);
            assert_eq!(melee_fire_on_hit.burn_duration_ms, 5_000);
            assert_eq!(melee_fire_on_hit.burn_tick_interval_ms, 1_000);
            assert_eq!(melee_fire_on_hit.burn_tick_damage, 1);
            assert_eq!(melee_fire_on_hit.burn_max_stacks, 5);
            assert_eq!(
                normalize_identifier(melee_fire_on_hit.burn_status_stack_group.as_str()),
                "FLAMING_WEAPON_BURN"
            );
            assert_eq!(
                melee_fire_on_hit.burn_dispel_types,
                vec![StatusDispelType::Magic]
            );
        }
        if let Some(melee_poison_on_hit) = melee_poison_on_hit {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author melee_poison_on_hit for PASSIVE gameplay"
            );
            assert!(
                melee_poison_on_hit.proc_chance.is_finite()
                    && melee_poison_on_hit.proc_chance > 0.0
                    && melee_poison_on_hit.proc_chance <= 1.0,
                "ability '{ability_id}' must author melee_poison_on_hit.proc_chance > 0 and <= 1"
            );
            assert!(
                (1..=60_000).contains(&melee_poison_on_hit.poison_duration_ms),
                "ability '{ability_id}' must author melee_poison_on_hit.poison_duration_ms between 1 and 60000"
            );
            assert!(
                (1..=melee_poison_on_hit.poison_duration_ms)
                    .contains(&melee_poison_on_hit.poison_tick_interval_ms),
                "ability '{ability_id}' must author a positive poison tick interval no longer than its duration"
            );
            assert!(
                melee_poison_on_hit.poison_tick_damage > 0,
                "ability '{ability_id}' must author positive melee_poison_on_hit.poison_tick_damage"
            );
            assert!(
                (2..=100).contains(&melee_poison_on_hit.poison_max_stacks),
                "ability '{ability_id}' must author melee_poison_on_hit.poison_max_stacks between 2 and 100"
            );
            assert!(
                !normalize_identifier(melee_poison_on_hit.poison_status_stack_group.as_str())
                    .is_empty(),
                "ability '{ability_id}' must author melee_poison_on_hit.poison_status_stack_group"
            );
        }
        if ability_id == BLIGHT_TOXIC_WEAPON_ABILITY_ID {
            let tuning =
                melee_poison_on_hit.expect("Toxic Weapon must author melee_poison_on_hit tuning");
            assert_eq!(spell_school_id, "BLIGHT");
            assert_eq!(ability_kind, "PASSIVE");
            assert!(ability
                .ability_tags
                .iter()
                .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
            assert!((tuning.proc_chance - 0.25).abs() < 0.0001);
            assert_eq!(tuning.poison_duration_ms, 6_000);
            assert_eq!(tuning.poison_tick_interval_ms, 1_000);
            assert_eq!(tuning.poison_tick_damage, 2);
            assert_eq!(tuning.poison_max_stacks, 5);
            assert_eq!(
                normalize_identifier(tuning.poison_status_stack_group.as_str()),
                "POISON"
            );
            assert_eq!(tuning.poison_dispel_types, vec![StatusDispelType::Poison]);
        }
        if let Some(fire_spell_ignite) = fire_spell_ignite {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author fire_spell_ignite for PASSIVE gameplay"
            );
            assert!(
                fire_spell_ignite.radius_meters.is_finite()
                    && (0.1..=50.0).contains(&fire_spell_ignite.radius_meters),
                "ability '{ability_id}' must author fire_spell_ignite.radius_meters between 0.1 and 50"
            );
            assert!(
                (1..=60_000).contains(&fire_spell_ignite.burn_duration_ms),
                "ability '{ability_id}' must author fire_spell_ignite.burn_duration_ms between 1 and 60000"
            );
            assert!(
                (1..=fire_spell_ignite.burn_duration_ms)
                    .contains(&fire_spell_ignite.burn_tick_interval_ms),
                "ability '{ability_id}' must author a positive Wildfire burn interval no longer than its duration"
            );
            assert!(
                fire_spell_ignite.burn_tick_damage > 0,
                "ability '{ability_id}' must author positive fire_spell_ignite.burn_tick_damage"
            );
            assert!(
                (1..=100).contains(&fire_spell_ignite.burn_max_stacks),
                "ability '{ability_id}' must author fire_spell_ignite.burn_max_stacks between 1 and 100"
            );
            assert!(
                !normalize_identifier(fire_spell_ignite.burn_status_stack_group.as_str())
                    .is_empty(),
                "ability '{ability_id}' must author fire_spell_ignite.burn_status_stack_group"
            );
        }
        if ability_id == RUIN_WILDFIRE_ABILITY_ID {
            let fire_spell_ignite =
                fire_spell_ignite.expect("Wildfire must author fire_spell_ignite tuning");
            assert_eq!(spell_school_id, "RUIN", "Wildfire must remain Ruin");
            assert_eq!(ability_kind, "PASSIVE", "Wildfire must remain passive");
            assert!(ability
                .ability_tags
                .iter()
                .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
            assert!((fire_spell_ignite.radius_meters - 5.0).abs() < 0.0001);
            assert_eq!(fire_spell_ignite.burn_duration_ms, 5_000);
            assert_eq!(fire_spell_ignite.burn_tick_interval_ms, 1_000);
            assert_eq!(fire_spell_ignite.burn_tick_damage, 1);
            assert_eq!(fire_spell_ignite.burn_max_stacks, 5);
            assert_eq!(
                normalize_identifier(fire_spell_ignite.burn_status_stack_group.as_str()),
                "WILDFIRE_BURN"
            );
            assert_eq!(
                fire_spell_ignite.burn_dispel_types,
                vec![StatusDispelType::Magic]
            );
        }
        assert!(
            fire_damage_taken_mana_restore_ratio.is_finite()
                && (0.0..=1.0).contains(&fire_damage_taken_mana_restore_ratio),
            "ability '{ability_id}' must author fire_damage_taken_mana_restore_ratio between 0 and 1"
        );
        if fire_damage_taken_mana_restore_ratio > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author fire_damage_taken_mana_restore_ratio for PASSIVE gameplay"
            );
        }
        if ability_id == RUIN_FURNACE_ABILITY_ID {
            assert_eq!(
                spell_school_id, "RUIN",
                "Furnace must remain a Ruin passive"
            );
            assert_eq!(ability_kind, "PASSIVE", "Furnace must remain passive");
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Furnace must carry the PASSIVE ability tag"
            );
            assert!(
                (fire_damage_taken_mana_restore_ratio - 1.0).abs() < 0.0001,
                "Furnace must restore mana equal to confirmed fire damage taken"
            );
        }
        if critical_strike_cooldown_reduction_ms > 0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author critical_strike_cooldown_reduction_ms for PASSIVE gameplay"
            );
            assert!(
                critical_strike_cooldown_reduction_ms <= 60_000,
                "ability '{ability_id}' critical_strike_cooldown_reduction_ms must not exceed 60000"
            );
        }
        if ability_id == RUIN_ACCELERATION_ABILITY_ID {
            assert_eq!(
                spell_school_id, "RUIN",
                "Acceleration must remain a Ruin passive"
            );
            assert_eq!(ability_kind, "PASSIVE", "Acceleration must remain passive");
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Acceleration must carry the PASSIVE ability tag"
            );
            assert_eq!(
                critical_strike_cooldown_reduction_ms, 1_000,
                "Acceleration must advance active ability cooldowns by 1 second"
            );
        }
        assert!(
            movement_spell_cast_time_reduction.is_finite()
                && (0.0..1.0).contains(&movement_spell_cast_time_reduction),
            "ability '{ability_id}' must author movement_spell_cast_time_reduction between 0 inclusive and 1 exclusive"
        );
        if movement_spell_cast_time_reduction > 0.0 || movement_spell_cast_time_buff_duration_ms > 0
        {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author movement spell cast-time tuning for PASSIVE gameplay"
            );
            assert!(
                movement_spell_cast_time_reduction > 0.0
                    && movement_spell_cast_time_buff_duration_ms > 0,
                "ability '{ability_id}' must author both movement spell cast-time reduction and buff duration"
            );
            assert!(
                movement_spell_cast_time_buff_duration_ms <= 60_000,
                "ability '{ability_id}' movement spell cast-time buff duration must not exceed 60000"
            );
        }
        if ability_id == RUIN_QUICKENING_ABILITY_ID {
            assert_eq!(
                spell_school_id, "RUIN",
                "Quickening must remain a Ruin passive"
            );
            assert_eq!(ability_kind, "PASSIVE", "Quickening must remain passive");
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Quickening must carry the PASSIVE ability tag"
            );
            assert!(
                (movement_spell_cast_time_reduction - 0.5).abs() < 0.0001,
                "Quickening must reduce the next non-instant spell's cast time by 50%"
            );
            assert_eq!(
                movement_spell_cast_time_buff_duration_ms, 5_000,
                "Quickening must last 5 seconds"
            );
        }
        if !critical_spell_proc_action_id.is_empty() {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author critical_spell_proc_action_id for PASSIVE gameplay"
            );
            assert!(
                progression_catalog().abilities.iter().any(|candidate| {
                    normalize_identifier(candidate.action_id.as_str())
                        == critical_spell_proc_action_id
                        && ability_gameplay_kind(candidate) == "SPELL"
                }),
                "ability '{ability_id}' critical_spell_proc_action_id must reference an authored spell action"
            );
        }
        assert!(
            auto_attack_proc_chance.is_finite() && (0.0..=1.0).contains(&auto_attack_proc_chance),
            "ability '{ability_id}' must author auto_attack_proc_chance between 0 and 1"
        );
        if !auto_attack_proc_action_id.is_empty() || auto_attack_proc_chance > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author auto-attack proc tuning for PASSIVE gameplay"
            );
            assert!(
                !auto_attack_proc_action_id.is_empty() && auto_attack_proc_chance > 0.0,
                "ability '{ability_id}' must author both auto_attack_proc_action_id and a positive auto_attack_proc_chance"
            );
            assert!(
                progression_catalog().abilities.iter().any(|candidate| {
                    normalize_identifier(candidate.action_id.as_str())
                        == auto_attack_proc_action_id
                        && ability_gameplay_kind(candidate) == "MELEE"
                        && candidate
                            .combat_discipline_id
                            .as_deref()
                            .map(normalize_identifier)
                            .is_some_and(|candidate_discipline| {
                                candidate_discipline == combat_discipline_id
                            })
                }),
                "ability '{ability_id}' auto_attack_proc_action_id must reference an authored melee action in the same combat discipline"
            );
        }
        if ability_id == DAGGER_RESTLESS_BLADES_ABILITY_ID {
            assert_eq!(
                combat_discipline_id, COMBAT_PROFILE_DAGGERS,
                "Restless Blades must remain a Daggers passive"
            );
            assert_eq!(
                ability_kind, "PASSIVE",
                "Restless Blades must remain passive"
            );
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Restless Blades must carry the PASSIVE ability tag"
            );
            assert_eq!(
                auto_attack_proc_action_id, "DAGGER_QUICK_CUT",
                "Restless Blades must proc the authored Quick Cut technique"
            );
            assert!(
                (auto_attack_proc_chance - 0.10).abs() < 0.0001,
                "Restless Blades must have a 10% proc chance"
            );
        }
        if ability_id == RUIN_CHAIN_REACTION_ABILITY_ID {
            assert_eq!(
                spell_school_id, "RUIN",
                "Chain Reaction must remain a Ruin passive"
            );
            assert_eq!(
                ability_kind, "PASSIVE",
                "Chain Reaction must remain passive"
            );
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Chain Reaction must carry the PASSIVE ability tag"
            );
            assert_eq!(
                critical_spell_proc_action_id, "BOLT",
                "Chain Reaction must proc the authored Bolt spell"
            );
        }
        if ability_id == RUIN_RIME_ABILITY_ID {
            assert_eq!(
                spell_school_id, "BLIGHT",
                "Rime must remain a Blight ability"
            );
            assert_eq!(ability_kind, "SPELL", "Rime must remain an active spell");
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "ACTION_BAR_ACTION"),
                "Rime must carry the ACTION_BAR_ACTION ability tag"
            );
        }
        assert!(
            frozen_melee_first_hit_damage_bonus.is_finite()
                && (0.0..=1.0).contains(&frozen_melee_first_hit_damage_bonus),
            "ability '{ability_id}' must author frozen_melee_first_hit_damage_bonus between 0 and 1"
        );
        if frozen_melee_first_hit_damage_bonus > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author frozen_melee_first_hit_damage_bonus for PASSIVE gameplay"
            );
        }
        if ability_id == RUIN_FRACTURE_ABILITY_ID {
            assert_eq!(
                spell_school_id, "BLIGHT",
                "Fracture must remain a Blight passive"
            );
            assert_eq!(ability_kind, "PASSIVE", "Fracture must remain passive");
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Fracture must carry the PASSIVE ability tag"
            );
            assert!(
                (frozen_melee_first_hit_damage_bonus - 0.5).abs() < 0.0001,
                "Fracture must grant 50% increased damage to the first qualifying melee hit"
            );
        }
        assert!(
            noncritical_lightning_spell_crit_chance_bonus.is_finite()
                && (0.0..=1.0).contains(&noncritical_lightning_spell_crit_chance_bonus),
            "ability '{ability_id}' must author noncritical_lightning_spell_crit_chance_bonus between 0 and 1"
        );
        if noncritical_lightning_spell_crit_chance_bonus > 0.0 {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author noncritical_lightning_spell_crit_chance_bonus for PASSIVE gameplay"
            );
        }
        if ability_id == RUIN_POTENTIAL_ABILITY_ID {
            assert_eq!(
                spell_school_id, "RUIN",
                "Potential must remain a Ruin passive"
            );
            assert_eq!(ability_kind, "PASSIVE", "Potential must remain passive");
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Potential must carry the PASSIVE ability tag"
            );
            assert!(
                (noncritical_lightning_spell_crit_chance_bonus - 0.05).abs() < 0.0001,
                "Potential must grant 5 percentage points of crit chance per eligible strike"
            );
        }
        if ability_id == SUBTLETY_OPPORTUNIST_ABILITY_ID {
            assert_eq!(
                combat_discipline_id, COMBAT_PROFILE_DAGGERS,
                "Opportunist must remain a Subtlety passive"
            );
            assert_eq!(ability_kind, "PASSIVE", "Opportunist must remain passive");
            assert!(
                (disabled_target_damage_bonus - 0.15).abs() < 0.0001,
                "Opportunist must grant 15% increased damage against disabled targets"
            );
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Opportunist must carry the PASSIVE ability tag"
            );
        }
        if ability_id == ARCHER_MAVERICK_ABILITY_ID {
            assert_eq!(combat_discipline_id, COMBAT_PROFILE_ARCHER_BOW);
            assert_eq!(ability_kind, "PASSIVE");
            assert!((isolated_damage_bonus - 0.15).abs() < 0.0001);
            assert!((isolated_ally_radius_meters - 10.0).abs() < 0.0001);
        }
        if ability_id == ARCHER_POINT_BLANK_ABILITY_ID {
            assert_eq!(combat_discipline_id, COMBAT_PROFILE_ARCHER_BOW);
            assert_eq!(ability_kind, "PASSIVE");
            assert!((point_blank_damage_bonus - 0.30).abs() < 0.0001);
            assert!((point_blank_full_bonus_range_meters - 2.5).abs() < 0.0001);
            assert!((point_blank_zero_bonus_range_meters - 18.0).abs() < 0.0001);
        }
        if ability_id == ARCHER_CAREFUL_AIM_ABILITY_ID {
            assert_eq!(combat_discipline_id, COMBAT_PROFILE_ARCHER_BOW);
            assert_eq!(ability_kind, "PASSIVE");
            assert!((stationary_target_damage_bonus - 0.15).abs() < 0.0001);
            assert_eq!(stationary_target_window_ms, 250);
            assert!((stationary_target_max_displacement_meters - 0.05).abs() < 0.0001);
        }
        if ability_id == ARCHER_HEARTSEEKER_ABILITY_ID {
            assert_eq!(combat_discipline_id, COMBAT_PROFILE_ARCHER_BOW);
            assert_eq!(ability_kind, "MELEE");
            assert_eq!(ability.gameplay.base_damage, Some(42));
            assert!(stationary_target_auto_crit);
            assert_eq!(stationary_target_window_ms, 250);
            assert!((stationary_target_max_displacement_meters - 0.05).abs() < 0.0001);
        }
        if ability_id == ARCHER_PERFORATION_ABILITY_ID {
            assert_eq!(combat_discipline_id, COMBAT_PROFILE_ARCHER_BOW);
            assert_eq!(ability_kind, "PASSIVE");
            assert!(projectile_piercing);
        }
        if ability_id == SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID {
            assert_eq!(
                combat_discipline_id, COMBAT_PROFILE_DAGGERS,
                "Surprise Attacks must remain a Subtlety passive"
            );
            assert_eq!(
                ability_kind, "PASSIVE",
                "Surprise Attacks must remain passive"
            );
            assert_eq!(
                stealth_attack_stun_ms, 2_000,
                "Surprise Attacks must stun stealth-attack targets for 2 seconds"
            );
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Surprise Attacks must carry the PASSIVE ability tag"
            );
        }
        if ability_id == SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID {
            assert_eq!(
                combat_discipline_id, COMBAT_PROFILE_DAGGERS,
                "Tactical Advantage must remain a Subtlety passive"
            );
            assert_eq!(
                ability_kind, "PASSIVE",
                "Tactical Advantage must remain passive"
            );
            assert!(
                (behind_target_damage_bonus - 0.15).abs() < 0.0001,
                "Tactical Advantage must grant 15% increased damage from behind"
            );
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Tactical Advantage must carry the PASSIVE ability tag"
            );
        }
        if ability_id == SUBTLETY_FLEET_FOOTED_ABILITY_ID {
            assert_eq!(
                combat_discipline_id, COMBAT_PROFILE_DAGGERS,
                "Fleet Footed must remain a Subtlety passive"
            );
            assert_eq!(ability_kind, "PASSIVE", "Fleet Footed must remain passive");
            assert!(
                (dodge_recharge_time_reduction - 0.2).abs() < 0.0001,
                "Fleet Footed must reduce Dodge recharge time by 20%"
            );
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Fleet Footed must carry the PASSIVE ability tag"
            );
        }
        if ability_id == SUBTLETY_LINGERING_SHADE_ABILITY_ID {
            assert_eq!(
                combat_discipline_id, COMBAT_PROFILE_DAGGERS,
                "Lingering Shade must remain a Subtlety passive"
            );
            assert_eq!(
                ability_kind, "PASSIVE",
                "Lingering Shade must remain passive"
            );
            assert_eq!(
                movement_return.map(|definition| definition.window_ms),
                Some(3_000),
                "Lingering Shade must provide a 3 second movement return window"
            );
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Lingering Shade must carry the PASSIVE ability tag"
            );
        }
        let authors_timed_mode_lifecycle = ability.gameplay.duration_ms.is_some()
            || ability.gameplay.break_on_attack
            || ability.gameplay.break_on_direct_damage;
        if authors_timed_mode_lifecycle {
            assert_eq!(
                ability_kind, ABILITY_KIND_COMBAT_MODE_TOGGLE,
                "ability '{ability_id}' may only author timed mode lifecycle fields for COMBAT_MODE_TOGGLE gameplay"
            );
        }
        if ability_id == DAGGER_SHROUD_ABILITY_ID {
            assert_eq!(
                ability_kind, ABILITY_KIND_COMBAT_MODE_TOGGLE,
                "Subtlety Shroud must remain a combat-mode ability"
            );
            assert!(
                ability.gameplay.cooldown_ms.is_some_and(|value| value > 0),
                "Subtlety Shroud must define a positive cooldown_ms"
            );
            assert!(
                ability.gameplay.duration_ms.is_some_and(|value| value > 0),
                "Subtlety Shroud must define a positive duration_ms"
            );
            assert!(
                ability.gameplay.break_on_attack,
                "Subtlety Shroud must break on attack"
            );
            assert!(
                ability.gameplay.break_on_direct_damage,
                "Subtlety Shroud must break on direct damage"
            );
        }
        assert!(
            ability_kind == "SPELL"
                || ability_kind == "PASSIVE"
                || actor_scope == "NPC"
                || resolved_combat_discipline_id_for_ability_definition(ability).is_some(),
            "ability '{ability_id}' must resolve to a combat profile unless it is a generic spell-school ability or NPC-only action"
        );
        if ability_kind == "MOVEMENT" {
            assert!(
                ability.gameplay.delivery.is_some(),
                "movement ability '{ability_id}' must define gameplay.delivery"
            );
            let movement = movement_delivery_definition(ability_id.as_str(), &ability.gameplay)
                .expect("validated movement ability must define gameplay.delivery");
            validate_movement_delivery(ability_id.as_str(), &movement);
        }

        if ability_kind == "SPELL" {
            validate_spell_gameplay(ability_id.as_str(), &ability.gameplay);
        } else if ability_kind == "MOVEMENT" {
            assert!(
                ability.gameplay.cast_time_ms.is_none()
                    && ability.gameplay.cast_mobility.is_empty()
                    && ability.gameplay.targeting.is_empty()
                    && ability.gameplay.requires_target.is_none()
                    && ability.gameplay.aim_radius.is_none()
                    && ability.gameplay.melee_targeting.is_none()
                    && ability.gameplay.minimum_range.is_none()
                    && ability.gameplay.target_health_damage_scaling.is_none()
                    && ability.gameplay.resource_cost.is_none()
                    && ability.gameplay.primary_resource_gain_on_cast == 0.0
                    && !ability.gameplay.arms_auto_attack_on_cast,
                "movement ability '{ability_id}' must define execution fields inside gameplay.delivery"
            );
            assert!(
                ability.gameplay.global_cooldown_ms.is_none(),
                "movement ability '{ability_id}' must define global_cooldown_ms inside gameplay.delivery"
            );
        } else if ability_kind == "MELEE" {
            assert!(
                ability.gameplay.cast_time_ms.is_none()
                    && ability.gameplay.cast_mobility.is_empty()
                    && ability.gameplay.targeting.is_empty()
                    && ability.gameplay.requires_target.is_none()
                    && ability.gameplay.aim_radius.is_none()
                    && ability.gameplay.resource_cost.is_none()
                    && ability.gameplay.primary_resource_gain_on_cast == 0.0
                    && !ability.gameplay.arms_auto_attack_on_cast
                    && ability.gameplay.delivery.is_none(),
                "melee ability '{ability_id}' must not define spell or movement delivery gameplay fields"
            );
        } else {
            assert!(
                ability.gameplay.cast_time_ms.is_none()
                    && ability.gameplay.cast_mobility.is_empty()
                    && ability.gameplay.targeting.is_empty()
                    && ability.gameplay.requires_target.is_none()
                    && ability.gameplay.aim_radius.is_none()
                    && ability.gameplay.melee_targeting.is_none()
                    && ability.gameplay.melee_impact_effects.is_empty()
                    && ability.gameplay.minimum_range.is_none()
                    && ability.gameplay.target_health_damage_scaling.is_none()
                    && ability.gameplay.resource_cost.is_none()
                    && ability.gameplay.global_cooldown_ms.is_none()
                    && ability.gameplay.primary_resource_gain_on_cast == 0.0
                    && !ability.gameplay.arms_auto_attack_on_cast
                    && ability.gameplay.delivery.is_none(),
                "non-spell/non-melee ability '{ability_id}' must not define delivery gameplay fields"
            );
        }

        if ability_kind == "MELEE" {
            validate_melee_gameplay_fields(ability_id.as_str(), &ability.gameplay);
            validate_melee_impact_effects(
                ability_id.as_str(),
                &ability.gameplay.melee_impact_effects,
            );
        } else {
            assert!(
                ability.gameplay.gap_close.is_none(),
                "non-melee ability '{ability_id}' must not define gap_close"
            );
            assert!(
                ability.gameplay.melee_timed_movement.is_none(),
                "non-melee ability '{ability_id}' must not define melee_timed_movement"
            );
            assert!(
                ability.gameplay.melee_evasive_leap.is_none(),
                "non-melee ability '{ability_id}' must not define melee_evasive_leap"
            );
            assert!(
                ability.gameplay.melee_channel.is_none(),
                "non-melee ability '{ability_id}' must not define melee_channel"
            );
            assert!(
                ability.gameplay.melee_targeting.is_none(),
                "non-melee ability '{ability_id}' must not define melee_targeting"
            );
            assert!(
                ability.gameplay.melee_impact_effects.is_empty(),
                "non-melee ability '{ability_id}' must not define melee_impact_effects"
            );
        }
    }

    let authored_player_passives: HashSet<_> = progression_catalog()
        .abilities
        .iter()
        .filter(|ability| normalize_identifier(ability.actor_scope.as_str()) != "NPC")
        .filter(|ability| normalize_identifier(ability.selection_kind.as_str()) == "PASSIVE")
        .map(|ability| normalize_identifier(ability.ability_id.as_str()))
        .collect();
    let inventoried_player_passives: HashSet<_> = PLAYER_PASSIVE_RUNTIME_INVENTORY
        .iter()
        .map(|ability_id| normalize_identifier(ability_id))
        .collect();
    assert_eq!(
        authored_player_passives, inventoried_player_passives,
        "every authored player passive must be present in the selected-passive runtime inventory"
    );
}

fn validate_blade_twisting_tuning(
    ability_id: &str,
    ability_kind: &str,
    combat_discipline_id: &str,
    gameplay: &AbilityGameplayDefinition,
) {
    let ratio = gameplay.blade_twisting_bleed_damage_ratio;
    let duration_ms = gameplay.blade_twisting_bleed_duration_ms;
    let tick_interval_ms = gameplay.blade_twisting_bleed_tick_interval_ms;
    assert!(
        ratio.is_finite() && (0.0..=1.0).contains(&ratio),
        "ability '{ability_id}' must author blade_twisting_bleed_damage_ratio between 0 and 1"
    );

    if ability_id == DAGGER_BLADE_TWISTING_ABILITY_ID {
        assert_eq!(ability_kind, "SPELL", "Blade Twisting must remain a spell");
        assert_eq!(
            combat_discipline_id, COMBAT_PROFILE_DAGGERS,
            "Blade Twisting must remain a Dagger ability"
        );
        assert_eq!(
            gameplay.cooldown_ms,
            Some(30_000),
            "Blade Twisting must have a 30 second cooldown"
        );
        assert!(
            (ratio - 0.5).abs() < 0.0001,
            "Blade Twisting must author a 50% Bleed damage ratio"
        );
        assert_eq!(
            duration_ms, 5_000,
            "Blade Twisting must author a 5 second Bleed"
        );
        assert_eq!(
            tick_interval_ms, 1_000,
            "Blade Twisting must author a 1 second Bleed tick interval"
        );
    } else {
        assert_eq!(
            ratio, 0.0,
            "only Blade Twisting may author blade_twisting_bleed_damage_ratio"
        );
        assert_eq!(
            duration_ms, 0,
            "only Blade Twisting may author blade_twisting_bleed_duration_ms"
        );
        assert_eq!(
            tick_interval_ms, 0,
            "only Blade Twisting may author blade_twisting_bleed_tick_interval_ms"
        );
    }
}

fn validated_ability_actor_scope(ability_id: &str, actor_scope: &str) -> String {
    let actor_scope = normalize_identifier(actor_scope);
    assert!(
        matches!(actor_scope.as_str(), "PLAYER" | "NPC" | "BOTH"),
        "ability '{ability_id}' must define actor_scope as PLAYER, NPC, or BOTH"
    );
    actor_scope
}

fn validate_melee_gameplay_fields(ability_id: &str, gameplay: &AbilityGameplayDefinition) {
    let authored_movement_count = usize::from(gameplay.gap_close.is_some())
        + usize::from(gameplay.melee_timed_movement.is_some())
        + usize::from(gameplay.melee_evasive_leap.is_some());
    if authored_movement_count > 1 {
        panic!("melee ability '{ability_id}' must define at most one authored movement");
    }
    if gameplay.melee_channel.is_some() && authored_movement_count > 0 {
        panic!(
            "melee ability '{ability_id}' must not combine melee_channel with authored movement"
        );
    }
    if let Some(movement) = gameplay.melee_timed_movement.as_ref() {
        validate_melee_timed_movement(ability_id, movement);
    }
    if let Some(leap) = gameplay.melee_evasive_leap.as_ref() {
        validate_melee_evasive_leap(ability_id, leap);
    }
    if let Some(channel) = gameplay.melee_channel.as_ref() {
        validate_melee_channel(ability_id, channel);
    }

    let airborne_targeting_mode = normalize_identifier(
        gameplay
            .airborne_targeting_mode
            .as_deref()
            .unwrap_or_default(),
    );
    assert!(
        airborne_targeting_mode == "ANY_TARGET" || airborne_targeting_mode == "GROUNDED_TARGET_ONLY",
        "melee ability '{ability_id}' airborne_targeting_mode must be ANY_TARGET or GROUNDED_TARGET_ONLY, got '{airborne_targeting_mode}'"
    );
    let range = gameplay.range.unwrap_or(0.0);
    assert!(
        range.is_finite() && range > 0.0,
        "melee ability '{ability_id}' range must be positive"
    );
    let minimum_range = gameplay.minimum_range.unwrap_or(0.0);
    assert!(
        minimum_range.is_finite() && minimum_range >= 0.0,
        "melee ability '{ability_id}' minimum_range must be non-negative"
    );
    assert!(
        minimum_range < range,
        "melee ability '{ability_id}' minimum_range must be less than range"
    );
    validate_authored_global_cooldown_ms(
        ability_id,
        gameplay.uses_global_cooldown,
        gameplay.global_cooldown_ms,
    );
    validate_target_health_damage_scaling(
        ability_id,
        gameplay.target_health_damage_scaling.as_ref(),
    );

    let Some(targeting) = gameplay.melee_targeting.as_ref() else {
        return;
    };
    let targeting_kind = normalize_identifier(targeting.kind.as_str());
    match targeting_kind.as_str() {
        "TARGET" => {
            assert!(
                targeting.requires_target.unwrap_or(true),
                "melee ability '{ability_id}' TARGET melee_targeting must require a target"
            );
        }
        "CASTER_RADIUS" => {
            assert!(
                !targeting.requires_target.unwrap_or(false),
                "melee ability '{ability_id}' CASTER_RADIUS melee_targeting must not require a target"
            );
        }
        "CASTER_CONE" => {
            assert!(
                !targeting.requires_target.unwrap_or(false),
                "melee ability '{ability_id}' CASTER_CONE melee_targeting must not require a target"
            );
            let angle_degrees = targeting.angle_degrees.unwrap_or(0.0);
            assert!(
                angle_degrees.is_finite() && angle_degrees > 0.0 && angle_degrees <= 360.0,
                "melee ability '{ability_id}' CASTER_CONE melee_targeting.angle_degrees must be in (0, 360]"
            );
        }
        "CASTER_RECTANGLE" => {
            assert!(
                !targeting.requires_target.unwrap_or(false),
                "melee ability '{ability_id}' CASTER_RECTANGLE melee_targeting must not require a target"
            );
            let width = targeting.width.unwrap_or(0.0);
            assert!(
                width.is_finite() && width > 0.0,
                "melee ability '{ability_id}' CASTER_RECTANGLE melee_targeting.width must be positive"
            );
        }
        _ => panic!(
            "melee ability '{ability_id}' melee_targeting.kind must be TARGET, CASTER_RADIUS, CASTER_CONE, or CASTER_RECTANGLE, got '{targeting_kind}'"
        ),
    }
}

fn validate_melee_evasive_leap(ability_id: &str, leap: &MeleeEvasiveLeapDefinition) {
    assert!(
        leap.duration_ms > 0,
        "melee ability '{ability_id}' melee_evasive_leap.duration_ms must be positive"
    );
    assert!(
        leap.arc_height.is_finite() && leap.arc_height > 0.0,
        "melee ability '{ability_id}' melee_evasive_leap.arc_height must be positive"
    );
}

fn validate_melee_channel(ability_id: &str, channel: &MeleeChannelDefinition) {
    assert!(
        channel.duration_ms > 0,
        "melee ability '{ability_id}' melee_channel.duration_ms must be positive"
    );
    if channel.use_authored_hit_windows {
        assert_eq!(
            channel.first_tick_delay_ms, 0,
            "melee ability '{ability_id}' authored-hit-window melee_channel.first_tick_delay_ms must be zero"
        );
        assert_eq!(
            channel.tick_interval_ms, 0,
            "melee ability '{ability_id}' authored-hit-window melee_channel.tick_interval_ms must be zero"
        );
    } else {
        assert!(
            channel.first_tick_delay_ms > 0
                && channel.first_tick_delay_ms <= channel.duration_ms,
            "melee ability '{ability_id}' melee_channel.first_tick_delay_ms must be in (0, duration_ms]"
        );
        assert!(
            channel.tick_interval_ms > 0 && channel.tick_interval_ms <= channel.duration_ms,
            "melee ability '{ability_id}' melee_channel.tick_interval_ms must be in (0, duration_ms]"
        );
    }
    assert!(
        channel.resource_cost_per_release.is_finite() && channel.resource_cost_per_release >= 0.0,
        "melee ability '{ability_id}' melee_channel.resource_cost_per_release must be finite and non-negative"
    );
    assert!(
        channel.resource_cost_per_release == 0.0 || !channel.use_authored_hit_windows,
        "melee ability '{ability_id}' melee_channel.resource_cost_per_release needs tick-driven releases, not authored hit windows"
    );
    let per_release_kind = normalize_identifier(channel.resource_kind_per_release.as_str());
    assert!(
        per_release_kind.is_empty() || per_release_kind == "MANA" || per_release_kind == "STAMINA",
        "melee ability '{ability_id}' melee_channel.resource_kind_per_release must be MANA, STAMINA, or empty"
    );
    assert!(
        per_release_kind.is_empty() || channel.resource_cost_per_release > 0.0,
        "melee ability '{ability_id}' melee_channel.resource_kind_per_release needs a non-zero resource_cost_per_release"
    );
    assert!(
        channel.cancel_on_movement,
        "melee ability '{ability_id}' melee_channel.cancel_on_movement must be true"
    );
}

fn validate_melee_timed_movement(ability_id: &str, movement: &MeleeTimedMovementDefinition) {
    let kind = normalize_identifier(movement.kind.as_str());
    assert!(
        kind == "BACKSTEP",
        "melee ability '{ability_id}' melee_timed_movement.kind must be BACKSTEP, got '{kind}'"
    );
    assert!(
        movement.start_delay_ms > 0,
        "melee ability '{ability_id}' melee_timed_movement.start_delay_ms must be positive"
    );
    let direction = normalize_identifier(movement.direction.as_str());
    assert!(
        direction == "BACKWARD",
        "melee ability '{ability_id}' melee_timed_movement.direction must be BACKWARD, got '{direction}'"
    );
    assert!(
        movement.distance.is_finite() && movement.distance > 0.0,
        "melee ability '{ability_id}' melee_timed_movement.distance must be positive"
    );
    assert!(
        movement.speed.is_finite() && movement.speed > 0.0,
        "melee ability '{ability_id}' melee_timed_movement.speed must be positive"
    );
    let collision_policy = normalize_identifier(movement.collision_policy.as_str());
    assert!(
        collision_policy == "STOP_AT_BLOCK",
        "melee ability '{ability_id}' melee_timed_movement.collision_policy must be STOP_AT_BLOCK, got '{collision_policy}'"
    );
    let facing_policy = normalize_identifier(movement.facing_policy.as_str());
    assert!(
        facing_policy == "FACE_START",
        "melee ability '{ability_id}' melee_timed_movement.facing_policy must be FACE_START, got '{facing_policy}'"
    );
}

fn validate_melee_impact_effects(ability_id: &str, effects: &[MeleeImpactEffectDefinition]) {
    for effect in effects {
        match effect {
            MeleeImpactEffectDefinition::Knockback { distance_meters } => {
                assert!(
                    distance_meters.is_finite() && *distance_meters > 0.0,
                    "melee ability '{ability_id}' KNOCKBACK impact effect distance_meters must be positive"
                );
            }
            MeleeImpactEffectDefinition::ApplyStatus { status }
            | MeleeImpactEffectDefinition::ApplyStatusOnHit { status, .. } => {
                validate_status_application_definition(
                    ability_id,
                    "melee_impact_effects[].status",
                    status.kind.as_str(),
                    status.duration_ms,
                    status.status_stack_group.as_deref(),
                    status.slow_pct,
                    status.tick_damage,
                    status.tick_heal,
                    status.tick_interval_ms,
                    status.modifier_scalar,
                    status.absorb_amount,
                    status.absorb_cap,
                    status.max_stacks,
                    status_application_from_definition(
                        status,
                        authored_status_stack_group_default(status.kind.as_str()),
                    ),
                );
            }
            MeleeImpactEffectDefinition::RemoveStatus {
                polarity,
                dispel_types,
                max_count,
            } => {
                assert!(
                    polarity.is_some() || !dispel_types.is_empty(),
                    "melee ability '{ability_id}' REMOVE_STATUS impact effect must define polarity or dispel_types"
                );
                assert!(
                    *max_count > 0,
                    "melee ability '{ability_id}' REMOVE_STATUS impact effect max_count must be at least 1"
                );
            }
            MeleeImpactEffectDefinition::RefreshRandomStatus {
                polarity,
                dispel_types,
                ..
            } => {
                assert!(
                    polarity.is_some() || !dispel_types.is_empty(),
                    "melee ability '{ability_id}' REFRESH_RANDOM_STATUS impact effect must define polarity or dispel_types"
                );
            }
        }
    }
}

fn validate_status_application_definition(
    ability_id: &str,
    path: &str,
    kind: &str,
    duration_ms: u64,
    status_stack_group: Option<&str>,
    slow_pct: f32,
    tick_damage: i32,
    tick_heal: i32,
    tick_interval_ms: u64,
    modifier_scalar: f32,
    absorb_amount: i32,
    absorb_cap: i32,
    max_stacks: u32,
    application: StatusApplication,
) {
    let normalized_kind = normalize_identifier(kind);
    assert!(
        StatusEffectKind::from_wire(normalized_kind.as_str()).is_some(),
        "ability '{ability_id}' {path}.kind '{}' is not a known status kind",
        kind
    );
    assert!(
        duration_ms > 0,
        "ability '{ability_id}' {path}.duration_ms must be positive"
    );
    assert!(
        max_stacks > 0,
        "ability '{ability_id}' {path}.max_stacks must be at least 1"
    );
    if let Some(status_stack_group) = status_stack_group {
        assert!(
            !status_stack_group.trim().is_empty(),
            "ability '{ability_id}' {path}.status_stack_group must not be empty"
        );
    }
    validate_status_payload_fields(
        ability_id,
        path,
        StatusEffectKind::from_wire(normalized_kind.as_str()).expect("status kind was validated"),
        slow_pct,
        tick_damage,
        tick_heal,
        tick_interval_ms,
        modifier_scalar,
        absorb_amount,
        absorb_cap,
    );
    assert!(
        !application.is_invalid(),
        "ability '{ability_id}' {path} has invalid payload fields for status '{}'",
        kind
    );
}

#[allow(clippy::too_many_arguments)]
fn validate_status_payload_fields(
    ability_id: &str,
    path: &str,
    kind: StatusEffectKind,
    slow_pct: f32,
    tick_damage: i32,
    tick_heal: i32,
    tick_interval_ms: u64,
    modifier_scalar: f32,
    absorb_amount: i32,
    absorb_cap: i32,
) {
    AuthoredStatusPayload::new_with_absorb(
        kind,
        slow_pct,
        tick_damage,
        tick_heal,
        tick_interval_ms,
        modifier_scalar,
        absorb_amount,
        absorb_cap,
    )
    .validate(format!("ability '{ability_id}'").as_str(), path)
    .unwrap_or_else(|err| panic!("{err}"));
}

fn validate_spell_gameplay(ability_id: &str, gameplay: &AbilityGameplayDefinition) {
    assert!(
        gameplay.cooldown_ms.is_some(),
        "spell ability '{ability_id}' must define gameplay.cooldown_ms"
    );
    assert!(
        gameplay.uses_global_cooldown.is_some(),
        "spell ability '{ability_id}' must define gameplay.uses_global_cooldown"
    );
    validate_authored_global_cooldown_ms(
        ability_id,
        gameplay.uses_global_cooldown,
        gameplay.global_cooldown_ms,
    );
    assert!(
        gameplay.cast_time_ms.is_some(),
        "spell ability '{ability_id}' must define gameplay.cast_time_ms"
    );
    assert!(
        !gameplay.cast_mobility.trim().is_empty(),
        "spell ability '{ability_id}' must define gameplay.cast_mobility"
    );
    assert!(
        !gameplay.targeting.trim().is_empty(),
        "spell ability '{ability_id}' must define gameplay.targeting"
    );
    assert!(
        gameplay.requires_target.is_some(),
        "spell ability '{ability_id}' must define gameplay.requires_target"
    );
    assert!(
        gameplay.delivery.is_some(),
        "spell ability '{ability_id}' must define gameplay.delivery"
    );
    validate_spell_delivery_damage_type(ability_id, gameplay);
    validate_interrupt_damage_impact_effects(ability_id, gameplay);
    assert!(
        gameplay.minimum_range.is_none(),
        "spell ability '{ability_id}' must not define gameplay.minimum_range"
    );
    assert!(
        gameplay.target_health_damage_scaling.is_none(),
        "spell ability '{ability_id}' must not define gameplay.target_health_damage_scaling"
    );
    assert!(
        gameplay.soulstealer_empowered_damage_bonus.is_finite()
            && (0.0..=1.0).contains(&gameplay.soulstealer_empowered_damage_bonus),
        "spell ability '{ability_id}' must author soulstealer_empowered_damage_bonus between 0 and 1"
    );
    if ability_id == SOULSTEALER_ABILITY_ID {
        assert!(
            (gameplay.soulstealer_empowered_damage_bonus - 0.5).abs() < 0.0001,
            "Soulstealer must author a provisional 50% empowered damage bonus"
        );
    } else {
        assert_eq!(
            gameplay.soulstealer_empowered_damage_bonus, 0.0,
            "only Soulstealer may author soulstealer_empowered_damage_bonus"
        );
    }
}

fn validate_interrupt_damage_impact_effects(
    ability_id: &str,
    gameplay: &AbilityGameplayDefinition,
) {
    let Some(impact_effects) = gameplay
        .delivery
        .as_ref()
        .and_then(|delivery| delivery.get("impact_effects"))
        .and_then(|value| value.as_array())
    else {
        return;
    };

    for effect in impact_effects {
        let kind = effect
            .get("kind")
            .and_then(|value| value.as_str())
            .map(normalize_identifier)
            .unwrap_or_default();
        if kind != "INTERRUPT_CAST_WITH_DAMAGE" {
            continue;
        }

        let damage = effect
            .get("damage")
            .and_then(|value| value.as_i64())
            .unwrap_or(0);
        assert!(
            damage > 0 && damage <= i32::MAX as i64,
            "spell ability '{ability_id}' INTERRUPT_CAST_WITH_DAMAGE damage must be a positive i32"
        );
        let damage_type = effect
            .get("damage_type")
            .and_then(|value| value.as_str())
            .map(normalize_identifier)
            .unwrap_or_default();
        assert!(
            is_known_damage_type(damage_type.as_str()),
            "spell ability '{ability_id}' INTERRUPT_CAST_WITH_DAMAGE has unsupported damage_type '{damage_type}'"
        );
    }
}

fn validate_spell_delivery_damage_type(ability_id: &str, gameplay: &AbilityGameplayDefinition) {
    let Some(delivery) = gameplay.delivery.as_ref() else {
        return;
    };
    let Some(kind) = delivery
        .get("kind")
        .and_then(|value| value.as_str())
        .map(normalize_identifier)
    else {
        return;
    };
    if !matches!(
        kind.as_str(),
        "DIRECT_TARGET"
            | "PROJECTILE"
            | "AREA"
            | "INSTANT_BEAM"
            | "CHANNEL"
            | "EMANATION"
            | "PERSISTENT_AREA"
    ) {
        return;
    }

    let damage_type = delivery
        .get("damage_type")
        .and_then(|value| value.as_str())
        .map(normalize_identifier)
        .unwrap_or_default();
    if ability_id.starts_with("SPELL_") {
        assert!(
            !damage_type.is_empty(),
            "generic spell ability '{ability_id}' must explicitly define gameplay.delivery.damage_type"
        );
    }
    if !damage_type.is_empty() {
        assert!(
            is_known_damage_type(damage_type.as_str()),
            "spell ability '{ability_id}' has unsupported gameplay.delivery.damage_type '{damage_type}'"
        );
    }
}

fn is_known_damage_type(value: &str) -> bool {
    matches!(
        normalize_identifier(value).as_str(),
        "PHYSICAL"
            | "FIRE"
            | "COLD"
            | "AIR"
            | "LIGHTNING"
            | "POISON"
            | "HOLY"
            | "SHADOW"
            | "NECROTIC"
            | "ARCANE"
    )
}

fn validate_target_health_damage_scaling(
    ability_id: &str,
    scaling: Option<&TargetHealthDamageScalingDefinition>,
) {
    let Some(scaling) = scaling else {
        return;
    };
    assert!(
        scaling.min_multiplier.is_finite() && scaling.min_multiplier > 0.0,
        "melee ability '{ability_id}' target_health_damage_scaling.min_multiplier must be positive"
    );
    assert!(
        scaling.max_multiplier.is_finite() && scaling.max_multiplier >= scaling.min_multiplier,
        "melee ability '{ability_id}' target_health_damage_scaling.max_multiplier must be at least min_multiplier"
    );
}

fn validate_authored_global_cooldown_ms(
    ability_id: &str,
    uses_global_cooldown: Option<bool>,
    global_cooldown_ms: Option<u64>,
) {
    if global_cooldown_ms.is_none() {
        return;
    }
    assert!(
        uses_global_cooldown == Some(true),
        "ability '{ability_id}' must only define global_cooldown_ms when uses_global_cooldown is true"
    );
    assert!(
        global_cooldown_ms.unwrap_or(0) > 0,
        "ability '{ability_id}' global_cooldown_ms must be positive"
    );
}

/// A self-directed movement delivery steers off the caster's own facing and
/// lands nothing: no target, no damage, no impact. It exists so a disengage can
/// be an ability in its own right instead of borrowing a melee strike's hit
/// window to carry its movement.
fn validate_self_directed_movement_delivery(
    ability_id: &str,
    movement: &MovementDeliveryDefinition,
) {
    assert_eq!(
        movement.cast_time_ms, 0,
        "self-directed movement ability '{ability_id}' must be instant"
    );
    assert_eq!(
        normalize_identifier(movement.cast_mobility.as_str()),
        "MOBILE",
        "self-directed movement ability '{ability_id}' must use MOBILE cast_mobility"
    );
    assert_eq!(
        normalize_identifier(movement.targeting.as_str()),
        "SELF",
        "self-directed movement ability '{ability_id}' must use SELF targeting"
    );
    assert!(
        !movement.requires_target,
        "self-directed movement ability '{ability_id}' must not require a target"
    );
    assert!(
        !movement.arms_auto_attack_on_cast,
        "self-directed movement ability '{ability_id}' must not arm auto attack"
    );
    assert!(
        movement.resource_cost.is_finite() && movement.resource_cost >= 0.0,
        "self-directed movement ability '{ability_id}' must define a non-negative finite resource_cost"
    );
    assert!(
        movement.speed.is_finite() && movement.speed > 0.0,
        "self-directed movement ability '{ability_id}' must define positive finite speed"
    );
    assert!(
        movement.max_distance.is_finite() && movement.max_distance > 0.0,
        "self-directed movement ability '{ability_id}' must define positive finite max_distance"
    );
    assert_eq!(
        movement.damage, 0,
        "self-directed movement ability '{ability_id}' must not author damage"
    );
    assert_eq!(
        movement.radius, 0.0,
        "self-directed movement ability '{ability_id}' must not author an impact radius"
    );
    assert!(
        movement.impact_effects.is_empty(),
        "self-directed movement ability '{ability_id}' must not author impact effects"
    );
    assert_eq!(
        normalize_identifier(movement.direction.as_str()),
        "BACKWARD",
        "self-directed movement ability '{ability_id}' supports only BACKWARD direction"
    );
    assert_eq!(
        normalize_identifier(movement.collision_policy.as_str()),
        "STOP_AT_BLOCK",
        "self-directed movement ability '{ability_id}' must use STOP_AT_BLOCK collision_policy"
    );
    assert_eq!(
        normalize_identifier(movement.facing_policy.as_str()),
        "FACE_START",
        "self-directed movement ability '{ability_id}' must use FACE_START facing_policy"
    );
    assert_eq!(
        normalize_identifier(movement.block_behavior.as_str()),
        "UNBLOCKABLE",
        "self-directed movement ability '{ability_id}' lands nothing and must be UNBLOCKABLE"
    );
    assert_eq!(
        normalize_identifier(movement.parry_behavior.as_str()),
        "UNPARRYABLE",
        "self-directed movement ability '{ability_id}' lands nothing and must be UNPARRYABLE"
    );
}

fn validate_movement_delivery(ability_id: &str, movement: &MovementDeliveryDefinition) {
    let kind = normalize_identifier(movement.kind.as_str());
    assert!(
        kind == "DASH_TO_TARGET" || kind == "BACKSTEP",
        "movement ability '{ability_id}' has unsupported gameplay.delivery.kind '{}'",
        movement.kind
    );
    assert!(
        movement.cooldown_ms > 0,
        "movement ability '{ability_id}' must define positive cooldown_ms"
    );
    assert!(
        movement.uses_global_cooldown,
        "movement ability '{ability_id}' must currently use the global cooldown"
    );
    validate_authored_global_cooldown_ms(
        ability_id,
        Some(movement.uses_global_cooldown),
        movement.global_cooldown_ms,
    );

    if kind == "BACKSTEP" {
        validate_self_directed_movement_delivery(ability_id, movement);
        return;
    }
    assert!(
        movement.cast_time_ms > 0,
        "movement ability '{ability_id}' must define positive cast_time_ms"
    );
    assert_eq!(
        normalize_identifier(movement.cast_mobility.as_str()),
        "MOBILE",
        "movement ability '{ability_id}' must currently use MOBILE cast_mobility"
    );
    assert_eq!(
        normalize_identifier(movement.targeting.as_str()),
        "TARGET",
        "movement ability '{ability_id}' must currently use TARGET targeting"
    );
    assert!(
        movement.requires_target,
        "movement ability '{ability_id}' must currently require a target"
    );
    assert!(
        movement.resource_cost.is_finite() && movement.resource_cost >= 0.0,
        "movement ability '{ability_id}' must define a non-negative finite resource_cost"
    );
    assert!(
        movement.arms_auto_attack_on_cast,
        "movement ability '{ability_id}' must currently arm auto attack on cast"
    );
    assert!(
        movement.speed.is_finite() && movement.speed > 0.0,
        "movement ability '{ability_id}' must define positive finite speed"
    );
    assert!(
        movement.max_distance.is_finite() && movement.max_distance > 0.0,
        "movement ability '{ability_id}' must define positive finite max_distance"
    );

    assert!(
        movement.damage >= 0,
        "movement ability '{ability_id}' must define non-negative damage"
    );
    assert!(
        movement.radius.is_finite() && movement.radius > 0.0,
        "movement ability '{ability_id}' must define positive finite radius"
    );

    let block_behavior = normalize_identifier(movement.block_behavior.as_str());
    assert!(
        block_behavior == "BLOCKABLE" || block_behavior == "UNBLOCKABLE",
        "movement ability '{ability_id}' has unsupported block_behavior '{}'",
        movement.block_behavior
    );
    let parry_behavior = normalize_identifier(movement.parry_behavior.as_str());
    assert!(
        parry_behavior == "PARRYABLE" || parry_behavior == "UNPARRYABLE",
        "movement ability '{ability_id}' has unsupported parry_behavior '{}'",
        movement.parry_behavior
    );

    assert!(
        movement.arrival.buffer.is_finite() && movement.arrival.buffer >= 0.0,
        "movement ability '{ability_id}' must define non-negative finite arrival.buffer"
    );
    assert!(
        movement.arrival.epsilon.is_finite() && movement.arrival.epsilon >= 0.0,
        "movement ability '{ability_id}' must define non-negative finite arrival.epsilon"
    );
    assert!(
        !movement.impact_effects.is_empty(),
        "movement ability '{ability_id}' must define at least one impact effect"
    );
    for effect in &movement.impact_effects {
        match effect {
            MovementDeliveryImpactEffectDefinition::ApplyStatus { status } => {
                validate_status_application_definition(
                    ability_id,
                    "delivery.impact_effects[].status",
                    status.kind.as_str(),
                    status.duration_ms,
                    status.status_stack_group.as_deref(),
                    status.slow_pct,
                    status.tick_damage,
                    status.tick_heal,
                    status.tick_interval_ms,
                    status.modifier_scalar,
                    status.absorb_amount,
                    status.absorb_cap,
                    status.max_stacks,
                    movement_status_application_from_definition(
                        status,
                        authored_status_stack_group_default(status.kind.as_str()),
                    ),
                );
            }
        }
    }
}

#[cfg(test)]
fn action_presentation_key(definition: &ActionPresentationDefinition) -> String {
    action_presentation_key_for(
        definition.presentation_kind.as_str(),
        definition.presentation_id.as_str(),
    )
}

fn action_presentation_key_for(presentation_kind: &str, presentation_id: &str) -> String {
    format!(
        "{}:{}",
        normalize_identifier(presentation_kind),
        normalize_identifier(presentation_id)
    )
}

fn action_presentation_row_from_definition(
    definition: &ActionPresentationDefinition,
) -> ActionPresentationCatalog {
    let presentation_kind = normalize_identifier(definition.presentation_kind.as_str());
    let presentation_id = normalize_identifier(definition.presentation_id.as_str());
    ActionPresentationCatalog {
        key: action_presentation_key_for(presentation_kind.as_str(), presentation_id.as_str()),
        presentation_kind,
        presentation_id,
        display_name: definition.display_name.trim().to_string(),
        description: definition.description.trim().to_string(),
        sort_order: definition.sort_order,
    }
}

fn derived_spell_action_presentation_rows(
    catalog: &ProgressionCatalogFile,
) -> Vec<ActionPresentationCatalog> {
    let ability_presentations: HashMap<String, &ActionPresentationDefinition> = catalog
        .action_presentations
        .iter()
        .filter(|definition| {
            normalize_identifier(definition.presentation_kind.as_str()) == "ABILITY"
        })
        .map(|definition| {
            (
                normalize_identifier(definition.presentation_id.as_str()),
                definition,
            )
        })
        .collect();

    catalog
        .abilities
        .iter()
        .filter(|ability| ability_gameplay_kind(ability) == "SPELL")
        .map(|ability| {
            let ability_id = normalize_identifier(ability.ability_id.as_str());
            let spell_id = normalize_identifier(ability.action_id.as_str());
            let ability_presentation = ability_presentations.get(ability_id.as_str()).copied();
            let display_name = ability_presentation
                .map(|presentation| presentation.display_name.trim())
                .filter(|display_name| !display_name.is_empty())
                .unwrap_or_else(|| ability.display_name.trim())
                .to_string();
            let description = ability_presentation
                .map(|presentation| presentation.description.trim().to_string())
                .unwrap_or_default();
            let sort_order = ability_presentation
                .map(|presentation| presentation.sort_order)
                .unwrap_or(ability.sort_order);

            ActionPresentationCatalog {
                key: action_presentation_key_for("SPELL", spell_id.as_str()),
                presentation_kind: "SPELL".to_string(),
                presentation_id: spell_id,
                display_name,
                description,
                sort_order,
            }
        })
        .collect()
}

fn combat_vfx_cue_key(definition: &CombatVfxCueDefinition) -> String {
    format!(
        "{}:{}:{}:{}:{}:{}:{}:{}:{}:{}:{}:{}",
        normalize_identifier(definition.owner_kind.as_str()),
        normalize_identifier(definition.owner_id.as_str()),
        normalize_identifier(definition.trigger.as_str()),
        definition
            .hit_index
            .map(|hit_index| hit_index.to_string())
            .unwrap_or_else(|| "*".to_string()),
        definition
            .projectile_sequence_index
            .map(|projectile_sequence_index| projectile_sequence_index.to_string())
            .unwrap_or_else(|| "*".to_string()),
        normalize_identifier(definition.anchor.as_str()),
        normalize_identifier(definition.attach_mode.as_str()),
        normalize_identifier(definition.vfx_role.as_str()),
        normalize_identifier(definition.lifecycle.as_str()),
        normalize_identifier(definition.vfx_id.as_str()),
        definition.start_delay_ms,
        definition.sort_order
    )
}

fn normalize_identifier(value: &str) -> String {
    normalize_authored_action_id(value)
}

fn normalize_damage_type(value: &str) -> String {
    DamageType::from_wire(value).as_str().to_string()
}

fn normalize_optional_target_audience(value: &str) -> String {
    let normalized = normalize_identifier(value);
    if normalized.is_empty() {
        TARGET_AUDIENCE_HOSTILE.to_string()
    } else {
        normalized
    }
}

fn ability_gameplay_kind(ability: &AbilityDefinition) -> String {
    normalize_identifier(ability.gameplay.kind.as_str())
}

pub(crate) fn subtlety_disabled_target_damage_bonus() -> f32 {
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == SUBTLETY_OPPORTUNIST_ABILITY_ID
        })
        .map(|ability| ability.gameplay.disabled_target_damage_bonus.max(0.0))
        .unwrap_or(0.0)
}

pub(crate) fn subtlety_behind_target_damage_bonus() -> f32 {
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str())
                == SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID
        })
        .map(|ability| ability.gameplay.behind_target_damage_bonus.max(0.0))
        .unwrap_or(0.0)
}

pub(crate) fn precision_maverick_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<(f32, f32)> {
    if !player_has_selected_passive_ability(ctx, owner, ARCHER_MAVERICK_ABILITY_ID) {
        return None;
    }
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == ARCHER_MAVERICK_ABILITY_ID
        })
        .and_then(|ability| {
            let bonus = ability.gameplay.isolated_damage_bonus;
            let radius = ability.gameplay.isolated_ally_radius_meters;
            (bonus.is_finite() && bonus > 0.0 && radius.is_finite() && radius > 0.0)
                .then_some((bonus, radius))
        })
}

pub(crate) fn precision_point_blank_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<(f32, f32, f32)> {
    if !player_has_selected_passive_ability(ctx, owner, ARCHER_POINT_BLANK_ABILITY_ID) {
        return None;
    }
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == ARCHER_POINT_BLANK_ABILITY_ID
        })
        .and_then(|ability| {
            let bonus = ability.gameplay.point_blank_damage_bonus;
            let full_range = ability.gameplay.point_blank_full_bonus_range_meters;
            let zero_range = ability.gameplay.point_blank_zero_bonus_range_meters;
            (bonus.is_finite()
                && bonus > 0.0
                && full_range.is_finite()
                && full_range > 0.0
                && zero_range.is_finite()
                && zero_range > full_range)
                .then_some((bonus, full_range, zero_range))
        })
}

pub(crate) fn precision_careful_aim_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<(f32, u64, f32)> {
    if !player_has_selected_passive_ability(ctx, owner, ARCHER_CAREFUL_AIM_ABILITY_ID) {
        return None;
    }
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == ARCHER_CAREFUL_AIM_ABILITY_ID
        })
        .and_then(|ability| {
            let bonus = ability.gameplay.stationary_target_damage_bonus;
            let window_ms = ability.gameplay.stationary_target_window_ms;
            let max_displacement = ability.gameplay.stationary_target_max_displacement_meters;
            (bonus.is_finite()
                && bonus > 0.0
                && window_ms > 0
                && max_displacement.is_finite()
                && max_displacement > 0.0)
                .then_some((bonus, window_ms, max_displacement))
        })
}

pub(crate) fn precision_heartseeker_stationary_rule() -> Option<(u64, f32)> {
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == ARCHER_HEARTSEEKER_ABILITY_ID
        })
        .and_then(|ability| {
            let window_ms = ability.gameplay.stationary_target_window_ms;
            let max_displacement = ability.gameplay.stationary_target_max_displacement_meters;
            (ability.gameplay.stationary_target_auto_crit
                && window_ms > 0
                && max_displacement.is_finite()
                && max_displacement > 0.0)
                .then_some((window_ms, max_displacement))
        })
}

pub(crate) fn precision_perforation_for_owner(ctx: &ReducerContext, owner: Identity) -> bool {
    player_has_selected_passive_ability(ctx, owner, ARCHER_PERFORATION_ABILITY_ID)
        && progression_catalog().abilities.iter().any(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == ARCHER_PERFORATION_ABILITY_ID
                && ability.gameplay.projectile_piercing
        })
}

pub(crate) fn subtlety_dodge_recharge_time_reduction() -> f32 {
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == SUBTLETY_FLEET_FOOTED_ABILITY_ID
        })
        .map(|ability| ability.gameplay.dodge_recharge_time_reduction)
        .unwrap_or(0.0)
}

pub(crate) fn subtlety_movement_return_window() -> Duration {
    Duration::from_millis(
        progression_catalog()
            .abilities
            .iter()
            .find(|ability| {
                normalize_identifier(ability.ability_id.as_str())
                    == SUBTLETY_LINGERING_SHADE_ABILITY_ID
            })
            .and_then(|ability| ability.gameplay.movement_return.as_ref())
            .map(|definition| definition.window_ms)
            .unwrap_or(0),
    )
}

pub(crate) fn subtlety_surprise_attack_stun_duration() -> Duration {
    Duration::from_millis(
        progression_catalog()
            .abilities
            .iter()
            .find(|ability| {
                normalize_identifier(ability.ability_id.as_str())
                    == SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID
            })
            .map(|ability| ability.gameplay.stealth_attack_stun_ms)
            .unwrap_or(0),
    )
}

pub(crate) fn ruin_flaming_weapon_on_hit_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<MeleeFireOnHitRuntime> {
    if !player_has_selected_passive_ability(ctx, owner, RUIN_FLAMING_WEAPON_ABILITY_ID) {
        return None;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_FLAMING_WEAPON_ABILITY_ID
        })
        .and_then(|ability| ability.gameplay.melee_fire_on_hit.as_ref())
        .map(|definition| MeleeFireOnHitRuntime {
            bonus_damage: definition.bonus_damage,
            burn_duration: Duration::from_millis(definition.burn_duration_ms),
            burn_tick_interval: Duration::from_millis(definition.burn_tick_interval_ms),
            burn_tick_damage: definition.burn_tick_damage,
            burn_max_stacks: definition.burn_max_stacks,
            burn_status_stack_group: normalize_identifier(
                definition.burn_status_stack_group.as_str(),
            ),
            burn_dispel_types: definition.burn_dispel_types.clone(),
        })
}

pub(crate) fn blight_toxic_weapon_on_hit_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<MeleePoisonOnHitRuntime> {
    if !player_has_selected_passive_ability(ctx, owner, BLIGHT_TOXIC_WEAPON_ABILITY_ID) {
        return None;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == BLIGHT_TOXIC_WEAPON_ABILITY_ID
        })
        .and_then(|ability| ability.gameplay.melee_poison_on_hit.as_ref())
        .map(|definition| MeleePoisonOnHitRuntime {
            proc_chance: definition.proc_chance,
            poison_duration: Duration::from_millis(definition.poison_duration_ms),
            poison_tick_interval: Duration::from_millis(definition.poison_tick_interval_ms),
            poison_tick_damage: definition.poison_tick_damage,
            poison_max_stacks: definition.poison_max_stacks,
            poison_status_stack_group: normalize_identifier(
                definition.poison_status_stack_group.as_str(),
            ),
            poison_dispel_types: definition.poison_dispel_types.clone(),
        })
}

pub(crate) fn ruin_wildfire_ignite_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<FireSpellIgniteRuntime> {
    if !player_has_selected_passive_ability(ctx, owner, RUIN_WILDFIRE_ABILITY_ID) {
        return None;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_WILDFIRE_ABILITY_ID
        })
        .and_then(|ability| ability.gameplay.fire_spell_ignite.as_ref())
        .map(|definition| FireSpellIgniteRuntime {
            radius_meters: definition.radius_meters,
            burn_duration: Duration::from_millis(definition.burn_duration_ms),
            burn_tick_interval: Duration::from_millis(definition.burn_tick_interval_ms),
            burn_tick_damage: definition.burn_tick_damage,
            burn_max_stacks: definition.burn_max_stacks,
            burn_status_stack_group: normalize_identifier(
                definition.burn_status_stack_group.as_str(),
            ),
            burn_dispel_types: definition.burn_dispel_types.clone(),
        })
}

pub(crate) fn blight_fracture_melee_damage_bonus() -> f32 {
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_FRACTURE_ABILITY_ID
        })
        .map(|ability| {
            ability
                .gameplay
                .frozen_melee_first_hit_damage_bonus
                .max(0.0)
        })
        .unwrap_or(0.0)
}

pub(crate) fn ruin_furnace_mana_restore_ratio_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> f32 {
    if !player_has_selected_passive_ability(ctx, owner, RUIN_FURNACE_ABILITY_ID) {
        return 0.0;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_FURNACE_ABILITY_ID
        })
        .map(|ability| ability.gameplay.fire_damage_taken_mana_restore_ratio)
        .filter(|ratio| ratio.is_finite())
        .unwrap_or(0.0)
        .clamp(0.0, 1.0)
}

pub(crate) fn divinity_faith_mana_regen_bonus_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> f32 {
    if !player_has_selected_passive_ability(ctx, owner, DIVINITY_FAITH_ABILITY_ID) {
        return 0.0;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == DIVINITY_FAITH_ABILITY_ID
        })
        .map(|ability| ability.gameplay.mana_regen_bonus)
        .filter(|bonus| bonus.is_finite())
        .unwrap_or(0.0)
        .max(0.0)
}

pub(crate) fn primal_adaptation_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<(f32, Duration, u32)> {
    if !player_has_selected_passive_ability(ctx, owner, PRIMAL_ADAPTATION_ABILITY_ID) {
        return None;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == PRIMAL_ADAPTATION_ABILITY_ID
        })
        .and_then(|ability| {
            let resistance = ability.gameplay.adaptation_resistance_per_stack;
            let duration_ms = ability.gameplay.adaptation_duration_ms;
            let max_stacks = ability.gameplay.adaptation_max_stacks;
            (resistance.is_finite() && resistance > 0.0 && duration_ms > 0 && max_stacks > 0)
                .then_some((resistance, Duration::from_millis(duration_ms), max_stacks))
        })
}

pub(crate) fn primal_photosynthesis_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<(f32, Duration, Duration, u32)> {
    if !player_has_selected_passive_ability(ctx, owner, PRIMAL_PHOTOSYNTHESIS_ABILITY_ID) {
        return None;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == PRIMAL_PHOTOSYNTHESIS_ABILITY_ID
        })
        .and_then(|ability| {
            let mana_regen = ability.gameplay.stationary_mana_regen_per_stack;
            let first_delay_ms = ability.gameplay.stationary_first_stack_delay_ms;
            let interval_ms = ability.gameplay.stationary_stack_interval_ms;
            let max_stacks = ability.gameplay.stationary_max_stacks;
            (mana_regen.is_finite()
                && mana_regen > 0.0
                && first_delay_ms > 0
                && interval_ms > 0
                && max_stacks > 0)
                .then_some((
                    mana_regen,
                    Duration::from_millis(first_delay_ms),
                    Duration::from_millis(interval_ms),
                    max_stacks,
                ))
        })
}

pub(crate) fn primal_slipstream_cooldown_reduction_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Duration {
    if !player_has_selected_passive_ability(ctx, owner, PRIMAL_SLIPSTREAM_ABILITY_ID) {
        return Duration::ZERO;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == PRIMAL_SLIPSTREAM_ABILITY_ID
        })
        .map(|ability| Duration::from_millis(ability.gameplay.other_movement_cooldown_reduction_ms))
        .unwrap_or(Duration::ZERO)
}

pub(crate) fn ruin_acceleration_cooldown_reduction_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Duration {
    if !player_has_selected_passive_ability(ctx, owner, RUIN_ACCELERATION_ABILITY_ID) {
        return Duration::ZERO;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_ACCELERATION_ABILITY_ID
        })
        .map(|ability| {
            Duration::from_millis(ability.gameplay.critical_strike_cooldown_reduction_ms)
        })
        .unwrap_or(Duration::ZERO)
}

pub(crate) fn ruin_quickening_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<(f32, Duration)> {
    if !player_has_selected_passive_ability(ctx, owner, RUIN_QUICKENING_ABILITY_ID) {
        return None;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_QUICKENING_ABILITY_ID
        })
        .and_then(|ability| {
            let reduction = ability.gameplay.movement_spell_cast_time_reduction;
            let duration_ms = ability.gameplay.movement_spell_cast_time_buff_duration_ms;
            (reduction.is_finite() && reduction > 0.0 && reduction < 1.0 && duration_ms > 0)
                .then_some((reduction, Duration::from_millis(duration_ms)))
        })
}

pub(crate) fn ruin_chain_reaction_spell_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<String> {
    if !player_has_selected_passive_ability(ctx, owner, RUIN_CHAIN_REACTION_ABILITY_ID) {
        return None;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_CHAIN_REACTION_ABILITY_ID
        })
        .map(|ability| {
            normalize_identifier(ability.gameplay.critical_spell_proc_action_id.as_str())
        })
        .filter(|action_id| !action_id.is_empty())
}

pub(crate) fn restless_blades_auto_attack_proc_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<AutoAttackMeleeProcRuntime> {
    if !player_has_selected_passive_ability(ctx, owner, DAGGER_RESTLESS_BLADES_ABILITY_ID) {
        return None;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == DAGGER_RESTLESS_BLADES_ABILITY_ID
        })
        .and_then(|ability| {
            let action_id =
                normalize_identifier(ability.gameplay.auto_attack_proc_action_id.as_str());
            let combat_discipline_id = ability
                .combat_discipline_id
                .as_deref()
                .map(normalize_identifier)
                .unwrap_or_default();
            let proc_chance = ability.gameplay.auto_attack_proc_chance;
            (!action_id.is_empty()
                && !combat_discipline_id.is_empty()
                && proc_chance.is_finite()
                && proc_chance > 0.0)
                .then_some(AutoAttackMeleeProcRuntime {
                    action_id,
                    combat_discipline_id,
                    proc_chance: proc_chance.clamp(0.0, 1.0),
                })
        })
}

pub(crate) fn ruin_potential_crit_chance_per_stack_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> f32 {
    if !player_has_selected_passive_ability(ctx, owner, RUIN_POTENTIAL_ABILITY_ID) {
        return 0.0;
    }

    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_POTENTIAL_ABILITY_ID
        })
        .map(|ability| {
            ability
                .gameplay
                .noncritical_lightning_spell_crit_chance_bonus
        })
        .filter(|bonus| bonus.is_finite())
        .unwrap_or(0.0)
        .clamp(0.0, 1.0)
}

pub(crate) fn spell_ability_id_for_action_id(action_id: &str) -> Option<String> {
    let normalized_action_id = normalize_identifier(action_id);
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            ability_gameplay_kind(ability) == "SPELL"
                && normalize_identifier(ability.action_id.as_str()) == normalized_action_id
        })
        .map(|ability| normalize_identifier(ability.ability_id.as_str()))
}

pub(crate) fn ability_belongs_to_build_selection(ability_id: &str, selection_id: &str) -> bool {
    let ability_id = normalize_identifier(ability_id);
    let selection_id = normalize_identifier(selection_id);
    progression_catalog().abilities.iter().any(|ability| {
        normalize_identifier(ability.ability_id.as_str()) == ability_id
            && (ability
                .combat_discipline_id
                .as_deref()
                .map(normalize_identifier)
                .is_some_and(|value| value == selection_id)
                || ability
                    .spell_school_id
                    .as_deref()
                    .map(normalize_identifier)
                    .is_some_and(|value| value == selection_id))
    })
}

pub(crate) fn soulstealer_empowered_damage_bonus() -> f32 {
    progression_catalog()
        .abilities
        .iter()
        .find(|ability| normalize_identifier(ability.ability_id.as_str()) == SOULSTEALER_ABILITY_ID)
        .map(|ability| ability.gameplay.soulstealer_empowered_damage_bonus)
        .filter(|bonus| bonus.is_finite())
        .unwrap_or(0.0)
        .clamp(0.0, 1.0)
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct BladeTwistingBleedRuntime {
    pub damage_ratio: f32,
    pub duration: Duration,
    pub tick_interval: Duration,
}

pub(crate) fn blade_twisting_bleed_runtime() -> Option<BladeTwistingBleedRuntime> {
    let gameplay = &progression_catalog()
        .abilities
        .iter()
        .find(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == DAGGER_BLADE_TWISTING_ABILITY_ID
        })?
        .gameplay;
    (gameplay.blade_twisting_bleed_damage_ratio > 0.0
        && gameplay.blade_twisting_bleed_duration_ms > 0
        && gameplay.blade_twisting_bleed_tick_interval_ms > 0)
        .then(|| BladeTwistingBleedRuntime {
            damage_ratio: gameplay.blade_twisting_bleed_damage_ratio.clamp(0.0, 1.0),
            duration: Duration::from_millis(gameplay.blade_twisting_bleed_duration_ms),
            tick_interval: Duration::from_millis(gameplay.blade_twisting_bleed_tick_interval_ms),
        })
}

fn canonical_action_bar_slot_id(value: &str) -> String {
    match normalize_identifier(value).as_str() {
        "BOTTOM_01" => "SLOT_0_0".to_string(),
        "BOTTOM_02" => "SLOT_0_1".to_string(),
        "BOTTOM_03" => "SLOT_0_2".to_string(),
        "BOTTOM_04" => "SLOT_0_3".to_string(),
        "BOTTOM_05" => "SLOT_0_4".to_string(),
        "BOTTOM_06" => "SLOT_0_5".to_string(),
        "BOTTOM_07" => "SLOT_0_6".to_string(),
        "BOTTOM_08" => "SLOT_0_7".to_string(),
        "BOTTOM_09" => "SLOT_0_8".to_string(),
        other => other.to_string(),
    }
}

fn encode_tags(tags: &[String]) -> String {
    let mut normalized: Vec<_> = tags.iter().map(|tag| normalize_identifier(tag)).collect();
    normalized.sort();
    normalized.dedup();
    normalized.join(",")
}

#[cfg(test)]
mod tests {
    use spacetimedb::Timestamp;
    use std::collections::{HashMap, HashSet};
    use std::time::Duration;

    use crate::animation_set_test_utils::{
        animation_set_assets_by_combat_profile, parse_top_level_animation_set_field,
    };
    use crate::combat::{
        DamageType, StackPolicy, StatusApplication, StatusDispelType, StatusEffectKind,
        StatusPayload, StatusPolarity, StatusStackGroupDefault, DEFAULT_HIT_RADIUS,
    };
    use crate::melee::{auto_attack_reference_for_profile, profile_supports_action_reference};
    use crate::progression::melee_timed_movement_for_ability_id;
    use crate::relations::TargetAudience;
    use crate::resources::RESOURCE_KIND_MANA;
    use crate::spells::{spell_definition_by_str, SpellBehavior, SpellTargeting};

    use super::{
        ability_gameplay_kind, action_id_is_movement_ability, action_presentation_key,
        authored_status_presentation_ids, authorize_v2_active_selection,
        canonical_action_bar_slot_id, combat_rule_value, combat_vfx_cue_key,
        derived_spell_action_presentation_rows, encode_tags, melee_channel_for_ability_id,
        melee_evasive_leap_for_ability_id, melee_impact_effects_for_ability_id,
        normalize_identifier, normalize_optional_target_audience,
        primary_resource_gain_on_action_accept, progression_catalog,
        projectile_body_vfx_id_for_spell, resolved_combat_discipline_id_for_ability_definition,
        resolved_melee_targeting_for_catalog, shroud_has_expired, validate_auto_attack_catalog,
        validate_combat_mode_catalog, validate_progression_catalog_authoring_contract,
        AbilityDefinition, CombatVfxPresentationManifest, ConsumeTargetStatusFrequency,
        ConsumeTargetStatusRule, ConsumeTargetStatusSourceScope, ConsumeTargetStatusStackMode,
        FrozenActiveAuthorizationDenial, MeleeChannelRuntime, MeleeImpactEffectRuntime,
        ABILITY_KIND_COMBAT_MODE_TOGGLE, ARCHER_DRAW_MODE_TOGGLE_ABILITY_ID,
        AUTO_ATTACK_MOVEMENT_ALLOW_MOVING, AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE,
        BLIGHT_TOXIC_WEAPON_ABILITY_ID, COMBAT_MODE_FULL_DRAW, COMBAT_MODE_READY,
        COMBAT_MODE_SHORT_DRAW, COMBAT_MODE_STEALTHED, COMBAT_PROFILE_ARCHER_BOW,
        COMBAT_PROFILE_DAGGERS, COMBAT_PROFILE_STAFF, COMBAT_PROFILE_SWORD_AND_SHIELD,
        COMBAT_PROFILE_TWO_HANDED_SWORD, DAGGER_BLADE_TWISTING_ABILITY_ID,
        DAGGER_SHROUD_ABILITY_ID, PLAYER_PASSIVE_RUNTIME_INVENTORY, PRIMAL_ADAPTATION_ABILITY_ID,
        PRIMAL_PHOTOSYNTHESIS_ABILITY_ID, PRIMAL_SLIPSTREAM_ABILITY_ID, RESOURCE_KIND_STAMINA,
        SUBTLETY_FLEET_FOOTED_ABILITY_ID, SUBTLETY_LINGERING_SHADE_ABILITY_ID,
        SUBTLETY_OPPORTUNIST_ABILITY_ID, SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID,
        SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID,
    };
    use crate::action_ids::{AuthoredActionId, RuntimeActionId};

    const GAP_CLOSE_TARGET_ARRIVAL_DISTANCE_METERS: f32 = 2.0;
    const SPELL_CAST_ANIMATION_MAP_ASSET: &str =
        include_str!("../../Assets/Arena/Resources/SpellCastAnimationMap.asset");

    #[test]
    fn frozen_active_authorization_keeps_spells_global_and_techniques_weapon_gated() {
        assert_eq!(authorize_v2_active_selection(true, false, false), Ok(()));
        assert_eq!(authorize_v2_active_selection(false, true, true), Ok(()));
        assert_eq!(
            authorize_v2_active_selection(false, true, false),
            Err(FrozenActiveAuthorizationDenial::WrongWeapon)
        );
        assert_eq!(
            authorize_v2_active_selection(false, false, false),
            Err(FrozenActiveAuthorizationDenial::UnselectedFeature)
        );
        assert_eq!(
            FrozenActiveAuthorizationDenial::NoFrozenBuild.as_str(),
            "NO_FROZEN_BUILD"
        );
        assert_eq!(
            FrozenActiveAuthorizationDenial::NoActiveDiscipline.as_str(),
            "NO_ACTIVE_DISCIPLINE"
        );
    }

    #[test]
    fn consumable_target_status_rule_supports_one_or_all_vulnerable_stacks() {
        let rule: ConsumeTargetStatusRule = serde_json::from_str(
            r#"{
                "status_kind": "VULNERABLE",
                "status_stack_group": "VULNERABLE:{SOURCE}",
                "stack_mode": "ONE",
                "source_scope": "APPLIER_TEAM",
                "frequency": "ONCE_PER_ACTION_PER_TARGET",
                "damage_bonus_per_stack": 0.5
            }"#,
        )
        .expect("the canonical consumable status rule should deserialize");

        assert_eq!(rule.status_kind, StatusEffectKind::Vulnerable);
        assert_eq!(
            rule.source_scope,
            ConsumeTargetStatusSourceScope::ApplierTeam
        );
        assert_eq!(
            rule.frequency,
            ConsumeTargetStatusFrequency::OncePerActionPerTarget
        );
        assert_eq!(rule.stack_mode, ConsumeTargetStatusStackMode::One);
        assert_eq!(rule.stack_mode.maximum_stacks(), Some(1));
        assert!((rule.damage_bonus_per_stack - 0.5).abs() < f32::EPSILON);
        assert_eq!(rule.validate("TEST", "MELEE"), Ok(()));
        assert_eq!(rule.validate("TEST", "SPELL"), Ok(()));
        assert!(rule.validate("TEST", "PASSIVE").is_err());

        let mut invalid = rule.clone();
        invalid.status_kind = StatusEffectKind::FindWeakness;
        assert!(invalid.validate("TEST", "MELEE").is_err());

        let mut invalid = rule.clone();
        invalid.status_stack_group.clear();
        assert!(invalid.validate("TEST", "MELEE").is_err());

        let mut invalid = rule;
        invalid.damage_bonus_per_stack = 0.0;
        assert!(invalid.validate("TEST", "MELEE").is_err());
    }

    #[test]
    fn heartseeker_vulnerability_techniques_have_the_authored_contract() {
        let catalog = progression_catalog();

        let consumers: Vec<_> = catalog
            .abilities
            .iter()
            .filter(|ability| ability.gameplay.consume_target_status.is_some())
            .map(|ability| normalize_identifier(ability.ability_id.as_str()))
            .collect();
        assert_eq!(consumers, vec!["DAGGER_VITAL_STRIKE"]);

        let vital_strike = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_VITAL_STRIKE")
            .expect("Vital Strike should be authored");
        let consume = vital_strike
            .gameplay
            .consume_target_status
            .as_ref()
            .expect("Vital Strike should consume Vulnerable");
        assert_eq!(consume.status_kind, StatusEffectKind::Vulnerable);
        assert_eq!(consume.status_stack_group, "VULNERABLE");
        assert_eq!(consume.stack_mode, ConsumeTargetStatusStackMode::One);
        assert_eq!(consume.stack_mode.maximum_stacks(), Some(1));
        assert_eq!(
            consume.source_scope,
            ConsumeTargetStatusSourceScope::ApplierTeam
        );
        assert_eq!(
            consume.frequency,
            ConsumeTargetStatusFrequency::OncePerActionPerTarget
        );
        assert!((consume.damage_bonus_per_stack - 0.5).abs() < f32::EPSILON);

        let vulnerable_producers: Vec<_> = catalog
            .abilities
            .iter()
            .filter(|ability| {
                ability.gameplay.delivery.as_ref().is_some_and(|delivery| {
                    delivery
                        .pointer("/status/kind")
                        .and_then(serde_json::Value::as_str)
                        .is_some_and(|kind| normalize_identifier(kind) == "VULNERABLE")
                        || delivery
                            .get("additional_applications")
                            .and_then(serde_json::Value::as_array)
                            .is_some_and(|applications| {
                                applications.iter().any(|application| {
                                    application
                                        .pointer("/status/kind")
                                        .and_then(serde_json::Value::as_str)
                                        .is_some_and(|kind| {
                                            normalize_identifier(kind) == "VULNERABLE"
                                        })
                                })
                            })
                }) || ability.gameplay.melee_impact_effects.iter().any(|effect| {
                    matches!(
                        effect,
                        super::MeleeImpactEffectDefinition::ApplyStatus { status }
                            | super::MeleeImpactEffectDefinition::ApplyStatusOnHit { status, .. }
                            if normalize_identifier(status.kind.as_str()) == "VULNERABLE"
                    )
                })
            })
            .map(|ability| normalize_identifier(ability.ability_id.as_str()))
            .collect();
        assert_eq!(
            vulnerable_producers,
            vec![
                "DAGGER_PRECISION_STRIKE",
                "DAGGER_EXPOSE_WEAKNESS",
                "DAGGER_GOUGE"
            ]
        );

        let precision_strike = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_PRECISION_STRIKE")
            .expect("Precision Strike should be authored");
        assert_eq!(
            melee_impact_effects_for_ability_id("DAGGER_PRECISION_STRIKE"),
            vec![MeleeImpactEffectRuntime::ApplyStatusOnHit {
                hit_index: 0,
                status: StatusApplication::new(
                    StatusPayload::Vulnerable,
                    Duration::from_secs(86_400),
                    Some("VULNERABLE".to_string()),
                    StatusStackGroupDefault::EffectKind,
                    3,
                    StackPolicy::AddStackRefresh,
                )
                .with_dispel_types(vec![StatusDispelType::Physical]),
            }]
        );
        assert_eq!(precision_strike.gameplay.melee_impact_effects.len(), 1);

        let cruelty = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_CRUELTY")
            .expect("Cruelty should be authored");
        assert_eq!(cruelty.action_id, "CRUELTY");
        assert_eq!(cruelty.gameplay.cooldown_ms, Some(60_000));

        let expose_weakness = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_EXPOSE_WEAKNESS")
            .expect("Expose Weakness should be authored");
        assert_eq!(expose_weakness.action_id, "EXPOSE_WEAKNESS");
        assert_eq!(expose_weakness.gameplay.cooldown_ms, Some(120_000));

        assert!(authored_status_presentation_ids(catalog).contains("VULNERABLE"));
        assert!(authored_status_presentation_ids(catalog).contains("CRUELTY"));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            presentation.presentation_kind == "STATUS"
                && presentation.presentation_id == "VULNERABLE"
                && presentation.display_name == "Vulnerable"
        }));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            presentation.presentation_kind == "ABILITY"
                && presentation.presentation_id == "DAGGER_VITAL_STRIKE"
                && presentation.description.contains("50%")
        }));
        for (ability_id, cooldown_description) in [
            ("DAGGER_CRUELTY", "60 second cooldown"),
            ("DAGGER_EXPOSE_WEAKNESS", "120 second cooldown"),
        ] {
            assert!(catalog.action_presentations.iter().any(|presentation| {
                presentation.presentation_kind == "ABILITY"
                    && presentation.presentation_id == ability_id
                    && presentation.description.contains(cooldown_description)
            }));
        }
        assert!(SPELL_CAST_ANIMATION_MAP_ASSET.contains("- spellId: CRUELTY"));
        assert!(SPELL_CAST_ANIMATION_MAP_ASSET.contains("- spellId: EXPOSE_WEAKNESS"));
    }

    #[test]
    fn every_authored_player_passive_is_in_the_selected_passive_runtime_inventory() {
        let authored: HashSet<_> = progression_catalog()
            .abilities
            .iter()
            .filter(|ability| normalize_identifier(ability.actor_scope.as_str()) != "NPC")
            .filter(|ability| normalize_identifier(ability.selection_kind.as_str()) == "PASSIVE")
            .map(|ability| normalize_identifier(ability.ability_id.as_str()))
            .collect();
        let inventoried: HashSet<_> = PLAYER_PASSIVE_RUNTIME_INVENTORY
            .iter()
            .map(|ability_id| normalize_identifier(ability_id))
            .collect();

        assert_eq!(inventoried.len(), PLAYER_PASSIVE_RUNTIME_INVENTORY.len());
        assert_eq!(authored, inventoried);
        assert_eq!(authored.len(), 27);
    }

    #[test]
    fn restless_blades_authors_a_ten_percent_quick_cut_proc() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| {
                normalize_identifier(ability.ability_id.as_str())
                    == super::DAGGER_RESTLESS_BLADES_ABILITY_ID
            })
            .expect("Restless Blades must be authored");

        assert_eq!(
            ability.gameplay.auto_attack_proc_action_id,
            "DAGGER_QUICK_CUT"
        );
        assert!((ability.gameplay.auto_attack_proc_chance - 0.10).abs() < 0.0001);
    }

    #[test]
    fn knockback_speed_rule_matches_authored_tuning() {
        assert!((combat_rule_value("KNOCKBACK_SPEED_METERS_PER_SEC") - 24.0).abs() < f32::EPSILON);
    }

    fn parse_spell_ids_from_cast_animation_map_asset(asset_contents: &str) -> HashSet<String> {
        asset_contents
            .lines()
            .filter_map(|line| line.trim_start().strip_prefix("- spellId: "))
            .map(|value| normalize_identifier(value.trim()))
            .collect()
    }

    fn spell_cast_animation_mapping(asset_contents: &str, spell_id: &str) -> (u8, u8, String) {
        let expected_header = format!("- spellId: {}", normalize_identifier(spell_id));
        let mut entry_lines = asset_contents
            .lines()
            .skip_while(|line| line.trim() != expected_header);
        assert_eq!(
            entry_lines.next().map(str::trim),
            Some(expected_header.as_str()),
            "spell '{spell_id}' must exist in the cast animation map"
        );

        let mut assignment_kind = None;
        let mut motion = None;
        let mut animation_id = String::new();
        for line in entry_lines {
            let trimmed = line.trim();
            if trimmed.starts_with("- spellId: ") {
                break;
            }
            if let Some(value) = trimmed.strip_prefix("assignmentKind: ") {
                assignment_kind = value.parse::<u8>().ok();
            } else if let Some(value) = trimmed.strip_prefix("motion: ") {
                motion = value.parse::<u8>().ok();
            } else if let Some(value) = trimmed.strip_prefix("animationId: ") {
                animation_id = normalize_identifier(value);
            }
        }

        (
            assignment_kind
                .unwrap_or_else(|| panic!("spell '{spell_id}' must author assignmentKind")),
            motion.unwrap_or_else(|| panic!("spell '{spell_id}' must author motion")),
            animation_id,
        )
    }

    fn json_ability_gameplay_kind(object: &serde_json::Map<String, serde_json::Value>) -> &str {
        object
            .get("gameplay")
            .and_then(serde_json::Value::as_object)
            .and_then(|gameplay| gameplay.get("kind"))
            .and_then(serde_json::Value::as_str)
            .expect("gameplay.kind is required")
    }

    fn ability_delivery_kind(ability: &AbilityDefinition) -> String {
        ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .and_then(|delivery| delivery.get("kind"))
            .and_then(serde_json::Value::as_str)
            .map(normalize_identifier)
            .unwrap_or_default()
    }

    fn projectile_delivery_projectile_count(ability: &AbilityDefinition) -> u32 {
        let delivery_kind = ability_delivery_kind(ability);
        if delivery_kind == "CHANNEL" {
            return if ability
                .gameplay
                .delivery
                .as_ref()
                .and_then(serde_json::Value::as_object)
                .and_then(|delivery| delivery.get("projectile"))
                .is_some()
            {
                1
            } else {
                0
            };
        }
        if delivery_kind != "PROJECTILE" {
            return 0;
        }
        ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .and_then(|delivery| delivery.get("motion"))
            .and_then(serde_json::Value::as_object)
            .and_then(|motion| {
                let kind = motion
                    .get("kind")
                    .and_then(serde_json::Value::as_str)
                    .map(normalize_identifier)
                    .unwrap_or_else(|| "LINEAR".to_string());
                if kind == "ORBIT_CASTER" {
                    motion
                        .get("projectile_count")
                        .and_then(serde_json::Value::as_u64)
                        .map(|count| count as u32)
                } else {
                    None
                }
            })
            .unwrap_or(1)
    }

    fn ability_uses_projectile_body(ability: &AbilityDefinition) -> bool {
        projectile_delivery_projectile_count(ability) > 0
    }

    fn ability_uses_traveling_area_motion(ability: &AbilityDefinition) -> bool {
        ability_delivery_kind(ability) == "PROJECTILE"
            && ability
                .gameplay
                .delivery
                .as_ref()
                .and_then(serde_json::Value::as_object)
                .and_then(|delivery| delivery.get("motion"))
                .and_then(serde_json::Value::as_object)
                .and_then(|motion| motion.get("kind"))
                .and_then(serde_json::Value::as_str)
                .is_some_and(|kind| normalize_identifier(kind) == "TRAVELING_AREA")
    }

    fn animation_set_asset_for_combat_profile(combat_discipline_id: &str) -> &'static str {
        let normalized = normalize_identifier(combat_discipline_id);
        animation_set_assets_by_combat_profile()
            .get(normalized.as_str())
            .unwrap_or_else(|| {
                panic!(
                    "no CombatAnimationSet asset authors combatProfileId '{}'",
                    normalized
                )
            })
            .as_str()
    }

    #[test]
    fn animation_sets_use_semantic_spell_motion_bindings_without_legacy_spell_rows() {
        for (profile_id, asset_contents) in animation_set_assets_by_combat_profile() {
            assert!(
                asset_contents.contains("  spellCastMotionBindings:"),
                "{profile_id} must author semantic spell cast motion bindings"
            );
            assert!(
                !asset_contents.contains("  spells:"),
                "{profile_id} must not retain the legacy per-spell animation array"
            );
            assert!(
                !asset_contents.contains("    familyBaseName: MagicAttackDirect2H01"),
                "{profile_id} must never use the rejected MagicAttackDirect2H01 family"
            );
            for family in [
                "MagicAttackDirect1H01",
                "MagicAttackCall1H01",
                "MagicAttackCall1H02",
                "MagicAttackGround01",
                "MagicAttackOmni01",
                "SpecialMagicAttack01",
            ] {
                assert!(
                    asset_contents.contains(&format!("    familyBaseName: {family}")),
                    "{profile_id} is missing required semantic family binding '{family}'"
                );
            }

            let direct_2h_binding = "  - motion: 6\n    familyBaseName: MagicAttackDirect2H02";
            if profile_id == COMBAT_PROFILE_DAGGERS || profile_id == COMBAT_PROFILE_STAFF {
                assert!(
                    asset_contents.contains(direct_2h_binding),
                    "{profile_id} must bind Direct2H to MagicAttackDirect2H02"
                );
            } else {
                assert!(
                    !asset_contents.contains(direct_2h_binding),
                    "{profile_id} must fall Direct2H back to its assigned Direct1H hand"
                );
            }
        }

        let sword_and_shield = animation_set_asset_for_combat_profile("SWORD_AND_SHIELD");
        assert_eq!(
            parse_top_level_animation_set_field(sword_and_shield, "oneHandedCastHand").as_deref(),
            Some("1"),
            "SwordAndShield must cast one-hand families with the right hand"
        );
        let archer_bow = animation_set_asset_for_combat_profile("ARCHER_BOW");
        assert_eq!(
            parse_top_level_animation_set_field(archer_bow, "oneHandedCastHand").as_deref(),
            Some("0"),
            "ArcherBow/Precision must cast one-hand families with the left hand"
        );

        let upheaval = spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, "UPHEAVAL");
        assert_eq!(
            (upheaval.0, upheaval.1),
            (0, 2),
            "Upheaval must be classified as Raise"
        );
        let battle_cry = spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, "BATTLE_CRY");
        assert_eq!(
            (battle_cry.0, battle_cry.1),
            (1, 0),
            "Battle Cry must be a fixed set-independent animation exception"
        );
    }

    #[test]
    fn spell_cast_animation_map_matches_requested_semantic_classifications() {
        let assert_mapping = |spell_id: &str, assignment_kind: u8, motion: u8| {
            let mapping = spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, spell_id);
            assert_eq!(
                (mapping.0, mapping.1),
                (assignment_kind, motion),
                "spell '{spell_id}' must author assignmentKind {assignment_kind}, motion {motion}"
            );
        };

        for spell_id in [
            "MIRROR_IMAGE",
            "RECALL",
            "MANA_SHIELD",
            "SHIMMER",
            "TRANSPOSE",
            "BLOOD_OFFERING",
            "RIME",
            "IMMOLATION",
            "TELEPORT",
            "GLACIAL_ADVANCE",
            "AURA_OF_RENEWAL",
            "MIASMA",
            "REAP",
            "COMBUSTION",
            "CONTAGION",
            "MOULT",
            "STONE_CARAPACE",
        ] {
            assert_mapping(spell_id, 2, 0);
        }

        for spell_id in ["FIREBALL", "SMITE"] {
            assert_mapping(spell_id, 0, 6);
        }

        assert_eq!(
            spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, "ICICLE"),
            (3, 0, "MAGE_AIMED_RELEASE_01".to_string()),
            "Icicle must select the shared Mage Aimed Release recipe"
        );

        assert_eq!(
            spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, "FLAMING_ORB"),
            (3, 0, "MAGE_PROJECTILE_CAST_02".to_string()),
            "Flaming Orb must select the shared Mage Projectile Cast 2 catalog recipe"
        );

        for spell_id in [
            "PLAGUEBOLT",
            "EARTH_BLAST",
            "LAVA_BLAST",
            "TIDAL_BLAST",
            "WIND_BLAST",
            "CAUTERIZE",
            "FLASHFIRE",
            "FLASH_FREEZE",
            "DEEPENING_COLD",
            "FULMINATION",
            "VAMPIRIC_ORB",
        ] {
            assert_mapping(spell_id, 0, 1);
        }

        for (spell_id, animation_id) in [
            ("BOLT", "MAGE_COMBO_CAST_04_01"),
            ("CAPACITOR", "MAGE_COMBO_CAST_01_02"),
            ("BUFFET", "MAGE_COMBO_CAST_01_01"),
            ("WITHERING_ORB", "MAGE_COMBO_CAST_01_01"),
            ("CLOUDBURST", "MAGE_SKILL_CAST_05"),
        ] {
            assert_eq!(
                spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, spell_id),
                (3, 0, animation_id.to_string()),
                "spell '{spell_id}' must retain its shared Mage catalog recipe"
            );
        }

        for spell_id in [
            "GIGANTISM",
            "FLURRY",
            "OVERGROWTH",
            "WELLSPRING",
            "NECRO_PRISON",
            "NECROTIC_AURA",
            "BENEDICTION",
            "FLASH_OF_GRACE",
            "RESTORATION",
            "SANCTUARY",
            "VERDANT_SPIRITS",
            "TAILWIND",
        ] {
            assert_mapping(spell_id, 0, 2);
        }

        for spell_id in ["GRAVEBURST", "GRAVEWAKE", "DEFILED_GROUND"] {
            assert_eq!(
                spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, spell_id),
                (3, 0, "MAGE_SKILL_CAST_03".to_string()),
                "spell '{spell_id}' must retain its shared Mage Skill Cast 3 recipe"
            );
        }

        assert_eq!(
            spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, "DIVINE_MEND"),
            (3, 0, "LEGACY_CALL_CAST_1H_01_L_CHARGED".to_string()),
            "Divine Mend must retain its shared charged call-cast recipe"
        );

        for spell_id in ["EARTHQUAKE", "FISSURE"] {
            assert_eq!(
                spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, spell_id),
                (3, 0, "MAGE_SKILL_CAST_04".to_string()),
                "spell '{spell_id}' must retain its shared Mage Skill Cast 4 recipe"
            );
        }
        assert_mapping("BLIZZARD", 0, 7);
    }

    #[test]
    fn player_spell_cast_animation_classification_inventory_is_explicit() {
        let mapped_spell_ids =
            parse_spell_ids_from_cast_animation_map_asset(SPELL_CAST_ANIMATION_MAP_ASSET);
        let actual_unclassified: HashSet<String> = progression_catalog()
            .abilities
            .iter()
            .filter(|ability| {
                ability_gameplay_kind(ability) == "SPELL"
                    && normalize_identifier(ability.actor_scope.as_str()) == "PLAYER"
            })
            .map(|ability| normalize_identifier(ability.action_id.as_str()))
            .filter(|spell_id| !mapped_spell_ids.contains(spell_id))
            .collect();
        assert_eq!(
            actual_unclassified,
            HashSet::new(),
            "every player spell must have a motion, fixed, or explicit no-animation classification"
        );
    }

    fn spell_ids_for_combat_profile(combat_discipline_id: &str) -> HashSet<String> {
        let animation_set = animation_set_asset_for_combat_profile(combat_discipline_id);
        assert!(
            animation_set.contains("  spellCastMotionBindings:"),
            "combat profile '{}' must author semantic spell cast motion bindings",
            combat_discipline_id
        );
        parse_spell_ids_from_cast_animation_map_asset(SPELL_CAST_ANIMATION_MAP_ASSET)
    }

    fn parse_current_animation_set_melee_fields(
        asset_contents: &str,
        field_prefix: &str,
    ) -> HashSet<String> {
        let mut values = HashSet::new();
        let mut in_melee_attacks = false;

        for line in asset_contents.lines() {
            if line == "  meleeAttacks:" {
                in_melee_attacks = true;
                continue;
            }
            if !in_melee_attacks {
                continue;
            }
            if in_melee_attacks && line.trim_start().starts_with("autoAttackAuthoredStrikeId:") {
                in_melee_attacks = false;
                continue;
            }
            if let Some(value) = line.trim_start().strip_prefix(field_prefix) {
                let normalized = normalize_identifier(value.trim());
                if !normalized.is_empty() {
                    values.insert(normalized);
                }
            }
        }

        values
    }

    fn parse_authored_strike_ids_from_animation_set_asset(asset_contents: &str) -> HashSet<String> {
        parse_current_animation_set_melee_fields(asset_contents, "id: ")
    }

    fn authored_strike_ids_for_combat_profile(combat_discipline_id: &str) -> HashSet<String> {
        parse_authored_strike_ids_from_animation_set_asset(animation_set_asset_for_combat_profile(
            combat_discipline_id,
        ))
    }

    fn parse_authored_strike_hit_window_counts_from_animation_set_asset(
        asset_contents: &str,
    ) -> HashMap<String, usize> {
        let mut counts = HashMap::new();
        let mut in_melee_attacks = false;
        let mut current_id: Option<String> = None;
        let mut in_hit_windows = false;
        let mut hit_window_count = 0usize;

        for line in asset_contents.lines() {
            if line == "  meleeAttacks:" {
                in_melee_attacks = true;
                continue;
            }
            if !in_melee_attacks {
                continue;
            }
            let trimmed = line.trim_start();
            if trimmed.starts_with("autoAttackAuthoredStrikeId:") {
                if let Some(id) = current_id.take() {
                    counts.insert(id, hit_window_count);
                }
                break;
            }

            if let Some(value) = trimmed.strip_prefix("id: ") {
                if let Some(id) = current_id.take() {
                    counts.insert(id, hit_window_count);
                }
                current_id = Some(normalize_identifier(value.trim()));
                in_hit_windows = false;
                hit_window_count = 0;
                continue;
            }

            if current_id.is_none() {
                continue;
            }

            if trimmed == "hitWindows:" {
                in_hit_windows = true;
                continue;
            }
            if in_hit_windows && trimmed.starts_with("- timeNormalized:") {
                hit_window_count += 1;
                continue;
            }
            if trimmed.starts_with("recoveryMs:") {
                in_hit_windows = false;
            }
        }

        if let Some(id) = current_id.take() {
            counts.insert(id, hit_window_count);
        }

        counts
    }

    fn authored_strike_hit_window_counts_for_combat_profile(
        combat_discipline_id: &str,
    ) -> HashMap<String, usize> {
        parse_authored_strike_hit_window_counts_from_animation_set_asset(
            animation_set_asset_for_combat_profile(combat_discipline_id),
        )
    }

    fn parse_runtime_slot_ids_from_animation_set_asset(asset_contents: &str) -> HashSet<String> {
        parse_current_animation_set_melee_fields(asset_contents, "slotId: ")
    }

    fn runtime_slot_ids_for_combat_profile(combat_discipline_id: &str) -> HashSet<String> {
        parse_runtime_slot_ids_from_animation_set_asset(animation_set_asset_for_combat_profile(
            combat_discipline_id,
        ))
    }

    #[derive(Clone, Debug, PartialEq, Eq)]
    enum ResolvedAuthoringCategory {
        Melee,
        Spell,
        Movement,
        AutoAttackReplacement,
        CombatModeToggle,
        Passive,
        Unknown(String),
    }

    #[derive(Clone, Debug)]
    struct ResolvedCombatAuthoringAction {
        ability_id: String,
        actor_scope: String,
        category: ResolvedAuthoringCategory,
        combat_discipline_id: String,
        authored_action_id: String,
        action_bar_default_slots: Vec<String>,
        has_action_bar_action_tag: bool,
        has_ability_presentation: bool,
        melee_matches_authored_strike: bool,
        melee_matches_runtime_slot: bool,
        spell_has_definition: bool,
        spell_has_animation: bool,
        auto_attack_replacement_has_definition: bool,
        auto_attack_replacement_profile_matches: bool,
        auto_attack_replacement_matches_authored_strike: bool,
        auto_attack_replacement_matches_runtime_slot: bool,
    }

    #[derive(Clone, Copy, Debug, PartialEq, Eq)]
    enum CombatAuthoringRule {
        AbilityProfileResolves,
        MeleeActionIdMatchesAuthoredStrike,
        MeleeActionIdNotRuntimeSlot,
        SpellActionIdResolvesToSpell,
        SelectableSpellHasAnimationEntry,
        AutoAttackReplacementResolves,
        AutoAttackReplacementStrikeMatchesAuthoredStrike,
        CombatProfileActionBarDefaultResolves,
        PlayerFacingActionHasPresentation,
        PresentationTargetResolves,
        SpellPresentationNotAuthored,
        CombatVfxCueResolves,
        MeleeImpactAreaValid,
        AbilityKindSupported,
    }

    impl CombatAuthoringRule {
        fn code(self) -> &'static str {
            match self {
                Self::AbilityProfileResolves => "ability-profile-resolves",
                Self::MeleeActionIdMatchesAuthoredStrike => {
                    "melee-action-id-matches-authored-strike"
                }
                Self::MeleeActionIdNotRuntimeSlot => "melee-action-id-not-runtime-slot",
                Self::SpellActionIdResolvesToSpell => "spell-action-id-resolves-to-spell",
                Self::SelectableSpellHasAnimationEntry => "selectable-spell-has-animation-entry",
                Self::AutoAttackReplacementResolves => "auto-attack-replacement-resolves",
                Self::AutoAttackReplacementStrikeMatchesAuthoredStrike => {
                    "auto-attack-replacement-strike-matches-authored-strike"
                }
                Self::CombatProfileActionBarDefaultResolves => {
                    "combat-profile-action-bar-default-resolves"
                }
                Self::PlayerFacingActionHasPresentation => "player-facing-action-has-presentation",
                Self::PresentationTargetResolves => "presentation-target-resolves",
                Self::SpellPresentationNotAuthored => "spell-presentation-not-authored",
                Self::CombatVfxCueResolves => "combat-vfx-cue-resolves",
                Self::MeleeImpactAreaValid => "melee-impact-area-valid",
                Self::AbilityKindSupported => "ability-kind-supported",
            }
        }
    }

    #[derive(Clone, Debug, PartialEq, Eq)]
    struct CombatAuthoringError {
        rule: CombatAuthoringRule,
        message: String,
    }

    impl CombatAuthoringError {
        fn new(rule: CombatAuthoringRule, message: String) -> Self {
            Self { rule, message }
        }

        fn render(&self) -> String {
            format!("{}: {}", self.rule.code(), self.message)
        }
    }

    #[test]
    fn combat_vfx_cue_keys_keep_fireball_one_hand_cast_explicit() {
        let catalog = progression_catalog();
        let mut keys = HashSet::new();
        for cue in &catalog.combat_vfx_cues {
            assert!(
                keys.insert(combat_vfx_cue_key(cue)),
                "combat VFX cue key must be unique for {}:{} {} {}",
                cue.owner_kind,
                cue.owner_id,
                cue.trigger,
                cue.vfx_id
            );
        }

        let fireball_hand_cues: Vec<_> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_FIREBALL"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_CAST"
                    && normalize_identifier(cue.vfx_id.as_str()) == "VFX_FIRE_CAST_HAND_01"
            })
            .collect();
        assert_eq!(
            fireball_hand_cues.len(),
            1,
            "Fireball is authored as a one-hand cast; adding a second hand must be an explicit second cue"
        );
        assert_eq!(
            normalize_identifier(fireball_hand_cues[0].anchor.as_str()),
            "LEFT_HAND",
            "Fireball's cast-hand cue should follow only the authored casting hand"
        );

        let flaming_orb_hand_cues: Vec<_> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_FLAMING_ORB"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_CAST"
                    && normalize_identifier(cue.vfx_id.as_str()) == "VFX_FIRE_CAST_HAND_01"
            })
            .collect();
        assert_eq!(
            flaming_orb_hand_cues.len(),
            1,
            "Flaming Orb should reuse the fire school's single shared cast-hand template"
        );
        assert_eq!(
            normalize_identifier(flaming_orb_hand_cues[0].anchor.as_str()),
            "LEFT_HAND"
        );
    }

    #[test]
    fn projectile_body_vfx_resolution_is_single_selected_template() {
        assert_eq!(
            projectile_body_vfx_id_for_spell("SPELL_FIREBALL", "FIREBALL", 0).as_deref(),
            Some("VFX_FIREBALL_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("SPELL_FLAMING_ORB", "FLAMING_ORB", 0).as_deref(),
            Some("VFX_FLAMING_ORB_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("SPELL_ICICLE", "ICICLE", 0).as_deref(),
            Some("VFX_ICICLE_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("WARRIOR_GROUND_SLASH", "GROUND_SLASH", 0).as_deref(),
            Some("VFX_GROUND_SLASH_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("SPELL_VAMPIRIC_ORB", "VAMPIRIC_ORB", 0).as_deref(),
            Some("VFX_BOOMERANG_ORB_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("PALADIN_BLESSED_SHIELD", "BLESSED_SHIELD", 0)
                .as_deref(),
            Some("VFX_BLESSED_SHIELD_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("PALADIN_BLADE_BARRIER", "BLADE_BARRIER", 0),
            None,
            "Blade Barrier is a persistent target field, not an orbiting projectile"
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("SPELL_FIREBALL", "FIREBALL", 1),
            None,
            "runtime should not silently select a visual for an unauthored projectile sequence"
        );
    }

    #[test]
    fn combat_vfx_manifest_prefers_ability_projectile_body_over_spell_fallback() {
        let catalog = serde_json::from_str::<super::ProgressionCatalogFile>(
            r#"{
                "auto_attacks": [],
                "action_presentations": [],
                "combat_vfx_cues": [
                    {
                        "owner_kind": "SPELL",
                        "owner_id": "FIREBALL",
                        "trigger": "SPELL_RELEASE",
                        "anchor": "RIGHT_HAND",
                        "vfx_id": "SPELL_FIREBALL_PROJECTILE",
                        "attach_mode": "SPAWN_WORLD",
                        "vfx_role": "PROJECTILE_BODY",
                        "lifecycle": "UNTIL_TERMINAL_EVENT",
                        "projectile_sequence_index": 0,
                        "sort_order": 20
                    },
                    {
                        "owner_kind": "ABILITY",
                        "owner_id": "SPELL_FIREBALL",
                        "trigger": "SPELL_RELEASE",
                        "anchor": "RIGHT_HAND",
                        "vfx_id": "ABILITY_FIREBALL_PROJECTILE",
                        "attach_mode": "SPAWN_WORLD",
                        "vfx_role": "PROJECTILE_BODY",
                        "lifecycle": "UNTIL_TERMINAL_EVENT",
                        "projectile_sequence_index": 0,
                        "sort_order": 30
                    },
                    {
                        "owner_kind": "SPELL",
                        "owner_id": "FIREBALL",
                        "trigger": "SPELL_RELEASE",
                        "anchor": "RIGHT_HAND",
                        "vfx_id": "SPELL_FIREBALL_TRAIL",
                        "attach_mode": "SPAWN_WORLD",
                        "vfx_role": "PROJECTILE_TRAIL",
                        "lifecycle": "UNTIL_TERMINAL_EVENT",
                        "projectile_sequence_index": 0,
                        "sort_order": 40
                    },
                    {
                        "owner_kind": "ABILITY",
                        "owner_id": "SPELL_FIREBALL",
                        "trigger": "SPELL_RELEASE",
                        "anchor": "RIGHT_HAND",
                        "vfx_id": "ABILITY_FIREBALL_TRAIL",
                        "attach_mode": "SPAWN_WORLD",
                        "vfx_role": "PROJECTILE_TRAIL",
                        "lifecycle": "UNTIL_TERMINAL_EVENT",
                        "projectile_sequence_index": 0,
                        "sort_order": 50
                    }
                ]
            }"#,
        )
        .expect("test catalog should parse");
        let manifest = CombatVfxPresentationManifest::build(&catalog);

        assert_eq!(
            manifest.projectile_body_vfx_id_for_spell("SPELL_FIREBALL", "FIREBALL", 0),
            Some("ABILITY_FIREBALL_PROJECTILE")
        );
        assert_eq!(
            manifest.projectile_body_vfx_id_for_spell("OTHER_FIREBALL", "FIREBALL", 0),
            Some("SPELL_FIREBALL_PROJECTILE")
        );
        assert_eq!(
            manifest.projectile_trail_vfx_id_for_spell("SPELL_FIREBALL", "FIREBALL", 0),
            Some("ABILITY_FIREBALL_TRAIL")
        );
        assert_eq!(
            manifest.projectile_trail_vfx_id_for_spell("OTHER_FIREBALL", "FIREBALL", 0),
            Some("SPELL_FIREBALL_TRAIL")
        );
    }

    #[test]
    fn stale_movement_delivery_json_key_is_rejected() {
        let json = r#"{
            "ability_id": "BAD_CHARGE",
            "actor_scope": "PLAYER",
            "selection_kind": "ACTIVE",
            "combat_discipline_id": "TWO_HANDED_SWORD",
            "action_id": "BAD_CHARGE",
            "display_name": "Bad Charge",
            "sort_order": 1,
            "gameplay": {
                "kind": "MOVEMENT",
                "movement_delivery": {
                    "kind": "DASH_TO_TARGET"
                }
            }
        }"#;

        let err = match serde_json::from_str::<super::AbilityDefinition>(json) {
            Ok(_) => panic!("legacy gameplay.movement_delivery key must not parse"),
            Err(err) => err,
        };
        assert!(
            err.to_string()
                .contains("unknown field `movement_delivery`"),
            "unexpected parse error: {err}"
        );
    }

    #[test]
    fn movement_ability_classification_covers_current_delivery_domains() {
        assert!(action_id_is_movement_ability("TELEPORT"));
        assert!(action_id_is_movement_ability("WARRIOR_CHARGE"));
        assert!(action_id_is_movement_ability("DAGGER_COMBO_ATTACK_01_04"));
        assert!(!action_id_is_movement_ability("FIREBALL"));
        assert!(!action_id_is_movement_ability("TAILWIND"));
    }

    #[test]
    #[should_panic(expected = "SPELL presentation 'MOMENTUM' is derived")]
    fn authored_spell_presentations_are_rejected() {
        let catalog = serde_json::from_str::<super::ProgressionCatalogFile>(
            r#"{
                "auto_attacks": [],
                "action_presentations": [
                    {
                        "presentation_kind": "SPELL",
                        "presentation_id": "MOMENTUM",
                        "display_name": "Momentum",
                        "sort_order": 1
                    }
                ]
            }"#,
        )
        .expect("test catalog should parse");

        validate_progression_catalog_authoring_contract(&catalog);
    }

    #[test]
    fn authored_status_presentations_for_known_status_kinds_are_accepted() {
        let catalog = serde_json::from_str::<super::ProgressionCatalogFile>(
            r#"{
                "auto_attacks": [],
                "action_presentations": [
                    {
                        "presentation_kind": "STATUS",
                        "presentation_id": "STUN",
                        "display_name": "Stun",
                        "sort_order": 1
                    }
                ]
            }"#,
        )
        .expect("test catalog should parse");

        validate_progression_catalog_authoring_contract(&catalog);
    }

    #[test]
    #[should_panic(expected = "STATUS presentation 'UNKNOWN_STATUS' must reference")]
    fn authored_unknown_status_presentations_are_rejected() {
        let catalog = serde_json::from_str::<super::ProgressionCatalogFile>(
            r#"{
                "auto_attacks": [],
                "action_presentations": [
                    {
                        "presentation_kind": "STATUS",
                        "presentation_id": "UNKNOWN_STATUS",
                        "display_name": "Unknown Status",
                        "sort_order": 1
                    }
                ]
            }"#,
        )
        .expect("test catalog should parse");

        validate_progression_catalog_authoring_contract(&catalog);
    }

    #[test]
    #[should_panic(expected = "authors scale in progression_catalog.shared.json")]
    fn authored_combat_vfx_cue_scale_is_rejected() {
        let catalog = serde_json::from_str::<super::ProgressionCatalogFile>(
            r#"{
                "auto_attacks": [],
                "action_presentations": [],
                "combat_vfx_cues": [
                    {
                        "owner_kind": "ABILITY",
                        "owner_id": "SPELL_FIREBALL",
                        "trigger": "SPELL_IMPACT",
                        "anchor": "IMPACT_POINT",
                        "vfx_id": "VFX_FIREBALL_HIT_01",
                        "attach_mode": "SPAWN_WORLD",
                        "vfx_role": "ONE_SHOT",
                        "lifecycle": "DURATION",
                        "scale": 0.075,
                        "duration_ms": 1000,
                        "sort_order": 1
                    }
                ]
            }"#,
        )
        .expect("test catalog should parse");

        validate_progression_catalog_authoring_contract(&catalog);
    }

    #[test]
    fn frost_nova_vfx_uses_particle_system_lifecycle() {
        let catalog = progression_catalog();
        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_FROST_NOVA"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_RELEASE"
                    && normalize_identifier(cue.vfx_id.as_str()) == "VFX_FROST_NOVA_01"
            })
            .expect("Frost Nova release VFX cue should be authored");

        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
        assert_eq!(
            cue.duration_ms, 0,
            "PARTICLE_SYSTEM VFX cues should let the prefab particle systems define visual lifetime"
        );
    }

    #[test]
    fn nova_authors_self_centered_arcane_area_and_target_hit_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_NOVA")
            .expect("Nova ability should be authored");

        assert_eq!(normalize_identifier(ability.action_id.as_str()), "NOVA");
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "SELF"
        );
        assert_eq!(ability.gameplay.requires_target, Some(false));
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(ability.gameplay.resource_cost, Some(20.0));

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Nova should author area delivery");
        assert_eq!(
            delivery.get("kind").and_then(|value| value.as_str()),
            Some("AREA")
        );
        assert_eq!(
            delivery.get("damage_type").and_then(|value| value.as_str()),
            Some("ARCANE")
        );
        assert_eq!(
            delivery.get("radius").and_then(|value| value.as_f64()),
            Some(4.6)
        );

        let release = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_NOVA"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_RELEASE"
            })
            .expect("Nova release VFX cue should be authored");
        assert_eq!(normalize_identifier(release.anchor.as_str()), "CASTER");
        assert_eq!(
            normalize_identifier(release.vfx_id.as_str()),
            "VFX_NOVA_CAST_01"
        );

        let hit = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_NOVA"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Nova target-hit VFX cue should be authored");
        assert_eq!(normalize_identifier(hit.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(normalize_identifier(hit.vfx_id.as_str()), "VFX_NOVA_HIT_01");
    }

    #[test]
    fn blinding_light_authors_overhead_release_vfx() {
        let catalog = progression_catalog();
        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "SPELL_BLINDING_LIGHT"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_RELEASE"
            })
            .expect("Blinding Light release VFX cue should be authored");

        assert_eq!(normalize_identifier(cue.anchor.as_str()), "CASTER_OVERHEAD");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_BLINDING_LIGHT_HOLY_OVERHEAD_01"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
    }

    #[test]
    fn glacial_spike_authors_target_impact_vfx() {
        let catalog = progression_catalog();
        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "SPELL_GLACIAL_SPIKE"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Glacial Spike impact VFX cue should be authored");

        assert_eq!(normalize_identifier(cue.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_GLACIAL_SPIKE_TARGET_01"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
    }

    #[test]
    fn frozen_splinters_authors_projectile_body_vfx() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_FROZEN_SPLINTERS")
            .expect("Frozen Splinters ability should be authored");
        assert_eq!(ability.action_id, "FROZEN_SPLINTERS");
        assert_eq!(ability_delivery_kind(ability), "CHANNEL");
        assert_eq!(projectile_delivery_projectile_count(ability), 1);
        assert_eq!(
            projectile_body_vfx_id_for_spell("SPELL_FROZEN_SPLINTERS", "FROZEN_SPLINTERS", 0)
                .as_deref(),
            Some("VFX_FROZEN_SPLINTER_PROJECTILE_01")
        );
    }

    #[test]
    fn magic_missile_authors_projectile_body_vfx() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_MAGIC_MISSILE")
            .expect("Magic Missile ability should be authored");
        assert_eq!(ability.action_id, "MAGIC_MISSILE");
        assert_eq!(ability_delivery_kind(ability), "CHANNEL");
        assert_eq!(projectile_delivery_projectile_count(ability), 1);
        assert_eq!(
            projectile_body_vfx_id_for_spell("SPELL_MAGIC_MISSILE", "MAGIC_MISSILE", 0).as_deref(),
            Some("VFX_MAGIC_MISSILE_PROJECTILE_01")
        );
    }

    #[test]
    fn restoration_authors_healing_channel_and_cast_lifetime_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_RESTORATION")
            .expect("Restoration ability should be authored");
        assert_eq!(ability.action_id, "RESTORATION");
        assert_eq!(ability_delivery_kind(ability), "CHANNEL");
        assert_eq!(projectile_delivery_projectile_count(ability), 0);
        assert_eq!(
            normalize_optional_target_audience(ability.gameplay.target_audience.as_str()),
            "ASSISTABLE"
        );

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Restoration should define delivery data");
        assert_eq!(
            delivery.get("heal").and_then(serde_json::Value::as_i64),
            Some(1)
        );
        assert_eq!(
            delivery
                .get("resource_cost_per_second")
                .and_then(serde_json::Value::as_f64),
            Some(1.0)
        );
        assert_eq!(
            delivery
                .get("duration_seconds")
                .and_then(serde_json::Value::as_f64),
            Some(5.0)
        );
        assert_eq!(
            normalize_identifier(
                delivery
                    .get("damage_type")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default()
            ),
            "HOLY"
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "SPELL_RESTORATION"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Restoration channel VFX cue should be authored");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "TARGET");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_RESTORATION_CHANNEL_01"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "UNTIL_CAST_END"
        );
    }

    #[test]
    fn protection_authors_targeted_damage_reduction_and_attached_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_PROTECTION")
            .expect("Protection ability should be authored");
        assert_eq!(ability.action_id, "PROTECTION");
        assert_eq!(ability_delivery_kind(ability), "APPLY_STATUS");
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "TARGET"
        );
        assert_eq!(
            normalize_optional_target_audience(ability.gameplay.target_audience.as_str()),
            "PARTY_OR_SELF"
        );

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Protection should define delivery data");
        assert_eq!(
            delivery
                .get("duration_ms")
                .and_then(serde_json::Value::as_u64),
            Some(5_000)
        );
        assert_eq!(
            delivery
                .get("max_distance")
                .and_then(serde_json::Value::as_f64),
            Some(18.0)
        );
        assert_eq!(
            delivery
                .get("damage_type")
                .and_then(serde_json::Value::as_str),
            Some("HOLY")
        );
        assert_eq!(
            delivery
                .get("status")
                .and_then(serde_json::Value::as_object)
                .and_then(|status| status.get("kind"))
                .and_then(serde_json::Value::as_str),
            Some("DAMAGE_TAKEN_REDUCTION")
        );
        assert_eq!(
            delivery
                .get("status")
                .and_then(serde_json::Value::as_object)
                .and_then(|status| status.get("modifier_scalar"))
                .and_then(serde_json::Value::as_f64),
            Some(0.5)
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "SPELL_PROTECTION"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Protection target VFX cue should be authored");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "TARGET");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "FOLLOW_ANCHOR"
        );
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_PROTECTION_SHIELD_BUFF_01"
        );
        assert_eq!(normalize_identifier(cue.lifecycle.as_str()), "DURATION");
        assert_eq!(cue.duration_ms, 5_000);
    }

    #[test]
    fn celestial_mantle_authors_targeted_holy_immunity_and_attached_wings() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_CELESTIAL_MANTLE")
            .expect("Celestial Mantle ability should be authored");
        assert_eq!(ability.action_id, "CELESTIAL_MANTLE");
        assert_eq!(ability_delivery_kind(ability), "APPLY_STATUS");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(
            normalize_optional_target_audience(ability.gameplay.target_audience.as_str()),
            "PARTY_OR_SELF"
        );

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Celestial Mantle should define delivery data");
        assert_eq!(
            delivery
                .get("damage_type")
                .and_then(serde_json::Value::as_str),
            Some("HOLY")
        );
        assert_eq!(
            delivery
                .get("status")
                .and_then(serde_json::Value::as_object)
                .and_then(|status| status.get("kind"))
                .and_then(serde_json::Value::as_str),
            Some("MOVEMENT_IMPAIRING_IMMUNITY")
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_CELESTIAL_MANTLE"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Celestial Mantle target wing VFX cue should be authored");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "TARGET_BACK");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "FOLLOW_ANCHOR"
        );
        assert_eq!(normalize_identifier(cue.vfx_role.as_str()), "ATTACHED");
        assert_eq!(normalize_identifier(cue.lifecycle.as_str()), "DURATION");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_CELESTIAL_MANTLE_WINGS_01"
        );
        assert_eq!(cue.duration_ms, 5_000);
    }

    #[test]
    fn glacial_advance_authors_blight_stun_immunity_and_frost_impact() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_GLACIAL_ADVANCE")
            .expect("Glacial Advance ability should be authored");
        assert_eq!(ability.spell_school_id.as_deref(), Some("BLIGHT"));
        assert_eq!(ability.action_id, "GLACIAL_ADVANCE");
        assert_eq!(ability_delivery_kind(ability), "APPLY_STATUS");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(ability.gameplay.cooldown_ms, Some(30_000));
        assert_eq!(ability.gameplay.resource_cost, Some(20.0));
        assert_eq!(
            normalize_optional_target_audience(ability.gameplay.target_audience.as_str()),
            "PARTY_OR_SELF"
        );

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Glacial Advance should define delivery data");
        assert_eq!(
            delivery
                .get("duration_ms")
                .and_then(serde_json::Value::as_u64),
            Some(10_000)
        );
        assert_eq!(
            delivery
                .get("max_distance")
                .and_then(serde_json::Value::as_f64),
            Some(18.0)
        );
        assert_eq!(
            delivery
                .get("damage_type")
                .and_then(serde_json::Value::as_str),
            Some("COLD")
        );
        assert_eq!(
            delivery
                .get("status")
                .and_then(serde_json::Value::as_object)
                .and_then(|status| status.get("kind"))
                .and_then(serde_json::Value::as_str),
            Some("STUN_IMMUNITY")
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_GLACIAL_ADVANCE"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Glacial Advance frost impact VFX cue should be authored");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_GLACIAL_SPIKE_TARGET_01"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
        assert_eq!(cue.duration_ms, 0);
    }

    #[test]
    fn holy_shield_authors_divinity_absorb_and_natural_end_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_HOLY_SHIELD")
            .expect("Holy Shield ability should be authored");
        assert_eq!(ability.spell_school_id.as_deref(), Some("DIVINITY"));
        assert_eq!(ability.action_id, "HOLY_SHIELD");
        assert_eq!(ability_delivery_kind(ability), "APPLY_STATUS");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(ability.gameplay.cooldown_ms, Some(30_000));
        assert_eq!(ability.gameplay.resource_cost, Some(0.0));
        assert_eq!(
            normalize_optional_target_audience(ability.gameplay.target_audience.as_str()),
            "PARTY_OR_SELF"
        );

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Holy Shield should define delivery data");
        assert_eq!(
            delivery
                .get("duration_ms")
                .and_then(serde_json::Value::as_u64),
            Some(20_000)
        );
        assert_eq!(
            delivery
                .get("max_distance")
                .and_then(serde_json::Value::as_f64),
            Some(18.0)
        );
        let status = delivery
            .get("status")
            .and_then(serde_json::Value::as_object)
            .expect("Holy Shield should author temporary hitpoints");
        assert_eq!(
            status.get("kind").and_then(serde_json::Value::as_str),
            Some("TEMPORARY_HITPOINTS")
        );
        assert_eq!(
            status
                .get("absorb_amount")
                .and_then(serde_json::Value::as_i64),
            Some(100)
        );
        assert_eq!(
            status.get("absorb_cap").and_then(serde_json::Value::as_i64),
            Some(100)
        );

        let active_cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_HOLY_SHIELD"
                    && normalize_identifier(cue.trigger.as_str()) == "STATUS_ACTIVE"
            })
            .expect("Holy Shield active target VFX cue should be authored");
        assert_eq!(normalize_identifier(active_cue.anchor.as_str()), "TARGET");
        assert_eq!(
            normalize_identifier(active_cue.attach_mode.as_str()),
            "FOLLOW_ANCHOR"
        );
        assert_eq!(
            normalize_identifier(active_cue.vfx_id.as_str()),
            "VFX_HOLY_SHIELD_ACTIVE_01"
        );
        assert_eq!(
            normalize_identifier(active_cue.lifecycle.as_str()),
            "UNTIL_STATUS_END"
        );

        let end_cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_HOLY_SHIELD"
                    && normalize_identifier(cue.trigger.as_str()) == "STATUS_END"
            })
            .expect("Holy Shield natural-end VFX cue should be authored");
        assert_eq!(normalize_identifier(end_cue.anchor.as_str()), "TARGET");
        assert_eq!(
            normalize_identifier(end_cue.vfx_id.as_str()),
            "VFX_HOLY_SHIELD_END_01"
        );
        assert_eq!(
            normalize_identifier(end_cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
        assert_eq!(end_cue.duration_ms, 0);
    }

    #[test]
    fn rebuke_authors_divinity_interrupt_conditional_holy_damage_and_impact_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_REBUKE")
            .expect("Rebuke ability should be authored");
        assert_eq!(ability.spell_school_id.as_deref(), Some("DIVINITY"));
        assert_eq!(ability.action_id, "REBUKE");
        assert_eq!(ability_delivery_kind(ability), "DIRECT_TARGET");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(ability.gameplay.cooldown_ms, Some(12_000));
        assert_eq!(ability.gameplay.uses_global_cooldown, Some(false));
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "TARGET"
        );
        assert_eq!(
            normalize_optional_target_audience(ability.gameplay.target_audience.as_str()),
            "HOSTILE"
        );
        assert_eq!(ability.gameplay.requires_target, Some(true));
        assert_eq!(ability.gameplay.requires_target_los, Some(true));
        assert_eq!(ability.gameplay.resource_cost, Some(0.0));

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Rebuke delivery should be authored");
        assert_eq!(
            delivery
                .get("max_distance")
                .and_then(serde_json::Value::as_f64),
            Some(18.0)
        );
        assert_eq!(
            delivery.get("damage").and_then(serde_json::Value::as_i64),
            Some(0)
        );
        assert_eq!(
            delivery
                .get("damage_type")
                .and_then(serde_json::Value::as_str),
            Some("HOLY")
        );
        let effects = delivery
            .get("impact_effects")
            .and_then(serde_json::Value::as_array)
            .expect("Rebuke impact effect should be authored");
        assert_eq!(effects.len(), 1);
        assert_eq!(
            effects[0].get("kind").and_then(serde_json::Value::as_str),
            Some("INTERRUPT_CAST_WITH_DAMAGE")
        );
        assert_eq!(
            effects[0].get("damage").and_then(serde_json::Value::as_i64),
            Some(30)
        );
        assert_eq!(
            effects[0]
                .get("damage_type")
                .and_then(serde_json::Value::as_str),
            Some("HOLY")
        );

        let cues: Vec<_> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| normalize_identifier(cue.owner_id.as_str()) == "SPELL_REBUKE")
            .collect();
        assert_eq!(cues.len(), 1);
        let cue = cues[0];
        assert_eq!(normalize_identifier(cue.trigger.as_str()), "SPELL_IMPACT");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(normalize_identifier(cue.vfx_id.as_str()), "VFX_HOLY_HIT_01");
    }

    #[test]
    fn new_divinity_spell_family_and_faith_passive_are_fully_presented() {
        let catalog = progression_catalog();
        let expected = [
            ("SPELL_SMITE", "SMITE", "SPELL"),
            ("SPELL_PENANCE", "PENANCE", "SPELL"),
            ("SPELL_RECKONING", "RECKONING", "SPELL"),
            ("SPELL_MARTYR", "MARTYR", "SPELL"),
            ("SPELL_BURDEN", "BURDEN", "SPELL"),
            ("DIVINITY_FAITH", "FAITH", "PASSIVE"),
        ];
        for (ability_id, action_id, gameplay_kind) in expected {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("missing {ability_id}"));
            assert_eq!(ability.spell_school_id.as_deref(), Some("DIVINITY"));
            assert_eq!(ability.action_id, action_id);
            assert_eq!(ability_gameplay_kind(ability), gameplay_kind);
            assert!(catalog.action_presentations.iter().any(|presentation| {
                normalize_identifier(presentation.presentation_id.as_str()) == ability_id
            }));
        }

        let faith = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DIVINITY_FAITH")
            .expect("Faith ability");
        assert!((faith.gameplay.mana_regen_bonus - 2.0).abs() < 0.0001);
        assert!(faith
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));

        let expected_cues = [
            ("SPELL_SMITE", "SPELL_IMPACT", "VFX_HOLY_HIT_01"),
            ("SPELL_PENANCE", "SPELL_IMPACT", "VFX_ABSOLUTION_HOLY_01"),
            ("SPELL_RECKONING", "STATUS_END", "VFX_HOLY_HIT_01"),
            ("SPELL_MARTYR", "SPELL_IMPACT", "VFX_CLEANSE_HOLY_01"),
            (
                "SPELL_BURDEN",
                "STATUS_ACTIVE",
                "VFX_PROTECTION_SHIELD_BUFF_01",
            ),
        ];
        for (owner, trigger, vfx_id) in expected_cues {
            assert!(catalog.combat_vfx_cues.iter().any(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == owner
                    && normalize_identifier(cue.trigger.as_str()) == trigger
                    && normalize_identifier(cue.vfx_id.as_str()) == vfx_id
            }));
        }
    }

    #[test]
    fn blight_toxic_weapon_and_contagion_author_poison_contracts() {
        let catalog = progression_catalog();
        let toxic = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == BLIGHT_TOXIC_WEAPON_ABILITY_ID)
            .expect("Toxic Weapon should be authored");
        assert_eq!(toxic.spell_school_id.as_deref(), Some("BLIGHT"));
        assert_eq!(ability_gameplay_kind(toxic), "PASSIVE");
        let poison = toxic
            .gameplay
            .melee_poison_on_hit
            .as_ref()
            .expect("Toxic Weapon should define melee poison tuning");
        assert!((poison.proc_chance - 0.25).abs() < 0.0001);
        assert_eq!(poison.poison_duration_ms, 6_000);
        assert_eq!(poison.poison_tick_interval_ms, 1_000);
        assert_eq!(poison.poison_tick_damage, 2);
        assert_eq!(poison.poison_max_stacks, 5);
        assert_eq!(poison.poison_status_stack_group, "POISON");
        assert_eq!(poison.poison_dispel_types, vec![StatusDispelType::Poison]);

        let contagion = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_CONTAGION")
            .expect("Contagion should be authored");
        assert_eq!(contagion.spell_school_id.as_deref(), Some("BLIGHT"));
        assert_eq!(ability_gameplay_kind(contagion), "SPELL");
        assert_eq!(ability_delivery_kind(contagion), "APPLY_STATUS");
        assert_eq!(contagion.gameplay.cooldown_ms, Some(12_000));
        let delivery = contagion.gameplay.delivery.as_ref().unwrap();
        assert_eq!(
            delivery
                .get("duration_ms")
                .and_then(serde_json::Value::as_u64),
            Some(10_000)
        );
        assert_eq!(
            delivery
                .get("status_stack_group")
                .and_then(serde_json::Value::as_str),
            Some("CONTAGIOUS:{SOURCE}")
        );
        assert_eq!(
            delivery
                .pointer("/status/kind")
                .and_then(serde_json::Value::as_str),
            Some("CONTAGIOUS")
        );
        assert_eq!(combat_rule_value("CONTAGION_RADIUS_METERS"), 5.0);
        assert!(authored_status_presentation_ids(catalog).contains("CONTAGIOUS"));
        assert!(authored_status_presentation_ids(catalog).contains("POISON"));
        assert!(catalog.combat_vfx_cues.iter().any(|cue| {
            normalize_identifier(cue.owner_id.as_str()) == "SPELL_CONTAGION"
                && normalize_identifier(cue.trigger.as_str()) == "STATUS_ACTIVE"
                && normalize_identifier(cue.vfx_id.as_str()) == "VFX_CONTAGION_ACTIVE_01"
        }));
    }

    #[test]
    fn soulstealer_authors_blight_empowerment_and_target_impact_presentation() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_SOULSTEALER")
            .expect("Soulstealer ability should be authored");
        assert_eq!(ability.spell_school_id.as_deref(), Some("MORTALITY"));
        assert_eq!(ability.action_id, "SOULSTEALER");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(ability.gameplay.cast_time_ms, Some(2_000));
        assert!((ability.gameplay.soulstealer_empowered_damage_bonus - 0.5).abs() < 0.0001);

        assert!(authored_status_presentation_ids(catalog).contains("SOUL_STOLEN"));
        assert!(authored_status_presentation_ids(catalog).contains("BLIGHT_EMPOWERED"));
        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_SOULSTEALER"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Soulstealer target impact VFX cue should be authored");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "TARGET");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_SOULSTEALER_TARGET_01"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
    }

    #[test]
    fn ruin_capacitor_authors_targeted_lightning_column_and_discharge_vfx() {
        let catalog = progression_catalog();
        let capacitor = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_CAPACITOR")
            .expect("Capacitor ability should be authored");
        assert_eq!(capacitor.spell_school_id.as_deref(), Some("RUIN"));
        assert_eq!(capacitor.action_id, "CAPACITOR");
        assert_eq!(ability_gameplay_kind(capacitor), "SPELL");
        assert_eq!(ability_delivery_kind(capacitor), "AREA");
        assert_eq!(
            normalize_identifier(capacitor.gameplay.targeting.as_str()),
            "TARGET"
        );
        assert_eq!(capacitor.gameplay.requires_target, Some(true));
        let delivery = capacitor
            .gameplay
            .delivery
            .as_ref()
            .expect("Capacitor should define area delivery");
        assert_eq!(
            delivery
                .get("max_distance")
                .and_then(serde_json::Value::as_f64),
            Some(18.0)
        );
        assert_eq!(
            delivery.get("damage").and_then(serde_json::Value::as_i64),
            Some(40)
        );
        assert_eq!(
            delivery
                .get("damage_type")
                .and_then(serde_json::Value::as_str),
            Some("LIGHTNING")
        );
        let shape = delivery
            .get("shape")
            .expect("Capacitor should define a column shape");
        assert_eq!(
            shape.get("kind").and_then(serde_json::Value::as_str),
            Some("TARGET_COLUMN")
        );
        assert_eq!(
            shape.get("width").and_then(serde_json::Value::as_f64),
            Some(2.5)
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| cue.owner_id == "SPELL_CAPACITOR" && cue.trigger == "SPELL_RELEASE")
            .expect("Capacitor should author its Discharge release VFX");
        assert_eq!(cue.vfx_id, "VFX_CAPACITOR_DISCHARGE_01");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "WORLD_ALIGNED_TO_FACING"
        );
    }

    #[test]
    fn blight_rime_authors_off_gcd_active_status_empowerment() {
        let catalog = progression_catalog();
        let rime = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_RIME")
            .expect("Rime ability should be authored");
        assert_eq!(rime.spell_school_id.as_deref(), Some("BLIGHT"));
        assert_eq!(rime.action_id, "RIME");
        assert_eq!(ability_gameplay_kind(rime), "SPELL");
        assert!(rime
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "ACTION_BAR_ACTION"));

        let definition =
            spell_definition_by_str("RIME").expect("Rime should derive a runtime spell definition");
        assert_eq!(definition.behavior, SpellBehavior::ApplyStatus);
        assert_eq!(definition.targeting, SpellTargeting::Self_);
        assert_eq!(definition.target_audience, TargetAudience::SelfOnly);
        assert!(!definition.requires_target);
        assert!(!definition.uses_global_cooldown);
        assert_eq!(definition.cooldown, Duration::from_secs(30));
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.status_stack_group.as_deref(), Some("RIME_ARMED"));
        assert_eq!(definition.apply_status_polarity, Some(StatusPolarity::Buff));
        assert_eq!(
            definition
                .apply_status
                .as_ref()
                .expect("Rime should apply its armed status")
                .kind,
            StatusEffectKind::Rime
        );

        let ability_presentation = catalog
            .action_presentations
            .iter()
            .find(|presentation| {
                presentation.presentation_kind == "ABILITY"
                    && presentation.presentation_id == "RUIN_RIME"
            })
            .expect("Rime should have ability presentation text");
        assert_eq!(ability_presentation.display_name, "Rime");
        assert!(authored_status_presentation_ids(catalog).contains("RIME"));
    }

    #[test]
    fn blight_blizzard_authors_point_area_channel_and_persistent_ice_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_BLIZZARD")
            .expect("Blizzard ability should be authored");
        assert_eq!(ability.spell_school_id.as_deref(), Some("BLIGHT"));
        assert_eq!(ability.action_id, "BLIZZARD");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");

        let definition = spell_definition_by_str("BLIZZARD")
            .expect("Blizzard should derive a runtime spell definition");
        assert_eq!(definition.behavior.as_str(), "CHANNEL");
        assert_eq!(definition.targeting, SpellTargeting::Point);
        assert!(!definition.requires_target);
        assert_eq!(definition.damage_type, DamageType::Cold);
        assert!((definition.radius - 4.0).abs() < 0.0001);

        let area = definition
            .secondary
            .channel_area
            .as_ref()
            .expect("Blizzard should author channel-area tunables");
        let slow = area
            .impact_effects
            .iter()
            .find_map(|effect| effect.as_status())
            .expect("Blizzard should apply an area slow");
        assert_eq!(slow.payload(), StatusPayload::Slow { slow_pct: 0.5 });

        let field_cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_BLIZZARD"
                    && normalize_identifier(cue.vfx_id.as_str()) == "VFX_BLIZZARD_AREA_01"
            })
            .expect("Blizzard should author its icicle-rain field cue");
        assert_eq!(
            normalize_identifier(field_cue.trigger.as_str()),
            "AREA_IMPACT"
        );
        assert_eq!(
            normalize_identifier(field_cue.anchor.as_str()),
            "AREA_ORIGIN"
        );
        assert_eq!(
            normalize_identifier(field_cue.lifecycle.as_str()),
            "UNTIL_CAST_END"
        );
    }

    #[test]
    fn ruin_immolation_and_combustion_author_shared_stack_and_detonation_contracts() {
        let catalog = progression_catalog();
        let immolation = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_IMMOLATION")
            .expect("Immolation ability should be authored");
        assert_eq!(immolation.spell_school_id.as_deref(), Some("RUIN"));
        assert_eq!(immolation.action_id, "IMMOLATION");
        assert_eq!(ability_gameplay_kind(immolation), "SPELL");
        assert_eq!(ability_delivery_kind(immolation), "IMMOLATION");
        assert_eq!(immolation.gameplay.cast_time_ms, Some(0));
        assert_eq!(immolation.gameplay.targeting, "SELF");
        let immolation_delivery = immolation
            .gameplay
            .delivery
            .as_ref()
            .expect("Immolation should define delivery tuning");
        assert_eq!(
            immolation_delivery
                .get("damage_interval_ms")
                .and_then(serde_json::Value::as_u64),
            Some(1_000)
        );
        assert_eq!(
            immolation_delivery
                .get("stack_interval_ms")
                .and_then(serde_json::Value::as_u64),
            Some(3_000)
        );
        assert_eq!(
            immolation_delivery
                .get("stack_duration_ms")
                .and_then(serde_json::Value::as_u64),
            Some(6_000)
        );
        assert_eq!(
            immolation_delivery
                .get("max_stacks")
                .and_then(serde_json::Value::as_u64),
            Some(10)
        );
        assert!(authored_status_presentation_ids(catalog).contains("IMMOLATION"));

        let active_body = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_IMMOLATION"
                    && normalize_identifier(cue.trigger.as_str()) == "EMANATION_ACTIVE"
            })
            .expect("Immolation should author its active-stack body VFX");
        assert_eq!(
            normalize_identifier(active_body.vfx_id.as_str()),
            "VFX_IMMOLATION_BODY_LIGHT_01"
        );
        assert_eq!(normalize_identifier(active_body.anchor.as_str()), "CASTER");
        assert_eq!(
            normalize_identifier(active_body.attach_mode.as_str()),
            "FOLLOW_ANCHOR"
        );
        assert_eq!(
            normalize_identifier(active_body.lifecycle.as_str()),
            "UNTIL_RADIAL_EFFECT_END"
        );

        let max_stack_body = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_IMMOLATION"
                    && normalize_identifier(cue.trigger.as_str()) == "EMANATION_MAX_STACKS"
            })
            .expect("Immolation should author its max-stack body VFX");
        assert_eq!(
            normalize_identifier(max_stack_body.vfx_id.as_str()),
            "VFX_IMMOLATION_BODY_STRONG_01"
        );
        assert_eq!(
            normalize_identifier(max_stack_body.anchor.as_str()),
            "CASTER"
        );
        assert_eq!(
            normalize_identifier(max_stack_body.attach_mode.as_str()),
            "FOLLOW_ANCHOR"
        );
        assert_eq!(
            normalize_identifier(max_stack_body.lifecycle.as_str()),
            "UNTIL_RADIAL_EFFECT_END"
        );

        let combustion = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_COMBUSTION")
            .expect("Combustion ability should be authored");
        assert_eq!(combustion.spell_school_id.as_deref(), Some("RUIN"));
        assert_eq!(combustion.action_id, "COMBUSTION");
        assert_eq!(ability_gameplay_kind(combustion), "SPELL");
        assert_eq!(ability_delivery_kind(combustion), "AREA");
        assert_eq!(combustion.gameplay.cast_time_ms, Some(0));
        assert_eq!(combustion.gameplay.targeting, "SELF");
        assert_eq!(combustion.gameplay.target_audience, "HOSTILE");
        let combustion_delivery = combustion
            .gameplay
            .delivery
            .as_ref()
            .expect("Combustion should define delivery tuning");
        assert_eq!(
            combustion_delivery
                .get("consume_caster_burns")
                .and_then(serde_json::Value::as_bool),
            Some(true)
        );
        assert_eq!(
            combustion_delivery
                .get("radius")
                .and_then(serde_json::Value::as_f64),
            Some(4.6)
        );
    }

    #[test]
    fn ruin_fulmination_authors_any_target_melee_arc_debuff_and_tunables() {
        let catalog = progression_catalog();
        let fulmination = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_FULMINATION")
            .expect("Fulmination ability should be authored");
        assert_eq!(
            normalize_identifier(fulmination.spell_school_id.as_deref().unwrap_or_default()),
            "RUIN"
        );
        assert_eq!(fulmination.action_id, "FULMINATION");
        assert_eq!(ability_gameplay_kind(fulmination), "SPELL");
        assert_eq!(fulmination.gameplay.cast_time_ms, Some(0));
        assert_eq!(fulmination.gameplay.cooldown_ms, Some(12_000));
        assert_eq!(fulmination.gameplay.resource_cost, Some(20.0));
        assert_eq!(
            normalize_identifier(fulmination.gameplay.target_audience.as_str()),
            "ANY"
        );

        let definition = spell_definition_by_str("FULMINATION")
            .expect("Fulmination should derive a spell definition");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.duration, 8.0);
        assert_eq!(definition.max_distance, 30.0);
        assert_eq!(definition.damage_type, DamageType::Lightning);
        assert_eq!(definition.target_audience.as_str(), "ANY");
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("FULMINATION")
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Fulmination should apply its marker debuff");
        assert_eq!(status.payload(), StatusPayload::Fulmination);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Magic]);

        assert!(catalog.action_presentations.iter().any(|presentation| {
            presentation.presentation_kind == "ABILITY"
                && presentation.presentation_id == "SPELL_FULMINATION"
        }));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            presentation.presentation_kind == "STATUS"
                && presentation.presentation_id == "FULMINATION"
        }));
        assert!(catalog.combat_vfx_cues.iter().any(|cue| {
            normalize_identifier(cue.owner_id.as_str()) == "SPELL_FULMINATION"
                && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
                && normalize_identifier(cue.vfx_id.as_str()) == "VFX_LIGHTNING_01"
        }));
        assert!(catalog.combat_vfx_cues.iter().any(|cue| {
            normalize_identifier(cue.owner_id.as_str()) == "SPELL_FULMINATION"
                && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
                && normalize_identifier(cue.anchor.as_str()) == "IMPACT_POINT"
                && normalize_identifier(cue.vfx_id.as_str()) == "VFX_FULMINATION_ARC_01"
        }));

        let arc_radius = catalog
            .combat_rules
            .iter()
            .find(|rule| rule.combat_rule_id == "FULMINATION_ARC_RADIUS_METERS")
            .expect("Fulmination arc radius should be authored");
        assert_eq!(arc_radius.scalar_value, 10.0);
        let arc_damage = catalog
            .combat_rules
            .iter()
            .find(|rule| rule.combat_rule_id == "FULMINATION_ARC_DAMAGE_MULTIPLIER")
            .expect("Fulmination arc damage multiplier should be authored");
        assert_eq!(arc_damage.scalar_value, 1.0);
    }

    #[test]
    fn flashfire_authors_instant_fire_damage_and_target_hit_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_FLASHFIRE")
            .expect("Flashfire ability should be authored");
        assert_eq!(ability.action_id, "FLASHFIRE");
        assert_eq!(ability_delivery_kind(ability), "DIRECT_TARGET");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Flashfire should define delivery data");
        assert_eq!(
            delivery.get("damage").and_then(serde_json::Value::as_i64),
            Some(30)
        );
        assert_eq!(
            delivery
                .get("damage_type")
                .and_then(serde_json::Value::as_str),
            Some("FIRE")
        );

        let cues: Vec<_> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| normalize_identifier(cue.owner_id.as_str()) == "SPELL_FLASHFIRE")
            .collect();
        assert_eq!(cues.len(), 1);
        let cue = cues[0];
        assert_eq!(normalize_identifier(cue.slot.as_str()), "IMPACT");
        assert_eq!(normalize_identifier(cue.trigger.as_str()), "SPELL_IMPACT");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_FIREBALL_HIT_01"
        );
        assert_eq!(normalize_identifier(cue.vfx_role.as_str()), "ONE_SHOT");
        assert_eq!(normalize_identifier(cue.lifecycle.as_str()), "DURATION");
        assert_eq!(cue.duration_ms, 1_000);
    }

    #[test]
    fn collapse_authors_instant_arcane_magic_dot_consumption_and_hit_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_COLLAPSE")
            .expect("Collapse ability should be authored");
        assert_eq!(ability.action_id, "COLLAPSE");
        assert_eq!(ability_delivery_kind(ability), "CONSUME_STATUS");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Collapse should define delivery data");
        assert_eq!(
            delivery
                .get("damage_type")
                .and_then(serde_json::Value::as_str),
            Some("ARCANE")
        );
        assert_eq!(
            delivery
                .get("deal_remaining_dot_damage")
                .and_then(serde_json::Value::as_bool),
            Some(true)
        );
        assert_eq!(
            delivery
                .get("dispel_types")
                .and_then(serde_json::Value::as_array)
                .and_then(|values| values.first())
                .and_then(serde_json::Value::as_str),
            Some("MAGIC")
        );

        let cues: Vec<_> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| normalize_identifier(cue.owner_id.as_str()) == "SPELL_COLLAPSE")
            .collect();
        assert_eq!(cues.len(), 1);
        let cue = cues[0];
        assert_eq!(normalize_identifier(cue.trigger.as_str()), "SPELL_IMPACT");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_ARCANE_HIT_01"
        );
    }

    #[test]
    fn frozen_grasp_authors_self_area_root_and_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_FROZEN_GRASP")
            .expect("expected Frozen Grasp ability");
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "FROZEN_GRASP"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "SELF"
        );
        assert_eq!(ability.gameplay.requires_target, Some(false));
        assert_eq!(ability.gameplay.resource_cost, Some(20.0));

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Frozen Grasp should author spell delivery");
        let effects = delivery
            .get("impact_effects")
            .and_then(|value| value.as_array())
            .expect("Frozen Grasp should author impact effects");
        assert!(
            effects.iter().any(|effect| {
                effect.get("kind").and_then(|value| value.as_str()) == Some("ROOT")
                    && effect.get("duration_ms").and_then(|value| value.as_u64()) == Some(1200)
            }),
            "Frozen Grasp should apply ROOT through area impact effects"
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "SPELL_FROZEN_GRASP"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Frozen Grasp should author an area-impact VFX cue");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "AREA_ORIGIN");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_FROZEN_GRASP_AREA_01"
        );
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "WORLD_ALIGNED_TO_FACING"
        );
    }

    #[test]
    fn frost_needle_authors_delayed_point_area_and_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_FROST_NEEDLE")
            .expect("expected Frost Needle ability");
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "FROST_NEEDLE"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "POINT"
        );
        assert_eq!(ability.gameplay.requires_target, Some(false));

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Frost Needle should author spell delivery");
        assert_eq!(
            delivery
                .get("impact_delay_ms")
                .and_then(|value| value.as_u64()),
            Some(500)
        );
        assert_eq!(
            delivery.get("damage_type").and_then(|value| value.as_str()),
            Some("COLD")
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "SPELL_FROST_NEEDLE"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Frost Needle should author a delayed area-impact VFX cue");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "AREA_ORIGIN");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_FROST_NEEDLE_01"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
    }

    #[test]
    fn graveburst_stays_in_mortality_and_authors_delayed_area_impact_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_GRAVEBURST")
            .expect("expected Graveburst ability");
        assert_eq!(ability.spell_school_id.as_deref(), Some("MORTALITY"));
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "GRAVEBURST"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "POINT"
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "SPELL_GRAVEBURST"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Graveburst should author a delayed area-impact VFX cue");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "AREA_ORIGIN");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_GRAVEBURST_AREA_01"
        );
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "SPAWN_WORLD"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
        assert_eq!(cue.duration_ms, 0);
    }

    #[test]
    fn gravewake_stays_in_mortality_and_authors_moving_bone_wave_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_GRAVEWAKE")
            .expect("expected Gravewake ability");
        assert_eq!(ability.spell_school_id.as_deref(), Some("MORTALITY"));
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "GRAVEWAKE"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "SELF"
        );
        assert_eq!(ability.gameplay.requires_target, Some(false));

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Gravewake should author projectile delivery");
        assert_eq!(
            delivery
                .get("motion")
                .and_then(|motion| motion.get("kind"))
                .and_then(|kind| kind.as_str()),
            Some("TRAVELING_AREA")
        );
        assert_eq!(
            delivery
                .get("terrain_conforming")
                .and_then(|value| value.as_bool()),
            Some(true)
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_GRAVEWAKE"
                    && normalize_identifier(cue.vfx_role.as_str()) == "PROJECTILE_BODY"
            })
            .expect("Gravewake should author a projectile-body VFX cue");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "CASTER");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_GRAVEWAKE_BONE_WAVE_01"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "UNTIL_TERMINAL_EVENT"
        );
    }

    #[test]
    fn necro_prison_stays_in_mortality_and_authors_movement_only_zone_contract() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_NECRO_PRISON")
            .expect("expected Necro Prison ability");
        assert_eq!(ability.spell_school_id.as_deref(), Some("MORTALITY"));
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "NECRO_PRISON"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "POINT"
        );
        assert_eq!(ability.gameplay.requires_target_los, Some(false));
        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Necro Prison should author a delivery contract");
        assert_eq!(
            delivery.get("kind").and_then(|value| value.as_str()),
            Some("NECRO_PRISON")
        );
        assert_eq!(
            delivery.get("radius").and_then(|value| value.as_f64()),
            Some(4.0)
        );
        assert!(delivery.get("block_behavior").is_none());
        assert!(catalog
            .combat_vfx_cues
            .iter()
            .all(|cue| { normalize_identifier(cue.owner_id.as_str()) != "SPELL_NECRO_PRISON" }));
    }

    #[test]
    fn blood_offering_stays_in_mortality_and_authors_health_for_mana_contract() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_BLOOD_OFFERING")
            .expect("expected Blood Offering ability");
        assert_eq!(ability.spell_school_id.as_deref(), Some("MORTALITY"));
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "BLOOD_OFFERING"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "SELF"
        );
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(ability.gameplay.primary_resource_gain_on_cast, 50.0);
        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Blood Offering should author a delivery contract");
        assert_eq!(
            delivery.get("kind").and_then(|value| value.as_str()),
            Some("SELF_RESOURCE")
        );
        assert_eq!(
            delivery.get("health_cost").and_then(|value| value.as_i64()),
            Some(20)
        );
        assert_eq!(
            delivery
                .get("resource_gain_kind")
                .and_then(|value| value.as_str()),
            Some("MANA")
        );
    }

    #[test]
    fn meteor_vfx_uses_cast_glow_travel_body_and_impact_prefab() {
        let catalog = progression_catalog();
        let meteor_cues: Vec<_> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| normalize_identifier(cue.owner_id.as_str()) == "SPELL_METEOR")
            .collect();

        assert_eq!(
            meteor_cues.len(),
            3,
            "Meteor should author one cast glow, one travel body, and one impact cue"
        );

        let cast_glow = meteor_cues
            .iter()
            .find(|cue| normalize_identifier(cue.trigger.as_str()) == "SPELL_CAST")
            .expect("Meteor should author a charged cast-glow VFX cue");
        assert_eq!(normalize_identifier(cast_glow.anchor.as_str()), "LEFT_HAND");
        assert_eq!(
            normalize_identifier(cast_glow.lifecycle.as_str()),
            "UNTIL_RELEASE_EVENT"
        );

        let travel = meteor_cues
            .iter()
            .find(|cue| normalize_identifier(cue.vfx_role.as_str()) == "TRAVEL_BODY")
            .expect("Meteor should author a release travel body VFX cue");
        assert_eq!(
            normalize_identifier(travel.trigger.as_str()),
            "SPELL_RELEASE"
        );
        assert_eq!(normalize_identifier(travel.anchor.as_str()), "ORIGIN");
        assert_eq!(
            normalize_identifier(travel.vfx_id.as_str()),
            "VFX_METEOR_HEAD_01"
        );
        assert_eq!(
            normalize_identifier(travel.lifecycle.as_str()),
            "UNTIL_TERMINAL_EVENT"
        );

        let cue = meteor_cues
            .iter()
            .find(|cue| normalize_identifier(cue.vfx_id.as_str()) == "VFX_METEOR_01")
            .expect("Meteor should author an impact VFX cue");
        assert_eq!(normalize_identifier(cue.trigger.as_str()), "SPELL_IMPACT");
        assert_eq!(normalize_identifier(cue.vfx_id.as_str()), "VFX_METEOR_01");
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
    }

    fn assert_grid_slot_id(slot_id: &str) {
        let mut parts = slot_id.split('_');
        let prefix = parts.next();
        assert!(
            matches!(prefix, Some("slot") | Some("fixed") | Some("discipline")),
            "slot id '{slot_id}' must start with slot_, fixed_, or discipline_"
        );
        assert!(parts
            .next()
            .and_then(|part| part.parse::<u32>().ok())
            .is_some());
        if prefix == Some("discipline") {
            assert_eq!(parts.next(), None);
            return;
        }
        assert!(parts
            .next()
            .and_then(|part| part.parse::<u32>().ok())
            .is_some());
        assert_eq!(parts.next(), None);
    }

    #[test]
    fn authored_ability_actor_scopes_are_valid() {
        for ability in &progression_catalog().abilities {
            assert!(matches!(
                super::validated_ability_actor_scope(
                    ability.ability_id.as_str(),
                    ability.actor_scope.as_str(),
                )
                .as_str(),
                "PLAYER" | "NPC" | "BOTH"
            ));
        }
    }

    #[test]
    fn npc_spell_action_resolves_its_authored_ability() {
        assert_eq!(
            super::authored_npc_spell_ability_id("skeleton_wizard_frost_bolt"),
            Some("NPC_SKELETON_WIZARD_FROST_BOLT")
        );
        assert_eq!(super::authored_npc_spell_ability_id("FIREBALL"), None);
    }

    #[test]
    #[should_panic(expected = "must define actor_scope as PLAYER, NPC, or BOTH")]
    fn unknown_ability_actor_scope_is_rejected() {
        super::validated_ability_actor_scope("TEST_ABILITY", "COMPANION");
    }

    #[test]
    fn progression_abilities_author_applies_stagger_explicitly() {
        let value: serde_json::Value =
            serde_json::from_str(super::PROGRESSION_CATALOG_JSON).expect("catalog json must parse");
        let abilities = value
            .get("abilities")
            .and_then(serde_json::Value::as_array)
            .expect("abilities must be an array");

        for ability in abilities {
            let object = ability.as_object().expect("ability must be an object");
            let ability_kind = json_ability_gameplay_kind(object);
            let gameplay = object
                .get("gameplay")
                .and_then(serde_json::Value::as_object)
                .expect("gameplay must be an object");
            if ability_kind != "MELEE" {
                assert!(
                    !gameplay.contains_key("applies_stagger"),
                    "non-melee ability should not author melee applies_stagger: {object:?}"
                );
                continue;
            }
            assert!(
                gameplay.contains_key("applies_stagger"),
                "ability is missing applies_stagger: {object:?}"
            );
        }
    }

    #[test]
    fn progression_catalog_runtime_validation_accepts_current_authoring() {
        super::validate_ability_catalog();
    }

    #[test]
    fn compiled_progression_removes_only_vfx_authoring_ownership() {
        let mut authored: serde_json::Value =
            serde_json::from_str(include_str!("progression_catalog.shared.json")).unwrap();
        let runtime: serde_json::Value =
            serde_json::from_str(super::PROGRESSION_CATALOG_JSON).unwrap();
        let cues = authored["combat_vfx_cues"].as_array_mut().unwrap();
        assert!(!cues.is_empty());
        for cue in cues {
            let fields = cue.as_object_mut().unwrap();
            assert!(fields.remove("authoring_mode").is_some());
            fields.remove("authoring_reason");
        }
        assert_eq!(
            runtime, authored,
            "runtime projection must preserve every other field and array order"
        );
    }

    #[test]
    fn mana_regen_scales_with_insight_for_starter_ring_target() {
        let value: serde_json::Value =
            serde_json::from_str(super::PROGRESSION_CATALOG_JSON).expect("catalog json must parse");
        let resources = value
            .get("resources")
            .and_then(serde_json::Value::as_array)
            .expect("resources must be an array");
        let mana = resources
            .iter()
            .find(|resource| {
                resource
                    .get("resource_kind")
                    .and_then(serde_json::Value::as_str)
                    == Some(RESOURCE_KIND_MANA)
            })
            .expect("mana resource must be authored");

        assert_eq!(
            mana.get("regen_per_insight")
                .and_then(serde_json::Value::as_f64),
            Some(0.2)
        );
    }

    #[test]
    fn progression_melee_abilities_author_required_gameplay_fields_only_on_melee_rows() {
        let value: serde_json::Value =
            serde_json::from_str(super::PROGRESSION_CATALOG_JSON).expect("catalog json must parse");
        let abilities = value
            .get("abilities")
            .and_then(serde_json::Value::as_array)
            .expect("abilities must be an array");
        let melee_fields = [
            "base_damage",
            "applies_stagger",
            "range",
            "cooldown_ms",
            "uses_global_cooldown",
            "parry_behavior",
            "block_behavior",
            "airborne_targeting_mode",
        ];
        let melee_only_fields = [
            "base_damage",
            "target_health_damage_scaling",
            "applies_stagger",
            "range",
            "minimum_range",
            "parry_behavior",
            "block_behavior",
            "airborne_targeting_mode",
            "gap_close",
            "melee_impact_area",
            "melee_targeting",
        ];

        for ability in abilities {
            let object = ability.as_object().expect("ability must be an object");
            let ability_kind = json_ability_gameplay_kind(object);
            let gameplay = object
                .get("gameplay")
                .and_then(serde_json::Value::as_object)
                .expect("gameplay must be an object");
            match ability_kind {
                "MELEE" => {
                    for field in melee_fields {
                        assert!(
                            gameplay.contains_key(field),
                            "melee ability is missing {field}: {object:?}"
                        );
                    }
                }
                "SPELL"
                | "MOVEMENT"
                | "AUTO_ATTACK_REPLACEMENT"
                | "PASSIVE"
                | ABILITY_KIND_COMBAT_MODE_TOGGLE => {
                    for field in melee_only_fields {
                        assert!(
                            !gameplay.contains_key(field),
                            "non-melee ability should not author melee field {field}: {object:?}"
                        );
                    }
                }
                other => panic!("unsupported gameplay.kind '{other}'"),
            }
        }
    }

    #[test]
    fn authored_gap_closers_are_valid_melee_gameplay() {
        let catalog = progression_catalog();
        let supported_kinds = [
            "LINEAR",
            "LEAP",
            "TELEPORT",
            "TELEPORT_BEHIND",
            "TELEPORT_BEHIND_TARGET_DISABLED",
        ];
        let supported_destinations = [
            "NEAREST_CONTACT_POINT",
            "BEHIND_TARGET",
            "TARGET_SIDE_LEFT",
            "TARGET_SIDE_RIGHT",
            "CURRENT_LINE",
        ];
        let supported_collision = ["REQUIRE_CLEAR_PATH", "STOP_AT_BLOCK"];
        let gap_closers: Vec<_> = catalog
            .abilities
            .iter()
            .filter(|ability| ability.gameplay.gap_close.is_some())
            .collect();

        for ability in gap_closers {
            assert_eq!(
                ability_gameplay_kind(ability),
                "MELEE",
                "gap_close belongs on melee abilities only: {}",
                ability.ability_id
            );
            let gap_close = ability
                .gameplay
                .gap_close
                .as_ref()
                .expect("gap_close should exist");
            let kind = normalize_identifier(gap_close.kind.as_str());
            assert!(
                supported_kinds.contains(&kind.as_str()),
                "{} has unsupported gap_close.kind {}",
                ability.ability_id,
                gap_close.kind
            );
            let destination = normalize_identifier(gap_close.destination.as_str());
            assert!(
                supported_destinations.contains(&destination.as_str()),
                "{} has unsupported gap_close.destination {}",
                ability.ability_id,
                gap_close.destination
            );
            let collision_policy = normalize_identifier(gap_close.collision_policy.as_str());
            assert!(
                supported_collision.contains(&collision_policy.as_str()),
                "{} has unsupported gap_close.collision_policy {}",
                ability.ability_id,
                gap_close.collision_policy
            );
            if !matches!(
                kind.as_str(),
                "TELEPORT" | "TELEPORT_BEHIND" | "TELEPORT_BEHIND_TARGET_DISABLED"
            ) {
                assert!(
                    gap_close.speed.unwrap_or(0.0) > 0.0,
                    "{} non-teleport gap closer must define positive speed",
                    ability.ability_id
                );
            }
            assert!(
                gap_close.arrival_buffer >= 0.0,
                "{} gap_close.arrival_buffer must be non-negative",
                ability.ability_id
            );
            let default_player_arrival_distance =
                DEFAULT_HIT_RADIUS + DEFAULT_HIT_RADIUS + gap_close.arrival_buffer;
            assert!(
                (default_player_arrival_distance - GAP_CLOSE_TARGET_ARRIVAL_DISTANCE_METERS).abs()
                    < 0.001,
                "{} gap_close must arrive {:.2}m center-to-center for default player capsules; got {:.2}m from arrival_buffer {:.2}",
                ability.ability_id,
                GAP_CLOSE_TARGET_ARRIVAL_DISTANCE_METERS,
                default_player_arrival_distance,
                gap_close.arrival_buffer
            );
            assert!(
                gap_close.arrival_epsilon >= 0.0,
                "{} gap_close.arrival_epsilon must be non-negative",
                ability.ability_id
            );
            assert!(
                gap_close.impact_range > 0.0,
                "{} gap_close.impact_range must be positive",
                ability.ability_id
            );
            assert!(
                gap_close.impact_range < ability.gameplay.range.unwrap_or(0.0),
                "{} gap_close.impact_range must stay below acquisition range",
                ability.ability_id
            );
            assert!(
                gap_close.require_arrival_for_swing,
                "{} V1 gap closers must require arrival before swing",
                ability.ability_id
            );
        }
    }

    #[test]
    fn shroud_expires_at_its_authored_five_second_boundary() {
        let changed_at = Timestamp::UNIX_EPOCH + Duration::from_secs(10);
        assert!(!shroud_has_expired(
            changed_at,
            changed_at + Duration::from_millis(4_999),
            5_000,
        ));
        assert!(shroud_has_expired(
            changed_at,
            changed_at + Duration::from_secs(5),
            5_000,
        ));
    }

    #[test]
    fn dagger_melee_abilities_use_dagger_owned_strike_ids() {
        let catalog = progression_catalog();
        for (ability_id, action_id) in [
            ("DAGGER_QUICK_CUT", "DAGGER_QUICK_CUT"),
            ("DAGGER_SLICE", "DAGGER_SLICE"),
            ("DAGGER_DASHING_CUT", "DAGGER_DASHING_CUT"),
            ("DAGGER_ROUNDHOUSE", "DAGGER_ROUNDHOUSE"),
            ("DAGGER_GUT_RIPPER", "DAGGER_COMBO_ATTACK_04_01"),
            ("DAGGER_SEVER", "DAGGER_COMBO_ATTACK_03_03"),
            ("DAGGER_DEEP_CUT", "DAGGER_COMBO_ATTACK_04_04"),
            ("DAGGER_HEMORRHAGE", "DAGGER_COMBO_ATTACK_01_03"),
            ("DAGGER_SPINNING_SLASH", "DAGGER_COMBO_ATTACK_03_01"),
            ("DAGGER_BLADE_FLURRY", "DAGGER_COMBO_ATTACK_02_02"),
            ("DAGGER_DEADLY_FLOURISH", "DAGGER_DEADLY_FLOURISH"),
            ("DAGGER_PURSUE", "DAGGER_COMBO_ATTACK_01_04"),
            ("DAGGER_DOWNWARD_SLASH", "DAGGER_DOWNWARD_SLASH"),
            ("DAGGER_NERVE_STRIKE", "DAGGER_NERVE_STRIKE"),
            ("DAGGER_COUP_DE_GRACE", "DAGGER_COUP_DE_GRACE"),
            ("DAGGER_PRECISION_STRIKE", "DAGGER_PRECISION_STRIKE"),
            ("DAGGER_EVISCERATE", "DAGGER_EVISCERATE"),
            ("DAGGER_VITAL_STRIKE", "DAGGER_VITAL_STRIKE"),
            ("DAGGER_DEATH_CROSS", "DAGGER_DEATH_CROSS"),
            ("DAGGER_DIVING_STRIKE", "DAGGER_DIVING_STRIKE"),
            ("DAGGER_DISEMBOWEL", "DAGGER_DISEMBOWEL"),
            ("DAGGER_FLAY", "DAGGER_FLAY"),
            ("DAGGER_QUICKENING_STRIKE", "DAGGER_QUICKENING_STRIKE"),
        ] {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| normalize_identifier(ability.ability_id.as_str()) == ability_id)
                .unwrap_or_else(|| panic!("{ability_id} must exist"));

            assert_eq!(
                normalize_identifier(ability.combat_discipline_id.as_deref().unwrap_or_default()),
                COMBAT_PROFILE_DAGGERS
            );
            assert_eq!(ability_gameplay_kind(ability), "MELEE");
            assert_eq!(normalize_identifier(ability.action_id.as_str()), action_id);
            assert!(profile_supports_action_reference(
                COMBAT_PROFILE_DAGGERS,
                &AuthoredActionId::new(action_id)
            ));
        }
    }

    #[test]
    fn dagger_channel_attack_authors_movement_canceling_runtime() {
        assert_eq!(
            melee_channel_for_ability_id("DAGGER_FLAY"),
            Some(MeleeChannelRuntime {
                duration_ms: 2500,
                first_tick_delay_ms: 44,
                tick_interval_ms: 333,
                cancel_on_movement: true,
                use_authored_hit_windows: false,
                holdable: false,
                resource_cost_per_release: 0.0,
                resource_kind_per_release: "",
            })
        );
    }

    #[test]
    fn archer_triple_shot_authors_a_three_window_movement_cancelable_sequence() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "ARCHER_TRIPLE_SHOT")
            .expect("ARCHER_TRIPLE_SHOT must exist");

        assert_eq!(
            ability.combat_discipline_id.as_deref(),
            Some(COMBAT_PROFILE_ARCHER_BOW)
        );
        assert_eq!(ability.action_id, "ARCHER_TRIPLE_SHOT");
        assert_eq!(ability.gameplay.base_damage, Some(48));
        assert_eq!(
            melee_channel_for_ability_id("ARCHER_TRIPLE_SHOT"),
            Some(MeleeChannelRuntime {
                duration_ms: 2743,
                first_tick_delay_ms: 0,
                tick_interval_ms: 0,
                cancel_on_movement: true,
                use_authored_hit_windows: true,
                holdable: false,
                resource_cost_per_release: 0.0,
                resource_kind_per_release: "",
            })
        );
    }

    #[test]
    fn archer_backstep_attacks_reuse_the_authored_timed_movement_path() {
        for ability_id in ["ARCHER_BACKSTEP", "ARCHER_DISENGAGE"] {
            let movement = melee_timed_movement_for_ability_id(ability_id)
                .unwrap_or_else(|| panic!("{ability_id} should author timed movement"));

            assert_eq!(movement.ability_id, ability_id);
            assert_eq!(movement.kind, "BACKSTEP");
            assert_eq!(movement.start_delay_ms, 220);
            assert_eq!(movement.direction, "BACKWARD");
            assert_eq!(movement.distance, 7.0);
            assert_eq!(movement.speed, 18.0);
            assert_eq!(movement.collision_policy, "STOP_AT_BLOCK");
            assert_eq!(movement.facing_policy, "FACE_START");

            let ability = progression_catalog()
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("{ability_id} ability should exist"));
            assert_eq!(
                ability.combat_discipline_id.as_deref(),
                Some(COMBAT_PROFILE_ARCHER_BOW)
            );
            assert_eq!(ability.action_id, ability_id);
            assert_eq!(ability.gameplay.cooldown_ms, Some(1600));
            assert_eq!(ability.gameplay.uses_global_cooldown, Some(true));
            assert_eq!(ability.gameplay.global_cooldown_ms, Some(650));
        }
    }

    #[test]
    fn archer_evasive_shot_authors_tunable_leap_and_immunity_window() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "ARCHER_EVASIVE_SHOT")
            .expect("ARCHER_EVASIVE_SHOT ability should exist");
        let leap = melee_evasive_leap_for_ability_id("ARCHER_EVASIVE_SHOT")
            .expect("ARCHER_EVASIVE_SHOT should author an evasive leap");

        assert_eq!(
            ability.combat_discipline_id.as_deref(),
            Some(COMBAT_PROFILE_ARCHER_BOW)
        );
        assert_eq!(ability.action_id, "ARCHER_EVASIVE_SHOT");
        assert_eq!(ability.gameplay.base_damage, Some(28));
        assert_eq!(ability.gameplay.cooldown_ms, Some(8000));
        assert_eq!(ability.gameplay.uses_global_cooldown, Some(true));
        assert_eq!(ability.gameplay.global_cooldown_ms, Some(650));
        assert_eq!(leap.duration_ms, 2000);
        assert_eq!(leap.arc_height, 2.25);
    }

    #[test]
    fn archer_heartseeker_authors_high_damage_stationary_auto_crit_shot() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "ARCHER_HEARTSEEKER")
            .expect("ARCHER_HEARTSEEKER ability should exist");

        assert_eq!(
            ability.combat_discipline_id.as_deref(),
            Some(COMBAT_PROFILE_ARCHER_BOW)
        );
        assert_eq!(ability.action_id, "ARCHER_HEARTSEEKER");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(ability.resource_cost, 25.0);
        assert_eq!(ability.gameplay.base_damage, Some(42));
        assert_eq!(ability.gameplay.range, Some(18.0));
        assert_eq!(ability.gameplay.cooldown_ms, Some(5_000));
        assert!(ability.gameplay.stationary_target_auto_crit);
        assert_eq!(ability.gameplay.stationary_target_window_ms, 250);
        assert!((ability.gameplay.stationary_target_max_displacement_meters - 0.05).abs() < 0.0001);
        assert!(profile_supports_action_reference(
            COMBAT_PROFILE_ARCHER_BOW,
            &AuthoredActionId::new("ARCHER_HEARTSEEKER")
        ));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation) == "ABILITY:ARCHER_HEARTSEEKER"
        }));
    }

    #[test]
    fn close_range_weapon_melee_reach_preserves_area_and_gap_close_ranges() {
        const CLOSE_MELEE_RANGE_METERS: f32 = 1.8;

        let catalog = progression_catalog();
        let close_range_profiles = [
            COMBAT_PROFILE_DAGGERS,
            COMBAT_PROFILE_SWORD_AND_SHIELD,
            COMBAT_PROFILE_TWO_HANDED_SWORD,
        ];
        let area_ranges = [
            ("DAGGER_SPINNING_SLASH", "CASTER_RADIUS", 3.25),
            ("WARRIOR_CATACLYSM", "CASTER_CONE", 11.5),
            ("WARRIOR_WHIRLWIND", "CASTER_RADIUS", 3.25),
            ("PALADIN_SACRED_THRUST", "CASTER_RECTANGLE", 5.0),
        ];
        let gap_close_ranges = [
            ("DAGGER_DASHING_CUT", 12.0, Some(4.0)),
            ("DAGGER_PURSUE", 12.0, Some(4.0)),
            ("DAGGER_COUP_DE_GRACE", 12.0, None),
            ("DAGGER_DEATH_CROSS", 12.0, None),
            ("DAGGER_DIVING_STRIKE", 12.0, None),
            ("WARRIOR_EARTHSHATTER", 8.0, None),
            ("WARRIOR_CHARGE", 18.0, Some(5.0)),
            ("WARRIOR_IMPALE", 18.0, Some(5.0)),
            ("PALADIN_CHARGE", 18.0, Some(5.0)),
            ("PALADIN_AVENGE", 18.0, Some(5.0)),
            ("PALADIN_AIR_TO_GROUND_1", 18.0, Some(5.0)),
            ("PALADIN_AIR_TO_GROUND_3", 18.0, Some(0.0)),
        ];

        let mut targeted_count = 0;
        let mut area_count = 0;
        let mut gap_close_count = 0;
        for ability in catalog.abilities.iter().filter(|ability| {
            close_range_profiles.contains(
                &normalize_identifier(ability.combat_discipline_id.as_deref().unwrap_or_default())
                    .as_str(),
            ) && ability_gameplay_kind(ability) == "MELEE"
        }) {
            let targeting = resolved_melee_targeting_for_catalog(&ability.gameplay);
            if let Some(gap_close) = ability.gameplay.gap_close.as_ref() {
                let (_, expected_range, expected_minimum_range) = gap_close_ranges
                    .iter()
                    .find(|(ability_id, _, _)| *ability_id == ability.ability_id)
                    .unwrap_or_else(|| panic!("unexpected gap closer {}", ability.ability_id));
                assert_eq!(
                    ability.gameplay.range,
                    Some(*expected_range),
                    "{} gap-close acquisition range changed",
                    ability.ability_id
                );
                assert_eq!(
                    ability.gameplay.minimum_range, *expected_minimum_range,
                    "{} gap-close minimum range changed",
                    ability.ability_id
                );
                assert_eq!(
                    gap_close.impact_range, CLOSE_MELEE_RANGE_METERS,
                    "{} gap-close impact reach must match close melee reach",
                    ability.ability_id
                );
                assert_eq!(
                    gap_close.arrival_buffer, 1.44,
                    "{} gap-close arrival distance changed",
                    ability.ability_id
                );
                assert_eq!(
                    gap_close.arrival_epsilon, 0.05,
                    "{} gap-close arrival tolerance changed",
                    ability.ability_id
                );

                let intended_arrival_center_distance =
                    DEFAULT_HIT_RADIUS + DEFAULT_HIT_RADIUS + gap_close.arrival_buffer;
                assert_eq!(intended_arrival_center_distance, 2.0);
                let furthest_arrived_center_distance =
                    intended_arrival_center_distance + gap_close.arrival_epsilon;
                let impact_center_reach = gap_close.impact_range + DEFAULT_HIT_RADIUS;
                assert!(
                    furthest_arrived_center_distance <= impact_center_reach,
                    "{} can arrive at {:.2}m, outside impact reach {:.2}m",
                    ability.ability_id,
                    furthest_arrived_center_distance,
                    impact_center_reach
                );
                gap_close_count += 1;
            } else if targeting.kind == "TARGET" {
                assert_eq!(
                    ability.gameplay.range,
                    Some(CLOSE_MELEE_RANGE_METERS),
                    "{} must use close targeted melee reach",
                    ability.ability_id
                );
                targeted_count += 1;
            } else {
                let (_, expected_kind, expected_range) = area_ranges
                    .iter()
                    .find(|(ability_id, _, _)| *ability_id == ability.ability_id)
                    .unwrap_or_else(|| panic!("unexpected area melee {}", ability.ability_id));
                assert_eq!(targeting.kind, *expected_kind);
                assert_eq!(ability.gameplay.range, Some(*expected_range));
                area_count += 1;
            }
        }

        assert!(targeted_count > 0);
        assert_eq!(area_count, area_ranges.len());
        assert_eq!(gap_close_count, gap_close_ranges.len());

        for profile_id in close_range_profiles {
            let profile_auto_attacks: Vec<_> = catalog
                .auto_attacks
                .iter()
                .filter(|attack| {
                    normalize_identifier(attack.combat_discipline_id.as_str()) == profile_id
                })
                .collect();
            assert!(!profile_auto_attacks.is_empty(), "{profile_id} auto attack");
            assert!(profile_auto_attacks
                .iter()
                .all(|attack| attack.range == CLOSE_MELEE_RANGE_METERS));
        }

        let dread_strike = catalog
            .auto_attack_replacements
            .iter()
            .find(|replacement| replacement.replacement_id == "WARRIOR_DREAD_STRIKE")
            .expect("WARRIOR_DREAD_STRIKE auto-attack replacement must exist");
        assert_eq!(dread_strike.range, CLOSE_MELEE_RANGE_METERS);
    }

    #[test]
    fn dagger_diving_strike_authors_required_arrival_gap_close() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_DIVING_STRIKE")
            .expect("DAGGER_DIVING_STRIKE must exist");
        let gap_close = ability
            .gameplay
            .gap_close
            .as_ref()
            .expect("DAGGER_DIVING_STRIKE must author a gap close");

        assert_eq!(normalize_identifier(gap_close.kind.as_str()), "LEAP");
        assert_eq!(
            normalize_identifier(gap_close.destination.as_str()),
            "NEAREST_CONTACT_POINT"
        );
        assert_eq!(ability.gameplay.minimum_range, None);
        assert!(gap_close.require_arrival_for_swing);
        assert!(gap_close.activate_outside_impact_reach);
    }

    #[test]
    fn selected_dagger_gap_closers_are_the_only_authored_leaps() {
        let leap_abilities: HashSet<_> = progression_catalog()
            .abilities
            .iter()
            .filter_map(|ability| {
                let gap_close = ability.gameplay.gap_close.as_ref()?;
                (normalize_identifier(gap_close.kind.as_str()) == "LEAP")
                    .then(|| ability.ability_id.as_str())
            })
            .collect();

        assert_eq!(
            leap_abilities,
            HashSet::from([
                "DAGGER_PURSUE",
                "DAGGER_DEATH_CROSS",
                "DAGGER_DIVING_STRIKE",
            ])
        );
    }

    #[test]
    fn dagger_nerve_strike_authors_four_second_stun() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_NERVE_STRIKE")
            .expect("DAGGER_NERVE_STRIKE must exist");

        assert_eq!(
            ability.combat_discipline_id.as_deref(),
            Some(COMBAT_PROFILE_DAGGERS)
        );
        assert_eq!(ability.action_id, "DAGGER_NERVE_STRIKE");
        assert_eq!(ability.display_name, "Nerve Strike");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(
            melee_impact_effects_for_ability_id("DAGGER_NERVE_STRIKE"),
            vec![MeleeImpactEffectRuntime::ApplyStatus {
                status: StatusApplication::new(
                    StatusPayload::Stun,
                    Duration::from_secs(4),
                    Some("DAGGER_NERVE_STRIKE_STUN".to_string()),
                    StatusStackGroupDefault::ActionSuffix("STUN"),
                    1,
                    StackPolicy::Refresh,
                ),
            }]
        );
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation) == "ABILITY:DAGGER_NERVE_STRIKE"
                && presentation.display_name == "Nerve Strike"
        }));
    }

    #[test]
    fn dagger_gut_ripper_applies_physical_bleed() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_GUT_RIPPER")
            .expect("DAGGER_GUT_RIPPER must exist");

        assert_eq!(ability.action_id, "DAGGER_COMBO_ATTACK_04_01");
        assert_eq!(ability.gameplay.applies_stagger, Some(false));
        assert_eq!(
            melee_impact_effects_for_ability_id("DAGGER_GUT_RIPPER"),
            vec![MeleeImpactEffectRuntime::ApplyStatus {
                status: StatusApplication::new(
                    StatusPayload::Dot {
                        tick_damage: 3,
                        damage_type: crate::combat::DamageType::Physical,
                        tick_interval: Duration::from_secs(1),
                    },
                    Duration::from_millis(6000),
                    Some("BLEED:{SOURCE}".to_string()),
                    StatusStackGroupDefault::InstanceScopedActionSuffix("DOT"),
                    10,
                    StackPolicy::AddStackEscalatingDecay,
                )
                .with_dispel_types(vec![StatusDispelType::Bleed]),
            }]
        );
    }

    #[test]
    fn dagger_sever_stacks_one_shared_slow_duration() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_SEVER")
            .expect("DAGGER_SEVER must exist");

        assert_eq!(ability.action_id, "DAGGER_COMBO_ATTACK_03_03");
        assert_eq!(ability.resource_cost, 20.0);
        assert_eq!(ability.gameplay.base_damage, Some(24));
        assert_eq!(ability.gameplay.cooldown_ms, Some(650));
        assert_eq!(
            melee_impact_effects_for_ability_id("DAGGER_SEVER"),
            vec![MeleeImpactEffectRuntime::ApplyStatus {
                status: StatusApplication::new(
                    StatusPayload::Slow { slow_pct: 0.25 },
                    Duration::from_secs(6),
                    Some("SEVER".to_string()),
                    StatusStackGroupDefault::ActionSuffix("SLOW"),
                    2,
                    StackPolicy::AddStackRefresh,
                )
                .with_dispel_types(vec![StatusDispelType::Physical]),
            }]
        );
    }

    #[test]
    fn dagger_deep_cut_is_a_low_cost_random_bleed_refresh_attack() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_DEEP_CUT")
            .expect("DAGGER_DEEP_CUT must exist");

        assert_eq!(ability.action_id, "DAGGER_COMBO_ATTACK_04_04");
        assert_eq!(ability.resource_cost, 10.0);
        assert_eq!(ability.gameplay.base_damage, Some(12));
        assert_eq!(ability.gameplay.cooldown_ms, Some(650));
        assert_eq!(
            melee_impact_effects_for_ability_id("DAGGER_DEEP_CUT"),
            vec![MeleeImpactEffectRuntime::RefreshRandomStatus {
                hit_index: 0,
                polarity: Some(StatusPolarity::Debuff),
                dispel_types: vec![StatusDispelType::Bleed],
            }]
        );
    }

    #[test]
    fn dagger_hemorrhage_replaces_the_legacy_gut_ripper_debuff_contract() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_HEMORRHAGE")
            .expect("DAGGER_HEMORRHAGE must exist");

        assert_eq!(ability.action_id, "DAGGER_COMBO_ATTACK_01_03");
        assert_eq!(ability.resource_cost, 20.0);
        assert_eq!(ability.gameplay.base_damage, Some(20));
        assert_eq!(ability.gameplay.cooldown_ms, Some(900));
        assert_eq!(
            melee_impact_effects_for_ability_id("DAGGER_HEMORRHAGE"),
            vec![MeleeImpactEffectRuntime::ApplyStatusOnHit {
                hit_index: 0,
                status: StatusApplication::new(
                    StatusPayload::Hemorrhage {
                        modifier_scalar: 1.0,
                    },
                    Duration::from_secs(8),
                    Some("HEMORRHAGE".to_string()),
                    StatusStackGroupDefault::EffectKind,
                    1,
                    StackPolicy::Refresh,
                )
                .with_dispel_types(vec![StatusDispelType::Bleed]),
            }]
        );
        assert_eq!(
            StatusEffectKind::from_wire("HEMORRHAGING"),
            Some(StatusEffectKind::Hemorrhaging),
            "legacy persisted Hemorrhaging rows must remain readable"
        );
        assert!(authored_status_presentation_ids(progression_catalog()).contains("HEMORRHAGING"));
    }

    #[test]
    fn dagger_spinning_slash_authors_targetless_radius_melee() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_SPINNING_SLASH")
            .expect("DAGGER_SPINNING_SLASH must exist");

        assert_eq!(ability.action_id, "DAGGER_COMBO_ATTACK_03_01");
        let targeting = resolved_melee_targeting_for_catalog(&ability.gameplay);
        assert_eq!(targeting.kind, "CASTER_RADIUS");
        assert!(!targeting.requires_target);
        assert_eq!(targeting.radius, 3.25);
        assert_eq!(targeting.range, 3.25);
    }

    #[test]
    fn dagger_pursue_authors_leap_gap_close() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_PURSUE")
            .expect("DAGGER_PURSUE must exist");
        let gap_close = ability
            .gameplay
            .gap_close
            .as_ref()
            .expect("DAGGER_PURSUE must author gap_close");

        assert_eq!(ability.action_id, "DAGGER_COMBO_ATTACK_01_04");
        assert_eq!(normalize_identifier(gap_close.kind.as_str()), "LEAP");
        assert_eq!(
            normalize_identifier(gap_close.destination.as_str()),
            "NEAREST_CONTACT_POINT"
        );
        assert_eq!(gap_close.speed, Some(24.0));
        assert_eq!(
            normalize_identifier(gap_close.collision_policy.as_str()),
            "STOP_AT_BLOCK"
        );
        assert!(gap_close.require_arrival_for_swing);
        assert!(!gap_close.activate_outside_impact_reach);
    }

    #[test]
    fn dagger_coup_de_grace_authors_conditional_instant_behind_teleport() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_COUP_DE_GRACE")
            .expect("DAGGER_COUP_DE_GRACE must exist");
        let gap_close = ability
            .gameplay
            .gap_close
            .as_ref()
            .expect("DAGGER_COUP_DE_GRACE must author gap_close");

        assert_eq!(ability.action_id, "DAGGER_COUP_DE_GRACE");
        assert_eq!(
            normalize_identifier(gap_close.kind.as_str()),
            "TELEPORT_BEHIND_TARGET_DISABLED"
        );
        assert_eq!(
            normalize_identifier(gap_close.destination.as_str()),
            "BEHIND_TARGET"
        );
        assert_eq!(gap_close.impact_range, 1.8);
        assert_eq!(
            normalize_identifier(gap_close.collision_policy.as_str()),
            "REQUIRE_CLEAR_PATH"
        );
        assert!(gap_close.require_arrival_for_swing);
        assert!(!gap_close.requires_target_facing);
        assert!(!gap_close.activate_outside_impact_reach);
    }

    #[test]
    fn dagger_death_cross_authors_leap_gap_close() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_DEATH_CROSS")
            .expect("DAGGER_DEATH_CROSS must exist");
        let gap_close = ability
            .gameplay
            .gap_close
            .as_ref()
            .expect("DAGGER_DEATH_CROSS must author gap_close");

        assert_eq!(ability.gameplay.range, Some(12.0));
        assert_eq!(ability.gameplay.minimum_range, None);
        assert_eq!(normalize_identifier(gap_close.kind.as_str()), "LEAP");
        assert_eq!(
            normalize_identifier(gap_close.destination.as_str()),
            "NEAREST_CONTACT_POINT"
        );
        assert_eq!(gap_close.speed, Some(24.0));
        assert_eq!(gap_close.impact_range, 1.8);
        assert_eq!(
            normalize_identifier(gap_close.collision_policy.as_str()),
            "STOP_AT_BLOCK"
        );
        assert!(gap_close.require_arrival_for_swing);
        assert!(!gap_close.requires_target_facing);
        assert!(gap_close.activate_outside_impact_reach);
    }

    #[test]
    fn dagger_animation_set_does_not_use_foreign_melee_ids() {
        let asset_contents = animation_set_asset_for_combat_profile(COMBAT_PROFILE_DAGGERS);

        assert!(
            !asset_contents.contains("SWORD_AND_SHIELD"),
            "Dagger animation set must not use SwordAndShield melee ids"
        );
        assert!(
            !asset_contents.contains("PALADIN_"),
            "Dagger animation set must not use Paladin melee ids"
        );
    }

    #[test]
    fn melee_stagger_application_is_authored_per_ability_action() {
        let hew = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_HEW")
            .expect("WARRIOR_HEW must exist");
        let cleave = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_CLEAVE")
            .expect("WARRIOR_CLEAVE must exist");

        assert_eq!(hew.gameplay.applies_stagger, Some(true));
        assert_eq!(cleave.gameplay.applies_stagger, Some(true));
        let hew_followup = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_HEW_2")
            .expect("WARRIOR_HEW_2 must exist");
        assert_eq!(hew_followup.gameplay.applies_stagger, Some(false));
    }

    #[test]
    fn charge_abilities_author_melee_gap_close() {
        let catalog = progression_catalog();

        for ability_id in ["WARRIOR_CHARGE", "WARRIOR_IMPALE", "PALADIN_CHARGE"] {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| normalize_identifier(ability.ability_id.as_str()) == ability_id)
                .expect("charge ability must exist");
            assert_eq!(
                ability_gameplay_kind(ability),
                "MELEE",
                "charge ability '{}' must be a melee ability",
                ability.ability_id
            );
            assert_eq!(ability.gameplay.base_damage, Some(32));
            assert_eq!(ability.gameplay.applies_stagger, Some(false));
            assert_eq!(ability.gameplay.range, Some(18.0));
            assert_eq!(ability.gameplay.minimum_range, Some(5.0));
            assert_eq!(ability.gameplay.cooldown_ms, Some(1600));
            assert_eq!(ability.gameplay.uses_global_cooldown, Some(true));
            assert_eq!(
                normalize_identifier(
                    ability
                        .gameplay
                        .airborne_targeting_mode
                        .as_deref()
                        .unwrap_or_default()
                ),
                "ANY_TARGET"
            );
            assert_eq!(
                normalize_identifier(
                    ability
                        .gameplay
                        .parry_behavior
                        .as_deref()
                        .unwrap_or_default()
                ),
                "PARRYABLE"
            );
            assert_eq!(
                normalize_identifier(
                    ability
                        .gameplay
                        .block_behavior
                        .as_deref()
                        .unwrap_or_default()
                ),
                "BLOCKABLE"
            );
            let gap_close = ability
                .gameplay
                .gap_close
                .as_ref()
                .expect("charge ability must author a melee gap_close");
            assert_eq!(normalize_identifier(gap_close.kind.as_str()), "LINEAR");
            assert_eq!(
                normalize_identifier(gap_close.destination.as_str()),
                "NEAREST_CONTACT_POINT"
            );
            assert_eq!(gap_close.speed, Some(23.0));
            assert_eq!(gap_close.arrival_buffer, 1.44);
            assert_eq!(gap_close.arrival_epsilon, 0.05);
            assert_eq!(gap_close.impact_range, 1.8);
            assert_eq!(
                normalize_identifier(gap_close.collision_policy.as_str()),
                "STOP_AT_BLOCK"
            );
            assert!(gap_close.require_arrival_for_swing);
            assert!(!gap_close.requires_target_facing);
            assert_eq!(
                melee_impact_effects_for_ability_id(ability_id),
                vec![MeleeImpactEffectRuntime::ApplyStatus {
                    status: StatusApplication::new(
                        StatusPayload::Stun,
                        std::time::Duration::from_millis(5000),
                        Some(format!("{ability_id}_STUN")),
                        StatusStackGroupDefault::ActionSuffix("STUN"),
                        1,
                        StackPolicy::Refresh,
                    ),
                }]
            );
        }
    }

    #[test]
    fn paladin_shield_bash_uses_light_combo_3_animation_and_purges_magic_buff() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_SHIELD_PUMMEL")
            .expect("PALADIN_SHIELD_PUMMEL must exist");

        assert_eq!(
            normalize_identifier(ability.combat_discipline_id.as_deref().unwrap_or_default()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability.display_name, "Shield Bash");
        assert_eq!(ability.action_id, "SWORD_AND_SHIELD_LIGHT_COMBO_3");
        assert!(profile_supports_action_reference(
            COMBAT_PROFILE_SWORD_AND_SHIELD,
            &AuthoredActionId::new(ability.action_id.as_str())
        ));
        assert_eq!(
            melee_impact_effects_for_ability_id("PALADIN_SHIELD_PUMMEL"),
            vec![MeleeImpactEffectRuntime::RemoveStatus {
                polarity: Some(crate::combat::StatusPolarity::Buff),
                dispel_types: vec![StatusDispelType::Magic],
                max_count: 1,
            }]
        );
    }

    #[test]
    fn paladin_blessed_shield_authors_orbit_spell() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_BLESSED_SHIELD")
            .expect("PALADIN_BLESSED_SHIELD must exist");
        assert_eq!(
            normalize_identifier(ability.combat_discipline_id.as_deref().unwrap_or_default()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability.action_id, "BLESSED_SHIELD");
        assert_eq!(ability.display_name, "Blessed Shield");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "SELF"
        );
        assert_eq!(ability.gameplay.requires_target, Some(false));
        assert_eq!(ability.gameplay.resource_cost, Some(0.0));

        let definition =
            spell_definition_by_str("BLESSED_SHIELD").expect("BLESSED_SHIELD spell definition");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::Projectile
        );
        assert_eq!(definition.damage, 18);
        assert_eq!(definition.damage_type, DamageType::Holy);
        let orbit = definition
            .secondary
            .projectile
            .as_ref()
            .expect("Blessed Shield should define projectile secondary tunables")
            .motion
            .orbit()
            .expect("Blessed Shield should use orbit-caster projectile motion");
        assert_eq!(orbit.projectile_count, 1);
        assert!((orbit.orbit_radius - 2.0).abs() < 0.0001);
        assert!((orbit.angular_speed_deg_per_sec - 180.0).abs() < 0.0001);
        assert!((orbit.lifetime_seconds - 10.0).abs() < 0.0001);
        assert_eq!(
            projectile_body_vfx_id_for_spell("PALADIN_BLESSED_SHIELD", "BLESSED_SHIELD", 0)
                .as_deref(),
            Some("VFX_BLESSED_SHIELD_PROJECTILE_01")
        );
    }

    #[test]
    fn paladin_cleansing_touch_authors_target_impact_vfx() {
        let cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "PALADIN_CLEANSING_TOUCH"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Cleansing Touch should author a target-side impact VFX cue");

        assert_eq!(normalize_identifier(cue.anchor.as_str()), "TARGET");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "FOLLOW_ANCHOR"
        );
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_CLEANSE_HOLY_01"
        );
    }

    #[test]
    fn paladin_absolution_authors_target_wings_blessing_vfx() {
        let cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "PALADIN_ABSOLUTION"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Absolution should author a target-side impact VFX cue");

        assert_eq!(normalize_identifier(cue.anchor.as_str()), "TARGET");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "FOLLOW_ANCHOR"
        );
        assert_eq!(normalize_identifier(cue.lifecycle.as_str()), "DURATION");
        assert_eq!(cue.duration_ms, 5_000);
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_ABSOLUTION_HOLY_01"
        );
    }

    #[test]
    fn paladin_radiant_burst_is_a_sword_and_shield_holy_cone_with_baked_animation() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_RADIANT_BURST")
            .expect("PALADIN_RADIANT_BURST must exist");

        assert_eq!(
            normalize_identifier(ability.combat_discipline_id.as_deref().unwrap_or_default()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "RADIANT_BURST"
        );
        assert_eq!(ability.gameplay.resource_cost, Some(20.0));

        let definition =
            spell_definition_by_str("RADIANT_BURST").expect("Radiant Burst spell definition");
        assert_eq!(definition.behavior, crate::spells::SpellBehavior::Area);
        assert_eq!(definition.targeting, crate::spells::SpellTargeting::Self_);
        assert_eq!(definition.target_audience.as_str(), "HOSTILE");
        assert!(!definition.requires_target);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.damage, 35);
        assert_eq!(definition.damage_type, DamageType::Holy);

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Radiant Burst should author area delivery");
        let shape = delivery
            .get("shape")
            .and_then(|value| value.as_object())
            .expect("Radiant Burst should author cone geometry");
        assert_eq!(
            shape.get("kind").and_then(|value| value.as_str()),
            Some("CASTER_CONE")
        );
        assert_eq!(
            shape.get("angle_degrees").and_then(|value| value.as_f64()),
            Some(65.0)
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "PALADIN_RADIANT_BURST"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Radiant Burst should author a facing-aligned area VFX cue");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "AREA_ORIGIN");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "WORLD_ALIGNED_TO_FACING"
        );
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_RADIANT_BURST_CONE_01"
        );
        assert_eq!(normalize_identifier(cue.lifecycle.as_str()), "DURATION");
        assert_eq!(cue.duration_ms, 2500);

        assert!(
            SPELL_CAST_ANIMATION_MAP_ASSET.contains("- spellId: RADIANT_BURST"),
            "Radiant Burst must resolve through the global spell cast animation map"
        );
        assert!(
            SPELL_CAST_ANIMATION_MAP_ASSET.contains(
                "clip: {fileID: 7400000, guid: b77a7a02d110945d7bd3e5e445fbc043, type: 2}"
            ),
            "Radiant Burst must retain its fixed SwordAndShield Combo_Attack_01_03 clip"
        );
    }

    #[test]
    fn warrior_disengage_strike_authors_timed_backstep() {
        let movement = melee_timed_movement_for_ability_id("WARRIOR_DISENGAGE_STRIKE")
            .expect("disengage strike should author timed movement");

        assert_eq!(movement.ability_id, "WARRIOR_DISENGAGE_STRIKE");
        assert_eq!(movement.kind, "BACKSTEP");
        assert_eq!(movement.start_delay_ms, 220);
        assert_eq!(movement.direction, "BACKWARD");
        assert_eq!(movement.distance, 7.0);
        assert_eq!(movement.speed, 18.0);
        assert_eq!(movement.collision_policy, "STOP_AT_BLOCK");
        assert_eq!(movement.facing_policy, "FACE_START");
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_DISENGAGE_STRIKE")
            .expect("disengage strike ability should exist");
        assert_eq!(ability.gameplay.cooldown_ms, Some(1600));
        assert_eq!(ability.gameplay.uses_global_cooldown, Some(true));
        assert_eq!(ability.gameplay.global_cooldown_ms, Some(650));
    }

    #[test]
    fn dagger_breakaway_is_a_self_directed_movement_ability() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_BREAKAWAY")
            .expect("Breakaway ability should exist");
        assert_eq!(ability_gameplay_kind(ability), "MOVEMENT");
        assert!(
            melee_timed_movement_for_ability_id("DAGGER_BREAKAWAY").is_none(),
            "a disengage must not borrow a melee strike to carry its movement"
        );

        let delivery = super::movement_delivery_for_ability_id("DAGGER_BREAKAWAY")
            .expect("Breakaway should author a movement delivery");
        assert_eq!(delivery.kind, "BACKSTEP");
        assert_eq!(delivery.direction, "BACKWARD");
        assert_eq!(delivery.facing_policy, "FACE_START");
        assert!(!delivery.requires_target);
        assert_eq!(delivery.damage, 0);

        let animation =
            spell_cast_animation_mapping(SPELL_CAST_ANIMATION_MAP_ASSET, "DAGGER_BREAKAWAY");
        assert_eq!(
            (animation.0, animation.1),
            (1, 0),
            "Breakaway must retain its fixed hand-authored action animation"
        );
        assert!(
            SPELL_CAST_ANIMATION_MAP_ASSET.contains(
                "clip: {fileID: 7400000, guid: 5bee978605c5016384b406221b04c3db, type: 2}"
            ),
            "Breakaway must retain its original Combo_Attack_02_03 clip"
        );
    }

    #[test]
    fn warrior_charge_grants_primary_resource_on_accept_only() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_CHARGE")
            .expect("expected Warrior Charge ability");

        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "WARRIOR_CHARGE"
        );
        assert_eq!(
            primary_resource_gain_on_action_accept("WARRIOR_CHARGE"),
            20.0
        );
        assert_eq!(
            primary_resource_gain_on_action_accept("WARRIOR_IMPALE"),
            20.0
        );
        assert_eq!(
            primary_resource_gain_on_action_accept("PALADIN_CHARGE"),
            0.0
        );
    }

    #[test]
    fn legacy_bottom_slot_ids_canonicalize_to_grid_ids() {
        assert_eq!(canonical_action_bar_slot_id("bottom_01"), "SLOT_0_0");
        assert_eq!(canonical_action_bar_slot_id("BOTTOM_08"), "SLOT_0_7");
        assert_eq!(canonical_action_bar_slot_id("BOTTOM_09"), "SLOT_0_8");
        assert_eq!(canonical_action_bar_slot_id("slot_1_0"), "SLOT_1_0");
    }

    #[test]
    fn warrior_whirlwind_resolves_via_greatsword_profile() {
        let hew = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_HEW")
            .expect("expected Warrior Hew ability");
        let hew_targeting = resolved_melee_targeting_for_catalog(&hew.gameplay);
        assert_eq!(hew_targeting.kind, "TARGET");
        assert!(hew_targeting.requires_target);

        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_WHIRLWIND")
            .expect("expected Warrior Whirlwind ability");
        let combat_discipline_id = COMBAT_PROFILE_TWO_HANDED_SWORD;

        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "WHIRLWIND"
        );
        let targeting = resolved_melee_targeting_for_catalog(&ability.gameplay);
        assert_eq!(targeting.kind, "CASTER_RADIUS");
        assert!(!targeting.requires_target);
        assert_eq!(targeting.radius, 3.25);
        assert_eq!(targeting.range, 3.25);
        assert!(profile_supports_action_reference(
            combat_discipline_id,
            &AuthoredActionId::new(ability.action_id.as_str())
        ));
    }

    #[test]
    fn warrior_cataclysm_authors_targetless_cone_area_vfx() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_CATACLYSM")
            .expect("expected Cataclysm ability");
        assert_eq!(ability.display_name, "Cataclysm");
        let targeting = resolved_melee_targeting_for_catalog(&ability.gameplay);
        assert_eq!(targeting.kind, "CASTER_CONE");
        assert!(!targeting.requires_target);
        assert_eq!(targeting.range, 11.5);
        assert_eq!(targeting.angle_degrees, 65.0);
        assert_eq!(
            melee_impact_effects_for_ability_id("WARRIOR_CATACLYSM"),
            vec![MeleeImpactEffectRuntime::ApplyStatus {
                status: StatusApplication::new(
                    StatusPayload::Stun,
                    std::time::Duration::from_millis(2000),
                    Some("WARRIOR_CATACLYSM_STUN".to_string()),
                    StatusStackGroupDefault::ActionSuffix("STUN"),
                    1,
                    StackPolicy::Refresh,
                ),
            }]
        );

        let cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "WARRIOR_CATACLYSM"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Cataclysm should author an area-impact VFX cue");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "AREA_ORIGIN");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_EARTHSHATTER_FIRE_AREA_01"
        );
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "WORLD_ALIGNED_TO_FACING"
        );
    }

    #[test]
    fn warrior_ice_spikes_authors_self_area_cone_vfx() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_ICE_SPIKES")
            .expect("expected Ice Spikes ability");
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "ICE_SPIKES"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.kind.as_str()),
            "SPELL"
        );
        assert_eq!(ability.gameplay.cooldown_ms, Some(2000));
        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Ice Spikes should author spell delivery");
        let effects = delivery
            .get("impact_effects")
            .and_then(|value| value.as_array())
            .expect("Ice Spikes should author impact effects");
        assert!(
            effects.iter().any(|effect| {
                effect.get("kind").and_then(|value| value.as_str()) == Some("FREEZE")
                    && effect.get("duration_ms").and_then(|value| value.as_u64()) == Some(1200)
            }),
            "Ice Spikes should apply FREEZE through area impact effects"
        );

        let cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_ICE_SPIKES"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Ice Spikes should author an area-impact VFX cue");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "AREA_ORIGIN");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_ICE_SPIKES_AREA_01"
        );
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "WORLD_ALIGNED_TO_FACING"
        );

        let fallback_cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "SPELL"
                    && normalize_identifier(cue.owner_id.as_str()) == "ICE_SPIKES"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Ice Spikes should author a spell-owned area-impact fallback VFX cue");
        assert_eq!(
            normalize_identifier(fallback_cue.vfx_id.as_str()),
            "VFX_ICE_SPIKES_AREA_01"
        );
        assert_eq!(
            normalize_identifier(fallback_cue.attach_mode.as_str()),
            "WORLD_ALIGNED_TO_FACING"
        );
    }

    #[test]
    fn gust_of_wind_authors_air_cone_knockback_and_facing_aligned_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_GUST_OF_WIND")
            .expect("expected Gust of Wind ability");
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "GUST_OF_WIND"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "SELF"
        );
        assert_eq!(ability.gameplay.requires_target, Some(false));
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));

        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .expect("Gust of Wind should author spell delivery");
        assert_eq!(
            delivery.get("damage").and_then(|value| value.as_i64()),
            Some(0)
        );
        assert_eq!(
            delivery.get("damage_type").and_then(|value| value.as_str()),
            Some("AIR")
        );
        let shape = delivery
            .get("shape")
            .and_then(|value| value.as_object())
            .expect("Gust of Wind should author cone geometry");
        assert_eq!(
            shape.get("kind").and_then(|value| value.as_str()),
            Some("CASTER_CONE")
        );
        let effects = delivery
            .get("impact_effects")
            .and_then(|value| value.as_array())
            .expect("Gust of Wind should author impact effects");
        assert!(effects.iter().any(|effect| {
            effect.get("kind").and_then(|value| value.as_str()) == Some("KNOCKBACK")
                && effect
                    .get("distance_meters")
                    .and_then(|value| value.as_f64())
                    == Some(4.0)
        }));

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_GUST_OF_WIND"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Gust of Wind should author an area-impact VFX cue");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "AREA_ORIGIN");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_AIR_GUST_CONE_01"
        );
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "WORLD_ALIGNED_TO_FACING"
        );
    }

    #[test]
    fn primal_blast_family_authors_short_range_riders_and_shared_forward_vfx() {
        let catalog = progression_catalog();
        let expected = [
            ("SPELL_EARTH_BLAST", "EARTH_BLAST", "PHYSICAL", "STUN"),
            (
                "SPELL_TIDAL_BLAST",
                "TIDAL_BLAST",
                "PHYSICAL",
                "REMOVE_STATUS",
            ),
            ("SPELL_LAVA_BLAST", "LAVA_BLAST", "FIRE", "BURN"),
            ("SPELL_WIND_BLAST", "WIND_BLAST", "PHYSICAL", "KNOCKBACK"),
        ];

        for (ability_id, action_id, damage_type, rider_kind) in expected {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| normalize_identifier(ability.ability_id.as_str()) == ability_id)
                .unwrap_or_else(|| panic!("expected Primal blast ability {ability_id}"));
            assert_eq!(normalize_identifier(ability.action_id.as_str()), action_id);
            assert_eq!(
                normalize_identifier(ability.gameplay.kind.as_str()),
                "SPELL"
            );
            assert_eq!(ability.gameplay.cooldown_ms, Some(1_200));
            assert_eq!(ability.gameplay.cast_time_ms, Some(0));
            assert_eq!(
                normalize_identifier(ability.gameplay.cast_mobility.as_str()),
                "MOBILE"
            );
            assert_eq!(
                normalize_identifier(ability.gameplay.targeting.as_str()),
                "TARGET"
            );
            assert_eq!(ability.gameplay.requires_target, Some(true));
            assert_eq!(ability.gameplay.requires_target_los, Some(true));

            let delivery = ability
                .gameplay
                .delivery
                .as_ref()
                .expect("Primal blasts should author spell delivery");
            assert_eq!(
                delivery.get("kind").and_then(|value| value.as_str()),
                Some("DIRECT_TARGET")
            );
            assert_eq!(
                delivery
                    .get("max_distance")
                    .and_then(|value| value.as_f64()),
                Some(8.0)
            );
            assert_eq!(
                delivery.get("damage").and_then(|value| value.as_i64()),
                Some(30)
            );
            assert_eq!(
                delivery.get("damage_type").and_then(|value| value.as_str()),
                Some(damage_type)
            );
            let effects = delivery
                .get("impact_effects")
                .and_then(|value| value.as_array())
                .expect("Primal blasts should author one impact rider");
            assert_eq!(effects.len(), 1);
            assert_eq!(
                effects[0].get("kind").and_then(|value| value.as_str()),
                Some(rider_kind)
            );

            let cue = catalog
                .combat_vfx_cues
                .iter()
                .find(|cue| {
                    normalize_identifier(cue.owner_id.as_str()) == ability_id
                        && normalize_identifier(cue.trigger.as_str()) == "SPELL_RELEASE"
                })
                .unwrap_or_else(|| panic!("{ability_id} should author a release VFX cue"));
            assert_eq!(
                normalize_identifier(cue.vfx_id.as_str()),
                "VFX_PRIMAL_FOUR_ELEMENTS_FORWARD_01"
            );
            assert_eq!(normalize_identifier(cue.anchor.as_str()), "CASTER");
            assert_eq!(
                normalize_identifier(cue.attach_mode.as_str()),
                "WORLD_ALIGNED_TO_FACING"
            );
        }

        let tidal = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_TIDAL_BLAST")
            .expect("Tidal Blast ability");
        let tidal_effect = &tidal.gameplay.delivery.as_ref().unwrap()["impact_effects"][0];
        assert_eq!(tidal_effect["polarity"].as_str(), Some("BUFF"));
        assert_eq!(tidal_effect["max_count"].as_u64(), Some(1));
    }

    #[test]
    fn primal_adaptation_overgrowth_photosynthesis_tailwind_slipstream_and_wellspring_are_authored()
    {
        let catalog = progression_catalog();
        let ids = [
            "PRIMAL_ADAPTATION",
            "SPELL_OVERGROWTH",
            "PRIMAL_PHOTOSYNTHESIS",
            "SPELL_TAILWIND",
            "PRIMAL_SLIPSTREAM",
            "SPELL_WELLSPRING",
        ];
        for id in ids {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| normalize_identifier(ability.ability_id.as_str()) == id)
                .unwrap_or_else(|| panic!("expected Primal ability {id}"));
            assert_eq!(
                normalize_identifier(ability.spell_school_id.as_deref().unwrap_or_default()),
                "PRIMAL"
            );
        }

        let adaptation = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == PRIMAL_ADAPTATION_ABILITY_ID)
            .expect("Adaptation ability");
        assert_eq!(ability_gameplay_kind(adaptation), "PASSIVE");
        assert!((adaptation.gameplay.adaptation_resistance_per_stack - 0.02).abs() < 0.0001);
        assert_eq!(adaptation.gameplay.adaptation_duration_ms, 10_000);
        assert_eq!(adaptation.gameplay.adaptation_max_stacks, 10);

        let photosynthesis = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == PRIMAL_PHOTOSYNTHESIS_ABILITY_ID)
            .expect("Photosynthesis ability");
        // Stack cadence is authored tuning, not a code contract - validate_ability_catalog is
        // the gate that keeps it in sync with the catalog.
        assert!(photosynthesis.gameplay.stationary_first_stack_delay_ms > 0);
        assert!(photosynthesis.gameplay.stationary_stack_interval_ms > 0);
        assert_eq!(photosynthesis.gameplay.stationary_max_stacks, 5);
        assert!((photosynthesis.gameplay.stationary_mana_regen_per_stack - 1.0).abs() < 0.0001);
        let photosynthesis_cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == PRIMAL_PHOTOSYNTHESIS_ABILITY_ID
                    && normalize_identifier(cue.trigger.as_str()) == "STATUS_ACTIVE"
            })
            .expect("Photosynthesis should author a reconstructable status VFX cue");
        assert_eq!(
            normalize_identifier(photosynthesis_cue.anchor.as_str()),
            "TARGET"
        );
        assert_eq!(
            normalize_identifier(photosynthesis_cue.lifecycle.as_str()),
            "UNTIL_STATUS_END"
        );

        let slipstream = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == PRIMAL_SLIPSTREAM_ABILITY_ID)
            .expect("Slipstream ability");
        assert_eq!(
            slipstream.gameplay.other_movement_cooldown_reduction_ms,
            2_000
        );

        let wellspring = spell_definition_by_str("WELLSPRING").expect("Wellspring definition");
        assert_eq!(
            wellspring.behavior,
            crate::spells::SpellBehavior::PersistentArea
        );
        assert_eq!(wellspring.duration, 5.0);
        assert_eq!(wellspring.radius, 4.0);
        let area = wellspring
            .secondary
            .persistent_area
            .as_ref()
            .expect("Wellspring persistent-area tuning");
        assert_eq!(area.pulse_interval, Duration::from_secs(1));
        assert_eq!(area.heal_amount, 5);
        assert_eq!(area.mana_restore_amount, 5.0);
        assert_eq!(area.effect_target_audience, TargetAudience::PartyOrSelf);

        let fissure_travel_cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_FISSURE"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_RELEASE"
            })
            .expect("Fissure should author a projectile travel VFX cue");
        assert_eq!(
            normalize_identifier(fissure_travel_cue.anchor.as_str()),
            "ORIGIN"
        );
        assert_eq!(
            normalize_identifier(fissure_travel_cue.attach_mode.as_str()),
            "SPAWN_WORLD"
        );
        assert_eq!(
            normalize_identifier(fissure_travel_cue.vfx_role.as_str()),
            "PROJECTILE_BODY"
        );
        assert_eq!(
            normalize_identifier(fissure_travel_cue.lifecycle.as_str()),
            "UNTIL_TERMINAL_EVENT"
        );
        assert_eq!(
            normalize_identifier(fissure_travel_cue.vfx_id.as_str()),
            "VFX_FISSURE_TRAVEL_01"
        );
        assert_eq!(fissure_travel_cue.projectile_sequence_index, Some(0));

        let fissure_impact_cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_FISSURE"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Fissure should author a terminal eruption VFX cue");
        assert_eq!(
            normalize_identifier(fissure_impact_cue.anchor.as_str()),
            "IMPACT_POINT"
        );
        assert_eq!(
            normalize_identifier(fissure_impact_cue.attach_mode.as_str()),
            "SPAWN_WORLD"
        );
        assert_eq!(
            normalize_identifier(fissure_impact_cue.vfx_role.as_str()),
            "ONE_SHOT"
        );
        assert_eq!(
            normalize_identifier(fissure_impact_cue.lifecycle.as_str()),
            "DURATION"
        );
        assert_eq!(fissure_impact_cue.duration_ms, 5000);
        assert_eq!(
            normalize_identifier(fissure_impact_cue.vfx_id.as_str()),
            "VFX_FISSURE_ERUPTION_01"
        );
    }

    #[test]
    fn cloudburst_vfx_plays_once_at_the_aimed_point_until_its_particles_finish() {
        let cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| normalize_identifier(cue.owner_id.as_str()) == "SPELL_CLOUDBURST")
            .expect("Cloudburst should author its rain VFX cue");

        assert_eq!(normalize_identifier(cue.trigger.as_str()), "SPELL_RELEASE");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "SPAWN_WORLD"
        );
        assert_eq!(normalize_identifier(cue.vfx_role.as_str()), "ONE_SHOT");
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
        assert_eq!(cue.duration_ms, 0);
    }

    #[test]
    fn negate_authors_arcane_shock_particle_vfx() {
        let cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_NEGATE"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_RELEASE"
            })
            .expect("Negate should author a release VFX cue");

        assert_eq!(normalize_identifier(cue.anchor.as_str()), "CASTER");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_NEGATE_ARCANE_SHOCK_01"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
        assert_eq!(cue.duration_ms, 0);
    }

    #[test]
    fn necrotic_aura_vfx_follows_ground_without_inheriting_caster_transform() {
        let cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "SPELL_NECROTIC_AURA"
                    && normalize_identifier(cue.trigger.as_str()) == "EMANATION_ACTIVE"
            })
            .expect("Necrotic Aura must author its active emanation VFX");

        assert_eq!(normalize_identifier(cue.anchor.as_str()), "CASTER");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "FOLLOW_GROUND_POSITION"
        );
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "UNTIL_RADIAL_EFFECT_END"
        );
    }

    #[test]
    fn warrior_sunder_and_cleave_resolve_via_greatsword_profile() {
        let combat_discipline_id = COMBAT_PROFILE_TWO_HANDED_SWORD;

        for (ability_id, action_id) in [
            ("WARRIOR_SUNDER", "COMBO_ATTACK_4_4_LUNGING_SLASH"),
            ("WARRIOR_CLEAVE", "COMBO_ATTACK_2_1_SPIN"),
        ] {
            let ability = progression_catalog()
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .expect("expected Warrior ability");

            assert_eq!(normalize_identifier(ability.action_id.as_str()), action_id);
            assert!(profile_supports_action_reference(
                combat_discipline_id,
                &AuthoredActionId::new(ability.action_id.as_str())
            ));
        }
    }

    #[test]
    fn buffet_authors_impact_point_hit_vfx_without_projectile_cues() {
        let catalog = progression_catalog();
        let cues: Vec<_> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| normalize_identifier(cue.owner_id.as_str()) == "SPELL_BUFFET")
            .collect();

        assert_eq!(cues.len(), 1);
        let cue = cues[0];
        assert_eq!(normalize_identifier(cue.trigger.as_str()), "SPELL_IMPACT");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_BUFFET_IMPACT_01"
        );
        assert_eq!(normalize_identifier(cue.vfx_role.as_str()), "ONE_SHOT");
        assert_eq!(
            normalize_identifier(cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );
    }

    #[test]
    fn warrior_heavy_swing_resolves_as_auto_attack_replacement() {
        let combat_discipline_id = COMBAT_PROFILE_TWO_HANDED_SWORD;
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_DREAD_STRIKE")
            .expect("expected Warrior Heavy Swing ability");
        let replacement = progression_catalog()
            .auto_attack_replacements
            .iter()
            .find(|replacement| replacement.replacement_id == ability.action_id)
            .expect("Heavy Swing must resolve to replacement tuning");

        assert_eq!(ability_gameplay_kind(ability), "AUTO_ATTACK_REPLACEMENT");
        assert_eq!(
            normalize_identifier(replacement.combat_discipline_id.as_str()),
            combat_discipline_id
        );
        assert!(profile_supports_action_reference(
            combat_discipline_id,
            &AuthoredActionId::new(replacement.authored_melee_strike_id.as_str())
        ));
        assert!(!replacement.grants_primary_resource_on_hit);
    }

    #[test]
    fn warrior_momentum_resolves_via_spell_catalog() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_MOMENTUM")
            .expect("expected Warrior Momentum ability");

        assert_eq!(normalize_identifier(ability.action_id.as_str()), "MOMENTUM");
        assert!(spell_definition_by_str(ability.action_id.as_str()).is_some());
    }

    #[test]
    fn subtlety_utility_abilities_author_requested_status_contracts() {
        let expected = [
            ("DAGGER_FIND_WEAKNESS", "FIND_WEAKNESS", 86_400_000_u64),
            ("DAGGER_BLADE_TWISTING", "BLADE_TWISTING", 86_400_000_u64),
            ("DAGGER_GOUGE", "GOUGE", 4_000_u64),
            ("DAGGER_TEMPLE_STRIKE", "CONFUSION", 5_000_u64),
        ];
        for (ability_id, status_kind, duration_ms) in expected {
            let ability = progression_catalog()
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("expected {ability_id}"));
            assert_eq!(ability_gameplay_kind(ability), "SPELL");
            assert_eq!(
                ability.combat_discipline_id.as_deref(),
                Some(COMBAT_PROFILE_DAGGERS)
            );
            let delivery = ability
                .gameplay
                .delivery
                .as_ref()
                .and_then(serde_json::Value::as_object)
                .expect("utility ability delivery");
            assert_eq!(
                delivery.get("kind").and_then(serde_json::Value::as_str),
                Some("APPLY_STATUS")
            );
            assert_eq!(
                delivery
                    .get("duration_ms")
                    .and_then(serde_json::Value::as_u64),
                Some(duration_ms)
            );
            assert_eq!(
                delivery
                    .get("status")
                    .and_then(serde_json::Value::as_object)
                    .and_then(|status| status.get("kind"))
                    .and_then(serde_json::Value::as_str),
                Some(status_kind)
            );
        }

        let blade_twisting = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == DAGGER_BLADE_TWISTING_ABILITY_ID)
            .expect("expected Blade Twisting");
        assert_eq!(blade_twisting.gameplay.cooldown_ms, Some(30_000));
        assert!((blade_twisting.gameplay.blade_twisting_bleed_damage_ratio - 0.5).abs() < 0.0001);
        assert_eq!(
            blade_twisting.gameplay.blade_twisting_bleed_duration_ms,
            5_000
        );
        assert_eq!(
            blade_twisting
                .gameplay
                .blade_twisting_bleed_tick_interval_ms,
            1_000
        );

        let gouge = spell_definition_by_str("GOUGE").expect("Gouge runtime definition");
        let gouge_additional = &gouge
            .secondary
            .apply_status
            .as_ref()
            .expect("Gouge apply-status tunables")
            .additional_applications;
        assert_eq!(gouge_additional.len(), 1);
        assert_eq!(gouge_additional[0].duration, Duration::from_secs(86_400));
        assert_eq!(
            gouge_additional[0].status_stack_group.as_deref(),
            Some("VULNERABLE")
        );
        assert_eq!(
            gouge_additional[0].status.kind,
            StatusEffectKind::Vulnerable
        );
        assert_eq!(gouge_additional[0].status.max_stacks, 3);
        assert_eq!(
            gouge_additional[0].status.stack_policy,
            StackPolicy::AddStackRefresh
        );

        let animation_ids = spell_ids_for_combat_profile(COMBAT_PROFILE_DAGGERS);
        for spell_id in ["FIND_WEAKNESS", "BLADE_TWISTING", "GOUGE", "TEMPLE_STRIKE"] {
            assert!(
                animation_ids.contains(spell_id),
                "Dagger animation set must map {spell_id}"
            );
        }
    }

    #[test]
    fn moved_disarm_and_shadow_abilities_author_their_new_disciplines() {
        let catalog = progression_catalog();
        let disarm = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_DISARM")
            .expect("expected Disarm");
        assert_eq!(
            normalize_identifier(disarm.combat_discipline_id.as_deref().unwrap_or_default()),
            COMBAT_PROFILE_DAGGERS
        );
        assert!(spell_ids_for_combat_profile(COMBAT_PROFILE_TWO_HANDED_SWORD).contains("DISARM"));
        assert!(
            spell_ids_for_combat_profile(COMBAT_PROFILE_DAGGERS).contains("DISARM"),
            "semantic spell classifications resolve through every combat animation set"
        );

        for ability_id in [
            "DAGGER_DARKNESS",
            "DAGGER_STALK",
            "DAGGER_STALK_SHADOWSTEP",
            "DAGGER_SHADOWREND",
        ] {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("expected {ability_id}"));
            assert_eq!(ability.spell_school_id.as_deref(), Some("MORTALITY"));
            assert_eq!(
                ability.combat_discipline_id.as_deref(),
                Some(if ability_id == "DAGGER_STALK_SHADOWSTEP" {
                    COMBAT_PROFILE_STAFF
                } else {
                    COMBAT_PROFILE_DAGGERS
                })
            );
        }
    }

    #[test]
    fn mortality_and_blight_own_the_complete_restructured_spell_families() {
        let catalog = progression_catalog();
        let expected_mortality = HashSet::from([
            "SPELL_VAMPIRIC_ORB",
            "SPELL_WITHERING_ORB",
            "SPELL_SOULSTEALER",
            "SPELL_NECROTIC_AURA",
            "SPELL_DEFILED_GROUND",
            "SPELL_REAP",
            "SPELL_GRIM_WHEEL",
            "SPELL_GRAVEBURST",
            "SPELL_GRAVEWAKE",
            "SPELL_NECRO_PRISON",
            "SPELL_BLOOD_OFFERING",
            "DAGGER_DARKNESS",
            "DAGGER_STALK",
            "DAGGER_STALK_SHADOWSTEP",
            "DAGGER_SHADOWREND",
        ]);
        let expected_blight = HashSet::from([
            "SPELL_ICICLE",
            "SPELL_FROST_NEEDLE",
            "SPELL_ICE_SPIKES",
            "SPELL_FROZEN_SPLINTERS",
            "SPELL_BLIZZARD",
            "SPELL_FROST_NOVA",
            "SPELL_GLACIAL_SPIKE",
            "SPELL_FROZEN_GRASP",
            "RUIN_RIME",
            "RUIN_FRACTURE",
            "SPELL_FLASH_FREEZE",
            "SPELL_DEEPENING_COLD",
            "SPELL_GLACIAL_ADVANCE",
            "SPELL_MIASMA",
            "SPELL_PLAGUEBOLT",
            "BLIGHT_TOXIC_WEAPON",
            "SPELL_CONTAGION",
        ]);

        let mortality: HashSet<&str> = catalog
            .abilities
            .iter()
            .filter(|ability| ability.spell_school_id.as_deref() == Some("MORTALITY"))
            .map(|ability| ability.ability_id.as_str())
            .collect();
        let blight: HashSet<&str> = catalog
            .abilities
            .iter()
            .filter(|ability| ability.spell_school_id.as_deref() == Some("BLIGHT"))
            .map(|ability| ability.ability_id.as_str())
            .collect();

        assert_eq!(mortality, expected_mortality);
        assert_eq!(blight, expected_blight);
    }

    #[test]
    fn arcana_recall_authors_the_store_and_replay_contract() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_RECALL")
            .expect("expected Arcana Recall spell");
        assert_eq!(ability.spell_school_id.as_deref(), Some("ARCANA"));
        assert_eq!(normalize_identifier(&ability.action_id), "RECALL");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(ability.gameplay.cooldown_ms, Some(0));
        assert_eq!(ability.gameplay.resource_cost, Some(0.0));
        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Recall delivery");
        assert_eq!(
            delivery.get("kind").and_then(serde_json::Value::as_str),
            Some("RECALL")
        );
        assert_eq!(
            delivery
                .get("replay_cooldown_ms")
                .and_then(serde_json::Value::as_u64),
            Some(60_000)
        );
        assert!(progression_catalog()
            .action_presentations
            .iter()
            .any(|presentation| {
                action_presentation_key(presentation) == "ABILITY:SPELL_RECALL"
                    && presentation.display_name == "Recall"
            }));
    }

    #[test]
    fn arcana_transpose_authors_swap_delivery_and_both_endpoint_cues() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_TRANSPOSE")
            .expect("expected Arcana Transpose spell");
        assert_eq!(ability.spell_school_id.as_deref(), Some("ARCANA"));
        assert_eq!(normalize_identifier(&ability.action_id), "TRANSPOSE");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(ability.gameplay.cooldown_ms, Some(30_000));
        let delivery = ability
            .gameplay
            .delivery
            .as_ref()
            .and_then(serde_json::Value::as_object)
            .expect("Transpose delivery");
        assert_eq!(
            delivery.get("kind").and_then(serde_json::Value::as_str),
            Some("TRANSPOSE")
        );
        assert_eq!(
            delivery
                .get("max_distance")
                .and_then(serde_json::Value::as_f64),
            Some(18.0)
        );
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation) == "ABILITY:SPELL_TRANSPOSE"
                && presentation.display_name == "Transpose"
        }));

        let triggers: HashSet<String> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| normalize_identifier(&cue.owner_id) == "SPELL_TRANSPOSE")
            .map(|cue| normalize_identifier(&cue.trigger))
            .collect();
        assert_eq!(
            triggers,
            HashSet::from(["SPELL_RELEASE".to_string(), "SPELL_IMPACT".to_string()])
        );
    }

    #[test]
    fn spell_abilities_do_not_define_positive_resource_costs() {
        let catalog = progression_catalog();

        for ability in &catalog.abilities {
            if ability_gameplay_kind(ability) != "SPELL" {
                continue;
            }

            assert!(
                ability.resource_cost <= 0.0001,
                "spell ability '{}' must leave resource_cost on the spell catalog row",
                ability.ability_id
            );
        }
    }

    #[test]
    fn warrior_self_buffs_have_semantic_cast_assignments() {
        let animation_set_spell_ids = spell_ids_for_combat_profile(COMBAT_PROFILE_TWO_HANDED_SWORD);

        assert!(
            animation_set_spell_ids.contains("MOMENTUM"),
            "expected Momentum semantic cast assignment"
        );
        assert!(
            animation_set_spell_ids.contains("BATTLE_CRY"),
            "expected Battle Cry fixed cast assignment"
        );
        assert!(
            animation_set_spell_ids.contains("FORTIFY"),
            "expected Fortify semantic cast assignment"
        );
        assert!(
            animation_set_spell_ids.contains("IRON_WILL"),
            "expected Iron Will semantic cast assignment"
        );
        assert!(
            animation_set_spell_ids.contains("DEFIANCE"),
            "expected Defiance semantic cast assignment"
        );
        assert!(
            animation_set_spell_ids.contains("FRENZY"),
            "expected Frenzy semantic cast assignment"
        );
        assert!(
            animation_set_spell_ids.contains("SECOND_WIND"),
            "expected Second Wind semantic cast assignment"
        );
        assert!(
            animation_set_spell_ids.contains("BERSERKING"),
            "expected Berserking semantic cast assignment"
        );
        assert!(
            animation_set_spell_ids.contains("BATTLE_TRANCE"),
            "expected Battle Trance semantic cast assignment"
        );
    }

    #[test]
    fn runtime_action_ids_are_normalized_before_progression_matching() {
        assert_eq!(
            normalize_identifier(RuntimeActionId::new("utility_1").as_str()),
            "UTILITY_1"
        );
        assert_eq!(
            normalize_identifier(RuntimeActionId::new("utility_1").as_str()),
            "UTILITY_1"
        );
        assert_eq!(
            normalize_identifier(RuntimeActionId::new("COMBO_ATTACK_3_1_LOW_TO_HIGH").as_str()),
            "COMBO_ATTACK_3_1_LOW_TO_HIGH"
        );
    }
}

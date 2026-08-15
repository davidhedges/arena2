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
use crate::inventory::{
    apply_combat_discipline_weapon_loadout, combat_discipline_weapon_loadout_is_available,
    equipment_combat_profile_id_for_owner, equipment_modifier_totals_for_owner,
    equipment_spell_slot_capacity_for_owner,
};
use crate::melee::sync_melee_attack_modifier_catalog;
use crate::player::Player;
use crate::relations::TARGET_AUDIENCE_HOSTILE;
use crate::spells::{is_on_named_cooldown, player_knows_spell, stamp_named_cooldown_for_duration};

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
use crate::progression::active_combat_discipline as _;
#[allow(unused_imports)]
use crate::progression::active_combat_mode as _;
#[allow(unused_imports)]
use crate::progression::auto_attack_catalog as _;
#[allow(unused_imports)]
use crate::progression::character_action_bar_assignment as _;
#[allow(unused_imports)]
use crate::progression::character_combat_discipline_weapon_loadout as _;
#[allow(unused_imports)]
use crate::progression::character_discipline_ability_selection as _;
#[allow(unused_imports)]
use crate::progression::character_discipline_loadout as _;
#[allow(unused_imports)]
use crate::progression::combat_discipline_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_mode_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_profile_catalog as _;
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

const PROGRESSION_CATALOG_JSON: &str = include_str!("progression_catalog.shared.json");
const ACTION_KIND_ABILITY: &str = "ABILITY";
const ACTION_KIND_FIXED: &str = "FIXED";
const FIXED_ACTION_DODGE: &str = "DODGE";
const FIXED_ACTION_PARRY: &str = "PARRY";
const ACTION_KIND_COMBAT_DISCIPLINE_SWITCH: &str = "COMBAT_DISCIPLINE_SWITCH";
pub(crate) const GLOBAL_ACTION_BAR_PROFILE: &str = "GLOBAL";
const PRIMARY_DISCIPLINE_ABILITY_MINIMUM: usize = 8;
const SECONDARY_DISCIPLINE_ABILITY_MINIMUM: usize = 1;
const MAX_DISCIPLINE_LOADOUT_ABILITIES: usize = 128;
const RULE_DEFAULT_GLOBAL_COOLDOWN_MS: &str = "DEFAULT_GLOBAL_COOLDOWN_MS";
const FALLBACK_DEFAULT_GLOBAL_COOLDOWN_MS: u64 = 1500;
const MAX_DEFAULT_GLOBAL_COOLDOWN_MS: u64 = 60_000;
pub(crate) const COMBAT_PROFILE_ARCHER_BOW: &str = "ARCHER_BOW";
pub(crate) const COMBAT_PROFILE_DAGGERS: &str = "DAGGERS";
pub(crate) const COMBAT_PROFILE_SWORD_AND_SHIELD: &str = "SWORD_AND_SHIELD";
pub(crate) const COMBAT_PROFILE_TWO_HANDED_SWORD: &str = "TWO_HANDED_SWORD";
pub(crate) const DISCIPLINE_SUBTLETY: &str = "SUBTLETY";
pub(crate) const DISCIPLINE_WAR: &str = "WAR";
pub(crate) const DISCIPLINE_ZEAL: &str = "ZEAL";
pub(crate) const DISCIPLINE_PRECISION: &str = "PRECISION";
pub(crate) const DISCIPLINE_BLIGHT: &str = "BLIGHT";
pub(crate) const DISCIPLINE_RUIN: &str = "RUIN";
pub(crate) const DISCIPLINE_DIVINITY: &str = "DIVINITY";
pub(crate) const DISCIPLINE_ARCANA: &str = "ARCANA";
pub(crate) const DISCIPLINE_PRIMAL: &str = "PRIMAL";
const DISCIPLINE_KIND_WEAPON: &str = "WEAPON";
const DISCIPLINE_KIND_SPELL_SCHOOL: &str = "SPELL_SCHOOL";
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
// Keep the original wire ID stable so existing action-bar assignments survive the rename.
const DAGGER_SHROUD_ABILITY_ID: &str = "DAGGER_STEALTH";
const SUBTLETY_FLEET_FOOTED_ABILITY_ID: &str = "SUBTLETY_FLEET_FOOTED";
const SUBTLETY_LINGERING_SHADE_ABILITY_ID: &str = "SUBTLETY_LINGERING_SHADE";
const SUBTLETY_OPPORTUNIST_ABILITY_ID: &str = "SUBTLETY_OPPORTUNIST";
const SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID: &str = "SUBTLETY_SURPRISE_ATTACKS";
const SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID: &str = "SUBTLETY_TACTICAL_ADVANTAGE";
const RUIN_FLAMING_WEAPON_ABILITY_ID: &str = "RUIN_FLAMING_WEAPON";
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
    combat_profiles: Vec<CombatProfileDefinition>,
    #[serde(default)]
    combat_disciplines: Vec<CombatDisciplineDefinition>,
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
    combat_profile_action_bar_defaults: Vec<CombatProfileActionBarDefaultDefinition>,
    #[serde(default)]
    slots: Vec<ActionBarSlotDefinition>,
}

#[derive(Clone, Deserialize)]
struct CombatProfileDefinition {
    combat_profile_id: String,
    display_name: String,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct CombatDisciplineDefinition {
    discipline_id: String,
    display_name: String,
    discipline_kind: String,
    #[serde(default)]
    combat_profile_id: String,
    #[serde(default)]
    primary_resource_kind: String,
    #[serde(default)]
    inactive_resource_tick: bool,
    #[serde(default)]
    inactive_decay_delay_ms: u64,
    sort_order: u32,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct CombatModeDefinition {
    combat_profile_id: String,
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
    discipline_id: String,
    combat_profile_id: String,
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
    dodge_recharge_time_reduction: f32,
    #[serde(default)]
    movement_return: Option<MovementReturnDefinition>,
    #[serde(default)]
    stealth_attack_stun_ms: u64,
    #[serde(default)]
    melee_fire_on_hit: Option<MeleeFireOnHitDefinition>,
    #[serde(default)]
    fire_spell_ignite: Option<FireSpellIgniteDefinition>,
    #[serde(default)]
    soulstealer_empowered_damage_bonus: f32,
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
    frost_spell_debuff_protection: bool,
    #[serde(default)]
    frozen_melee_first_hit_damage_bonus: f32,
    #[serde(default)]
    noncritical_lightning_spell_crit_chance_bonus: f32,
    #[serde(default)]
    mana_regen_bonus: f32,
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
    arrival: MovementDeliveryArrivalDefinition,
    #[serde(default)]
    impact_effects: Vec<MovementDeliveryImpactEffectDefinition>,
}

#[derive(Clone, Deserialize)]
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
    RemoveStatus {
        #[serde(default)]
        polarity: Option<StatusPolarity>,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
        #[serde(default = "default_one_status_stack")]
        max_count: u32,
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
    RemoveStatus {
        polarity: Option<StatusPolarity>,
        dispel_types: Vec<StatusDispelType>,
        max_count: u32,
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

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct MeleeChannelRuntime {
    pub duration_ms: u64,
    pub first_tick_delay_ms: u64,
    pub tick_interval_ms: u64,
    pub cancel_on_movement: bool,
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
struct MeleeChannelDefinition {
    duration_ms: u64,
    first_tick_delay_ms: u64,
    tick_interval_ms: u64,
    cancel_on_movement: bool,
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
    discipline_id: String,
    combat_profile_id: String,
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
    combat_profile_id: String,
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
struct CombatProfileActionBarDefaultDefinition {
    combat_profile_id: String,
    slot_id: String,
    #[serde(default)]
    action_kind: String,
    #[serde(default)]
    action_id: String,
    #[serde(default)]
    ability_id: String,
    sort_order: u32,
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

    let default_ability_assignments: HashSet<String> = catalog
        .combat_profile_action_bar_defaults
        .iter()
        .filter_map(|assignment| {
            let action_ref = action_ref_for_action_bar_default(assignment);
            action_ref.is_ability().then_some(action_ref.id)
        })
        .collect();
    for ability in &catalog.abilities {
        let ability_id = normalize_identifier(ability.ability_id.as_str());
        let is_core = ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "CORE_ABILITY");
        let is_spell = ability_gameplay_kind(ability) == "SPELL";
        assert!(
            !is_core || is_spell || default_ability_assignments.contains(ability_id.as_str()),
            "core non-spell ability '{}' must have a action-bar default",
            ability.ability_id
        );
    }
}

fn authored_status_presentation_ids(catalog: &ProgressionCatalogFile) -> HashSet<String> {
    let mut ids = known_status_kind_ids();
    for ability in &catalog.abilities {
        if let Some(delivery) = ability.gameplay.delivery.as_ref() {
            collect_status_stack_groups(delivery, &mut ids);
        }
        for effect in &ability.gameplay.melee_impact_effects {
            match effect {
                MeleeImpactEffectDefinition::Knockback { .. } => {}
                MeleeImpactEffectDefinition::ApplyStatus { status } => {
                    collect_optional_status_stack_group(
                        status.status_stack_group.as_deref(),
                        &mut ids,
                    );
                }
                MeleeImpactEffectDefinition::RemoveStatus { .. } => {}
            }
        }
        if let Some(melee_fire_on_hit) = ability.gameplay.melee_fire_on_hit.as_ref() {
            collect_optional_status_stack_group(
                Some(melee_fire_on_hit.burn_status_stack_group.as_str()),
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
        StatusEffectKind::Stagger,
        StatusEffectKind::Knockdown,
        StatusEffectKind::Slow,
        StatusEffectKind::Dot,
        StatusEffectKind::Hot,
        StatusEffectKind::MoveSlowImmunity,
        StatusEffectKind::MovementImpairingImmunity,
        StatusEffectKind::StunImmunity,
        StatusEffectKind::Silence,
        StatusEffectKind::DamageAmp,
        StatusEffectKind::DirectDamageAmp,
        StatusEffectKind::DamageTakenReduction,
        StatusEffectKind::HealingTakenReduction,
        StatusEffectKind::DamageDealtReduction,
        StatusEffectKind::ManaRegen,
        StatusEffectKind::StaminaRegen,
        StatusEffectKind::MagicResistance,
        StatusEffectKind::KnockbackResistance,
        StatusEffectKind::Thorns,
        StatusEffectKind::VengeanceAura,
        StatusEffectKind::DamageTakenFromSourceAmp,
        StatusEffectKind::MeleeAttackModifier,
        StatusEffectKind::AttackSpeed,
        StatusEffectKind::CastSpeed,
        StatusEffectKind::TemporaryHitpoints,
        StatusEffectKind::Berserking,
        StatusEffectKind::BattleTrance,
        StatusEffectKind::TargetedAbilityAvoidance,
        StatusEffectKind::Fulmination,
        StatusEffectKind::Quickening,
        StatusEffectKind::Rime,
        StatusEffectKind::SoulStolen,
        StatusEffectKind::BlightEmpowered,
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
                    ids.insert(normalized);
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
            ids.insert(normalized);
        }
    }
}

#[table(accessor = combat_profile_catalog, public)]
pub struct CombatProfileCatalog {
    #[primary_key]
    pub combat_profile_id: String,
    pub display_name: String,
    pub sort_order: u32,
}

#[table(accessor = combat_discipline_catalog, public)]
pub struct CombatDisciplineCatalog {
    #[primary_key]
    pub discipline_id: String,
    pub discipline_kind: String,
    #[index(btree)]
    pub combat_profile_id: String,
    pub display_name: String,
    pub primary_resource_kind: String,
    pub inactive_resource_tick: bool,
    pub inactive_decay_delay_ms: u64,
    pub sort_order: u32,
}

#[table(accessor = combat_mode_catalog, public)]
pub struct CombatModeCatalog {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub combat_profile_id: String,
    pub mode_id: String,
    pub display_name: String,
    pub is_default: bool,
    pub sort_order: u32,
}

#[table(accessor = active_combat_mode, public)]
pub struct ActiveCombatMode {
    #[primary_key]
    pub owner: Identity,
    pub combat_profile_id: String,
    pub mode_id: String,
    pub changed_at: Timestamp,
}

#[table(accessor = active_combat_discipline, public)]
pub struct ActiveCombatDiscipline {
    #[primary_key]
    pub owner: Identity,
    pub discipline_id: String,
    pub combat_profile_id: String,
    pub primary_resource_kind: String,
    pub changed_at: Timestamp,
}

#[table(accessor = character_discipline_loadout, public)]
pub struct CharacterDisciplineLoadout {
    #[primary_key]
    pub owner: Identity,
    pub primary_discipline_id: String,
    pub secondary_discipline_id_1: String,
    pub secondary_discipline_id_2: String,
    pub updated_at: Timestamp,
}

#[table(accessor = character_discipline_ability_selection, public)]
pub struct CharacterDisciplineAbilitySelection {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    #[index(btree)]
    pub discipline_id: String,
    pub ability_id: String,
    pub sort_order: u32,
    pub updated_at: Timestamp,
}

#[table(accessor = character_combat_discipline_weapon_loadout, public)]
pub struct CharacterCombatDisciplineWeaponLoadout {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub discipline_id: String,
    pub main_hand_item_id: Option<String>,
    pub off_hand_item_id: Option<String>,
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
    pub discipline_id: String,
    pub combat_profile_id: String,
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
}

#[table(accessor = auto_attack_catalog, public)]
pub struct AutoAttackCatalog {
    #[primary_key]
    pub key: String,
    pub discipline_id: String,
    pub combat_profile_id: String,
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
    pub combat_profile_id: String,
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

#[table(accessor = character_action_bar_assignment, public)]
pub struct CharacterActionBarAssignment {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    #[index(btree)]
    pub combat_profile_id: String,
    pub slot_id: String,
    pub action_kind: String,
    pub action_id: String,
    pub ability_id: String,
    pub updated_at: Timestamp,
}

#[derive(Clone, Debug, PartialEq, Eq)]
enum ActionKind {
    Ability,
    Fixed,
    CombatDisciplineSwitch,
    Unsupported(String),
}

impl ActionKind {
    fn from_wire(value: &str) -> Self {
        let normalized = normalize_identifier(value);
        match normalized.as_str() {
            ACTION_KIND_ABILITY => Self::Ability,
            ACTION_KIND_FIXED => Self::Fixed,
            ACTION_KIND_COMBAT_DISCIPLINE_SWITCH => Self::CombatDisciplineSwitch,
            _ => Self::Unsupported(normalized),
        }
    }

    fn as_wire(&self) -> &str {
        match self {
            Self::Ability => ACTION_KIND_ABILITY,
            Self::Fixed => ACTION_KIND_FIXED,
            Self::CombatDisciplineSwitch => ACTION_KIND_COMBAT_DISCIPLINE_SWITCH,
            Self::Unsupported(value) => value.as_str(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum FixedActionId {
    Dodge,
    Parry,
    Unsupported(String),
}

impl FixedActionId {
    fn from_wire(value: &str) -> Self {
        let normalized = normalize_identifier(value);
        match normalized.as_str() {
            FIXED_ACTION_DODGE => Self::Dodge,
            FIXED_ACTION_PARRY => Self::Parry,
            _ => Self::Unsupported(normalized),
        }
    }

    fn as_wire(&self) -> &str {
        match self {
            Self::Dodge => FIXED_ACTION_DODGE,
            Self::Parry => FIXED_ACTION_PARRY,
            Self::Unsupported(value) => value.as_str(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct ActionRef {
    kind: ActionKind,
    id: String,
}

impl ActionRef {
    fn ability(ability_id: &str) -> Self {
        Self {
            kind: ActionKind::Ability,
            id: normalize_identifier(ability_id),
        }
    }

    fn from_wire(kind: &str, id: &str) -> Self {
        Self {
            kind: ActionKind::from_wire(kind),
            id: normalize_identifier(id),
        }
    }

    fn kind_wire(&self) -> &str {
        self.kind.as_wire()
    }

    fn is_ability(&self) -> bool {
        self.kind == ActionKind::Ability
    }
}

#[reducer]
pub fn publish_progression_catalogs(ctx: &ReducerContext) -> Result<(), String> {
    sync_progression_catalogs(ctx);
    Ok(())
}

#[reducer]
pub fn assign_character_action_bar_ability_to_slot(
    ctx: &ReducerContext,
    slot_id: String,
    ability_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let combat_profile_id = active_action_bar_combat_profile_id(ctx, owner)?;
    let normalized_slot_id = canonical_action_bar_slot_id(slot_id.as_str());
    let normalized_ability_id = normalize_identifier(ability_id.as_str());
    let action_ref = ActionRef::ability(normalized_ability_id.as_str());
    validate_character_action_bar_ref(
        ctx,
        owner,
        combat_profile_id.as_str(),
        normalized_slot_id.as_str(),
        &action_ref,
    )?;
    upsert_character_action_bar_assignment(
        ctx,
        owner,
        combat_profile_id.as_str(),
        normalized_slot_id.as_str(),
        &action_ref,
        ctx.timestamp,
    );
    Ok(())
}

#[reducer]
pub fn assign_character_action_bar_slot(
    ctx: &ReducerContext,
    slot_id: String,
    action_kind: String,
    action_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let combat_profile_id = active_action_bar_combat_profile_id(ctx, owner)?;
    let normalized_slot_id = canonical_action_bar_slot_id(slot_id.as_str());
    let action_ref = ActionRef::from_wire(action_kind.as_str(), action_id.as_str());
    validate_character_action_bar_ref(
        ctx,
        owner,
        combat_profile_id.as_str(),
        normalized_slot_id.as_str(),
        &action_ref,
    )?;
    upsert_character_action_bar_assignment(
        ctx,
        owner,
        combat_profile_id.as_str(),
        normalized_slot_id.as_str(),
        &action_ref,
        ctx.timestamp,
    );
    Ok(())
}

#[reducer]
pub fn clear_character_action_bar_slot(
    ctx: &ReducerContext,
    slot_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let combat_profile_id = active_action_bar_combat_profile_id(ctx, owner)?;
    let normalized_slot_id = canonical_action_bar_slot_id(slot_id.as_str());
    require_slot_catalog_row(ctx, normalized_slot_id.as_str())?;
    let key = character_action_bar_key(
        owner,
        combat_profile_id.as_str(),
        normalized_slot_id.as_str(),
    );
    if ctx
        .db
        .character_action_bar_assignment()
        .key()
        .find(key.clone())
        .is_some()
    {
        ctx.db.character_action_bar_assignment().key().delete(key);
    }
    Ok(())
}

fn shroud_ability_definition() -> &'static AbilityDefinition {
    ability_definition(DAGGER_SHROUD_ABILITY_ID)
        .expect("Subtlety Shroud ability must remain authored")
}

fn shroud_is_active(active: &ActiveCombatMode) -> bool {
    active.combat_profile_id == COMBAT_PROFILE_DAGGERS && active.mode_id == COMBAT_MODE_STEALTHED
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
            combat_profile_id: COMBAT_PROFILE_DAGGERS.to_string(),
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
        || !character_has_selected_discipline(ctx, owner, DISCIPLINE_SUBTLETY)
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
    let combat_profile_id = derived_combat_profile_id_for_owner(ctx, owner)
        .ok_or_else(|| "owner has no resolved combat profile".to_string())?;
    let mode_id = normalize_identifier(mode_id.as_str());
    if !combat_mode_is_valid_for_profile(ctx, combat_profile_id.as_str(), mode_id.as_str()) {
        return Err(format!(
            "combat mode '{}' is not valid for profile '{}'",
            mode_id, combat_profile_id
        ));
    }

    if let Some(active) = ctx.db.active_combat_mode().owner().find(owner) {
        if active.combat_profile_id == combat_profile_id && active.mode_id == mode_id {
            return Ok(());
        }
    }

    let entering_shroud =
        combat_profile_id == COMBAT_PROFILE_DAGGERS && mode_id == COMBAT_MODE_STEALTHED;
    if entering_shroud {
        let shroud = shroud_ability_definition();
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
            combat_profile_id,
            mode_id,
            changed_at: ctx.timestamp,
        },
    );
    Ok(())
}

#[reducer]
pub fn assign_combat_discipline_weapon_loadout(
    ctx: &ReducerContext,
    discipline_id: String,
    main_hand_item_id: Option<String>,
    off_hand_item_id: Option<String>,
) -> Result<(), String> {
    let owner = ctx.sender();
    let discipline = require_combat_discipline(ctx, discipline_id.as_str())?;
    let main_hand_item_id = normalize_optional_item_instance_id(main_hand_item_id);
    let off_hand_item_id = normalize_optional_item_instance_id(off_hand_item_id);
    if !combat_discipline_weapon_loadout_is_available(
        ctx,
        owner,
        discipline.discipline_id.as_str(),
        main_hand_item_id.as_deref(),
        off_hand_item_id.as_deref(),
    ) {
        return Err(format!(
            "weapon loadout is not valid for combat discipline '{}'",
            discipline.discipline_id
        ));
    }

    upsert_combat_discipline_weapon_loadout(
        ctx,
        CharacterCombatDisciplineWeaponLoadout {
            key: combat_discipline_weapon_loadout_key(owner, discipline.discipline_id.as_str()),
            owner,
            discipline_id: discipline.discipline_id,
            main_hand_item_id,
            off_hand_item_id,
            updated_at: ctx.timestamp,
        },
    );
    Ok(())
}

#[reducer]
pub fn set_combat_discipline(ctx: &ReducerContext, discipline_id: String) -> Result<(), String> {
    let owner = ctx.sender();
    let discipline = require_combat_discipline(ctx, discipline_id.as_str())?;
    let loadout = ctx
        .db
        .character_combat_discipline_weapon_loadout()
        .key()
        .find(combat_discipline_weapon_loadout_key(
            owner,
            discipline.discipline_id.as_str(),
        ))
        .ok_or_else(|| {
            format!(
                "combat discipline '{}' has no saved weapon loadout",
                discipline.discipline_id
            )
        })?;
    apply_combat_discipline_weapon_loadout(
        ctx,
        owner,
        discipline.discipline_id.as_str(),
        loadout.main_hand_item_id.as_deref(),
        loadout.off_hand_item_id.as_deref(),
    )?;

    upsert_active_combat_discipline(
        ctx,
        ActiveCombatDiscipline {
            owner,
            discipline_id: discipline.discipline_id,
            combat_profile_id: discipline.combat_profile_id,
            primary_resource_kind: discipline.primary_resource_kind,
            changed_at: ctx.timestamp,
        },
    );
    crate::combat::clear_potential_state_for_owner(ctx, owner);
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
    ensure_default_character_action_bar_assignments(ctx, owner, ctx.timestamp);
    sync_persisted_discipline_action_bars(ctx, owner, ctx.timestamp);
    Ok(())
}

#[reducer]
pub fn save_character_discipline_loadout(
    ctx: &ReducerContext,
    primary_discipline_id: String,
    secondary_discipline_id_1: String,
    secondary_discipline_id_2: String,
    selected_ability_ids: Vec<String>,
) -> Result<(), String> {
    save_character_discipline_loadout_for_owner(
        ctx,
        ctx.sender(),
        primary_discipline_id,
        secondary_discipline_id_1,
        secondary_discipline_id_2,
        selected_ability_ids,
    )
}

pub(crate) fn save_character_discipline_loadout_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    primary_discipline_id: String,
    secondary_discipline_id_1: String,
    secondary_discipline_id_2: String,
    selected_ability_ids: Vec<String>,
) -> Result<(), String> {
    let primary_discipline_id =
        require_combat_discipline(ctx, primary_discipline_id.as_str())?.discipline_id;
    let secondary_discipline_id_1 =
        normalize_selected_secondary_discipline(ctx, secondary_discipline_id_1)?;
    let secondary_discipline_id_2 =
        normalize_selected_secondary_discipline(ctx, secondary_discipline_id_2)?;
    validate_character_discipline_selection(
        primary_discipline_id.as_str(),
        secondary_discipline_id_1.as_str(),
        secondary_discipline_id_2.as_str(),
    )?;

    let selected_abilities = validate_character_discipline_ability_selection(
        ctx,
        primary_discipline_id.as_str(),
        secondary_discipline_id_1.as_str(),
        secondary_discipline_id_2.as_str(),
        selected_ability_ids,
    )?;

    let loadout = CharacterDisciplineLoadout {
        owner,
        primary_discipline_id,
        secondary_discipline_id_1,
        secondary_discipline_id_2,
        updated_at: ctx.timestamp,
    };
    upsert_character_discipline_loadout(ctx, loadout);
    replace_character_discipline_ability_selections(
        ctx,
        owner,
        selected_abilities.as_slice(),
        ctx.timestamp,
    );
    sync_selected_discipline_action_bars(ctx, owner, selected_abilities.as_slice(), ctx.timestamp);
    Ok(())
}

pub(crate) fn sync_progression_catalogs(ctx: &ReducerContext) {
    sync_combat_profile_catalog(ctx);
    sync_combat_discipline_catalog(ctx);
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

pub(crate) fn backfill_character_action_bar_rows(ctx: &ReducerContext) -> usize {
    let players: Vec<Player> = ctx.db.player().iter().collect();
    let mut repaired = 0usize;

    for player in players {
        let is_dummy = ctx
            .db
            .player_state()
            .player_id()
            .find(player.identity)
            .map(|state| state.is_dummy)
            .unwrap_or(false);
        if is_dummy {
            continue;
        }
        let had_action_bar_rows = ctx
            .db
            .character_action_bar_assignment()
            .owner()
            .filter(player.identity)
            .next()
            .is_some();
        if ensure_default_progression_for_identity(ctx, player.identity).is_ok()
            && !had_action_bar_rows
        {
            repaired += 1;
        }
    }

    repaired
}

pub(crate) fn clear_generic_fixed_action_bar_assignments(ctx: &ReducerContext) -> usize {
    let rows: Vec<CharacterActionBarAssignment> = ctx
        .db
        .character_action_bar_assignment()
        .iter()
        .filter(|assignment| character_action_bar_assignment_is_generic_fixed_action(assignment))
        .collect();
    let removed = rows.len();
    for row in rows {
        ctx.db
            .character_action_bar_assignment()
            .key()
            .delete(row.key.clone());
        restore_default_action_bar_assignment_for_slot(
            ctx,
            row.owner,
            row.combat_profile_id.as_str(),
            row.slot_id.as_str(),
            ctx.timestamp,
        );
    }
    removed
}

pub(crate) fn migrate_renamed_melee_action_bar_assignments(ctx: &ReducerContext) -> usize {
    const ID_MIGRATIONS: &[(&str, &str, Option<&str>)] = &[
        ("WARRIOR_SKYFALL_1", "WARRIOR_CRUSHING_BLOW", None),
        ("WARRIOR_SKYFALL_2", "WARRIOR_CATACLYSM", None),
        (
            "WARRIOR_CRUSHING_BLOW",
            "WARRIOR_CATACLYSM",
            Some("slot_0_2"),
        ),
        ("WARRIOR_SKYFALL_3", "WARRIOR_BUZZSAW", None),
        ("WARRIOR_BLADESTORM", "WARRIOR_BUZZSAW", None),
    ];

    let id_migrations: Vec<_> = ID_MIGRATIONS
        .iter()
        .map(|(legacy, replacement, slot_id)| {
            (
                normalize_identifier(legacy),
                ActionRef::ability(replacement),
                slot_id.map(canonical_action_bar_slot_id),
            )
        })
        .collect();
    let mut migrated = 0usize;

    let rows: Vec<_> = ctx.db.character_action_bar_assignment().iter().collect();
    for mut row in rows {
        let action_ref = action_ref_for_character_action_bar_assignment(&row);
        let legacy_ability_field = normalize_identifier(row.ability_id.as_str());
        let row_slot_id = canonical_action_bar_slot_id(row.slot_id.as_str());
        let Some((_, new_action_ref, _)) = id_migrations.iter().find(|(legacy_id, _, slot_id)| {
            if slot_id
                .as_ref()
                .is_some_and(|slot_id| *slot_id != row_slot_id)
            {
                return false;
            }
            (action_ref.kind == ActionKind::Ability && action_ref.id == *legacy_id)
                || legacy_ability_field == *legacy_id
        }) else {
            continue;
        };

        row.action_kind = new_action_ref.kind_wire().to_string();
        row.action_id = new_action_ref.id.clone();
        row.ability_id = legacy_ability_id_for_action_ref(&new_action_ref);
        row.updated_at = ctx.timestamp;
        ctx.db.character_action_bar_assignment().key().update(row);
        migrated = migrated.saturating_add(1);
    }

    migrated
}

pub(crate) fn migrate_generic_spell_action_bar_assignments(ctx: &ReducerContext) -> usize {
    const ID_MIGRATIONS: &[(&str, &str)] = &[
        ("WARRIOR_FIREBALL", "SPELL_FIREBALL"),
        ("WARRIOR_ICICLE", "SPELL_ICICLE"),
        ("WARRIOR_ORBITING_BLADES", "SPELL_ORBITING_BLADES"),
        ("WARRIOR_METEOR", "SPELL_METEOR"),
        ("WARRIOR_LIGHTNING", "SPELL_LIGHTNING"),
        ("WARRIOR_ERUPTION", "SPELL_ERUPTION"),
        ("WARRIOR_ICE_SPIKES", "SPELL_ICE_SPIKES"),
        ("WARRIOR_BOOMERANG_ORB", "SPELL_VAMPIRIC_ORB"),
        ("SPELL_BOOMERANG_ORB", "SPELL_VAMPIRIC_ORB"),
        ("WARRIOR_WITHERING_ORB", "SPELL_WITHERING_ORB"),
        ("WARRIOR_INSTANT_BEAM", "SPELL_INSTANT_BEAM"),
        ("WARRIOR_ELECTROCUTE", "SPELL_ELECTROCUTE"),
        ("WARRIOR_FROST_NOVA", "SPELL_FROST_NOVA"),
        ("WARRIOR_NEGATE", "SPELL_NEGATE"),
    ];

    let id_migrations: Vec<_> = ID_MIGRATIONS
        .iter()
        .map(|(legacy, replacement)| {
            (
                normalize_identifier(legacy),
                ActionRef::ability(replacement),
            )
        })
        .collect();
    let mut migrated = 0usize;

    let rows: Vec<_> = ctx.db.character_action_bar_assignment().iter().collect();
    for mut row in rows {
        let action_ref = action_ref_for_character_action_bar_assignment(&row);
        let legacy_ability_field = normalize_identifier(row.ability_id.as_str());
        let Some((_, new_action_ref)) = id_migrations.iter().find(|(legacy_id, _)| {
            (action_ref.kind == ActionKind::Ability && action_ref.id == *legacy_id)
                || legacy_ability_field == *legacy_id
        }) else {
            continue;
        };

        row.action_kind = new_action_ref.kind_wire().to_string();
        row.action_id = new_action_ref.id.clone();
        row.ability_id = legacy_ability_id_for_action_ref(new_action_ref);
        row.updated_at = ctx.timestamp;
        ctx.db.character_action_bar_assignment().key().update(row);
        migrated = migrated.saturating_add(1);
    }

    migrated
}

pub(crate) fn ensure_default_progression_for_identity(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<(), String> {
    let Some(_player) = ctx.db.player().identity().find(owner) else {
        return Err("player row not found".to_string());
    };
    ensure_default_combat_discipline_state(ctx, owner, ctx.timestamp);
    ensure_default_character_discipline_loadout(ctx, owner, ctx.timestamp);
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
    clear_generic_fixed_action_bar_assignments_for_owner(ctx, owner, ctx.timestamp);
    ensure_default_character_action_bar_assignments(ctx, owner, ctx.timestamp);
    ensure_default_character_discipline_ability_selections(ctx, owner, ctx.timestamp);
    sync_persisted_discipline_action_bars(ctx, owner, ctx.timestamp);

    Ok(())
}

fn resolved_combat_profile_id_for_ability_definition(
    ability: &AbilityDefinition,
) -> Option<String> {
    let combat_profile_id = normalize_identifier(ability.combat_profile_id.as_str());
    if combat_profile_id.is_empty() {
        None
    } else {
        Some(combat_profile_id)
    }
}

fn discipline_id_for_combat_profile(combat_profile_id: &str) -> Option<&'static str> {
    match normalize_identifier(combat_profile_id).as_str() {
        COMBAT_PROFILE_DAGGERS => Some(DISCIPLINE_SUBTLETY),
        COMBAT_PROFILE_TWO_HANDED_SWORD => Some(DISCIPLINE_WAR),
        COMBAT_PROFILE_SWORD_AND_SHIELD => Some(DISCIPLINE_ZEAL),
        COMBAT_PROFILE_ARCHER_BOW => Some(DISCIPLINE_PRECISION),
        "STAFF" => Some(DISCIPLINE_ARCANA),
        _ => None,
    }
}

pub(crate) fn derived_combat_profile_id_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<String> {
    if let Some(profile_id) = equipment_combat_profile_id_for_owner(ctx, owner)
        .filter(|profile_id| combat_profile_exists(ctx, profile_id.as_str()))
    {
        return Some(profile_id);
    }
    if let Some(active) = ctx.db.active_combat_discipline().owner().find(owner) {
        if combat_discipline_is_available(ctx, owner, active.discipline_id.as_str())
            && combat_profile_exists(ctx, active.combat_profile_id.as_str())
        {
            return Some(active.combat_profile_id);
        }
    }
    None
}

fn active_action_bar_combat_profile_id(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<String, String> {
    derived_combat_profile_id_for_owner(ctx, owner)
        .ok_or_else(|| "owner has no resolved combat profile".to_string())
}

pub(crate) fn sync_active_combat_mode_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    if let Some(combat_profile_id) = derived_combat_profile_id_for_owner(ctx, owner) {
        normalize_active_combat_mode_for_profile(ctx, owner, combat_profile_id.as_str(), now);
    } else if ctx.db.active_combat_mode().owner().find(owner).is_some() {
        ctx.db.active_combat_mode().owner().delete(owner);
        crate::survival::on_survival_combat_mode_changed(ctx, owner);
    }
}

pub(crate) fn sync_progression_for_equipment_change(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    ensure_default_combat_discipline_state(ctx, owner, now);
    sync_active_combat_mode_for_owner(ctx, owner, now);
    clear_generic_fixed_action_bar_assignments_for_owner(ctx, owner, now);
    ensure_default_character_action_bar_assignments(ctx, owner, now);
    sync_persisted_discipline_action_bars(ctx, owner, now);
}

pub(crate) fn resolved_auto_attack_mode_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
) -> String {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    if !combat_profile_has_modes(ctx, combat_profile_id.as_str()) {
        return String::new();
    }

    if let Some(active) = ctx.db.active_combat_mode().owner().find(owner) {
        if active.combat_profile_id == combat_profile_id
            && combat_mode_is_valid_for_profile(
                ctx,
                combat_profile_id.as_str(),
                active.mode_id.as_str(),
            )
        {
            return active.mode_id;
        }
    }

    default_combat_mode_for_profile(ctx, combat_profile_id.as_str()).unwrap_or_default()
}

fn normalize_active_combat_mode_for_profile(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    now: Timestamp,
) {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    if combat_profile_id.is_empty() || !combat_profile_has_modes(ctx, combat_profile_id.as_str()) {
        if ctx.db.active_combat_mode().owner().find(owner).is_some() {
            ctx.db.active_combat_mode().owner().delete(owner);
            crate::survival::on_survival_combat_mode_changed(ctx, owner);
        }
        return;
    }

    if let Some(active) = ctx.db.active_combat_mode().owner().find(owner) {
        if active.combat_profile_id == combat_profile_id
            && combat_mode_is_valid_for_profile(
                ctx,
                combat_profile_id.as_str(),
                active.mode_id.as_str(),
            )
        {
            return;
        }
    }

    let Some(mode_id) = default_combat_mode_for_profile(ctx, combat_profile_id.as_str()) else {
        return;
    };
    upsert_active_combat_mode(
        ctx,
        ActiveCombatMode {
            owner,
            combat_profile_id,
            mode_id,
            changed_at: now,
        },
    );
}

pub(crate) fn combat_profile_has_modes(ctx: &ReducerContext, combat_profile_id: &str) -> bool {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    let has_modes = ctx
        .db
        .combat_mode_catalog()
        .combat_profile_id()
        .filter(&combat_profile_id)
        .next()
        .is_some();
    has_modes
}

fn combat_mode_is_valid_for_profile(
    ctx: &ReducerContext,
    combat_profile_id: &str,
    mode_id: &str,
) -> bool {
    ctx.db
        .combat_mode_catalog()
        .key()
        .find(combat_mode_key(combat_profile_id, mode_id))
        .is_some()
}

fn combat_profile_exists(ctx: &ReducerContext, combat_profile_id: &str) -> bool {
    ctx.db
        .combat_profile_catalog()
        .combat_profile_id()
        .find(normalize_identifier(combat_profile_id))
        .is_some()
}

fn default_combat_mode_for_profile(
    ctx: &ReducerContext,
    combat_profile_id: &str,
) -> Option<String> {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    let mut modes: Vec<_> = ctx
        .db
        .combat_mode_catalog()
        .combat_profile_id()
        .filter(&combat_profile_id)
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

fn upsert_active_combat_discipline(ctx: &ReducerContext, row: ActiveCombatDiscipline) {
    if ctx
        .db
        .active_combat_discipline()
        .owner()
        .find(row.owner)
        .is_some()
    {
        ctx.db.active_combat_discipline().owner().update(row);
    } else {
        ctx.db.active_combat_discipline().insert(row);
    }
}

fn upsert_character_discipline_loadout(ctx: &ReducerContext, row: CharacterDisciplineLoadout) {
    if ctx
        .db
        .character_discipline_loadout()
        .owner()
        .find(row.owner)
        .is_some()
    {
        ctx.db.character_discipline_loadout().owner().update(row);
    } else {
        ctx.db.character_discipline_loadout().insert(row);
    }
}

fn upsert_combat_discipline_weapon_loadout(
    ctx: &ReducerContext,
    row: CharacterCombatDisciplineWeaponLoadout,
) {
    if ctx
        .db
        .character_combat_discipline_weapon_loadout()
        .key()
        .find(row.key.clone())
        .is_some()
    {
        ctx.db
            .character_combat_discipline_weapon_loadout()
            .key()
            .update(row);
    } else {
        ctx.db
            .character_combat_discipline_weapon_loadout()
            .insert(row);
    }
}

fn require_combat_discipline(
    ctx: &ReducerContext,
    discipline_id: &str,
) -> Result<CombatDisciplineCatalog, String> {
    let discipline_id = normalize_identifier(discipline_id);
    ctx.db
        .combat_discipline_catalog()
        .discipline_id()
        .find(discipline_id.clone())
        .ok_or_else(|| format!("unknown combat discipline '{}'", discipline_id))
}

fn normalize_selected_secondary_discipline(
    ctx: &ReducerContext,
    discipline_id: String,
) -> Result<String, String> {
    let discipline_id = normalize_identifier(discipline_id.as_str());
    if discipline_id.is_empty() {
        return Ok(String::new());
    }
    Ok(require_combat_discipline(ctx, discipline_id.as_str())?.discipline_id)
}

fn validate_character_discipline_selection(
    primary_discipline_id: &str,
    secondary_discipline_id_1: &str,
    secondary_discipline_id_2: &str,
) -> Result<(), String> {
    if primary_discipline_id.is_empty() {
        return Err("a primary combat discipline is required".to_string());
    }
    if secondary_discipline_id_1 == primary_discipline_id
        || secondary_discipline_id_2 == primary_discipline_id
    {
        return Err("the primary combat discipline cannot also be secondary".to_string());
    }
    if !secondary_discipline_id_1.is_empty()
        && secondary_discipline_id_1 == secondary_discipline_id_2
    {
        return Err("secondary combat disciplines must be unique".to_string());
    }
    Ok(())
}

fn validate_character_discipline_ability_selection(
    ctx: &ReducerContext,
    primary_discipline_id: &str,
    secondary_discipline_id_1: &str,
    secondary_discipline_id_2: &str,
    selected_ability_ids: Vec<String>,
) -> Result<Vec<AbilityCatalog>, String> {
    if selected_ability_ids.len() > MAX_DISCIPLINE_LOADOUT_ABILITIES {
        return Err(format!(
            "discipline loadout may contain at most {MAX_DISCIPLINE_LOADOUT_ABILITIES} abilities"
        ));
    }

    let selected_discipline_ids: HashSet<String> = [
        primary_discipline_id,
        secondary_discipline_id_1,
        secondary_discipline_id_2,
    ]
    .into_iter()
    .map(normalize_identifier)
    .filter(|discipline_id| !discipline_id.is_empty())
    .collect();
    let mut seen = HashSet::new();
    let mut selected = Vec::new();
    let mut counts: HashMap<String, usize> = HashMap::new();

    for ability_id in selected_ability_ids {
        let ability_id = normalize_identifier(ability_id.as_str());
        if ability_id.is_empty() || !seen.insert(ability_id.clone()) {
            continue;
        }

        let ability = require_ability_catalog_row(ctx, ability_id.as_str())?;
        let discipline_id = normalize_identifier(ability.discipline_id.as_str());
        if !selected_discipline_ids.contains(discipline_id.as_str()) {
            return Err(format!(
                "ability '{}' does not belong to a selected discipline",
                ability.ability_id
            ));
        }
        if !ability_tags_allow_discipline_selection(ability.ability_tags.as_str()) {
            return Err(format!(
                "ability '{}' cannot be selected for a discipline loadout",
                ability.ability_id
            ));
        }

        *counts.entry(discipline_id).or_default() += 1;
        selected.push(ability);
    }

    let primary_count = counts
        .get(&normalize_identifier(primary_discipline_id))
        .copied()
        .unwrap_or_default();
    if primary_count < PRIMARY_DISCIPLINE_ABILITY_MINIMUM {
        return Err(format!(
            "primary discipline requires at least {PRIMARY_DISCIPLINE_ABILITY_MINIMUM} selected abilities"
        ));
    }

    for secondary_discipline_id in [secondary_discipline_id_1, secondary_discipline_id_2] {
        let secondary_discipline_id = normalize_identifier(secondary_discipline_id);
        if secondary_discipline_id.is_empty() {
            continue;
        }
        let count = counts
            .get(&secondary_discipline_id)
            .copied()
            .unwrap_or_default();
        if count < SECONDARY_DISCIPLINE_ABILITY_MINIMUM {
            return Err(format!(
                "secondary discipline '{}' requires at least {SECONDARY_DISCIPLINE_ABILITY_MINIMUM} selected ability",
                secondary_discipline_id
            ));
        }
    }

    Ok(selected)
}

fn ability_tags_allow_discipline_selection(ability_tags: &str) -> bool {
    ability_tags.split(',').any(|tag| {
        matches!(
            normalize_identifier(tag).as_str(),
            "ACTION_BAR_ACTION" | "PASSIVE"
        )
    })
}

fn ability_catalog_has_tag(ability: &AbilityCatalog, required_tag: &str) -> bool {
    let required_tag = normalize_identifier(required_tag);
    ability
        .ability_tags
        .split(',')
        .map(normalize_identifier)
        .any(|tag| tag == required_tag)
}

fn discipline_ability_selection_key(owner: Identity, ability_id: &str) -> String {
    format!("{}:{}", owner.to_hex(), normalize_identifier(ability_id))
}

fn replace_character_discipline_ability_selections(
    ctx: &ReducerContext,
    owner: Identity,
    selected_abilities: &[AbilityCatalog],
    now: Timestamp,
) {
    let stale_keys: Vec<String> = ctx
        .db
        .character_discipline_ability_selection()
        .owner()
        .filter(owner)
        .map(|row| row.key)
        .collect();
    for key in stale_keys {
        ctx.db
            .character_discipline_ability_selection()
            .key()
            .delete(key);
    }

    for (index, ability) in selected_abilities.iter().enumerate() {
        ctx.db.character_discipline_ability_selection().insert(
            CharacterDisciplineAbilitySelection {
                key: discipline_ability_selection_key(owner, ability.ability_id.as_str()),
                owner,
                discipline_id: normalize_identifier(ability.discipline_id.as_str()),
                ability_id: normalize_identifier(ability.ability_id.as_str()),
                sort_order: index as u32,
                updated_at: now,
            },
        );
    }
}

fn sync_selected_discipline_action_bars(
    ctx: &ReducerContext,
    owner: Identity,
    selected_abilities: &[AbilityCatalog],
    now: Timestamp,
) {
    let mut existing_placements: Vec<(String, String, String, String)> = ctx
        .db
        .character_action_bar_assignment()
        .owner()
        .filter(owner)
        .filter(|assignment| assignment.combat_profile_id != GLOBAL_ACTION_BAR_PROFILE)
        .filter_map(|assignment| {
            let action_ref = action_ref_for_character_action_bar_assignment(&assignment);
            action_ref.is_ability().then_some((
                assignment.key,
                normalize_identifier(assignment.combat_profile_id.as_str()),
                canonical_action_bar_slot_id(assignment.slot_id.as_str()),
                action_ref.id,
            ))
        })
        .collect();
    existing_placements.sort_by_key(|(_, profile_id, slot_id, _)| {
        (profile_id.clone(), slot_sort_key_for_id(slot_id.as_str()))
    });
    let mut existing_slot_by_ability = HashMap::new();
    for (_, profile_id, slot_id, ability_id) in &existing_placements {
        existing_slot_by_ability
            .entry((profile_id.clone(), ability_id.clone()))
            .or_insert_with(|| slot_id.clone());
    }
    for (key, _, _, _) in existing_placements {
        ctx.db.character_action_bar_assignment().key().delete(key);
    }

    let mut profile_ids = Vec::new();
    for ability in selected_abilities {
        let explicit_profile = normalize_identifier(ability.combat_profile_id.as_str());
        if !explicit_profile.is_empty() && !profile_ids.contains(&explicit_profile) {
            profile_ids.push(explicit_profile);
        }

        let discipline_id = normalize_identifier(ability.discipline_id.as_str());
        let discipline_profile = ctx
            .db
            .combat_discipline_catalog()
            .discipline_id()
            .find(discipline_id)
            .map(|discipline| normalize_identifier(discipline.combat_profile_id.as_str()))
            .unwrap_or_default();
        if !discipline_profile.is_empty() && !profile_ids.contains(&discipline_profile) {
            profile_ids.push(discipline_profile);
        }
    }
    if let Some(active_profile) = derived_combat_profile_id_for_owner(ctx, owner) {
        if !active_profile.is_empty() && !profile_ids.contains(&active_profile) {
            profile_ids.push(active_profile);
        }
    }

    let slot_ids = selectable_slot_ids();
    for profile_id in profile_ids {
        let mut profile_ability_ids = Vec::new();
        for ability in selected_abilities {
            let ability_profile = normalize_identifier(ability.combat_profile_id.as_str());
            let applies_to_profile = ability_profile == profile_id
                || (ability_profile.is_empty()
                    && ability.ability_kind.eq_ignore_ascii_case("SPELL"));
            if applies_to_profile && !profile_ability_ids.contains(&ability.ability_id) {
                profile_ability_ids.push(ability.ability_id.clone());
            }
        }

        let mut placements = Vec::new();
        let mut placed_ability_ids = HashSet::new();
        let mut used_slot_ids = HashSet::new();
        for ability_id in &profile_ability_ids {
            let Some(slot_id) =
                existing_slot_by_ability.get(&(profile_id.clone(), ability_id.clone()))
            else {
                continue;
            };
            if slot_ids.contains(slot_id) && used_slot_ids.insert(slot_id.clone()) {
                placements.push((slot_id.clone(), ability_id.clone()));
                placed_ability_ids.insert(ability_id.clone());
            }
        }
        for ability_id in &profile_ability_ids {
            if placed_ability_ids.contains(ability_id) {
                continue;
            }
            let Some(slot_id) = slot_ids
                .iter()
                .find(|slot_id| !used_slot_ids.contains(*slot_id))
            else {
                break;
            };
            used_slot_ids.insert(slot_id.clone());
            placed_ability_ids.insert(ability_id.clone());
            placements.push((slot_id.clone(), ability_id.clone()));
        }

        for (slot_id, ability_id) in placements {
            upsert_character_action_bar_assignment(
                ctx,
                owner,
                profile_id.as_str(),
                slot_id.as_str(),
                &ActionRef::ability(ability_id.as_str()),
                now,
            );
        }
    }
}

fn sync_persisted_discipline_action_bars(ctx: &ReducerContext, owner: Identity, now: Timestamp) {
    let mut selection_rows: Vec<CharacterDisciplineAbilitySelection> = ctx
        .db
        .character_discipline_ability_selection()
        .owner()
        .filter(owner)
        .collect();
    selection_rows.sort_by_key(|row| (row.sort_order, row.ability_id.clone()));
    let selected_abilities: Vec<AbilityCatalog> = selection_rows
        .into_iter()
        .filter_map(|selection| {
            ctx.db
                .ability_catalog()
                .ability_id()
                .find(normalize_identifier(selection.ability_id.as_str()))
        })
        .collect();
    if !selected_abilities.is_empty() {
        sync_selected_discipline_action_bars(ctx, owner, selected_abilities.as_slice(), now);
    }
}

fn character_discipline_loadout_contains(
    loadout: &CharacterDisciplineLoadout,
    discipline_id: &str,
) -> bool {
    let discipline_id = normalize_identifier(discipline_id);
    !discipline_id.is_empty()
        && (loadout.primary_discipline_id == discipline_id
            || loadout.secondary_discipline_id_1 == discipline_id
            || loadout.secondary_discipline_id_2 == discipline_id)
}

#[allow(dead_code)]
pub(crate) fn character_has_selected_discipline(
    ctx: &ReducerContext,
    owner: Identity,
    discipline_id: &str,
) -> bool {
    ctx.db
        .character_discipline_loadout()
        .owner()
        .find(owner)
        .is_some_and(|loadout| character_discipline_loadout_contains(&loadout, discipline_id))
}

pub(crate) fn character_has_selected_ability(
    ctx: &ReducerContext,
    owner: Identity,
    ability_id: &str,
) -> bool {
    ctx.db
        .character_discipline_ability_selection()
        .key()
        .find(discipline_ability_selection_key(owner, ability_id))
        .is_some()
}

fn combat_discipline_for_profile(
    ctx: &ReducerContext,
    combat_profile_id: &str,
) -> Option<CombatDisciplineCatalog> {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    let discipline = ctx
        .db
        .combat_discipline_catalog()
        .combat_profile_id()
        .filter(&combat_profile_id)
        .next();
    discipline
}

fn combat_discipline_is_available(
    ctx: &ReducerContext,
    owner: Identity,
    discipline_id: &str,
) -> bool {
    let discipline_id = normalize_identifier(discipline_id);
    let key = combat_discipline_weapon_loadout_key(owner, discipline_id.as_str());
    let Some(loadout) = ctx
        .db
        .character_combat_discipline_weapon_loadout()
        .key()
        .find(key)
    else {
        return false;
    };
    combat_discipline_weapon_loadout_is_available(
        ctx,
        owner,
        discipline_id.as_str(),
        loadout.main_hand_item_id.as_deref(),
        loadout.off_hand_item_id.as_deref(),
    )
}

fn ensure_default_combat_discipline_state(ctx: &ReducerContext, owner: Identity, now: Timestamp) {
    let Some(equipped_profile) = equipment_combat_profile_id_for_owner(ctx, owner) else {
        return;
    };
    let Some(discipline) = combat_discipline_for_profile(ctx, equipped_profile.as_str()) else {
        return;
    };
    let loadout_key =
        combat_discipline_weapon_loadout_key(owner, discipline.discipline_id.as_str());
    let should_write_loadout = match ctx
        .db
        .character_combat_discipline_weapon_loadout()
        .key()
        .find(loadout_key.clone())
    {
        Some(loadout) => !combat_discipline_weapon_loadout_is_available(
            ctx,
            owner,
            discipline.discipline_id.as_str(),
            loadout.main_hand_item_id.as_deref(),
            loadout.off_hand_item_id.as_deref(),
        ),
        None => true,
    };
    if should_write_loadout {
        if let Some((main_hand_item_id, off_hand_item_id)) =
            crate::inventory::equipped_weapon_item_ids_for_owner(ctx, owner)
        {
            upsert_combat_discipline_weapon_loadout(
                ctx,
                CharacterCombatDisciplineWeaponLoadout {
                    key: loadout_key,
                    owner,
                    discipline_id: discipline.discipline_id.clone(),
                    main_hand_item_id,
                    off_hand_item_id,
                    updated_at: now,
                },
            );
        }
    }
    if ctx
        .db
        .active_combat_discipline()
        .owner()
        .find(owner)
        .is_none_or(|active| {
            active.discipline_id != discipline.discipline_id
                || active.combat_profile_id != discipline.combat_profile_id
        })
        && combat_discipline_is_available(ctx, owner, discipline.discipline_id.as_str())
    {
        upsert_active_combat_discipline(
            ctx,
            ActiveCombatDiscipline {
                owner,
                discipline_id: discipline.discipline_id,
                combat_profile_id: discipline.combat_profile_id,
                primary_resource_kind: discipline.primary_resource_kind,
                changed_at: now,
            },
        );
    }
}

fn ensure_default_character_discipline_loadout(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    if ctx
        .db
        .character_discipline_loadout()
        .owner()
        .find(owner)
        .is_some()
    {
        return;
    }

    let primary_discipline_id = ctx
        .db
        .active_combat_discipline()
        .owner()
        .find(owner)
        .map(|row| row.discipline_id)
        .or_else(|| {
            ctx.db
                .combat_discipline_catalog()
                .discipline_id()
                .find(DISCIPLINE_WAR.to_string())
                .map(|row| row.discipline_id)
        });
    let Some(primary_discipline_id) = primary_discipline_id else {
        return;
    };

    upsert_character_discipline_loadout(
        ctx,
        CharacterDisciplineLoadout {
            owner,
            primary_discipline_id,
            secondary_discipline_id_1: String::new(),
            secondary_discipline_id_2: String::new(),
            updated_at: now,
        },
    );
}

fn ensure_default_character_discipline_ability_selections(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    if ctx
        .db
        .character_discipline_ability_selection()
        .owner()
        .filter(owner)
        .next()
        .is_some()
    {
        return;
    }

    let Some(loadout) = ctx.db.character_discipline_loadout().owner().find(owner) else {
        return;
    };
    let discipline_requirements = [
        (
            normalize_identifier(loadout.primary_discipline_id.as_str()),
            PRIMARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
        (
            normalize_identifier(loadout.secondary_discipline_id_1.as_str()),
            SECONDARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
        (
            normalize_identifier(loadout.secondary_discipline_id_2.as_str()),
            SECONDARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
    ];

    let mut catalog_abilities: Vec<AbilityCatalog> = ctx.db.ability_catalog().iter().collect();
    catalog_abilities.sort_by_key(|ability| {
        (
            ability.sort_order,
            normalize_identifier(ability.ability_id.as_str()),
        )
    });
    let mut assigned_abilities: Vec<(String, String)> = ctx
        .db
        .character_action_bar_assignment()
        .owner()
        .filter(owner)
        .filter_map(|assignment| {
            let action_ref = action_ref_for_character_action_bar_assignment(&assignment);
            action_ref
                .is_ability()
                .then_some((assignment.slot_id, action_ref.id))
        })
        .collect();
    assigned_abilities.sort_by_key(|(slot_id, _)| slot_sort_key_for_id(slot_id.as_str()));
    let assigned_ability_ids: Vec<String> = assigned_abilities
        .into_iter()
        .map(|(_, ability_id)| ability_id)
        .collect();

    let mut selected = Vec::new();
    let mut selected_ids = HashSet::new();
    for (discipline_id, minimum) in discipline_requirements {
        if discipline_id.is_empty() {
            continue;
        }

        let mut selected_for_discipline = 0usize;
        for ability_id in &assigned_ability_ids {
            if selected_for_discipline >= minimum {
                break;
            }
            let Some(ability) = catalog_abilities.iter().find(|ability| {
                ability.ability_id == *ability_id
                    && normalize_identifier(ability.discipline_id.as_str()) == discipline_id
                    && ability_catalog_has_tag(ability, "ACTION_BAR_ACTION")
            }) else {
                continue;
            };
            if selected_ids.insert(ability.ability_id.clone()) {
                selected.push(ability.clone());
                selected_for_discipline = selected_for_discipline.saturating_add(1);
            }
        }

        for ability in &catalog_abilities {
            if selected_for_discipline >= minimum {
                break;
            }
            if normalize_identifier(ability.discipline_id.as_str()) != discipline_id
                || !ability_catalog_has_tag(ability, "ACTION_BAR_ACTION")
            {
                continue;
            }
            if selected_ids.insert(ability.ability_id.clone()) {
                selected.push(ability.clone());
                selected_for_discipline = selected_for_discipline.saturating_add(1);
            }
        }
    }

    if selected.is_empty() {
        return;
    }
    replace_character_discipline_ability_selections(ctx, owner, selected.as_slice(), now);
}

fn combat_discipline_weapon_loadout_key(owner: Identity, discipline_id: &str) -> String {
    format!("{}:{}", owner.to_hex(), normalize_identifier(discipline_id))
}

fn normalize_optional_item_instance_id(value: Option<String>) -> Option<String> {
    value
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

pub(crate) fn active_stat_totals_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> AllocatedStatTotals {
    equipment_modifier_totals_for_owner(ctx, owner).allocated_stat_totals()
}

pub(crate) fn primary_resource_kind_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<String> {
    let _ = ctx;
    let _ = owner;
    Some(RESOURCE_KIND_STAMINA.to_string())
}

#[allow(dead_code)]
pub(crate) fn selectable_slot_ids() -> Vec<String> {
    let mut slots: Vec<_> = progression_catalog()
        .slots
        .iter()
        .filter(|slot| {
            slot.accepts_tags
                .iter()
                .any(|tag| normalize_identifier(tag) == "ACTION_BAR_ACTION")
        })
        .collect();
    slots.sort_by_key(|slot| slot_sort_key(slot));
    slots
        .into_iter()
        .map(|slot| canonical_action_bar_slot_id(slot.slot_id.as_str()))
        .collect()
}

pub(crate) fn ability_is_compatible_with_slot(ability_id: &str, slot_id: &str) -> bool {
    let Some(ability) = ability_definition(ability_id) else {
        return false;
    };
    let canonical_slot_id = canonical_action_bar_slot_id(slot_id);
    let Some(slot) = slot_definition(canonical_slot_id.as_str()) else {
        return false;
    };
    if slot.accepts_tags.is_empty() {
        return true;
    }
    let ability_tags: HashSet<_> = ability
        .ability_tags
        .iter()
        .map(|tag| normalize_identifier(tag))
        .collect();
    slot.accepts_tags
        .iter()
        .map(|tag| normalize_identifier(tag))
        .any(|tag| ability_tags.contains(tag.as_str()))
}

fn slot_accepts_tag(slot: &ActionBarSlotCatalog, required_tag: &str) -> bool {
    let required_tag = normalize_identifier(required_tag);
    if required_tag.is_empty() {
        return false;
    }
    slot.accepts_tags
        .split(',')
        .map(normalize_identifier)
        .any(|tag| tag == required_tag)
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

fn resolved_combat_profile_id_for_ability_catalog(
    _ctx: &ReducerContext,
    ability: &AbilityCatalog,
) -> Option<String> {
    let combat_profile_id = normalize_identifier(ability.combat_profile_id.as_str());
    if combat_profile_id.is_empty() {
        None
    } else {
        Some(combat_profile_id)
    }
}

fn ability_catalog_matches_combat_profile(
    ctx: &ReducerContext,
    ability: &AbilityCatalog,
    combat_profile_id: &str,
) -> bool {
    let normalized_combat_profile_id = normalize_identifier(combat_profile_id);
    resolved_combat_profile_id_for_ability_catalog(ctx, ability)
        .is_some_and(|ability_profile_id| ability_profile_id == normalized_combat_profile_id)
}

fn ability_catalog_is_active_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    ability: &AbilityCatalog,
    combat_profile_id: &str,
) -> bool {
    if ability_catalog_matches_combat_profile(ctx, ability, combat_profile_id) {
        return true;
    }

    if ability.ability_kind.eq_ignore_ascii_case("SPELL") {
        return player_knows_spell(ctx, owner, ability.action_id.as_str())
            || character_has_selected_ability(ctx, owner, ability.ability_id.as_str());
    }

    false
}

pub(crate) fn active_selectable_ability_for_authored_action(
    ctx: &ReducerContext,
    owner: Identity,
    authored_action_id: &AuthoredActionId,
) -> Option<AbilityCatalog> {
    let combat_profile_id = derived_combat_profile_id_for_owner(ctx, owner)?;
    let ability = ctx
        .db
        .character_action_bar_assignment()
        .owner()
        .filter(owner)
        .filter(|assignment| assignment.combat_profile_id == combat_profile_id)
        .filter_map(|assignment| {
            let action_ref = action_ref_for_character_action_bar_assignment(&assignment);
            if !action_ref.is_ability() {
                return None;
            }
            ctx.db
                .ability_catalog()
                .ability_id()
                .find(action_ref.id)
                .map(|ability| (assignment.slot_id, ability))
        })
        .find(|(slot_id, ability)| {
            ability.action_id == authored_action_id.as_str()
                && ability_catalog_is_active_for_owner(
                    ctx,
                    owner,
                    ability,
                    combat_profile_id.as_str(),
                )
                && character_action_bar_assignment_is_enabled(
                    ctx,
                    owner,
                    combat_profile_id.as_str(),
                    slot_id.as_str(),
                    ability,
                )
        })
        .map(|(_, ability)| ability);
    ability
}

pub(crate) fn active_selectable_ability_for_ability_id(
    ctx: &ReducerContext,
    owner: Identity,
    ability_id: &str,
) -> Option<AbilityCatalog> {
    let normalized_ability_id = normalize_identifier(ability_id);
    let combat_profile_id = derived_combat_profile_id_for_owner(ctx, owner)?;
    let ability = ctx
        .db
        .character_action_bar_assignment()
        .owner()
        .filter(owner)
        .filter(|assignment| assignment.combat_profile_id == combat_profile_id)
        .filter_map(|assignment| {
            let action_ref = action_ref_for_character_action_bar_assignment(&assignment);
            if !action_ref.is_ability() || action_ref.id != normalized_ability_id {
                return None;
            }
            ctx.db
                .ability_catalog()
                .ability_id()
                .find(action_ref.id)
                .map(|ability| (assignment.slot_id, ability))
        })
        .filter(|(slot_id, ability)| {
            ability_catalog_is_active_for_owner(ctx, owner, ability, combat_profile_id.as_str())
                && character_action_bar_assignment_is_enabled(
                    ctx,
                    owner,
                    combat_profile_id.as_str(),
                    slot_id.as_str(),
                    ability,
                )
        })
        .map(|(_, ability)| ability)
        .next();
    ability
}

pub(crate) fn active_action_bar_assignment_debug_summary(
    ctx: &ReducerContext,
    owner: Identity,
) -> String {
    let combat_profile_id = derived_combat_profile_id_for_owner(ctx, owner).unwrap_or_default();
    let assignments: Vec<String> = ctx
        .db
        .character_action_bar_assignment()
        .owner()
        .filter(owner)
        .filter(|assignment| {
            combat_profile_id.is_empty() || assignment.combat_profile_id == combat_profile_id
        })
        .map(|assignment| {
            format!(
                "{}:{}:{}:{}:{}",
                assignment.combat_profile_id,
                assignment.slot_id,
                assignment.action_kind,
                assignment.action_id,
                assignment.ability_id
            )
        })
        .collect();

    format!("character_action_bar=[{}]", assignments.join(","))
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
                    MeleeImpactEffectDefinition::RemoveStatus {
                        polarity,
                        dispel_types,
                        max_count,
                    } => MeleeImpactEffectRuntime::RemoveStatus {
                        polarity: *polarity,
                        dispel_types: dispel_types.clone(),
                        max_count: *max_count,
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

pub(crate) fn melee_channel_for_ability_id(ability_id: &str) -> Option<MeleeChannelRuntime> {
    let definition = ability_definition(ability_id)?;
    if ability_gameplay_kind(definition) != "MELEE" {
        return None;
    }
    let channel = definition.gameplay.melee_channel.as_ref()?;
    Some(MeleeChannelRuntime {
        duration_ms: channel.duration_ms,
        first_tick_delay_ms: channel.first_tick_delay_ms,
        tick_interval_ms: channel.tick_interval_ms,
        cancel_on_movement: channel.cancel_on_movement,
    })
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

fn sync_combat_profile_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .combat_profiles
        .iter()
        .map(|definition| normalize_identifier(definition.combat_profile_id.as_str()))
        .collect();

    for definition in &progression_catalog().combat_profiles {
        let combat_profile_id = normalize_identifier(definition.combat_profile_id.as_str());
        let row = CombatProfileCatalog {
            combat_profile_id: combat_profile_id.clone(),
            display_name: definition.display_name.clone(),
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .combat_profile_catalog()
            .combat_profile_id()
            .find(combat_profile_id.clone())
            .is_some()
        {
            ctx.db
                .combat_profile_catalog()
                .combat_profile_id()
                .update(row);
        } else {
            ctx.db.combat_profile_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .combat_profile_catalog()
        .iter()
        .map(|row| row.combat_profile_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db
            .combat_profile_catalog()
            .combat_profile_id()
            .delete(key);
    }
}

fn sync_combat_discipline_catalog(ctx: &ReducerContext) {
    validate_combat_discipline_catalog();
    let expected: HashSet<_> = progression_catalog()
        .combat_disciplines
        .iter()
        .map(|definition| normalize_identifier(definition.discipline_id.as_str()))
        .collect();

    for definition in &progression_catalog().combat_disciplines {
        let discipline_id = normalize_identifier(definition.discipline_id.as_str());
        let row = CombatDisciplineCatalog {
            discipline_id: discipline_id.clone(),
            discipline_kind: normalize_identifier(definition.discipline_kind.as_str()),
            combat_profile_id: normalize_identifier(definition.combat_profile_id.as_str()),
            display_name: definition.display_name.clone(),
            primary_resource_kind: normalize_identifier(definition.primary_resource_kind.as_str()),
            inactive_resource_tick: definition.inactive_resource_tick,
            inactive_decay_delay_ms: definition.inactive_decay_delay_ms,
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .combat_discipline_catalog()
            .discipline_id()
            .find(discipline_id.clone())
            .is_some()
        {
            ctx.db
                .combat_discipline_catalog()
                .discipline_id()
                .update(row);
        } else {
            ctx.db.combat_discipline_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .combat_discipline_catalog()
        .iter()
        .map(|row| row.discipline_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db
            .combat_discipline_catalog()
            .discipline_id()
            .delete(key);
    }
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
            combat_profile_id: normalize_identifier(definition.combat_profile_id.as_str()),
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
            discipline_id: normalize_identifier(definition.discipline_id.as_str()),
            combat_profile_id: resolved_combat_profile_id_for_ability_definition(definition)
                .unwrap_or_default(),
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
            discipline_id: normalize_identifier(definition.discipline_id.as_str()),
            combat_profile_id: normalize_identifier(definition.combat_profile_id.as_str()),
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
            combat_profile_id: normalize_identifier(definition.combat_profile_id.as_str()),
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

fn ensure_default_character_action_bar_assignments(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> usize {
    let mut inserted = 0usize;
    inserted = inserted.saturating_add(ensure_default_action_bar_assignments_for_scope(
        ctx,
        owner,
        GLOBAL_ACTION_BAR_PROFILE,
        now,
    ));
    if let Some(combat_profile_id) = derived_combat_profile_id_for_owner(ctx, owner) {
        inserted = inserted.saturating_add(ensure_default_action_bar_assignments_for_scope(
            ctx,
            owner,
            combat_profile_id.as_str(),
            now,
        ));
    }
    inserted
}

fn ensure_default_action_bar_assignments_for_scope(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    now: Timestamp,
) -> usize {
    let mut inserted = 0usize;
    let combat_profile_id = normalize_identifier(combat_profile_id);
    for assignment in action_bar_defaults_for_profile(combat_profile_id.as_str()) {
        let slot_id = canonical_action_bar_slot_id(assignment.slot_id.as_str());
        let key = character_action_bar_key(owner, combat_profile_id.as_str(), slot_id.as_str());
        if ctx
            .db
            .character_action_bar_assignment()
            .key()
            .find(key)
            .is_some()
        {
            continue;
        }

        let action_ref = action_ref_for_action_bar_default(assignment);
        if validate_character_action_bar_ref(
            ctx,
            owner,
            combat_profile_id.as_str(),
            slot_id.as_str(),
            &action_ref,
        )
        .is_err()
        {
            continue;
        }

        upsert_character_action_bar_assignment(
            ctx,
            owner,
            combat_profile_id.as_str(),
            slot_id.as_str(),
            &action_ref,
            now,
        );
        inserted = inserted.saturating_add(1);
    }

    inserted
}

fn restore_default_action_bar_assignment_for_slot(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    slot_id: &str,
    now: Timestamp,
) -> bool {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    let slot_id = canonical_action_bar_slot_id(slot_id);
    if ctx
        .db
        .character_action_bar_assignment()
        .key()
        .find(character_action_bar_key(
            owner,
            combat_profile_id.as_str(),
            slot_id.as_str(),
        ))
        .is_some()
    {
        return false;
    }

    let Some(assignment) = action_bar_defaults_for_profile(combat_profile_id.as_str())
        .into_iter()
        .find(|assignment| canonical_action_bar_slot_id(assignment.slot_id.as_str()) == slot_id)
    else {
        return false;
    };

    let action_ref = action_ref_for_action_bar_default(assignment);
    if validate_character_action_bar_ref(
        ctx,
        owner,
        combat_profile_id.as_str(),
        slot_id.as_str(),
        &action_ref,
    )
    .is_err()
    {
        return false;
    }

    upsert_character_action_bar_assignment(
        ctx,
        owner,
        combat_profile_id.as_str(),
        slot_id.as_str(),
        &action_ref,
        now,
    );
    true
}

const SWORD_AND_SHIELD_VISIBLE_DEFAULT_ABILITY_IDS: &[&str] = &[
    "PALADIN_FERVOR",
    "PALADIN_MANA_FONT",
    "PALADIN_STAMINA_FONT",
    "PALADIN_THORNS_AURA",
    "PALADIN_WARDING_AURA",
    "PALADIN_AURA_OF_VENGEANCE",
    "PALADIN_BLESSED_SHIELD",
    "PALADIN_BLADE_BARRIER",
];

pub(crate) fn backfill_sword_and_shield_visible_action_bar_rows(ctx: &ReducerContext) -> usize {
    let players: Vec<Player> = ctx.db.player().iter().collect();
    let mut inserted = 0usize;

    for player in players {
        let is_dummy = ctx
            .db
            .player_state()
            .player_id()
            .find(player.identity)
            .map(|state| state.is_dummy)
            .unwrap_or(false);
        if is_dummy {
            continue;
        }
        if derived_combat_profile_id_for_owner(ctx, player.identity).as_deref()
            != Some(COMBAT_PROFILE_SWORD_AND_SHIELD)
        {
            continue;
        }
        inserted = inserted.saturating_add(ensure_sword_and_shield_visible_defaults_for_owner(
            ctx,
            player.identity,
            ctx.timestamp,
        ));
    }

    inserted
}

fn ensure_sword_and_shield_visible_defaults_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> usize {
    let mut inserted = 0usize;
    for assignment in action_bar_defaults_for_profile(COMBAT_PROFILE_SWORD_AND_SHIELD) {
        let ability_id = normalize_identifier(assignment.ability_id.as_str());
        if !SWORD_AND_SHIELD_VISIBLE_DEFAULT_ABILITY_IDS.contains(&ability_id.as_str()) {
            continue;
        }
        if character_action_bar_has_ability(
            ctx,
            owner,
            COMBAT_PROFILE_SWORD_AND_SHIELD,
            ability_id.as_str(),
        ) {
            continue;
        }

        let slot_id = canonical_action_bar_slot_id(assignment.slot_id.as_str());
        let key =
            character_action_bar_key(owner, COMBAT_PROFILE_SWORD_AND_SHIELD, slot_id.as_str());
        if ctx
            .db
            .character_action_bar_assignment()
            .key()
            .find(key)
            .is_some()
        {
            continue;
        }

        let action_ref = action_ref_for_action_bar_default(assignment);
        if validate_character_action_bar_ref(
            ctx,
            owner,
            COMBAT_PROFILE_SWORD_AND_SHIELD,
            slot_id.as_str(),
            &action_ref,
        )
        .is_err()
        {
            continue;
        }

        upsert_character_action_bar_assignment(
            ctx,
            owner,
            COMBAT_PROFILE_SWORD_AND_SHIELD,
            slot_id.as_str(),
            &action_ref,
            now,
        );
        inserted = inserted.saturating_add(1);
    }

    inserted
}

fn character_action_bar_has_ability(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    ability_id: &str,
) -> bool {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    let ability_id = normalize_identifier(ability_id);
    ctx.db
        .character_action_bar_assignment()
        .owner()
        .filter(owner)
        .any(|assignment| {
            assignment.combat_profile_id == combat_profile_id
                && action_ref_for_character_action_bar_assignment(&assignment).id == ability_id
        })
}

fn character_action_bar_key(owner: Identity, combat_profile_id: &str, slot_id: &str) -> String {
    format!(
        "{}:{}:{}",
        owner.to_hex(),
        normalize_identifier(combat_profile_id),
        canonical_action_bar_slot_id(slot_id)
    )
}

fn action_bar_defaults_for_profile(
    combat_profile_id: &str,
) -> Vec<&'static CombatProfileActionBarDefaultDefinition> {
    let normalized_combat_profile_id = normalize_identifier(combat_profile_id);
    let mut assignments: Vec<_> = progression_catalog()
        .combat_profile_action_bar_defaults
        .iter()
        .filter(|assignment| {
            normalize_identifier(assignment.combat_profile_id.as_str())
                == normalized_combat_profile_id
        })
        .collect();
    assignments.sort_by_key(|assignment| {
        (
            assignment.sort_order,
            slot_sort_key_for_id(assignment.slot_id.as_str()),
            normalize_identifier(assignment.ability_id.as_str()),
        )
    });
    assignments
}

fn action_ref_for_action_bar_default(
    assignment: &CombatProfileActionBarDefaultDefinition,
) -> ActionRef {
    let action_kind = normalize_identifier(assignment.action_kind.as_str());
    let action_id = normalize_identifier(assignment.action_id.as_str());
    if !action_kind.is_empty() || !action_id.is_empty() {
        return ActionRef::from_wire(action_kind.as_str(), action_id.as_str());
    }
    ActionRef::ability(assignment.ability_id.as_str())
}

#[allow(dead_code)]
fn slot_sort_key(slot: &ActionBarSlotDefinition) -> (u32, u32, u32) {
    (slot.ui_row, slot.ui_col, slot.sort_order)
}

#[allow(dead_code)]
fn slot_sort_key_for_id(slot_id: &str) -> (u32, u32, u32) {
    slot_definition(slot_id)
        .map(slot_sort_key)
        .unwrap_or((u32::MAX, u32::MAX, u32::MAX))
}

fn require_ability_catalog_row(
    ctx: &ReducerContext,
    ability_id: &str,
) -> Result<AbilityCatalog, String> {
    let normalized = normalize_identifier(ability_id);
    ctx.db
        .ability_catalog()
        .ability_id()
        .find(normalized.clone())
        .ok_or_else(|| format!("ability '{}' not found in catalog", normalized))
}

fn require_slot_catalog_row(
    ctx: &ReducerContext,
    slot_id: &str,
) -> Result<ActionBarSlotCatalog, String> {
    let normalized = canonical_action_bar_slot_id(slot_id);
    ctx.db
        .action_bar_slot_catalog()
        .slot_id()
        .find(normalized.clone())
        .ok_or_else(|| format!("slot '{}' not found in catalog", normalized))
}

fn validate_character_action_bar_ref(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    slot_id: &str,
    action_ref: &ActionRef,
) -> Result<(), String> {
    let slot = require_slot_catalog_row(ctx, slot_id)?;
    let combat_profile_id = normalize_identifier(combat_profile_id);
    let is_global_scope = combat_profile_id == GLOBAL_ACTION_BAR_PROFILE;
    if combat_profile_id.is_empty()
        || (!is_global_scope && !combat_profile_exists(ctx, combat_profile_id.as_str()))
    {
        return Err(format!(
            "combat profile '{}' is not available for action bar assignment",
            combat_profile_id
        ));
    }
    match &action_ref.kind {
        ActionKind::Ability => {
            let ability = require_ability_catalog_row(ctx, action_ref.id.as_str())?;
            let is_spell = ability.ability_kind.eq_ignore_ascii_case("SPELL");
            let matches_combat_profile =
                ability_catalog_matches_combat_profile(ctx, &ability, combat_profile_id.as_str());
            if !is_spell && !matches_combat_profile {
                let ability_profile_id =
                    resolved_combat_profile_id_for_ability_catalog(ctx, &ability)
                        .unwrap_or_default();
                return Err(format!(
                    "ability '{}' requires combat profile '{}' but owner has combat profile '{}'",
                    ability.ability_id, ability_profile_id, combat_profile_id
                ));
            }
            if is_spell
                && !matches_combat_profile
                && !player_knows_spell(ctx, owner, ability.action_id.as_str())
                && !character_has_selected_ability(ctx, owner, ability.ability_id.as_str())
            {
                return Err(format!(
                    "spell ability '{}' requires learned spell '{}'",
                    ability.ability_id, ability.action_id
                ));
            }
            if is_spell
                && !matches_combat_profile
                && !character_has_selected_ability(ctx, owner, ability.ability_id.as_str())
            {
                require_available_spell_slot_for_assignment(
                    ctx,
                    owner,
                    combat_profile_id.as_str(),
                    slot.slot_id.as_str(),
                )?;
            }
            if !ability_is_compatible_with_slot(action_ref.id.as_str(), slot.slot_id.as_str()) {
                return Err(format!(
                    "ability '{}' is not compatible with slot '{}'",
                    ability.ability_id, slot.slot_id
                ));
            }
            Ok(())
        }
        ActionKind::CombatDisciplineSwitch => {
            if !is_global_scope {
                return Err(
                    "combat discipline switch actions must use GLOBAL action-bar scope".to_string(),
                );
            }
            let discipline = require_combat_discipline(ctx, action_ref.id.as_str())?;
            if !slot_accepts_tag(&slot, "DISCIPLINE_SWITCH") {
                return Err(format!(
                    "combat discipline '{}' is not compatible with slot '{}'",
                    discipline.discipline_id, slot.slot_id
                ));
            }
            Ok(())
        }
        ActionKind::Fixed => {
            let fixed_action_id = FixedActionId::from_wire(action_ref.id.as_str());
            match &fixed_action_id {
                FixedActionId::Dodge | FixedActionId::Parry => Err(format!(
                    "fixed action '{}' is a generic keybind and cannot be assigned to an action-bar slot",
                    fixed_action_id.as_wire()
                )),
                FixedActionId::Unsupported(value) => {
                    Err(format!("unsupported fixed action '{value}'"))
                }
            }
        }
        ActionKind::Unsupported(kind) => Err(format!("unsupported action kind '{kind}'")),
    }
}

fn require_available_spell_slot_for_assignment(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    slot_id: &str,
) -> Result<(), String> {
    let capacity = equipment_spell_slot_capacity_for_owner(ctx, owner);
    let used = assigned_spell_count_excluding_slot(ctx, owner, combat_profile_id, slot_id);
    if used >= capacity {
        return Err(format!(
            "spell slot capacity exceeded: {used}/{capacity} spell slots are already assigned"
        ));
    }

    Ok(())
}

fn character_action_bar_assignment_is_enabled(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    slot_id: &str,
    ability: &AbilityCatalog,
) -> bool {
    if !ability.ability_kind.eq_ignore_ascii_case("SPELL") {
        return true;
    }
    if ability_catalog_matches_combat_profile(ctx, ability, combat_profile_id) {
        return true;
    }

    if character_has_selected_ability(ctx, owner, ability.ability_id.as_str()) {
        return true;
    }

    character_action_bar_spell_assignment_is_within_capacity(ctx, owner, combat_profile_id, slot_id)
}

fn character_action_bar_spell_assignment_is_within_capacity(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    slot_id: &str,
) -> bool {
    let capacity = equipment_spell_slot_capacity_for_owner(ctx, owner) as usize;
    if capacity == 0 {
        return false;
    }

    let slot_id = canonical_action_bar_slot_id(slot_id);
    let mut spell_slots = assigned_spell_slot_ids(ctx, owner, combat_profile_id);
    spell_slots.sort();
    spell_slots
        .iter()
        .position(|candidate| candidate == &slot_id)
        .map(|index| index < capacity)
        .unwrap_or(false)
}

fn assigned_spell_count_excluding_slot(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    excluded_slot_id: &str,
) -> u32 {
    let excluded_slot_id = canonical_action_bar_slot_id(excluded_slot_id);
    assigned_spell_slot_ids(ctx, owner, combat_profile_id)
        .into_iter()
        .filter(|slot_id| slot_id != &excluded_slot_id)
        .count() as u32
}

fn assigned_spell_slot_ids(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
) -> Vec<String> {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    ctx.db
        .character_action_bar_assignment()
        .owner()
        .filter(owner)
        .filter(|assignment| assignment.combat_profile_id == combat_profile_id)
        .filter_map(|assignment| {
            let action_ref = action_ref_for_character_action_bar_assignment(&assignment);
            if !action_ref.is_ability() {
                return None;
            }
            let ability = ctx.db.ability_catalog().ability_id().find(action_ref.id)?;
            if ability.ability_kind.eq_ignore_ascii_case("SPELL")
                && !ability_catalog_matches_combat_profile(
                    ctx,
                    &ability,
                    combat_profile_id.as_str(),
                )
            {
                Some(canonical_action_bar_slot_id(assignment.slot_id.as_str()))
            } else {
                None
            }
        })
        .collect()
}

fn action_ref_for_character_action_bar_assignment(
    assignment: &CharacterActionBarAssignment,
) -> ActionRef {
    let action_kind = normalize_identifier(assignment.action_kind.as_str());
    let action_id = normalize_identifier(assignment.action_id.as_str());
    if !action_kind.is_empty() || !action_id.is_empty() {
        return ActionRef::from_wire(action_kind.as_str(), action_id.as_str());
    }
    ActionRef::ability(assignment.ability_id.as_str())
}

fn character_action_bar_assignment_is_generic_fixed_action(
    assignment: &CharacterActionBarAssignment,
) -> bool {
    let action_ref = action_ref_for_character_action_bar_assignment(assignment);
    let action_id = normalize_identifier(assignment.action_id.as_str());
    let ability_id = normalize_identifier(assignment.ability_id.as_str());
    (action_ref.kind == ActionKind::Fixed
        && matches!(
            action_ref.id.as_str(),
            FIXED_ACTION_DODGE | FIXED_ACTION_PARRY
        ))
        || matches!(action_id.as_str(), FIXED_ACTION_DODGE | FIXED_ACTION_PARRY)
        || matches!(ability_id.as_str(), FIXED_ACTION_DODGE | FIXED_ACTION_PARRY)
}

fn clear_generic_fixed_action_bar_assignments_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> usize {
    let rows: Vec<CharacterActionBarAssignment> = ctx
        .db
        .character_action_bar_assignment()
        .owner()
        .filter(owner)
        .filter(|assignment| character_action_bar_assignment_is_generic_fixed_action(assignment))
        .collect();
    let removed = rows.len();
    for row in rows {
        ctx.db
            .character_action_bar_assignment()
            .key()
            .delete(row.key.clone());
        restore_default_action_bar_assignment_for_slot(
            ctx,
            row.owner,
            row.combat_profile_id.as_str(),
            row.slot_id.as_str(),
            now,
        );
    }
    removed
}

fn legacy_ability_id_for_action_ref(action_ref: &ActionRef) -> String {
    if action_ref.is_ability() {
        action_ref.id.clone()
    } else {
        String::new()
    }
}

fn upsert_character_action_bar_assignment(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile_id: &str,
    slot_id: &str,
    action_ref: &ActionRef,
    updated_at: Timestamp,
) {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    let key = character_action_bar_key(owner, combat_profile_id.as_str(), slot_id);
    let row = CharacterActionBarAssignment {
        key: key.clone(),
        owner,
        combat_profile_id,
        slot_id: canonical_action_bar_slot_id(slot_id),
        action_kind: action_ref.kind_wire().to_string(),
        action_id: action_ref.id.clone(),
        ability_id: legacy_ability_id_for_action_ref(action_ref),
        updated_at,
    };
    if ctx
        .db
        .character_action_bar_assignment()
        .key()
        .find(key)
        .is_some()
    {
        ctx.db.character_action_bar_assignment().key().update(row);
    } else {
        ctx.db.character_action_bar_assignment().insert(row);
    }
}

fn ability_definition(ability_id: &str) -> Option<&'static AbilityDefinition> {
    let ability_id = normalize_identifier(ability_id);
    progression_catalog()
        .abilities
        .iter()
        .find(|definition| normalize_identifier(definition.ability_id.as_str()) == ability_id)
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

fn slot_definition(slot_id: &str) -> Option<&'static ActionBarSlotDefinition> {
    let slot_id = canonical_action_bar_slot_id(slot_id);
    progression_catalog()
        .slots
        .iter()
        .find(|definition| canonical_action_bar_slot_id(definition.slot_id.as_str()) == slot_id)
}

fn auto_attack_catalog_key(definition: &AutoAttackDefinition) -> String {
    auto_attack_catalog_key_for(
        definition.combat_profile_id.as_str(),
        definition.mode_id.as_str(),
        AuthoredActionId::new(definition.action_id.as_str()).as_str(),
    )
}

fn auto_attack_catalog_key_for(combat_profile_id: &str, mode_id: &str, action_id: &str) -> String {
    let combat_profile_id = normalize_identifier(combat_profile_id);
    let mode_id = normalize_identifier(mode_id);
    let action_id = AuthoredActionId::new(action_id).into_string();
    if mode_id.is_empty() {
        format!("{combat_profile_id}:{action_id}")
    } else {
        format!("{combat_profile_id}:{mode_id}:{action_id}")
    }
}

fn combat_mode_catalog_key(definition: &CombatModeDefinition) -> String {
    combat_mode_key(
        definition.combat_profile_id.as_str(),
        definition.mode_id.as_str(),
    )
}

fn combat_mode_key(combat_profile_id: &str, mode_id: &str) -> String {
    format!(
        "{}:{}",
        normalize_identifier(combat_profile_id),
        normalize_identifier(mode_id)
    )
}

fn validate_combat_discipline_catalog() {
    let known_profiles: HashSet<_> = progression_catalog()
        .combat_profiles
        .iter()
        .map(|profile| normalize_identifier(profile.combat_profile_id.as_str()))
        .collect();
    let known_resources: HashSet<_> = progression_catalog()
        .resources
        .iter()
        .map(|resource| normalize_identifier(resource.resource_kind.as_str()))
        .collect();
    let mut discipline_ids = HashSet::new();
    let mut profile_ids = HashSet::new();
    let expected_disciplines = HashSet::from([
        DISCIPLINE_SUBTLETY.to_string(),
        DISCIPLINE_WAR.to_string(),
        DISCIPLINE_ZEAL.to_string(),
        DISCIPLINE_PRECISION.to_string(),
        DISCIPLINE_BLIGHT.to_string(),
        DISCIPLINE_RUIN.to_string(),
        DISCIPLINE_DIVINITY.to_string(),
        DISCIPLINE_ARCANA.to_string(),
        DISCIPLINE_PRIMAL.to_string(),
    ]);
    for discipline in &progression_catalog().combat_disciplines {
        let discipline_id = normalize_identifier(discipline.discipline_id.as_str());
        let discipline_kind = normalize_identifier(discipline.discipline_kind.as_str());
        let combat_profile_id = normalize_identifier(discipline.combat_profile_id.as_str());
        let resource_kind = normalize_identifier(discipline.primary_resource_kind.as_str());
        assert!(
            !discipline_id.is_empty(),
            "combat discipline id must not be empty"
        );
        assert!(
            discipline_ids.insert(discipline_id.clone()),
            "duplicate combat discipline '{}'",
            discipline_id
        );
        let (expected_kind, expected_profile_id) = match discipline_id.as_str() {
            DISCIPLINE_SUBTLETY => (DISCIPLINE_KIND_WEAPON, COMBAT_PROFILE_DAGGERS),
            DISCIPLINE_WAR => (DISCIPLINE_KIND_WEAPON, COMBAT_PROFILE_TWO_HANDED_SWORD),
            DISCIPLINE_ZEAL => (DISCIPLINE_KIND_WEAPON, COMBAT_PROFILE_SWORD_AND_SHIELD),
            DISCIPLINE_PRECISION => (DISCIPLINE_KIND_WEAPON, COMBAT_PROFILE_ARCHER_BOW),
            DISCIPLINE_ARCANA => (DISCIPLINE_KIND_SPELL_SCHOOL, "STAFF"),
            DISCIPLINE_BLIGHT | DISCIPLINE_RUIN | DISCIPLINE_DIVINITY | DISCIPLINE_PRIMAL => {
                (DISCIPLINE_KIND_SPELL_SCHOOL, "")
            }
            _ => panic!("unsupported combat discipline '{}'", discipline_id),
        };
        assert_eq!(
            discipline_kind, expected_kind,
            "combat discipline '{}' must use discipline_kind '{}'",
            discipline_id, expected_kind
        );
        assert_eq!(
            combat_profile_id, expected_profile_id,
            "combat discipline '{}' must use combat_profile_id '{}'",
            discipline_id, expected_profile_id
        );
        if combat_profile_id.is_empty() {
            assert_eq!(
                discipline_kind, DISCIPLINE_KIND_SPELL_SCHOOL,
                "only spell-school disciplines may omit combat_profile_id"
            );
        } else {
            assert!(
                known_profiles.contains(combat_profile_id.as_str()),
                "combat discipline '{}' references unknown combat profile '{}'",
                discipline_id,
                discipline.combat_profile_id
            );
            assert!(
                profile_ids.insert(combat_profile_id.clone()),
                "combat profile '{}' is assigned to multiple disciplines",
                combat_profile_id
            );
        }
        if discipline_kind == DISCIPLINE_KIND_WEAPON {
            assert!(
                known_resources.contains(resource_kind.as_str()),
                "combat discipline '{}' references unknown resource '{}'",
                discipline_id,
                discipline.primary_resource_kind
            );
            assert_eq!(
                resource_kind, RESOURCE_KIND_STAMINA,
                "weapon discipline '{}' must use the shared standard resource policy '{}'",
                discipline_id, RESOURCE_KIND_STAMINA
            );
        } else {
            assert!(
                resource_kind.is_empty(),
                "spell-school discipline '{}' must not own a primary resource; costs belong to abilities",
                discipline_id
            );
            assert!(
                !discipline.inactive_resource_tick,
                "spell-school discipline '{}' must not author inactive resource ticking",
                discipline_id
            );
        }
    }
    assert_eq!(
        discipline_ids, expected_disciplines,
        "combat discipline ids must stay aligned with authored discipline constants"
    );
}

fn validate_combat_mode_catalog() {
    let known_profiles: HashSet<_> = progression_catalog()
        .combat_profiles
        .iter()
        .map(|profile| normalize_identifier(profile.combat_profile_id.as_str()))
        .collect();
    let mut keys = HashSet::new();
    for mode in &progression_catalog().combat_modes {
        let profile_id = normalize_identifier(mode.combat_profile_id.as_str());
        let mode_id = normalize_identifier(mode.mode_id.as_str());
        assert!(
            !profile_id.is_empty(),
            "combat mode profile must not be empty"
        );
        assert!(!mode_id.is_empty(), "combat mode id must not be empty");
        assert!(
            known_profiles.contains(profile_id.as_str()),
            "combat mode '{}' references unknown combat profile '{}'",
            mode.mode_id,
            mode.combat_profile_id
        );
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
            .find(|mode| normalize_identifier(mode.combat_profile_id.as_str())
                == COMBAT_PROFILE_ARCHER_BOW
                && normalize_identifier(mode.mode_id.as_str()) == COMBAT_MODE_FULL_DRAW)
            .map(|mode| mode.is_default),
        Some(true),
        "ARCHER_BOW must default to FULL_DRAW"
    );

    assert_eq!(
        progression_catalog()
            .combat_modes
            .iter()
            .find(|mode| normalize_identifier(mode.combat_profile_id.as_str())
                == COMBAT_PROFILE_DAGGERS
                && normalize_identifier(mode.mode_id.as_str()) == COMBAT_MODE_READY)
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
        let discipline_id = normalize_identifier(attack.discipline_id.as_str());
        let expected_discipline_id = discipline_id_for_combat_profile(
            normalize_identifier(attack.combat_profile_id.as_str()).as_str(),
        )
        .expect("auto attack combat profile must have a discipline");
        assert_eq!(
            discipline_id, expected_discipline_id,
            "auto attack '{}' must belong to discipline '{}' for combat profile '{}'",
            attack.action_id, expected_discipline_id, attack.combat_profile_id
        );
        if !mode_id.is_empty() {
            let mode_key = combat_mode_key(attack.combat_profile_id.as_str(), mode_id.as_str());
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

        if normalize_identifier(attack.combat_profile_id.as_str()) == COMBAT_PROFILE_ARCHER_BOW
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
    let known_profiles: HashSet<_> = progression_catalog()
        .combat_profiles
        .iter()
        .map(|profile| normalize_identifier(profile.combat_profile_id.as_str()))
        .collect();
    let discipline_kinds: HashMap<_, _> = progression_catalog()
        .combat_disciplines
        .iter()
        .map(|discipline| {
            (
                normalize_identifier(discipline.discipline_id.as_str()),
                normalize_identifier(discipline.discipline_kind.as_str()),
            )
        })
        .collect();

    for ability in &progression_catalog().abilities {
        let ability_id = normalize_identifier(ability.ability_id.as_str());
        let actor_scope =
            validated_ability_actor_scope(ability_id.as_str(), ability.actor_scope.as_str());
        let explicit_combat_profile_id = normalize_identifier(ability.combat_profile_id.as_str());
        let discipline_id = normalize_identifier(ability.discipline_id.as_str());
        if !explicit_combat_profile_id.is_empty() {
            assert!(
                known_profiles.contains(explicit_combat_profile_id.as_str()),
                "ability '{ability_id}' references unknown combat_profile_id '{}'",
                ability.combat_profile_id
            );
        }

        if actor_scope != "NPC" {
            let discipline_kind =
                discipline_kinds
                    .get(discipline_id.as_str())
                    .unwrap_or_else(|| {
                        panic!(
                            "player ability '{ability_id}' references unknown discipline_id '{}'",
                            ability.discipline_id
                        )
                    });
            if let Some(expected_discipline_id) =
                discipline_id_for_combat_profile(explicit_combat_profile_id.as_str())
            {
                assert_eq!(
                    discipline_id, expected_discipline_id,
                    "player ability '{ability_id}' must belong to discipline '{}' for combat profile '{}'",
                    expected_discipline_id, explicit_combat_profile_id
                );
            } else {
                assert_eq!(
                    discipline_kind, DISCIPLINE_KIND_SPELL_SCHOOL,
                    "profile-neutral player ability '{ability_id}' must belong to a spell-school discipline"
                );
            }
        }

        let ability_kind = ability_gameplay_kind(ability);
        let disabled_target_damage_bonus = ability.gameplay.disabled_target_damage_bonus;
        let behind_target_damage_bonus = ability.gameplay.behind_target_damage_bonus;
        let dodge_recharge_time_reduction = ability.gameplay.dodge_recharge_time_reduction;
        let movement_return = ability.gameplay.movement_return.as_ref();
        let stealth_attack_stun_ms = ability.gameplay.stealth_attack_stun_ms;
        let melee_fire_on_hit = ability.gameplay.melee_fire_on_hit.as_ref();
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
        let frost_spell_debuff_protection = ability.gameplay.frost_spell_debuff_protection;
        let frozen_melee_first_hit_damage_bonus =
            ability.gameplay.frozen_melee_first_hit_damage_bonus;
        let noncritical_lightning_spell_crit_chance_bonus = ability
            .gameplay
            .noncritical_lightning_spell_crit_chance_bonus;
        let mana_regen_bonus = ability.gameplay.mana_regen_bonus;
        assert!(
            disabled_target_damage_bonus.is_finite()
                && (0.0..=1.0).contains(&disabled_target_damage_bonus),
            "ability '{ability_id}' must author disabled_target_damage_bonus between 0 and 1"
        );
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
            assert_eq!(
                discipline_id, DISCIPLINE_DIVINITY,
                "Faith must remain Divinity"
            );
            assert_eq!(ability_kind, "PASSIVE", "Faith must remain passive");
            assert!(ability
                .ability_tags
                .iter()
                .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
            assert!((mana_regen_bonus - 2.0).abs() < 0.0001);
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
                discipline_id, DISCIPLINE_RUIN,
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
            assert_eq!(discipline_id, DISCIPLINE_RUIN, "Wildfire must remain Ruin");
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
                discipline_id, DISCIPLINE_RUIN,
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
                discipline_id, DISCIPLINE_RUIN,
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
                discipline_id, DISCIPLINE_RUIN,
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
        if ability_id == RUIN_CHAIN_REACTION_ABILITY_ID {
            assert_eq!(
                discipline_id, DISCIPLINE_RUIN,
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
        if frost_spell_debuff_protection {
            assert_eq!(
                ability_kind, "PASSIVE",
                "ability '{ability_id}' may only author frost_spell_debuff_protection for PASSIVE gameplay"
            );
        }
        if ability_id == RUIN_RIME_ABILITY_ID {
            assert_eq!(
                discipline_id, DISCIPLINE_RUIN,
                "Rime must remain a Ruin passive"
            );
            assert_eq!(ability_kind, "PASSIVE", "Rime must remain passive");
            assert!(
                ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"),
                "Rime must carry the PASSIVE ability tag"
            );
            assert!(
                frost_spell_debuff_protection,
                "Rime must protect debuffs after frost-spell impacts"
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
                discipline_id, DISCIPLINE_RUIN,
                "Fracture must remain a Ruin passive"
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
                discipline_id, DISCIPLINE_RUIN,
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
                discipline_id, DISCIPLINE_SUBTLETY,
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
        if ability_id == SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID {
            assert_eq!(
                discipline_id, DISCIPLINE_SUBTLETY,
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
                discipline_id, DISCIPLINE_SUBTLETY,
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
                discipline_id, DISCIPLINE_SUBTLETY,
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
                discipline_id, DISCIPLINE_SUBTLETY,
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
                || resolved_combat_profile_id_for_ability_definition(ability).is_some(),
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
    if gameplay.gap_close.is_some() && gameplay.melee_timed_movement.is_some() {
        panic!("melee ability '{ability_id}' must not combine gap_close and melee_timed_movement");
    }
    if gameplay.melee_channel.is_some()
        && (gameplay.gap_close.is_some() || gameplay.melee_timed_movement.is_some())
    {
        panic!(
            "melee ability '{ability_id}' must not combine melee_channel with authored movement"
        );
    }
    if let Some(movement) = gameplay.melee_timed_movement.as_ref() {
        validate_melee_timed_movement(ability_id, movement);
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

fn validate_melee_channel(ability_id: &str, channel: &MeleeChannelDefinition) {
    assert!(
        channel.duration_ms > 0,
        "melee ability '{ability_id}' melee_channel.duration_ms must be positive"
    );
    assert!(
        channel.first_tick_delay_ms > 0
            && channel.first_tick_delay_ms <= channel.duration_ms,
        "melee ability '{ability_id}' melee_channel.first_tick_delay_ms must be in (0, duration_ms]"
    );
    assert!(
        channel.tick_interval_ms > 0 && channel.tick_interval_ms <= channel.duration_ms,
        "melee ability '{ability_id}' melee_channel.tick_interval_ms must be in (0, duration_ms]"
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
            MeleeImpactEffectDefinition::ApplyStatus { status } => {
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

fn validate_movement_delivery(ability_id: &str, movement: &MovementDeliveryDefinition) {
    assert_eq!(
        normalize_identifier(movement.kind.as_str()),
        "DASH_TO_TARGET",
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
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_RUIN) {
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

pub(crate) fn ruin_wildfire_ignite_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<FireSpellIgniteRuntime> {
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_RUIN) {
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

pub(crate) fn ruin_fracture_melee_damage_bonus() -> f32 {
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
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_RUIN) {
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
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_DIVINITY) {
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

pub(crate) fn ruin_acceleration_cooldown_reduction_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Duration {
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_RUIN) {
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
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_RUIN) {
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
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_RUIN) {
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

pub(crate) fn ruin_rime_protects_debuffs_for_owner(ctx: &ReducerContext, owner: Identity) -> bool {
    character_has_selected_discipline(ctx, owner, DISCIPLINE_RUIN)
        && progression_catalog().abilities.iter().any(|ability| {
            normalize_identifier(ability.ability_id.as_str()) == RUIN_RIME_ABILITY_ID
                && ability.gameplay.frost_spell_debuff_protection
        })
}

pub(crate) fn ruin_potential_crit_chance_per_stack_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> f32 {
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_RUIN) {
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

pub(crate) fn ability_belongs_to_discipline(ability_id: &str, discipline_id: &str) -> bool {
    let ability_id = normalize_identifier(ability_id);
    let discipline_id = normalize_identifier(discipline_id);
    progression_catalog().abilities.iter().any(|ability| {
        normalize_identifier(ability.ability_id.as_str()) == ability_id
            && normalize_identifier(ability.discipline_id.as_str()) == discipline_id
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
    use spacetimedb::{Identity, Timestamp};
    use std::collections::{HashMap, HashSet};
    use std::time::Duration;

    use crate::animation_set_test_utils::{
        animation_set_assets_by_combat_profile, parse_top_level_animation_set_field,
    };
    use crate::combat::{
        DamageType, StackPolicy, StatusApplication, StatusDispelType, StatusEffectKind,
        StatusPayload, StatusStackGroupDefault, DEFAULT_HIT_RADIUS,
    };
    use crate::melee::{auto_attack_reference_for_profile, profile_supports_action_reference};
    use crate::progression::melee_timed_movement_for_ability_id;
    use crate::resources::RESOURCE_KIND_MANA;
    use crate::spells::{spell_definition_by_str, SpellTargeting};

    use super::{
        ability_gameplay_kind, ability_is_compatible_with_slot,
        ability_tags_allow_discipline_selection, action_presentation_key,
        action_ref_for_action_bar_default, authored_status_presentation_ids,
        canonical_action_bar_slot_id, character_action_bar_assignment_is_generic_fixed_action,
        character_discipline_loadout_contains, combat_rule_value, combat_vfx_cue_key,
        derived_spell_action_presentation_rows, encode_tags, melee_channel_for_ability_id,
        melee_impact_effects_for_ability_id, normalize_identifier,
        normalize_optional_target_audience, primary_resource_gain_on_action_accept,
        progression_catalog, projectile_body_vfx_id_for_spell,
        resolved_combat_profile_id_for_ability_definition, resolved_melee_targeting_for_catalog,
        selectable_slot_ids, shroud_has_expired, validate_auto_attack_catalog,
        validate_combat_mode_catalog, validate_progression_catalog_authoring_contract,
        AbilityDefinition, ActionKind, CharacterActionBarAssignment, CharacterDisciplineLoadout,
        CombatVfxPresentationManifest, FixedActionId, MeleeChannelRuntime,
        MeleeImpactEffectRuntime, ABILITY_KIND_COMBAT_MODE_TOGGLE, ACTION_KIND_FIXED,
        ARCHER_DRAW_MODE_TOGGLE_ABILITY_ID, AUTO_ATTACK_MOVEMENT_ALLOW_MOVING,
        AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE, COMBAT_MODE_FULL_DRAW, COMBAT_MODE_READY,
        COMBAT_MODE_SHORT_DRAW, COMBAT_MODE_STEALTHED, COMBAT_PROFILE_ARCHER_BOW,
        COMBAT_PROFILE_DAGGERS, COMBAT_PROFILE_SWORD_AND_SHIELD, COMBAT_PROFILE_TWO_HANDED_SWORD,
        DAGGER_SHROUD_ABILITY_ID, DISCIPLINE_DIVINITY, DISCIPLINE_RUIN, GLOBAL_ACTION_BAR_PROFILE,
        RESOURCE_KIND_STAMINA, SUBTLETY_FLEET_FOOTED_ABILITY_ID,
        SUBTLETY_LINGERING_SHADE_ABILITY_ID, SUBTLETY_OPPORTUNIST_ABILITY_ID,
        SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID, SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID,
    };
    use crate::action_ids::{AuthoredActionId, RuntimeActionId};

    const GAP_CLOSE_TARGET_ARRIVAL_DISTANCE_METERS: f32 = 2.0;

    #[test]
    fn discipline_loadout_selection_accepts_active_and_passive_abilities() {
        assert!(ability_tags_allow_discipline_selection("ACTION_BAR_ACTION"));
        assert!(ability_tags_allow_discipline_selection("PASSIVE"));
        assert!(ability_tags_allow_discipline_selection(
            "CORE_ABILITY, PASSIVE"
        ));
        let encoded = encode_tags(&["CORE_ABILITY".to_string(), "ACTION_BAR_ACTION".to_string()]);
        assert_eq!(encoded, "ACTION_BAR_ACTION,CORE_ABILITY");
        assert!(ability_tags_allow_discipline_selection(encoded.as_str()));
        assert!(!ability_tags_allow_discipline_selection("CORE_ABILITY"));
    }

    #[test]
    fn knockback_speed_rule_matches_authored_tuning() {
        assert!((combat_rule_value("KNOCKBACK_SPEED_METERS_PER_SEC") - 24.0).abs() < f32::EPSILON);
    }

    fn parse_spell_ids_from_animation_set_asset(asset_contents: &str) -> HashSet<String> {
        asset_contents
            .lines()
            .filter_map(|line| line.trim_start().strip_prefix("- spellId: "))
            .map(|value| normalize_identifier(value.trim()))
            .collect()
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

    fn animation_set_asset_for_combat_profile(combat_profile_id: &str) -> &'static str {
        let normalized = normalize_identifier(combat_profile_id);
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
    fn animation_set_assets_author_explicit_identity() {
        for (expected_profile, asset_contents) in animation_set_assets_by_combat_profile() {
            assert_eq!(
                parse_top_level_animation_set_field(asset_contents, "animationSetId").as_deref(),
                Some(expected_profile.as_str()),
                "{expected_profile} must author an explicit animationSetId"
            );
            assert_eq!(
                parse_top_level_animation_set_field(asset_contents, "combatProfileId").as_deref(),
                Some(expected_profile.as_str()),
                "{expected_profile} must author an explicit combatProfileId"
            );
        }

        for profile in &progression_catalog().combat_profiles {
            let profile_id = normalize_identifier(profile.combat_profile_id.as_str());
            assert!(
                animation_set_assets_by_combat_profile().contains_key(profile_id.as_str()),
                "combat profile '{}' must have a CombatAnimationSet asset with matching combatProfileId",
                profile.combat_profile_id
            );
        }
    }

    fn spell_ids_for_combat_profile(combat_profile_id: &str) -> HashSet<String> {
        parse_spell_ids_from_animation_set_asset(animation_set_asset_for_combat_profile(
            combat_profile_id,
        ))
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

    fn authored_strike_ids_for_combat_profile(combat_profile_id: &str) -> HashSet<String> {
        parse_authored_strike_ids_from_animation_set_asset(animation_set_asset_for_combat_profile(
            combat_profile_id,
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
        combat_profile_id: &str,
    ) -> HashMap<String, usize> {
        parse_authored_strike_hit_window_counts_from_animation_set_asset(
            animation_set_asset_for_combat_profile(combat_profile_id),
        )
    }

    fn parse_runtime_slot_ids_from_animation_set_asset(asset_contents: &str) -> HashSet<String> {
        parse_current_animation_set_melee_fields(asset_contents, "slotId: ")
    }

    fn runtime_slot_ids_for_combat_profile(combat_profile_id: &str) -> HashSet<String> {
        parse_runtime_slot_ids_from_animation_set_asset(animation_set_asset_for_combat_profile(
            combat_profile_id,
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
        combat_profile_id: String,
        authored_action_id: String,
        action_bar_default_slots: Vec<String>,
        has_action_bar_action_tag: bool,
        has_core_ability_tag: bool,
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
        CoreAbilityHasActionBarDefault,
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
                Self::CoreAbilityHasActionBarDefault => "core-ability-has-action-bar-default",
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

    fn build_combat_authoring_graph() -> Vec<ResolvedCombatAuthoringAction> {
        let catalog = progression_catalog();
        let presentation_keys: HashSet<_> = catalog
            .action_presentations
            .iter()
            .map(action_presentation_key)
            .collect();

        catalog
            .abilities
            .iter()
            .map(|ability| {
                let ability_id = normalize_identifier(ability.ability_id.as_str());
                let combat_profile_id =
                    resolved_combat_profile_id_for_ability_definition(ability).unwrap_or_default();
                let authored_action_id = normalize_identifier(ability.action_id.as_str());
                let has_action_bar_action_tag = ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "ACTION_BAR_ACTION");
                let has_core_ability_tag = ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "CORE_ABILITY");
                let category = match ability_gameplay_kind(ability).as_str() {
                    "MELEE" => ResolvedAuthoringCategory::Melee,
                    "SPELL" => ResolvedAuthoringCategory::Spell,
                    "MOVEMENT" => ResolvedAuthoringCategory::Movement,
                    "AUTO_ATTACK_REPLACEMENT" => ResolvedAuthoringCategory::AutoAttackReplacement,
                    ABILITY_KIND_COMBAT_MODE_TOGGLE => ResolvedAuthoringCategory::CombatModeToggle,
                    "PASSIVE" => ResolvedAuthoringCategory::Passive,
                    other => ResolvedAuthoringCategory::Unknown(other.to_string()),
                };

                let action_bar_default_slots: Vec<String> = catalog
                    .combat_profile_action_bar_defaults
                    .iter()
                    .filter_map(|assignment| {
                        let action_ref = action_ref_for_action_bar_default(assignment);
                        if action_ref.is_ability() && action_ref.id == ability_id {
                            Some(canonical_action_bar_slot_id(assignment.slot_id.as_str()))
                        } else {
                            None
                        }
                    })
                    .collect();

                let has_ability_presentation =
                    presentation_keys.contains(format!("ABILITY:{ability_id}").as_str());

                let melee_matches_authored_strike = if category == ResolvedAuthoringCategory::Melee
                    && !combat_profile_id.is_empty()
                {
                    authored_strike_ids_for_combat_profile(combat_profile_id.as_str())
                        .contains(authored_action_id.as_str())
                } else {
                    true
                };
                let melee_matches_runtime_slot = if category == ResolvedAuthoringCategory::Melee
                    && !combat_profile_id.is_empty()
                {
                    runtime_slot_ids_for_combat_profile(combat_profile_id.as_str())
                        .contains(authored_action_id.as_str())
                } else {
                    false
                };

                let spell_has_definition = category != ResolvedAuthoringCategory::Spell
                    || spell_definition_by_str(authored_action_id.as_str()).is_some();
                let spell_requires_animation = category == ResolvedAuthoringCategory::Spell
                    && (has_action_bar_action_tag || !action_bar_default_slots.is_empty());
                let spell_has_animation = !spell_requires_animation
                    || (!combat_profile_id.is_empty()
                        && spell_ids_for_combat_profile(combat_profile_id.as_str())
                            .contains(authored_action_id.as_str()));
                let replacement = catalog.auto_attack_replacements.iter().find(|replacement| {
                    normalize_identifier(replacement.replacement_id.as_str())
                        == authored_action_id.as_str()
                });
                let replacement_strike_id = replacement
                    .map(|replacement| {
                        AuthoredActionId::new(replacement.authored_melee_strike_id.as_str())
                            .into_string()
                    })
                    .unwrap_or_default();
                let auto_attack_replacement_has_definition = category
                    != ResolvedAuthoringCategory::AutoAttackReplacement
                    || replacement.is_some();
                let auto_attack_replacement_profile_matches = category
                    != ResolvedAuthoringCategory::AutoAttackReplacement
                    || replacement
                        .map(|replacement| {
                            normalize_identifier(replacement.combat_profile_id.as_str())
                                == combat_profile_id
                        })
                        .unwrap_or(false);
                let auto_attack_replacement_matches_authored_strike = category
                    != ResolvedAuthoringCategory::AutoAttackReplacement
                    || (!combat_profile_id.is_empty()
                        && authored_strike_ids_for_combat_profile(combat_profile_id.as_str())
                            .contains(replacement_strike_id.as_str()));
                let auto_attack_replacement_matches_runtime_slot = category
                    == ResolvedAuthoringCategory::AutoAttackReplacement
                    && !combat_profile_id.is_empty()
                    && runtime_slot_ids_for_combat_profile(combat_profile_id.as_str())
                        .contains(replacement_strike_id.as_str());

                ResolvedCombatAuthoringAction {
                    ability_id,
                    actor_scope: normalize_identifier(ability.actor_scope.as_str()),
                    category,
                    combat_profile_id,
                    authored_action_id,
                    action_bar_default_slots,
                    has_action_bar_action_tag,
                    has_core_ability_tag,
                    has_ability_presentation,
                    melee_matches_authored_strike,
                    melee_matches_runtime_slot,
                    spell_has_definition,
                    spell_has_animation,
                    auto_attack_replacement_has_definition,
                    auto_attack_replacement_profile_matches,
                    auto_attack_replacement_matches_authored_strike,
                    auto_attack_replacement_matches_runtime_slot,
                }
            })
            .collect()
    }

    fn validate_combat_authoring_graph(
        graph: &[ResolvedCombatAuthoringAction],
    ) -> Vec<CombatAuthoringError> {
        let catalog = progression_catalog();
        let mut errors = Vec::new();
        let presentation_keys: HashSet<_> = catalog
            .action_presentations
            .iter()
            .map(action_presentation_key)
            .collect();
        let known_profiles: HashSet<_> = catalog
            .combat_profiles
            .iter()
            .map(|profile| normalize_identifier(profile.combat_profile_id.as_str()))
            .collect();

        for action in graph {
            let is_generic_spell = action.category == ResolvedAuthoringCategory::Spell
                && action.combat_profile_id.is_empty();
            let is_generic_passive = action.category == ResolvedAuthoringCategory::Passive
                && action.combat_profile_id.is_empty();
            let is_npc_only_action = action.actor_scope == "NPC";
            if !action.combat_profile_id.is_empty()
                && !known_profiles.contains(action.combat_profile_id.as_str())
            {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::AbilityProfileResolves,
                    format!(
                        "ability '{}' references unknown combat profile '{}'",
                        action.ability_id, action.combat_profile_id
                    ),
                ));
            }
            if action.combat_profile_id.is_empty()
                && !is_generic_spell
                && !is_generic_passive
                && !is_npc_only_action
            {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::AbilityProfileResolves,
                    format!(
                        "ability '{}' must declare combat_profile_id",
                        action.ability_id
                    ),
                ));
            }

            match &action.category {
                ResolvedAuthoringCategory::Melee => {
                    if !action.melee_matches_authored_strike {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::MeleeActionIdMatchesAuthoredStrike,
                            format!(
                                "melee ability '{}' action_id '{}' must match an authored strike id in combat profile '{}'",
                                action.ability_id, action.authored_action_id, action.combat_profile_id
                            ),
                        ));
                    }
                    if action.melee_matches_runtime_slot {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::MeleeActionIdNotRuntimeSlot,
                            format!(
                                "melee ability '{}' action_id '{}' must not point at runtime slot id plumbing for combat profile '{}'",
                                action.ability_id, action.authored_action_id, action.combat_profile_id
                            ),
                        ));
                    }
                }
                ResolvedAuthoringCategory::Spell => {
                    if !action.spell_has_definition {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::SpellActionIdResolvesToSpell,
                            format!(
                                "spell ability '{}' action_id '{}' must resolve to a spell catalog row",
                                action.ability_id, action.authored_action_id
                            ),
                        ));
                    }
                    if !is_generic_spell && !action.spell_has_animation {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::SelectableSpellHasAnimationEntry,
                            format!(
                                "spell ability '{}' uses spell '{}' but animation set for combat profile '{}' has no matching spell animation entry",
                                action.ability_id, action.authored_action_id, action.combat_profile_id
                            ),
                        ));
                    }
                }
                ResolvedAuthoringCategory::Movement => {}
                ResolvedAuthoringCategory::AutoAttackReplacement => {
                    if !action.auto_attack_replacement_has_definition {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::AutoAttackReplacementResolves,
                            format!(
                                "auto-attack replacement ability '{}' action_id '{}' must resolve to an auto_attack_replacements[] row",
                                action.ability_id, action.authored_action_id
                            ),
                        ));
                    }
                    if !action.auto_attack_replacement_profile_matches {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::AutoAttackReplacementResolves,
                            format!(
                                "auto-attack replacement ability '{}' must target combat profile '{}'",
                                action.ability_id, action.combat_profile_id
                            ),
                        ));
                    }
                    if !action.auto_attack_replacement_matches_authored_strike {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::AutoAttackReplacementStrikeMatchesAuthoredStrike,
                            format!(
                                "auto-attack replacement ability '{}' must reference an authored strike id in combat profile '{}'",
                                action.ability_id, action.combat_profile_id
                            ),
                        ));
                    }
                    if action.auto_attack_replacement_matches_runtime_slot {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::MeleeActionIdNotRuntimeSlot,
                            format!(
                                "auto-attack replacement ability '{}' must not point at runtime slot id plumbing for combat profile '{}'",
                                action.ability_id, action.combat_profile_id
                            ),
                        ));
                    }
                }
                ResolvedAuthoringCategory::CombatModeToggle => {}
                ResolvedAuthoringCategory::Passive => {}
                ResolvedAuthoringCategory::Unknown(kind) => {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::AbilityKindSupported,
                        format!(
                            "ability '{}' uses unsupported gameplay.kind '{}'",
                            action.ability_id, kind
                        ),
                    ));
                }
            }

            let is_player_facing =
                !action.action_bar_default_slots.is_empty() || action.has_action_bar_action_tag;
            if action.has_core_ability_tag
                && !matches!(action.category, ResolvedAuthoringCategory::Spell)
                && action.action_bar_default_slots.is_empty()
            {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CoreAbilityHasActionBarDefault,
                    format!(
                        "core non-spell ability '{}' must have an action-bar default",
                        action.ability_id
                    ),
                ));
            }
            if is_player_facing && !action.has_ability_presentation {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::PlayerFacingActionHasPresentation,
                    format!(
                        "player-facing ability '{}' must have an ABILITY presentation row",
                        action.ability_id
                    ),
                ));
            }
        }

        let status_presentation_ids = authored_status_presentation_ids(catalog);
        for presentation in &catalog.action_presentations {
            let kind = normalize_identifier(presentation.presentation_kind.as_str());
            let id = normalize_identifier(presentation.presentation_id.as_str());

            match kind.as_str() {
                "ABILITY" => {
                    if !graph.iter().any(|action| action.ability_id == id) {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::PresentationTargetResolves,
                            format!(
                                "ABILITY presentation '{}' must reference a known ability",
                                presentation.presentation_id
                            ),
                        ));
                    }
                }
                "SPELL" => {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::SpellPresentationNotAuthored,
                        format!(
                            "SPELL presentation '{}' is derived from SPELL ability gameplay; author the ABILITY presentation instead",
                            presentation.presentation_id
                        ),
                    ));
                }
                "FIXED" => {
                    if let FixedActionId::Unsupported(value) = FixedActionId::from_wire(id.as_str())
                    {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::PresentationTargetResolves,
                            format!("FIXED presentation '{value}' must reference a supported fixed action"),
                        ));
                    }
                }
                "COMBAT_DISCIPLINE_SWITCH" => {
                    if !catalog.combat_disciplines.iter().any(|discipline| {
                        normalize_identifier(discipline.discipline_id.as_str()) == id
                    }) {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::PresentationTargetResolves,
                            format!(
                                "COMBAT_DISCIPLINE_SWITCH presentation '{}' must reference a known combat discipline",
                                presentation.presentation_id
                            ),
                        ));
                    }
                }
                "STATUS" => {
                    if !status_presentation_ids.contains(id.as_str()) {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::PresentationTargetResolves,
                            format!(
                                "STATUS presentation '{}' must reference a known status kind or authored status stack group",
                                presentation.presentation_id
                            ),
                        ));
                    }
                }
                _ => {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::PresentationTargetResolves,
                        format!(
                            "presentation '{}' uses unsupported kind '{}'",
                            presentation.presentation_id, presentation.presentation_kind
                        ),
                    ));
                }
            }
        }

        let known_slots: HashSet<_> = catalog
            .slots
            .iter()
            .map(|slot| canonical_action_bar_slot_id(slot.slot_id.as_str()))
            .collect();
        let mut action_bar_default_slots = HashSet::new();

        for assignment in &catalog.combat_profile_action_bar_defaults {
            let normalized_combat_profile_id =
                normalize_identifier(assignment.combat_profile_id.as_str());
            let normalized_slot_id = canonical_action_bar_slot_id(assignment.slot_id.as_str());
            if !action_bar_default_slots.insert((
                normalized_combat_profile_id.clone(),
                normalized_slot_id.clone(),
            )) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                    format!(
                        "duplicate action-bar default for combat profile '{}' slot '{}'",
                        assignment.combat_profile_id, assignment.slot_id
                    ),
                ));
            }
            let is_global_scope = normalized_combat_profile_id == GLOBAL_ACTION_BAR_PROFILE;
            if !is_global_scope && !known_profiles.contains(normalized_combat_profile_id.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                    format!(
                        "action-bar default references unknown combat profile '{}'",
                        assignment.combat_profile_id
                    ),
                ));
            }
            if !known_slots.contains(normalized_slot_id.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                    format!(
                        "action-bar default references unknown slot '{}'",
                        assignment.slot_id
                    ),
                ));
            }

            let action_ref = action_ref_for_action_bar_default(assignment);
            match &action_ref.kind {
                ActionKind::Ability => {
                    let Some(action) = graph
                        .iter()
                        .find(|action| action.ability_id == action_ref.id)
                    else {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                            format!(
                                "action-bar default references unknown ability '{}'",
                                action_ref.id
                            ),
                        ));
                        continue;
                    };

                    if action.combat_profile_id != normalized_combat_profile_id {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                            format!(
                                "action-bar default ability '{}' belongs to combat profile '{}' but default is for combat profile '{}'",
                                action_ref.id, action.combat_profile_id, assignment.combat_profile_id
                            ),
                        ));
                    }
                    if !ability_is_compatible_with_slot(
                        action_ref.id.as_str(),
                        normalized_slot_id.as_str(),
                    ) {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                            format!(
                                "action-bar default ability '{}' is incompatible with slot '{}'",
                                action_ref.id, assignment.slot_id
                            ),
                        ));
                    }
                }
                ActionKind::Fixed => {
                    let fixed_action_id = FixedActionId::from_wire(action_ref.id.as_str());
                    match &fixed_action_id {
                        FixedActionId::Dodge | FixedActionId::Parry => {
                            errors.push(CombatAuthoringError::new(
                                CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                                format!(
                                    "action-bar default references generic fixed keybind '{}'",
                                    fixed_action_id.as_wire()
                                ),
                            ));
                        }
                        FixedActionId::Unsupported(value) => {
                            errors.push(CombatAuthoringError::new(
                                CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                                format!("action-bar default references unsupported fixed action '{value}'"),
                            ));
                        }
                    }

                    if !matches!(fixed_action_id, FixedActionId::Unsupported(_)) {
                        let presentation_key =
                            format!("{}:{}", ACTION_KIND_FIXED, fixed_action_id.as_wire());
                        if !presentation_keys.contains(presentation_key.as_str()) {
                            errors.push(CombatAuthoringError::new(
                                CombatAuthoringRule::PlayerFacingActionHasPresentation,
                                format!(
                                    "player-facing fixed action '{}' must have a FIXED presentation row",
                                    fixed_action_id.as_wire()
                                ),
                            ));
                        }
                    }
                }
                ActionKind::CombatDisciplineSwitch => {
                    if !is_global_scope {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                            "combat discipline switch defaults must use GLOBAL scope".to_string(),
                        ));
                    }
                    if !catalog.combat_disciplines.iter().any(|discipline| {
                        normalize_identifier(discipline.discipline_id.as_str()) == action_ref.id
                    }) {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                            format!(
                                "combat discipline switch references unknown discipline '{}'",
                                action_ref.id
                            ),
                        ));
                    }
                    if let Some(slot) = catalog.slots.iter().find(|slot| {
                        canonical_action_bar_slot_id(slot.slot_id.as_str()) == normalized_slot_id
                    }) {
                        if !slot
                            .accepts_tags
                            .iter()
                            .map(|tag| normalize_identifier(tag.as_str()))
                            .any(|tag| tag == "DISCIPLINE_SWITCH")
                        {
                            errors.push(CombatAuthoringError::new(
                                CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                                format!(
                                    "combat discipline switch '{}' is incompatible with slot '{}'",
                                    action_ref.id, assignment.slot_id
                                ),
                            ));
                        }
                    }
                }
                ActionKind::Unsupported(kind) => {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatProfileActionBarDefaultResolves,
                        format!("action-bar default uses unsupported action kind '{kind}'"),
                    ));
                }
            }
        }

        let known_abilities: HashSet<_> = graph
            .iter()
            .map(|action| action.ability_id.as_str())
            .collect();
        let known_spells: HashSet<_> = catalog
            .abilities
            .iter()
            .filter(|ability| ability_gameplay_kind(ability) == "SPELL")
            .map(|ability| normalize_identifier(ability.action_id.as_str()))
            .collect();
        let projectile_spell_abilities: HashMap<_, _> = catalog
            .abilities
            .iter()
            .filter(|ability| ability_gameplay_kind(ability) == "SPELL")
            .filter(|ability| ability_uses_projectile_body(ability))
            .map(|ability| {
                (
                    normalize_identifier(ability.ability_id.as_str()),
                    normalize_identifier(ability.action_id.as_str()),
                )
            })
            .collect();
        let projectile_spell_kinds: HashSet<_> =
            projectile_spell_abilities.values().cloned().collect();
        let projectile_sequence_count_by_ability: HashMap<_, _> = catalog
            .abilities
            .iter()
            .filter(|ability| ability_gameplay_kind(ability) == "SPELL")
            .filter(|ability| ability_uses_projectile_body(ability))
            .map(|ability| {
                (
                    normalize_identifier(ability.ability_id.as_str()),
                    projectile_delivery_projectile_count(ability),
                )
            })
            .collect();
        let mut projectile_sequence_count_by_spell_kind: HashMap<String, u32> = HashMap::new();
        for ability in catalog
            .abilities
            .iter()
            .filter(|ability| ability_gameplay_kind(ability) == "SPELL")
            .filter(|ability| ability_uses_projectile_body(ability))
        {
            let spell_kind = normalize_identifier(ability.action_id.as_str());
            let count = projectile_delivery_projectile_count(ability);
            projectile_sequence_count_by_spell_kind
                .entry(spell_kind)
                .and_modify(|existing| *existing = (*existing).max(count))
                .or_insert(count);
        }
        let mut cast_time_spell_abilities: HashMap<String, u64> = HashMap::new();
        let mut cast_time_spell_kinds: HashMap<String, u64> = HashMap::new();
        for ability in catalog
            .abilities
            .iter()
            .filter(|ability| ability_gameplay_kind(ability) == "SPELL")
        {
            let cast_time_ms = ability.gameplay.cast_time_ms.unwrap_or(0);
            if cast_time_ms == 0 {
                continue;
            }

            cast_time_spell_abilities.insert(
                normalize_identifier(ability.ability_id.as_str()),
                cast_time_ms,
            );
            let spell_kind = normalize_identifier(ability.action_id.as_str());
            cast_time_spell_kinds
                .entry(spell_kind)
                .and_modify(|existing| *existing = (*existing).max(cast_time_ms))
                .or_insert(cast_time_ms);
        }
        let known_melee_strikes: HashSet<_> = catalog
            .combat_profiles
            .iter()
            .map(|profile| normalize_identifier(profile.combat_profile_id.as_str()))
            .flat_map(|profile| {
                authored_strike_ids_for_combat_profile(profile.as_str())
                    .into_iter()
                    .collect::<Vec<_>>()
            })
            .collect();
        let known_melee_strike_hit_windows: HashMap<_, _> = catalog
            .combat_profiles
            .iter()
            .map(|profile| normalize_identifier(profile.combat_profile_id.as_str()))
            .flat_map(|profile| {
                authored_strike_hit_window_counts_for_combat_profile(profile.as_str())
                    .into_iter()
                    .collect::<Vec<_>>()
            })
            .collect();
        let known_melee_ability_hit_windows: HashMap<_, _> = catalog
            .abilities
            .iter()
            .filter(|ability| ability_gameplay_kind(ability) == "MELEE")
            .filter_map(|ability| {
                let ability_id = normalize_identifier(ability.ability_id.as_str());
                let combat_profile_id = resolved_combat_profile_id_for_ability_definition(ability)?;
                let authored_action_id = normalize_identifier(ability.action_id.as_str());
                let counts = authored_strike_hit_window_counts_for_combat_profile(
                    combat_profile_id.as_str(),
                );
                counts
                    .get(authored_action_id.as_str())
                    .copied()
                    .map(|count| (ability_id, count))
            })
            .collect();

        let vfx_manifest = CombatVfxPresentationManifest::build(catalog);

        for ability in &catalog.abilities {
            let ability_id = normalize_identifier(ability.ability_id.as_str());
            let ability_kind = ability_gameplay_kind(ability);
            let Some(area) = ability.gameplay.melee_impact_area.as_ref() else {
                continue;
            };
            if ability_kind != "MELEE" {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::MeleeImpactAreaValid,
                    format!(
                        "non-melee ability '{}' must not author melee_impact_area",
                        ability.ability_id
                    ),
                ));
                continue;
            }
            if !area.radius.is_finite() || area.radius <= 0.0 {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::MeleeImpactAreaValid,
                    format!(
                        "melee ability '{}' melee_impact_area radius must be positive",
                        ability.ability_id
                    ),
                ));
            }
            if !area.damage_multiplier.is_finite() || area.damage_multiplier <= 0.0 {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::MeleeImpactAreaValid,
                    format!(
                        "melee ability '{}' melee_impact_area damage_multiplier must be positive",
                        ability.ability_id
                    ),
                ));
            }
            if let Some(hit_index) = area.hit_index {
                match known_melee_ability_hit_windows.get(ability_id.as_str()) {
                    Some(count) if (hit_index as usize) < *count => {}
                    Some(count) => errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::MeleeImpactAreaValid,
                        format!(
                            "melee ability '{}' melee_impact_area hit_index {} is out of range with {} hit window(s)",
                            ability.ability_id, hit_index, count
                        ),
                    )),
                    None => {}
                }
            }
        }

        let supported_vfx_owner_kinds = ["ABILITY", "SPELL", "MELEE_STRIKE"];
        let supported_vfx_triggers = [
            "MELEE_CAST",
            "MELEE_ACTIVE_WINDOW",
            "MELEE_IMPACT",
            "MELEE_BLOCK",
            "MELEE_PARRY",
            "AREA_IMPACT",
            "SPELL_CAST",
            "SPELL_RELEASE",
            "SPELL_IMPACT",
            "SPELL_BLOCK",
            "SPELL_PARRY",
            "SPELL_FIZZLE",
            "STATUS_ACTIVE",
            "STATUS_END",
            "EMANATION_ACTIVE",
            "EMANATION_MAX_STACKS",
            "SPECIAL_MOVEMENT_START",
            "SPECIAL_MOVEMENT_ARRIVAL",
        ];
        let supported_vfx_anchors = [
            "CASTER",
            "CASTER_OVERHEAD",
            "TARGET",
            "TARGET_BACK",
            "ORIGIN",
            "AREA_ORIGIN",
            "IMPACT_POINT",
            "GROUND_UNDER_CASTER",
            "GROUND_UNDER_TARGET",
            "WEAPON_MAIN_HAND",
            "WEAPON_OFF_HAND",
            "WEAPON_BLADE_START",
            "WEAPON_BLADE_END",
            "LEFT_HAND",
            "RIGHT_HAND",
        ];
        let supported_attach_modes = [
            "",
            "SPAWN_WORLD",
            "FOLLOW_ANCHOR",
            "FOLLOW_GROUND_POSITION",
            "WORLD_ALIGNED_TO_FACING",
        ];
        let supported_vfx_roles = [
            "",
            "ONE_SHOT",
            "ATTACHED",
            "PROJECTILE_BODY",
            "PROJECTILE_TRAIL",
            "TRAVEL_BODY",
        ];
        let supported_lifecycles = [
            "",
            "DURATION",
            "PARTICLE_SYSTEM",
            "UNTIL_RELEASE_EVENT",
            "UNTIL_TERMINAL_EVENT",
            // Persists until the owning cast/channel's ActiveCast row is deleted (channel
            // end / cancel). Used for hand-attached channel cues like Magic Missile's glow.
            "UNTIL_CAST_END",
            // State-backed caster field; ends when its ActiveRadialEffect row is deleted.
            "UNTIL_RADIAL_EFFECT_END",
            // State-backed target attachment; ends when its StatusEffect row is deleted.
            "UNTIL_STATUS_END",
        ];
        for cue in &catalog.combat_vfx_cues {
            let owner_kind = normalize_identifier(cue.owner_kind.as_str());
            let owner_id = normalize_identifier(cue.owner_id.as_str());
            let trigger = normalize_identifier(cue.trigger.as_str());
            let anchor = normalize_identifier(cue.anchor.as_str());
            let attach_mode = normalize_identifier(cue.attach_mode.as_str());
            let vfx_role = normalize_identifier(cue.vfx_role.as_str());
            let lifecycle = normalize_identifier(cue.lifecycle.as_str());
            let effective_vfx_role = if vfx_role.is_empty() {
                "ONE_SHOT"
            } else {
                vfx_role.as_str()
            };
            let effective_lifecycle = if lifecycle.is_empty() {
                "DURATION"
            } else {
                lifecycle.as_str()
            };
            let vfx_id = normalize_identifier(cue.vfx_id.as_str());
            if cue.scale.is_some() {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' authors scale in progression_catalog.shared.json; prefab scale now belongs in CombatVFXRegistry",
                        cue.vfx_id
                    ),
                ));
            }

            if !supported_vfx_owner_kinds.contains(&owner_kind.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' uses unsupported owner_kind '{}'",
                        cue.vfx_id, cue.owner_kind
                    ),
                ));
            }

            let owner_resolves = match owner_kind.as_str() {
                "ABILITY" => known_abilities.contains(owner_id.as_str()),
                "SPELL" => known_spells.contains(owner_id.as_str()),
                "MELEE_STRIKE" => known_melee_strikes.contains(owner_id.as_str()),
                _ => false,
            };
            if !owner_resolves {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' owner '{}:{}' must resolve",
                        cue.vfx_id, cue.owner_kind, cue.owner_id
                    ),
                ));
            }

            if !supported_vfx_triggers.contains(&trigger.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' uses unsupported trigger '{}'",
                        cue.vfx_id, cue.trigger
                    ),
                ));
            }
            if !supported_vfx_anchors.contains(&anchor.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' uses unsupported anchor '{}'",
                        cue.vfx_id, cue.anchor
                    ),
                ));
            }
            if !supported_attach_modes.contains(&attach_mode.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' uses unsupported attach_mode '{}'",
                        cue.vfx_id, cue.attach_mode
                    ),
                ));
            }
            if attach_mode == "FOLLOW_GROUND_POSITION"
                && (trigger != "EMANATION_ACTIVE"
                    || anchor != "CASTER"
                    || effective_vfx_role != "ATTACHED"
                    || effective_lifecycle != "UNTIL_RADIAL_EFFECT_END")
            {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' FOLLOW_GROUND_POSITION requires EMANATION_ACTIVE + CASTER + ATTACHED + UNTIL_RADIAL_EFFECT_END",
                        cue.vfx_id
                    ),
                ));
            }
            if !supported_vfx_roles.contains(&vfx_role.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' uses unsupported vfx_role '{}'",
                        cue.vfx_id, cue.vfx_role
                    ),
                ));
            }
            if !supported_lifecycles.contains(&lifecycle.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' uses unsupported lifecycle '{}'",
                        cue.vfx_id, cue.lifecycle
                    ),
                ));
            }
            let cast_time_ms = match owner_kind.as_str() {
                "ABILITY" => cast_time_spell_abilities.get(owner_id.as_str()).copied(),
                "SPELL" => cast_time_spell_kinds.get(owner_id.as_str()).copied(),
                _ => None,
            };
            // Class-A single-cue field-relation rules (Appendix A). Shared with the VFX generator
            // through the one checker in `vfx_generation`, so the generator and this contract can
            // never silently diverge (design doc decisions 5 / 10). The `cast_time_*` maps only
            // hold spells with `cast_time_ms > 0`, so `is_some()` == charged. Rules that need
            // catalog context (owner resolution, projectile ownership/count, `start_delay_ms`,
            // `hit_index`) stay below — the checker cannot see that data.
            for violation in
                crate::vfx_generation::check_cue_field_rules(&crate::vfx_generation::CueFields {
                    trigger: trigger.as_str(),
                    anchor: anchor.as_str(),
                    attach_mode: attach_mode.as_str(),
                    role: effective_vfx_role,
                    lifecycle: effective_lifecycle,
                    duration_is_zero: cue.duration_ms == 0,
                    charged_cast: cast_time_ms.is_some(),
                })
            {
                use crate::vfx_generation::CueFieldViolation as V;
                let message = match violation {
                    V::UntilReleaseEventOffCast => format!(
                        "combat VFX cue '{}' uses UNTIL_RELEASE_EVENT outside SPELL_CAST",
                        cue.vfx_id
                    ),
                    V::ParticleSystemBadRole => format!(
                        "combat VFX cue '{}' uses PARTICLE_SYSTEM lifecycle with role '{}'; PARTICLE_SYSTEM is only valid for ONE_SHOT prefab cues",
                        cue.vfx_id, effective_vfx_role
                    ),
                    V::ParticleSystemNonZeroDuration => format!(
                        "combat VFX cue '{}' uses PARTICLE_SYSTEM lifecycle and must set duration_ms to 0",
                        cue.vfx_id
                    ),
                    V::CastTimeHandGlowNotUntilRelease => format!(
                        "combat VFX cue '{}' is a hand-attached SPELL_CAST cue for cast-time spell owner '{}:{}' (cast_time_ms {}) but uses lifecycle '{}'; use UNTIL_RELEASE_EVENT with duration_ms 0",
                        cue.vfx_id,
                        cue.owner_kind,
                        cue.owner_id,
                        cast_time_ms.unwrap_or(0),
                        effective_lifecycle
                    ),
                    V::ProjectileBodyOffRelease => format!(
                        "combat VFX cue '{}' uses PROJECTILE_BODY outside SPELL_RELEASE",
                        cue.vfx_id
                    ),
                    V::ProjectileBodyFollowAnchor => format!(
                        "combat VFX cue '{}' PROJECTILE_BODY must not use FOLLOW_ANCHOR",
                        cue.vfx_id
                    ),
                    V::ProjectileTrailOffRelease => format!(
                        "combat VFX cue '{}' uses PROJECTILE_TRAIL outside SPELL_RELEASE",
                        cue.vfx_id
                    ),
                    V::ProjectileTrailFollowAnchor => format!(
                        "combat VFX cue '{}' PROJECTILE_TRAIL must not use FOLLOW_ANCHOR",
                        cue.vfx_id
                    ),
                    V::ProjectileTrailBadLifecycle => format!(
                        "combat VFX cue '{}' PROJECTILE_TRAIL must use UNTIL_TERMINAL_EVENT",
                        cue.vfx_id
                    ),
                    V::ProjectileTrailNonZeroDuration => format!(
                        "combat VFX cue '{}' PROJECTILE_TRAIL must set duration_ms to 0",
                        cue.vfx_id
                    ),
                    V::TravelBodyOffRelease => format!(
                        "combat VFX cue '{}' uses TRAVEL_BODY outside SPELL_RELEASE",
                        cue.vfx_id
                    ),
                    V::TravelBodyFollowAnchor => format!(
                        "combat VFX cue '{}' TRAVEL_BODY must not use FOLLOW_ANCHOR",
                        cue.vfx_id
                    ),
                    V::TravelBodyBadLifecycle => format!(
                        "combat VFX cue '{}' TRAVEL_BODY must use UNTIL_TERMINAL_EVENT",
                        cue.vfx_id
                    ),
                    V::TravelBodyNonZeroDuration => format!(
                        "combat VFX cue '{}' TRAVEL_BODY must set duration_ms to 0",
                        cue.vfx_id
                    ),
                    V::OneShotDurationZero => format!(
                        "combat VFX cue '{}' ONE_SHOT DURATION must define positive duration_ms",
                        cue.vfx_id
                    ),
                    V::TargetAnchorPreImpact => format!(
                        "combat VFX cue '{}' uses target anchor '{}' on {}; target anchors are only valid once an impact/block/parry/fizzle target is known",
                        cue.vfx_id, anchor, trigger
                    ),
                    V::WorldImpactTargetAnchor => format!(
                        "combat VFX cue '{}' is a world-spawned {} cue using target anchor '{}'; use IMPACT_POINT for detached hit VFX or FOLLOW_ANCHOR for an effect that intentionally tracks the target",
                        cue.vfx_id, trigger, anchor
                    ),
                };
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    message,
                ));
            }
            // Context-dependent projectile visual rules (field legality is in the shared checker above).
            if matches!(vfx_role.as_str(), "PROJECTILE_BODY" | "PROJECTILE_TRAIL") {
                if cue.start_delay_ms > 0 {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' {} must not author start_delay_ms; projectile visuals bind to active projectile runtime rows",
                            cue.vfx_id, vfx_role
                        ),
                    ));
                }
                let projectile_sequence_index = cue.projectile_sequence_index.unwrap_or(0);
                match owner_kind.as_str() {
                    "ABILITY" if !projectile_spell_abilities.contains_key(owner_id.as_str()) => {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatVfxCueResolves,
                            format!(
                                "combat VFX cue '{}' {} owner '{}:{}' must resolve to a projectile-producing spell ability",
                                cue.vfx_id, vfx_role, cue.owner_kind, cue.owner_id
                            ),
                        ));
                    }
                    "ABILITY" => {
                        let count = projectile_sequence_count_by_ability
                            .get(owner_id.as_str())
                            .copied()
                            .unwrap_or(1);
                        if projectile_sequence_index >= count {
                            errors.push(CombatAuthoringError::new(
                                CombatAuthoringRule::CombatVfxCueResolves,
                                format!(
                                    "combat VFX cue '{}' projectile_sequence_index {} is out of range for projectile spell ability '{}:{}' with {} projectile row(s)",
                                    cue.vfx_id, projectile_sequence_index, cue.owner_kind, cue.owner_id, count
                                ),
                            ));
                        }
                    }
                    "SPELL" if !projectile_spell_kinds.contains(owner_id.as_str()) => {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatVfxCueResolves,
                            format!(
                                "combat VFX cue '{}' {} owner '{}:{}' must resolve to a projectile-producing spell kind",
                                cue.vfx_id, vfx_role, cue.owner_kind, cue.owner_id
                            ),
                        ));
                    }
                    "SPELL" => {
                        let count = projectile_sequence_count_by_spell_kind
                            .get(owner_id.as_str())
                            .copied()
                            .unwrap_or(1);
                        if projectile_sequence_index >= count {
                            errors.push(CombatAuthoringError::new(
                                CombatAuthoringRule::CombatVfxCueResolves,
                                format!(
                                    "combat VFX cue '{}' projectile_sequence_index {} is out of range for projectile spell kind '{}:{}' with {} projectile row(s)",
                                    cue.vfx_id, projectile_sequence_index, cue.owner_kind, cue.owner_id, count
                                ),
                            ));
                        }
                    }
                    _ => {}
                }
            }
            if vfx_id.is_empty() {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    "combat VFX cue vfx_id must not be empty".to_string(),
                ));
            }
            if let Some(hit_index) = cue.hit_index {
                let hit_index = hit_index as usize;
                let melee_hit_trigger = matches!(
                    trigger.as_str(),
                    "MELEE_ACTIVE_WINDOW" | "MELEE_IMPACT" | "MELEE_BLOCK" | "MELEE_PARRY"
                );
                if !melee_hit_trigger {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' hit_index is only valid for melee hit triggers",
                            cue.vfx_id
                        ),
                    ));
                } else {
                    let hit_window_count = match owner_kind.as_str() {
                        "MELEE_STRIKE" => known_melee_strike_hit_windows.get(owner_id.as_str()),
                        "ABILITY" => known_melee_ability_hit_windows.get(owner_id.as_str()),
                        _ => None,
                    };
                    match hit_window_count {
                        Some(count) if hit_index < *count => {}
                        Some(count) => errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatVfxCueResolves,
                            format!(
                                "combat VFX cue '{}' hit_index {} is out of range for melee owner '{}:{}' with {} hit window(s)",
                                cue.vfx_id, hit_index, cue.owner_kind, cue.owner_id, count
                            ),
                        )),
                        None => errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatVfxCueResolves,
                            format!(
                                "combat VFX cue '{}' hit_index requires owner '{}:{}' to resolve to a melee strike",
                                cue.vfx_id, cue.owner_kind, cue.owner_id
                            ),
                        )),
                    }
                }
            }
        }

        for (ability_id, spell_kind) in &projectile_spell_abilities {
            let selected_count = vfx_manifest.selected_projectile_body_cue_count(
                ability_id.as_str(),
                spell_kind.as_str(),
                0,
            );
            if selected_count != 1 {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "spell projectile ability '{}' kind '{}' must resolve exactly one selected PROJECTILE_BODY cue for projectile_sequence_index 0; found {}",
                        ability_id,
                        spell_kind,
                        selected_count
                    ),
                ));
            }

            let sequence_count = projectile_sequence_count_by_ability
                .get(ability_id.as_str())
                .copied()
                .unwrap_or(1);
            for sequence_index in 0..sequence_count {
                let selected_trail_count = vfx_manifest.selected_projectile_trail_cue_count(
                    ability_id.as_str(),
                    spell_kind.as_str(),
                    sequence_index,
                );
                if selected_trail_count > 1 {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "spell projectile ability '{}' kind '{}' must resolve at most one selected PROJECTILE_TRAIL cue for projectile_sequence_index {}; found {}",
                            ability_id, spell_kind, sequence_index, selected_trail_count
                        ),
                    ));
                }
            }
        }

        errors
    }

    #[test]
    fn combat_authoring_graph_validates_first_pass_contract() {
        let graph = build_combat_authoring_graph();
        assert!(
            !graph.is_empty(),
            "combat authoring graph should resolve current abilities"
        );
        let errors = validate_combat_authoring_graph(graph.as_slice());
        assert!(
            errors.is_empty(),
            "combat authoring graph validation failed:\n{}",
            errors
                .iter()
                .map(CombatAuthoringError::render)
                .collect::<Vec<_>>()
                .join("\n")
        );
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
            "combat_profile_id": "TWO_HANDED_SWORD",
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
    fn authored_status_presentations_for_stack_groups_are_accepted() {
        let catalog = serde_json::from_str::<super::ProgressionCatalogFile>(
            r#"{
                "auto_attacks": [],
                "combat_profiles": [
                    {
                        "combat_profile_id": "TWO_HANDED_SWORD",
                        "display_name": "Greatsword",
                        "sort_order": 1
                    }
                ],
                "abilities": [
                    {
                        "ability_id": "TEST_STATUS_ABILITY",
                        "actor_scope": "PLAYER",
                        "combat_profile_id": "TWO_HANDED_SWORD",
                        "action_id": "TEST_STATUS",
                        "display_name": "Test Status",
                        "resource_kind": "",
                        "sort_order": 1,
                        "gameplay": {
                            "kind": "SPELL",
                            "delivery": {
                                "kind": "APPLY_STATUS",
                                "status_stack_group": "TEST_STACK_GROUP"
                            }
                        }
                    }
                ],
                "action_presentations": [
                    {
                        "presentation_kind": "STATUS",
                        "presentation_id": "TEST_STACK_GROUP",
                        "display_name": "Test Stack Group",
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
    fn glacial_advance_authors_ruin_stun_immunity_and_frost_impact() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_GLACIAL_ADVANCE")
            .expect("Glacial Advance ability should be authored");
        assert_eq!(ability.discipline_id, DISCIPLINE_RUIN);
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
        assert_eq!(ability.discipline_id, DISCIPLINE_DIVINITY);
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
        assert_eq!(ability.discipline_id, DISCIPLINE_DIVINITY);
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
            assert_eq!(ability.discipline_id, DISCIPLINE_DIVINITY);
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
    fn ruin_flaming_weapon_authors_melee_fire_and_stacking_burning_passive() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_FLAMING_WEAPON")
            .expect("Flaming Weapon ability should be authored");
        assert_eq!(
            normalize_identifier(ability.discipline_id.as_str()),
            DISCIPLINE_RUIN
        );
        assert!(ability.combat_profile_id.is_empty());
        assert_eq!(ability.action_id, "FLAMING_WEAPON");
        assert_eq!(ability_gameplay_kind(ability), "PASSIVE");
        assert!(ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));

        let tuning = ability
            .gameplay
            .melee_fire_on_hit
            .as_ref()
            .expect("Flaming Weapon should define melee fire-on-hit tuning");
        assert_eq!(tuning.bonus_damage, 5);
        assert_eq!(tuning.burn_duration_ms, 5_000);
        assert_eq!(tuning.burn_tick_interval_ms, 1_000);
        assert_eq!(tuning.burn_tick_damage, 1);
        assert_eq!(tuning.burn_max_stacks, 5);
        assert_eq!(tuning.burn_status_stack_group, "FLAMING_WEAPON_BURN");
        assert_eq!(tuning.burn_dispel_types, vec![StatusDispelType::Magic]);

        let presentation = catalog
            .action_presentations
            .iter()
            .find(|presentation| presentation.presentation_id == "RUIN_FLAMING_WEAPON")
            .expect("Flaming Weapon should have ability presentation text");
        assert_eq!(presentation.display_name, "Flaming Weapon");
        let burning = catalog
            .action_presentations
            .iter()
            .find(|presentation| presentation.presentation_id == "FLAMING_WEAPON_BURN")
            .expect("Flaming Weapon Burning should have status presentation text");
        assert_eq!(burning.display_name, "Burning");
        assert!(authored_status_presentation_ids(catalog).contains("FLAMING_WEAPON_BURN"));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| assignment.ability_id == "RUIN_FLAMING_WEAPON"));
    }

    #[test]
    fn soulstealer_authors_blight_empowerment_and_target_impact_presentation() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_SOULSTEALER")
            .expect("Soulstealer ability should be authored");
        assert_eq!(ability.discipline_id, "BLIGHT");
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
    fn ruin_wildfire_authors_nearby_fire_spell_ignite_passive() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_WILDFIRE")
            .expect("Wildfire ability should be authored");
        assert_eq!(ability.discipline_id, DISCIPLINE_RUIN);
        assert_eq!(ability.action_id, "WILDFIRE");
        assert_eq!(ability_gameplay_kind(ability), "PASSIVE");
        assert!(ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));

        let tuning = ability
            .gameplay
            .fire_spell_ignite
            .as_ref()
            .expect("Wildfire should define fire-spell ignite tuning");
        assert!((tuning.radius_meters - 5.0).abs() < 0.0001);
        assert_eq!(tuning.burn_duration_ms, 5_000);
        assert_eq!(tuning.burn_tick_interval_ms, 1_000);
        assert_eq!(tuning.burn_tick_damage, 1);
        assert_eq!(tuning.burn_max_stacks, 5);
        assert_eq!(tuning.burn_status_stack_group, "WILDFIRE_BURN");
        assert_eq!(tuning.burn_dispel_types, vec![StatusDispelType::Magic]);

        assert!(authored_status_presentation_ids(catalog).contains("WILDFIRE_BURN"));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| assignment.ability_id == "RUIN_WILDFIRE"));
        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "RUIN_WILDFIRE"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Wildfire should author an ignite VFX cue");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_FIRE_AREA_BURST_01_ARENA"
        );
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "AREA_ORIGIN");
    }

    #[test]
    fn ruin_furnace_authors_fire_damage_to_mana_passive() {
        let catalog = progression_catalog();
        let furnace = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_FURNACE")
            .expect("Furnace ability should be authored");
        assert_eq!(
            normalize_identifier(furnace.discipline_id.as_str()),
            DISCIPLINE_RUIN
        );
        assert!(furnace.combat_profile_id.is_empty());
        assert_eq!(furnace.action_id, "FURNACE");
        assert_eq!(ability_gameplay_kind(furnace), "PASSIVE");
        assert!(furnace
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!((furnace.gameplay.fire_damage_taken_mana_restore_ratio - 1.0).abs() < 0.0001);

        let presentation = catalog
            .action_presentations
            .iter()
            .find(|presentation| presentation.presentation_id == "RUIN_FURNACE")
            .expect("Furnace should have ability presentation text");
        assert_eq!(presentation.display_name, "Furnace");
        assert_eq!(
            presentation.description,
            "Passive: fire damage taken restores an equal amount of mana."
        );
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| assignment.ability_id == "RUIN_FURNACE"));
    }

    #[test]
    fn ruin_lightning_passives_author_acceleration_quickening_chain_reaction_and_potential() {
        let catalog = progression_catalog();
        let acceleration = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_ACCELERATION")
            .expect("Acceleration ability should be authored");
        assert_eq!(acceleration.discipline_id, DISCIPLINE_RUIN);
        assert_eq!(ability_gameplay_kind(acceleration), "PASSIVE");
        assert_eq!(
            acceleration.gameplay.critical_strike_cooldown_reduction_ms,
            1_000
        );

        let quickening = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_QUICKENING")
            .expect("Quickening ability should be authored");
        assert_eq!(quickening.discipline_id, DISCIPLINE_RUIN);
        assert_eq!(ability_gameplay_kind(quickening), "PASSIVE");
        assert!((quickening.gameplay.movement_spell_cast_time_reduction - 0.5).abs() < 0.0001);
        assert_eq!(
            quickening
                .gameplay
                .movement_spell_cast_time_buff_duration_ms,
            5_000
        );

        let chain_reaction = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_CHAIN_REACTION")
            .expect("Chain Reaction ability should be authored");
        assert_eq!(chain_reaction.discipline_id, DISCIPLINE_RUIN);
        assert_eq!(ability_gameplay_kind(chain_reaction), "PASSIVE");
        assert_eq!(
            normalize_identifier(
                chain_reaction
                    .gameplay
                    .critical_spell_proc_action_id
                    .as_str()
            ),
            "BOLT"
        );

        let potential = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_POTENTIAL")
            .expect("Potential ability should be authored");
        assert_eq!(potential.discipline_id, DISCIPLINE_RUIN);
        assert_eq!(potential.action_id, "POTENTIAL");
        assert_eq!(ability_gameplay_kind(potential), "PASSIVE");
        assert!(
            (potential
                .gameplay
                .noncritical_lightning_spell_crit_chance_bonus
                - 0.05)
                .abs()
                < 0.0001
        );

        for presentation_id in [
            "RUIN_ACCELERATION",
            "RUIN_QUICKENING",
            "RUIN_CHAIN_REACTION",
            "RUIN_POTENTIAL",
        ] {
            assert!(catalog
                .action_presentations
                .iter()
                .any(|presentation| presentation.presentation_id == presentation_id));
            assert!(!catalog
                .combat_profile_action_bar_defaults
                .iter()
                .any(|assignment| assignment.ability_id == presentation_id));
        }
        assert!(authored_status_presentation_ids(catalog).contains("QUICKENING"));
    }

    #[test]
    fn ruin_capacitor_authors_targeted_lightning_column_and_discharge_vfx() {
        let catalog = progression_catalog();
        let capacitor = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_CAPACITOR")
            .expect("Capacitor ability should be authored");
        assert_eq!(capacitor.discipline_id, DISCIPLINE_RUIN);
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
    fn ruin_rime_authors_frost_debuff_protection_meta_status() {
        let catalog = progression_catalog();
        let rime = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_RIME")
            .expect("Rime ability should be authored");
        assert_eq!(rime.discipline_id, DISCIPLINE_RUIN);
        assert_eq!(rime.action_id, "RIME");
        assert_eq!(ability_gameplay_kind(rime), "PASSIVE");
        assert!(rime.gameplay.frost_spell_debuff_protection);
        assert!(rime
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| assignment.ability_id == "RUIN_RIME"));

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
    fn ruin_blizzard_authors_point_area_channel_and_persistent_ice_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_BLIZZARD")
            .expect("Blizzard ability should be authored");
        assert_eq!(ability.discipline_id, DISCIPLINE_RUIN);
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
        assert_eq!(immolation.discipline_id, DISCIPLINE_RUIN);
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
        assert_eq!(combustion.discipline_id, DISCIPLINE_RUIN);
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
    fn ruin_fracture_flash_freeze_and_deepening_cold_author_shatter_combo() {
        let catalog = progression_catalog();
        let fracture = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "RUIN_FRACTURE")
            .expect("Fracture ability should be authored");
        assert_eq!(
            normalize_identifier(fracture.discipline_id.as_str()),
            DISCIPLINE_RUIN
        );
        assert!(fracture.combat_profile_id.is_empty());
        assert_eq!(fracture.action_id, "FRACTURE");
        assert_eq!(ability_gameplay_kind(fracture), "PASSIVE");
        assert!(fracture
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!((fracture.gameplay.frozen_melee_first_hit_damage_bonus - 0.5).abs() < 0.0001);
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| assignment.ability_id == "RUIN_FRACTURE"));

        let flash_freeze = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_FLASH_FREEZE")
            .expect("Flash Freeze ability should be authored");
        assert_eq!(
            normalize_identifier(flash_freeze.discipline_id.as_str()),
            DISCIPLINE_RUIN
        );
        assert_eq!(flash_freeze.action_id, "FLASH_FREEZE");
        assert_eq!(ability_gameplay_kind(flash_freeze), "SPELL");
        assert_eq!(flash_freeze.gameplay.cast_time_ms, Some(0));
        assert_eq!(flash_freeze.gameplay.cooldown_ms, Some(12_000));
        assert_eq!(flash_freeze.gameplay.resource_cost, Some(20.0));

        let definition = spell_definition_by_str("FLASH_FREEZE")
            .expect("Flash Freeze should derive a spell definition");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.duration, 1.0);
        assert_eq!(definition.max_distance, 30.0);
        assert_eq!(definition.damage_type, DamageType::Cold);
        assert_eq!(definition.status_stack_group.as_deref(), Some("FREEZE"));
        let status = definition
            .apply_status
            .as_ref()
            .expect("Flash Freeze should apply Freeze");
        assert_eq!(status.payload(), StatusPayload::Freeze);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Magic]);

        for ability_id in ["RUIN_FRACTURE", "SPELL_FLASH_FREEZE"] {
            assert!(catalog.action_presentations.iter().any(|presentation| {
                presentation.presentation_kind == "ABILITY"
                    && presentation.presentation_id == ability_id
            }));
        }
        assert!(catalog.action_presentations.iter().any(|presentation| {
            presentation.presentation_kind == "STATUS"
                && presentation.presentation_id == "FREEZE"
                && presentation.display_name == "Frozen"
        }));
        assert!(catalog.combat_vfx_cues.iter().any(|cue| {
            normalize_identifier(cue.owner_id.as_str()) == "SPELL_FLASH_FREEZE"
                && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
                && normalize_identifier(cue.vfx_id.as_str()) == "VFX_GLACIAL_SPIKE_TARGET_01"
        }));

        let deepening_cold = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_DEEPENING_COLD")
            .expect("Deepening Cold ability should be authored");
        assert_eq!(
            normalize_identifier(deepening_cold.discipline_id.as_str()),
            DISCIPLINE_RUIN
        );
        assert_eq!(deepening_cold.action_id, "DEEPENING_COLD");
        assert_eq!(ability_gameplay_kind(deepening_cold), "SPELL");
        assert_eq!(deepening_cold.gameplay.cast_time_ms, Some(0));
        assert_eq!(deepening_cold.gameplay.cooldown_ms, Some(12_000));
        assert_eq!(deepening_cold.gameplay.resource_cost, Some(20.0));

        let definition = spell_definition_by_str("DEEPENING_COLD")
            .expect("Deepening Cold should derive a spell definition");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.duration, 3.0);
        assert_eq!(definition.max_distance, 30.0);
        assert_eq!(definition.damage_type, DamageType::Cold);
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("DEEPENING_COLD_SLOW")
        );
        let slow = definition
            .apply_status
            .as_ref()
            .expect("Deepening Cold should apply an initial Slow");
        assert_eq!(slow.payload(), StatusPayload::Slow { slow_pct: 0.1 });
        assert_eq!(slow.max_stacks, 3);
        assert_eq!(slow.stack_policy, StackPolicy::AddStackRefresh);

        let staged = &definition
            .secondary
            .apply_status
            .as_ref()
            .expect("Deepening Cold should define staged statuses")
            .staged_applications;
        assert_eq!(staged.len(), 3);
        assert_eq!(staged[0].delay, Duration::from_secs(1));
        assert_eq!(staged[0].duration, Duration::from_secs(2));
        assert_eq!(
            staged[0].status.payload(),
            StatusPayload::Slow { slow_pct: 0.1 }
        );
        assert_eq!(staged[1].delay, Duration::from_secs(2));
        assert_eq!(staged[1].duration, Duration::from_secs(1));
        assert_eq!(
            staged[1].status.payload(),
            StatusPayload::Slow { slow_pct: 0.1 }
        );
        assert_eq!(staged[2].delay, Duration::from_secs(3));
        assert_eq!(staged[2].duration, Duration::from_secs(2));
        assert_eq!(staged[2].status_stack_group.as_deref(), Some("FREEZE"));
        assert_eq!(staged[2].status.payload(), StatusPayload::Freeze);
        assert!(catalog.action_presentations.iter().any(|presentation| {
            presentation.presentation_kind == "ABILITY"
                && presentation.presentation_id == "SPELL_DEEPENING_COLD"
        }));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            presentation.presentation_kind == "STATUS"
                && presentation.presentation_id == "DEEPENING_COLD_SLOW"
        }));
        assert!(catalog.combat_vfx_cues.iter().any(|cue| {
            normalize_identifier(cue.owner_id.as_str()) == "SPELL_DEEPENING_COLD"
                && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
                && normalize_identifier(cue.vfx_id.as_str()) == "VFX_GLACIAL_SPIKE_TARGET_01"
        }));
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
            normalize_identifier(fulmination.discipline_id.as_str()),
            DISCIPLINE_RUIN
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
    fn graveburst_stays_in_blight_and_authors_delayed_area_impact_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_GRAVEBURST")
            .expect("expected Graveburst ability");
        assert_eq!(ability.discipline_id, "BLIGHT");
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
    fn gravewake_stays_in_blight_and_authors_moving_bone_wave_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_GRAVEWAKE")
            .expect("expected Gravewake ability");
        assert_eq!(ability.discipline_id, "BLIGHT");
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
    fn necro_prison_stays_in_blight_and_authors_movement_only_zone_contract() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_NECRO_PRISON")
            .expect("expected Necro Prison ability");
        assert_eq!(ability.discipline_id, "BLIGHT");
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
    fn blood_offering_stays_in_blight_and_authors_health_for_mana_contract() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_BLOOD_OFFERING")
            .expect("expected Blood Offering ability");
        assert_eq!(ability.discipline_id, "BLIGHT");
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
    fn progression_catalog_ids_are_unique_and_non_empty() {
        let catalog = progression_catalog();

        let mut combat_profile_ids = HashSet::new();
        for definition in &catalog.combat_profiles {
            assert!(!definition.combat_profile_id.trim().is_empty());
            assert!(combat_profile_ids.insert(definition.combat_profile_id.clone()));
        }

        let mut resource_kinds = HashSet::new();
        for definition in &catalog.resources {
            assert!(!definition.resource_kind.trim().is_empty());
            assert!(resource_kinds.insert(definition.resource_kind.clone()));
            assert!(!definition.display_name.trim().is_empty());
            assert!(definition.base_max >= 0.0);
        }

        let mut ability_ids = HashSet::new();
        for definition in &catalog.abilities {
            assert!(!definition.ability_id.trim().is_empty());
            assert!(ability_ids.insert(definition.ability_id.clone()));
            assert!(!definition.action_id.trim().is_empty());
            let ability_resource_kind = normalize_identifier(definition.resource_kind.as_str());
            assert!(
                ability_resource_kind.is_empty()
                    || ability_resource_kind == RESOURCE_KIND_STAMINA
                    || ability_resource_kind == "MANA",
                "ability '{}' must use STAMINA, MANA, or empty resource_kind, found '{}'",
                definition.ability_id,
                definition.resource_kind
            );
            let gameplay_kind = normalize_identifier(definition.gameplay.kind.as_str());
            if gameplay_kind == "SPELL" {
                let is_free_npc_action = normalize_identifier(definition.actor_scope.as_str())
                    == "NPC"
                    && ability_resource_kind.is_empty()
                    && definition.resource_cost == 0.0;
                assert!(
                    ability_resource_kind == "MANA" || is_free_npc_action,
                    "spell ability '{}' must use MANA unless it is an explicitly free NPC action",
                    definition.ability_id
                );
            } else if gameplay_kind == "PASSIVE" {
                let is_spell_school_passive = definition.combat_profile_id.trim().is_empty();
                assert!(
                    (is_spell_school_passive && ability_resource_kind == "MANA")
                        || (!is_spell_school_passive
                            && ability_resource_kind == RESOURCE_KIND_STAMINA),
                    "passive ability '{}' must use MANA when profile-neutral or STAMINA when profile-bound",
                    definition.ability_id
                );
            } else if matches!(
                gameplay_kind.as_str(),
                "MELEE" | "MOVEMENT" | "AUTO_ATTACK_REPLACEMENT"
            ) {
                let is_free_npc_action = normalize_identifier(definition.actor_scope.as_str())
                    == "NPC"
                    && definition.resource_cost == 0.0;
                assert!(
                    ability_resource_kind == RESOURCE_KIND_STAMINA
                        || (is_free_npc_action && ability_resource_kind.is_empty()),
                    "martial ability '{}' must use STAMINA unless it is an explicit zero-cost NPC action",
                    definition.ability_id
                );
            }
        }

        let mut action_presentation_keys = HashSet::new();
        for definition in &catalog.action_presentations {
            let key = action_presentation_key(definition);
            assert!(!key.trim().is_empty());
            assert_eq!(key, normalize_identifier(key.as_str()));
            assert!(!normalize_identifier(definition.presentation_id.as_str()).is_empty());
            assert!(action_presentation_keys.insert(key));
            assert!(!definition.display_name.trim().is_empty());
        }

        for row in derived_spell_action_presentation_rows(catalog) {
            assert!(!row.key.trim().is_empty());
            assert_eq!(row.key, normalize_identifier(row.key.as_str()));
            assert!(action_presentation_keys.insert(row.key));
            assert_eq!(row.presentation_kind, "SPELL");
            assert!(!row.presentation_id.trim().is_empty());
            assert!(!row.display_name.trim().is_empty());
        }

        let mut slot_ids = HashSet::new();
        for definition in &catalog.slots {
            assert!(!definition.slot_id.trim().is_empty());
            assert!(slot_ids.insert(definition.slot_id.clone()));
            assert_grid_slot_id(definition.slot_id.as_str());
        }
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
    fn progression_auto_attacks_resolve_for_every_combat_profile_mode() {
        let catalog = progression_catalog();

        for profile in &catalog.combat_profiles {
            let profile_id = normalize_identifier(profile.combat_profile_id.as_str());
            let action_id =
                auto_attack_reference_for_profile(profile_id.as_str()).unwrap_or_else(|| {
                    panic!("combat profile '{profile_id}' is missing an auto attack reference")
                });
            let profile_modes: Vec<String> = catalog
                .combat_modes
                .iter()
                .filter(|mode| normalize_identifier(mode.combat_profile_id.as_str()) == profile_id)
                .map(|mode| normalize_identifier(mode.mode_id.as_str()))
                .collect();
            let modes_to_resolve = if profile_modes.is_empty() {
                vec![String::new()]
            } else {
                profile_modes
            };

            for mode_id in modes_to_resolve {
                assert!(
                    catalog.auto_attacks.iter().any(|row| {
                        normalize_identifier(row.combat_profile_id.as_str()) == profile_id
                            && AuthoredActionId::new(row.action_id.as_str()).as_str()
                                == action_id
                            && {
                                let row_mode = normalize_identifier(row.mode_id.as_str());
                                row_mode.is_empty() || row_mode == mode_id
                            }
                    }),
                    "combat profile '{}' mode '{}' cannot resolve auto attack '{}' from a mode override or shared profile row",
                    profile_id,
                    mode_id,
                    action_id
                );
            }
        }
    }

    #[test]
    fn archer_draw_modes_are_authored_for_auto_attack() {
        validate_combat_mode_catalog();
        validate_auto_attack_catalog();

        let catalog = progression_catalog();
        let archer_modes: HashSet<_> = catalog
            .combat_modes
            .iter()
            .filter(|mode| {
                normalize_identifier(mode.combat_profile_id.as_str()) == COMBAT_PROFILE_ARCHER_BOW
            })
            .map(|mode| normalize_identifier(mode.mode_id.as_str()))
            .collect();
        assert!(archer_modes.contains(COMBAT_MODE_SHORT_DRAW));
        assert!(archer_modes.contains(COMBAT_MODE_FULL_DRAW));

        let short_draw = catalog
            .auto_attacks
            .iter()
            .find(|attack| {
                normalize_identifier(attack.combat_profile_id.as_str()) == COMBAT_PROFILE_ARCHER_BOW
                    && normalize_identifier(attack.mode_id.as_str()) == COMBAT_MODE_SHORT_DRAW
            })
            .expect("SHORT_DRAW auto attack row");
        let full_draw = catalog
            .auto_attacks
            .iter()
            .find(|attack| {
                normalize_identifier(attack.combat_profile_id.as_str()) == COMBAT_PROFILE_ARCHER_BOW
                    && normalize_identifier(attack.mode_id.as_str()) == COMBAT_MODE_FULL_DRAW
            })
            .expect("FULL_DRAW auto attack row");

        assert!(short_draw.base_damage < full_draw.base_damage);
        assert!(short_draw.range < full_draw.range);
        assert_eq!(
            normalize_identifier(short_draw.movement_policy.as_str()),
            AUTO_ATTACK_MOVEMENT_ALLOW_MOVING
        );
        assert_eq!(
            normalize_identifier(full_draw.movement_policy.as_str()),
            AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE
        );

        let toggle = catalog
            .abilities
            .iter()
            .find(|ability| {
                normalize_identifier(ability.ability_id.as_str())
                    == ARCHER_DRAW_MODE_TOGGLE_ABILITY_ID
            })
            .expect("Archer draw mode toggle ability");
        assert_eq!(
            ability_gameplay_kind(toggle),
            ABILITY_KIND_COMBAT_MODE_TOGGLE
        );
        assert!(
            toggle
                .ability_tags
                .iter()
                .any(|tag| normalize_identifier(tag.as_str()) == "ACTION_BAR_ACTION"),
            "Archer draw mode toggle should be a action-bar action"
        );
        assert!(
            catalog
                .combat_profile_action_bar_defaults
                .iter()
                .any(|assignment| {
                    normalize_identifier(assignment.combat_profile_id.as_str())
                        == COMBAT_PROFILE_ARCHER_BOW
                        && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == "SLOT_1_1"
                        && normalize_identifier(assignment.ability_id.as_str())
                            == ARCHER_DRAW_MODE_TOGGLE_ABILITY_ID
                }),
            "Archer draw mode toggle should have an action-bar slot assignment"
        );
    }

    #[test]
    fn dagger_shroud_is_authored_as_timed_breakable_profile_mode() {
        validate_combat_mode_catalog();

        let catalog = progression_catalog();
        let dagger_modes: HashSet<_> = catalog
            .combat_modes
            .iter()
            .filter(|mode| {
                normalize_identifier(mode.combat_profile_id.as_str()) == COMBAT_PROFILE_DAGGERS
            })
            .map(|mode| normalize_identifier(mode.mode_id.as_str()))
            .collect();
        assert!(dagger_modes.contains(COMBAT_MODE_READY));
        assert!(dagger_modes.contains(COMBAT_MODE_STEALTHED));

        let shroud = catalog
            .abilities
            .iter()
            .find(|ability| {
                normalize_identifier(ability.ability_id.as_str()) == DAGGER_SHROUD_ABILITY_ID
            })
            .expect("Dagger Shroud ability");
        assert_eq!(
            normalize_identifier(shroud.combat_profile_id.as_str()),
            COMBAT_PROFILE_DAGGERS
        );
        assert_eq!(
            ability_gameplay_kind(shroud),
            ABILITY_KIND_COMBAT_MODE_TOGGLE
        );
        assert_eq!(shroud.display_name, "Shroud");
        assert_eq!(shroud.gameplay.cooldown_ms, Some(60_000));
        assert_eq!(shroud.gameplay.duration_ms, Some(5_000));
        assert!(shroud.gameplay.break_on_attack);
        assert!(shroud.gameplay.break_on_direct_damage);
        assert!(
            shroud
                .ability_tags
                .iter()
                .any(|tag| normalize_identifier(tag.as_str()) == "ACTION_BAR_ACTION"),
            "Dagger Shroud should be an action-bar action"
        );
        assert!(
            catalog
                .combat_profile_action_bar_defaults
                .iter()
                .any(|assignment| {
                    normalize_identifier(assignment.combat_profile_id.as_str())
                        == COMBAT_PROFILE_DAGGERS
                        && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == "SLOT_1_1"
                        && normalize_identifier(assignment.ability_id.as_str())
                            == DAGGER_SHROUD_ABILITY_ID
                }),
            "Dagger Shroud should have an action-bar slot assignment"
        );
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
            ("DAGGER_RIPOSTE", "DAGGER_RIPOSTE"),
            ("DAGGER_DASHING_CUT", "DAGGER_DASHING_CUT"),
            ("DAGGER_ROUNDHOUSE", "DAGGER_ROUNDHOUSE"),
            ("DAGGER_GUT_RIPPER", "DAGGER_COMBO_ATTACK_04_01"),
            ("DAGGER_SPINNING_SLASH", "DAGGER_COMBO_ATTACK_03_01"),
            ("DAGGER_CROSSCUT", "DAGGER_COMBO_ATTACK_02_04"),
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
            ("DAGGER_FLURRY", "DAGGER_FLURRY"),
        ] {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| normalize_identifier(ability.ability_id.as_str()) == ability_id)
                .unwrap_or_else(|| panic!("{ability_id} must exist"));

            assert_eq!(
                normalize_identifier(ability.combat_profile_id.as_str()),
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
    fn dagger_channel_attacks_author_movement_canceling_runtime() {
        assert_eq!(
            melee_channel_for_ability_id("DAGGER_FLAY"),
            Some(MeleeChannelRuntime {
                duration_ms: 2500,
                first_tick_delay_ms: 44,
                tick_interval_ms: 333,
                cancel_on_movement: true,
            })
        );
        assert_eq!(
            melee_channel_for_ability_id("DAGGER_FLURRY"),
            Some(MeleeChannelRuntime {
                duration_ms: 3000,
                first_tick_delay_ms: 107,
                tick_interval_ms: 667,
                cancel_on_movement: true,
            })
        );
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
        assert!(gap_close.require_arrival_for_swing);
    }

    #[test]
    fn dagger_roundhouse_staggers_without_knockback() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_ROUNDHOUSE")
            .expect("DAGGER_ROUNDHOUSE must exist");

        assert_eq!(ability.action_id, "DAGGER_ROUNDHOUSE");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(ability.gameplay.applies_stagger, Some(true));
        assert!(melee_impact_effects_for_ability_id("DAGGER_ROUNDHOUSE").is_empty());
        assert!(catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_DAGGERS
                    && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == "SLOT_0_3"
                    && normalize_identifier(assignment.ability_id.as_str()) == "DAGGER_ROUNDHOUSE"
            }));
    }

    #[test]
    fn dagger_session_abilities_have_default_action_bar_slots() {
        let catalog = progression_catalog();
        for (ability_id, slot_id) in [
            ("DAGGER_ROUNDHOUSE", "SLOT_0_3"),
            ("DAGGER_GUT_RIPPER", "SLOT_0_4"),
            ("DAGGER_SPINNING_SLASH", "SLOT_0_5"),
            ("DAGGER_CROSSCUT", "SLOT_0_6"),
            ("DAGGER_BLADE_FLURRY", "SLOT_0_7"),
            ("DAGGER_DEADLY_FLOURISH", "SLOT_0_8"),
            ("DAGGER_PURSUE", "SLOT_1_0"),
            ("DAGGER_DOWNWARD_SLASH", "SLOT_1_2"),
            ("DAGGER_COUP_DE_GRACE", "SLOT_1_3"),
        ] {
            assert!(catalog
                .combat_profile_action_bar_defaults
                .iter()
                .any(|assignment| {
                    normalize_identifier(assignment.combat_profile_id.as_str())
                        == COMBAT_PROFILE_DAGGERS
                        && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == slot_id
                        && normalize_identifier(assignment.ability_id.as_str()) == ability_id
                }));
        }
    }

    #[test]
    fn dagger_nerve_strike_authors_four_second_stun() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "DAGGER_NERVE_STRIKE")
            .expect("DAGGER_NERVE_STRIKE must exist");

        assert_eq!(ability.discipline_id, "SUBTLETY");
        assert_eq!(ability.combat_profile_id, COMBAT_PROFILE_DAGGERS);
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
                    Some("DAGGER_GUT_RIPPER_BLEED".to_string()),
                    StatusStackGroupDefault::InstanceScopedActionSuffix("DOT"),
                    1,
                    StackPolicy::Refresh,
                )
                .with_dispel_types(vec![StatusDispelType::Bleed]),
            }]
        );
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
    fn dagger_pursue_authors_existing_linear_gap_close() {
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
        assert_eq!(normalize_identifier(gap_close.kind.as_str()), "LINEAR");
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
        assert_eq!(gap_close.impact_range, 2.5);
        assert_eq!(
            normalize_identifier(gap_close.collision_policy.as_str()),
            "REQUIRE_CLEAR_PATH"
        );
        assert!(gap_close.require_arrival_for_swing);
        assert!(!gap_close.requires_target_facing);
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
    fn warrior_maim_authors_low_damage_slow_on_hew_animation() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_MAIM")
            .expect("WARRIOR_MAIM must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_TWO_HANDED_SWORD
        );
        assert_eq!(ability.action_id, "WARRIOR_MAIM");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(ability.gameplay.base_damage, Some(10));
        assert_eq!(ability.gameplay.applies_stagger, Some(false));
        assert!(!progression_catalog()
            .combat_profile_action_bar_defaults
            .iter()
            .any(
                |assignment| normalize_identifier(assignment.ability_id.as_str()) == "WARRIOR_MAIM"
            ));
        assert_eq!(
            melee_impact_effects_for_ability_id("WARRIOR_MAIM"),
            vec![MeleeImpactEffectRuntime::ApplyStatus {
                status: StatusApplication::new(
                    StatusPayload::Slow { slow_pct: 0.5 },
                    std::time::Duration::from_millis(10000),
                    None,
                    StatusStackGroupDefault::ActionSuffix("SLOW"),
                    1,
                    StackPolicy::Refresh,
                ),
            }]
        );
    }

    #[test]
    fn warrior_butcher_authors_low_to_high_execute_damage() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_BUTCHER")
            .expect("WARRIOR_BUTCHER must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_TWO_HANDED_SWORD
        );
        assert_eq!(ability.action_id, "COMBO_ATTACK_3_1_LOW_TO_HIGH");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(ability.gameplay.base_damage, Some(36));
        let scaling = ability
            .gameplay
            .target_health_damage_scaling
            .as_ref()
            .expect("Butcher must author target-health damage scaling");
        assert_eq!(scaling.min_multiplier, 1.0);
        assert_eq!(scaling.max_multiplier, 2.0);
        assert_eq!(ability.gameplay.applies_stagger, Some(false));
        assert!(catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_TWO_HANDED_SWORD
                    && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == "SLOT_1_2"
                    && normalize_identifier(assignment.ability_id.as_str()) == "WARRIOR_BUTCHER"
            }));
    }

    #[test]
    fn warrior_carve_authors_standalone_low_to_high_bleed() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_CARVE")
            .expect("WARRIOR_CARVE must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_TWO_HANDED_SWORD
        );
        assert_eq!(ability.action_id, "WARRIOR_CARVE");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(ability.gameplay.base_damage, Some(20));
        assert_eq!(ability.gameplay.applies_stagger, Some(false));
        assert!(profile_supports_action_reference(
            COMBAT_PROFILE_TWO_HANDED_SWORD,
            &AuthoredActionId::new("WARRIOR_CARVE")
        ));
        let two_handed_sword_asset = animation_set_assets_by_combat_profile()
            .get(COMBAT_PROFILE_TWO_HANDED_SWORD)
            .expect("TwoHandedSword animation set asset must be indexed");
        assert!(
            two_handed_sword_asset.contains(
                "- clip: {fileID: 7400000, guid: aa30250532e0e4b18be2027b6050bcaa, type: 2}\n    combat:\n      id: WARRIOR_CARVE\n      slotId: carve"
            ),
            "WARRIOR_CARVE must reuse the COMBO_ATTACK_1_2_LOW_TO_HIGH clip as a standalone action"
        );
        assert!(catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_TWO_HANDED_SWORD
                    && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == "SLOT_1_5"
                    && normalize_identifier(assignment.ability_id.as_str()) == "WARRIOR_CARVE"
            }));
        assert_eq!(
            melee_impact_effects_for_ability_id("WARRIOR_CARVE"),
            vec![MeleeImpactEffectRuntime::ApplyStatus {
                status: StatusApplication::new(
                    StatusPayload::Dot {
                        tick_damage: 3,
                        damage_type: crate::combat::DamageType::Physical,
                        tick_interval: Duration::from_secs(1),
                    },
                    Duration::from_millis(6000),
                    Some("WARRIOR_CARVE_BLEED".to_string()),
                    StatusStackGroupDefault::InstanceScopedActionSuffix("DOT"),
                    1,
                    StackPolicy::Refresh,
                )
                .with_dispel_types(vec![StatusDispelType::Bleed]),
            }]
        );
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
            assert_eq!(gap_close.impact_range, 2.5);
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
            normalize_identifier(ability.combat_profile_id.as_str()),
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
    fn paladin_lunging_strike_and_shield_slam_use_authored_gap_closer_strikes() {
        let catalog = progression_catalog();
        let expected = [
            (
                "PALADIN_AIR_TO_GROUND_1",
                "AIR_TO_GROUND_1",
                "Lunging Strike",
                "SLOT_1_5",
                5.0,
            ),
            (
                "PALADIN_AIR_TO_GROUND_3",
                "AIR_TO_GROUND_3",
                "Shield Slam",
                "SLOT_1_4",
                0.0,
            ),
        ];

        for (ability_id, action_id, display_name, slot_id, minimum_range) in expected {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("{ability_id} must exist"));

            assert_eq!(
                normalize_identifier(ability.combat_profile_id.as_str()),
                COMBAT_PROFILE_SWORD_AND_SHIELD
            );
            assert_eq!(ability.action_id, action_id);
            assert_eq!(ability.display_name, display_name);
            assert_eq!(ability_gameplay_kind(ability), "MELEE");
            assert_eq!(ability.gameplay.range, Some(18.0));
            assert_eq!(ability.gameplay.minimum_range, Some(minimum_range));
            let gap_close = ability
                .gameplay
                .gap_close
                .as_ref()
                .expect("active air-to-ground attacks must author gap_close");
            assert_eq!(normalize_identifier(gap_close.kind.as_str()), "LINEAR");
            assert_eq!(
                normalize_identifier(gap_close.destination.as_str()),
                "NEAREST_CONTACT_POINT"
            );
            assert_eq!(gap_close.speed, Some(23.0));
            assert_eq!(gap_close.arrival_buffer, 1.44);
            assert_eq!(gap_close.arrival_epsilon, 0.05);
            assert_eq!(gap_close.impact_range, 2.5);
            assert_eq!(
                normalize_identifier(gap_close.collision_policy.as_str()),
                "STOP_AT_BLOCK"
            );
            assert!(gap_close.require_arrival_for_swing);
            assert!(!gap_close.requires_target_facing);
            assert!(profile_supports_action_reference(
                COMBAT_PROFILE_SWORD_AND_SHIELD,
                &AuthoredActionId::new(action_id)
            ));
            assert!(catalog
                .combat_profile_action_bar_defaults
                .iter()
                .any(|assignment| {
                    normalize_identifier(assignment.combat_profile_id.as_str())
                        == COMBAT_PROFILE_SWORD_AND_SHIELD
                        && normalize_identifier(assignment.ability_id.as_str()) == ability_id
                        && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == slot_id
                }));
        }

        assert!(catalog
            .abilities
            .iter()
            .all(|ability| ability.ability_id != "PALADIN_AIR_TO_GROUND_2"));
        assert!(catalog
            .combat_profile_action_bar_defaults
            .iter()
            .all(|assignment| assignment.ability_id != "PALADIN_AIR_TO_GROUND_2"));
        assert_eq!(
            melee_impact_effects_for_ability_id("PALADIN_AIR_TO_GROUND_3"),
            vec![MeleeImpactEffectRuntime::ApplyStatus {
                status: StatusApplication::new(
                    StatusPayload::Stun,
                    std::time::Duration::from_millis(3000),
                    Some("PALADIN_SHIELD_SLAM_STUN".to_string()),
                    StatusStackGroupDefault::ActionSuffix("STUN"),
                    1,
                    StackPolicy::Refresh,
                ),
            }]
        );
    }

    #[test]
    fn paladin_rebuke_uses_finisher_1_and_applies_branded_holy_dot() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_REBUKE")
            .expect("PALADIN_REBUKE must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability.action_id, "SWORD_AND_SHIELD_FINISHER_1");
        assert_eq!(ability.display_name, "Rebuke");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert!(profile_supports_action_reference(
            COMBAT_PROFILE_SWORD_AND_SHIELD,
            &AuthoredActionId::new("SWORD_AND_SHIELD_FINISHER_1")
        ));
        assert!(catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_SWORD_AND_SHIELD
                    && normalize_identifier(assignment.ability_id.as_str()) == "PALADIN_REBUKE"
                    && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == "SLOT_1_2"
            }));
        assert_eq!(
            melee_impact_effects_for_ability_id("PALADIN_REBUKE"),
            vec![MeleeImpactEffectRuntime::ApplyStatus {
                status: StatusApplication::new(
                    StatusPayload::Dot {
                        tick_damage: 4,
                        damage_type: crate::combat::DamageType::Holy,
                        tick_interval: Duration::from_secs(1),
                    },
                    Duration::from_millis(6000),
                    Some("PALADIN_BRANDED".to_string()),
                    StatusStackGroupDefault::InstanceScopedActionSuffix("DOT"),
                    1,
                    StackPolicy::Refresh,
                )
                .with_dispel_types(vec![StatusDispelType::Magic]),
            }]
        );
    }

    #[test]
    fn paladin_hallowed_thrust_uses_finisher_1_and_default_slot() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_HALLOWED_THRUST")
            .expect("PALADIN_HALLOWED_THRUST must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability.action_id, "SWORD_AND_SHIELD_FINISHER_1");
        assert_eq!(ability.display_name, "Hallowed Thrust");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(ability.resource_cost, 20.0);
        assert!(profile_supports_action_reference(
            COMBAT_PROFILE_SWORD_AND_SHIELD,
            &AuthoredActionId::new("SWORD_AND_SHIELD_FINISHER_1")
        ));
        assert!(catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_SWORD_AND_SHIELD
                    && normalize_identifier(assignment.ability_id.as_str())
                        == "PALADIN_HALLOWED_THRUST"
                    && canonical_action_bar_slot_id(assignment.slot_id.as_str()) == "SLOT_0_4"
            }));
    }

    #[test]
    fn paladin_sacred_thrust_is_a_distinct_rectangular_melee_with_requested_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_SACRED_THRUST")
            .expect("PALADIN_SACRED_THRUST must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability.action_id, "SWORD_AND_SHIELD_ALT_LIGHT_3");
        assert_eq!(ability.display_name, "Sacred Thrust");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(ability.resource_cost, 20.0);
        assert_eq!(ability.gameplay.base_damage, Some(30));
        assert_eq!(ability.gameplay.damage_type.as_deref(), Some("HOLY"));
        assert_eq!(ability.gameplay.range, Some(5.0));
        assert_eq!(ability.gameplay.requires_target_los, Some(true));

        let targeting = resolved_melee_targeting_for_catalog(&ability.gameplay);
        assert_eq!(targeting.kind, "CASTER_RECTANGLE");
        assert!(!targeting.requires_target);
        assert_eq!(targeting.range, 5.0);
        assert_eq!(targeting.width, 1.25);
        assert!(profile_supports_action_reference(
            COMBAT_PROFILE_SWORD_AND_SHIELD,
            &AuthoredActionId::new("SWORD_AND_SHIELD_ALT_LIGHT_3")
        ));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_SWORD_AND_SHIELD
                    && normalize_identifier(assignment.ability_id.as_str())
                        == "PALADIN_SACRED_THRUST"
            }));

        let forward_cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "PALADIN_SACRED_THRUST"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Sacred Thrust should author a facing-aligned forward VFX cue");
        assert_eq!(
            normalize_identifier(forward_cue.anchor.as_str()),
            "AREA_ORIGIN"
        );
        assert_eq!(
            normalize_identifier(forward_cue.attach_mode.as_str()),
            "WORLD_ALIGNED_TO_FACING"
        );
        assert_eq!(
            normalize_identifier(forward_cue.vfx_id.as_str()),
            "VFX_SACRED_THRUST_FORWARD_01"
        );
        assert_eq!(
            normalize_identifier(forward_cue.lifecycle.as_str()),
            "DURATION"
        );
        assert_eq!(forward_cue.duration_ms, 2500);

        let hit_cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "PALADIN_SACRED_THRUST"
                    && normalize_identifier(cue.trigger.as_str()) == "MELEE_IMPACT"
            })
            .expect("Sacred Thrust should author a per-target melee hit VFX cue");
        assert_eq!(
            normalize_identifier(hit_cue.anchor.as_str()),
            "IMPACT_POINT"
        );
        assert_eq!(hit_cue.hit_index, Some(0));
        assert_eq!(
            normalize_identifier(hit_cue.vfx_id.as_str()),
            "VFX_SACRED_THRUST_HIT_01"
        );
        assert_eq!(
            normalize_identifier(hit_cue.lifecycle.as_str()),
            "PARTICLE_SYSTEM"
        );

        let sword_and_shield_asset = animation_set_assets_by_combat_profile()
            .get(COMBAT_PROFILE_SWORD_AND_SHIELD)
            .expect("SwordAndShield animation set");
        assert!(sword_and_shield_asset.contains("id: SWORD_AND_SHIELD_ALT_LIGHT_3"));
        assert!(sword_and_shield_asset
            .contains("clip: {fileID: 7400000, guid: 065abbc4be9a94fd5a87d76ce7b75cc7, type: 2}"));
    }

    #[test]
    fn paladin_serrated_blades_authors_melee_bleed_modifier_buff() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_SERRATED_BLADES")
            .expect("PALADIN_SERRATED_BLADES must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability.action_id, "SERRATED_BLADES");
        assert_eq!(ability.display_name, "Serrated Blades");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(ability.gameplay.resource_cost, Some(0.0));
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "SELF"
        );
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_SWORD_AND_SHIELD
                    && normalize_identifier(assignment.ability_id.as_str())
                        == "PALADIN_SERRATED_BLADES"
            }));

        let definition =
            spell_definition_by_str("SERRATED_BLADES").expect("Serrated Blades spell definition");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Serrated Blades should apply a status");
        assert_eq!(status.payload(), StatusPayload::MeleeAttackModifier);
        assert!((definition.duration - 10.0).abs() < 0.0001);
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("SERRATED_BLADES")
        );
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert!(status.dispel_types.is_empty());
    }

    #[test]
    fn paladin_fervor_authors_castable_move_speed_aura() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_FERVOR")
            .expect("PALADIN_FERVOR must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "SELF"
        );
        assert_eq!(
            normalize_optional_target_audience(ability.gameplay.target_audience.as_str()),
            "PARTY_OR_SELF"
        );
        assert_eq!(ability.gameplay.resource_cost, Some(0.0));
        let definition =
            spell_definition_by_str(ability.action_id.as_str()).expect("Fervor spell definition");
        assert_eq!(definition.primary_resource_cost, 0.0);
        assert_eq!(definition.behavior, crate::spells::SpellBehavior::Aura);
        assert_eq!(definition.radius, 20.0);
        assert_eq!(definition.target_audience.as_str(), "PARTY_OR_SELF");
        let aura = definition
            .secondary
            .aura
            .as_ref()
            .expect("Fervor must define aura secondary tunables");
        assert_eq!(aura.tick_interval, std::time::Duration::from_millis(250));
        let [effect] = aura.effects.as_slice() else {
            panic!("Fervor must author exactly one aura status effect");
        };
        let effect = effect.as_status().expect("Fervor effect must be a status");
        assert_eq!(
            effect.payload(),
            StatusPayload::MoveSpeed {
                modifier_scalar: 0.1
            }
        );
        assert_eq!(effect.duration(), std::time::Duration::from_millis(750));
        assert!(effect.dispel_types().is_empty());
        let default_assignment = catalog
            .combat_profile_action_bar_defaults
            .iter()
            .find(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_SWORD_AND_SHIELD
                    && normalize_identifier(assignment.ability_id.as_str()) == "PALADIN_FERVOR"
            })
            .expect("Fervor should appear on the SwordAndShield default action bar");
        assert_eq!(
            normalize_identifier(default_assignment.slot_id.as_str()),
            "SLOT_0_5"
        );
    }

    #[test]
    fn paladin_resource_thorns_and_warding_auras_author_party_effects() {
        let catalog = progression_catalog();
        let expected = [
            (
                "PALADIN_MANA_FONT",
                "MANA_FONT",
                "Mana Font",
                StatusPayload::ManaRegen {
                    modifier_scalar: 2.0,
                },
                "PALADIN_MANA_FONT_MANA_REGEN",
                "SLOT_0_6",
            ),
            (
                "PALADIN_STAMINA_FONT",
                "STAMINA_FONT",
                "Stamina Font",
                StatusPayload::StaminaRegen {
                    modifier_scalar: 5.0,
                },
                "PALADIN_STAMINA_FONT_STAMINA_REGEN",
                "SLOT_0_7",
            ),
            (
                "PALADIN_THORNS_AURA",
                "THORNS_AURA",
                "Thorns Aura",
                StatusPayload::Thorns { damage: 3 },
                "PALADIN_THORNS_AURA_THORNS",
                "SLOT_0_8",
            ),
            (
                "PALADIN_WARDING_AURA",
                "WARDING_AURA",
                "Warding Aura",
                StatusPayload::MagicResistance {
                    modifier_scalar: 0.15,
                },
                "PALADIN_WARDING_AURA_MAGIC_RESISTANCE",
                "SLOT_1_0",
            ),
            (
                "PALADIN_AURA_OF_VENGEANCE",
                "AURA_OF_VENGEANCE",
                "Aura of Vengeance",
                StatusPayload::VengeanceAura,
                "PALADIN_AURA_OF_VENGEANCE",
                "SLOT_1_6",
            ),
        ];

        for (ability_id, action_id, display_name, payload, stack_group, slot_id) in expected {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("{ability_id} must exist"));
            assert_eq!(
                normalize_identifier(ability.combat_profile_id.as_str()),
                COMBAT_PROFILE_SWORD_AND_SHIELD
            );
            assert_eq!(ability.action_id, action_id);
            assert_eq!(ability.display_name, display_name);
            assert_eq!(ability_gameplay_kind(ability), "SPELL");
            assert_eq!(
                normalize_identifier(ability.gameplay.targeting.as_str()),
                "SELF"
            );
            assert_eq!(
                normalize_optional_target_audience(ability.gameplay.target_audience.as_str()),
                "PARTY_OR_SELF"
            );
            assert_eq!(ability.gameplay.resource_cost, Some(0.0));
            let default_assignment = catalog
                .combat_profile_action_bar_defaults
                .iter()
                .find(|assignment| {
                    normalize_identifier(assignment.combat_profile_id.as_str())
                        == COMBAT_PROFILE_SWORD_AND_SHIELD
                        && normalize_identifier(assignment.ability_id.as_str()) == ability_id
                })
                .unwrap_or_else(|| {
                    panic!("{ability_id} should appear on the SwordAndShield default action bar")
                });
            assert_eq!(
                normalize_identifier(default_assignment.slot_id.as_str()),
                slot_id,
                "{ability_id} should live on the visible SwordAndShield action bars"
            );

            let definition = spell_definition_by_str(action_id)
                .unwrap_or_else(|| panic!("{action_id} spell definition"));
            assert_eq!(definition.primary_resource_cost, 0.0);
            assert_eq!(definition.behavior, crate::spells::SpellBehavior::Aura);
            assert_eq!(definition.radius, 20.0);
            assert_eq!(definition.target_audience.as_str(), "PARTY_OR_SELF");
            let aura = definition
                .secondary
                .aura
                .as_ref()
                .expect("aura spell must define aura secondary tunables");
            assert_eq!(aura.tick_interval, Duration::from_millis(250));
            let [effect] = aura.effects.as_slice() else {
                panic!("{action_id} must author exactly one aura status effect");
            };
            let effect = effect.as_status().expect("aura effect must be a status");
            assert_eq!(effect.payload(), payload);
            assert_eq!(effect.duration(), Duration::from_millis(750));
            assert_eq!(effect.explicit_stack_group(), Some(stack_group));
            assert!(effect.dispel_types().is_empty());
        }
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
            normalize_identifier(ability.combat_profile_id.as_str()),
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
    fn paladin_blade_barrier_authors_persistent_holy_target_field() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_BLADE_BARRIER")
            .expect("PALADIN_BLADE_BARRIER must exist");
        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability.action_id, "BLADE_BARRIER");
        assert_eq!(ability.display_name, "Blade Barrier");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(ability.gameplay.cast_time_ms, Some(0));
        assert_eq!(ability.gameplay.cooldown_ms, Some(7_500));
        assert_eq!(
            normalize_identifier(ability.gameplay.targeting.as_str()),
            "TARGET"
        );
        assert_eq!(
            normalize_identifier(ability.gameplay.target_audience.as_str()),
            "ANY"
        );
        assert_eq!(ability.gameplay.requires_target, Some(true));
        assert_eq!(ability.gameplay.requires_target_los, Some(true));
        assert_eq!(ability.gameplay.resource_cost, Some(0.0));
        assert_eq!(
            ability.gameplay.delivery.as_ref().unwrap()["kind"],
            "PERSISTENT_AREA"
        );

        let default_assignment = catalog
            .combat_profile_action_bar_defaults
            .iter()
            .find(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_SWORD_AND_SHIELD
                    && normalize_identifier(assignment.ability_id.as_str())
                        == "PALADIN_BLADE_BARRIER"
            })
            .expect("Blade Barrier should remain in SwordAndShield slot 1-8");
        assert_eq!(
            normalize_identifier(default_assignment.slot_id.as_str()),
            "SLOT_1_8"
        );

        let definition =
            spell_definition_by_str("BLADE_BARRIER").expect("BLADE_BARRIER spell definition");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::PersistentArea
        );
        assert_eq!(definition.targeting, crate::spells::SpellTargeting::Target);
        assert_eq!(definition.target_audience.as_str(), "ANY");
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.damage, 18);
        assert_eq!(definition.damage_type, DamageType::Holy);
        assert_eq!(definition.max_distance, 18.0);
        assert_eq!(definition.radius, 2.0);
        assert_eq!(definition.update_interval, 1.0);
        assert_eq!(definition.duration, 7.5);
        let persistent = definition
            .secondary
            .persistent_area
            .as_ref()
            .expect("Blade Barrier should define persistent-area tunables");
        assert_eq!(persistent.pulse_interval, Duration::from_millis(1_000));
        assert_eq!(persistent.effect_target_audience.as_str(), "HOSTILE");
        assert!(persistent.impact_effects.is_empty());
        assert_eq!(
            projectile_body_vfx_id_for_spell("PALADIN_BLADE_BARRIER", "BLADE_BARRIER", 0),
            None
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "PALADIN_BLADE_BARRIER"
                    && normalize_identifier(cue.slot.as_str()) == "PERSISTENT_FIELD"
            })
            .expect("Blade Barrier should author one persistent target-field cue");
        assert_eq!(cue.vfx_id, "VFX_BLADE_BARRIER_AREA_01");
        assert_eq!(normalize_identifier(cue.trigger.as_str()), "SPELL_IMPACT");
        assert_eq!(normalize_identifier(cue.anchor.as_str()), "TARGET");
        assert_eq!(
            normalize_identifier(cue.attach_mode.as_str()),
            "FOLLOW_ANCHOR"
        );
        assert_eq!(normalize_identifier(cue.vfx_role.as_str()), "ATTACHED");
        assert_eq!(normalize_identifier(cue.lifecycle.as_str()), "DURATION");
        assert_eq!(cue.duration_ms, 7_500);
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
    fn paladin_sacred_flame_authors_non_projectile_dot_and_landing_vfx() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "PALADIN_SACRED_FLAME")
            .expect("PALADIN_SACRED_FLAME must exist");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_SWORD_AND_SHIELD
        );
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        assert_eq!(ability.action_id, "SACRED_FLAME");
        assert_eq!(ability.gameplay.resource_cost, Some(1.0));

        let definition =
            spell_definition_by_str("SACRED_FLAME").expect("Sacred Flame spell definition");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        assert_eq!(definition.target_audience.as_str(), "HOSTILE");
        assert!(definition.secondary.projectile.is_none());
        assert_eq!(
            definition
                .apply_status
                .as_ref()
                .expect("Sacred Flame should apply a status")
                .payload(),
            StatusPayload::Dot {
                tick_damage: 4,
                damage_type: crate::combat::DamageType::Physical,
                tick_interval: Duration::from_secs(1),
            }
        );
        assert_eq!(
            definition
                .apply_status
                .as_ref()
                .expect("Sacred Flame should apply a status")
                .dispel_types,
            vec![StatusDispelType::Magic]
        );

        let cue = catalog
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_kind.as_str()) == "ABILITY"
                    && normalize_identifier(cue.owner_id.as_str()) == "PALADIN_SACRED_FLAME"
                    && normalize_identifier(cue.trigger.as_str()) == "SPELL_IMPACT"
            })
            .expect("Sacred Flame should author an initial landing VFX cue");

        assert_eq!(normalize_identifier(cue.anchor.as_str()), "IMPACT_POINT");
        assert_eq!(
            normalize_identifier(cue.vfx_id.as_str()),
            "VFX_SACRED_FLAME_HIT_01"
        );
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                normalize_identifier(assignment.combat_profile_id.as_str())
                    == COMBAT_PROFILE_SWORD_AND_SHIELD
                    && normalize_identifier(assignment.ability_id.as_str())
                        == "PALADIN_SACRED_FLAME"
            }));
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
            normalize_identifier(ability.combat_profile_id.as_str()),
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

        let sword_and_shield_asset = animation_set_assets_by_combat_profile()
            .get(COMBAT_PROFILE_SWORD_AND_SHIELD)
            .expect("SwordAndShield animation set");
        assert!(
            sword_and_shield_asset.contains("- spellId: RADIANT_BURST"),
            "Radiant Burst must resolve through the SwordAndShield spell animation entries"
        );
        assert!(
            sword_and_shield_asset.contains(
                "ground: {fileID: 7400000, guid: b77a7a02d110945d7bd3e5e445fbc043, type: 2}"
            ),
            "Radiant Burst must use the requested SwordAndShield Combo_Attack_01_03 clip"
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
    fn selectable_slots_are_always_available_in_catalog_order() {
        assert_eq!(
            selectable_slot_ids(),
            vec![
                "SLOT_0_0".to_string(),
                "SLOT_0_1".to_string(),
                "SLOT_0_2".to_string(),
                "SLOT_0_3".to_string(),
                "SLOT_0_4".to_string(),
                "SLOT_0_5".to_string(),
                "SLOT_0_6".to_string(),
                "SLOT_0_7".to_string(),
                "SLOT_0_8".to_string(),
                "SLOT_1_0".to_string(),
                "SLOT_1_1".to_string(),
                "SLOT_1_2".to_string(),
                "SLOT_1_3".to_string(),
                "SLOT_1_4".to_string(),
                "SLOT_1_5".to_string(),
                "SLOT_1_6".to_string(),
                "SLOT_1_7".to_string(),
                "SLOT_1_8".to_string(),
                "SLOT_2_0".to_string(),
                "SLOT_2_1".to_string(),
                "SLOT_2_2".to_string(),
                "SLOT_2_3".to_string(),
                "SLOT_2_4".to_string(),
                "SLOT_2_5".to_string(),
                "SLOT_2_6".to_string(),
                "SLOT_2_7".to_string(),
                "SLOT_2_8".to_string()
            ]
        );
    }

    #[test]
    fn ability_slot_compatibility_matches_tag_contract() {
        assert!(!progression_catalog().abilities.is_empty());
        assert!(ability_is_compatible_with_slot("WARRIOR_HEW", "slot_0_0"));
        assert!(!ability_is_compatible_with_slot("UNKNOWN", "slot_0_0"));
        assert!(!ability_is_compatible_with_slot("WARRIOR_HEW", "UNKNOWN"));
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
        let combat_profile_id = COMBAT_PROFILE_TWO_HANDED_SWORD;

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
            combat_profile_id,
            &AuthoredActionId::new(ability.action_id.as_str())
        ));
    }

    #[test]
    fn renamed_skyfall_sequence_authors_current_ids() {
        let catalog = progression_catalog();
        let expected = [
            (
                "WARRIOR_CRUSHING_BLOW",
                "CRUSHING_BLOW",
                "Crushing Blow",
                "slot_0_1",
            ),
            ("WARRIOR_CATACLYSM", "CATACLYSM", "Cataclysm", "slot_0_2"),
            ("WARRIOR_BUZZSAW", "BUZZSAW", "Buzzsaw", "slot_0_3"),
        ];

        for (ability_id, action_id, display_name, slot_id) in expected {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("expected renamed ability {ability_id}"));
            assert_eq!(ability.action_id, action_id);
            assert_eq!(ability.display_name, display_name);
            assert!(profile_supports_action_reference(
                COMBAT_PROFILE_TWO_HANDED_SWORD,
                &AuthoredActionId::new(action_id)
            ));

            let default = catalog
                .combat_profile_action_bar_defaults
                .iter()
                .find(|assignment| assignment.ability_id == ability_id)
                .unwrap_or_else(|| panic!("expected action-bar default for {ability_id}"));
            assert_eq!(default.slot_id, slot_id);
        }

        for old_ability_id in [
            "WARRIOR_SKYFALL_1",
            "WARRIOR_SKYFALL_2",
            "WARRIOR_SKYFALL_3",
            "WARRIOR_BLADESTORM",
        ] {
            assert!(
                catalog
                    .abilities
                    .iter()
                    .all(|ability| ability.ability_id != old_ability_id),
                "{old_ability_id} should not remain as an authored ability"
            );
        }
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
    fn generic_spell_abilities_are_profile_neutral_and_author_damage_types() {
        let expected = [
            ("SPELL_FIREBALL", "FIREBALL", "FIRE"),
            ("SPELL_BOLT", "BOLT", "LIGHTNING"),
            ("SPELL_ICICLE", "ICICLE", "COLD"),
            ("SPELL_ELECTROCUTE", "ELECTROCUTE", "LIGHTNING"),
            ("SPELL_FROZEN_SPLINTERS", "FROZEN_SPLINTERS", "COLD"),
            ("SPELL_BLIZZARD", "BLIZZARD", "COLD"),
            ("SPELL_MAGIC_MISSILE", "MAGIC_MISSILE", "ARCANE"),
            ("SPELL_VAMPIRIC_ORB", "VAMPIRIC_ORB", "SHADOW"),
            ("SPELL_GRIM_WHEEL", "GRIM_WHEEL", "PHYSICAL"),
            ("SPELL_GRAVEWAKE", "GRAVEWAKE", "PHYSICAL"),
            ("SPELL_LIGHTNING", "LIGHTNING", "LIGHTNING"),
            ("SPELL_METEOR", "METEOR", "FIRE"),
            ("SPELL_NEGATE", "NEGATE", "ARCANE"),
            ("SPELL_WITHERING_ORB", "WITHERING_ORB", "SHADOW"),
            ("SPELL_NECROTIC_AURA", "NECROTIC_AURA", "NECROTIC"),
            ("SPELL_FROST_NOVA", "FROST_NOVA", "COLD"),
            ("SPELL_NOVA", "NOVA", "ARCANE"),
            ("SPELL_ICE_SPIKES", "ICE_SPIKES", "COLD"),
            ("SPELL_GLACIAL_SPIKE", "GLACIAL_SPIKE", "COLD"),
            ("SPELL_FROZEN_GRASP", "FROZEN_GRASP", "COLD"),
            ("SPELL_ERUPTION", "ERUPTION", "FIRE"),
            ("SPELL_FROST_NEEDLE", "FROST_NEEDLE", "COLD"),
            ("SPELL_INSTANT_BEAM", "INSTANT_BEAM", "ARCANE"),
            ("SPELL_ORBITING_BLADES", "ORBITING_BLADES", "LIGHTNING"),
            ("SPELL_GIGANTISM", "GIGANTISM", "PHYSICAL"),
            ("SPELL_GUST_OF_WIND", "GUST_OF_WIND", "AIR"),
            ("SPELL_BUFFET", "BUFFET", "AIR"),
            ("SPELL_VERDANT_SPIRITS", "VERDANT_SPIRITS", "AIR"),
            ("SPELL_CELESTIAL_MANTLE", "CELESTIAL_MANTLE", "HOLY"),
            ("SPELL_HOLY_SHIELD", "HOLY_SHIELD", "HOLY"),
            ("SPELL_REBUKE", "REBUKE", "HOLY"),
            ("SPELL_GLACIAL_ADVANCE", "GLACIAL_ADVANCE", "COLD"),
            ("SPELL_FLASHFIRE", "FLASHFIRE", "FIRE"),
            ("SPELL_COLLAPSE", "COLLAPSE", "ARCANE"),
            ("SPELL_SILENCE", "SILENCE", "ARCANE"),
            ("SPELL_MANA_SHIELD", "MANA_SHIELD", "ARCANE"),
            ("SPELL_SHIMMER", "SHIMMER", "ARCANE"),
        ];

        for (ability_id, action_id, damage_type) in expected {
            let ability = progression_catalog()
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("expected generic spell ability {ability_id}"));
            assert_eq!(normalize_identifier(ability.combat_profile_id.as_str()), "");
            assert_eq!(normalize_identifier(ability.action_id.as_str()), action_id);
            assert_eq!(
                ability
                    .gameplay
                    .delivery
                    .as_ref()
                    .and_then(|delivery| delivery.get("damage_type"))
                    .and_then(|value| value.as_str())
                    .map(normalize_identifier)
                    .as_deref(),
                Some(damage_type)
            );
        }

        let sparks = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_ORBITING_BLADES")
            .expect("Sparks should retain the stable SPELL_ORBITING_BLADES ability id");
        assert_eq!(sparks.display_name, "Sparks");

        assert!(progression_catalog()
            .action_presentations
            .iter()
            .any(|presentation| {
                normalize_identifier(presentation.presentation_kind.as_str()) == "ABILITY"
                    && normalize_identifier(presentation.presentation_id.as_str())
                        == "SPELL_ORBITING_BLADES"
                    && presentation.display_name == "Sparks"
            }));
    }

    #[test]
    fn existing_profile_neutral_spells_use_consolidated_disciplines() {
        let catalog = progression_catalog();
        let expected_groups = [
            (
                "ARCANA",
                &[
                    "SPELL_INSTANT_BEAM",
                    "SPELL_MAGIC_MISSILE",
                    "SPELL_NOVA",
                    "SPELL_NEGATE",
                    "SPELL_COLLAPSE",
                    "SPELL_DISPEL_MAGIC",
                    "SPELL_TELEPORT",
                    "SPELL_SILENCE",
                    "SPELL_MANA_SHIELD",
                    "SPELL_SHIMMER",
                ][..],
            ),
            (
                "BLIGHT",
                &[
                    "SPELL_VAMPIRIC_ORB",
                    "SPELL_WITHERING_ORB",
                    "SPELL_NECROTIC_AURA",
                    "SPELL_DEFILED_GROUND",
                    "SPELL_REAP",
                    "SPELL_GRIM_WHEEL",
                    "SPELL_GRAVEBURST",
                    "SPELL_GRAVEWAKE",
                    "SPELL_NECRO_PRISON",
                    "SPELL_BLOOD_OFFERING",
                ][..],
            ),
            (
                "DIVINITY",
                &[
                    "SPELL_RESTORATION",
                    "SPELL_PROTECTION",
                    "SPELL_BLINDING_LIGHT",
                    "SPELL_CELESTIAL_MANTLE",
                    "SPELL_HOLY_SHIELD",
                    "SPELL_REBUKE",
                ][..],
            ),
            (
                "PRIMAL",
                &[
                    "SPELL_GIGANTISM",
                    "SPELL_GUST_OF_WIND",
                    "SPELL_BUFFET",
                    "SPELL_STONESPIRE",
                ][..],
            ),
            (
                "RUIN",
                &[
                    "SPELL_FIREBALL",
                    "SPELL_FLAMING_ORB",
                    "SPELL_BOLT",
                    "SPELL_ICICLE",
                    "SPELL_ORBITING_BLADES",
                    "SPELL_METEOR",
                    "SPELL_LIGHTNING",
                    "SPELL_ERUPTION",
                    "SPELL_FROST_NEEDLE",
                    "SPELL_ICE_SPIKES",
                    "SPELL_ELECTROCUTE",
                    "SPELL_FROZEN_SPLINTERS",
                    "SPELL_BLIZZARD",
                    "SPELL_FROST_NOVA",
                    "SPELL_GLACIAL_SPIKE",
                    "SPELL_FROZEN_GRASP",
                    "SPELL_CAUTERIZE",
                    "SPELL_FLASHFIRE",
                    "SPELL_GLACIAL_ADVANCE",
                ][..],
            ),
        ];

        for (discipline_id, ability_ids) in expected_groups {
            for ability_id in ability_ids {
                let ability = catalog
                    .abilities
                    .iter()
                    .find(|ability| ability.ability_id == *ability_id)
                    .unwrap_or_else(|| panic!("expected profile-neutral spell {ability_id}"));
                assert_eq!(ability.actor_scope, "PLAYER");
                assert_eq!(ability_gameplay_kind(ability), "SPELL");
                assert!(ability.combat_profile_id.is_empty());
                assert_eq!(ability.discipline_id, discipline_id);
            }
        }

        let stonespire = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_STONESPIRE")
            .expect("Stonespire should remain authored");
        assert_eq!(stonespire.discipline_id, "PRIMAL");
        assert_eq!(ability_delivery_kind(stonespire), "WORLD_OBSTACLE");
        assert!(stonespire
            .gameplay
            .delivery
            .as_ref()
            .and_then(|delivery| delivery.get("damage_type"))
            .is_none());
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
        let combat_profile_id = COMBAT_PROFILE_TWO_HANDED_SWORD;

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
                combat_profile_id,
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
        let combat_profile_id = COMBAT_PROFILE_TWO_HANDED_SWORD;
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
            normalize_identifier(replacement.combat_profile_id.as_str()),
            combat_profile_id
        );
        assert!(profile_supports_action_reference(
            combat_profile_id,
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
    fn warrior_fortify_resolves_via_spell_catalog_without_default_placement() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_FORTIFY")
            .expect("expected Warrior Fortify ability");

        assert_eq!(normalize_identifier(ability.action_id.as_str()), "FORTIFY");
        assert!(spell_definition_by_str(ability.action_id.as_str()).is_some());
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                assignment.action_kind == "ABILITY" && assignment.ability_id == "WARRIOR_FORTIFY"
            }));
    }

    #[test]
    fn warrior_iron_will_resolves_via_spell_catalog_without_default_placement() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_IRON_WILL")
            .expect("expected Warrior Iron Will ability");

        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "IRON_WILL"
        );
        let definition = spell_definition_by_str(ability.action_id.as_str())
            .expect("Iron Will should resolve through the spell catalog");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::RemoveStatus
        );
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                assignment.action_kind == "ABILITY" && assignment.ability_id == "WARRIOR_IRON_WILL"
            }));
    }

    #[test]
    fn warrior_defiance_resolves_via_spell_catalog_without_default_placement() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_DEFIANCE")
            .expect("expected Warrior Defiance ability");

        assert_eq!(normalize_identifier(ability.action_id.as_str()), "DEFIANCE");
        let definition = spell_definition_by_str(ability.action_id.as_str())
            .expect("Defiance should resolve through the spell catalog");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        assert!(!definition.uses_global_cooldown);
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                assignment.action_kind == "ABILITY" && assignment.ability_id == "WARRIOR_DEFIANCE"
            }));
    }

    #[test]
    fn warrior_frenzy_authors_attack_speed_self_buff() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_FRENZY")
            .expect("expected Warrior Frenzy ability");

        assert_eq!(normalize_identifier(ability.action_id.as_str()), "FRENZY");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        let definition = spell_definition_by_str(ability.action_id.as_str())
            .expect("Frenzy should resolve through the spell catalog");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        assert!(!definition.requires_target);
        assert_eq!(definition.cast_time, Duration::from_millis(0));
        assert!((definition.duration - 8.0).abs() < 0.0001);
        assert_eq!(definition.status_stack_group.as_deref(), Some("FRENZY"));
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        let status = definition
            .apply_status
            .as_ref()
            .expect("Frenzy should apply an attack speed status");
        assert_eq!(status.kind, StatusEffectKind::AttackSpeed);
        assert_eq!(
            status.payload(),
            StatusPayload::AttackSpeed {
                modifier_scalar: 0.5,
            }
        );
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id == "WARRIOR_FRENZY"
            }));
    }

    #[test]
    fn warrior_berserking_authors_critical_defense_tradeoff_buff() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_BERSERKING")
            .expect("expected Warrior Berserking ability");

        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "BERSERKING"
        );
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        let definition = spell_definition_by_str(ability.action_id.as_str())
            .expect("Berserking should resolve through the spell catalog");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        assert!(!definition.requires_target);
        assert_eq!(definition.cast_time, Duration::from_millis(0));
        assert!((definition.duration - 10.0).abs() < 0.0001);
        assert_eq!(definition.status_stack_group.as_deref(), Some("BERSERKING"));
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        let status = definition
            .apply_status
            .as_ref()
            .expect("Berserking should apply a berserking status");
        assert_eq!(status.kind, StatusEffectKind::Berserking);
        assert_eq!(status.payload(), StatusPayload::Berserking);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation) == "ABILITY:WARRIOR_BERSERKING"
                && presentation.display_name == "Berserking"
        }));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id == "WARRIOR_BERSERKING"
            }));
    }

    #[test]
    fn warrior_battle_trance_authors_death_prevention_buff() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_BATTLE_TRANCE")
            .expect("expected Warrior Battle Trance ability");

        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "BATTLE_TRANCE"
        );
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        let definition = spell_definition_by_str(ability.action_id.as_str())
            .expect("Battle Trance should resolve through the spell catalog");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ApplyStatus
        );
        assert!(!definition.requires_target);
        assert_eq!(definition.cast_time, Duration::from_millis(0));
        assert!((definition.duration - 5.0).abs() < 0.0001);
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("BATTLE_TRANCE")
        );
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        let status = definition
            .apply_status
            .as_ref()
            .expect("Battle Trance should apply a battle trance status");
        assert_eq!(status.kind, StatusEffectKind::BattleTrance);
        assert_eq!(status.payload(), StatusPayload::BattleTrance);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation) == "ABILITY:WARRIOR_BATTLE_TRANCE"
                && presentation.display_name == "Battle Trance"
        }));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id == "WARRIOR_BATTLE_TRANCE"
            }));
    }

    #[test]
    fn warrior_feast_authors_bleed_consume_heal_spell() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_FEAST")
            .expect("expected Warrior Feast ability");

        assert_eq!(normalize_identifier(ability.action_id.as_str()), "FEAST");
        assert_eq!(ability_gameplay_kind(ability), "SPELL");
        let definition = spell_definition_by_str(ability.action_id.as_str())
            .expect("Feast should resolve through the spell catalog");
        assert_eq!(
            definition.behavior,
            crate::spells::SpellBehavior::ConsumeStatus
        );
        assert!(definition.requires_target);
        assert!((definition.max_distance - 20.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        let consume_status = definition
            .secondary
            .consume_status
            .as_ref()
            .expect("Feast should consume bleed statuses");
        assert_eq!(consume_status.max_count, 0);
        assert_eq!(
            consume_status.polarity,
            Some(crate::combat::StatusPolarity::Debuff)
        );
        assert_eq!(consume_status.dispel_types, vec![StatusDispelType::Bleed]);
        assert_eq!(consume_status.heal_per_stack, 20);
        assert!(!consume_status.deal_remaining_dot_damage);
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation) == "ABILITY:WARRIOR_FEAST"
                && presentation.display_name == "Feast"
        }));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id == "WARRIOR_FEAST"
            }));

        let animation_set_spell_ids = spell_ids_for_combat_profile(COMBAT_PROFILE_TWO_HANDED_SWORD);
        assert!(
            animation_set_spell_ids.contains("FEAST"),
            "expected Feast spell animation entry in the derived greatsword animation set"
        );
    }

    #[test]
    fn warrior_bloodlust_authors_two_handed_passive() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_BLOODLUST")
            .expect("expected Warrior Bloodlust ability");

        assert_eq!(
            normalize_identifier(ability.combat_profile_id.as_str()),
            COMBAT_PROFILE_TWO_HANDED_SWORD
        );
        assert_eq!(
            normalize_identifier(ability.action_id.as_str()),
            "BLOODLUST"
        );
        assert_eq!(ability.display_name, "Bloodlust");
        assert_eq!(ability_gameplay_kind(ability), "PASSIVE");
        assert!(ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation) == "ABILITY:WARRIOR_BLOODLUST"
                && presentation.display_name == "Bloodlust"
        }));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id == "WARRIOR_BLOODLUST"
            }));
    }

    #[test]
    fn subtlety_opportunist_authors_disabled_target_damage_passive() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == SUBTLETY_OPPORTUNIST_ABILITY_ID)
            .expect("expected Subtlety Opportunist perk");

        assert_eq!(
            normalize_identifier(ability.discipline_id.as_str()),
            "SUBTLETY"
        );
        assert_eq!(ability.display_name, "Opportunist");
        assert_eq!(ability_gameplay_kind(ability), "PASSIVE");
        assert!((ability.gameplay.disabled_target_damage_bonus - 0.15).abs() < 0.0001);
        assert!(ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation)
                == format!("ABILITY:{SUBTLETY_OPPORTUNIST_ABILITY_ID}")
                && presentation.display_name == "Opportunist"
        }));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id == SUBTLETY_OPPORTUNIST_ABILITY_ID
            }));
    }

    #[test]
    fn tactical_advantage_authors_behind_target_damage_passive() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID)
            .expect("expected Tactical Advantage passive");

        assert_eq!(
            normalize_identifier(ability.discipline_id.as_str()),
            "SUBTLETY"
        );
        assert_eq!(ability.display_name, "Tactical Advantage");
        assert_eq!(ability_gameplay_kind(ability), "PASSIVE");
        assert!((ability.gameplay.behind_target_damage_bonus - 0.15).abs() < 0.0001);
        assert!(ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation)
                == format!("ABILITY:{SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID}")
                && presentation.display_name == "Tactical Advantage"
        }));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id
                    == SUBTLETY_TACTICAL_ADVANTAGE_ABILITY_ID
            }));
    }

    #[test]
    fn fleet_footed_authors_dodge_recharge_reduction_passive() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == SUBTLETY_FLEET_FOOTED_ABILITY_ID)
            .expect("expected Fleet Footed passive");

        assert_eq!(
            normalize_identifier(ability.discipline_id.as_str()),
            "SUBTLETY"
        );
        assert_eq!(ability.display_name, "Fleet Footed");
        assert_eq!(ability_gameplay_kind(ability), "PASSIVE");
        assert!((ability.gameplay.dodge_recharge_time_reduction - 0.2).abs() < 0.0001);
        assert!(ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation)
                == format!("ABILITY:{SUBTLETY_FLEET_FOOTED_ABILITY_ID}")
                && presentation.display_name == "Fleet Footed"
        }));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id == SUBTLETY_FLEET_FOOTED_ABILITY_ID
            }));
    }

    #[test]
    fn lingering_shade_authors_three_second_movement_return_passive() {
        let catalog = progression_catalog();
        let ability = catalog
            .abilities
            .iter()
            .find(|ability| ability.ability_id == SUBTLETY_LINGERING_SHADE_ABILITY_ID)
            .expect("expected Lingering Shade passive");

        assert_eq!(
            normalize_identifier(ability.discipline_id.as_str()),
            "SUBTLETY"
        );
        assert_eq!(ability.display_name, "Lingering Shade");
        assert_eq!(ability_gameplay_kind(ability), "PASSIVE");
        assert_eq!(
            ability
                .gameplay
                .movement_return
                .as_ref()
                .map(|definition| definition.window_ms),
            Some(3_000)
        );
        assert!(ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!(catalog.action_presentations.iter().any(|presentation| {
            action_presentation_key(presentation)
                == format!("ABILITY:{SUBTLETY_LINGERING_SHADE_ABILITY_ID}")
                && presentation.display_name == "Lingering Shade"
        }));
        assert!(!catalog
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id
                    == SUBTLETY_LINGERING_SHADE_ABILITY_ID
            }));
    }

    #[test]
    fn subtlety_utility_abilities_author_requested_status_contracts() {
        let expected = [
            ("DAGGER_FIND_WEAKNESS", "FIND_WEAKNESS", 86_400_000_u64),
            ("DAGGER_BLADE_TWISTING", "BLADE_TWISTING", 86_400_000_u64),
            ("DAGGER_DISARM", "DISARM", 4_000_u64),
            ("DAGGER_GOUGE", "GOUGE", 4_000_u64),
        ];
        for (ability_id, status_kind, duration_ms) in expected {
            let ability = progression_catalog()
                .abilities
                .iter()
                .find(|ability| ability.ability_id == ability_id)
                .unwrap_or_else(|| panic!("expected {ability_id}"));
            assert_eq!(ability_gameplay_kind(ability), "SPELL");
            assert_eq!(ability.combat_profile_id, COMBAT_PROFILE_DAGGERS);
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

        let animation_ids = parse_spell_ids_from_animation_set_asset(
            animation_set_asset_for_combat_profile(COMBAT_PROFILE_DAGGERS),
        );
        for spell_id in ["FIND_WEAKNESS", "BLADE_TWISTING", "DISARM", "GOUGE"] {
            assert!(
                animation_ids.contains(spell_id),
                "Dagger animation set must map {spell_id}"
            );
        }
    }

    #[test]
    fn surprise_attacks_authors_two_second_subtlety_passive() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID)
            .expect("expected Surprise Attacks passive");
        assert_eq!(ability_gameplay_kind(ability), "PASSIVE");
        assert_eq!(ability.gameplay.stealth_attack_stun_ms, 2_000);
        assert!(ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "PASSIVE"));
        assert!(!progression_catalog()
            .combat_profile_action_bar_defaults
            .iter()
            .any(|assignment| {
                action_ref_for_action_bar_default(assignment).id
                    == SUBTLETY_SURPRISE_ATTACKS_ABILITY_ID
            }));
    }

    #[test]
    fn arcana_recall_authors_the_store_and_replay_contract() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "SPELL_RECALL")
            .expect("expected Arcana Recall spell");
        assert_eq!(normalize_identifier(&ability.discipline_id), "ARCANA");
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
        assert_eq!(normalize_identifier(&ability.discipline_id), "ARCANA");
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
    fn discipline_perks_apply_from_primary_or_either_secondary_slot() {
        for (primary, secondary_1, secondary_2) in [
            ("SUBTLETY", "WAR", "RUIN"),
            ("WAR", "SUBTLETY", "RUIN"),
            ("WAR", "RUIN", "SUBTLETY"),
        ] {
            let loadout = CharacterDisciplineLoadout {
                owner: Identity::ZERO,
                primary_discipline_id: primary.to_string(),
                secondary_discipline_id_1: secondary_1.to_string(),
                secondary_discipline_id_2: secondary_2.to_string(),
                updated_at: Timestamp::UNIX_EPOCH,
            };
            assert!(character_discipline_loadout_contains(&loadout, "SUBTLETY"));
        }

        let loadout = CharacterDisciplineLoadout {
            owner: Identity::ZERO,
            primary_discipline_id: "WAR".to_string(),
            secondary_discipline_id_1: "RUIN".to_string(),
            secondary_discipline_id_2: "ZEAL".to_string(),
            updated_at: Timestamp::UNIX_EPOCH,
        };
        assert!(!character_discipline_loadout_contains(&loadout, "SUBTLETY"));
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
    fn default_action_bar_assignments_do_not_place_spells() {
        let catalog = progression_catalog();
        let intentional_spell_defaults: HashSet<&str> = [
            "PALADIN_FERVOR",
            "PALADIN_MANA_FONT",
            "PALADIN_STAMINA_FONT",
            "PALADIN_THORNS_AURA",
            "PALADIN_WARDING_AURA",
            "PALADIN_AURA_OF_VENGEANCE",
            "PALADIN_BLESSED_SHIELD",
            "PALADIN_BLADE_BARRIER",
        ]
        .into_iter()
        .collect();

        for assignment in &catalog.combat_profile_action_bar_defaults {
            let action_ref = action_ref_for_action_bar_default(assignment);
            if !action_ref.is_ability() {
                continue;
            }

            let ability = catalog
                .abilities
                .iter()
                .find(|ability| normalize_identifier(ability.ability_id.as_str()) == action_ref.id)
                .expect("action-bar default ability must exist");
            if intentional_spell_defaults.contains(action_ref.id.as_str()) {
                assert_eq!(
                    normalize_identifier(assignment.combat_profile_id.as_str()),
                    COMBAT_PROFILE_SWORD_AND_SHIELD,
                    "intentional spell default '{}' must stay scoped to SwordAndShield",
                    action_ref.id
                );
                continue;
            }
            assert_ne!(
                ability_gameplay_kind(ability),
                "SPELL",
                "default action-bar assignment '{}' for combat profile '{}' should not place learned spells",
                action_ref.id,
                assignment.combat_profile_id
            );
        }
    }

    #[test]
    fn fixed_actions_are_presentations_not_gear_abilities() {
        let catalog = progression_catalog();

        for fixed_action_id in ["DODGE", "PARRY"] {
            assert!(
                catalog.action_presentations.iter().any(|presentation| {
                    normalize_identifier(presentation.presentation_kind.as_str())
                        == ACTION_KIND_FIXED
                        && normalize_identifier(presentation.presentation_id.as_str())
                            == fixed_action_id
                }),
                "fixed action '{}' must have a FIXED presentation row",
                fixed_action_id
            );
            assert!(
                catalog.abilities.iter().all(|ability| {
                    normalize_identifier(ability.ability_id.as_str()) != fixed_action_id
                        && normalize_identifier(ability.action_id.as_str()) != fixed_action_id
                }),
                "fixed action '{}' must not be authored as a gear ability",
                fixed_action_id
            );
        }
        for fixed_action_id in ["DODGE", "PARRY"] {
            assert!(
                catalog.combat_profile_action_bar_defaults.iter().all(|assignment| {
                    action_ref_for_action_bar_default(assignment).id != fixed_action_id
                }),
                "{fixed_action_id} is a generic keybind and must not be assigned to action-bar defaults"
            );
        }
    }

    #[test]
    fn generic_fixed_action_bar_assignment_cleanup_matches_dodge_and_parry() {
        fn assignment(
            action_kind: &str,
            action_id: &str,
            ability_id: &str,
        ) -> CharacterActionBarAssignment {
            CharacterActionBarAssignment {
                key: "test-key".to_string(),
                owner: spacetimedb::Identity::ZERO,
                combat_profile_id: COMBAT_PROFILE_TWO_HANDED_SWORD.to_string(),
                slot_id: "SLOT_1_0".to_string(),
                action_kind: action_kind.to_string(),
                action_id: action_id.to_string(),
                ability_id: ability_id.to_string(),
                updated_at: spacetimedb::Timestamp::UNIX_EPOCH,
            }
        }

        assert!(character_action_bar_assignment_is_generic_fixed_action(
            &assignment(ACTION_KIND_FIXED, "DODGE", "")
        ));
        assert!(character_action_bar_assignment_is_generic_fixed_action(
            &assignment(ACTION_KIND_FIXED, "PARRY", "")
        ));
        assert!(character_action_bar_assignment_is_generic_fixed_action(
            &assignment("", "", "DODGE")
        ));
        assert!(!character_action_bar_assignment_is_generic_fixed_action(
            &assignment("ABILITY", "WARRIOR_HEW", "WARRIOR_HEW")
        ));
    }

    #[test]
    fn warrior_self_buffs_have_greatsword_spell_animation_entries() {
        let animation_set_spell_ids = spell_ids_for_combat_profile(COMBAT_PROFILE_TWO_HANDED_SWORD);

        assert!(
            animation_set_spell_ids.contains("MOMENTUM"),
            "expected Momentum spell animation entry in the derived greatsword animation set"
        );
        assert!(
            animation_set_spell_ids.contains("BATTLE_CRY"),
            "expected Battle Cry spell animation entry in the derived greatsword animation set"
        );
        assert!(
            animation_set_spell_ids.contains("FORTIFY"),
            "expected Fortify spell animation entry in the derived greatsword animation set"
        );
        assert!(
            animation_set_spell_ids.contains("IRON_WILL"),
            "expected Iron Will spell animation entry in the derived greatsword animation set"
        );
        assert!(
            animation_set_spell_ids.contains("DEFIANCE"),
            "expected Defiance spell animation entry in the derived greatsword animation set"
        );
        assert!(
            animation_set_spell_ids.contains("FRENZY"),
            "expected Frenzy spell animation entry in the derived greatsword animation set"
        );
        assert!(
            animation_set_spell_ids.contains("SECOND_WIND"),
            "expected Second Wind spell animation entry in the derived greatsword animation set"
        );
        assert!(
            animation_set_spell_ids.contains("BERSERKING"),
            "expected Berserking spell animation entry in the derived greatsword animation set"
        );
        assert!(
            animation_set_spell_ids.contains("BATTLE_TRANCE"),
            "expected Battle Trance spell animation entry in the derived greatsword animation set"
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

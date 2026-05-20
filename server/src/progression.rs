use std::collections::{HashMap, HashSet};
use std::sync::OnceLock;
use std::time::Duration;

use serde::de::{Error as DeError, IgnoredAny, MapAccess, Visitor};
use serde::{Deserialize, Deserializer};
use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::action_ids::{normalize_authored_action_id, AuthoredActionId};
use crate::appearance::sync_character_appearance_outfit_for_class;
use crate::combat::{
    AuthoredStatusPayload, StackPolicy, StatusApplication, StatusEffectKind,
    StatusStackGroupDefault,
};
use crate::melee::sync_melee_attack_modifier_catalog;
use crate::player::Player;
use crate::relations::TARGET_AUDIENCE_HOSTILE;
use crate::resources::sync_primary_resource_for_player;

#[allow(unused_imports)]
use crate::player::player as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::progression::ability_catalog as _;
#[allow(unused_imports)]
use crate::progression::action_presentation_catalog as _;
#[allow(unused_imports)]
use crate::progression::active_combat_mode as _;
#[allow(unused_imports)]
use crate::progression::auto_attack_catalog as _;
#[allow(unused_imports)]
use crate::progression::character_class_loadout_state as _;
#[allow(unused_imports)]
use crate::progression::character_progression as _;
#[allow(unused_imports)]
use crate::progression::class_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_mode_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_profile_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_rule_catalog as _;
#[allow(unused_imports)]
use crate::progression::combat_vfx_cue_catalog as _;
#[allow(unused_imports)]
use crate::progression::fixed_action_binding_catalog as _;
#[allow(unused_imports)]
use crate::progression::loadout_slot_catalog as _;
#[allow(unused_imports)]
use crate::progression::melee_ability_catalog as _;
#[allow(unused_imports)]
use crate::progression::melee_gap_close_catalog as _;
#[allow(unused_imports)]
use crate::progression::resource_catalog as _;
#[allow(unused_imports)]
use crate::progression::saved_spec as _;
#[allow(unused_imports)]
use crate::progression::saved_spec_slot_assignment as _;
#[allow(unused_imports)]
use crate::progression::saved_spec_stat_allocation as _;
#[allow(unused_imports)]
use crate::progression::stat_scaling_catalog as _;

const PROGRESSION_CATALOG_JSON: &str = include_str!("progression_catalog.shared.json");
const DEFAULT_SPEC_NAME: &str = "Default";
const DEFAULT_SPEC_VERSION: u32 = 1;
const MAX_SPEC_NAME_LEN: usize = 32;

const STAT_KIND_MIGHT: &str = "MIGHT";
const STAT_KIND_INSIGHT: &str = "INSIGHT";
const STAT_KIND_FINESSE: &str = "FINESSE";
const STAT_KIND_QUICKNESS: &str = "QUICKNESS";
const STAT_KIND_FORTITUDE: &str = "FORTITUDE";
const ACTION_KIND_ABILITY: &str = "ABILITY";
const ACTION_KIND_FIXED: &str = "FIXED";
const FIXED_ACTION_DODGE: &str = "DODGE";
const FIXED_ACTION_PARRY: &str = "PARRY";
pub(crate) const COMBAT_PROFILE_ARCHER_BOW: &str = "ARCHER_BOW";
pub(crate) const COMBAT_MODE_SHORT_DRAW: &str = "SHORT_DRAW";
pub(crate) const COMBAT_MODE_FULL_DRAW: &str = "FULL_DRAW";
pub(crate) const AUTO_ATTACK_MOVEMENT_ALLOW_MOVING: &str = "ALLOW_MOVING";
pub(crate) const AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE: &str =
    "RESET_CADENCE_ON_VOLUNTARY_MOVE";
#[cfg(test)]
const ABILITY_KIND_COMBAT_MODE_TOGGLE: &str = "COMBAT_MODE_TOGGLE";
#[cfg(test)]
const ARCHER_DRAW_MODE_TOGGLE_ABILITY_ID: &str = "ARCHER_DRAW_MODE_TOGGLE";

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) enum StatKind {
    Might,
    Insight,
    Finesse,
    Quickness,
    Fortitude,
}

impl StatKind {
    pub(crate) const ALL: [Self; 5] = [
        Self::Might,
        Self::Insight,
        Self::Finesse,
        Self::Quickness,
        Self::Fortitude,
    ];

    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::Might => STAT_KIND_MIGHT,
            Self::Insight => STAT_KIND_INSIGHT,
            Self::Finesse => STAT_KIND_FINESSE,
            Self::Quickness => STAT_KIND_QUICKNESS,
            Self::Fortitude => STAT_KIND_FORTITUDE,
        }
    }

    fn from_wire(value: &str) -> Option<Self> {
        match normalize_identifier(value).as_str() {
            STAT_KIND_MIGHT => Some(Self::Might),
            STAT_KIND_INSIGHT => Some(Self::Insight),
            STAT_KIND_FINESSE => Some(Self::Finesse),
            STAT_KIND_QUICKNESS => Some(Self::Quickness),
            STAT_KIND_FORTITUDE => Some(Self::Fortitude),
            _ => None,
        }
    }
}

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
    combat_modes: Vec<CombatModeDefinition>,
    #[serde(default)]
    classes: Vec<ClassDefinition>,
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
    #[serde(default)]
    fixed_action_bindings: Vec<FixedActionBindingDefinition>,
    #[serde(default)]
    action_presentations: Vec<ActionPresentationDefinition>,
    #[serde(default)]
    combat_vfx_cues: Vec<CombatVfxCueDefinition>,
    #[serde(default)]
    default_loadout_assignments: Vec<DefaultLoadoutAssignmentDefinition>,
    #[serde(default)]
    slots: Vec<LoadoutSlotDefinition>,
}

#[derive(Clone, Deserialize)]
struct CombatProfileDefinition {
    combat_profile_id: String,
    display_name: String,
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
struct ClassDefinition {
    class_id: String,
    display_name: String,
    default_combat_profile_id: String,
    primary_resource_kind: String,
    max_saved_specs: u32,
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
    class_id: String,
    gameplay: AbilityGameplayDefinition,
    action_id: String,
    display_name: String,
    #[serde(default)]
    fixed_action_id: String,
    #[serde(default)]
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
    applies_stagger: Option<bool>,
    range: Option<f32>,
    cooldown_ms: Option<u64>,
    uses_global_cooldown: Option<bool>,
    parry_behavior: Option<String>,
    block_behavior: Option<String>,
    airborne_targeting_mode: Option<String>,
    #[serde(default)]
    gap_close: Option<GapCloseDefinition>,
    #[serde(default)]
    melee_timed_movement: Option<MeleeTimedMovementDefinition>,
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
// "Movement delivery" is the runtime domain name. The authored JSON field is
// gameplay.delivery on MOVEMENT abilities, not a separate movement_delivery key.
struct MovementDeliveryDefinition {
    kind: String,
    cooldown_ms: u64,
    uses_global_cooldown: bool,
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
}

#[derive(Clone, Deserialize)]
#[serde(tag = "kind", rename_all = "SCREAMING_SNAKE_CASE", deny_unknown_fields)]
enum MeleeImpactEffectDefinition {
    ApplyStatus { status: MeleeImpactStatusDefinition },
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
        status.tick_heal,
        status.tick_interval_ms,
        status.modifier_scalar,
        status.absorb_amount,
        status.absorb_cap,
        status.max_stacks,
        status.stack_policy,
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
        status.tick_heal,
        status.tick_interval_ms,
        status.modifier_scalar,
        status.absorb_amount,
        status.absorb_cap,
        status.max_stacks,
        status.stack_policy,
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
    tick_heal: i32,
    tick_interval_ms: u64,
    modifier_scalar: f32,
    absorb_amount: i32,
    absorb_cap: i32,
    max_stacks: u32,
    stack_policy: StackPolicy,
    default: StatusStackGroupDefault,
) -> StatusApplication {
    let normalized = normalize_identifier(kind);
    let kind = StatusEffectKind::from_wire(normalized.as_str())
        .unwrap_or_else(|| panic!("unknown status kind '{kind}'"));
    let payload = AuthoredStatusPayload::new_with_absorb(
        kind,
        slow_pct,
        tick_damage,
        tick_heal,
        tick_interval_ms,
        modifier_scalar,
        absorb_amount,
        absorb_cap,
    )
    .payload();
    StatusApplication::new(
        payload,
        Duration::from_millis(duration_ms),
        status_stack_group,
        default,
        max_stacks,
        stack_policy,
    )
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
        "MOVE_SLOW_IMMUNITY" => StatusStackGroupDefault::ActionSuffix("MOVE_SLOW_IMMUNITY"),
        "DAMAGE_AMP" => StatusStackGroupDefault::ActionSuffix("DAMAGE_AMP"),
        "DIRECT_DAMAGE_AMP" => StatusStackGroupDefault::ActionSuffix("DIRECT_DAMAGE_AMP"),
        "HEALING_TAKEN_REDUCTION" => {
            StatusStackGroupDefault::ActionSuffix("HEALING_TAKEN_REDUCTION")
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
    pub cast_time_ms: u64,
    pub cast_mobility: String,
    pub targeting: String,
    pub target_audience: String,
    pub requires_target: bool,
    pub resource_cost: f32,
    pub arms_auto_attack_on_cast: bool,
    pub speed: f32,
    pub max_distance: f32,
    pub damage: i32,
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
pub(crate) struct MeleeImpactEffectRuntime {
    pub status: StatusApplication,
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
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct AutoAttackDefinition {
    combat_profile_id: String,
    #[serde(default)]
    mode_id: String,
    action_id: String,
    base_damage: i32,
    range: f32,
    cooldown_ms: u64,
    #[serde(default = "default_auto_attack_movement_policy")]
    movement_policy: String,
    uses_global_cooldown: bool,
    parry_behavior: String,
    block_behavior: String,
    airborne_targeting_mode: String,
    applies_stagger: bool,
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
    range: f32,
    cooldown_ms: u64,
    uses_global_cooldown: bool,
    parry_behavior: String,
    block_behavior: String,
    airborne_targeting_mode: String,
    applies_stagger: bool,
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
struct FixedActionBindingDefinition {
    class_id: String,
    fixed_action_id: String,
    ability_id: String,
    sort_order: u32,
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
            if normalize_identifier(cue.trigger.as_str()) != "SPELL_RELEASE"
                || normalize_identifier(cue.vfx_role.as_str()) != "PROJECTILE_BODY"
            {
                continue;
            }

            let owner_kind = normalize_identifier(cue.owner_kind.as_str());
            let owner_id = normalize_identifier(cue.owner_id.as_str());
            let sequence_index = cue.projectile_sequence_index.unwrap_or(0);
            let vfx_id = normalize_identifier(cue.vfx_id.as_str());
            match owner_kind.as_str() {
                "ABILITY" => Self::insert_projectile_body_selection(
                    &mut manifest.projectile_body_by_ability,
                    owner_id,
                    sequence_index,
                    vfx_id,
                    cue.sort_order,
                ),
                "SPELL" => Self::insert_projectile_body_selection(
                    &mut manifest.projectile_body_by_spell,
                    owner_id,
                    sequence_index,
                    vfx_id,
                    cue.sort_order,
                ),
                _ => {}
            }
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
struct DefaultLoadoutAssignmentDefinition {
    class_id: String,
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
struct LoadoutSlotDefinition {
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
        .default_loadout_assignments
        .iter()
        .filter_map(|assignment| {
            let action_ref = action_ref_for_default_assignment(assignment);
            action_ref.is_ability().then_some(action_ref.id)
        })
        .collect();
    for ability in &catalog.abilities {
        let ability_id = normalize_identifier(ability.ability_id.as_str());
        let is_core = ability
            .ability_tags
            .iter()
            .any(|tag| normalize_identifier(tag.as_str()) == "CORE_ABILITY");
        assert!(
            !is_core || default_ability_assignments.contains(ability_id.as_str()),
            "core ability '{}' must have a default loadout assignment",
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
                MeleeImpactEffectDefinition::ApplyStatus { status } => {
                    collect_optional_status_stack_group(
                        status.status_stack_group.as_deref(),
                        &mut ids,
                    );
                }
            }
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
        StatusEffectKind::Stagger,
        StatusEffectKind::Knockdown,
        StatusEffectKind::Slow,
        StatusEffectKind::Dot,
        StatusEffectKind::Hot,
        StatusEffectKind::MoveSlowImmunity,
        StatusEffectKind::DamageAmp,
        StatusEffectKind::DirectDamageAmp,
        StatusEffectKind::HealingTakenReduction,
        StatusEffectKind::MeleeAttackModifier,
        StatusEffectKind::AttackSpeed,
        StatusEffectKind::CastSpeed,
        StatusEffectKind::TemporaryHitpoints,
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

#[table(accessor = class_catalog, public)]
pub struct ClassCatalog {
    #[primary_key]
    pub class_id: String,
    pub display_name: String,
    pub default_combat_profile_id: String,
    pub primary_resource_kind: String,
    pub max_saved_specs: u32,
    pub sort_order: u32,
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

#[table(accessor = ability_catalog, public)]
pub struct AbilityCatalog {
    #[primary_key]
    pub ability_id: String,
    pub class_id: String,
    pub ability_kind: String,
    pub action_id: String,
    pub display_name: String,
    pub fixed_action_id: String,
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
    pub applies_stagger: bool,
    pub range: f32,
    pub cooldown_ms: u64,
    pub uses_global_cooldown: bool,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub airborne_targeting_mode: String,
    pub targeting_kind: String,
    pub target_audience: String,
    pub requires_target: bool,
    pub targeting_radius: f32,
    pub targeting_range: f32,
    pub targeting_angle_degrees: f32,
    pub impact_area_radius: f32,
    pub impact_area_damage_multiplier: f32,
    pub impact_area_hit_index: i32,
    pub impact_area_include_primary_target: bool,
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
    pub combat_profile_id: String,
    pub mode_id: String,
    pub action_id: String,
    pub base_damage: i32,
    pub range: f32,
    pub cooldown_ms: u64,
    pub movement_policy: String,
    pub uses_global_cooldown: bool,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub airborne_targeting_mode: String,
    pub applies_stagger: bool,
}

#[table(accessor = auto_attack_replacement_catalog)]
pub struct AutoAttackReplacementCatalog {
    #[primary_key]
    pub replacement_id: String,
    pub combat_profile_id: String,
    pub authored_melee_strike_id: String,
    pub base_damage: i32,
    pub range: f32,
    pub cooldown_ms: u64,
    pub uses_global_cooldown: bool,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub airborne_targeting_mode: String,
    pub applies_stagger: bool,
    pub grants_primary_resource_on_hit: bool,
    pub expires_ms: u64,
    pub sort_order: u32,
}

#[table(accessor = fixed_action_binding_catalog, public)]
pub struct FixedActionBindingCatalog {
    #[primary_key]
    pub key: String,
    pub class_id: String,
    pub fixed_action_id: String,
    pub ability_id: String,
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

#[table(accessor = loadout_slot_catalog, public)]
pub struct LoadoutSlotCatalog {
    #[primary_key]
    pub slot_id: String,
    pub ui_row: u32,
    pub ui_col: u32,
    pub slot_group: String,
    pub accepts_tags: String,
    pub sort_order: u32,
}

#[table(accessor = character_progression, public)]
pub struct CharacterProgression {
    #[primary_key]
    pub owner: Identity,
    pub class_id: String,
}

#[table(accessor = character_class_loadout_state, public)]
pub struct CharacterClassLoadoutState {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub class_id: String,
    pub active_spec_id: String,
    pub updated_at: Timestamp,
}

#[table(accessor = saved_spec, public)]
pub struct SavedSpec {
    #[primary_key]
    pub spec_id: String,
    #[index(btree)]
    pub owner: Identity,
    pub name: String,
    pub class_id: String,
    pub version: u32,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
}

#[table(accessor = saved_spec_stat_allocation, public)]
pub struct SavedSpecStatAllocation {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub spec_id: String,
    pub stat_kind: String,
    pub allocated_points: u32,
}

#[table(accessor = saved_spec_slot_assignment, public)]
pub struct SavedSpecSlotAssignment {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub spec_id: String,
    pub slot_id: String,
    pub action_kind: String,
    pub action_id: String,
    pub ability_id: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
enum ActionKind {
    Ability,
    Fixed,
    Unsupported(String),
}

impl ActionKind {
    fn from_wire(value: &str) -> Self {
        let normalized = normalize_identifier(value);
        match normalized.as_str() {
            ACTION_KIND_ABILITY => Self::Ability,
            ACTION_KIND_FIXED => Self::Fixed,
            _ => Self::Unsupported(normalized),
        }
    }

    fn as_wire(&self) -> &str {
        match self {
            Self::Ability => ACTION_KIND_ABILITY,
            Self::Fixed => ACTION_KIND_FIXED,
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

    #[cfg(test)]
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
pub fn create_saved_spec(ctx: &ReducerContext, name: String) -> Result<(), String> {
    let owner = ctx.sender();
    let now = ctx.timestamp;
    let progression = require_character_progression(ctx, owner)?;
    let class = require_class_catalog_row(ctx, progression.class_id.as_str())?;
    let existing_specs = saved_specs_for_owner_and_class(ctx, owner, progression.class_id.as_str());
    if existing_specs.len() as u32 >= class.max_saved_specs {
        return Err(format!(
            "saved spec limit reached for class '{}'",
            class.class_id
        ));
    }

    let normalized_name = validate_spec_name(name.as_str())?;
    let spec_id = next_saved_spec_id(ctx, owner, progression.class_id.as_str(), now);
    let normalized_class_id = normalize_identifier(progression.class_id.as_str());
    ctx.db.saved_spec().insert(SavedSpec {
        spec_id: spec_id.clone(),
        owner,
        name: normalized_name,
        class_id: normalized_class_id.clone(),
        version: DEFAULT_SPEC_VERSION,
        created_at: now,
        updated_at: now,
    });

    for stat_kind in StatKind::ALL {
        ctx.db
            .saved_spec_stat_allocation()
            .insert(SavedSpecStatAllocation {
                key: saved_spec_stat_key(spec_id.as_str(), stat_kind),
                spec_id: spec_id.clone(),
                stat_kind: stat_kind.as_str().to_string(),
                allocated_points: 0,
            });
    }

    backfill_missing_default_slot_assignments(ctx, normalized_class_id.as_str(), spec_id.as_str());

    Ok(())
}

#[reducer]
pub fn rename_saved_spec(
    ctx: &ReducerContext,
    spec_id: String,
    name: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let mut spec = require_owned_spec(ctx, owner, spec_id.as_str())?;
    spec.name = validate_spec_name(name.as_str())?;
    spec.updated_at = ctx.timestamp;
    ctx.db.saved_spec().spec_id().update(spec);
    Ok(())
}

#[reducer]
pub fn delete_saved_spec(ctx: &ReducerContext, spec_id: String) -> Result<(), String> {
    let owner = ctx.sender();
    let spec = require_owned_spec(ctx, owner, spec_id.as_str())?;
    let existing_specs = saved_specs_for_owner_and_class(ctx, owner, spec.class_id.as_str());
    if existing_specs.len() <= 1 {
        return Err(format!(
            "cannot delete the last saved spec for class '{}'",
            spec.class_id
        ));
    }
    if class_loadout_state_uses_spec(ctx, owner, spec.class_id.as_str(), spec.spec_id.as_str()) {
        return Err("cannot delete the active spec".to_string());
    }

    delete_saved_spec_rows(ctx, spec.spec_id.as_str());
    Ok(())
}

#[reducer]
pub fn set_saved_spec_stat_allocation(
    ctx: &ReducerContext,
    spec_id: String,
    stat_kind: String,
    allocated_points: u32,
) -> Result<(), String> {
    let owner = ctx.sender();
    let mut spec = require_owned_spec(ctx, owner, spec_id.as_str())?;
    let parsed_stat_kind = StatKind::from_wire(stat_kind.as_str())
        .ok_or_else(|| format!("unknown stat kind '{}'", stat_kind.trim()))?;

    upsert_stat_allocation(
        ctx,
        spec.spec_id.as_str(),
        parsed_stat_kind,
        allocated_points,
    );
    spec.updated_at = ctx.timestamp;
    ctx.db.saved_spec().spec_id().update(spec);
    Ok(())
}

#[reducer]
pub fn assign_saved_spec_ability_to_slot(
    ctx: &ReducerContext,
    spec_id: String,
    slot_id: String,
    ability_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let mut spec = require_owned_spec(ctx, owner, spec_id.as_str())?;
    let normalized_slot_id = canonical_loadout_slot_id(slot_id.as_str());
    let normalized_ability_id = normalize_identifier(ability_id.as_str());
    let action_ref = ActionRef::ability(normalized_ability_id.as_str());
    validate_slot_action_ref(
        ctx,
        spec.class_id.as_str(),
        normalized_slot_id.as_str(),
        &action_ref,
    )?;

    upsert_slot_assignment(
        ctx,
        spec.spec_id.as_str(),
        normalized_slot_id.as_str(),
        &action_ref,
    );
    spec.updated_at = ctx.timestamp;
    ctx.db.saved_spec().spec_id().update(spec);
    Ok(())
}

#[reducer]
pub fn assign_saved_spec_action_to_slot(
    ctx: &ReducerContext,
    spec_id: String,
    slot_id: String,
    action_kind: String,
    action_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let mut spec = require_owned_spec(ctx, owner, spec_id.as_str())?;
    let normalized_slot_id = canonical_loadout_slot_id(slot_id.as_str());
    let action_ref = ActionRef::from_wire(action_kind.as_str(), action_id.as_str());
    validate_slot_action_ref(
        ctx,
        spec.class_id.as_str(),
        normalized_slot_id.as_str(),
        &action_ref,
    )?;

    upsert_slot_assignment(
        ctx,
        spec.spec_id.as_str(),
        normalized_slot_id.as_str(),
        &action_ref,
    );
    spec.updated_at = ctx.timestamp;
    ctx.db.saved_spec().spec_id().update(spec);
    Ok(())
}

#[reducer]
pub fn clear_saved_spec_slot(
    ctx: &ReducerContext,
    spec_id: String,
    slot_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let mut spec = require_owned_spec(ctx, owner, spec_id.as_str())?;
    let normalized_slot_id = canonical_loadout_slot_id(slot_id.as_str());
    require_slot_catalog_row(ctx, normalized_slot_id.as_str())?;
    let key = saved_spec_slot_key(spec.spec_id.as_str(), normalized_slot_id.as_str());
    if ctx
        .db
        .saved_spec_slot_assignment()
        .key()
        .find(key.clone())
        .is_some()
    {
        ctx.db.saved_spec_slot_assignment().key().delete(key);
    }
    spec.updated_at = ctx.timestamp;
    ctx.db.saved_spec().spec_id().update(spec);
    Ok(())
}

#[reducer]
pub fn activate_saved_spec(ctx: &ReducerContext, spec_id: String) -> Result<(), String> {
    let owner = ctx.sender();
    let spec = require_owned_spec(ctx, owner, spec_id.as_str())?;
    let progression = require_character_progression(ctx, owner)?;
    if spec.class_id != progression.class_id {
        return Err(format!(
            "saved spec class '{}' does not match character class '{}'",
            spec.class_id, progression.class_id
        ));
    }
    let class_id = spec.class_id.clone();
    let active_spec_id = spec.spec_id.clone();
    upsert_class_loadout_state(
        ctx,
        owner,
        class_id.as_str(),
        active_spec_id.as_str(),
        ctx.timestamp,
    );
    Ok(())
}

#[reducer]
pub fn switch_loadout_class(ctx: &ReducerContext, class_id: String) -> Result<(), String> {
    let owner = ctx.sender();
    let normalized_class_id = canonical_class_id(class_id.as_str());
    let class = class_definition(normalized_class_id.as_str())
        .ok_or_else(|| format!("unknown class '{}'", normalized_class_id))?;
    let mut progression = require_character_progression(ctx, owner)?;

    ensure_class_loadout_state(ctx, owner, class, ctx.timestamp);
    progression.class_id = normalized_class_id.clone();
    ctx.db.character_progression().owner().update(progression);

    if let Some(mut player) = ctx.db.player().identity().find(owner) {
        if player.class_id != normalized_class_id {
            player.class_id = normalized_class_id.clone();
            ctx.db.player().identity().update(player);
        }
    }

    sync_character_appearance_outfit_for_class(ctx, owner, normalized_class_id.as_str())?;
    normalize_active_combat_mode_for_profile(
        ctx,
        owner,
        normalize_identifier(class.default_combat_profile_id.as_str()).as_str(),
        ctx.timestamp,
    );
    sync_primary_resource_for_player(ctx, owner, ctx.timestamp);
    Ok(())
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

pub(crate) fn sync_progression_catalogs(ctx: &ReducerContext) {
    sync_combat_profile_catalog(ctx);
    sync_combat_mode_catalog(ctx);
    sync_class_catalog(ctx);
    sync_resource_catalog(ctx);
    sync_combat_rule_catalog(ctx);
    sync_stat_scaling_catalog(ctx);
    sync_ability_catalog(ctx);
    sync_melee_ability_catalog(ctx);
    sync_melee_gap_close_catalog(ctx);
    sync_melee_attack_modifier_catalog(ctx);
    sync_auto_attack_catalog(ctx);
    sync_auto_attack_replacement_catalog(ctx);
    sync_fixed_action_binding_catalog(ctx);
    sync_action_presentation_catalog(ctx);
    sync_combat_vfx_cue_catalog(ctx);
    sync_loadout_slot_catalog(ctx);
    repair_legacy_class_rows(ctx);
    repair_saved_spec_rows(ctx, ctx.timestamp);
    repair_class_loadout_state_rows(ctx, ctx.timestamp);
}

pub(crate) fn backfill_character_progression_rows(ctx: &ReducerContext) -> usize {
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
        let had_progression = ctx
            .db
            .character_progression()
            .owner()
            .find(player.identity)
            .is_some();
        if ensure_default_progression_for_identity(ctx, player.identity).is_ok() && !had_progression
        {
            repaired += 1;
        }
    }

    repaired
}

pub(crate) fn ensure_default_progression_for_identity(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<(), String> {
    let Some(mut player) = ctx.db.player().identity().find(owner) else {
        return Err("player row not found".to_string());
    };
    let raw_fallback_class_id = if player.class_id.trim().is_empty() {
        default_class_id()
    } else {
        player.class_id.clone()
    };
    let fallback_class_id = canonical_class_id(raw_fallback_class_id.as_str());

    if let Some(mut progression) = ctx.db.character_progression().owner().find(owner) {
        let progression_class_id = canonical_class_id(progression.class_id.as_str());
        let class_id = if class_definition(progression_class_id.as_str()).is_some() {
            progression_class_id
        } else {
            fallback_class_id
        };
        let class = class_definition(class_id.as_str())
            .ok_or_else(|| format!("unknown class '{}'", class_id))?;
        ensure_class_loadout_state(ctx, owner, class, ctx.timestamp);
        let mut dirty = false;
        if progression.class_id != class_id {
            progression.class_id = class_id.clone();
            dirty = true;
        }
        if dirty {
            ctx.db.character_progression().owner().update(progression);
        }

        if player.class_id != class_id {
            player.class_id = class_id;
            ctx.db.player().identity().update(player);
        }
        normalize_active_combat_mode_for_profile(
            ctx,
            owner,
            normalize_identifier(class.default_combat_profile_id.as_str()).as_str(),
            ctx.timestamp,
        );
    } else {
        let class = class_definition(fallback_class_id.as_str())
            .ok_or_else(|| format!("unknown class '{}'", fallback_class_id))?;
        ensure_class_loadout_state(ctx, owner, class, ctx.timestamp);
        if player.class_id != fallback_class_id {
            player.class_id = fallback_class_id.clone();
            ctx.db.player().identity().update(player);
        }
        ctx.db.character_progression().insert(CharacterProgression {
            owner,
            class_id: fallback_class_id,
        });
        normalize_active_combat_mode_for_profile(
            ctx,
            owner,
            normalize_identifier(class.default_combat_profile_id.as_str()).as_str(),
            ctx.timestamp,
        );
    }

    Ok(())
}

pub(crate) fn default_class_id() -> String {
    "WARRIOR".to_string()
}

pub(crate) fn derived_combat_profile_id_for_class(class_id: &str) -> Option<String> {
    let class = class_definition(class_id)?;
    Some(normalize_identifier(
        class.default_combat_profile_id.as_str(),
    ))
}

pub(crate) fn runtime_class_id_for_owner(ctx: &ReducerContext, owner: Identity) -> Option<String> {
    if let Some(progression) = ctx.db.character_progression().owner().find(owner) {
        let class_id = canonical_class_id(progression.class_id.as_str());
        if class_definition(class_id.as_str()).is_some() {
            return Some(class_id);
        }
    }

    if let Some(player) = ctx.db.player().identity().find(owner) {
        let is_dummy = ctx
            .db
            .player_state()
            .player_id()
            .find(owner)
            .map(|state| state.is_dummy)
            .unwrap_or(false);
        let raw = if player.class_id.trim().is_empty() {
            if is_dummy {
                String::new()
            } else {
                player.class_id
            }
        } else {
            player.class_id
        };
        if !raw.trim().is_empty() {
            let class_id = canonical_class_id(raw.as_str());
            if class_definition(class_id.as_str()).is_some() {
                return Some(class_id);
            }
        }
    }
    None
}

pub(crate) fn derived_combat_profile_id_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<String> {
    let class_id = runtime_class_id_for_owner(ctx, owner)?;
    derived_combat_profile_id_for_class(class_id.as_str())
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
}

pub(crate) fn saved_spec_total_for_stat(
    allocations: &[SavedSpecStatAllocation],
    stat_kind: StatKind,
) -> u32 {
    allocations
        .iter()
        .filter(|allocation| allocation.stat_kind == stat_kind.as_str())
        .map(|allocation| allocation.allocated_points)
        .sum()
}

pub(crate) fn active_stat_totals_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> AllocatedStatTotals {
    active_spec_id_for_owner(ctx, owner)
        .map(|active_spec_id| stat_totals_for_spec(ctx, active_spec_id.as_str()))
        .unwrap_or_default()
}

pub(crate) fn primary_resource_kind_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<String> {
    let progression = ctx.db.character_progression().owner().find(owner)?;
    let class = ctx
        .db
        .class_catalog()
        .class_id()
        .find(progression.class_id)?;
    Some(class.primary_resource_kind)
}

#[allow(dead_code)]
pub(crate) fn selectable_slot_ids() -> Vec<String> {
    let mut slots: Vec<_> = progression_catalog().slots.iter().collect();
    slots.sort_by_key(|slot| slot_sort_key(slot));
    slots
        .into_iter()
        .map(|slot| canonical_loadout_slot_id(slot.slot_id.as_str()))
        .collect()
}

pub(crate) fn ability_is_compatible_with_slot(ability_id: &str, slot_id: &str) -> bool {
    let Some(ability) = ability_definition(ability_id) else {
        return false;
    };
    let canonical_slot_id = canonical_loadout_slot_id(slot_id);
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

pub(crate) fn action_id_is_selectable_loadout_action(
    ctx: &ReducerContext,
    action_id: &AuthoredActionId,
) -> bool {
    ctx.db
        .ability_catalog()
        .iter()
        .filter(|row| row.fixed_action_id.is_empty())
        .any(|row| row.action_id == action_id.as_str())
}

pub(crate) fn owner_has_active_selectable_action(
    ctx: &ReducerContext,
    owner: Identity,
    authored_action_id: &AuthoredActionId,
) -> bool {
    active_selectable_ability_for_authored_action(ctx, owner, authored_action_id).is_some()
}

fn current_class_id_for_owner(ctx: &ReducerContext, owner: Identity) -> Option<String> {
    ctx.db
        .character_progression()
        .owner()
        .find(owner)
        .map(|progression| canonical_class_id(progression.class_id.as_str()))
}

fn active_class_loadout_state_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<CharacterClassLoadoutState> {
    let class_id = current_class_id_for_owner(ctx, owner)?;
    let key = character_class_loadout_key(owner, class_id.as_str());
    let state = ctx.db.character_class_loadout_state().key().find(key)?;
    if !spec_belongs_to_owner_and_class(
        ctx,
        owner,
        state.active_spec_id.as_str(),
        class_id.as_str(),
    ) {
        return None;
    }
    Some(state)
}

fn active_spec_id_for_owner(ctx: &ReducerContext, owner: Identity) -> Option<String> {
    active_class_loadout_state_for_owner(ctx, owner).map(|state| state.active_spec_id)
}

pub(crate) fn active_selectable_ability_for_authored_action(
    ctx: &ReducerContext,
    owner: Identity,
    authored_action_id: &AuthoredActionId,
) -> Option<AbilityCatalog> {
    let active_spec_id = active_spec_id_for_owner(ctx, owner)?;
    let ability = ctx
        .db
        .saved_spec_slot_assignment()
        .spec_id()
        .filter(active_spec_id.as_str())
        .filter_map(|assignment| {
            let action_ref = action_ref_for_slot_assignment(&assignment);
            if !action_ref.is_ability() {
                return None;
            }
            ctx.db.ability_catalog().ability_id().find(action_ref.id)
        })
        .find(|ability| ability.action_id == authored_action_id.as_str());
    ability
}

pub(crate) fn active_selectable_ability_for_ability_id(
    ctx: &ReducerContext,
    owner: Identity,
    ability_id: &str,
) -> Option<AbilityCatalog> {
    let normalized_ability_id = normalize_identifier(ability_id);
    let active_spec_id = active_spec_id_for_owner(ctx, owner)?;
    let ability = ctx
        .db
        .saved_spec_slot_assignment()
        .spec_id()
        .filter(active_spec_id.as_str())
        .filter_map(|assignment| {
            let action_ref = action_ref_for_slot_assignment(&assignment);
            if !action_ref.is_ability() || action_ref.id != normalized_ability_id {
                return None;
            }
            ctx.db.ability_catalog().ability_id().find(action_ref.id)
        })
        .next();
    ability
}

pub(crate) fn active_loadout_assignment_debug_summary(
    ctx: &ReducerContext,
    owner: Identity,
) -> String {
    let Some(active_spec_id) = active_spec_id_for_owner(ctx, owner) else {
        return "active_specs=[] assignments=[]".to_string();
    };

    let assignments: Vec<String> = ctx
        .db
        .saved_spec_slot_assignment()
        .spec_id()
        .filter(active_spec_id.as_str())
        .map(|assignment| {
            format!(
                "{}:{}:{}:{}:{}",
                active_spec_id,
                assignment.slot_id,
                assignment.action_kind,
                assignment.action_id,
                assignment.ability_id
            )
        })
        .collect();

    format!(
        "active_specs=[{}] assignments=[{}]",
        active_spec_id,
        assignments.join(",")
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
                    MeleeImpactEffectDefinition::ApplyStatus { status } => {
                        MeleeImpactEffectRuntime {
                            status: status_application_from_definition(
                                status,
                                authored_status_stack_group_default(status.kind.as_str()),
                            ),
                        }
                    }
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
        cast_time_ms: movement.cast_time_ms,
        cast_mobility: normalize_identifier(movement.cast_mobility.as_str()),
        targeting: normalize_identifier(movement.targeting.as_str()),
        target_audience: normalize_optional_target_audience(movement.target_audience.as_str()),
        requires_target: movement.requires_target,
        resource_cost: movement.resource_cost.max(0.0),
        arms_auto_attack_on_cast: movement.arms_auto_attack_on_cast,
        speed: movement.speed,
        max_distance: movement.max_distance,
        damage: movement.damage,
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

fn sync_class_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .classes
        .iter()
        .map(|definition| normalize_identifier(definition.class_id.as_str()))
        .collect();

    for definition in &progression_catalog().classes {
        let class_id = normalize_identifier(definition.class_id.as_str());
        let row = ClassCatalog {
            class_id: class_id.clone(),
            display_name: definition.display_name.clone(),
            default_combat_profile_id: normalize_identifier(
                definition.default_combat_profile_id.as_str(),
            ),
            primary_resource_kind: normalize_identifier(definition.primary_resource_kind.as_str()),
            max_saved_specs: definition.max_saved_specs.max(1),
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .class_catalog()
            .class_id()
            .find(class_id.clone())
            .is_some()
        {
            ctx.db.class_catalog().class_id().update(row);
        } else {
            ctx.db.class_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .class_catalog()
        .iter()
        .map(|row| row.class_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.class_catalog().class_id().delete(key);
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
            class_id: normalize_identifier(definition.class_id.as_str()),
            ability_kind: ability_gameplay_kind(definition),
            action_id: AuthoredActionId::new(definition.action_id.as_str()).into_string(),
            display_name: definition.display_name.clone(),
            fixed_action_id: normalize_identifier(definition.fixed_action_id.as_str()),
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
        let row = MeleeAbilityCatalog {
            ability_id: ability_id.clone(),
            action_id: AuthoredActionId::new(definition.action_id.as_str()).into_string(),
            base_damage: required_melee_field(
                definition.gameplay.base_damage,
                &ability_id,
                "base_damage",
            ),
            applies_stagger: required_melee_field(
                definition.gameplay.applies_stagger,
                &ability_id,
                "applies_stagger",
            ),
            range: required_melee_field(definition.gameplay.range, &ability_id, "range"),
            cooldown_ms: required_melee_field(
                definition.gameplay.cooldown_ms,
                &ability_id,
                "cooldown_ms",
            ),
            uses_global_cooldown: required_melee_field(
                definition.gameplay.uses_global_cooldown,
                &ability_id,
                "uses_global_cooldown",
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
            combat_profile_id: normalize_identifier(definition.combat_profile_id.as_str()),
            mode_id: normalize_identifier(definition.mode_id.as_str()),
            action_id: AuthoredActionId::new(definition.action_id.as_str()).into_string(),
            base_damage: definition.base_damage,
            range: definition.range,
            cooldown_ms: definition.cooldown_ms,
            movement_policy: normalize_identifier(definition.movement_policy.as_str()),
            uses_global_cooldown: definition.uses_global_cooldown,
            parry_behavior: normalize_identifier(definition.parry_behavior.as_str()),
            block_behavior: normalize_identifier(definition.block_behavior.as_str()),
            airborne_targeting_mode: normalize_identifier(
                definition.airborne_targeting_mode.as_str(),
            ),
            applies_stagger: definition.applies_stagger,
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
            range: definition.range,
            cooldown_ms: definition.cooldown_ms,
            uses_global_cooldown: definition.uses_global_cooldown,
            parry_behavior: normalize_identifier(definition.parry_behavior.as_str()),
            block_behavior: normalize_identifier(definition.block_behavior.as_str()),
            airborne_targeting_mode: normalize_identifier(
                definition.airborne_targeting_mode.as_str(),
            ),
            applies_stagger: definition.applies_stagger,
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

fn sync_fixed_action_binding_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .fixed_action_bindings
        .iter()
        .map(fixed_action_binding_key)
        .collect();

    for definition in &progression_catalog().fixed_action_bindings {
        let key = fixed_action_binding_key(definition);
        let row = FixedActionBindingCatalog {
            key: key.clone(),
            class_id: normalize_identifier(definition.class_id.as_str()),
            fixed_action_id: normalize_identifier(definition.fixed_action_id.as_str()),
            ability_id: normalize_identifier(definition.ability_id.as_str()),
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .fixed_action_binding_catalog()
            .key()
            .find(key.clone())
            .is_some()
        {
            ctx.db.fixed_action_binding_catalog().key().update(row);
        } else {
            ctx.db.fixed_action_binding_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .fixed_action_binding_catalog()
        .iter()
        .map(|row| row.key)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.fixed_action_binding_catalog().key().delete(key);
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

fn sync_loadout_slot_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = progression_catalog()
        .slots
        .iter()
        .map(|definition| canonical_loadout_slot_id(definition.slot_id.as_str()))
        .collect();

    for definition in &progression_catalog().slots {
        let slot_id = canonical_loadout_slot_id(definition.slot_id.as_str());
        let row = LoadoutSlotCatalog {
            slot_id: slot_id.clone(),
            ui_row: definition.ui_row,
            ui_col: definition.ui_col,
            slot_group: normalize_identifier(definition.slot_group.as_str()),
            accepts_tags: encode_tags(&definition.accepts_tags),
            sort_order: definition.sort_order,
        };
        if ctx
            .db
            .loadout_slot_catalog()
            .slot_id()
            .find(slot_id.clone())
            .is_some()
        {
            ctx.db.loadout_slot_catalog().slot_id().update(row);
        } else {
            ctx.db.loadout_slot_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .loadout_slot_catalog()
        .iter()
        .map(|row| row.slot_id)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.loadout_slot_catalog().slot_id().delete(key);
    }
}

fn ensure_saved_spec(
    ctx: &ReducerContext,
    owner: Identity,
    class: &ClassDefinition,
    spec_id: &str,
    now: Timestamp,
) {
    if ctx
        .db
        .saved_spec()
        .spec_id()
        .find(spec_id.to_string())
        .is_none()
    {
        ctx.db.saved_spec().insert(SavedSpec {
            spec_id: spec_id.to_string(),
            owner,
            name: DEFAULT_SPEC_NAME.to_string(),
            class_id: normalize_identifier(class.class_id.as_str()),
            version: DEFAULT_SPEC_VERSION,
            created_at: now,
            updated_at: now,
        });
    }

    for stat_kind in StatKind::ALL {
        let key = saved_spec_stat_key(spec_id, stat_kind);
        if ctx
            .db
            .saved_spec_stat_allocation()
            .key()
            .find(key.clone())
            .is_some()
        {
            continue;
        }
        ctx.db
            .saved_spec_stat_allocation()
            .insert(SavedSpecStatAllocation {
                key,
                spec_id: spec_id.to_string(),
                stat_kind: stat_kind.as_str().to_string(),
                allocated_points: default_allocated_points_for(class, stat_kind),
            });
    }

    backfill_missing_default_slot_assignments(ctx, class.class_id.as_str(), spec_id);
}

fn default_allocated_points_for(_class: &ClassDefinition, _stat_kind: StatKind) -> u32 {
    0
}

fn backfill_missing_default_slot_assignments(
    ctx: &ReducerContext,
    class_id: &str,
    spec_id: &str,
) -> bool {
    let assigned_slots: HashSet<String> = ctx
        .db
        .saved_spec_slot_assignment()
        .spec_id()
        .filter(spec_id)
        .map(|assignment| canonical_loadout_slot_id(assignment.slot_id.as_str()))
        .collect();
    let mut inserted_any = false;

    for assignment in default_loadout_assignments_for_class(class_id) {
        let slot_id = canonical_loadout_slot_id(assignment.slot_id.as_str());
        if assigned_slots.contains(slot_id.as_str()) {
            continue;
        }
        let action_ref = action_ref_for_default_assignment(&assignment);
        upsert_slot_assignment(ctx, spec_id, slot_id.as_str(), &action_ref);
        inserted_any = true;
    }

    inserted_any
}

fn default_spec_id(owner: Identity, class_id: &str) -> String {
    format!(
        "{}:{}:default",
        owner.to_hex(),
        normalize_identifier(class_id)
    )
}

fn character_class_loadout_key(owner: Identity, class_id: &str) -> String {
    format!("{}:{}", owner.to_hex(), normalize_identifier(class_id))
}

fn saved_spec_stat_key(spec_id: &str, stat_kind: StatKind) -> String {
    format!("{}:{}", spec_id, stat_kind.as_str())
}

fn saved_spec_slot_key(spec_id: &str, slot_id: &str) -> String {
    format!("{}:{}", spec_id, canonical_loadout_slot_id(slot_id))
}

fn default_loadout_assignments_for_class(
    class_id: &str,
) -> Vec<&'static DefaultLoadoutAssignmentDefinition> {
    let normalized_class_id = normalize_identifier(class_id);
    let mut assignments: Vec<_> = progression_catalog()
        .default_loadout_assignments
        .iter()
        .filter(|assignment| {
            normalize_identifier(assignment.class_id.as_str()) == normalized_class_id
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

fn action_ref_for_default_assignment(assignment: &DefaultLoadoutAssignmentDefinition) -> ActionRef {
    let action_kind = normalize_identifier(assignment.action_kind.as_str());
    let action_id = normalize_identifier(assignment.action_id.as_str());
    if !action_kind.is_empty() || !action_id.is_empty() {
        return ActionRef::from_wire(action_kind.as_str(), action_id.as_str());
    }
    ActionRef::ability(assignment.ability_id.as_str())
}

#[allow(dead_code)]
fn slot_sort_key(slot: &LoadoutSlotDefinition) -> (u32, u32, u32) {
    (slot.ui_row, slot.ui_col, slot.sort_order)
}

#[allow(dead_code)]
fn slot_sort_key_for_id(slot_id: &str) -> (u32, u32, u32) {
    slot_definition(slot_id)
        .map(slot_sort_key)
        .unwrap_or((u32::MAX, u32::MAX, u32::MAX))
}

fn ensure_class_loadout_state(
    ctx: &ReducerContext,
    owner: Identity,
    class: &ClassDefinition,
    now: Timestamp,
) -> CharacterClassLoadoutState {
    let class_id = normalize_identifier(class.class_id.as_str());
    let key = character_class_loadout_key(owner, class_id.as_str());
    let default_spec_id = default_spec_id(owner, class.class_id.as_str());
    ensure_saved_spec(ctx, owner, class, default_spec_id.as_str(), now);

    if let Some(mut state) = ctx
        .db
        .character_class_loadout_state()
        .key()
        .find(key.clone())
    {
        state.class_id = canonical_class_id(state.class_id.as_str());
        if !spec_belongs_to_owner_and_class(
            ctx,
            owner,
            state.active_spec_id.as_str(),
            class_id.as_str(),
        ) {
            state.active_spec_id = default_spec_id;
            state.updated_at = now;
        }
        let resolved_state = CharacterClassLoadoutState {
            key: state.key.clone(),
            owner: state.owner,
            class_id: state.class_id.clone(),
            active_spec_id: state.active_spec_id.clone(),
            updated_at: state.updated_at,
        };
        ctx.db.character_class_loadout_state().key().update(state);
        return resolved_state;
    }

    let candidate_active_spec_id = default_spec_id;

    let state = CharacterClassLoadoutState {
        key: key.clone(),
        owner,
        class_id: class_id.clone(),
        active_spec_id: candidate_active_spec_id.clone(),
        updated_at: now,
    };
    ctx.db.character_class_loadout_state().insert(state);
    CharacterClassLoadoutState {
        key,
        owner,
        class_id,
        active_spec_id: candidate_active_spec_id,
        updated_at: now,
    }
}

fn upsert_class_loadout_state(
    ctx: &ReducerContext,
    owner: Identity,
    class_id: &str,
    active_spec_id: &str,
    now: Timestamp,
) {
    let class_id = canonical_class_id(class_id);
    let key = character_class_loadout_key(owner, class_id.as_str());
    let state = CharacterClassLoadoutState {
        key: key.clone(),
        owner,
        class_id,
        active_spec_id: active_spec_id.to_string(),
        updated_at: now,
    };
    if ctx
        .db
        .character_class_loadout_state()
        .key()
        .find(key)
        .is_some()
    {
        ctx.db.character_class_loadout_state().key().update(state);
    } else {
        ctx.db.character_class_loadout_state().insert(state);
    }
}

fn class_definition(class_id: &str) -> Option<&'static ClassDefinition> {
    let class_id = canonical_class_id(class_id);
    progression_catalog()
        .classes
        .iter()
        .find(|definition| normalize_identifier(definition.class_id.as_str()) == class_id)
}

fn canonical_class_id(value: &str) -> String {
    let normalized = normalize_identifier(value);
    match normalized.as_str() {
        "WARRIOR" => return "WARRIOR".to_string(),
        "PALADIN" => return "PALADIN".to_string(),
        "ARCHER" => return "RANGER".to_string(),
        _ => {}
    }
    if progression_catalog()
        .classes
        .iter()
        .any(|definition| normalize_identifier(definition.class_id.as_str()) == normalized)
    {
        return normalized;
    }
    normalized
}

fn repair_legacy_class_rows(ctx: &ReducerContext) {
    let player_rows: Vec<_> = ctx.db.player().iter().collect();
    for mut row in player_rows {
        let raw_class_id = if row.class_id.trim().is_empty() {
            default_class_id()
        } else {
            row.class_id.clone()
        };
        let canonical = canonical_class_id(raw_class_id.as_str());
        if canonical == row.class_id {
            continue;
        }
        row.class_id = canonical;
        ctx.db.player().identity().update(row);
    }

    let progression_rows: Vec<_> = ctx.db.character_progression().iter().collect();
    for mut row in progression_rows {
        let canonical = canonical_class_id(row.class_id.as_str());
        if canonical == row.class_id {
            continue;
        }
        row.class_id = canonical;
        ctx.db.character_progression().owner().update(row);
    }

    let saved_specs: Vec<_> = ctx.db.saved_spec().iter().collect();
    for mut spec in saved_specs {
        let canonical = canonical_class_id(spec.class_id.as_str());
        if canonical == spec.class_id {
            continue;
        }
        spec.class_id = canonical;
        ctx.db.saved_spec().spec_id().update(spec);
    }

    let states: Vec<_> = ctx.db.character_class_loadout_state().iter().collect();
    for state in states {
        let canonical = canonical_class_id(state.class_id.as_str());
        if canonical == state.class_id {
            continue;
        }
        let owner = state.owner;
        let active_spec_id = state.active_spec_id;
        let updated_at = state.updated_at;
        ctx.db
            .character_class_loadout_state()
            .key()
            .delete(state.key);
        upsert_class_loadout_state(
            ctx,
            owner,
            canonical.as_str(),
            active_spec_id.as_str(),
            updated_at,
        );
    }
}

fn repair_saved_spec_rows(ctx: &ReducerContext, now: Timestamp) {
    let specs: Vec<_> = ctx.db.saved_spec().iter().collect();
    for mut spec in specs {
        let Some(class) = class_definition(spec.class_id.as_str()) else {
            continue;
        };

        let mut updated_at = spec.updated_at;
        if repair_legacy_slot_assignment_rows(ctx, spec.spec_id.as_str()) {
            updated_at = now;
        }
        if repair_malformed_ability_slot_assignment_rows(
            ctx,
            class.class_id.as_str(),
            spec.spec_id.as_str(),
        ) {
            updated_at = now;
        }
        if repair_bad_giant_swing_q_assignment(ctx, spec.spec_id.as_str()) {
            updated_at = now;
        }
        for stat_kind in StatKind::ALL {
            let key = saved_spec_stat_key(spec.spec_id.as_str(), stat_kind);
            if ctx
                .db
                .saved_spec_stat_allocation()
                .key()
                .find(key.clone())
                .is_some()
            {
                continue;
            }
            ctx.db
                .saved_spec_stat_allocation()
                .insert(SavedSpecStatAllocation {
                    key,
                    spec_id: spec.spec_id.clone(),
                    stat_kind: stat_kind.as_str().to_string(),
                    allocated_points: default_allocated_points_for(class, stat_kind),
                });
            updated_at = now;
        }

        if backfill_missing_default_slot_assignments(
            ctx,
            class.class_id.as_str(),
            spec.spec_id.as_str(),
        ) {
            updated_at = now;
        }
        if updated_at != spec.updated_at {
            spec.updated_at = updated_at;
            ctx.db.saved_spec().spec_id().update(spec);
        }
    }
}

fn repair_legacy_slot_assignment_rows(ctx: &ReducerContext, spec_id: &str) -> bool {
    let assignments: Vec<_> = ctx
        .db
        .saved_spec_slot_assignment()
        .spec_id()
        .filter(spec_id)
        .collect();
    let mut repaired = false;

    for assignment in assignments {
        let canonical_slot_id = canonical_loadout_slot_id(assignment.slot_id.as_str());
        let canonical_key =
            saved_spec_slot_key(assignment.spec_id.as_str(), canonical_slot_id.as_str());
        let action_ref = action_ref_for_slot_assignment(&assignment);
        if assignment.slot_id == canonical_slot_id
            && assignment.key == canonical_key
            && assignment.action_kind == action_ref.kind_wire()
            && assignment.action_id == action_ref.id
            && assignment.ability_id == legacy_ability_id_for_action_ref(&action_ref)
        {
            continue;
        }

        ctx.db
            .saved_spec_slot_assignment()
            .key()
            .delete(assignment.key.clone());
        if ctx
            .db
            .saved_spec_slot_assignment()
            .key()
            .find(canonical_key.clone())
            .is_none()
        {
            ctx.db
                .saved_spec_slot_assignment()
                .insert(SavedSpecSlotAssignment {
                    key: canonical_key,
                    spec_id: assignment.spec_id,
                    slot_id: canonical_slot_id,
                    action_kind: action_ref.kind_wire().to_string(),
                    action_id: action_ref.id.clone(),
                    ability_id: legacy_ability_id_for_action_ref(&action_ref),
                });
        }
        repaired = true;
    }

    repaired
}

fn repair_malformed_ability_slot_assignment_rows(
    ctx: &ReducerContext,
    class_id: &str,
    spec_id: &str,
) -> bool {
    let assignments: Vec<_> = ctx
        .db
        .saved_spec_slot_assignment()
        .spec_id()
        .filter(spec_id)
        .collect();
    let normalized_class_id = normalize_identifier(class_id);
    let mut repaired = false;

    for assignment in assignments {
        let action_ref = action_ref_for_slot_assignment(&assignment);
        let ActionKind::Unsupported(kind) = &action_ref.kind else {
            continue;
        };
        if action_ref.id != *kind {
            continue;
        }

        let Some(ability) = ability_definition(kind.as_str()) else {
            continue;
        };
        if normalize_identifier(ability.class_id.as_str()) != normalized_class_id {
            continue;
        }

        upsert_slot_assignment(
            ctx,
            assignment.spec_id.as_str(),
            assignment.slot_id.as_str(),
            &ActionRef::ability(kind.as_str()),
        );
        repaired = true;
    }

    repaired
}

fn repair_bad_giant_swing_q_assignment(ctx: &ReducerContext, spec_id: &str) -> bool {
    let bad_slot_id = canonical_loadout_slot_id("slot_1_0");
    let target_slot_id = canonical_loadout_slot_id("slot_1_2");
    let bad_key = saved_spec_slot_key(spec_id, bad_slot_id.as_str());
    let Some(assignment) = ctx
        .db
        .saved_spec_slot_assignment()
        .key()
        .find(bad_key.clone())
    else {
        return false;
    };
    let action_ref = action_ref_for_slot_assignment(&assignment);
    if !action_ref.is_ability() || action_ref.id != "WARRIOR_GIANT_SWING" {
        return false;
    }

    ctx.db.saved_spec_slot_assignment().key().delete(bad_key);
    let target_key = saved_spec_slot_key(spec_id, target_slot_id.as_str());
    if ctx
        .db
        .saved_spec_slot_assignment()
        .key()
        .find(target_key)
        .is_none()
    {
        upsert_slot_assignment(ctx, spec_id, target_slot_id.as_str(), &action_ref);
    }
    true
}

fn repair_class_loadout_state_rows(ctx: &ReducerContext, now: Timestamp) {
    let states: Vec<_> = ctx.db.character_class_loadout_state().iter().collect();
    for state in states {
        let Some(class) = class_definition(state.class_id.as_str()) else {
            ctx.db
                .character_class_loadout_state()
                .key()
                .delete(state.key);
            continue;
        };
        ensure_class_loadout_state(ctx, state.owner, class, now);
    }

    let progressions: Vec<_> = ctx.db.character_progression().iter().collect();
    for progression in progressions {
        let Some(class) = class_definition(progression.class_id.as_str()) else {
            continue;
        };
        ensure_class_loadout_state(ctx, progression.owner, class, now);
    }
}

fn validate_spec_name(value: &str) -> Result<String, String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return Err("spec name cannot be empty".to_string());
    }
    if trimmed.chars().count() > MAX_SPEC_NAME_LEN {
        return Err(format!(
            "spec name cannot exceed {} characters",
            MAX_SPEC_NAME_LEN
        ));
    }
    Ok(trimmed.to_string())
}

fn next_saved_spec_id(
    ctx: &ReducerContext,
    owner: Identity,
    class_id: &str,
    now: Timestamp,
) -> String {
    let base = format!(
        "{}:{}:{}",
        owner.to_hex(),
        normalize_identifier(class_id),
        now.to_micros_since_unix_epoch()
    );
    if ctx.db.saved_spec().spec_id().find(base.clone()).is_none() {
        return base;
    }
    let mut suffix = 1_u32;
    loop {
        let candidate = format!("{base}:{suffix}");
        if ctx
            .db
            .saved_spec()
            .spec_id()
            .find(candidate.clone())
            .is_none()
        {
            return candidate;
        }
        suffix = suffix.saturating_add(1);
    }
}

fn saved_specs_for_owner_and_class(
    ctx: &ReducerContext,
    owner: Identity,
    class_id: &str,
) -> Vec<SavedSpec> {
    let class_id = canonical_class_id(class_id);
    ctx.db
        .saved_spec()
        .owner()
        .filter(owner)
        .filter(|spec| canonical_class_id(spec.class_id.as_str()) == class_id)
        .collect()
}

fn spec_belongs_to_owner_and_class(
    ctx: &ReducerContext,
    owner: Identity,
    spec_id: &str,
    class_id: &str,
) -> bool {
    let class_id = canonical_class_id(class_id);
    ctx.db
        .saved_spec()
        .spec_id()
        .find(spec_id.trim().to_string())
        .map(|spec| spec.owner == owner && canonical_class_id(spec.class_id.as_str()) == class_id)
        .unwrap_or(false)
}

fn class_loadout_state_uses_spec(
    ctx: &ReducerContext,
    owner: Identity,
    class_id: &str,
    spec_id: &str,
) -> bool {
    let key = character_class_loadout_key(owner, class_id);
    ctx.db
        .character_class_loadout_state()
        .key()
        .find(key)
        .map(|state| state.active_spec_id == spec_id)
        .unwrap_or(false)
}

fn require_character_progression(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<CharacterProgression, String> {
    ctx.db
        .character_progression()
        .owner()
        .find(owner)
        .ok_or_else(|| "character progression row not found".to_string())
}

fn require_owned_spec(
    ctx: &ReducerContext,
    owner: Identity,
    spec_id: &str,
) -> Result<SavedSpec, String> {
    let normalized_spec_id = spec_id.trim().to_string();
    let spec = ctx
        .db
        .saved_spec()
        .spec_id()
        .find(normalized_spec_id.clone())
        .ok_or_else(|| format!("saved spec '{}' not found", normalized_spec_id))?;
    if spec.owner != owner {
        return Err("saved spec does not belong to the current player".to_string());
    }
    Ok(spec)
}

fn require_class_catalog_row(ctx: &ReducerContext, class_id: &str) -> Result<ClassCatalog, String> {
    let normalized = normalize_identifier(class_id);
    ctx.db
        .class_catalog()
        .class_id()
        .find(normalized.clone())
        .ok_or_else(|| format!("class '{}' not found in catalog", normalized))
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
) -> Result<LoadoutSlotCatalog, String> {
    let normalized = canonical_loadout_slot_id(slot_id);
    ctx.db
        .loadout_slot_catalog()
        .slot_id()
        .find(normalized.clone())
        .ok_or_else(|| format!("slot '{}' not found in catalog", normalized))
}

fn validate_slot_action_ref(
    ctx: &ReducerContext,
    class_id: &str,
    slot_id: &str,
    action_ref: &ActionRef,
) -> Result<(), String> {
    let slot = require_slot_catalog_row(ctx, slot_id)?;
    match &action_ref.kind {
        ActionKind::Ability => {
            let ability = require_ability_catalog_row(ctx, action_ref.id.as_str())?;
            if ability.class_id != normalize_identifier(class_id) {
                return Err(format!(
                    "ability '{}' does not belong to class '{}'",
                    ability.ability_id, class_id
                ));
            }
            if !ability_is_compatible_with_slot(action_ref.id.as_str(), slot.slot_id.as_str()) {
                return Err(format!(
                    "ability '{}' is not compatible with slot '{}'",
                    ability.ability_id, slot.slot_id
                ));
            }
            Ok(())
        }
        ActionKind::Fixed => {
            let fixed_action_id = FixedActionId::from_wire(action_ref.id.as_str());
            match &fixed_action_id {
                FixedActionId::Dodge | FixedActionId::Parry => Ok(()),
                FixedActionId::Unsupported(value) => {
                    Err(format!("unsupported fixed action '{value}'"))
                }
            }
        }
        ActionKind::Unsupported(kind) => Err(format!("unsupported action kind '{kind}'")),
    }
}

fn stat_totals_for_spec(ctx: &ReducerContext, spec_id: &str) -> AllocatedStatTotals {
    let allocations: Vec<_> = ctx
        .db
        .saved_spec_stat_allocation()
        .spec_id()
        .filter(spec_id)
        .collect();
    AllocatedStatTotals {
        might: saved_spec_total_for_stat(allocations.as_slice(), StatKind::Might),
        insight: saved_spec_total_for_stat(allocations.as_slice(), StatKind::Insight),
        finesse: saved_spec_total_for_stat(allocations.as_slice(), StatKind::Finesse),
        quickness: saved_spec_total_for_stat(allocations.as_slice(), StatKind::Quickness),
        fortitude: saved_spec_total_for_stat(allocations.as_slice(), StatKind::Fortitude),
    }
}

fn upsert_stat_allocation(
    ctx: &ReducerContext,
    spec_id: &str,
    stat_kind: StatKind,
    allocated_points: u32,
) {
    let key = saved_spec_stat_key(spec_id, stat_kind);
    let row = SavedSpecStatAllocation {
        key: key.clone(),
        spec_id: spec_id.to_string(),
        stat_kind: stat_kind.as_str().to_string(),
        allocated_points,
    };
    if ctx
        .db
        .saved_spec_stat_allocation()
        .key()
        .find(key)
        .is_some()
    {
        ctx.db.saved_spec_stat_allocation().key().update(row);
    } else {
        ctx.db.saved_spec_stat_allocation().insert(row);
    }
}

fn action_ref_for_slot_assignment(assignment: &SavedSpecSlotAssignment) -> ActionRef {
    let action_kind = normalize_identifier(assignment.action_kind.as_str());
    let action_id = normalize_identifier(assignment.action_id.as_str());
    if !action_kind.is_empty() || !action_id.is_empty() {
        return ActionRef::from_wire(action_kind.as_str(), action_id.as_str());
    }
    ActionRef::ability(assignment.ability_id.as_str())
}

fn legacy_ability_id_for_action_ref(action_ref: &ActionRef) -> String {
    if action_ref.is_ability() {
        action_ref.id.clone()
    } else {
        String::new()
    }
}

fn upsert_slot_assignment(
    ctx: &ReducerContext,
    spec_id: &str,
    slot_id: &str,
    action_ref: &ActionRef,
) {
    let key = saved_spec_slot_key(spec_id, slot_id);
    let row = SavedSpecSlotAssignment {
        key: key.clone(),
        spec_id: spec_id.to_string(),
        slot_id: canonical_loadout_slot_id(slot_id),
        action_kind: action_ref.kind_wire().to_string(),
        action_id: action_ref.id.clone(),
        ability_id: legacy_ability_id_for_action_ref(action_ref),
    };
    if ctx
        .db
        .saved_spec_slot_assignment()
        .key()
        .find(key)
        .is_some()
    {
        ctx.db.saved_spec_slot_assignment().key().update(row);
    } else {
        ctx.db.saved_spec_slot_assignment().insert(row);
    }
}

fn delete_saved_spec_rows(ctx: &ReducerContext, spec_id: &str) {
    let stat_keys: Vec<_> = ctx
        .db
        .saved_spec_stat_allocation()
        .spec_id()
        .filter(spec_id)
        .map(|row| row.key)
        .collect();
    for key in stat_keys {
        ctx.db.saved_spec_stat_allocation().key().delete(key);
    }

    let slot_keys: Vec<_> = ctx
        .db
        .saved_spec_slot_assignment()
        .spec_id()
        .filter(spec_id)
        .map(|row| row.key)
        .collect();
    for key in slot_keys {
        ctx.db.saved_spec_slot_assignment().key().delete(key);
    }

    ctx.db.saved_spec().spec_id().delete(spec_id.to_string());
}

fn ability_definition(ability_id: &str) -> Option<&'static AbilityDefinition> {
    let ability_id = normalize_identifier(ability_id);
    progression_catalog()
        .abilities
        .iter()
        .find(|definition| normalize_identifier(definition.ability_id.as_str()) == ability_id)
}

fn slot_definition(slot_id: &str) -> Option<&'static LoadoutSlotDefinition> {
    let slot_id = canonical_loadout_slot_id(slot_id);
    progression_catalog()
        .slots
        .iter()
        .find(|definition| canonical_loadout_slot_id(definition.slot_id.as_str()) == slot_id)
}

fn fixed_action_binding_key(definition: &FixedActionBindingDefinition) -> String {
    format!(
        "{}:{}",
        normalize_identifier(definition.class_id.as_str()),
        normalize_identifier(definition.fixed_action_id.as_str())
    )
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
}

fn validate_ability_catalog() {
    for ability in &progression_catalog().abilities {
        let ability_id = normalize_identifier(ability.ability_id.as_str());
        let ability_kind = ability_gameplay_kind(ability);
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
                    && ability.gameplay.resource_cost.is_none()
                    && ability.gameplay.primary_resource_gain_on_cast == 0.0
                    && !ability.gameplay.arms_auto_attack_on_cast,
                "movement ability '{ability_id}' must define execution fields inside gameplay.delivery"
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
                    && ability.gameplay.resource_cost.is_none()
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

fn validate_melee_gameplay_fields(ability_id: &str, gameplay: &AbilityGameplayDefinition) {
    if gameplay.gap_close.is_some() && gameplay.melee_timed_movement.is_some() {
        panic!("melee ability '{ability_id}' must not combine gap_close and melee_timed_movement");
    }
    if let Some(movement) = gameplay.melee_timed_movement.as_ref() {
        validate_melee_timed_movement(ability_id, movement);
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
        _ => panic!(
            "melee ability '{ability_id}' melee_targeting.kind must be TARGET, CASTER_RADIUS, or CASTER_CONE, got '{targeting_kind}'"
        ),
    }
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

fn canonical_loadout_slot_id(value: &str) -> String {
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
    normalized.join("|")
}

#[cfg(test)]
mod tests {
    use std::collections::{HashMap, HashSet};

    use crate::animation_set_test_utils::{
        animation_set_assets_by_combat_profile, parse_top_level_animation_set_field,
    };
    use crate::combat::{
        StackPolicy, StatusApplication, StatusPayload, StatusStackGroupDefault, DEFAULT_HIT_RADIUS,
    };
    use crate::melee::profile_supports_action_reference;
    use crate::progression::melee_timed_movement_for_ability_id;
    use crate::spells::spell_definition_by_str;

    use super::{
        ability_gameplay_kind, ability_is_compatible_with_slot, action_presentation_key,
        action_ref_for_default_assignment, authored_status_presentation_ids,
        canonical_loadout_slot_id, combat_vfx_cue_key, derived_combat_profile_id_for_class,
        derived_spell_action_presentation_rows, melee_impact_effects_for_ability_id,
        normalize_identifier, primary_resource_gain_on_action_accept, progression_catalog,
        projectile_body_vfx_id_for_spell, resolved_melee_targeting_for_catalog,
        saved_spec_total_for_stat, selectable_slot_ids, validate_auto_attack_catalog,
        validate_combat_mode_catalog, validate_progression_catalog_authoring_contract,
        AbilityDefinition, ActionKind, CombatVfxPresentationManifest, FixedActionId,
        MeleeImpactEffectRuntime, SavedSpecStatAllocation, StatKind,
        ABILITY_KIND_COMBAT_MODE_TOGGLE, ACTION_KIND_FIXED, ARCHER_DRAW_MODE_TOGGLE_ABILITY_ID,
        AUTO_ATTACK_MOVEMENT_ALLOW_MOVING, AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE,
        COMBAT_MODE_FULL_DRAW, COMBAT_MODE_SHORT_DRAW, COMBAT_PROFILE_ARCHER_BOW,
    };
    use crate::action_ids::{AuthoredActionId, RuntimeActionId};

    const GAP_CLOSE_TARGET_ARRIVAL_DISTANCE_METERS: f32 = 2.0;

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
        if ability_delivery_kind(ability) != "PROJECTILE" {
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
        Unknown(String),
    }

    #[derive(Clone, Debug)]
    struct ResolvedCombatAuthoringAction {
        ability_id: String,
        category: ResolvedAuthoringCategory,
        class_id: String,
        combat_profile_id: String,
        authored_action_id: String,
        fixed_action_id: String,
        default_loadout_slots: Vec<String>,
        has_loadout_action_tag: bool,
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
        AbilityClassResolves,
        MeleeActionIdMatchesAuthoredStrike,
        MeleeActionIdNotRuntimeSlot,
        SpellActionIdResolvesToSpell,
        SelectableSpellHasAnimationEntry,
        AutoAttackReplacementResolves,
        AutoAttackReplacementStrikeMatchesAuthoredStrike,
        DefaultLoadoutAssignmentResolves,
        FixedActionBindingResolves,
        CoreAbilityHasDefaultAssignment,
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
                Self::AbilityClassResolves => "ability-class-resolves",
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
                Self::DefaultLoadoutAssignmentResolves => "default-loadout-assignment-resolves",
                Self::FixedActionBindingResolves => "fixed-action-binding-resolves",
                Self::CoreAbilityHasDefaultAssignment => "core-ability-has-default-assignment",
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
                let class_id = normalize_identifier(ability.class_id.as_str());
                let combat_profile_id =
                    derived_combat_profile_id_for_class(class_id.as_str()).unwrap_or_default();
                let authored_action_id = normalize_identifier(ability.action_id.as_str());
                let fixed_action_id = normalize_identifier(ability.fixed_action_id.as_str());
                let has_loadout_action_tag = ability
                    .ability_tags
                    .iter()
                    .any(|tag| normalize_identifier(tag.as_str()) == "LOADOUT_ACTION");
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
                    other => ResolvedAuthoringCategory::Unknown(other.to_string()),
                };

                let default_loadout_slots: Vec<String> = catalog
                    .default_loadout_assignments
                    .iter()
                    .filter_map(|assignment| {
                        let action_ref = action_ref_for_default_assignment(assignment);
                        if action_ref.is_ability() && action_ref.id == ability_id {
                            Some(canonical_loadout_slot_id(assignment.slot_id.as_str()))
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
                    && (has_loadout_action_tag
                        || !fixed_action_id.is_empty()
                        || !default_loadout_slots.is_empty());
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
                    category,
                    class_id,
                    combat_profile_id,
                    authored_action_id,
                    fixed_action_id,
                    default_loadout_slots,
                    has_loadout_action_tag,
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
        let known_classes: HashSet<_> = catalog
            .classes
            .iter()
            .map(|class| normalize_identifier(class.class_id.as_str()))
            .collect();

        for action in graph {
            if !known_classes.contains(action.class_id.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::AbilityClassResolves,
                    format!(
                        "ability '{}' references unknown class '{}'",
                        action.ability_id, action.class_id
                    ),
                ));
            }
            if action.combat_profile_id.is_empty() {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::AbilityClassResolves,
                    format!(
                        "ability '{}' class '{}' must resolve to a default combat profile",
                        action.ability_id, action.class_id
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
                    if !action.spell_has_animation {
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

            let is_player_facing = !action.default_loadout_slots.is_empty()
                || !action.fixed_action_id.is_empty()
                || action.has_loadout_action_tag;
            if action.has_core_ability_tag && action.default_loadout_slots.is_empty() {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CoreAbilityHasDefaultAssignment,
                    format!(
                        "core ability '{}' must have a default loadout assignment",
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

        let mut fixed_action_binding_slots = HashSet::new();
        for binding in &catalog.fixed_action_bindings {
            let normalized_class_id = normalize_identifier(binding.class_id.as_str());
            let fixed_action_id = normalize_identifier(binding.fixed_action_id.as_str());
            let ability_id = normalize_identifier(binding.ability_id.as_str());

            if !fixed_action_binding_slots
                .insert((normalized_class_id.clone(), fixed_action_id.clone()))
            {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::FixedActionBindingResolves,
                    format!(
                        "duplicate fixed action binding for class '{}' action '{}'",
                        binding.class_id, binding.fixed_action_id
                    ),
                ));
            }
            if !known_classes.contains(normalized_class_id.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::FixedActionBindingResolves,
                    format!(
                        "fixed action binding '{}' references unknown class '{}'",
                        binding.ability_id, binding.class_id
                    ),
                ));
            }
            if let FixedActionId::Unsupported(value) =
                FixedActionId::from_wire(fixed_action_id.as_str())
            {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::FixedActionBindingResolves,
                    format!("fixed action binding references unsupported fixed action '{value}'"),
                ));
            }

            let Some(action) = graph.iter().find(|action| action.ability_id == ability_id) else {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::FixedActionBindingResolves,
                    format!(
                        "fixed action binding references unknown ability '{}'",
                        binding.ability_id
                    ),
                ));
                continue;
            };

            if action.class_id != normalized_class_id {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::FixedActionBindingResolves,
                    format!(
                        "fixed action ability '{}' belongs to class '{}' but binding is for class '{}'",
                        binding.ability_id, action.class_id, binding.class_id
                    ),
                ));
            }
            if action.fixed_action_id != fixed_action_id {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::FixedActionBindingResolves,
                    format!(
                        "fixed action ability '{}' must declare fixed_action_id '{}'",
                        binding.ability_id, binding.fixed_action_id
                    ),
                ));
            }
        }

        let known_slots: HashSet<_> = catalog
            .slots
            .iter()
            .map(|slot| canonical_loadout_slot_id(slot.slot_id.as_str()))
            .collect();
        let mut default_assignment_slots = HashSet::new();

        for assignment in &catalog.default_loadout_assignments {
            let normalized_class_id = normalize_identifier(assignment.class_id.as_str());
            let normalized_slot_id = canonical_loadout_slot_id(assignment.slot_id.as_str());
            if !default_assignment_slots
                .insert((normalized_class_id.clone(), normalized_slot_id.clone()))
            {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::DefaultLoadoutAssignmentResolves,
                    format!(
                        "duplicate default assignment for class '{}' slot '{}'",
                        assignment.class_id, assignment.slot_id
                    ),
                ));
            }
            if !known_classes.contains(normalized_class_id.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::DefaultLoadoutAssignmentResolves,
                    format!(
                        "default assignment references unknown class '{}'",
                        assignment.class_id
                    ),
                ));
            }
            if !known_slots.contains(normalized_slot_id.as_str()) {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::DefaultLoadoutAssignmentResolves,
                    format!(
                        "default assignment references unknown slot '{}'",
                        assignment.slot_id
                    ),
                ));
            }

            let action_ref = action_ref_for_default_assignment(assignment);
            match &action_ref.kind {
                ActionKind::Ability => {
                    let Some(action) = graph
                        .iter()
                        .find(|action| action.ability_id == action_ref.id)
                    else {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::DefaultLoadoutAssignmentResolves,
                            format!(
                                "default assignment references unknown ability '{}'",
                                action_ref.id
                            ),
                        ));
                        continue;
                    };

                    if action.class_id != normalized_class_id {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::DefaultLoadoutAssignmentResolves,
                            format!(
                                "default assignment ability '{}' belongs to class '{}' but assignment is for class '{}'",
                                action_ref.id, action.class_id, assignment.class_id
                            ),
                        ));
                    }
                    if !ability_is_compatible_with_slot(
                        action_ref.id.as_str(),
                        normalized_slot_id.as_str(),
                    ) {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::DefaultLoadoutAssignmentResolves,
                            format!(
                                "default assignment ability '{}' is incompatible with slot '{}'",
                                action_ref.id, assignment.slot_id
                            ),
                        ));
                    }
                }
                ActionKind::Fixed => {
                    let fixed_action_id = FixedActionId::from_wire(action_ref.id.as_str());
                    match &fixed_action_id {
                        FixedActionId::Dodge | FixedActionId::Parry => {}
                        FixedActionId::Unsupported(value) => {
                            errors.push(CombatAuthoringError::new(
                                CombatAuthoringRule::DefaultLoadoutAssignmentResolves,
                                format!("default assignment references unsupported fixed action '{value}'"),
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
                ActionKind::Unsupported(kind) => {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::DefaultLoadoutAssignmentResolves,
                        format!("default assignment uses unsupported action kind '{kind}'"),
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
            .filter(|ability| ability_delivery_kind(ability) == "PROJECTILE")
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
            .filter(|ability| ability_delivery_kind(ability) == "PROJECTILE")
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
            .filter(|ability| ability_delivery_kind(ability) == "PROJECTILE")
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
            .classes
            .iter()
            .filter_map(|class| derived_combat_profile_id_for_class(class.class_id.as_str()))
            .flat_map(|profile| {
                authored_strike_ids_for_combat_profile(profile.as_str())
                    .into_iter()
                    .collect::<Vec<_>>()
            })
            .collect();
        let known_melee_strike_hit_windows: HashMap<_, _> = catalog
            .classes
            .iter()
            .filter_map(|class| derived_combat_profile_id_for_class(class.class_id.as_str()))
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
                let class_id = normalize_identifier(ability.class_id.as_str());
                let combat_profile_id = derived_combat_profile_id_for_class(class_id.as_str())?;
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
            "SPECIAL_MOVEMENT_START",
            "SPECIAL_MOVEMENT_ARRIVAL",
        ];
        let supported_vfx_anchors = [
            "CASTER",
            "TARGET",
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
            "WORLD_ALIGNED_TO_FACING",
        ];
        let supported_vfx_roles = ["", "ONE_SHOT", "ATTACHED", "PROJECTILE_BODY", "TRAVEL_BODY"];
        let supported_lifecycles = [
            "",
            "DURATION",
            "PARTICLE_SYSTEM",
            "UNTIL_RELEASE_EVENT",
            "UNTIL_TERMINAL_EVENT",
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
            if effective_lifecycle == "UNTIL_RELEASE_EVENT" && trigger != "SPELL_CAST" {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' uses UNTIL_RELEASE_EVENT outside SPELL_CAST",
                        cue.vfx_id
                    ),
                ));
            }
            if effective_lifecycle == "PARTICLE_SYSTEM" {
                if effective_vfx_role != "ONE_SHOT" {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' uses PARTICLE_SYSTEM lifecycle with role '{}'; PARTICLE_SYSTEM is only valid for ONE_SHOT prefab cues",
                            cue.vfx_id, effective_vfx_role
                        ),
                    ));
                }
                if cue.duration_ms != 0 {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' uses PARTICLE_SYSTEM lifecycle and must set duration_ms to 0",
                            cue.vfx_id
                        ),
                    ));
                }
            }
            let cast_time_ms = match owner_kind.as_str() {
                "ABILITY" => cast_time_spell_abilities.get(owner_id.as_str()).copied(),
                "SPELL" => cast_time_spell_kinds.get(owner_id.as_str()).copied(),
                _ => None,
            };
            if let Some(cast_time_ms) = cast_time_ms {
                if trigger == "SPELL_CAST"
                    && attach_mode == "FOLLOW_ANCHOR"
                    && effective_vfx_role == "ATTACHED"
                    && matches!(anchor.as_str(), "LEFT_HAND" | "RIGHT_HAND")
                    && effective_lifecycle != "UNTIL_RELEASE_EVENT"
                {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' is a hand-attached SPELL_CAST cue for cast-time spell owner '{}:{}' (cast_time_ms {}) but uses lifecycle '{}'; use UNTIL_RELEASE_EVENT with duration_ms 0",
                            cue.vfx_id,
                            cue.owner_kind,
                            cue.owner_id,
                            cast_time_ms,
                            effective_lifecycle
                        ),
                    ));
                }
            }
            if vfx_role == "PROJECTILE_BODY" {
                if trigger != "SPELL_RELEASE" {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' uses PROJECTILE_BODY outside SPELL_RELEASE",
                            cue.vfx_id
                        ),
                    ));
                }
                if attach_mode == "FOLLOW_ANCHOR" {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' PROJECTILE_BODY must not use FOLLOW_ANCHOR",
                            cue.vfx_id
                        ),
                    ));
                }
                if cue.start_delay_ms > 0 {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' PROJECTILE_BODY must not author start_delay_ms; projectile body visuals bind to active projectile runtime rows",
                            cue.vfx_id
                        ),
                    ));
                }
                let projectile_sequence_index = cue.projectile_sequence_index.unwrap_or(0);
                match owner_kind.as_str() {
                    "ABILITY" if !projectile_spell_abilities.contains_key(owner_id.as_str()) => {
                        errors.push(CombatAuthoringError::new(
                            CombatAuthoringRule::CombatVfxCueResolves,
                            format!(
                                "combat VFX cue '{}' PROJECTILE_BODY owner '{}:{}' must resolve to a spell ability with PROJECTILE delivery",
                                cue.vfx_id, cue.owner_kind, cue.owner_id
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
                                "combat VFX cue '{}' PROJECTILE_BODY owner '{}:{}' must resolve to a spell kind used by PROJECTILE delivery",
                                cue.vfx_id, cue.owner_kind, cue.owner_id
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
            if vfx_role == "TRAVEL_BODY" {
                if trigger != "SPELL_RELEASE" {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' uses TRAVEL_BODY outside SPELL_RELEASE",
                            cue.vfx_id
                        ),
                    ));
                }
                if attach_mode == "FOLLOW_ANCHOR" {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' TRAVEL_BODY must not use FOLLOW_ANCHOR",
                            cue.vfx_id
                        ),
                    ));
                }
                if effective_lifecycle != "UNTIL_TERMINAL_EVENT" {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' TRAVEL_BODY must use UNTIL_TERMINAL_EVENT",
                            cue.vfx_id
                        ),
                    ));
                }
                if cue.duration_ms != 0 {
                    errors.push(CombatAuthoringError::new(
                        CombatAuthoringRule::CombatVfxCueResolves,
                        format!(
                            "combat VFX cue '{}' TRAVEL_BODY must set duration_ms to 0",
                            cue.vfx_id
                        ),
                    ));
                }
            }
            if effective_vfx_role == "ONE_SHOT"
                && effective_lifecycle == "DURATION"
                && cue.duration_ms == 0
            {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' ONE_SHOT DURATION must define positive duration_ms",
                        cue.vfx_id
                    ),
                ));
            }
            if anchor == "TARGET" && matches!(trigger.as_str(), "SPELL_CAST" | "SPELL_RELEASE") {
                errors.push(CombatAuthoringError::new(
                    CombatAuthoringRule::CombatVfxCueResolves,
                    format!(
                        "combat VFX cue '{}' uses TARGET anchor on {}; TARGET is only valid once an impact/block/parry/fizzle target is known",
                        cue.vfx_id, trigger
                    ),
                ));
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
                normalize_identifier(cue.owner_id.as_str()) == "WARRIOR_FIREBALL"
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
    }

    #[test]
    fn projectile_body_vfx_resolution_is_single_selected_template() {
        assert_eq!(
            projectile_body_vfx_id_for_spell("WARRIOR_FIREBALL", "FIREBALL", 0).as_deref(),
            Some("VFX_FIREBALL_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("WARRIOR_ICICLE", "ICICLE", 0).as_deref(),
            Some("VFX_ICICLE_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("WARRIOR_GROUND_SLASH", "GROUND_SLASH", 0).as_deref(),
            Some("VFX_GROUND_SLASH_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("WARRIOR_BOOMERANG_ORB", "BOOMERANG_ORB", 0)
                .as_deref(),
            Some("VFX_BOOMERANG_ORB_PROJECTILE_01")
        );
        assert_eq!(
            projectile_body_vfx_id_for_spell("WARRIOR_FIREBALL", "FIREBALL", 1),
            None,
            "runtime should not silently select a visual for an unauthored projectile sequence"
        );
    }

    #[test]
    fn combat_vfx_manifest_prefers_ability_projectile_body_over_spell_fallback() {
        let catalog = serde_json::from_str::<super::ProgressionCatalogFile>(
            r#"{
                "auto_attacks": [],
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
                        "owner_id": "WARRIOR_FIREBALL",
                        "trigger": "SPELL_RELEASE",
                        "anchor": "RIGHT_HAND",
                        "vfx_id": "ABILITY_FIREBALL_PROJECTILE",
                        "attach_mode": "SPAWN_WORLD",
                        "vfx_role": "PROJECTILE_BODY",
                        "lifecycle": "UNTIL_TERMINAL_EVENT",
                        "projectile_sequence_index": 0,
                        "sort_order": 30
                    }
                ]
            }"#,
        )
        .expect("test catalog should parse");
        let manifest = CombatVfxPresentationManifest::build(&catalog);

        assert_eq!(
            manifest.projectile_body_vfx_id_for_spell("WARRIOR_FIREBALL", "FIREBALL", 0),
            Some("ABILITY_FIREBALL_PROJECTILE")
        );
        assert_eq!(
            manifest.projectile_body_vfx_id_for_spell("OTHER_FIREBALL", "FIREBALL", 0),
            Some("SPELL_FIREBALL_PROJECTILE")
        );
    }

    #[test]
    fn stale_movement_delivery_json_key_is_rejected() {
        let json = r#"{
            "ability_id": "BAD_CHARGE",
            "class_id": "WARRIOR",
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
                "abilities": [
                    {
                        "ability_id": "TEST_STATUS_ABILITY",
                        "class_id": "WARRIOR",
                        "action_id": "TEST_STATUS",
                        "display_name": "Test Status",
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
                "combat_vfx_cues": [
                    {
                        "owner_kind": "ABILITY",
                        "owner_id": "WARRIOR_FIREBALL",
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
                normalize_identifier(cue.owner_id.as_str()) == "WARRIOR_FROST_NOVA"
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
    fn meteor_vfx_uses_travel_body_and_impact_prefab() {
        let catalog = progression_catalog();
        let meteor_cues: Vec<_> = catalog
            .combat_vfx_cues
            .iter()
            .filter(|cue| normalize_identifier(cue.owner_id.as_str()) == "WARRIOR_METEOR")
            .collect();

        assert_eq!(
            meteor_cues.len(),
            2,
            "Meteor should author one travel body and one impact cue"
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
            matches!(prefix, Some("slot") | Some("fixed")),
            "slot id '{slot_id}' must start with slot_ or fixed_"
        );
        assert!(parts
            .next()
            .and_then(|part| part.parse::<u32>().ok())
            .is_some());
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

        let mut class_ids = HashSet::new();
        for definition in &catalog.classes {
            assert!(!definition.class_id.trim().is_empty());
            assert!(class_ids.insert(definition.class_id.clone()));
            assert!(definition.max_saved_specs > 0);
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
            "applies_stagger",
            "range",
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
        let supported_kinds = ["LINEAR", "LEAP", "TELEPORT", "TELEPORT_BEHIND"];
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
            if !matches!(kind.as_str(), "TELEPORT" | "TELEPORT_BEHIND") {
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
    fn progression_auto_attacks_cover_every_combat_profile() {
        let catalog = progression_catalog();
        let auto_attack_profiles: HashSet<_> = catalog
            .auto_attacks
            .iter()
            .map(|row| normalize_identifier(row.combat_profile_id.as_str()))
            .collect();

        for profile in &catalog.combat_profiles {
            assert!(
                auto_attack_profiles
                    .contains(normalize_identifier(profile.combat_profile_id.as_str()).as_str()),
                "combat profile '{}' is missing auto_attacks[] gameplay",
                profile.combat_profile_id
            );
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
                .any(|tag| normalize_identifier(tag.as_str()) == "LOADOUT_ACTION"),
            "Archer draw mode toggle should be a loadout action"
        );
        assert!(
            catalog
                .default_loadout_assignments
                .iter()
                .any(|assignment| {
                    normalize_identifier(assignment.class_id.as_str()) == "RANGER"
                        && canonical_loadout_slot_id(assignment.slot_id.as_str()) == "SLOT_1_1"
                        && normalize_identifier(assignment.ability_id.as_str())
                            == ARCHER_DRAW_MODE_TOGGLE_ABILITY_ID
                }),
            "Archer draw mode toggle should have an action-bar slot assignment"
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

        assert_eq!(ability.class_id, "WARRIOR");
        assert_eq!(ability.action_id, "WARRIOR_MAIM");
        assert_eq!(ability_gameplay_kind(ability), "MELEE");
        assert_eq!(ability.gameplay.base_damage, Some(10));
        assert_eq!(ability.gameplay.applies_stagger, Some(false));
        assert!(!progression_catalog()
            .default_loadout_assignments
            .iter()
            .any(
                |assignment| normalize_identifier(assignment.ability_id.as_str()) == "WARRIOR_MAIM"
            ));
        assert_eq!(
            melee_impact_effects_for_ability_id("WARRIOR_MAIM"),
            vec![MeleeImpactEffectRuntime {
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
    fn charge_abilities_author_melee_gap_close() {
        let catalog = progression_catalog();

        for ability_id in ["WARRIOR_CHARGE", "WARRIOR_IMPALE", "PALADIN_CHARGE"] {
            let ability = catalog
                .abilities
                .iter()
                .find(|ability| normalize_identifier(ability.ability_id.as_str()) == ability_id)
                .expect("charge ability must exist");
            assert!(
                normalize_identifier(ability.fixed_action_id.as_str()).is_empty(),
                "charge ability '{}' must be selectable directly, not fixed-action backed",
                ability.ability_id
            );
            assert_eq!(
                ability_gameplay_kind(ability),
                "MELEE",
                "charge ability '{}' must be a melee ability",
                ability.ability_id
            );
            assert_eq!(ability.gameplay.base_damage, Some(32));
            assert_eq!(ability.gameplay.applies_stagger, Some(false));
            assert_eq!(ability.gameplay.range, Some(18.0));
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
                vec![MeleeImpactEffectRuntime {
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
    }

    #[test]
    fn warrior_charge_grants_primary_resource_on_accept_only() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_CHARGE")
            .expect("expected Warrior Charge ability");

        assert_eq!(normalize_identifier(ability.fixed_action_id.as_str()), "");
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
    fn stat_wire_identifiers_are_stable() {
        assert_eq!(StatKind::Might.as_str(), "MIGHT");
        assert_eq!(StatKind::Insight.as_str(), "INSIGHT");
        assert_eq!(StatKind::Finesse.as_str(), "FINESSE");
        assert_eq!(StatKind::Quickness.as_str(), "QUICKNESS");
        assert_eq!(StatKind::Fortitude.as_str(), "FORTITUDE");
    }

    #[test]
    fn saved_spec_total_for_stat_sums_matching_allocations_only() {
        let allocations = vec![
            SavedSpecStatAllocation {
                key: "spec:MIGHT".to_string(),
                spec_id: "spec".to_string(),
                stat_kind: "MIGHT".to_string(),
                allocated_points: 7,
            },
            SavedSpecStatAllocation {
                key: "spec:INSIGHT".to_string(),
                spec_id: "spec".to_string(),
                stat_kind: "INSIGHT".to_string(),
                allocated_points: 11,
            },
            SavedSpecStatAllocation {
                key: "spec:INSIGHT_2".to_string(),
                spec_id: "spec".to_string(),
                stat_kind: "INSIGHT".to_string(),
                allocated_points: 4,
            },
        ];

        assert_eq!(saved_spec_total_for_stat(&allocations, StatKind::Might), 7);
        assert_eq!(
            saved_spec_total_for_stat(&allocations, StatKind::Insight),
            15
        );
        assert_eq!(
            saved_spec_total_for_stat(&allocations, StatKind::Fortitude),
            0
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
        assert_eq!(canonical_loadout_slot_id("bottom_01"), "SLOT_0_0");
        assert_eq!(canonical_loadout_slot_id("BOTTOM_08"), "SLOT_0_7");
        assert_eq!(canonical_loadout_slot_id("BOTTOM_09"), "SLOT_0_8");
        assert_eq!(canonical_loadout_slot_id("slot_1_0"), "SLOT_1_0");
    }

    #[test]
    fn warrior_whirlwind_resolves_via_derived_greatsword_profile() {
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
        let combat_profile_id = derived_combat_profile_id_for_class("WARRIOR")
            .expect("Warrior default combat profile must resolve");

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
            combat_profile_id.as_str(),
            &AuthoredActionId::new(ability.action_id.as_str())
        ));
    }

    #[test]
    fn warrior_skyfall_2_authors_targetless_cone_area_vfx() {
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_SKYFALL_2")
            .expect("expected Skyfall 2 ability");
        let targeting = resolved_melee_targeting_for_catalog(&ability.gameplay);
        assert_eq!(targeting.kind, "CASTER_CONE");
        assert!(!targeting.requires_target);
        assert_eq!(targeting.range, 11.5);
        assert_eq!(targeting.angle_degrees, 65.0);

        let cue = progression_catalog()
            .combat_vfx_cues
            .iter()
            .find(|cue| {
                normalize_identifier(cue.owner_id.as_str()) == "WARRIOR_SKYFALL_2"
                    && normalize_identifier(cue.trigger.as_str()) == "AREA_IMPACT"
            })
            .expect("Skyfall 2 should author an area-impact VFX cue");
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
            .find(|ability| ability.ability_id == "WARRIOR_ICE_SPIKES")
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
                normalize_identifier(cue.owner_id.as_str()) == "WARRIOR_ICE_SPIKES"
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
    fn warrior_sunder_and_cleave_resolve_via_derived_greatsword_profile() {
        let combat_profile_id = derived_combat_profile_id_for_class("WARRIOR")
            .expect("Warrior default combat profile must resolve");

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
                combat_profile_id.as_str(),
                &AuthoredActionId::new(ability.action_id.as_str())
            ));
        }
    }

    #[test]
    fn warrior_dread_strike_resolves_as_auto_attack_replacement() {
        let combat_profile_id = derived_combat_profile_id_for_class("WARRIOR")
            .expect("Warrior default combat profile must resolve");
        let ability = progression_catalog()
            .abilities
            .iter()
            .find(|ability| ability.ability_id == "WARRIOR_DREAD_STRIKE")
            .expect("expected Warrior Dread Strike ability");
        let replacement = progression_catalog()
            .auto_attack_replacements
            .iter()
            .find(|replacement| replacement.replacement_id == ability.action_id)
            .expect("Dread Strike must resolve to replacement tuning");

        assert_eq!(ability_gameplay_kind(ability), "AUTO_ATTACK_REPLACEMENT");
        assert_eq!(
            normalize_identifier(replacement.combat_profile_id.as_str()),
            combat_profile_id
        );
        assert!(profile_supports_action_reference(
            combat_profile_id.as_str(),
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
            .default_loadout_assignments
            .iter()
            .any(|assignment| {
                assignment.action_kind == "ABILITY" && assignment.ability_id == "WARRIOR_FORTIFY"
            }));
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
    fn warrior_self_buffs_have_greatsword_spell_animation_entries() {
        let combat_profile_id = derived_combat_profile_id_for_class("WARRIOR")
            .expect("Warrior default combat profile must resolve");
        let animation_set_spell_ids = spell_ids_for_combat_profile(combat_profile_id.as_str());

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
    }

    #[test]
    fn class_default_combat_profile_derivation_is_stable() {
        assert_eq!(
            derived_combat_profile_id_for_class("WARRIOR").as_deref(),
            Some("TWO_HANDED_SWORD")
        );
        assert_eq!(
            derived_combat_profile_id_for_class("PALADIN").as_deref(),
            Some("SWORD_AND_SHIELD")
        );
        assert_eq!(
            derived_combat_profile_id_for_class("WARRIOR").as_deref(),
            Some("TWO_HANDED_SWORD")
        );
        assert_eq!(
            derived_combat_profile_id_for_class("PALADIN").as_deref(),
            Some("SWORD_AND_SHIELD")
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

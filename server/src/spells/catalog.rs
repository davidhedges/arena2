use std::collections::HashSet;
use std::sync::OnceLock;
use std::time::Duration;

use serde::{de, Deserialize, Deserializer, Serialize};

use crate::combat::scene_query::CombatAreaShape;
use crate::combat::{
    AuthoredStatusPayload, DamageType, StackPolicy, StatusApplication, StatusDispelType,
    StatusEffectKind, StatusPayload, StatusPolarity, StatusStackGroupDefault,
};
use crate::progression::default_global_cooldown_ms;
use crate::relations::{default_spell_target_audience, TargetAudience};

use super::manifest::{
    ApplyStatusDefinition, ApplyStatusSecondaryTunables, AreaSecondaryTunables,
    AuraSecondaryTunables, BespokeRuntimeSpell, BlockBehavior, BoomerangCasterProjectileTunables,
    ConsumeStatusSecondaryTunables, CurvedTargetProjectileTunables, DirectTargetSecondaryTunables,
    ImpactEffect, InstantBeamChargeScaling, InstantBeamSecondaryTunables, MeteorSkyOrigin,
    OrbitCasterProjectileTunables, ProjectileMotionTunables, ProjectileSecondaryTunables,
    RemoveStatusDefinition, RemoveStatusSecondaryTunables, SpellBehavior, SpellCastMobility,
    SpellDefinition, SpellId, SpellParryBehavior, SpellSecondaryTunables, SpellTargeting,
    SPELL_METEOR,
};

const PROGRESSION_CATALOG_JSON: &str = include_str!("../progression_catalog.shared.json");

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct SpellCatalogRow {
    kind: SpellId,
    cooldown_ms: u64,
    uses_global_cooldown: bool,
    global_cooldown_ms: u64,
    cast_time_ms: u64,
    cast_mobility: SpellCastMobility,
    targeting: SpellTargeting,
    #[serde(default)]
    target_audience: Option<TargetAudience>,
    requires_target: bool,
    #[serde(default)]
    requires_target_los: Option<bool>,
    #[serde(default)]
    aim_radius: Option<f32>,
    #[serde(default)]
    resource_cost: f32,
    #[serde(default)]
    primary_resource_gain_on_cast: f32,
    #[serde(default)]
    arms_auto_attack_on_cast: bool,
    delivery: SpellCatalogDelivery,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(tag = "kind", rename_all = "SCREAMING_SNAKE_CASE", deny_unknown_fields)]
enum SpellCatalogDelivery {
    DirectTarget {
        max_distance: f32,
        damage: i32,
        #[serde(default)]
        damage_type: String,
        block_behavior: BlockBehavior,
        #[serde(default)]
        parry_behavior: Option<SpellParryBehavior>,
        #[serde(default)]
        impact_effects: Vec<ImpactEffectRow>,
    },
    Projectile {
        #[serde(flatten)]
        projectile: ProjectileTuningRow,
        #[serde(default)]
        max_distance: f32,
        damage: i32,
        #[serde(default)]
        damage_type: String,
        block_behavior: BlockBehavior,
    },
    Area {
        #[serde(default)]
        speed: f32,
        #[serde(default)]
        max_distance: f32,
        #[serde(default)]
        damage: i32,
        #[serde(default)]
        damage_type: String,
        #[serde(default)]
        impact_delay_ms: u64,
        #[serde(default)]
        spawn_forward: f32,
        #[serde(default)]
        spawn_height: f32,
        #[serde(default)]
        duration_seconds: f32,
        #[serde(default)]
        radius: Option<f32>,
        #[serde(default)]
        shape: Option<AreaShapeRow>,
        #[serde(default)]
        projectile_radius: f32,
        block_behavior: BlockBehavior,
        #[serde(default)]
        sky_origin: Option<MeteorSkyOriginRow>,
        #[serde(default)]
        impact_effects: Vec<ImpactEffectRow>,
    },
    InstantBeam {
        max_distance: f32,
        damage: i32,
        #[serde(default)]
        damage_type: String,
        block_behavior: BlockBehavior,
        #[serde(default)]
        charge_scaling: Option<InstantBeamChargeScalingRow>,
    },
    Channel {
        max_distance: f32,
        damage: i32,
        #[serde(default)]
        damage_type: String,
        #[serde(default)]
        resource_cost_per_second: f32,
        update_interval_seconds: f32,
        duration_seconds: f32,
        block_behavior: BlockBehavior,
        #[serde(default)]
        projectile: Option<ProjectileTuningRow>,
    },
    ApplyStatus {
        duration_ms: u64,
        #[serde(default)]
        max_distance: f32,
        #[serde(default)]
        radius: f32,
        #[serde(default)]
        status_stack_group: Option<String>,
        polarity: StatusPolarity,
        #[serde(default = "default_unblockable")]
        block_behavior: BlockBehavior,
        #[serde(default)]
        parry_behavior: Option<SpellParryBehavior>,
        status: ApplyStatusDefinition,
    },
    RemoveStatus {
        #[serde(default)]
        statuses: Vec<RemoveStatusRow>,
        #[serde(default)]
        max_distance: f32,
        #[serde(default)]
        max_count: u32,
        #[serde(default)]
        polarity: Option<StatusPolarity>,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
    ConsumeStatus {
        max_distance: f32,
        #[serde(default)]
        max_count: u32,
        polarity: StatusPolarity,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
        heal_per_stack: i32,
    },
    Aura {
        radius: f32,
        tick_interval_ms: u64,
        #[serde(default)]
        effects: Vec<ImpactEffectRow>,
    },
    SelfResource {},
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct RemoveStatusRow {
    kind: StatusEffectKind,
    #[serde(default)]
    stack_group: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct ProjectileTuningRow {
    #[serde(default)]
    speed: f32,
    #[serde(default)]
    spawn_forward: f32,
    #[serde(default)]
    spawn_height: f32,
    #[serde(default)]
    turn_rate: f32,
    #[serde(default)]
    update_interval_seconds: f32,
    #[serde(default)]
    radius: f32,
    #[serde(default)]
    parry_behavior: Option<SpellParryBehavior>,
    #[serde(default)]
    homing_window_seconds: f32,
    #[serde(default)]
    motion: ProjectileMotionRow,
    #[serde(default)]
    impact_effects: Vec<ImpactEffectRow>,
    #[serde(default)]
    terrain_conforming: bool,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(tag = "kind", rename_all = "SCREAMING_SNAKE_CASE", deny_unknown_fields)]
enum AreaShapeRow {
    CasterCone {
        angle_degrees: f32,
        vertical_tolerance: f32,
    },
}

#[derive(Clone, Debug, Serialize)]
#[serde(tag = "kind", rename_all = "SCREAMING_SNAKE_CASE", deny_unknown_fields)]
enum ProjectileMotionRow {
    Linear {},
    CurvedTarget {
        arc_direction_degrees_min: f32,
        arc_direction_degrees_max: f32,
        arc_amplitude_min: f32,
        arc_amplitude_max: f32,
        control_point_fraction: f32,
    },
    OrbitCaster {
        projectile_count: u32,
        orbit_radius: f32,
        orbit_height: f32,
        angular_speed_deg_per_sec: f32,
        lifetime_seconds: f32,
        hit_radius: f32,
        hit_cooldown_seconds: f32,
        max_hits_per_target: u32,
        #[serde(default)]
        phase_offset_deg: f32,
    },
    BoomerangCaster {
        outbound_distance: f32,
        return_speed: f32,
        lifetime_seconds: f32,
        hit_radius: f32,
        hit_cooldown_seconds: f32,
        max_hits_per_target: u32,
    },
}

impl<'de> Deserialize<'de> for ProjectileMotionRow {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let value = serde_json::Value::deserialize(deserializer)?;
        let kind = value
            .get("kind")
            .and_then(serde_json::Value::as_str)
            .ok_or_else(|| de::Error::custom("projectile motion must define kind"))?;
        match kind.to_ascii_uppercase().as_str() {
            "LINEAR" => {
                let _: LinearProjectileMotionRow =
                    serde_json::from_value(value).map_err(de::Error::custom)?;
                Ok(Self::Linear {})
            }
            "CURVED_TARGET" => {
                let row: CurvedTargetProjectileMotionRow =
                    serde_json::from_value(value).map_err(de::Error::custom)?;
                Ok(Self::CurvedTarget {
                    arc_direction_degrees_min: row.arc_direction_degrees_min,
                    arc_direction_degrees_max: row.arc_direction_degrees_max,
                    arc_amplitude_min: row.arc_amplitude_min,
                    arc_amplitude_max: row.arc_amplitude_max,
                    control_point_fraction: row.control_point_fraction,
                })
            }
            "ORBIT_CASTER" => {
                let row: OrbitCasterProjectileMotionRow =
                    serde_json::from_value(value).map_err(de::Error::custom)?;
                Ok(Self::OrbitCaster {
                    projectile_count: row.projectile_count,
                    orbit_radius: row.orbit_radius,
                    orbit_height: row.orbit_height,
                    angular_speed_deg_per_sec: row.angular_speed_deg_per_sec,
                    lifetime_seconds: row.lifetime_seconds,
                    hit_radius: row.hit_radius,
                    hit_cooldown_seconds: row.hit_cooldown_seconds,
                    max_hits_per_target: row.max_hits_per_target,
                    phase_offset_deg: row.phase_offset_deg,
                })
            }
            "BOOMERANG_CASTER" => {
                let row: BoomerangCasterProjectileMotionRow =
                    serde_json::from_value(value).map_err(de::Error::custom)?;
                Ok(Self::BoomerangCaster {
                    outbound_distance: row.outbound_distance,
                    return_speed: row.return_speed,
                    lifetime_seconds: row.lifetime_seconds,
                    hit_radius: row.hit_radius,
                    hit_cooldown_seconds: row.hit_cooldown_seconds,
                    max_hits_per_target: row.max_hits_per_target,
                })
            }
            _ => Err(de::Error::custom(format!(
                "unknown projectile motion kind '{kind}'"
            ))),
        }
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LinearProjectileMotionRow {
    #[serde(rename = "kind")]
    _kind: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct CurvedTargetProjectileMotionRow {
    #[serde(rename = "kind")]
    _kind: String,
    arc_direction_degrees_min: f32,
    arc_direction_degrees_max: f32,
    arc_amplitude_min: f32,
    arc_amplitude_max: f32,
    control_point_fraction: f32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct OrbitCasterProjectileMotionRow {
    #[serde(rename = "kind")]
    _kind: String,
    projectile_count: u32,
    orbit_radius: f32,
    orbit_height: f32,
    angular_speed_deg_per_sec: f32,
    lifetime_seconds: f32,
    hit_radius: f32,
    hit_cooldown_seconds: f32,
    max_hits_per_target: u32,
    #[serde(default)]
    phase_offset_deg: f32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct BoomerangCasterProjectileMotionRow {
    #[serde(rename = "kind")]
    _kind: String,
    outbound_distance: f32,
    return_speed: f32,
    lifetime_seconds: f32,
    hit_radius: f32,
    hit_cooldown_seconds: f32,
    max_hits_per_target: u32,
}

impl Default for ProjectileMotionRow {
    fn default() -> Self {
        Self::Linear {}
    }
}

fn default_unblockable() -> BlockBehavior {
    BlockBehavior::Unblockable
}

fn default_one_stack() -> u32 {
    1
}

fn default_refresh_stack_policy() -> StackPolicy {
    StackPolicy::Refresh
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct MeteorSkyOriginRow {
    height: f32,
    drift_x: f32,
    drift_z: f32,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct InstantBeamChargeScalingRow {
    min_damage_scale: f32,
    max_charges: u32,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(tag = "kind", rename_all = "SCREAMING_SNAKE_CASE", deny_unknown_fields)]
enum ImpactEffectRow {
    Burn {
        duration_ms: u64,
        tick_interval_ms: u64,
        tick_damage: i32,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
    Stun {
        duration_ms: u64,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
    Freeze {
        duration_ms: u64,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
    Knockdown {
        duration_ms: u64,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
    Stagger {
        duration_ms: u64,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
    Root {
        duration_ms: u64,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
    Intimidated {
        duration_ms: u64,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
    },
    Slow {
        duration_ms: u64,
        slow_pct: f32,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default)]
        dispel_types: Vec<StatusDispelType>,
        #[serde(default = "default_one_stack")]
        max_stacks: u32,
        #[serde(default = "default_refresh_stack_policy")]
        stack_policy: StackPolicy,
    },
    MoveSpeed {
        duration_ms: u64,
        modifier_scalar: f32,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default = "default_one_stack")]
        max_stacks: u32,
        #[serde(default = "default_refresh_stack_policy")]
        stack_policy: StackPolicy,
    },
    ManaRegen {
        duration_ms: u64,
        modifier_scalar: f32,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default = "default_one_stack")]
        max_stacks: u32,
        #[serde(default = "default_refresh_stack_policy")]
        stack_policy: StackPolicy,
    },
    StaminaRegen {
        duration_ms: u64,
        modifier_scalar: f32,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default = "default_one_stack")]
        max_stacks: u32,
        #[serde(default = "default_refresh_stack_policy")]
        stack_policy: StackPolicy,
    },
    MagicResistance {
        duration_ms: u64,
        modifier_scalar: f32,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default = "default_one_stack")]
        max_stacks: u32,
        #[serde(default = "default_refresh_stack_policy")]
        stack_policy: StackPolicy,
    },
    Thorns {
        duration_ms: u64,
        tick_damage: i32,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default = "default_one_stack")]
        max_stacks: u32,
        #[serde(default = "default_refresh_stack_policy")]
        stack_policy: StackPolicy,
    },
    VengeanceAura {
        duration_ms: u64,
        #[serde(default)]
        status_stack_group: Option<String>,
        #[serde(default = "default_one_stack")]
        max_stacks: u32,
        #[serde(default = "default_refresh_stack_policy")]
        stack_policy: StackPolicy,
    },
}

pub(super) fn spell_definitions() -> &'static [SpellDefinition] {
    static DEFINITIONS: OnceLock<Vec<SpellDefinition>> = OnceLock::new();
    DEFINITIONS
        .get_or_init(|| {
            load_spell_definitions().unwrap_or_else(|err| {
                panic!("invalid spell catalog in progression_catalog.shared.json: {err}")
            })
        })
        .as_slice()
}

pub(super) fn spell_definition(kind: &SpellId) -> Option<&'static SpellDefinition> {
    spell_definitions()
        .iter()
        .find(|definition| &definition.kind == kind)
}

pub(crate) fn spell_definition_by_str(kind: &str) -> Option<&'static SpellDefinition> {
    let Ok(kind) = SpellId::new(kind) else {
        return None;
    };
    spell_definition(&kind)
}

pub(crate) fn require_spell_definition_by_str(
    kind: &str,
) -> Result<&'static SpellDefinition, String> {
    let kind = SpellId::new(kind).map_err(|err| format!("Invalid spell id '{kind}': {err}"))?;
    spell_definition(&kind).ok_or_else(|| format!("Unknown spell '{}'", kind.as_str()))
}

fn load_spell_definitions() -> Result<Vec<SpellDefinition>, String> {
    definitions_from_rows(spell_rows_from_json(PROGRESSION_CATALOG_JSON)?)
}

fn spell_rows_from_json(json: &str) -> Result<Vec<SpellCatalogRow>, String> {
    let value: serde_json::Value = serde_json::from_str(json)
        .map_err(|err| format!("failed to parse shared progression catalog: {err}"))?;

    spell_gameplay_spell_rows_from_value(&value)
}

#[derive(Clone, Debug, Deserialize)]
struct SpellAbilityCatalogRow {
    ability_id: String,
    gameplay: AbilityGameplayCatalogRow,
    action_id: String,
    #[serde(default)]
    resource_cost: f32,
}

#[derive(Clone, Debug, Deserialize)]
struct SpellGameplayCatalogRow {
    cooldown_ms: u64,
    uses_global_cooldown: bool,
    global_cooldown_ms: u64,
    cast_time_ms: u64,
    cast_mobility: SpellCastMobility,
    targeting: SpellTargeting,
    #[serde(default)]
    target_audience: Option<TargetAudience>,
    requires_target: bool,
    #[serde(default)]
    requires_target_los: Option<bool>,
    #[serde(default)]
    aim_radius: Option<f32>,
    #[serde(default)]
    resource_cost: f32,
    #[serde(default)]
    primary_resource_gain_on_cast: f32,
    #[serde(default)]
    arms_auto_attack_on_cast: bool,
    delivery: SpellCatalogDelivery,
}

#[derive(Clone, Debug, Deserialize)]
struct AbilityGameplayCatalogRow {
    kind: String,
    #[serde(default)]
    cooldown_ms: Option<u64>,
    #[serde(default)]
    uses_global_cooldown: Option<bool>,
    #[serde(default)]
    global_cooldown_ms: Option<u64>,
    #[serde(default)]
    cast_time_ms: Option<u64>,
    #[serde(default)]
    cast_mobility: Option<SpellCastMobility>,
    #[serde(default)]
    targeting: Option<SpellTargeting>,
    #[serde(default)]
    target_audience: Option<TargetAudience>,
    #[serde(default)]
    requires_target: Option<bool>,
    #[serde(default)]
    requires_target_los: Option<bool>,
    #[serde(default)]
    aim_radius: Option<f32>,
    #[serde(default)]
    resource_cost: f32,
    #[serde(default)]
    primary_resource_gain_on_cast: f32,
    #[serde(default)]
    arms_auto_attack_on_cast: bool,
    #[serde(default)]
    delivery: Option<serde_json::Value>,
}

fn normalize_catalog_identifier(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

fn catalog_ability_kind(gameplay: &AbilityGameplayCatalogRow) -> String {
    normalize_catalog_identifier(gameplay.kind.as_str())
}

fn require_spell_gameplay_field<T>(
    value: Option<T>,
    ability_id: &str,
    field_name: &str,
) -> Result<T, String> {
    value.ok_or_else(|| format!("spell ability '{ability_id}' must define gameplay.{field_name}"))
}

fn resolve_spell_global_cooldown_ms(
    ability_id: &str,
    uses_global_cooldown: bool,
    authored_global_cooldown_ms: Option<u64>,
) -> Result<u64, String> {
    if !uses_global_cooldown {
        if authored_global_cooldown_ms.is_some() {
            return Err(format!(
                "spell ability '{ability_id}' must only define gameplay.global_cooldown_ms when uses_global_cooldown is true"
            ));
        }
        return Ok(0);
    }
    let global_cooldown_ms = authored_global_cooldown_ms.unwrap_or_else(default_global_cooldown_ms);
    if global_cooldown_ms == 0 {
        return Err(format!(
            "spell ability '{ability_id}' gameplay.global_cooldown_ms must be positive"
        ));
    }
    Ok(global_cooldown_ms)
}

impl AbilityGameplayCatalogRow {
    fn into_spell_gameplay(self, ability_id: &str) -> Result<SpellGameplayCatalogRow, String> {
        let uses_global_cooldown = require_spell_gameplay_field(
            self.uses_global_cooldown,
            ability_id,
            "uses_global_cooldown",
        )?;
        Ok(SpellGameplayCatalogRow {
            cooldown_ms: require_spell_gameplay_field(self.cooldown_ms, ability_id, "cooldown_ms")?,
            uses_global_cooldown,
            global_cooldown_ms: resolve_spell_global_cooldown_ms(
                ability_id,
                uses_global_cooldown,
                self.global_cooldown_ms,
            )?,
            cast_time_ms: require_spell_gameplay_field(
                self.cast_time_ms,
                ability_id,
                "cast_time_ms",
            )?,
            cast_mobility: require_spell_gameplay_field(
                self.cast_mobility,
                ability_id,
                "cast_mobility",
            )?,
            targeting: require_spell_gameplay_field(self.targeting, ability_id, "targeting")?,
            target_audience: self.target_audience,
            requires_target: require_spell_gameplay_field(
                self.requires_target,
                ability_id,
                "requires_target",
            )?,
            requires_target_los: self.requires_target_los,
            aim_radius: self.aim_radius,
            resource_cost: self.resource_cost,
            primary_resource_gain_on_cast: self.primary_resource_gain_on_cast,
            arms_auto_attack_on_cast: self.arms_auto_attack_on_cast,
            delivery: serde_json::from_value(require_spell_gameplay_field(
                self.delivery,
                ability_id,
                "delivery",
            )?)
            .map_err(|err| {
                format!("spell ability '{ability_id}' has invalid gameplay.delivery: {err}")
            })?,
        })
    }
}

fn spell_gameplay_spell_rows_from_value(
    value: &serde_json::Value,
) -> Result<Vec<SpellCatalogRow>, String> {
    let Some(ability_rows) = value
        .get("abilities")
        .and_then(|abilities| abilities.as_array())
    else {
        return Ok(Vec::new());
    };

    let mut rows = Vec::new();
    for (index, row) in ability_rows.iter().enumerate() {
        let ability = serde_json::from_value::<SpellAbilityCatalogRow>(row.clone())
            .map_err(|err| format!("invalid abilities[{index}] spell row: {err}"))?;
        if catalog_ability_kind(&ability.gameplay) != "SPELL" {
            continue;
        }
        let delivery = ability
            .gameplay
            .clone()
            .into_spell_gameplay(ability.ability_id.as_str())?;
        rows.push(delivery.into_spell_row(ability)?);
    }
    Ok(rows)
}

impl SpellGameplayCatalogRow {
    fn into_spell_row(self, ability: SpellAbilityCatalogRow) -> Result<SpellCatalogRow, String> {
        let authored_spell_resource_cost = if self.resource_cost > 0.0 {
            self.resource_cost
        } else {
            ability.resource_cost
        };
        let resource_cost = match &self.delivery {
            SpellCatalogDelivery::Channel {
                resource_cost_per_second,
                ..
            } => {
                if authored_spell_resource_cost > 0.0 {
                    return Err(format!(
                        "spell ability '{}' CHANNEL delivery must author resource_cost_per_second instead of gameplay.resource_cost",
                        ability.ability_id
                    ));
                }
                *resource_cost_per_second
            }
            _ => authored_spell_resource_cost,
        };
        Ok(SpellCatalogRow {
            kind: SpellId::new(ability.action_id.as_str()).map_err(|err| {
                format!(
                    "spell ability '{}' action_id '{}' is not a valid runtime spell id: {err}",
                    ability.ability_id, ability.action_id
                )
            })?,
            cooldown_ms: self.cooldown_ms,
            uses_global_cooldown: self.uses_global_cooldown,
            global_cooldown_ms: self.global_cooldown_ms,
            cast_time_ms: self.cast_time_ms,
            cast_mobility: self.cast_mobility,
            targeting: self.targeting,
            target_audience: self.target_audience,
            requires_target: self.requires_target,
            requires_target_los: self.requires_target_los,
            aim_radius: self.aim_radius,
            resource_cost,
            primary_resource_gain_on_cast: self.primary_resource_gain_on_cast,
            arms_auto_attack_on_cast: self.arms_auto_attack_on_cast,
            delivery: self.delivery,
        })
    }
}

fn definitions_from_rows(rows: Vec<SpellCatalogRow>) -> Result<Vec<SpellDefinition>, String> {
    if rows.is_empty() {
        return Err("runtime spell catalog must derive at least one spell".to_string());
    }

    let mut seen = HashSet::new();
    let mut definitions = Vec::with_capacity(rows.len());
    for row in rows {
        let kind = row.kind.as_str().to_string();
        if !seen.insert(kind.clone()) {
            return Err(format!("duplicate spell row for {kind}"));
        }
        let definition = row.into_definition()?;
        validate_definition(&definition)?;
        definitions.push(definition);
    }

    Ok(definitions)
}

impl SpellCatalogRow {
    fn into_definition(self) -> Result<SpellDefinition, String> {
        let spell_id = self.kind.as_str().to_string();
        let mut definition = SpellDefinition {
            kind: self.kind,
            cooldown: Duration::from_millis(self.cooldown_ms),
            uses_global_cooldown: self.uses_global_cooldown,
            global_cooldown: Duration::from_millis(self.global_cooldown_ms),
            cast_time: Duration::from_millis(self.cast_time_ms),
            cast_mobility: self.cast_mobility,
            behavior: SpellBehavior::Projectile,
            targeting: self.targeting,
            target_audience: TargetAudience::Hostile,
            requires_target: self.requires_target,
            requires_target_los: self.requires_target_los.unwrap_or(true),
            aim_radius: self.aim_radius,
            speed: 0.0,
            max_distance: 0.0,
            damage: 0,
            damage_type: DamageType::Physical,
            spawn_forward: 0.0,
            spawn_height: 0.0,
            turn_rate: 0.0,
            update_interval: 0.0,
            duration: 0.0,
            radius: 0.0,
            projectile_radius: 0.0,
            status_stack_group: None,
            block_behavior: BlockBehavior::Blockable,
            primary_resource_cost: self.resource_cost,
            primary_resource_gain_on_cast: self.primary_resource_gain_on_cast,
            generates_primary_resource_on_cast: self.primary_resource_gain_on_cast > 0.0,
            arms_auto_attack_on_cast: self.arms_auto_attack_on_cast,
            apply_status: None,
            apply_status_polarity: None,
            secondary: SpellSecondaryTunables::default(),
        };

        match self.delivery {
            SpellCatalogDelivery::DirectTarget {
                max_distance,
                damage,
                damage_type,
                block_behavior,
                parry_behavior,
                impact_effects,
            } => {
                definition.behavior = SpellBehavior::DirectTarget;
                definition.max_distance = max_distance;
                definition.damage = damage;
                definition.damage_type = DamageType::from_wire(damage_type.as_str());
                definition.block_behavior = block_behavior;
                definition.secondary.direct_target = Some(DirectTargetSecondaryTunables {
                    parry_behavior: parry_behavior.unwrap_or(SpellParryBehavior::Unparryable),
                    impact_effects: impact_effects.into_iter().map(Into::into).collect(),
                });
            }
            SpellCatalogDelivery::Projectile {
                projectile,
                max_distance,
                damage,
                damage_type,
                block_behavior,
            } => {
                definition.behavior = SpellBehavior::Projectile;
                definition.damage = damage;
                definition.damage_type = DamageType::from_wire(damage_type.as_str());
                definition.spawn_forward = projectile.spawn_forward;
                definition.spawn_height = projectile.spawn_height;
                definition.turn_rate = projectile.turn_rate;
                definition.update_interval = projectile.update_interval_seconds;
                definition.block_behavior = block_behavior;
                let motion = ProjectileMotionTunables::from(projectile.motion);
                match &motion {
                    ProjectileMotionTunables::Linear => {
                        definition.speed = projectile.speed;
                        definition.max_distance = max_distance;
                        definition.radius = projectile.radius;
                    }
                    ProjectileMotionTunables::CurvedTarget(_) => {
                        definition.speed = projectile.speed;
                        definition.max_distance = max_distance;
                        definition.radius = projectile.radius;
                    }
                    ProjectileMotionTunables::OrbitCaster(orbit) => {
                        definition.speed = 0.0;
                        definition.max_distance = 0.0;
                        definition.duration = orbit.lifetime_seconds;
                        definition.radius = orbit.hit_radius;
                    }
                    ProjectileMotionTunables::BoomerangCaster(boomerang) => {
                        definition.speed = projectile.speed;
                        definition.max_distance = boomerang.outbound_distance;
                        definition.duration = boomerang.lifetime_seconds;
                        definition.radius = boomerang.hit_radius;
                    }
                }
                definition.secondary.projectile = Some(ProjectileSecondaryTunables {
                    motion,
                    parry_behavior: projectile
                        .parry_behavior
                        .unwrap_or(SpellParryBehavior::Unparryable),
                    homing_window_seconds: projectile.homing_window_seconds,
                    impact_effects: projectile
                        .impact_effects
                        .into_iter()
                        .map(Into::into)
                        .collect(),
                    terrain_conforming: projectile.terrain_conforming,
                });
            }
            SpellCatalogDelivery::Area {
                speed,
                max_distance,
                damage,
                damage_type,
                impact_delay_ms,
                spawn_forward,
                spawn_height,
                duration_seconds,
                radius,
                shape,
                projectile_radius,
                block_behavior,
                sky_origin,
                impact_effects,
            } => {
                let area_shape = resolve_area_shape(
                    spell_id.as_str(),
                    self.targeting,
                    self.requires_target,
                    radius,
                    shape,
                    max_distance,
                )?;
                definition.behavior = SpellBehavior::Area;
                definition.speed = speed;
                definition.max_distance = max_distance;
                definition.damage = damage;
                definition.damage_type = DamageType::from_wire(damage_type.as_str());
                definition.spawn_forward = spawn_forward;
                definition.spawn_height = spawn_height;
                definition.duration = duration_seconds;
                definition.radius = area_shape.query_radius();
                definition.projectile_radius = projectile_radius;
                definition.block_behavior = block_behavior;
                definition.secondary.area = Some(AreaSecondaryTunables {
                    impact_delay_ms,
                    sky_origin: sky_origin.map(Into::into),
                    shape: area_shape,
                    impact_effects: impact_effects.into_iter().map(Into::into).collect(),
                });
            }
            SpellCatalogDelivery::InstantBeam {
                max_distance,
                damage,
                damage_type,
                block_behavior,
                charge_scaling,
            } => {
                definition.behavior = SpellBehavior::InstantBeam;
                definition.max_distance = max_distance;
                definition.damage = damage;
                definition.damage_type = DamageType::from_wire(damage_type.as_str());
                definition.block_behavior = block_behavior;
                definition.secondary.instant_beam = Some(InstantBeamSecondaryTunables {
                    charge_scaling: charge_scaling.map(Into::into),
                });
            }
            SpellCatalogDelivery::Channel {
                max_distance,
                damage,
                damage_type,
                resource_cost_per_second: _,
                update_interval_seconds,
                duration_seconds,
                block_behavior,
                projectile,
            } => {
                definition.behavior = SpellBehavior::Channel;
                definition.max_distance = max_distance;
                definition.damage = damage;
                definition.damage_type = DamageType::from_wire(damage_type.as_str());
                definition.update_interval = update_interval_seconds;
                definition.duration = duration_seconds;
                definition.block_behavior = block_behavior;
                if let Some(projectile) = projectile {
                    definition.speed = projectile.speed;
                    definition.spawn_forward = projectile.spawn_forward;
                    definition.spawn_height = projectile.spawn_height;
                    definition.turn_rate = projectile.turn_rate;
                    definition.radius = projectile.radius;
                    definition.secondary.channel_projectile = Some(ProjectileSecondaryTunables {
                        motion: ProjectileMotionTunables::from(projectile.motion),
                        parry_behavior: projectile
                            .parry_behavior
                            .unwrap_or(SpellParryBehavior::Unparryable),
                        homing_window_seconds: projectile.homing_window_seconds,
                        impact_effects: projectile
                            .impact_effects
                            .into_iter()
                            .map(Into::into)
                            .collect(),
                        terrain_conforming: projectile.terrain_conforming,
                    });
                }
            }
            SpellCatalogDelivery::ApplyStatus {
                duration_ms,
                max_distance,
                radius,
                status_stack_group,
                polarity,
                block_behavior,
                parry_behavior,
                status,
            } => {
                definition.behavior = SpellBehavior::ApplyStatus;
                definition.duration = Duration::from_millis(duration_ms).as_secs_f32();
                definition.max_distance = max_distance;
                definition.radius = radius;
                definition.status_stack_group = status_stack_group;
                definition.block_behavior = block_behavior;
                definition.apply_status = Some(status);
                definition.apply_status_polarity = Some(polarity);
                definition.secondary.apply_status = Some(ApplyStatusSecondaryTunables {
                    parry_behavior: parry_behavior.unwrap_or(SpellParryBehavior::Unparryable),
                });
            }
            SpellCatalogDelivery::RemoveStatus {
                statuses,
                max_distance,
                max_count,
                polarity,
                dispel_types,
            } => {
                definition.behavior = SpellBehavior::RemoveStatus;
                definition.max_distance = max_distance;
                definition.block_behavior = BlockBehavior::Unblockable;
                definition.secondary.remove_status = Some(RemoveStatusSecondaryTunables {
                    statuses: statuses
                        .into_iter()
                        .map(|status| RemoveStatusDefinition {
                            kind: status.kind,
                            stack_group: status.stack_group,
                        })
                        .collect(),
                    max_count,
                    polarity,
                    dispel_types,
                });
            }
            SpellCatalogDelivery::ConsumeStatus {
                max_distance,
                max_count,
                polarity,
                dispel_types,
                heal_per_stack,
            } => {
                definition.behavior = SpellBehavior::ConsumeStatus;
                definition.max_distance = max_distance;
                definition.block_behavior = BlockBehavior::Unblockable;
                definition.secondary.consume_status = Some(ConsumeStatusSecondaryTunables {
                    max_count,
                    polarity: Some(polarity),
                    dispel_types,
                    heal_per_stack,
                });
            }
            SpellCatalogDelivery::Aura {
                radius,
                tick_interval_ms,
                effects,
            } => {
                definition.behavior = SpellBehavior::Aura;
                definition.radius = radius;
                definition.block_behavior = BlockBehavior::Unblockable;
                definition.secondary.aura = Some(AuraSecondaryTunables {
                    radius,
                    tick_interval: Duration::from_millis(tick_interval_ms),
                    effects: effects.into_iter().map(Into::into).collect(),
                });
            }
            SpellCatalogDelivery::SelfResource {} => {
                definition.behavior = SpellBehavior::SelfResource;
                definition.block_behavior = BlockBehavior::Unblockable;
            }
        }

        definition.target_audience = self.target_audience.unwrap_or_else(|| {
            default_spell_target_audience(
                definition.behavior,
                definition.targeting,
                definition.damage,
                definition.apply_status_polarity,
            )
        });

        Ok(definition)
    }
}

fn resolve_area_shape(
    spell_id: &str,
    targeting: SpellTargeting,
    requires_target: bool,
    radius: Option<f32>,
    shape: Option<AreaShapeRow>,
    max_distance: f32,
) -> Result<CombatAreaShape, String> {
    match shape {
        None => {
            let radius =
                radius.ok_or_else(|| format!("{spell_id} AREA delivery must define radius"))?;
            ensure_positive_f32(spell_id, "radius", radius)?;
            Ok(CombatAreaShape::Disc { radius })
        }
        Some(AreaShapeRow::CasterCone {
            angle_degrees,
            vertical_tolerance,
        }) => {
            if radius.is_some() {
                return Err(format!(
                    "{spell_id} CASTER_CONE AREA delivery must not define radius"
                ));
            }
            if targeting != SpellTargeting::Self_ || requires_target {
                return Err(format!(
                    "{spell_id} CASTER_CONE AREA delivery must use SELF targeting without a target requirement"
                ));
            }
            ensure_positive_f32(spell_id, "max_distance", max_distance)?;
            ensure_angle_degrees(spell_id, "angle_degrees", angle_degrees)?;
            ensure_positive_f32(spell_id, "vertical_tolerance", vertical_tolerance)?;
            Ok(CombatAreaShape::Cone {
                range: max_distance,
                angle_degrees,
                vertical_tolerance: Some(vertical_tolerance),
            })
        }
    }
}

impl From<MeteorSkyOriginRow> for MeteorSkyOrigin {
    fn from(row: MeteorSkyOriginRow) -> Self {
        Self {
            height: row.height,
            drift_x: row.drift_x,
            drift_z: row.drift_z,
        }
    }
}

impl From<InstantBeamChargeScalingRow> for InstantBeamChargeScaling {
    fn from(row: InstantBeamChargeScalingRow) -> Self {
        Self {
            min_damage_scale: row.min_damage_scale,
            max_charges: row.max_charges,
        }
    }
}

impl From<ImpactEffectRow> for ImpactEffect {
    fn from(row: ImpactEffectRow) -> Self {
        match row {
            ImpactEffectRow::Burn {
                duration_ms,
                tick_interval_ms,
                tick_damage,
                status_stack_group,
                dispel_types,
            } => StatusApplication::new(
                AuthoredStatusPayload::new(
                    StatusEffectKind::Dot,
                    0.0,
                    tick_damage,
                    0,
                    tick_interval_ms,
                    0.0,
                )
                .payload(),
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::InstanceScopedActionSuffix("BURN"),
                1,
                StackPolicy::Refresh,
            )
            .with_dispel_types(dispel_types),
            ImpactEffectRow::Stun {
                duration_ms,
                dispel_types,
            } => StatusApplication::new(
                StatusPayload::Stun,
                Duration::from_millis(duration_ms),
                None,
                StatusStackGroupDefault::ActionSuffix("STUN"),
                1,
                StackPolicy::Refresh,
            )
            .with_dispel_types(dispel_types),
            ImpactEffectRow::Freeze {
                duration_ms,
                dispel_types,
            } => StatusApplication::new(
                StatusPayload::Freeze,
                Duration::from_millis(duration_ms),
                None,
                StatusStackGroupDefault::ActionSuffix("FREEZE"),
                1,
                StackPolicy::Refresh,
            )
            .with_dispel_types(dispel_types),
            ImpactEffectRow::Knockdown {
                duration_ms,
                dispel_types,
            } => StatusApplication::new(
                StatusPayload::Knockdown,
                Duration::from_millis(duration_ms),
                None,
                StatusStackGroupDefault::ActionSuffix("KNOCKDOWN"),
                1,
                StackPolicy::Refresh,
            )
            .with_dispel_types(dispel_types),
            ImpactEffectRow::Stagger {
                duration_ms,
                dispel_types,
            } => StatusApplication::new(
                StatusPayload::Stagger,
                Duration::from_millis(duration_ms),
                None,
                StatusStackGroupDefault::Global("STAGGER"),
                1,
                StackPolicy::Refresh,
            )
            .with_dispel_types(dispel_types),
            ImpactEffectRow::Root {
                duration_ms,
                status_stack_group,
                dispel_types,
            } => StatusApplication::new(
                StatusPayload::Root,
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::Global("ROOT"),
                1,
                StackPolicy::Refresh,
            )
            .with_dispel_types(dispel_types),
            ImpactEffectRow::Intimidated {
                duration_ms,
                status_stack_group,
                dispel_types,
            } => StatusApplication::new(
                StatusPayload::Intimidated,
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::ActionSuffix("INTIMIDATED"),
                1,
                StackPolicy::Refresh,
            )
            .with_dispel_types(dispel_types),
            ImpactEffectRow::Slow {
                duration_ms,
                slow_pct,
                status_stack_group,
                dispel_types,
                max_stacks,
                stack_policy,
            } => StatusApplication::new(
                AuthoredStatusPayload::new(StatusEffectKind::Slow, slow_pct, 0, 0, 0, 0.0)
                    .payload(),
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::ActionSuffix("SLOW"),
                max_stacks,
                stack_policy,
            )
            .with_dispel_types(dispel_types),
            ImpactEffectRow::MoveSpeed {
                duration_ms,
                modifier_scalar,
                status_stack_group,
                max_stacks,
                stack_policy,
            } => StatusApplication::new(
                AuthoredStatusPayload::new(
                    StatusEffectKind::MoveSpeed,
                    0.0,
                    0,
                    0,
                    0,
                    modifier_scalar,
                )
                .payload(),
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::ActionSuffix("MOVE_SPEED"),
                max_stacks,
                stack_policy,
            ),
            ImpactEffectRow::ManaRegen {
                duration_ms,
                modifier_scalar,
                status_stack_group,
                max_stacks,
                stack_policy,
            } => StatusApplication::new(
                AuthoredStatusPayload::new(
                    StatusEffectKind::ManaRegen,
                    0.0,
                    0,
                    0,
                    0,
                    modifier_scalar,
                )
                .payload(),
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::ActionSuffix("MANA_REGEN"),
                max_stacks,
                stack_policy,
            ),
            ImpactEffectRow::StaminaRegen {
                duration_ms,
                modifier_scalar,
                status_stack_group,
                max_stacks,
                stack_policy,
            } => StatusApplication::new(
                AuthoredStatusPayload::new(
                    StatusEffectKind::StaminaRegen,
                    0.0,
                    0,
                    0,
                    0,
                    modifier_scalar,
                )
                .payload(),
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::ActionSuffix("STAMINA_REGEN"),
                max_stacks,
                stack_policy,
            ),
            ImpactEffectRow::MagicResistance {
                duration_ms,
                modifier_scalar,
                status_stack_group,
                max_stacks,
                stack_policy,
            } => StatusApplication::new(
                AuthoredStatusPayload::new(
                    StatusEffectKind::MagicResistance,
                    0.0,
                    0,
                    0,
                    0,
                    modifier_scalar,
                )
                .payload(),
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::ActionSuffix("MAGIC_RESISTANCE"),
                max_stacks,
                stack_policy,
            ),
            ImpactEffectRow::Thorns {
                duration_ms,
                tick_damage,
                status_stack_group,
                max_stacks,
                stack_policy,
            } => StatusApplication::new(
                AuthoredStatusPayload::new(StatusEffectKind::Thorns, 0.0, tick_damage, 0, 0, 0.0)
                    .payload(),
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::ActionSuffix("THORNS"),
                max_stacks,
                stack_policy,
            ),
            ImpactEffectRow::VengeanceAura {
                duration_ms,
                status_stack_group,
                max_stacks,
                stack_policy,
            } => StatusApplication::new(
                AuthoredStatusPayload::new(StatusEffectKind::VengeanceAura, 0.0, 0, 0, 0, 0.0)
                    .payload(),
                Duration::from_millis(duration_ms),
                status_stack_group,
                StatusStackGroupDefault::ActionSuffix("VENGEANCE_AURA"),
                max_stacks,
                stack_policy,
            ),
        }
    }
}

impl From<ProjectileMotionRow> for ProjectileMotionTunables {
    fn from(row: ProjectileMotionRow) -> Self {
        match row {
            ProjectileMotionRow::Linear {} => Self::Linear,
            ProjectileMotionRow::CurvedTarget {
                arc_direction_degrees_min,
                arc_direction_degrees_max,
                arc_amplitude_min,
                arc_amplitude_max,
                control_point_fraction,
            } => Self::CurvedTarget(CurvedTargetProjectileTunables {
                arc_direction_degrees_min,
                arc_direction_degrees_max,
                arc_amplitude_min,
                arc_amplitude_max,
                control_point_fraction,
            }),
            ProjectileMotionRow::OrbitCaster {
                projectile_count,
                orbit_radius,
                orbit_height,
                angular_speed_deg_per_sec,
                lifetime_seconds,
                hit_radius,
                hit_cooldown_seconds,
                max_hits_per_target,
                phase_offset_deg,
            } => Self::OrbitCaster(OrbitCasterProjectileTunables {
                projectile_count,
                orbit_radius,
                orbit_height,
                angular_speed_deg_per_sec,
                lifetime_seconds,
                hit_radius,
                hit_cooldown_seconds,
                max_hits_per_target,
                phase_offset_deg,
            }),
            ProjectileMotionRow::BoomerangCaster {
                outbound_distance,
                return_speed,
                lifetime_seconds,
                hit_radius,
                hit_cooldown_seconds,
                max_hits_per_target,
            } => Self::BoomerangCaster(BoomerangCasterProjectileTunables {
                outbound_distance,
                return_speed,
                lifetime_seconds,
                hit_radius,
                hit_cooldown_seconds,
                max_hits_per_target,
            }),
        }
    }
}

fn validate_definition(def: &SpellDefinition) -> Result<(), String> {
    if !def.uses_global_cooldown && def.cooldown.as_millis() == 0 {
        return Err(format!(
            "{} off-GCD spells still require an own cooldown",
            def.kind.as_str()
        ));
    }
    if def.uses_global_cooldown && def.global_cooldown.as_millis() == 0 {
        return Err(format!(
            "{} GCD spells require a positive global cooldown",
            def.kind.as_str()
        ));
    }
    if let Some(radius) = def.aim_radius {
        ensure_finite_non_negative(def.kind.as_str(), "aim_radius", radius)?;
    }
    if def.targeting == SpellTargeting::Point && def.aim_radius.is_none() {
        return Err(format!(
            "{} POINT-targeted spells must define aim_radius",
            def.kind.as_str()
        ));
    }

    ensure_finite_non_negative(
        def.kind.as_str(),
        "resource_cost",
        def.primary_resource_cost,
    )?;
    ensure_finite_non_negative(
        def.kind.as_str(),
        "primary_resource_gain_on_cast",
        def.primary_resource_gain_on_cast,
    )?;
    ensure_finite_non_negative(def.kind.as_str(), "speed", def.speed)?;
    ensure_finite_non_negative(def.kind.as_str(), "max_distance", def.max_distance)?;
    ensure_finite_non_negative(def.kind.as_str(), "spawn_forward", def.spawn_forward)?;
    ensure_finite_non_negative(def.kind.as_str(), "spawn_height", def.spawn_height)?;
    ensure_finite_non_negative(def.kind.as_str(), "turn_rate", def.turn_rate)?;
    ensure_finite_non_negative(
        def.kind.as_str(),
        "update_interval_seconds",
        def.update_interval,
    )?;
    ensure_finite_non_negative(def.kind.as_str(), "duration_seconds", def.duration)?;
    ensure_finite_non_negative(def.kind.as_str(), "radius", def.radius)?;
    ensure_finite_non_negative(
        def.kind.as_str(),
        "projectile_radius",
        def.projectile_radius,
    )?;
    if def.damage < 0 {
        return Err(format!("{} damage must be non-negative", def.kind.as_str()));
    }
    if let Some(group) = def.status_stack_group.as_deref() {
        if group.trim().is_empty() {
            return Err(format!(
                "{} status_stack_group must not be empty",
                def.kind.as_str()
            ));
        }
    }

    match def.behavior {
        SpellBehavior::ApplyStatus => {
            let Some(status) = def.apply_status.as_ref() else {
                return Err(format!(
                    "{} APPLY_STATUS must define status",
                    def.kind.as_str()
                ));
            };
            let Some(polarity) = def.apply_status_polarity else {
                return Err(format!(
                    "{} APPLY_STATUS must define polarity",
                    def.kind.as_str()
                ));
            };
            validate_apply_status(def, status.clone(), polarity)?;
        }
        SpellBehavior::SelfResource => {
            if def.targeting != SpellTargeting::Self_ || def.requires_target {
                return Err(format!(
                    "{} SELF_RESOURCE must use SELF targeting without a target requirement",
                    def.kind.as_str()
                ));
            }
            if def.primary_resource_gain_on_cast <= 0.0 {
                return Err(format!(
                    "{} SELF_RESOURCE must define a positive primary_resource_gain_on_cast",
                    def.kind.as_str()
                ));
            }
        }
        _ if def.apply_status.is_some() => {
            return Err(format!(
                "{} non-APPLY_STATUS spell must not define apply_status",
                def.kind.as_str()
            ));
        }
        _ => {}
    }

    if def.arms_auto_attack_on_cast {
        if def.targeting != SpellTargeting::Target || !def.requires_target {
            return Err(format!(
                "{} auto-attack arming spells must target and require a target",
                def.kind.as_str()
            ));
        }
        if def.target_audience != TargetAudience::Hostile {
            return Err(format!(
                "{} auto-attack arming spells must use HOSTILE target_audience",
                def.kind.as_str()
            ));
        }
    }

    if def.damage > 0 && def.target_audience != TargetAudience::Hostile {
        return Err(format!(
            "{} damaging spells must use HOSTILE target_audience",
            def.kind.as_str()
        ));
    }

    validate_secondary_tunables(def)?;

    Ok(())
}

fn validate_impact_effect(def: &SpellDefinition, effect: &ImpactEffect) -> Result<(), String> {
    ensure_positive_duration(
        def.kind.as_str(),
        "delivery.impact_effects[].duration_ms",
        effect.duration(),
    )?;
    if let Some(stack_group) = effect.explicit_stack_group() {
        if stack_group.trim().is_empty() {
            return Err(format!(
                "{} delivery.impact_effects[].status_stack_group must not be empty",
                def.kind.as_str()
            ));
        }
    }

    effect
        .payload()
        .validate_authored(def.kind.as_str(), "delivery.impact_effects[]")?;
    if effect.authored_max_stacks() == 0 {
        return Err(format!(
            "{} delivery.impact_effects[].max_stacks must be at least 1",
            def.kind.as_str()
        ));
    }
    Ok(())
}

fn validate_secondary_tunables(def: &SpellDefinition) -> Result<(), String> {
    if def.behavior != SpellBehavior::Aura && def.secondary.aura.is_some() {
        return Err(format!(
            "{} must not define aura secondary data",
            def.kind.as_str()
        ));
    }

    match def.behavior {
        SpellBehavior::DirectTarget => {
            if def.targeting != SpellTargeting::Target || !def.requires_target {
                return Err(format!(
                    "{} DIRECT_TARGET must use required TARGET targeting",
                    def.kind.as_str()
                ));
            }
            ensure_positive_f32(def.kind.as_str(), "delivery.max_distance", def.max_distance)?;
            let Some(direct_target) = def.secondary.direct_target.as_ref() else {
                return Err(format!(
                    "{} DIRECT_TARGET must define secondary direct-target data",
                    def.kind.as_str()
                ));
            };
            for effect in &direct_target.impact_effects {
                validate_impact_effect(def, effect)?;
            }
            ensure_no_secondary(def, true, true, true, true, true, true)?;
        }
        SpellBehavior::Projectile => {
            let Some(projectile) = def.secondary.projectile.as_ref() else {
                return Err(format!(
                    "{} PROJECTILE must define secondary projectile data",
                    def.kind.as_str()
                ));
            };
            validate_projectile_motion(def, projectile)?;
            for effect in &projectile.impact_effects {
                validate_impact_effect(def, effect)?;
            }
            ensure_no_secondary(def, false, true, true, true, true, true)?;
        }
        SpellBehavior::Area => {
            let Some(area) = def.secondary.area.as_ref() else {
                return Err(format!(
                    "{} AREA must define secondary area data",
                    def.kind.as_str()
                ));
            };
            if def.kind.as_str() == SPELL_METEOR {
                if area.sky_origin.is_none() {
                    return Err(format!(
                        "{} METEOR must define sky_origin",
                        def.kind.as_str()
                    ));
                }
                if area.impact_delay_ms != 0 {
                    return Err(format!(
                        "{} METEOR uses delivery.duration_seconds for runtime travel timing; delivery.impact_delay_ms is only for generic AREA impacts",
                        def.kind.as_str()
                    ));
                }
            } else if area.sky_origin.is_some() {
                return Err(format!(
                    "{} sky_origin is only supported by METEOR",
                    def.kind.as_str()
                ));
            }
            match def.targeting {
                SpellTargeting::Self_ => {
                    if def.requires_target {
                        return Err(format!(
                            "{} SELF AREA must not require a target",
                            def.kind.as_str()
                        ));
                    }
                }
                SpellTargeting::Point => {
                    if def.requires_target {
                        return Err(format!(
                            "{} POINT AREA must not require a target",
                            def.kind.as_str()
                        ));
                    }
                    if def.kind.as_str() != SPELL_METEOR {
                        ensure_positive_f32(
                            def.kind.as_str(),
                            "delivery.max_distance",
                            def.max_distance,
                        )?;
                        if let Some(aim_radius) = def.aim_radius {
                            if (aim_radius - def.radius).abs() > 0.001 {
                                return Err(format!(
                                    "{} POINT AREA aim_radius must match delivery.radius",
                                    def.kind.as_str()
                                ));
                            }
                        }
                    }
                }
                SpellTargeting::Target => {
                    if BespokeRuntimeSpell::from_spell_id(&def.kind).is_none() {
                        return Err(format!(
                            "{} AREA currently supports SELF or POINT targeting",
                            def.kind.as_str()
                        ));
                    }
                }
            }
            if let Some(sky_origin) = area.sky_origin {
                ensure_finite(
                    def.kind.as_str(),
                    "delivery.sky_origin.height",
                    sky_origin.height,
                )?;
                ensure_finite(
                    def.kind.as_str(),
                    "delivery.sky_origin.drift_x",
                    sky_origin.drift_x,
                )?;
                ensure_finite(
                    def.kind.as_str(),
                    "delivery.sky_origin.drift_z",
                    sky_origin.drift_z,
                )?;
                if sky_origin.height <= 0.0 {
                    return Err(format!(
                        "{} delivery.sky_origin.height must be positive",
                        def.kind.as_str()
                    ));
                }
            }
            let mut has_stun = false;
            for effect in &area.impact_effects {
                if effect.payload().kind() == StatusEffectKind::Stun {
                    has_stun = true;
                }
                validate_impact_effect(def, effect)?;
            }
            if def.kind.as_str() == SPELL_METEOR && !has_stun {
                return Err(format!(
                    "{} METEOR must define a STUN impact effect",
                    def.kind.as_str()
                ));
            }
            ensure_no_secondary(def, true, false, true, true, true, true)?;
        }
        SpellBehavior::InstantBeam => {
            let Some(instant_beam) = def.secondary.instant_beam else {
                return Err(format!(
                    "{} INSTANT_BEAM must define secondary instant-beam data",
                    def.kind.as_str()
                ));
            };
            let Some(charge_scaling) = instant_beam.charge_scaling else {
                return Err(format!(
                    "{} INSTANT_BEAM must define charge_scaling",
                    def.kind.as_str()
                ));
            };
            ensure_finite(
                def.kind.as_str(),
                "delivery.charge_scaling.min_damage_scale",
                charge_scaling.min_damage_scale,
            )?;
            if charge_scaling.min_damage_scale <= 0.0 || charge_scaling.min_damage_scale > 1.0 {
                return Err(format!(
                    "{} delivery.charge_scaling.min_damage_scale must be > 0 and <= 1",
                    def.kind.as_str()
                ));
            }
            if charge_scaling.max_charges == 0 {
                return Err(format!(
                    "{} delivery.charge_scaling.max_charges must be at least 1",
                    def.kind.as_str()
                ));
            }
            ensure_no_secondary(def, true, true, false, true, true, true)?;
        }
        SpellBehavior::ApplyStatus => {
            let Some(apply_status) = def.secondary.apply_status else {
                return Err(format!(
                    "{} APPLY_STATUS must define secondary apply-status data",
                    def.kind.as_str()
                ));
            };
            if def.targeting == SpellTargeting::Self_
                && apply_status.parry_behavior != SpellParryBehavior::Unparryable
            {
                return Err(format!(
                    "{} SELF APPLY_STATUS must be unparryable",
                    def.kind.as_str()
                ));
            }
            ensure_positive_duration(
                def.kind.as_str(),
                "delivery.duration_ms",
                Duration::from_secs_f32(def.duration),
            )?;
            ensure_no_secondary(def, true, true, true, false, true, true)?;
        }
        SpellBehavior::RemoveStatus => {
            let Some(remove_status) = def.secondary.remove_status.as_ref() else {
                return Err(format!(
                    "{} REMOVE_STATUS must define secondary remove-status data",
                    def.kind.as_str()
                ));
            };
            match def.targeting {
                SpellTargeting::Self_ => {
                    if def.requires_target {
                        return Err(format!(
                            "{} SELF REMOVE_STATUS must not require a target",
                            def.kind.as_str()
                        ));
                    }
                }
                SpellTargeting::Target => {
                    if !def.requires_target
                        && !matches!(
                            def.target_audience,
                            TargetAudience::SelfOnly
                                | TargetAudience::PartyOrSelf
                                | TargetAudience::Assistable
                        )
                    {
                        return Err(format!(
                            "{} optional-target REMOVE_STATUS must allow self fallback",
                            def.kind.as_str()
                        ));
                    }
                    ensure_positive_f32(
                        def.kind.as_str(),
                        "delivery.max_distance",
                        def.max_distance,
                    )?;
                }
                SpellTargeting::Point => {
                    return Err(format!(
                        "{} REMOVE_STATUS supports SELF or TARGET targeting",
                        def.kind.as_str()
                    ));
                }
            }
            let uses_filter =
                remove_status.polarity.is_some() || !remove_status.dispel_types.is_empty();
            if remove_status.statuses.is_empty() && !uses_filter {
                return Err(format!(
                    "{} REMOVE_STATUS must define statuses or a status filter",
                    def.kind.as_str()
                ));
            }
            for status in &remove_status.statuses {
                if status.stack_group.as_deref().is_some_and(str::is_empty) {
                    return Err(format!(
                        "{} REMOVE_STATUS status stack_group must not be empty",
                        def.kind.as_str()
                    ));
                }
            }
            ensure_no_secondary(def, true, true, true, true, false, true)?;
        }
        SpellBehavior::ConsumeStatus => {
            let Some(consume_status) = def.secondary.consume_status.as_ref() else {
                return Err(format!(
                    "{} CONSUME_STATUS must define secondary consume-status data",
                    def.kind.as_str()
                ));
            };
            if def.targeting != SpellTargeting::Target || !def.requires_target {
                return Err(format!(
                    "{} CONSUME_STATUS must use required TARGET targeting",
                    def.kind.as_str()
                ));
            }
            ensure_positive_f32(def.kind.as_str(), "delivery.max_distance", def.max_distance)?;
            if consume_status.polarity.is_none() || consume_status.dispel_types.is_empty() {
                return Err(format!(
                    "{} CONSUME_STATUS must define polarity and dispel_types",
                    def.kind.as_str()
                ));
            }
            if consume_status.heal_per_stack <= 0 {
                return Err(format!(
                    "{} CONSUME_STATUS heal_per_stack must be positive",
                    def.kind.as_str()
                ));
            }
            ensure_no_secondary(def, true, true, true, true, true, false)?;
        }
        SpellBehavior::Aura => {
            let Some(aura) = def.secondary.aura.as_ref() else {
                return Err(format!(
                    "{} AURA must define secondary aura data",
                    def.kind.as_str()
                ));
            };
            if def.targeting != SpellTargeting::Self_ || def.requires_target {
                return Err(format!(
                    "{} AURA must use SELF targeting without a target requirement",
                    def.kind.as_str()
                ));
            }
            if def.target_audience != TargetAudience::PartyOrSelf {
                return Err(format!(
                    "{} AURA must use PARTY_OR_SELF target_audience",
                    def.kind.as_str()
                ));
            }
            ensure_positive_f32(def.kind.as_str(), "delivery.radius", aura.radius)?;
            ensure_positive_duration(
                def.kind.as_str(),
                "delivery.tick_interval_ms",
                aura.tick_interval,
            )?;
            if aura.effects.is_empty() {
                return Err(format!(
                    "{} AURA must define at least one delivery.effects entry",
                    def.kind.as_str()
                ));
            }
            for effect in &aura.effects {
                if !effect.dispel_types().is_empty() {
                    return Err(format!(
                        "{} AURA effects must not define dispel_types",
                        def.kind.as_str()
                    ));
                }
                validate_impact_effect(def, effect)?;
            }
            ensure_no_secondary(def, true, true, true, true, true, true)?;
        }
        SpellBehavior::Channel => {
            let expected = SpellSecondaryTunables {
                channel_projectile: def.secondary.channel_projectile.clone(),
                ..SpellSecondaryTunables::default()
            };
            if def.secondary != expected {
                return Err(format!(
                    "{} CHANNEL must only define channel projectile secondary spell tunables",
                    def.kind.as_str()
                ));
            }
            if let Some(projectile) = &def.secondary.channel_projectile {
                validate_projectile_motion(def, projectile)?;
            }
        }
        SpellBehavior::SelfResource => {
            if def.secondary != SpellSecondaryTunables::default() {
                return Err(format!(
                    "{} {:?} must not define secondary spell tunables",
                    def.kind.as_str(),
                    def.behavior
                ));
            }
        }
    }
    Ok(())
}

fn validate_projectile_motion(
    def: &SpellDefinition,
    projectile: &ProjectileSecondaryTunables,
) -> Result<(), String> {
    ensure_finite_non_negative(
        def.kind.as_str(),
        "delivery.homing_window_seconds",
        projectile.homing_window_seconds,
    )?;

    match &projectile.motion {
        ProjectileMotionTunables::Linear => {
            ensure_positive_f32(def.kind.as_str(), "delivery.speed", def.speed)?;
            ensure_positive_f32(def.kind.as_str(), "delivery.max_distance", def.max_distance)?;
            ensure_positive_f32(def.kind.as_str(), "delivery.radius", def.radius)?;
        }
        ProjectileMotionTunables::CurvedTarget(curve) => {
            if def.targeting != SpellTargeting::Target || !def.requires_target {
                return Err(format!(
                    "{} CURVED_TARGET projectile motion must use TARGET targeting with a target requirement",
                    def.kind.as_str()
                ));
            }
            ensure_positive_f32(def.kind.as_str(), "delivery.speed", def.speed)?;
            ensure_positive_f32(def.kind.as_str(), "delivery.max_distance", def.max_distance)?;
            ensure_positive_f32(def.kind.as_str(), "delivery.radius", def.radius)?;
            ensure_finite(
                def.kind.as_str(),
                "delivery.motion.arc_direction_degrees_min",
                curve.arc_direction_degrees_min,
            )?;
            ensure_finite(
                def.kind.as_str(),
                "delivery.motion.arc_direction_degrees_max",
                curve.arc_direction_degrees_max,
            )?;
            if curve.arc_direction_degrees_max < curve.arc_direction_degrees_min {
                return Err(format!(
                    "{} delivery.motion.arc_direction_degrees_max must be >= arc_direction_degrees_min",
                    def.kind.as_str()
                ));
            }
            ensure_finite_non_negative(
                def.kind.as_str(),
                "delivery.motion.arc_amplitude_min",
                curve.arc_amplitude_min,
            )?;
            ensure_finite_non_negative(
                def.kind.as_str(),
                "delivery.motion.arc_amplitude_max",
                curve.arc_amplitude_max,
            )?;
            if curve.arc_amplitude_max < curve.arc_amplitude_min {
                return Err(format!(
                    "{} delivery.motion.arc_amplitude_max must be >= arc_amplitude_min",
                    def.kind.as_str()
                ));
            }
            ensure_positive_f32(
                def.kind.as_str(),
                "delivery.motion.control_point_fraction",
                curve.control_point_fraction,
            )?;
            if curve.control_point_fraction >= 1.0 {
                return Err(format!(
                    "{} delivery.motion.control_point_fraction must be less than 1",
                    def.kind.as_str()
                ));
            }
        }
        ProjectileMotionTunables::OrbitCaster(orbit) => {
            if def.targeting != SpellTargeting::Self_ || def.requires_target {
                return Err(format!(
                    "{} ORBIT_CASTER projectile motion must use SELF targeting without a target requirement",
                    def.kind.as_str()
                ));
            }
            if orbit.projectile_count == 0 {
                return Err(format!(
                    "{} delivery.motion.projectile_count must be positive",
                    def.kind.as_str()
                ));
            }
            ensure_positive_f32(
                def.kind.as_str(),
                "delivery.motion.orbit_radius",
                orbit.orbit_radius,
            )?;
            ensure_finite_non_negative(
                def.kind.as_str(),
                "delivery.motion.orbit_height",
                orbit.orbit_height,
            )?;
            ensure_finite_non_zero(
                def.kind.as_str(),
                "delivery.motion.angular_speed_deg_per_sec",
                orbit.angular_speed_deg_per_sec,
            )?;
            ensure_positive_f32(
                def.kind.as_str(),
                "delivery.motion.lifetime_seconds",
                orbit.lifetime_seconds,
            )?;
            ensure_positive_f32(
                def.kind.as_str(),
                "delivery.motion.hit_radius",
                orbit.hit_radius,
            )?;
            ensure_finite_non_negative(
                def.kind.as_str(),
                "delivery.motion.hit_cooldown_seconds",
                orbit.hit_cooldown_seconds,
            )?;
            ensure_finite(
                def.kind.as_str(),
                "delivery.motion.phase_offset_deg",
                orbit.phase_offset_deg,
            )?;
        }
        ProjectileMotionTunables::BoomerangCaster(boomerang) => {
            if def.targeting != SpellTargeting::Target || !def.requires_target {
                return Err(format!(
                    "{} BOOMERANG_CASTER projectile motion must use TARGET targeting with a target requirement",
                    def.kind.as_str()
                ));
            }
            ensure_positive_f32(def.kind.as_str(), "delivery.speed", def.speed)?;
            ensure_positive_f32(
                def.kind.as_str(),
                "delivery.motion.outbound_distance",
                boomerang.outbound_distance,
            )?;
            ensure_positive_f32(
                def.kind.as_str(),
                "delivery.motion.return_speed",
                boomerang.return_speed,
            )?;
            ensure_positive_f32(
                def.kind.as_str(),
                "delivery.motion.lifetime_seconds",
                boomerang.lifetime_seconds,
            )?;
            ensure_positive_f32(
                def.kind.as_str(),
                "delivery.motion.hit_radius",
                boomerang.hit_radius,
            )?;
            ensure_finite_non_negative(
                def.kind.as_str(),
                "delivery.motion.hit_cooldown_seconds",
                boomerang.hit_cooldown_seconds,
            )?;
            if boomerang.max_hits_per_target == 0 {
                return Err(format!(
                    "{} delivery.motion.max_hits_per_target must be positive",
                    def.kind.as_str()
                ));
            }
        }
    }

    Ok(())
}

fn ensure_no_secondary(
    def: &SpellDefinition,
    no_projectile: bool,
    no_area: bool,
    no_instant_beam: bool,
    no_apply_status: bool,
    no_remove_status: bool,
    no_consume_status: bool,
) -> Result<(), String> {
    if def.behavior != SpellBehavior::DirectTarget && def.secondary.direct_target.is_some() {
        return Err(format!(
            "{} must not define direct-target secondary data",
            def.kind.as_str()
        ));
    }
    if no_projectile && def.secondary.projectile.is_some() {
        return Err(format!(
            "{} must not define projectile secondary data",
            def.kind.as_str()
        ));
    }
    if no_area && def.secondary.area.is_some() {
        return Err(format!(
            "{} must not define area secondary data",
            def.kind.as_str()
        ));
    }
    if no_instant_beam && def.secondary.instant_beam.is_some() {
        return Err(format!(
            "{} must not define instant-beam secondary data",
            def.kind.as_str()
        ));
    }
    if no_apply_status && def.secondary.apply_status.is_some() {
        return Err(format!(
            "{} must not define apply-status secondary data",
            def.kind.as_str()
        ));
    }
    if no_remove_status && def.secondary.remove_status.is_some() {
        return Err(format!(
            "{} must not define remove-status secondary data",
            def.kind.as_str()
        ));
    }
    if no_consume_status && def.secondary.consume_status.is_some() {
        return Err(format!(
            "{} must not define consume-status secondary data",
            def.kind.as_str()
        ));
    }
    Ok(())
}

fn validate_apply_status(
    def: &SpellDefinition,
    status: ApplyStatusDefinition,
    polarity: StatusPolarity,
) -> Result<(), String> {
    match def.targeting {
        SpellTargeting::Self_ => {
            if def.requires_target {
                return Err(format!(
                    "{} SELF APPLY_STATUS must not require a target",
                    def.kind.as_str()
                ));
            }
            if def.max_distance > 0.0 {
                return Err(format!(
                    "{} SELF APPLY_STATUS must not define max_distance",
                    def.kind.as_str()
                ));
            }
            if polarity != StatusPolarity::Buff {
                return Err(format!(
                    "{} SELF APPLY_STATUS must use BUFF polarity",
                    def.kind.as_str()
                ));
            }
            if def.target_audience == TargetAudience::Assistable {
                return Err(format!(
                    "{} SELF APPLY_STATUS must not use ASSISTABLE target_audience",
                    def.kind.as_str()
                ));
            }
            validate_apply_status_kind_for_self(def.kind.as_str(), status.kind)?;
        }
        SpellTargeting::Target => {
            if !def.requires_target {
                return Err(format!(
                    "{} TARGET APPLY_STATUS must require a target",
                    def.kind.as_str()
                ));
            }
            if def.max_distance <= 0.0 {
                return Err(format!(
                    "{} TARGET APPLY_STATUS must define positive max_distance",
                    def.kind.as_str()
                ));
            }
            match polarity {
                StatusPolarity::Debuff => {
                    if def.target_audience != TargetAudience::Hostile {
                        return Err(format!(
                            "{} TARGET DEBUFF must use HOSTILE target_audience",
                            def.kind.as_str()
                        ));
                    }
                    validate_apply_status_kind_for_target(def.kind.as_str(), status.kind)?;
                }
                StatusPolarity::Buff => {
                    if !matches!(
                        def.target_audience,
                        TargetAudience::PartyOrSelf | TargetAudience::Assistable
                    ) {
                        return Err(format!(
                            "{} TARGET BUFF must use PARTY_OR_SELF or ASSISTABLE target_audience",
                            def.kind.as_str()
                        ));
                    }
                    validate_apply_status_kind_for_self(def.kind.as_str(), status.kind)?;
                }
            }
        }
        SpellTargeting::Point => {
            return Err(format!(
                "{} APPLY_STATUS does not support POINT targeting",
                def.kind.as_str()
            ));
        }
    }
    if status.max_stacks == 0 {
        return Err(format!(
            "{} apply_status.max_stacks must be at least 1",
            def.kind.as_str()
        ));
    }
    status
        .authored_payload()
        .validate(def.kind.as_str(), "apply_status")
}

fn validate_apply_status_kind_for_self(
    spell_id: &str,
    kind: StatusEffectKind,
) -> Result<(), String> {
    match kind {
        StatusEffectKind::MoveSlowImmunity
        | StatusEffectKind::DamageAmp
        | StatusEffectKind::DirectDamageAmp
        | StatusEffectKind::DamageTakenReduction
        | StatusEffectKind::ManaRegen
        | StatusEffectKind::StaminaRegen
        | StatusEffectKind::MagicResistance
        | StatusEffectKind::Thorns
        | StatusEffectKind::VengeanceAura
        | StatusEffectKind::MeleeAttackModifier
        | StatusEffectKind::AttackSpeed
        | StatusEffectKind::CastSpeed
        | StatusEffectKind::TemporaryHitpoints
        | StatusEffectKind::Berserking
        | StatusEffectKind::BattleTrance
        | StatusEffectKind::TargetedAbilityAvoidance => Ok(()),
        other => Err(format!(
            "{spell_id} SELF APPLY_STATUS status '{}' is not supported",
            other.as_str()
        )),
    }
}

fn validate_apply_status_kind_for_target(
    spell_id: &str,
    kind: StatusEffectKind,
) -> Result<(), String> {
    match kind {
        StatusEffectKind::Root
        | StatusEffectKind::Stun
        | StatusEffectKind::Freeze
        | StatusEffectKind::Intimidated
        | StatusEffectKind::Stagger
        | StatusEffectKind::Knockdown
        | StatusEffectKind::Dot => Ok(()),
        other => Err(format!(
            "{spell_id} TARGET APPLY_STATUS status '{}' is not supported",
            other.as_str()
        )),
    }
}

fn ensure_positive_duration(spell_id: &str, field: &str, value: Duration) -> Result<(), String> {
    if value.is_zero() {
        return Err(format!("{spell_id} {field} must be positive"));
    }
    Ok(())
}

fn ensure_finite_non_negative(spell_id: &str, field: &str, value: f32) -> Result<(), String> {
    ensure_finite(spell_id, field, value)?;
    if value < 0.0 {
        return Err(format!("{spell_id} {field} must be non-negative"));
    }
    Ok(())
}

fn ensure_finite_non_zero(spell_id: &str, field: &str, value: f32) -> Result<(), String> {
    ensure_finite(spell_id, field, value)?;
    if value == 0.0 {
        return Err(format!("{spell_id} {field} must be non-zero"));
    }
    Ok(())
}

fn ensure_positive_f32(spell_id: &str, field: &str, value: f32) -> Result<(), String> {
    ensure_finite(spell_id, field, value)?;
    if value <= 0.0 {
        return Err(format!("{spell_id} {field} must be positive"));
    }
    Ok(())
}

fn ensure_angle_degrees(spell_id: &str, field: &str, value: f32) -> Result<(), String> {
    ensure_finite(spell_id, field, value)?;
    if value <= 0.0 || value > 360.0 {
        return Err(format!("{spell_id} {field} must be in the range (0, 360]"));
    }
    Ok(())
}

fn ensure_finite(spell_id: &str, field: &str, value: f32) -> Result<(), String> {
    if !value.is_finite() {
        return Err(format!("{spell_id} {field} must be finite"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::super::manifest::BespokeRuntimeSpell;
    use super::*;
    use std::collections::HashSet;
    use std::path::{Path, PathBuf};
    use std::time::Duration;

    #[test]
    fn shared_catalog_preserves_derived_runtime_spell_order() {
        let definitions = load_spell_definitions().expect("spell catalog should load");
        let kinds: Vec<_> = definitions
            .iter()
            .map(|definition| definition.kind.as_str())
            .collect();

        assert_eq!(
            kinds,
            vec![
                "FIREBALL",
                "GROUND_SLASH",
                "ICICLE",
                "ORBITING_BLADES",
                "METEOR",
                "LIGHTNING",
                "ERUPTION",
                "FROST_NEEDLE",
                "ICE_SPIKES",
                "BOOMERANG_ORB",
                "WITHERING_ORB",
                "INSTANT_BEAM",
                "ELECTROCUTE",
                "FROZEN_SPLINTERS",
                "MAGIC_MISSILE",
                "FROST_NOVA",
                "NEGATE",
                "BLINDING_LIGHT",
                "GLACIAL_SPIKE",
                "FROZEN_GRASP",
                "MOMENTUM",
                "FORTIFY",
                "IRON_WILL",
                "DEFIANCE",
                "BATTLE_CRY",
                "GIANT_SWING",
                "FRENZY",
                "ENRAGE",
                "SECOND_WIND",
                "BERSERKING",
                "BATTLE_TRANCE",
                "FEAST",
                "SHOCKWAVE",
                "INTIMIDATE",
                "SERRATED_BLADES",
                "CONSECRATE",
                "CLEANSING_TOUCH",
                "ABSOLUTION",
                "FERVOR",
                "MANA_FONT",
                "STAMINA_FONT",
                "THORNS_AURA",
                "WARDING_AURA",
                "AURA_OF_VENGEANCE",
                "BLESSED_SHIELD",
                "BLADE_BARRIER",
                "SACRED_FLAME",
                "SKELETON_WIZARD_FROST_BOLT",
                "LICH_BONE_WARD",
                "SKELETON_ARCHER_SHOT",
            ]
        );
    }

    #[test]
    fn spell_abilities_derive_all_runtime_spell_rows() {
        let value: serde_json::Value =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("catalog json must parse");
        assert!(
            value.get("spells").is_none(),
            "spell authoring must live on ability gameplay rows, not top-level spells[]"
        );

        for id in [
            "FIREBALL",
            "GROUND_SLASH",
            "ICICLE",
            "ORBITING_BLADES",
            "METEOR",
            "LIGHTNING",
            "ERUPTION",
            "FROST_NEEDLE",
            "ICE_SPIKES",
            "BOOMERANG_ORB",
            "WITHERING_ORB",
            "INSTANT_BEAM",
            "ELECTROCUTE",
            "FROZEN_SPLINTERS",
            "MAGIC_MISSILE",
            "FROST_NOVA",
            "NEGATE",
            "BLINDING_LIGHT",
            "GLACIAL_SPIKE",
            "FROZEN_GRASP",
            "MOMENTUM",
            "FORTIFY",
            "IRON_WILL",
            "DEFIANCE",
            "BATTLE_CRY",
            "GIANT_SWING",
            "ENRAGE",
            "SECOND_WIND",
            "SHOCKWAVE",
            "INTIMIDATE",
            "CONSECRATE",
            "CLEANSING_TOUCH",
            "ABSOLUTION",
            "FERVOR",
            "AURA_OF_VENGEANCE",
            "BLESSED_SHIELD",
            "BLADE_BARRIER",
        ] {
            assert!(
                spell_definition_by_str(id).is_some(),
                "{id} must still derive a runtime SpellDefinition"
            );
        }
    }

    #[test]
    fn catalog_admits_new_simple_projectile_without_rust_identity_edits() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_PROJECTILE_ABILITY",
                "action_id": "TEST_PROJECTILE",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 1000,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "MOBILE",
                    "targeting": "TARGET",
                    "requires_target": true,
                    "resource_cost": 0.0,
                    "arms_auto_attack_on_cast": false,
                    "delivery": {
                        "kind": "PROJECTILE",
                        "speed": 20.0,
                        "max_distance": 25.0,
                        "damage": 10,
                        "spawn_forward": 1.0,
                        "spawn_height": 1.2,
                        "turn_rate": 0.0,
                        "update_interval_seconds": 0.1,
                        "radius": 0.35,
                        "block_behavior": "BLOCKABLE"
                    }
                }
            }]
        }"#;

        let definitions =
            definitions_from_rows(spell_rows_from_json(json).expect("test row should parse"))
                .expect("new projectile id should load without enum edits");

        assert_eq!(definitions.len(), 1);
        assert_eq!(definitions[0].kind.as_str(), "TEST_PROJECTILE");
        assert_eq!(definitions[0].behavior, SpellBehavior::Projectile);
    }

    #[test]
    fn channel_spells_author_per_second_resource_cost_on_delivery() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_CHANNEL_ABILITY",
                "action_id": "TEST_CHANNEL",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 1000,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "GROUNDED_STATIONARY",
                    "targeting": "TARGET",
                    "requires_target": true,
                    "arms_auto_attack_on_cast": false,
                    "delivery": {
                        "kind": "CHANNEL",
                        "max_distance": 18.0,
                        "damage": 2,
                        "resource_cost_per_second": 20.0,
                        "update_interval_seconds": 0.1,
                        "duration_seconds": 2.0,
                        "block_behavior": "BLOCKABLE"
                    }
                }
            }]
        }"#;

        let definitions =
            definitions_from_rows(spell_rows_from_json(json).expect("test row should parse"))
                .expect("channel spell should load");

        assert_eq!(definitions[0].kind.as_str(), "TEST_CHANNEL");
        assert_eq!(definitions[0].behavior, SpellBehavior::Channel);
        assert!((definitions[0].primary_resource_cost - 20.0).abs() < 0.0001);
    }

    #[test]
    fn channel_spells_reject_generic_resource_cost_authoring() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_CHANNEL_ABILITY",
                "action_id": "TEST_CHANNEL",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 1000,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "GROUNDED_STATIONARY",
                    "targeting": "TARGET",
                    "requires_target": true,
                    "resource_cost": 20.0,
                    "arms_auto_attack_on_cast": false,
                    "delivery": {
                        "kind": "CHANNEL",
                        "max_distance": 18.0,
                        "damage": 2,
                        "update_interval_seconds": 0.1,
                        "duration_seconds": 2.0,
                        "block_behavior": "BLOCKABLE"
                    }
                }
            }]
        }"#;

        let err =
            spell_rows_from_json(json).expect_err("generic channel resource_cost should reject");
        assert!(
            err.contains("resource_cost_per_second"),
            "unexpected error: {err}"
        );
    }

    #[test]
    fn catalog_admits_new_generic_area_without_rust_identity_edits() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_AREA_ABILITY",
                "action_id": "TEST_AREA",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 1000,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "MOBILE",
                    "targeting": "SELF",
                    "requires_target": false,
                    "resource_cost": 0.0,
                    "arms_auto_attack_on_cast": false,
                    "delivery": {
                        "kind": "AREA",
                        "damage": 10,
                        "radius": 3.0,
                        "block_behavior": "BLOCKABLE",
                        "impact_effects": [
                            {
                                "kind": "ROOT",
                                "duration_ms": 500,
                                "status_stack_group": "ROOT"
                            }
                        ]
                    }
                }
            }]
        }"#;

        let definitions =
            definitions_from_rows(spell_rows_from_json(json).expect("test row should parse"))
                .expect("new generic area id should load without enum edits");

        assert_eq!(definitions.len(), 1);
        assert_eq!(definitions[0].kind.as_str(), "TEST_AREA");
        assert_eq!(definitions[0].behavior, SpellBehavior::Area);
        assert_eq!(definitions[0].targeting, SpellTargeting::Self_);
        assert!(!definitions[0].requires_target);
        assert_eq!(definitions[0].radius, 3.0);
        assert_eq!(
            definitions[0].secondary.area.as_ref().unwrap().shape,
            CombatAreaShape::Disc { radius: 3.0 }
        );
    }

    #[test]
    fn catalog_admits_self_origin_area_cone_without_radius() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_ICE_SPIKES_ABILITY",
                "action_id": "TEST_ICE_SPIKES",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 900,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "MOBILE",
                    "targeting": "SELF",
                    "requires_target": false,
                    "resource_cost": 30.0,
                    "arms_auto_attack_on_cast": false,
                    "delivery": {
                        "kind": "AREA",
                        "max_distance": 11.5,
                        "damage": 35,
                        "block_behavior": "BLOCKABLE",
                        "shape": {
                            "kind": "CASTER_CONE",
                            "angle_degrees": 70.0,
                            "vertical_tolerance": 2.5
                        },
                        "impact_effects": []
                    }
                }
            }]
        }"#;

        let definitions =
            definitions_from_rows(spell_rows_from_json(json).expect("test row should parse"))
                .expect("new self-origin area cone should load");

        assert_eq!(definitions.len(), 1);
        assert_eq!(definitions[0].kind.as_str(), "TEST_ICE_SPIKES");
        assert_eq!(definitions[0].behavior, SpellBehavior::Area);
        assert_eq!(definitions[0].targeting, SpellTargeting::Self_);
        assert!(!definitions[0].requires_target);
        assert_eq!(definitions[0].primary_resource_cost, 30.0);
        assert_eq!(definitions[0].radius, 11.5);
        assert_eq!(
            definitions[0].secondary.area.as_ref().unwrap().shape,
            CombatAreaShape::Cone {
                range: 11.5,
                angle_degrees: 70.0,
                vertical_tolerance: Some(2.5)
            }
        );
    }

    #[test]
    fn area_cone_rejects_radius_and_invalid_geometry_or_targeting() {
        fn load(body: &str) -> Result<Vec<SpellDefinition>, String> {
            let json = format!(
                r#"{{
                    "abilities": [{{
                        "ability_id": "TEST_CONE_ABILITY",
                        "action_id": "TEST_CONE",
                        "gameplay": {{
                            "kind": "SPELL",
                            "cooldown_ms": 900,
                            "uses_global_cooldown": true,
                            "cast_time_ms": 0,
                            "cast_mobility": "MOBILE",
                            {body}
                        }}
                    }}]
                }}"#
            );
            definitions_from_rows(spell_rows_from_json(json.as_str())?)
        }

        let with_radius = load(
            r#"
            "targeting": "SELF",
            "requires_target": false,
            "resource_cost": 0.0,
            "arms_auto_attack_on_cast": false,
            "delivery": {
                "kind": "AREA",
                "max_distance": 11.5,
                "radius": 2.0,
                "damage": 35,
                "block_behavior": "BLOCKABLE",
                "shape": { "kind": "CASTER_CONE", "angle_degrees": 70.0, "vertical_tolerance": 2.5 }
            }"#,
        )
        .expect_err("cone area must reject radius");
        assert!(with_radius.contains("must not define radius"));

        let zero_range = load(
            r#"
            "targeting": "SELF",
            "requires_target": false,
            "resource_cost": 0.0,
            "arms_auto_attack_on_cast": false,
            "delivery": {
                "kind": "AREA",
                "max_distance": 0.0,
                "damage": 35,
                "block_behavior": "BLOCKABLE",
                "shape": { "kind": "CASTER_CONE", "angle_degrees": 70.0, "vertical_tolerance": 2.5 }
            }"#,
        )
        .expect_err("cone area must require positive max_distance");
        assert!(zero_range.contains("max_distance must be positive"));

        let bad_angle = load(
            r#"
            "targeting": "SELF",
            "requires_target": false,
            "resource_cost": 0.0,
            "arms_auto_attack_on_cast": false,
            "delivery": {
                "kind": "AREA",
                "max_distance": 11.5,
                "damage": 35,
                "block_behavior": "BLOCKABLE",
                "shape": { "kind": "CASTER_CONE", "angle_degrees": 0.0, "vertical_tolerance": 2.5 }
            }"#,
        )
        .expect_err("cone area must require a valid angle");
        assert!(bad_angle.contains("angle_degrees must be in the range"));

        let bad_targeting = load(
            r#"
            "targeting": "POINT",
            "requires_target": false,
            "aim_radius": 2.0,
            "resource_cost": 0.0,
            "arms_auto_attack_on_cast": false,
            "delivery": {
                "kind": "AREA",
                "max_distance": 11.5,
                "damage": 35,
                "block_behavior": "BLOCKABLE",
                "shape": { "kind": "CASTER_CONE", "angle_degrees": 70.0, "vertical_tolerance": 2.5 }
            }"#,
        )
        .expect_err("cone area must require SELF targeting");
        assert!(bad_targeting.contains("must use SELF targeting"));

        let zero_vertical_tolerance = load(
            r#"
            "targeting": "SELF",
            "requires_target": false,
            "resource_cost": 0.0,
            "arms_auto_attack_on_cast": false,
            "delivery": {
                "kind": "AREA",
                "max_distance": 11.5,
                "damage": 35,
                "block_behavior": "BLOCKABLE",
                "shape": { "kind": "CASTER_CONE", "angle_degrees": 70.0, "vertical_tolerance": 0.0 }
            }"#,
        )
        .expect_err("cone area must require positive vertical_tolerance");
        assert!(zero_vertical_tolerance.contains("vertical_tolerance must be positive"));
    }

    #[test]
    fn projectile_homing_burn_and_stagger_are_behavior_tunables() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_BURN_PROJECTILE_ABILITY",
                "action_id": "TEST_BURN_PROJECTILE",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 1000,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "MOBILE",
                    "targeting": "TARGET",
                    "requires_target": true,
                    "resource_cost": 0.0,
                    "arms_auto_attack_on_cast": false,
                    "delivery": {
                        "kind": "PROJECTILE",
                        "speed": 20.0,
                        "max_distance": 25.0,
                        "damage": 10,
                        "spawn_forward": 1.0,
                        "spawn_height": 1.2,
                        "turn_rate": 3.0,
                        "update_interval_seconds": 0.1,
                        "radius": 0.35,
                        "block_behavior": "BLOCKABLE",
                        "homing_window_seconds": 0.2,
                        "impact_effects": [
                            {
                                "kind": "BURN",
                                "duration_ms": 4000,
                                "tick_interval_ms": 1000,
                                "tick_damage": 2
                            },
                            {
                                "kind": "STAGGER",
                                "duration_ms": 500
                            }
                        ]
                    }
                }
            }]
        }"#;

        let definitions =
            definitions_from_rows(spell_rows_from_json(json).expect("test row should parse"))
                .expect("projectile effects should be delivery-level tunables");
        let projectile = definitions[0]
            .secondary
            .projectile
            .as_ref()
            .expect("projectile secondary data should exist");

        assert_eq!(definitions[0].kind.as_str(), "TEST_BURN_PROJECTILE");
        assert!((projectile.homing_window_seconds - 0.2).abs() < 0.0001);
        assert_eq!(projectile.impact_effects.len(), 2);
    }

    #[test]
    fn legacy_charge_delivery_is_rejected() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_CHARGE_ABILITY",
                "action_id": "TEST_CHARGE",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 1000,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "MOBILE",
                    "targeting": "TARGET",
                    "requires_target": true,
                    "resource_cost": 0.0,
                    "arms_auto_attack_on_cast": true,
                    "delivery": {
                        "kind": "CHARGE",
                        "speed": 18.0,
                        "max_distance": 14.0,
                        "damage": 20,
                        "radius": 0.8,
                        "block_behavior": "BLOCKABLE",
                        "parry_behavior": "UNPARRYABLE",
                        "arrival": {
                            "buffer": 0.7,
                            "epsilon": 0.05
                        },
                        "impact_effects": [
                            {
                                "kind": "KNOCKDOWN",
                                "duration_ms": 900
                            },
                            {
                                "kind": "STAGGER",
                                "duration_ms": 350
                            }
                        ]
                    }
                }
            }]
        }"#;

        assert!(spell_rows_from_json(json)
            .expect_err("spell delivery CHARGE should not be accepted")
            .contains("CHARGE"));
    }

    #[test]
    fn shared_catalog_does_not_author_removed_shield_spell() {
        let value: serde_json::Value =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("catalog should parse as JSON");
        assert!(
            value.get("spells").is_none(),
            "catalog should not author top-level spells"
        );
        assert!(spell_rows_from_json(PROGRESSION_CATALOG_JSON)
            .expect("catalog should load")
            .iter()
            .all(|spell| spell.kind.as_str() != "SHIELD"));
    }

    #[test]
    fn spell_catalog_round_trips_through_typed_serde() {
        for row in spell_rows_from_json(PROGRESSION_CATALOG_JSON).expect("catalog should load") {
            let encoded = serde_json::to_value(&row).expect("spell row should serialize");
            let decoded: SpellCatalogRow =
                serde_json::from_value(encoded).expect("spell row should deserialize");
            assert_eq!(decoded.kind, row.kind);
        }
    }

    #[test]
    fn unknown_delivery_fields_are_rejected() {
        let json = r#"{
            "kind": "ELECTROCUTE",
            "cooldown_ms": 1100,
            "uses_global_cooldown": true,
            "cast_time_ms": 0,
            "cast_mobility": "GROUNDED_STATIONARY",
            "targeting": "TARGET",
            "requires_target": true,
            "resource_cost": 0.0,
            "arms_auto_attack_on_cast": true,
            "delivery": {
                "kind": "CHANNEL",
                "max_distance": 18.0,
                "damage": 6,
                "update_interval_seconds": 0.08,
                "duration_seconds": 2.25,
                "unexpected_field": 75,
                "block_behavior": "BLOCKABLE"
            }
        }"#;

        assert!(serde_json::from_str::<SpellCatalogRow>(json).is_err());
    }

    #[test]
    fn removed_shield_delivery_is_rejected() {
        let json = r#"{
            "kind": "SHIELD",
            "cooldown_ms": 1400,
            "uses_global_cooldown": true,
            "cast_time_ms": 0,
            "cast_mobility": "MOBILE",
            "targeting": "TARGET",
            "requires_target": false,
            "resource_cost": 0.0,
            "arms_auto_attack_on_cast": false,
            "delivery": {
                "kind": "SHIELD",
                "duration_seconds": 2.5,
                "spawn_forward": 1.15,
                "spawn_height": 1.15,
                "radius": 1.45,
                "projectile_radius": 0.18,
                "block_behavior": "UNBLOCKABLE"
            }
        }"#;

        assert!(serde_json::from_str::<SpellCatalogRow>(json).is_err());
    }

    #[test]
    fn spell_catalog_ids_are_unique() {
        let definitions = load_spell_definitions().expect("spell catalog should load");
        let mut spell_ids = HashSet::new();

        for definition in definitions {
            assert!(spell_ids.insert(definition.kind.as_str().to_string()));
        }
    }

    #[test]
    fn bespoke_runtime_spell_ids_stay_on_an_explicit_budget() {
        assert_eq!(
            BespokeRuntimeSpell::ALL.map(BespokeRuntimeSpell::as_str),
            ["INSTANT_BEAM", "ELECTROCUTE", "METEOR", "NEGATE"],
            "growing this list past four is a signal to revisit the spell abstraction"
        );
        assert_eq!(
            BespokeRuntimeSpell::ALL.len(),
            4,
            "the bespoke runtime spell budget is four"
        );
        for spell_id in BespokeRuntimeSpell::ALL.map(BespokeRuntimeSpell::as_str) {
            assert!(
                spell_definition_by_str(spell_id).is_some(),
                "bespoke runtime spell id '{spell_id}' must resolve through the catalog"
            );
        }
    }

    #[test]
    fn runtime_spell_id_constants_stay_allowlisted() {
        let allowed = HashSet::from([
            "SPELL_METEOR",
            "SPELL_INSTANT_BEAM",
            "SPELL_ELECTROCUTE",
            "SPELL_NEGATE",
        ]);
        for relative_path in [
            "src/spells/casting.rs",
            "src/spells/simulation.rs",
            "src/movement_actions.rs",
            "src/auto_attack.rs",
        ] {
            let path = Path::new(env!("CARGO_MANIFEST_DIR")).join(relative_path);
            let source = std::fs::read_to_string(&path)
                .unwrap_or_else(|err| panic!("failed to read {}: {err}", path.display()));
            for token in spell_constant_tokens(source.as_str()) {
                if token.starts_with("SPELL_BLOCK")
                    || token.starts_with("SPELL_CAST")
                    || token.starts_with("SPELL_PARRY")
                    || token.starts_with("SPELL_PREDICTION_RESULT")
                {
                    continue;
                }
                assert!(
                    allowed.contains(token.as_str()),
                    "runtime file '{}' uses unallowlisted spell id constant '{}'",
                    relative_path,
                    token
                );
            }
        }
    }

    #[test]
    fn unity_spell_id_branches_stay_in_presentation_or_contracts() {
        let scripts_root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../Assets/Arena/Runtime");
        let allowed = HashSet::from([
            "Combat/GameplayContracts.cs",
            "Presentation/PlayerAnimator.cs",
        ]);
        let mut files = Vec::new();
        collect_cs_files(&scripts_root, &mut files);
        for path in files {
            let source = std::fs::read_to_string(&path)
                .unwrap_or_else(|err| panic!("failed to read {}: {err}", path.display()));
            if !source.contains("SpellIds.") {
                continue;
            }
            let relative = path
                .strip_prefix(&scripts_root)
                .expect("collected file should be under scripts root")
                .to_string_lossy()
                .replace('\\', "/");
            assert!(
                allowed.contains(relative.as_str()),
                "SpellIds usage in '{}' must stay presentation-only or in contracts",
                relative
            );
        }
    }

    #[test]
    fn migrated_projectile_spells_do_not_have_legacy_spell_vfx_branches() {
        let path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../Assets/Arena/Runtime/Presentation/SpellVFXDispatcher.cs");
        let source = match std::fs::read_to_string(&path) {
            Ok(source) => source,
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => return,
            Err(err) => panic!("failed to read {}: {err}", path.display()),
        };

        for migrated_spell in ["Fireball", "Icicle"] {
            assert!(
                !source.contains(format!("SpellIds.{migrated_spell} =>").as_str()),
                "migrated projectile spell '{migrated_spell}' must not keep a SpellVFXDispatcher switch branch"
            );
            assert!(
                !source.contains(format!("new {migrated_spell}VFX").as_str()),
                "migrated projectile spell '{migrated_spell}' must instantiate through combat_vfx_cues, not SpellVFXDispatcher"
            );
        }
    }

    fn spell_constant_tokens(source: &str) -> HashSet<String> {
        let mut tokens = HashSet::new();
        for (index, _) in source.match_indices("SPELL_") {
            let token: String = source[index..]
                .chars()
                .take_while(|ch| ch.is_ascii_uppercase() || ch.is_ascii_digit() || *ch == '_')
                .collect();
            if !token.is_empty() {
                tokens.insert(token);
            }
        }
        tokens
    }

    fn collect_cs_files(root: &Path, files: &mut Vec<PathBuf>) {
        let entries = std::fs::read_dir(root)
            .unwrap_or_else(|err| panic!("failed to read directory {}: {err}", root.display()));
        for entry in entries {
            let path = entry
                .unwrap_or_else(|err| panic!("failed to read directory entry: {err}"))
                .path();
            if path.is_dir() {
                collect_cs_files(&path, files);
            } else if path.extension().and_then(|ext| ext.to_str()) == Some("cs") {
                files.push(path);
            }
        }
    }

    #[test]
    fn secondary_spell_tunables_match_v1_values() {
        let fireball = spell_definition_by_str("FIREBALL").expect("FIREBALL should exist");
        let fireball_projectile = fireball
            .secondary
            .projectile
            .as_ref()
            .expect("Fireball should define projectile secondary data");
        assert_eq!(
            fireball_projectile.parry_behavior,
            SpellParryBehavior::Parryable
        );
        assert!((fireball_projectile.homing_window_seconds - 0.15).abs() < 0.0001);
        assert_eq!(
            fireball_projectile.impact_effects,
            vec![StatusApplication::new(
                StatusPayload::Dot {
                    tick_damage: 3,
                    damage_type: DamageType::Physical,
                    tick_interval: Duration::from_secs(1),
                },
                Duration::from_secs(10),
                Some("FIREBALL_BURN".to_string()),
                StatusStackGroupDefault::InstanceScopedActionSuffix("BURN"),
                1,
                StackPolicy::Refresh,
            )]
        );

        let icicle = spell_definition_by_str("ICICLE").expect("ICICLE should exist");
        assert!(
            (icicle
                .secondary
                .projectile
                .as_ref()
                .expect("Icicle should define projectile secondary data")
                .homing_window_seconds
                - 0.08)
                .abs()
                < 0.0001
        );

        let meteor = spell_definition_by_str("METEOR").expect("METEOR should exist");
        let meteor_area = meteor
            .secondary
            .area
            .as_ref()
            .expect("Meteor should define area secondary data");
        let sky_origin = meteor_area
            .sky_origin
            .expect("Meteor should define sky origin");
        assert!((sky_origin.height - 9.5).abs() < 0.0001);
        assert!((sky_origin.drift_x - 0.8).abs() < 0.0001);
        assert!((sky_origin.drift_z - -0.55).abs() < 0.0001);
        assert_eq!(
            meteor_area.impact_effects,
            vec![StatusApplication::new(
                StatusPayload::Stun,
                Duration::from_secs(4),
                None,
                StatusStackGroupDefault::ActionSuffix("STUN"),
                1,
                StackPolicy::Refresh,
            )]
        );

        let lightning = spell_definition_by_str("LIGHTNING").expect("LIGHTNING should exist");
        assert_eq!(lightning.behavior, SpellBehavior::Area);
        assert_eq!(lightning.targeting, SpellTargeting::Point);
        assert!(!lightning.requires_target);
        assert_eq!(lightning.damage, 34);
        assert!((lightning.max_distance - 12.0).abs() < 0.0001);
        assert!((lightning.radius - 2.6).abs() < 0.0001);
        assert!(
            (lightning
                .aim_radius
                .expect("Lightning should expose aim radius")
                - 2.6)
                .abs()
                < 0.0001
        );
        assert!(
            lightning
                .secondary
                .area
                .as_ref()
                .expect("Lightning should define area secondary data")
                .sky_origin
                .is_none(),
            "Lightning should use generic instant POINT AREA delivery, not Meteor sky-origin delivery"
        );

        let eruption = spell_definition_by_str("ERUPTION").expect("ERUPTION should exist");
        let eruption_area = eruption
            .secondary
            .area
            .as_ref()
            .expect("Eruption should define area secondary data");
        assert_eq!(eruption_area.impact_delay_ms, 500);

        let frost_needle =
            spell_definition_by_str("FROST_NEEDLE").expect("FROST_NEEDLE should exist");
        assert_eq!(frost_needle.behavior, SpellBehavior::Area);
        assert_eq!(frost_needle.targeting, SpellTargeting::Point);
        assert!(!frost_needle.requires_target);
        assert_eq!(frost_needle.damage, 38);
        assert_eq!(frost_needle.damage_type, DamageType::Cold);
        assert!((frost_needle.max_distance - 12.0).abs() < 0.0001);
        assert!((frost_needle.radius - 2.4).abs() < 0.0001);
        assert!(
            (frost_needle
                .aim_radius
                .expect("Frost Needle should expose aim radius")
                - 2.4)
                .abs()
                < 0.0001
        );
        let frost_needle_area = frost_needle
            .secondary
            .area
            .as_ref()
            .expect("Frost Needle should define area secondary data");
        assert_eq!(frost_needle_area.impact_delay_ms, 500);
        assert_eq!(
            frost_needle_area.impact_effects,
            Vec::<StatusApplication>::new()
        );

        let withering_orb =
            spell_definition_by_str("WITHERING_ORB").expect("WITHERING_ORB should exist");
        assert_eq!(withering_orb.behavior, SpellBehavior::Projectile);
        assert_eq!(withering_orb.targeting, SpellTargeting::Target);
        assert!(withering_orb.requires_target);
        assert!((withering_orb.speed - 2.0).abs() < 0.0001);
        let withering_projectile = withering_orb
            .secondary
            .projectile
            .as_ref()
            .expect("Withering Orb should define projectile secondary data");
        assert_eq!(
            withering_projectile.impact_effects,
            vec![StatusApplication::new(
                StatusPayload::Slow { slow_pct: 0.10 },
                Duration::from_secs(3),
                Some("WITHERING_ORB_SLOW".to_string()),
                StatusStackGroupDefault::ActionSuffix("SLOW"),
                10,
                StackPolicy::AddStackRefresh,
            )]
        );

        let frost_nova = spell_definition_by_str("FROST_NOVA").expect("FROST_NOVA should exist");
        let frost_nova_area = frost_nova
            .secondary
            .area
            .as_ref()
            .expect("Frost Nova should define area secondary data");
        assert_eq!(
            frost_nova_area.impact_effects,
            vec![StatusApplication::new(
                StatusPayload::Root,
                Duration::from_millis(1200),
                Some("ROOT".to_string()),
                StatusStackGroupDefault::Global("ROOT"),
                1,
                StackPolicy::Refresh,
            )],
            "Frost Nova root must be authored through generic AREA impact_effects"
        );

        let frozen_grasp =
            spell_definition_by_str("FROZEN_GRASP").expect("FROZEN_GRASP should exist");
        assert_eq!(frozen_grasp.behavior, SpellBehavior::Area);
        assert_eq!(frozen_grasp.targeting, SpellTargeting::Self_);
        assert!(!frozen_grasp.requires_target);
        assert_eq!(frozen_grasp.damage, 0);
        assert_eq!(frozen_grasp.damage_type, DamageType::Cold);
        assert!((frozen_grasp.primary_resource_cost - 20.0).abs() < 0.0001);
        let frozen_grasp_area = frozen_grasp
            .secondary
            .area
            .as_ref()
            .expect("Frozen Grasp should define area secondary data");
        assert_eq!(
            frozen_grasp_area.impact_effects,
            vec![StatusApplication::new(
                StatusPayload::Root,
                Duration::from_millis(1200),
                Some("ROOT".to_string()),
                StatusStackGroupDefault::Global("ROOT"),
                1,
                StackPolicy::Refresh,
            )],
            "Frozen Grasp root must be authored through generic AREA impact_effects"
        );

        let ice_spikes = spell_definition_by_str("ICE_SPIKES").expect("ICE_SPIKES should exist");
        let ice_spikes_area = ice_spikes
            .secondary
            .area
            .as_ref()
            .expect("Ice Spikes should define area secondary data");
        assert_eq!(
            ice_spikes_area.impact_effects,
            vec![StatusApplication::new(
                StatusPayload::Freeze,
                Duration::from_millis(1200),
                None,
                StatusStackGroupDefault::ActionSuffix("FREEZE"),
                1,
                StackPolicy::Refresh,
            )],
            "Ice Spikes freeze must be authored through generic AREA impact_effects"
        );

        let glacial_spike =
            spell_definition_by_str("GLACIAL_SPIKE").expect("GLACIAL_SPIKE should exist");
        assert_eq!(glacial_spike.behavior, SpellBehavior::DirectTarget);
        assert_eq!(glacial_spike.targeting, SpellTargeting::Target);
        assert!(glacial_spike.requires_target);
        assert_eq!(glacial_spike.damage, 35);
        assert_eq!(glacial_spike.damage_type, DamageType::Cold);
        let glacial_spike_direct_target = glacial_spike
            .secondary
            .direct_target
            .as_ref()
            .expect("Glacial Spike should define direct-target secondary data");
        assert_eq!(
            glacial_spike_direct_target.impact_effects,
            vec![StatusApplication::new(
                StatusPayload::Freeze,
                Duration::from_millis(1200),
                None,
                StatusStackGroupDefault::ActionSuffix("FREEZE"),
                1,
                StackPolicy::Refresh,
            )],
            "Glacial Spike freeze must use generic direct-target impact_effects"
        );

        let instant_beam =
            spell_definition_by_str("INSTANT_BEAM").expect("INSTANT_BEAM should exist");
        let charge_scaling = instant_beam
            .secondary
            .instant_beam
            .expect("Instant Beam should define secondary data")
            .charge_scaling
            .expect("Instant Beam should define charge scaling");
        assert!((charge_scaling.min_damage_scale - 0.35).abs() < 0.0001);
        assert_eq!(charge_scaling.max_charges, 5);

        let catalog_json: serde_json::Value =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("catalog should parse as JSON");
        assert!(
            catalog_json
                .get("abilities")
                .and_then(|abilities| abilities.as_array())
                .expect("catalog should author abilities")
                .iter()
                .filter_map(|ability| ability.get("gameplay"))
                .filter_map(|gameplay| gameplay.get("delivery"))
                .filter_map(|delivery| delivery.get("kind"))
                .all(|kind| kind != "CHARGE"),
            "Charge is gameplay.delivery authoring, not spell delivery authoring"
        );
    }

    #[test]
    fn orbit_projectile_spell_uses_catalog_owned_motion_tunables() {
        let orbiting_blades =
            spell_definition_by_str("ORBITING_BLADES").expect("Orbiting Blades should exist");
        assert_eq!(orbiting_blades.targeting, SpellTargeting::Self_);
        assert!(!orbiting_blades.requires_target);
        assert_eq!(orbiting_blades.damage, 18);
        assert!((orbiting_blades.duration - 10.0).abs() < 0.0001);
        assert!((orbiting_blades.radius - 0.45).abs() < 0.0001);

        let projectile = orbiting_blades
            .secondary
            .projectile
            .as_ref()
            .expect("Orbiting Blades should define projectile secondary data");
        assert_eq!(projectile.motion.kind(), "ORBIT_CASTER");
        let orbit = projectile
            .motion
            .orbit()
            .expect("Orbiting Blades should use orbit-caster projectile motion");
        assert_eq!(orbit.projectile_count, 3);
        assert!((orbit.orbit_radius - 2.0).abs() < 0.0001);
        assert!((orbit.orbit_height - 1.0).abs() < 0.0001);
        assert!((orbit.angular_speed_deg_per_sec - 180.0).abs() < 0.0001);
        assert!((orbit.lifetime_seconds - 10.0).abs() < 0.0001);
        assert!((orbit.hit_radius - 0.45).abs() < 0.0001);
        assert!((orbit.hit_cooldown_seconds - 0.35).abs() < 0.0001);
        assert_eq!(orbit.max_hits_per_target, 1);
        assert_eq!(orbit.phase_offset_deg, 0.0);
    }

    #[test]
    fn boomerang_projectile_motion_parses_as_catalog_owned_tunables() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_BOOMERANG_ABILITY",
                "action_id": "TEST_BOOMERANG",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 700,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "MOBILE",
                    "targeting": "TARGET",
                    "requires_target": true,
                    "delivery": {
                        "kind": "PROJECTILE",
                        "speed": 12.0,
                        "max_distance": 8.0,
                        "damage": 22,
                        "spawn_forward": 0.5,
                        "spawn_height": 1.1,
                        "turn_rate": 0.0,
                        "update_interval_seconds": 0.05,
                        "radius": 0.45,
                        "block_behavior": "BLOCKABLE",
                        "parry_behavior": "PARRYABLE",
                        "motion": {
                            "kind": "BOOMERANG_CASTER",
                            "outbound_distance": 8.0,
                            "return_speed": 14.0,
                            "lifetime_seconds": 3.0,
                            "hit_radius": 0.45,
                            "hit_cooldown_seconds": 0.25,
                            "max_hits_per_target": 1
                        }
                    }
                }
            }]
        }"#;
        let definitions = definitions_from_rows(
            spell_rows_from_json(json).expect("boomerang ability row should parse"),
        )
        .expect("boomerang row should validate");
        let definition = &definitions[0];
        assert_eq!(definition.kind.as_str(), "TEST_BOOMERANG");
        assert_eq!(definition.behavior, SpellBehavior::Projectile);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert!(definition.requires_target);
        assert!((definition.speed - 12.0).abs() < 0.0001);
        assert!((definition.max_distance - 8.0).abs() < 0.0001);
        assert!((definition.duration - 3.0).abs() < 0.0001);
        assert!((definition.radius - 0.45).abs() < 0.0001);

        let projectile = definition
            .secondary
            .projectile
            .as_ref()
            .expect("boomerang spell should define projectile data");
        assert_eq!(projectile.motion.kind(), "BOOMERANG_CASTER");
        let boomerang = projectile
            .motion
            .boomerang()
            .expect("boomerang spell should expose boomerang tunables");
        assert!((boomerang.outbound_distance - 8.0).abs() < 0.0001);
        assert!((boomerang.return_speed - 14.0).abs() < 0.0001);
        assert!((boomerang.hit_cooldown_seconds - 0.25).abs() < 0.0001);
        assert_eq!(boomerang.max_hits_per_target, 1);
    }

    #[test]
    fn invalid_boomerang_projectile_motion_is_rejected() {
        let json = r#"{
            "abilities": [{
                "ability_id": "TEST_BOOMERANG_ABILITY",
                "action_id": "TEST_BOOMERANG",
                "gameplay": {
                    "kind": "SPELL",
                    "cooldown_ms": 700,
                    "uses_global_cooldown": true,
                    "cast_time_ms": 0,
                    "cast_mobility": "MOBILE",
                    "targeting": "TARGET",
                    "requires_target": true,
                    "delivery": {
                        "kind": "PROJECTILE",
                        "speed": 12.0,
                        "max_distance": 8.0,
                        "damage": 22,
                        "spawn_forward": 0.5,
                        "spawn_height": 1.1,
                        "turn_rate": 0.0,
                        "update_interval_seconds": 0.05,
                        "radius": 0.45,
                        "block_behavior": "BLOCKABLE",
                        "motion": {
                            "kind": "BOOMERANG_CASTER",
                            "outbound_distance": 8.0,
                            "return_speed": 0.0,
                            "lifetime_seconds": 3.0,
                            "hit_radius": 0.45,
                            "hit_cooldown_seconds": 0.25,
                            "max_hits_per_target": 1
                        }
                    }
                }
            }]
        }"#;
        let rows = spell_rows_from_json(json).expect("boomerang ability row should parse");

        assert!(definitions_from_rows(rows)
            .expect_err("zero return speed should be rejected")
            .contains("delivery.motion.return_speed"));
    }

    #[test]
    fn unknown_secondary_effect_kinds_are_rejected() {
        let json = r#"{
            "kind": "FIREBALL",
            "cooldown_ms": 450,
            "uses_global_cooldown": true,
            "cast_time_ms": 0,
            "cast_mobility": "MOBILE",
            "targeting": "TARGET",
            "requires_target": true,
            "resource_cost": 0.0,
            "arms_auto_attack_on_cast": true,
            "delivery": {
                "kind": "PROJECTILE",
                "speed": 18.0,
                "max_distance": 30.0,
                "damage": 30,
                "spawn_forward": 1.0,
                "spawn_height": 1.2,
                "turn_rate": 3.0,
                "update_interval_seconds": 0.05,
                "radius": 0.8,
                "block_behavior": "BLOCKABLE",
                "homing_window_seconds": 0.15,
                "impact_effects": [
                    { "kind": "POISON", "duration_ms": 10000 }
                ]
            }
        }"#;

        assert!(serde_json::from_str::<SpellCatalogRow>(json).is_err());
    }

    #[test]
    fn intimidate_area_debuff_and_validation_are_behavior_tunables() {
        let definition = spell_definition_by_str("INTIMIDATE")
            .expect("Intimidate should be loaded from the catalog");
        let area = definition
            .secondary
            .area
            .as_ref()
            .expect("Intimidate should define area secondary data");

        assert_eq!(definition.behavior, SpellBehavior::Area);
        assert_eq!(definition.targeting, SpellTargeting::Self_);
        assert!(!definition.requires_target);
        assert_eq!(definition.cooldown, Duration::from_millis(5_000));
        assert_eq!(definition.block_behavior, BlockBehavior::Unblockable);
        assert_eq!(definition.target_audience, TargetAudience::Hostile);
        assert_eq!(definition.damage, 0);
        assert_eq!(definition.max_distance, 0.0);
        assert_eq!(definition.radius, 6.0);
        assert!(definition.apply_status.is_none());
        assert_eq!(area.impact_effects.len(), 1);
        let effect = &area.impact_effects[0];
        assert_eq!(effect.payload().kind(), StatusEffectKind::Intimidated);
        assert_eq!(effect.explicit_stack_group(), Some("INTIMIDATED"));
        assert_eq!(effect.duration(), Duration::from_millis(4_000));
    }

    #[test]
    fn area_intimidated_effect_requires_positive_duration() {
        let mut row = spell_rows_from_json(PROGRESSION_CATALOG_JSON)
            .expect("catalog should load")
            .into_iter()
            .find(|row| row.kind.as_str() == "INTIMIDATE")
            .expect("Intimidate row should exist");
        row.kind = SpellId::new("TEST_INTIMIDATE").expect("test id should be valid");
        if let SpellCatalogDelivery::Area { impact_effects, .. } = &mut row.delivery {
            match &mut impact_effects[0] {
                ImpactEffectRow::Intimidated { duration_ms, .. } => *duration_ms = 0,
                other => panic!("unexpected Intimidate impact effect: {other:?}"),
            }
        }
        let definition = row.into_definition().expect("row should convert");

        assert!(validate_definition(&definition)
            .expect_err("AREA impact effect without duration should fail")
            .contains("duration_ms"));
    }

    #[test]
    fn point_targeted_spells_require_aim_radius() {
        let mut row = spell_rows_from_json(PROGRESSION_CATALOG_JSON)
            .expect("catalog should load")
            .into_iter()
            .find(|row| row.kind.as_str() == "METEOR")
            .expect("Meteor row should exist");
        row.aim_radius = None;
        let definition = row.into_definition().expect("row should convert");

        assert!(validate_definition(&definition)
            .expect_err("POINT spell without aim radius should fail")
            .contains("aim_radius"));
    }

    #[test]
    fn generic_point_area_requires_range_and_matching_aim_radius() {
        let mut row = spell_rows_from_json(PROGRESSION_CATALOG_JSON)
            .expect("catalog should load")
            .into_iter()
            .find(|row| row.kind.as_str() == "FROST_NOVA")
            .expect("Frost Nova row should exist");
        row.kind = SpellId::new("TEST_POINT_AREA").expect("test id should be valid");
        row.targeting = SpellTargeting::Point;
        row.requires_target = false;
        row.aim_radius = Some(3.0);
        if let SpellCatalogDelivery::Area {
            max_distance,
            radius,
            ..
        } = &mut row.delivery
        {
            *max_distance = 18.0;
            *radius = Some(3.0);
        }
        let definition = row.into_definition().expect("row should convert");

        validate_definition(&definition).expect("valid point area should pass");
    }

    #[test]
    fn generic_point_area_rejects_missing_range() {
        let mut row = spell_rows_from_json(PROGRESSION_CATALOG_JSON)
            .expect("catalog should load")
            .into_iter()
            .find(|row| row.kind.as_str() == "FROST_NOVA")
            .expect("Frost Nova row should exist");
        row.kind = SpellId::new("TEST_POINT_AREA").expect("test id should be valid");
        row.targeting = SpellTargeting::Point;
        row.requires_target = false;
        row.aim_radius = Some(3.0);
        if let SpellCatalogDelivery::Area { radius, .. } = &mut row.delivery {
            *radius = Some(3.0);
        }
        let definition = row.into_definition().expect("row should convert");

        assert!(validate_definition(&definition)
            .expect_err("point area without max range should fail")
            .contains("max_distance"));
    }

    #[test]
    fn generic_point_area_rejects_aim_radius_drift() {
        let mut row = spell_rows_from_json(PROGRESSION_CATALOG_JSON)
            .expect("catalog should load")
            .into_iter()
            .find(|row| row.kind.as_str() == "FROST_NOVA")
            .expect("Frost Nova row should exist");
        row.kind = SpellId::new("TEST_POINT_AREA").expect("test id should be valid");
        row.targeting = SpellTargeting::Point;
        row.requires_target = false;
        row.aim_radius = Some(2.5);
        if let SpellCatalogDelivery::Area {
            max_distance,
            radius,
            ..
        } = &mut row.delivery
        {
            *max_distance = 18.0;
            *radius = Some(3.0);
        }
        let definition = row.into_definition().expect("row should convert");

        assert!(validate_definition(&definition)
            .expect_err("point area UI/gameplay radius drift should fail")
            .contains("aim_radius"));
    }

    #[test]
    fn generic_area_impact_delay_is_delivery_tunable() {
        let mut row = spell_rows_from_json(PROGRESSION_CATALOG_JSON)
            .expect("catalog should load")
            .into_iter()
            .find(|row| row.kind.as_str() == "FROST_NOVA")
            .expect("Frost Nova row should exist");
        row.kind = SpellId::new("TEST_DELAYED_AREA").expect("test id should be valid");
        if let SpellCatalogDelivery::Area {
            impact_delay_ms, ..
        } = &mut row.delivery
        {
            *impact_delay_ms = 325;
        }
        let definition = row.into_definition().expect("row should convert");

        validate_definition(&definition).expect("delayed generic area should pass");
        assert_eq!(
            definition
                .secondary
                .area
                .expect("area secondary data should exist")
                .impact_delay_ms,
            325
        );
    }

    #[test]
    fn meteor_rejects_generic_area_impact_delay() {
        let mut row = spell_rows_from_json(PROGRESSION_CATALOG_JSON)
            .expect("catalog should load")
            .into_iter()
            .find(|row| row.kind.as_str() == "METEOR")
            .expect("Meteor row should exist");
        if let SpellCatalogDelivery::Area {
            impact_delay_ms, ..
        } = &mut row.delivery
        {
            *impact_delay_ms = 500;
        }
        let definition = row.into_definition().expect("row should convert");

        assert!(validate_definition(&definition)
            .expect_err("Meteor should keep bespoke travel timing")
            .contains("impact_delay_ms"));
    }
}

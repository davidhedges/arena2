use std::time::Duration;

use crate::combat::scene_query::CombatAreaShape;

use serde::{Deserialize, Deserializer, Serialize, Serializer};

use crate::combat::{
    AuthoredStatusPayload, DamageType, StackPolicy, StatusApplication, StatusDispelType,
    StatusEffectKind, StatusPayload, StatusPolarity,
};
use crate::relations::TargetAudience;

pub(crate) const SPELL_METEOR: &str = "METEOR";
pub(crate) const SPELL_INSTANT_BEAM: &str = "INSTANT_BEAM";
pub(crate) const SPELL_ELECTROCUTE: &str = "ELECTROCUTE";
pub(crate) const SPELL_NEGATE: &str = "NEGATE";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum BespokeRuntimeSpell {
    InstantBeam,
    Electrocute,
    Meteor,
    Negate,
}

impl BespokeRuntimeSpell {
    #[cfg(test)]
    pub(crate) const ALL: [Self; 4] = [
        Self::InstantBeam,
        Self::Electrocute,
        Self::Meteor,
        Self::Negate,
    ];

    pub(crate) fn from_spell_id(spell_id: &SpellId) -> Option<Self> {
        match spell_id.as_str() {
            SPELL_INSTANT_BEAM => Some(Self::InstantBeam),
            SPELL_ELECTROCUTE => Some(Self::Electrocute),
            SPELL_METEOR => Some(Self::Meteor),
            SPELL_NEGATE => Some(Self::Negate),
            _ => None,
        }
    }

    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::InstantBeam => SPELL_INSTANT_BEAM,
            Self::Electrocute => SPELL_ELECTROCUTE,
            Self::Meteor => SPELL_METEOR,
            Self::Negate => SPELL_NEGATE,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub(crate) struct SpellId(String);

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct SpellIdValidationError {
    message: String,
}

impl SpellIdValidationError {
    fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl std::fmt::Display for SpellIdValidationError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(self.message.as_str())
    }
}

impl std::error::Error for SpellIdValidationError {}

impl SpellId {
    pub(crate) fn new(value: &str) -> Result<Self, SpellIdValidationError> {
        if value.is_empty() {
            return Err(SpellIdValidationError::new("spell id must not be empty"));
        }
        let mut previous_was_underscore = false;
        for (index, ch) in value.chars().enumerate() {
            if index == 0 && !ch.is_ascii_uppercase() {
                return Err(SpellIdValidationError::new(
                    "spell id must start with an uppercase ASCII letter",
                ));
            }
            if !(ch.is_ascii_uppercase() || ch.is_ascii_digit() || ch == '_') {
                return Err(SpellIdValidationError::new(
                    "spell id must contain only uppercase ASCII letters, digits, and underscores",
                ));
            }
            if ch == '_' && previous_was_underscore {
                return Err(SpellIdValidationError::new(
                    "spell id must not contain consecutive underscores",
                ));
            }
            previous_was_underscore = ch == '_';
        }
        if value.ends_with('_') {
            return Err(SpellIdValidationError::new(
                "spell id must not end with an underscore",
            ));
        }
        Ok(Self(value.to_string()))
    }

    pub(crate) fn as_str(&self) -> &str {
        self.0.as_str()
    }
}

impl<'de> Deserialize<'de> for SpellId {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let value = String::deserialize(deserializer)?;
        Self::new(value.as_str()).map_err(serde::de::Error::custom)
    }
}

impl Serialize for SpellId {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_str(self.as_str())
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum SpellBehavior {
    DirectTarget,
    Projectile,
    Area,
    InstantBeam,
    Channel,
    ApplyStatus,
    RemoveStatus,
    ConsumeStatus,
    Aura,
    SelfResource,
}

impl SpellBehavior {
    pub(super) fn as_str(self) -> &'static str {
        match self {
            Self::DirectTarget => "DIRECT_TARGET",
            Self::Projectile => "PROJECTILE",
            Self::Area => "AREA",
            Self::InstantBeam => "INSTANT_BEAM",
            Self::Channel => "CHANNEL",
            Self::ApplyStatus => "APPLY_STATUS",
            Self::RemoveStatus => "REMOVE_STATUS",
            Self::ConsumeStatus => "CONSUME_STATUS",
            Self::Aura => "AURA",
            Self::SelfResource => "SELF_RESOURCE",
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum SpellCastMobility {
    Mobile,
    GroundedStationary,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum SpellTargeting {
    Target,
    Point,
    #[serde(rename = "SELF")]
    Self_,
}

impl SpellTargeting {
    pub(super) fn as_str(self) -> &'static str {
        match self {
            Self::Target => "TARGET",
            Self::Point => "POINT",
            Self::Self_ => "SELF",
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum BlockBehavior {
    Blockable,
    Unblockable,
}

impl BlockBehavior {
    pub(super) fn as_str(self) -> &'static str {
        match self {
            Self::Blockable => "BLOCKABLE",
            Self::Unblockable => "UNBLOCKABLE",
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum SpellParryBehavior {
    Parryable,
    Unparryable,
}

impl SpellParryBehavior {
    pub(super) fn as_str(self) -> &'static str {
        match self {
            Self::Parryable => "PARRYABLE",
            Self::Unparryable => "UNPARRYABLE",
        }
    }
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct ApplyStatusDefinition {
    pub kind: StatusEffectKind,
    pub modifier_scalar: f32,
    #[serde(default)]
    pub slow_pct: f32,
    #[serde(default)]
    pub tick_damage: i32,
    #[serde(default)]
    pub damage_type: String,
    #[serde(default)]
    pub tick_heal: i32,
    #[serde(default)]
    pub tick_interval_ms: u64,
    #[serde(default)]
    pub absorb_amount: i32,
    #[serde(default)]
    pub absorb_cap: i32,
    pub max_stacks: u32,
    pub stack_policy: StackPolicy,
    #[serde(default)]
    pub dispel_types: Vec<StatusDispelType>,
}

impl ApplyStatusDefinition {
    pub(crate) fn authored_payload(&self) -> AuthoredStatusPayload {
        let mut payload = AuthoredStatusPayload::new_with_absorb(
            self.kind,
            self.slow_pct,
            self.tick_damage,
            self.tick_heal,
            self.tick_interval_ms,
            self.modifier_scalar,
            self.absorb_amount,
            self.absorb_cap,
        );
        payload.damage_type = DamageType::from_wire(self.damage_type.as_str());
        payload
    }

    pub(crate) fn payload(&self) -> StatusPayload {
        self.authored_payload().payload()
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct SpellSecondaryTunables {
    pub direct_target: Option<DirectTargetSecondaryTunables>,
    pub projectile: Option<ProjectileSecondaryTunables>,
    pub channel_projectile: Option<ProjectileSecondaryTunables>,
    pub area: Option<AreaSecondaryTunables>,
    pub instant_beam: Option<InstantBeamSecondaryTunables>,
    pub apply_status: Option<ApplyStatusSecondaryTunables>,
    pub remove_status: Option<RemoveStatusSecondaryTunables>,
    pub consume_status: Option<ConsumeStatusSecondaryTunables>,
    pub aura: Option<AuraSecondaryTunables>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct DirectTargetSecondaryTunables {
    pub heal_amount: i32,
    pub parry_behavior: SpellParryBehavior,
    pub impact_effects: Vec<ImpactEffect>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ProjectileSecondaryTunables {
    pub motion: ProjectileMotionTunables,
    pub parry_behavior: SpellParryBehavior,
    pub homing_window_seconds: f32,
    /// Linear damage falloff over the projectile's authoritative lifetime. A value of 1 preserves
    /// full damage; lower values reduce damage toward this multiplier at lifetime end.
    pub damage_multiplier_at_lifetime_end: f32,
    pub impact_effects: Vec<ImpactEffect>,
    // When true, the projectile ignores world geometry for collision (skips terrain fizzle) and tracks
    // the terrain surface height as it travels. For ground-skimming visuals like Ground Slash that need
    // to ride low along the ground without snagging on slopes/bumps.
    pub terrain_conforming: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) enum ProjectileMotionTunables {
    Linear,
    CurvedTarget(CurvedTargetProjectileTunables),
    OrbitCaster(OrbitCasterProjectileTunables),
    BoomerangCaster(BoomerangCasterProjectileTunables),
}

impl ProjectileMotionTunables {
    pub(crate) fn kind(&self) -> &'static str {
        match self {
            Self::Linear => "LINEAR",
            Self::CurvedTarget(_) => "CURVED_TARGET",
            Self::OrbitCaster(_) => "ORBIT_CASTER",
            Self::BoomerangCaster(_) => "BOOMERANG_CASTER",
        }
    }

    pub(crate) fn curved_target(&self) -> Option<&CurvedTargetProjectileTunables> {
        match self {
            Self::CurvedTarget(curve) => Some(curve),
            Self::Linear | Self::OrbitCaster(_) | Self::BoomerangCaster(_) => None,
        }
    }

    pub(crate) fn orbit(&self) -> Option<&OrbitCasterProjectileTunables> {
        match self {
            Self::Linear | Self::CurvedTarget(_) => None,
            Self::OrbitCaster(orbit) => Some(orbit),
            Self::BoomerangCaster(_) => None,
        }
    }

    pub(crate) fn boomerang(&self) -> Option<&BoomerangCasterProjectileTunables> {
        match self {
            Self::BoomerangCaster(boomerang) => Some(boomerang),
            Self::Linear | Self::CurvedTarget(_) | Self::OrbitCaster(_) => None,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct CurvedTargetProjectileTunables {
    pub arc_direction_degrees_min: f32,
    pub arc_direction_degrees_max: f32,
    pub arc_amplitude_min: f32,
    pub arc_amplitude_max: f32,
    pub control_point_fraction: f32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct OrbitCasterProjectileTunables {
    pub projectile_count: u32,
    pub orbit_radius: f32,
    pub orbit_height: f32,
    pub angular_speed_deg_per_sec: f32,
    pub lifetime_seconds: f32,
    pub hit_radius: f32,
    pub hit_cooldown_seconds: f32,
    pub max_hits_per_target: u32,
    pub phase_offset_deg: f32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct BoomerangCasterProjectileTunables {
    pub outbound_distance: f32,
    pub return_speed: f32,
    pub lifetime_seconds: f32,
    pub hit_radius: f32,
    pub hit_cooldown_seconds: f32,
    pub max_hits_per_target: u32,
}

pub(crate) type ImpactEffect = StatusApplication;

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct AreaSecondaryTunables {
    pub impact_delay_ms: u64,
    pub sky_origin: Option<MeteorSkyOrigin>,
    pub shape: CombatAreaShape,
    pub impact_effects: Vec<ImpactEffect>,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct MeteorSkyOrigin {
    pub height: f32,
    pub drift_x: f32,
    pub drift_z: f32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct InstantBeamSecondaryTunables {
    pub charge_scaling: Option<InstantBeamChargeScaling>,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct InstantBeamChargeScaling {
    pub min_damage_scale: f32,
    pub max_charges: u32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct ApplyStatusSecondaryTunables {
    pub parry_behavior: SpellParryBehavior,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct RemoveStatusSecondaryTunables {
    pub statuses: Vec<RemoveStatusDefinition>,
    pub max_count: u32,
    pub polarity: Option<StatusPolarity>,
    pub dispel_types: Vec<StatusDispelType>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ConsumeStatusSecondaryTunables {
    pub max_count: u32,
    pub polarity: Option<StatusPolarity>,
    pub dispel_types: Vec<StatusDispelType>,
    pub heal_per_stack: i32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct RemoveStatusDefinition {
    pub kind: StatusEffectKind,
    pub stack_group: Option<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct AuraSecondaryTunables {
    pub radius: f32,
    pub tick_interval: Duration,
    pub effects: Vec<ImpactEffect>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct SpellDefinition {
    pub kind: SpellId,
    pub cooldown: Duration,
    pub uses_global_cooldown: bool,
    pub global_cooldown: Duration,
    pub cast_time: Duration,
    pub cast_mobility: SpellCastMobility,
    pub behavior: SpellBehavior,
    pub targeting: SpellTargeting,
    pub target_audience: TargetAudience,
    pub requires_target: bool,
    pub requires_target_los: bool,
    pub aim_radius: Option<f32>,
    pub speed: f32,
    pub max_distance: f32,
    pub damage: i32,
    pub damage_type: DamageType,
    pub spawn_forward: f32,
    pub spawn_height: f32,
    pub turn_rate: f32,
    pub update_interval: f32,
    pub duration: f32,
    pub radius: f32,
    pub projectile_radius: f32,
    pub status_stack_group: Option<String>,
    pub block_behavior: BlockBehavior,
    pub primary_resource_cost: f32,
    pub primary_resource_gain_on_cast: f32,
    pub generates_primary_resource_on_cast: bool,
    pub arms_auto_attack_on_cast: bool,
    pub apply_status: Option<ApplyStatusDefinition>,
    pub apply_status_polarity: Option<StatusPolarity>,
    pub secondary: SpellSecondaryTunables,
}

pub(super) fn spell_definitions() -> &'static [SpellDefinition] {
    super::catalog::spell_definitions()
}

#[cfg(test)]
mod tests {
    use std::collections::HashSet;
    use std::time::Duration;

    use crate::combat::{StackPolicy, StatusDispelType, StatusEffectKind};
    use crate::relations::TargetAudience;

    use crate::spells::spell_definition_by_str;

    use super::{spell_definitions, SpellCastMobility, SpellId};

    fn definition(id: &str) -> &'static super::SpellDefinition {
        spell_definition_by_str(id).expect("spell should exist in catalog")
    }

    #[test]
    fn spell_ids_round_trip_as_valid_catalog_data() {
        for def in spell_definitions() {
            let parsed =
                SpellId::new(def.kind.as_str()).expect("catalog spell ids should be valid");
            assert_eq!(parsed, def.kind);
        }
    }

    #[test]
    fn spell_id_validation_rejects_invalid_ids() {
        for invalid in ["", "fireball", "_FOO", "FOO_", "FOO__BAR", "FOO-BAR"] {
            assert!(
                SpellId::new(invalid).is_err(),
                "invalid spell id '{invalid}' should be rejected"
            );
        }
        for valid in ["FIREBALL", "METEOR", "A1_B2"] {
            assert!(
                SpellId::new(valid).is_ok(),
                "valid spell id '{valid}' should be accepted"
            );
        }
    }

    #[test]
    fn removed_shield_spell_is_not_authored() {
        assert!(spell_definition_by_str("SHIELD").is_none());
    }

    #[test]
    fn catalog_entries_are_unique_and_valid() {
        let mut spell_ids = HashSet::new();
        for def in spell_definitions() {
            assert!(
                spell_ids.insert(def.kind.as_str()),
                "duplicate spell id in catalog: {}",
                def.kind.as_str()
            );
            if !def.uses_global_cooldown {
                assert!(
                    def.cooldown.as_millis() > 0,
                    "off-gcd spells still require their own cooldown for {}",
                    def.kind.as_str()
                );
            }
            if let Some(radius) = def.aim_radius {
                assert!(
                    radius >= 0.0,
                    "aim radius must be non-negative for {}",
                    def.kind.as_str()
                );
            }
            match def.behavior {
                super::SpellBehavior::ApplyStatus => {
                    assert!(
                        def.apply_status.is_some(),
                        "APPLY_STATUS spell '{}' must define apply_status",
                        def.kind.as_str()
                    );
                    assert!(
                        def.apply_status_polarity.is_some(),
                        "APPLY_STATUS spell '{}' must define apply_status_polarity",
                        def.kind.as_str()
                    );
                }
                super::SpellBehavior::RemoveStatus => {
                    assert!(
                        def.apply_status.is_none(),
                        "REMOVE_STATUS spell '{}' should not define apply_status",
                        def.kind.as_str()
                    );
                    assert!(
                        def.secondary.remove_status.is_some(),
                        "REMOVE_STATUS spell '{}' must define remove_status secondary data",
                        def.kind.as_str()
                    );
                }
                super::SpellBehavior::ConsumeStatus => {
                    assert!(
                        def.apply_status.is_none(),
                        "CONSUME_STATUS spell '{}' should not define apply_status",
                        def.kind.as_str()
                    );
                    assert!(
                        def.secondary.consume_status.is_some(),
                        "CONSUME_STATUS spell '{}' must define consume_status secondary data",
                        def.kind.as_str()
                    );
                }
                super::SpellBehavior::SelfResource => {
                    assert!(
                        def.apply_status.is_none(),
                        "SELF_RESOURCE spell '{}' should not define apply_status",
                        def.kind.as_str()
                    );
                    assert!(
                        def.primary_resource_gain_on_cast > 0.0,
                        "SELF_RESOURCE spell '{}' must grant primary resource",
                        def.kind.as_str()
                    );
                }
                _ => {
                    assert!(
                        def.apply_status.is_none(),
                        "non-APPLY_STATUS spell '{}' should not define apply_status",
                        def.kind.as_str()
                    );
                }
            }
            if def.arms_auto_attack_on_cast {
                assert_eq!(
                    def.targeting,
                    super::SpellTargeting::Target,
                    "auto-attack arming spell '{}' must use TARGET targeting",
                    def.kind.as_str()
                );
                assert!(
                    def.requires_target,
                    "auto-attack arming spell '{}' must require a target",
                    def.kind.as_str()
                );
            }
        }
    }

    #[test]
    fn cast_mobility_policy_matches_spell_design() {
        for id in [
            "FIREBALL",
            "FROST_NOVA",
            "NOVA",
            "NEGATE",
            "BLINDING_LIGHT",
            "GLACIAL_SPIKE",
            "FROZEN_GRASP",
            "FROST_NEEDLE",
            "MOMENTUM",
            "INTIMIDATE",
            "ENRAGE",
            "SECOND_WIND",
            "SHOCKWAVE",
        ] {
            assert_ne!(
                definition(id).cast_mobility,
                SpellCastMobility::GroundedStationary
            );
        }

        for id in [
            "ICICLE",
            "METEOR",
            "INSTANT_BEAM",
            "ELECTROCUTE",
            "FROZEN_SPLINTERS",
            "MAGIC_MISSILE",
        ] {
            assert_eq!(
                definition(id).cast_mobility,
                SpellCastMobility::GroundedStationary
            );
        }
    }

    #[test]
    fn hostile_targeted_auto_attack_arming_spells_remain_explicit() {
        for id in [
            "FIREBALL",
            "ICICLE",
            "INSTANT_BEAM",
            "ELECTROCUTE",
            "FROZEN_SPLINTERS",
            "MAGIC_MISSILE",
            "GLACIAL_SPIKE",
        ] {
            assert!(definition(id).arms_auto_attack_on_cast);
        }

        for id in [
            "METEOR",
            "FROST_NOVA",
            "NOVA",
            "NEGATE",
            "BLINDING_LIGHT",
            "FROZEN_GRASP",
            "FROST_NEEDLE",
            "MOMENTUM",
            "BATTLE_CRY",
            "GIANT_SWING",
            "INTIMIDATE",
            "IRON_WILL",
            "DEFIANCE",
            "ENRAGE",
            "SECOND_WIND",
            "SHOCKWAVE",
        ] {
            assert!(!definition(id).arms_auto_attack_on_cast);
        }
    }

    #[test]
    fn momentum_catalog_matches_v1_buff_defaults() {
        let definition = definition("MOMENTUM");

        assert_eq!(definition.kind.as_str(), "MOMENTUM");
        assert_eq!(definition.cooldown, Duration::from_millis(12_000));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "APPLY_STATUS");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.duration - 4.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert_eq!(definition.status_stack_group.as_deref(), Some("MOMENTUM"));
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Momentum should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::MoveSlowImmunity);
        assert_eq!(status.modifier_scalar, 0.0);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
    }

    #[test]
    fn battle_cry_catalog_matches_damage_amp_buff_defaults() {
        let definition = definition("BATTLE_CRY");

        assert_eq!(definition.kind.as_str(), "BATTLE_CRY");
        assert_eq!(definition.cooldown, Duration::from_millis(12_000));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "APPLY_STATUS");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.duration - 300.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert_eq!(definition.status_stack_group.as_deref(), Some("BATTLE_CRY"));
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Battle Cry should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::DamageAmp);
        assert!((status.modifier_scalar - 0.1).abs() < 0.0001);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
    }

    #[test]
    fn fortify_catalog_matches_temporary_hitpoints_buff_defaults() {
        let definition = definition("FORTIFY");

        assert_eq!(definition.kind.as_str(), "FORTIFY");
        assert_eq!(definition.cooldown, Duration::from_millis(12_000));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "APPLY_STATUS");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.duration - 8.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert_eq!(definition.status_stack_group.as_deref(), Some("FORTIFY"));
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Fortify should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::TemporaryHitpoints);
        assert_eq!(status.absorb_amount, 30);
        assert_eq!(status.absorb_cap, 60);
        assert_eq!(status.max_stacks, 2);
        assert_eq!(status.stack_policy, StackPolicy::AddStackRefresh);
    }

    #[test]
    fn iron_will_catalog_removes_intimidated_and_fear() {
        let definition = definition("IRON_WILL");

        assert_eq!(definition.kind.as_str(), "IRON_WILL");
        assert_eq!(definition.cooldown, Duration::from_millis(30_000));
        assert!(!definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "REMOVE_STATUS");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        let remove_status = definition
            .secondary
            .remove_status
            .as_ref()
            .expect("Iron Will should define remove-status payloads");
        let kinds: Vec<_> = remove_status
            .statuses
            .iter()
            .map(|status| status.kind)
            .collect();
        assert_eq!(
            kinds,
            vec![StatusEffectKind::Intimidated, StatusEffectKind::Fear]
        );
        assert!(remove_status
            .statuses
            .iter()
            .all(|status| status.stack_group.is_none()));
    }

    #[test]
    fn cleansing_touch_catalog_filters_magic_debuffs_in_melee_range() {
        let definition = definition("CLEANSING_TOUCH");

        assert_eq!(definition.kind.as_str(), "CLEANSING_TOUCH");
        assert_eq!(definition.behavior.as_str(), "REMOVE_STATUS");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert!(!definition.requires_target);
        assert!((definition.max_distance - 2.5).abs() < 0.0001);
        assert_eq!(definition.target_audience, TargetAudience::PartyOrSelf);

        let remove_status = definition
            .secondary
            .remove_status
            .as_ref()
            .expect("Cleansing Touch should define remove-status filters");
        assert!(remove_status.statuses.is_empty());
        assert_eq!(remove_status.max_count, 1);
        assert_eq!(
            remove_status.polarity,
            Some(crate::combat::StatusPolarity::Debuff)
        );
        assert_eq!(remove_status.dispel_types, vec![StatusDispelType::Magic]);
    }

    #[test]
    fn absolution_catalog_filters_all_magic_debuffs_at_range() {
        let definition = definition("ABSOLUTION");

        assert_eq!(definition.kind.as_str(), "ABSOLUTION");
        assert_eq!(definition.cooldown, Duration::from_millis(120_000));
        assert_eq!(definition.behavior.as_str(), "REMOVE_STATUS");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert!(!definition.requires_target);
        assert!((definition.max_distance - 30.0).abs() < 0.0001);
        assert_eq!(definition.target_audience, TargetAudience::PartyOrSelf);

        let remove_status = definition
            .secondary
            .remove_status
            .as_ref()
            .expect("Absolution should define remove-status filters");
        assert!(remove_status.statuses.is_empty());
        assert_eq!(remove_status.max_count, 0);
        assert_eq!(
            remove_status.polarity,
            Some(crate::combat::StatusPolarity::Debuff)
        );
        assert_eq!(remove_status.dispel_types, vec![StatusDispelType::Magic]);
    }

    #[test]
    fn defiance_catalog_matches_damage_reduction_buff_defaults() {
        let definition = definition("DEFIANCE");

        assert_eq!(definition.kind.as_str(), "DEFIANCE");
        assert_eq!(definition.cooldown, Duration::from_millis(60_000));
        assert!(!definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "APPLY_STATUS");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.duration - 5.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert_eq!(definition.status_stack_group.as_deref(), Some("DEFIANCE"));
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Defiance should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::DamageTakenReduction);
        assert!((status.modifier_scalar - 0.1).abs() < 0.0001);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
    }

    #[test]
    fn giant_swing_catalog_matches_melee_modifier_buff_defaults() {
        let definition = definition("GIANT_SWING");

        assert_eq!(definition.kind.as_str(), "GIANT_SWING");
        assert_eq!(definition.cooldown, Duration::from_millis(12_000));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "APPLY_STATUS");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.duration - 12.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("GIANT_SWING")
        );
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Giant Swing should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::MeleeAttackModifier);
        assert_eq!(status.modifier_scalar, 0.0);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
    }

    #[test]
    fn enrage_catalog_matches_damage_amp_buff_defaults() {
        let definition = definition("ENRAGE");

        assert_eq!(definition.kind.as_str(), "ENRAGE");
        assert_eq!(definition.behavior.as_str(), "APPLY_STATUS");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.duration - 12.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert_eq!(definition.status_stack_group.as_deref(), Some("ENRAGE"));
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Enrage should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::DamageAmp);
        assert!((status.modifier_scalar - 0.5).abs() < 0.0001);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert!((definition.primary_resource_gain_on_cast - 0.0).abs() < 0.0001);
        assert!(!definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn second_wind_catalog_grants_stamina_without_status_payload() {
        let definition = definition("SECOND_WIND");

        assert_eq!(definition.kind.as_str(), "SECOND_WIND");
        assert_eq!(definition.behavior.as_str(), "SELF_RESOURCE");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert_eq!(definition.apply_status, None);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert!((definition.primary_resource_gain_on_cast - 50.0).abs() < 0.0001);
        assert!(definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn blinding_light_catalog_matches_targeted_avoidance_buff_defaults() {
        let definition = definition("BLINDING_LIGHT");

        assert_eq!(definition.kind.as_str(), "BLINDING_LIGHT");
        assert_eq!(definition.cooldown, Duration::from_millis(30_000));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "APPLY_STATUS");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.duration - 5.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("BLINDING_LIGHT")
        );
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Blinding Light should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::TargetedAbilityAvoidance);
        assert_eq!(status.modifier_scalar, 0.0);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert!((definition.primary_resource_gain_on_cast - 0.0).abs() < 0.0001);
        assert!(!definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn glacial_spike_catalog_matches_targeted_cold_freeze_defaults() {
        let definition = definition("GLACIAL_SPIKE");

        assert_eq!(definition.kind.as_str(), "GLACIAL_SPIKE");
        assert_eq!(definition.cooldown, Duration::from_millis(1_200));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "DIRECT_TARGET");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert!(definition.requires_target);
        assert_eq!(definition.cast_time, Duration::from_millis(2_000));
        assert_eq!(definition.damage, 35);
        assert_eq!(definition.damage_type.as_str(), "COLD");
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert!(definition.arms_auto_attack_on_cast);
        let direct_target = definition
            .secondary
            .direct_target
            .as_ref()
            .expect("Glacial Spike should define direct-target secondary data");
        assert_eq!(direct_target.impact_effects.len(), 1);
        let status = &direct_target.impact_effects[0];
        assert_eq!(status.payload().kind(), StatusEffectKind::Freeze);
        assert_eq!(status.duration(), Duration::from_millis(1_200));
        assert!((definition.primary_resource_gain_on_cast - 0.0).abs() < 0.0001);
        assert!(!definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn frozen_splinters_catalog_matches_channel_projectile_defaults() {
        let definition = definition("FROZEN_SPLINTERS");

        assert_eq!(definition.kind.as_str(), "FROZEN_SPLINTERS");
        assert_eq!(definition.cooldown, Duration::from_millis(1_100));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "CHANNEL");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert!(definition.requires_target);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.damage, 2);
        assert_eq!(definition.damage_type.as_str(), "COLD");
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert!((definition.update_interval - 0.1).abs() < 0.0001);
        assert!((definition.duration - 2.25).abs() < 0.0001);
        assert!(definition.arms_auto_attack_on_cast);
        let projectile = definition
            .secondary
            .channel_projectile
            .as_ref()
            .expect("Frozen Splinters should define channel projectile data");
        assert_eq!(projectile.motion.kind(), "LINEAR");
        assert_eq!(projectile.parry_behavior.as_str(), "PARRYABLE");
    }

    #[test]
    fn magic_missile_catalog_matches_channel_projectile_defaults() {
        let definition = definition("MAGIC_MISSILE");

        assert_eq!(definition.kind.as_str(), "MAGIC_MISSILE");
        assert_eq!(definition.cooldown, Duration::from_millis(1_100));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "CHANNEL");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert!(definition.requires_target);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.damage, 2);
        assert_eq!(definition.damage_type.as_str(), "ARCANE");
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert!((definition.update_interval - 0.1).abs() < 0.0001);
        assert!((definition.duration - 2.25).abs() < 0.0001);
        assert!(definition.arms_auto_attack_on_cast);
        let projectile = definition
            .secondary
            .channel_projectile
            .as_ref()
            .expect("Magic Missile should define channel projectile data");
        assert_eq!(projectile.motion.kind(), "CURVED_TARGET");
        let curve = projectile
            .motion
            .curved_target()
            .expect("Magic Missile should author curved target motion");
        assert!((curve.arc_direction_degrees_min - 20.0).abs() < 0.0001);
        assert!((curve.arc_direction_degrees_max - 160.0).abs() < 0.0001);
        assert!((curve.arc_amplitude_min - 1.25).abs() < 0.0001);
        assert!((curve.arc_amplitude_max - 4.25).abs() < 0.0001);
        assert!((curve.control_point_fraction - 0.5).abs() < 0.0001);
        assert_eq!(projectile.parry_behavior.as_str(), "PARRYABLE");
    }

    #[test]
    fn frozen_grasp_catalog_matches_self_area_root_defaults() {
        let definition = definition("FROZEN_GRASP");

        assert_eq!(definition.kind.as_str(), "FROZEN_GRASP");
        assert_eq!(definition.cooldown, Duration::from_millis(1_200));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "AREA");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert_eq!(definition.damage, 0);
        assert_eq!(definition.damage_type.as_str(), "COLD");
        assert!((definition.radius - 4.6).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert!(!definition.arms_auto_attack_on_cast);
        let area = definition
            .secondary
            .area
            .as_ref()
            .expect("Frozen Grasp should define area secondary data");
        assert_eq!(area.impact_effects.len(), 1);
        let status = &area.impact_effects[0];
        assert_eq!(status.payload().kind(), StatusEffectKind::Root);
        assert_eq!(status.duration(), Duration::from_millis(1_200));
        assert!((definition.primary_resource_gain_on_cast - 0.0).abs() < 0.0001);
        assert!(!definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn frost_needle_catalog_matches_delayed_point_area_defaults() {
        let definition = definition("FROST_NEEDLE");

        assert_eq!(definition.kind.as_str(), "FROST_NEEDLE");
        assert_eq!(definition.cooldown, Duration::from_millis(1_400));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "AREA");
        assert_eq!(definition.targeting.as_str(), "POINT");
        assert!(!definition.requires_target);
        assert_eq!(definition.damage, 38);
        assert_eq!(definition.damage_type.as_str(), "COLD");
        assert!((definition.radius - 2.4).abs() < 0.0001);
        assert!((definition.max_distance - 12.0).abs() < 0.0001);
        assert!(
            (definition
                .aim_radius
                .expect("Frost Needle should expose aim radius")
                - 2.4)
                .abs()
                < 0.0001
        );
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert!(!definition.arms_auto_attack_on_cast);
        let area = definition
            .secondary
            .area
            .as_ref()
            .expect("Frost Needle should define area secondary data");
        assert_eq!(area.impact_delay_ms, 500);
        assert!(area.impact_effects.is_empty());
        assert!((definition.primary_resource_gain_on_cast - 0.0).abs() < 0.0001);
        assert!(!definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn shockwave_catalog_matches_self_area_damage_defaults() {
        let definition = definition("SHOCKWAVE");

        assert_eq!(definition.kind.as_str(), "SHOCKWAVE");
        assert_eq!(definition.cooldown, Duration::from_millis(2_000));
        assert_eq!(definition.behavior.as_str(), "AREA");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert_eq!(definition.damage, 28);
        assert!((definition.radius - 4.6).abs() < 0.0001);
        assert!((definition.max_distance - 0.0).abs() < 0.0001);
        assert_eq!(definition.apply_status, None);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert!((definition.primary_resource_gain_on_cast - 0.0).abs() < 0.0001);
        assert!(!definition.generates_primary_resource_on_cast);
        let area = definition
            .secondary
            .area
            .as_ref()
            .expect("Shockwave should define area secondary data");
        assert_eq!(area.impact_effects.len(), 1);
        let effect = &area.impact_effects[0];
        assert_eq!(effect.payload().kind(), StatusEffectKind::Stagger);
        assert_eq!(effect.duration(), Duration::from_millis(1_000));
    }

    #[test]
    fn intimidate_catalog_matches_area_status_defaults() {
        let definition = definition("INTIMIDATE");

        assert_eq!(definition.kind.as_str(), "INTIMIDATE");
        assert_eq!(definition.cooldown, Duration::from_millis(5_000));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "AREA");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert!(!definition.requires_target);
        assert!((definition.radius - 6.0).abs() < 0.0001);
        assert!((definition.max_distance - 0.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert!(definition.status_stack_group.is_none());
        assert!(definition.apply_status_polarity.is_none());
        assert!(definition.apply_status.is_none());
        let area = definition
            .secondary
            .area
            .as_ref()
            .expect("Intimidate should define area secondary data");
        assert_eq!(area.impact_effects.len(), 1);
        let status = &area.impact_effects[0];
        assert_eq!(status.payload().kind(), StatusEffectKind::Intimidated);
        assert_eq!(status.explicit_stack_group(), Some("INTIMIDATED"));
        assert_eq!(status.duration(), Duration::from_millis(4_000));
        assert_eq!(status.max_stacks(), 1);
        assert_eq!(status.stack_policy(), StackPolicy::Refresh);
    }
}

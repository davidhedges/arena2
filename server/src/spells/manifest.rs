use std::time::Duration;

use crate::combat::scene_query::CombatAreaShape;

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use spacetimedb::Identity;

use crate::combat::{
    AuthoredStatusPayload, DamageType, EffectPacket, StackPolicy, StatusApplication,
    StatusDispelType, StatusEffectKind, StatusPayload, StatusPolarity,
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
    PersistentArea,
    InstantBeam,
    Channel,
    ApplyStatus,
    RemoveStatus,
    ConsumeStatus,
    Aura,
    Emanation,
    Immolation,
    Sanctuary,
    NecroPrison,
    SelfResource,
    SelfTeleport,
    Transpose,
    WorldObstacle,
    Recall,
}

impl SpellBehavior {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::DirectTarget => "DIRECT_TARGET",
            Self::Projectile => "PROJECTILE",
            Self::Area => "AREA",
            Self::PersistentArea => "PERSISTENT_AREA",
            Self::InstantBeam => "INSTANT_BEAM",
            Self::Channel => "CHANNEL",
            Self::ApplyStatus => "APPLY_STATUS",
            Self::RemoveStatus => "REMOVE_STATUS",
            Self::ConsumeStatus => "CONSUME_STATUS",
            Self::Aura => "AURA",
            Self::Emanation => "EMANATION",
            Self::Immolation => "IMMOLATION",
            Self::Sanctuary => "SANCTUARY",
            Self::NecroPrison => "NECRO_PRISON",
            Self::SelfResource => "SELF_RESOURCE",
            Self::SelfTeleport => "SELF_TELEPORT",
            Self::Transpose => "TRANSPOSE",
            Self::WorldObstacle => "WORLD_OBSTACLE",
            Self::Recall => "RECALL",
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
    pub channel: Option<ChannelSecondaryTunables>,
    pub channel_area: Option<ChannelAreaSecondaryTunables>,
    pub channel_projectile: Option<ProjectileSecondaryTunables>,
    pub area: Option<AreaSecondaryTunables>,
    pub persistent_area: Option<PersistentAreaSecondaryTunables>,
    pub instant_beam: Option<InstantBeamSecondaryTunables>,
    pub apply_status: Option<ApplyStatusSecondaryTunables>,
    pub remove_status: Option<RemoveStatusSecondaryTunables>,
    pub consume_status: Option<ConsumeStatusSecondaryTunables>,
    pub aura: Option<AuraSecondaryTunables>,
    pub emanation: Option<EmanationSecondaryTunables>,
    pub immolation: Option<ImmolationSecondaryTunables>,
    pub sanctuary: Option<SanctuarySecondaryTunables>,
    pub necro_prison: Option<NecroPrisonSecondaryTunables>,
    pub world_obstacle: Option<WorldObstacleSecondaryTunables>,
    pub recall: Option<RecallSecondaryTunables>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct DirectTargetSecondaryTunables {
    pub heal_amount: i32,
    pub self_damage_amount: i32,
    /// Chooses damage for hostile targets and healing for non-hostile targets.
    pub relation_aware: bool,
    pub parry_behavior: SpellParryBehavior,
    pub impact_effects: Vec<ImpactEffect>,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct ChannelSecondaryTunables {
    pub heal_amount: i32,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ChannelAreaSecondaryTunables {
    pub radius: f32,
    pub impact_effects: Vec<ImpactEffect>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct PersistentAreaSecondaryTunables {
    pub pulse_interval: Duration,
    pub effect_target_audience: TargetAudience,
    pub heal_amount: i32,
    pub mana_restore_amount: f32,
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
    /// Optional target-max-health damage used instead of the authored flat damage.
    pub damage_target_max_health_fraction: f32,
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
    TravelingArea(TravelingAreaProjectileTunables),
}

impl ProjectileMotionTunables {
    pub(crate) fn kind(&self) -> &'static str {
        match self {
            Self::Linear => "LINEAR",
            Self::CurvedTarget(_) => "CURVED_TARGET",
            Self::OrbitCaster(_) => "ORBIT_CASTER",
            Self::BoomerangCaster(_) => "BOOMERANG_CASTER",
            Self::TravelingArea(_) => "TRAVELING_AREA",
        }
    }

    pub(crate) fn curved_target(&self) -> Option<&CurvedTargetProjectileTunables> {
        match self {
            Self::CurvedTarget(curve) => Some(curve),
            Self::Linear
            | Self::OrbitCaster(_)
            | Self::BoomerangCaster(_)
            | Self::TravelingArea(_) => None,
        }
    }

    pub(crate) fn orbit(&self) -> Option<&OrbitCasterProjectileTunables> {
        match self {
            Self::Linear | Self::CurvedTarget(_) | Self::TravelingArea(_) => None,
            Self::OrbitCaster(orbit) => Some(orbit),
            Self::BoomerangCaster(_) => None,
        }
    }

    pub(crate) fn boomerang(&self) -> Option<&BoomerangCasterProjectileTunables> {
        match self {
            Self::BoomerangCaster(boomerang) => Some(boomerang),
            Self::Linear
            | Self::CurvedTarget(_)
            | Self::OrbitCaster(_)
            | Self::TravelingArea(_) => None,
        }
    }

    pub(crate) fn traveling_area(&self) -> Option<&TravelingAreaProjectileTunables> {
        match self {
            Self::TravelingArea(area) => Some(area),
            Self::Linear
            | Self::CurvedTarget(_)
            | Self::OrbitCaster(_)
            | Self::BoomerangCaster(_) => None,
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
    /// When present, casts join one persistent orbit group for the same caster and action.
    /// Existing fixed-batch orbit spells omit this value and retain their authored behavior.
    pub max_active_projectiles: Option<u32>,
    /// Whether a successful hostile contact terminates this projectile.
    pub consume_on_contact: bool,
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
    /// Time spent stationary at maximum range before beginning the return leg.
    pub apex_hold_seconds: f32,
    pub return_speed: f32,
    pub lifetime_seconds: f32,
    pub hit_radius: f32,
    pub hit_cooldown_seconds: f32,
    pub max_hits_per_target: u32,
    /// Optional moving rectangular contact volume, aligned perpendicular to travel.
    /// Zero values preserve the legacy swept-radius contact behavior.
    pub hitbox_length: f32,
    pub hitbox_width: f32,
    /// When enabled, confirmed HP damage is accumulated and restored when the projectile
    /// completes its return to the caster.
    pub heal_caster_on_return: bool,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct TravelingAreaProjectileTunables {
    /// Width across the wave, perpendicular to its direction of travel.
    pub hitbox_length: f32,
    /// Depth of the moving contact volume along its direction of travel.
    pub hitbox_width: f32,
    /// Contact count is tracked per target for the lifetime of the wave.
    pub max_hits_per_target: u32,
    /// Optional terminal eruption when the traveling area reaches its end.
    pub terminal_radius: f32,
    pub terminal_damage: i32,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) enum ImpactEffect {
    Status(StatusApplication),
    StatusWithPolarity {
        status: StatusApplication,
        polarity: StatusPolarity,
        target_audience: TargetAudience,
    },
    ChanceStatus {
        status: StatusApplication,
        chance: f32,
        polarity: StatusPolarity,
        target_audience: TargetAudience,
    },
    Knockback {
        distance_meters: f32,
    },
    RemoveStatus {
        polarity: Option<StatusPolarity>,
        dispel_types: Vec<StatusDispelType>,
        max_count: u32,
    },
    RemoveStatusByDamageType {
        polarity: Option<StatusPolarity>,
        damage_type: DamageType,
        max_count: u32,
    },
    InterruptCast,
    InterruptCastWithDamage {
        damage: i32,
        damage_type: String,
    },
}

impl PartialEq<StatusApplication> for ImpactEffect {
    fn eq(&self, other: &StatusApplication) -> bool {
        self.as_status().is_some_and(|status| status == other)
    }
}

impl ImpactEffect {
    pub(crate) fn as_status(&self) -> Option<&StatusApplication> {
        match self {
            Self::Status(status) => Some(status),
            Self::StatusWithPolarity { status, .. } | Self::ChanceStatus { status, .. } => {
                Some(status)
            }
            Self::Knockback { .. }
            | Self::RemoveStatus { .. }
            | Self::RemoveStatusByDamageType { .. }
            | Self::InterruptCast
            | Self::InterruptCastWithDamage { .. } => None,
        }
    }

    pub(crate) fn chance_roll_succeeds(
        &self,
        roll_key: &str,
        target: Identity,
        effect_index: usize,
    ) -> bool {
        let Self::ChanceStatus { chance, .. } = self else {
            return true;
        };
        if !chance.is_finite() || *chance <= 0.0 {
            return false;
        }
        if *chance >= 1.0 {
            return true;
        }

        let mut hash = 0xcbf29ce484222325u64 ^ effect_index as u64;
        let target_hex = target.to_hex();
        for byte in roll_key.bytes().chain(target_hex.bytes()) {
            hash ^= byte as u64;
            hash = hash.wrapping_mul(0x100000001b3);
        }
        hash ^= hash >> 33;
        hash = hash.wrapping_mul(0xff51afd7ed558ccd);
        hash ^= hash >> 33;
        hash = hash.wrapping_mul(0xc4ceb9fe1a85ec53);
        hash ^= hash >> 33;
        let roll = ((hash >> 40) as f32) / ((1u64 << 24) as f32);
        roll < chance.clamp(0.0, 1.0)
    }

    pub(crate) fn requires_positive_damage(&self) -> bool {
        self.as_status()
            .is_some_and(StatusApplication::requires_positive_damage)
    }

    #[allow(clippy::too_many_arguments)]
    pub(crate) fn to_effect_packet(
        &self,
        source: Identity,
        target: Identity,
        spell_id: &str,
        polarity: StatusPolarity,
        action_key: &str,
        dir_x: f32,
        dir_z: f32,
    ) -> EffectPacket {
        let target_audience = match polarity {
            StatusPolarity::Debuff => TargetAudience::Hostile,
            StatusPolarity::Buff => TargetAudience::PartyOrSelf,
        };
        self.to_effect_packet_for_audience(
            source,
            target,
            spell_id,
            polarity,
            target_audience,
            action_key,
            dir_x,
            dir_z,
        )
    }

    #[allow(clippy::too_many_arguments)]
    pub(crate) fn to_effect_packet_for_audience(
        &self,
        source: Identity,
        target: Identity,
        spell_id: &str,
        polarity: StatusPolarity,
        target_audience: TargetAudience,
        action_key: &str,
        dir_x: f32,
        dir_z: f32,
    ) -> EffectPacket {
        match self {
            Self::Status(status) => status.to_effect_packet_for_audience(
                source,
                target,
                spell_id,
                polarity,
                target_audience,
                action_key,
            ),
            Self::StatusWithPolarity {
                status,
                polarity,
                target_audience,
            }
            | Self::ChanceStatus {
                status,
                polarity,
                target_audience,
                ..
            } => status.to_effect_packet_for_audience(
                source,
                target,
                spell_id,
                *polarity,
                *target_audience,
                action_key,
            ),
            Self::Knockback { distance_meters } => EffectPacket::Knockback {
                source,
                target,
                spell_id: spell_id.to_string(),
                dir_x,
                dir_z,
                distance_meters: *distance_meters,
            },
            Self::RemoveStatus {
                polarity,
                dispel_types,
                max_count,
            } => EffectPacket::RemoveStatusByFilter {
                target,
                polarity: *polarity,
                dispel_types: dispel_types.clone(),
                max_count: *max_count,
            },
            Self::RemoveStatusByDamageType {
                polarity,
                damage_type,
                max_count,
            } => EffectPacket::RemoveStatusByDamageType {
                target,
                polarity: *polarity,
                damage_type: *damage_type,
                max_count: *max_count,
            },
            Self::InterruptCast => EffectPacket::InterruptCast {
                source,
                target,
                spell_id: spell_id.to_string(),
            },
            Self::InterruptCastWithDamage {
                damage,
                damage_type,
            } => EffectPacket::InterruptCastWithDamage {
                source,
                target,
                spell_id: spell_id.to_string(),
                damage: *damage,
                damage_type: DamageType::from_wire(damage_type.as_str()),
            },
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct AreaSecondaryTunables {
    pub impact_delay_ms: u64,
    pub sky_origin: Option<MeteorSkyOrigin>,
    pub shape: CombatAreaShape,
    pub impact_effects: Vec<ImpactEffect>,
    pub consume_caster_burns: bool,
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

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ApplyStatusSecondaryTunables {
    pub apply_to_caster: bool,
    pub parry_behavior: SpellParryBehavior,
    pub staged_applications: Vec<StagedStatusApplicationTunables>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct StagedStatusApplicationTunables {
    pub delay: Duration,
    pub duration: Duration,
    pub status_stack_group: Option<String>,
    pub status: ApplyStatusDefinition,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct RemoveStatusSecondaryTunables {
    pub statuses: Vec<RemoveStatusDefinition>,
    pub max_count: u32,
    pub polarity: Option<StatusPolarity>,
    pub dispel_types: Vec<StatusDispelType>,
    /// Zero removes the selected status row; positive values remove only that many stacks.
    pub stacks_per_status: u32,
    /// Optional self-heal based on the caster's maximum health.
    pub heal_caster_max_health_fraction: f32,
    /// Optional burn the removal costs its target, as a fraction of the target's
    /// maximum health. Flat authored damage: it never crits, scales, or kills.
    pub damage_target_max_health_fraction: f32,
    /// Moves matching status rows to the caster instead of removing them.
    pub transfer_to_caster: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ConsumeStatusSecondaryTunables {
    pub max_count: u32,
    pub polarity: Option<StatusPolarity>,
    pub dispel_types: Vec<StatusDispelType>,
    pub heal_per_stack: i32,
    pub deal_remaining_dot_damage: bool,
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
pub(crate) struct EmanationSecondaryTunables {
    pub radius: f32,
    pub pulse_interval: Duration,
    pub impact_effects: Vec<ImpactEffect>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ImmolationSecondaryTunables {
    pub damage_interval: Duration,
    pub stack_interval: Duration,
    pub stack_duration: Duration,
    pub max_stacks: u32,
    pub max_health_damage_per_stack: f32,
    pub damage_amp_per_stack: f32,
    pub status_stack_group: String,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct SanctuarySecondaryTunables {
    pub duration: Duration,
    pub visual_resource_path: String,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct NecroPrisonSecondaryTunables {
    pub duration: Duration,
    pub visual_resource_path: String,
    pub dissipate_visual_resource_path: String,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct WorldObstacleSecondaryTunables {
    pub forward_distance: f32,
    pub duration: Duration,
    pub visual_yaw_offset_degrees: f32,
    pub collider_local_center: [f32; 3],
    pub collider_local_rotation: [f32; 4],
    pub collider_size: [f32; 3],
    pub visual_resource_path: String,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct RecallSecondaryTunables {
    pub replay_cooldown: Duration,
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
    pub self_health_cost: i32,
    pub self_resource_gain_kind: String,
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

    use spacetimedb::Identity;

    use crate::combat::{
        DamageType, EffectPacket, StackPolicy, StatusApplication, StatusDispelType,
        StatusEffectKind, StatusPayload, StatusPolarity, StatusStackGroupDefault,
    };
    use crate::relations::TargetAudience;

    use crate::spells::spell_definition_by_str;

    use super::{
        spell_definitions, BlockBehavior, ImpactEffect, SpellBehavior, SpellCastMobility, SpellId,
        SpellParryBehavior, SpellTargeting,
    };

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
    fn chance_status_rolls_are_stable_per_target_and_independent_per_pulse() {
        let effect = ImpactEffect::ChanceStatus {
            status: StatusApplication::new(
                StatusPayload::Stun,
                Duration::from_millis(500),
                Some("EARTHQUAKE_STUN".to_string()),
                StatusStackGroupDefault::Global("EARTHQUAKE_STUN"),
                1,
                StackPolicy::Refresh,
            ),
            chance: 0.25,
            polarity: StatusPolarity::Debuff,
            target_audience: TargetAudience::Hostile,
        };
        let targets: Vec<_> = (1..=64)
            .map(|value| Identity::from_hex(format!("{value:064x}").as_str()).unwrap())
            .collect();
        let first: Vec<_> = targets
            .iter()
            .map(|target| effect.chance_roll_succeeds("quake:1000000", *target, 1))
            .collect();
        let repeated: Vec<_> = targets
            .iter()
            .map(|target| effect.chance_roll_succeeds("quake:1000000", *target, 1))
            .collect();
        let second: Vec<_> = targets
            .iter()
            .map(|target| effect.chance_roll_succeeds("quake:2000000", *target, 1))
            .collect();

        assert_eq!(first, repeated);
        assert!(first.iter().any(|success| *success));
        assert!(first.iter().any(|success| !*success));
        assert!(first.iter().zip(second).any(|(left, right)| *left != right));
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
            "PROTECTION",
            "GLACIAL_SPIKE",
            "FROZEN_GRASP",
            "GUST_OF_WIND",
            "BUFFET",
            "EARTH_BLAST",
            "TIDAL_BLAST",
            "LAVA_BLAST",
            "WIND_BLAST",
            "GIGANTISM",
            "FLURRY",
            "VERDANT_SPIRITS",
            "REBUKE",
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
            "BLIZZARD",
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
            "EARTH_BLAST",
            "TIDAL_BLAST",
            "LAVA_BLAST",
            "WIND_BLAST",
        ] {
            assert!(definition(id).arms_auto_attack_on_cast);
        }

        for id in [
            "METEOR",
            "BLIZZARD",
            "FROST_NOVA",
            "NOVA",
            "NEGATE",
            "BLINDING_LIGHT",
            "PROTECTION",
            "FROZEN_GRASP",
            "GUST_OF_WIND",
            "BUFFET",
            "GIGANTISM",
            "FLURRY",
            "VERDANT_SPIRITS",
            "REBUKE",
            "FROST_NEEDLE",
            "MOMENTUM",
            "BATTLE_CRY",
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
    fn cauterize_catalog_removes_all_bleed_debuffs_from_an_assistable_target() {
        let definition = definition("CAUTERIZE");

        assert_eq!(definition.kind.as_str(), "CAUTERIZE");
        assert_eq!(definition.cooldown, Duration::from_millis(1_200));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.behavior.as_str(), "REMOVE_STATUS");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert_eq!(definition.target_audience, TargetAudience::Assistable);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert!((definition.max_distance - 18.0).abs() < 0.0001);

        let remove_status = definition
            .secondary
            .remove_status
            .as_ref()
            .expect("Cauterize should define dispel filters");
        assert!(remove_status.statuses.is_empty());
        assert_eq!(remove_status.max_count, 0);
        assert_eq!(
            remove_status.polarity,
            Some(crate::combat::StatusPolarity::Debuff)
        );
        assert!(!remove_status.dispel_types.is_empty());
    }

    #[test]
    fn absolution_catalog_filters_all_independent_debuffs_at_range() {
        let definition = definition("ABSOLUTION");

        assert_eq!(definition.kind.as_str(), "ABSOLUTION");
        assert_eq!(definition.cooldown, Duration::from_millis(120_000));
        assert_eq!(definition.behavior.as_str(), "REMOVE_STATUS");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert!(!definition.requires_target);
        assert!((definition.max_distance - 30.0).abs() < 0.0001);
        assert_eq!(definition.target_audience, TargetAudience::Assistable);

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
        assert!(
            remove_status.dispel_types.is_empty(),
            "an empty dispel-type filter means every debuff category"
        );
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
        assert_eq!(definition.self_health_cost, 0);
        assert_eq!(definition.self_resource_gain_kind, "STAMINA");
        assert!(definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn blood_offering_catalog_trades_nonlethal_health_for_mana() {
        let definition = definition("BLOOD_OFFERING");

        assert_eq!(definition.cooldown, Duration::from_secs(12));
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.behavior, SpellBehavior::SelfResource);
        assert_eq!(definition.targeting, SpellTargeting::Self_);
        assert_eq!(definition.target_audience, TargetAudience::SelfOnly);
        assert!(!definition.requires_target);
        assert!(!definition.requires_target_los);
        assert_eq!(definition.self_health_cost, 20);
        assert_eq!(definition.self_resource_gain_kind, "MANA");
        assert!((definition.primary_resource_gain_on_cast - 50.0).abs() < 0.0001);
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
    fn mirror_image_catalog_matches_targeted_three_charge_buff() {
        let definition = definition("MIRROR_IMAGE");

        assert_eq!(definition.kind.as_str(), "MIRROR_IMAGE");
        assert_eq!(definition.cooldown, Duration::from_secs(30));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.behavior, SpellBehavior::ApplyStatus);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::PartyOrSelf);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.max_distance, 18.0);
        assert_eq!(definition.duration, 20.0);
        assert_eq!(definition.primary_resource_cost, 0.0);
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("MIRROR_IMAGE")
        );
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Mirror Image should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::MirrorImage);
        assert_eq!(status.max_stacks, 3);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Magic]);
    }

    #[test]
    fn temple_strike_catalog_matches_five_second_mental_confusion_contract() {
        let definition = definition("TEMPLE_STRIKE");

        assert_eq!(definition.cooldown, Duration::from_secs(12));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.cast_mobility, SpellCastMobility::Mobile);
        assert_eq!(definition.behavior, SpellBehavior::ApplyStatus);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::Hostile);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.max_distance, 2.5);
        assert_eq!(definition.duration, 5.0);
        assert_eq!(definition.primary_resource_cost, 0.0);
        assert!(!definition.arms_auto_attack_on_cast);
        assert_eq!(definition.status_stack_group.as_deref(), Some("CONFUSION"));
        assert_eq!(
            definition.apply_status_polarity,
            Some(StatusPolarity::Debuff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Temple Strike should define Confusion");
        assert_eq!(status.payload(), StatusPayload::Confusion);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Mental]);
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
        let status = direct_target.impact_effects[0]
            .as_status()
            .expect("Glacial Spike impact effect must be a status");
        assert_eq!(status.payload().kind(), StatusEffectKind::Freeze);
        assert_eq!(status.duration(), Duration::from_millis(1_200));
        assert!((definition.primary_resource_gain_on_cast - 0.0).abs() < 0.0001);
        assert!(!definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn flash_freeze_catalog_matches_instant_one_second_magic_freeze() {
        let definition = definition("FLASH_FREEZE");

        assert_eq!(definition.kind.as_str(), "FLASH_FREEZE");
        assert_eq!(definition.cooldown, Duration::from_secs(12));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior, SpellBehavior::ApplyStatus);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::Hostile);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.duration, 1.0);
        assert_eq!(definition.max_distance, 30.0);
        assert_eq!(definition.damage_type, DamageType::Cold);
        assert_eq!(definition.status_stack_group.as_deref(), Some("FREEZE"));
        assert_eq!(
            definition.apply_status_polarity,
            Some(StatusPolarity::Debuff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Flash Freeze should apply a status");
        assert_eq!(status.payload(), StatusPayload::Freeze);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Magic]);
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert!(!definition.arms_auto_attack_on_cast);
    }

    #[test]
    fn glacial_advance_catalog_matches_targeted_stun_immunity() {
        let definition = definition("GLACIAL_ADVANCE");

        assert_eq!(definition.kind.as_str(), "GLACIAL_ADVANCE");
        assert_eq!(definition.cooldown, Duration::from_secs(30));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior, SpellBehavior::ApplyStatus);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::PartyOrSelf);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.duration, 10.0);
        assert_eq!(definition.max_distance, 18.0);
        assert_eq!(definition.damage_type, DamageType::Cold);
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("GLACIAL_ADVANCE")
        );
        assert_eq!(definition.apply_status_polarity, Some(StatusPolarity::Buff));
        let status = definition
            .apply_status
            .as_ref()
            .expect("Glacial Advance should apply a status");
        assert_eq!(status.payload(), StatusPayload::StunImmunity);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Magic]);
        assert_eq!(definition.primary_resource_cost, 20.0);
        assert!(!definition.arms_auto_attack_on_cast);
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
    fn blizzard_catalog_matches_point_area_channel_defaults() {
        let definition = definition("BLIZZARD");

        assert_eq!(definition.cooldown, Duration::from_secs(8));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior, SpellBehavior::Channel);
        assert_eq!(definition.targeting, SpellTargeting::Point);
        assert_eq!(definition.target_audience, TargetAudience::Hostile);
        assert!(!definition.requires_target);
        assert!(!definition.requires_target_los);
        assert_eq!(
            definition.cast_mobility,
            SpellCastMobility::GroundedStationary
        );
        assert_eq!(definition.damage, 10);
        assert_eq!(definition.damage_type, DamageType::Cold);
        assert!((definition.max_distance - 15.0).abs() < 0.0001);
        assert!((definition.radius - 4.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert!((definition.update_interval - 0.5).abs() < 0.0001);
        assert!((definition.duration - 4.0).abs() < 0.0001);
        assert!(!definition.arms_auto_attack_on_cast);

        let area = definition
            .secondary
            .channel_area
            .as_ref()
            .expect("Blizzard should define channel-area data");
        assert!((area.radius - 4.0).abs() < 0.0001);
        let slow = area
            .impact_effects
            .iter()
            .find_map(ImpactEffect::as_status)
            .expect("Blizzard should apply a slow");
        assert_eq!(slow.payload(), StatusPayload::Slow { slow_pct: 0.5 });
        assert_eq!(slow.duration(), Duration::from_secs(1));
        assert_eq!(slow.explicit_stack_group(), Some("BLIZZARD_SLOW"));
        assert_eq!(slow.max_stacks(), 1);
        assert_eq!(slow.stack_policy(), StackPolicy::Refresh);
        assert_eq!(slow.dispel_types(), &[StatusDispelType::Magic]);
    }

    #[test]
    fn flamethrower_catalog_matches_forward_column_channel_defaults() {
        let definition = definition("FLAMETHROWER");

        assert_eq!(definition.kind.as_str(), "FLAMETHROWER");
        assert_eq!(definition.cooldown, Duration::from_millis(1_100));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior, SpellBehavior::Channel);
        assert_eq!(definition.targeting, SpellTargeting::Self_);
        assert_eq!(definition.target_audience, TargetAudience::Hostile);
        assert!(!definition.requires_target);
        assert!(!definition.requires_target_los);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(
            definition.cast_mobility,
            SpellCastMobility::GroundedStationary
        );
        assert_eq!(definition.damage, 6);
        assert_eq!(definition.damage_type, DamageType::Fire);
        assert!((definition.max_distance - 10.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 18.0).abs() < 0.0001);
        assert!((definition.update_interval - 0.2).abs() < 0.0001);
        assert!((definition.duration - 4.0).abs() < 0.0001);
        assert!(!definition.arms_auto_attack_on_cast);
        assert!(definition.secondary.channel.is_none());
        let area = definition
            .secondary
            .channel_area
            .as_ref()
            .expect("Flamethrower should define forward-column channel data");
        assert!((area.radius - 2.5).abs() < 0.0001);
        assert!(definition.secondary.channel_projectile.is_none());
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
    fn restoration_catalog_matches_friendly_healing_channel_defaults() {
        let definition = definition("RESTORATION");

        assert_eq!(definition.kind.as_str(), "RESTORATION");
        assert_eq!(definition.cooldown, Duration::from_millis(1_100));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "CHANNEL");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert_eq!(definition.target_audience, TargetAudience::Assistable);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.damage, 0);
        assert_eq!(definition.damage_type.as_str(), "HOLY");
        assert!((definition.max_distance - 18.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 1.0).abs() < 0.0001);
        assert!((definition.update_interval - 1.0).abs() < 0.0001);
        assert!((definition.duration - 5.0).abs() < 0.0001);
        assert!(!definition.arms_auto_attack_on_cast);
        assert_eq!(
            definition
                .secondary
                .channel
                .expect("Restoration should define channel healing data")
                .heal_amount,
            1
        );
        assert!(definition.secondary.channel_projectile.is_none());
    }

    #[test]
    fn protection_catalog_matches_targeted_damage_reduction_buff_defaults() {
        let definition = definition("PROTECTION");

        assert_eq!(definition.kind.as_str(), "PROTECTION");
        assert_eq!(definition.cooldown, Duration::from_millis(30_000));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "APPLY_STATUS");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert_eq!(definition.target_audience, TargetAudience::PartyOrSelf);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.damage_type.as_str(), "HOLY");
        assert!((definition.duration - 5.0).abs() < 0.0001);
        assert!((definition.max_distance - 18.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert_eq!(definition.status_stack_group.as_deref(), Some("PROTECTION"));
        assert_eq!(
            definition.apply_status_polarity,
            Some(crate::combat::StatusPolarity::Buff)
        );
        let status = definition
            .apply_status
            .as_ref()
            .expect("Protection should define an apply-status payload");
        assert_eq!(status.kind, StatusEffectKind::DamageTakenReduction);
        assert!((status.modifier_scalar - 0.5).abs() < 0.0001);
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert!(!definition.arms_auto_attack_on_cast);
    }

    #[test]
    fn holy_shield_catalog_matches_targeted_temporary_hitpoints_defaults() {
        let definition = definition("HOLY_SHIELD");

        assert_eq!(definition.kind.as_str(), "HOLY_SHIELD");
        assert_eq!(definition.cooldown, Duration::from_secs(30));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior, SpellBehavior::ApplyStatus);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::PartyOrSelf);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.duration, 20.0);
        assert_eq!(definition.max_distance, 18.0);
        assert_eq!(definition.damage_type, DamageType::Holy);
        assert_eq!(definition.primary_resource_cost, 0.0);
        assert_eq!(
            definition.status_stack_group.as_deref(),
            Some("HOLY_SHIELD")
        );
        assert_eq!(definition.apply_status_polarity, Some(StatusPolarity::Buff));
        let status = definition
            .apply_status
            .as_ref()
            .expect("Holy Shield should apply a status");
        assert_eq!(
            status.payload(),
            StatusPayload::TemporaryHitpoints {
                absorb_amount: 100,
                absorb_cap: 100,
            }
        );
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Magic]);
        assert!(!definition.arms_auto_attack_on_cast);
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
        let status = area.impact_effects[0]
            .as_status()
            .expect("Frozen Grasp impact effect must be a status");
        assert_eq!(status.payload().kind(), StatusEffectKind::Root);
        assert_eq!(status.duration(), Duration::from_millis(1_200));
        assert!((definition.primary_resource_gain_on_cast - 0.0).abs() < 0.0001);
        assert!(!definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn gust_of_wind_catalog_matches_zero_damage_cone_knockback_defaults() {
        let definition = definition("GUST_OF_WIND");

        assert_eq!(definition.kind.as_str(), "GUST_OF_WIND");
        assert_eq!(definition.cooldown, Duration::from_millis(1_200));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "AREA");
        assert_eq!(definition.targeting.as_str(), "SELF");
        assert_eq!(definition.target_audience.as_str(), "HOSTILE");
        assert!(!definition.requires_target);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.damage, 0);
        assert_eq!(definition.damage_type.as_str(), "AIR");
        assert!((definition.primary_resource_cost - 20.0).abs() < 0.0001);
        assert!(!definition.arms_auto_attack_on_cast);
        let area = definition
            .secondary
            .area
            .as_ref()
            .expect("Gust of Wind should define area secondary data");
        assert_eq!(
            area.shape,
            crate::combat::scene_query::CombatAreaShape::Cone {
                range: 7.5,
                angle_degrees: 65.0,
                vertical_tolerance: Some(2.5),
            }
        );
        assert_eq!(area.impact_effects.len(), 1);
        assert!(matches!(
            area.impact_effects[0],
            ImpactEffect::Knockback {
                distance_meters: 4.0
            }
        ));
    }

    #[test]
    fn buffet_catalog_matches_instant_one_damage_interrupt_defaults() {
        let definition = definition("BUFFET");

        assert_eq!(definition.kind.as_str(), "BUFFET");
        assert_eq!(definition.cooldown, Duration::from_millis(12_000));
        assert!(!definition.uses_global_cooldown);
        assert_eq!(definition.behavior.as_str(), "DIRECT_TARGET");
        assert_eq!(definition.targeting.as_str(), "TARGET");
        assert_eq!(definition.target_audience.as_str(), "HOSTILE");
        assert!(definition.requires_target);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.damage, 1);
        assert_eq!(definition.damage_type.as_str(), "AIR");
        assert!((definition.max_distance - 18.0).abs() < 0.0001);
        assert!((definition.primary_resource_cost - 0.0).abs() < 0.0001);
        assert!(!definition.arms_auto_attack_on_cast);
        let direct_target = definition
            .secondary
            .direct_target
            .as_ref()
            .expect("Buffet should define direct-target secondary data");
        assert_eq!(direct_target.parry_behavior.as_str(), "UNPARRYABLE");
        assert_eq!(
            direct_target.impact_effects,
            vec![ImpactEffect::InterruptCast]
        );
        assert!(!definition.generates_primary_resource_on_cast);
    }

    #[test]
    fn primal_blast_catalog_matches_short_range_instant_contracts() {
        for (spell_id, damage_type) in [
            ("EARTH_BLAST", DamageType::Physical),
            ("TIDAL_BLAST", DamageType::Physical),
            ("LAVA_BLAST", DamageType::Fire),
            ("WIND_BLAST", DamageType::Physical),
        ] {
            let definition = definition(spell_id);
            assert_eq!(definition.cooldown, Duration::from_millis(1_200));
            assert!(definition.uses_global_cooldown);
            assert_eq!(definition.cast_time, Duration::ZERO);
            assert_eq!(definition.cast_mobility, SpellCastMobility::Mobile);
            assert_eq!(definition.behavior, SpellBehavior::DirectTarget);
            assert_eq!(definition.targeting, SpellTargeting::Target);
            assert_eq!(definition.target_audience, TargetAudience::Hostile);
            assert!(definition.requires_target);
            assert!(definition.requires_target_los);
            assert_eq!(definition.damage, 30);
            assert_eq!(definition.damage_type, damage_type);
            assert_eq!(definition.max_distance, 8.0);
            assert_eq!(definition.primary_resource_cost, 0.0);
            assert!(definition.arms_auto_attack_on_cast);
            let direct_target = definition
                .secondary
                .direct_target
                .as_ref()
                .expect("Primal blasts should define direct-target secondary data");
            assert_eq!(direct_target.parry_behavior, SpellParryBehavior::Parryable);
            assert_eq!(direct_target.impact_effects.len(), 1);
        }

        let tidal = definition("TIDAL_BLAST");
        assert_eq!(
            tidal
                .secondary
                .direct_target
                .as_ref()
                .unwrap()
                .impact_effects,
            vec![ImpactEffect::RemoveStatus {
                polarity: Some(StatusPolarity::Buff),
                dispel_types: Vec::new(),
                max_count: 1,
            }]
        );
        let tidal_effect = &tidal
            .secondary
            .direct_target
            .as_ref()
            .unwrap()
            .impact_effects[0];
        match tidal_effect.to_effect_packet(
            Identity::ZERO,
            Identity::ZERO,
            "tidal-instance",
            StatusPolarity::Debuff,
            "tidal-action",
            0.0,
            1.0,
        ) {
            EffectPacket::RemoveStatusByFilter {
                target,
                polarity,
                dispel_types,
                max_count,
            } => {
                assert_eq!(target, Identity::ZERO);
                assert_eq!(polarity, Some(StatusPolarity::Buff));
                assert!(dispel_types.is_empty());
                assert_eq!(max_count, 1);
            }
            _ => panic!("Tidal Blast should queue one filtered buff removal"),
        }
    }

    #[test]
    fn gigantism_catalog_matches_targeted_primal_buff_contract() {
        let definition = definition("GIGANTISM");

        assert_eq!(definition.kind.as_str(), "GIGANTISM");
        assert_eq!(definition.cooldown, Duration::from_secs(30));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.cast_mobility, SpellCastMobility::Mobile);
        assert_eq!(definition.behavior, SpellBehavior::ApplyStatus);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::PartyOrSelf);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.max_distance, 18.0);
        assert_eq!(definition.duration, 20.0);
        assert_eq!(definition.primary_resource_cost, 0.0);
        assert!(!definition.arms_auto_attack_on_cast);
        assert_eq!(definition.status_stack_group.as_deref(), Some("GIGANTISM"));
        assert_eq!(definition.apply_status_polarity, Some(StatusPolarity::Buff));
        let status = definition
            .apply_status
            .as_ref()
            .expect("Gigantism should define an apply-status payload");
        assert_eq!(
            status.payload(),
            StatusPayload::Gigantism {
                modifier_scalar: 0.20,
            }
        );
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Magic]);
    }

    #[test]
    fn flurry_catalog_matches_targeted_primal_buff_contract() {
        let definition = definition("FLURRY");

        assert_eq!(definition.kind.as_str(), "FLURRY");
        assert_eq!(definition.cooldown, Duration::from_secs(30));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.cast_mobility, SpellCastMobility::Mobile);
        assert_eq!(definition.behavior, SpellBehavior::ApplyStatus);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::PartyOrSelf);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.max_distance, 18.0);
        assert_eq!(definition.duration, 20.0);
        assert_eq!(definition.primary_resource_cost, 0.0);
        assert!(!definition.arms_auto_attack_on_cast);
        assert_eq!(definition.status_stack_group.as_deref(), Some("FLURRY"));
        assert_eq!(definition.apply_status_polarity, Some(StatusPolarity::Buff));
        let status = definition
            .apply_status
            .as_ref()
            .expect("Flurry should define an apply-status payload");
        assert_eq!(
            status.payload(),
            StatusPayload::Flurry {
                modifier_scalar: 0.15,
            }
        );
        assert_eq!(status.max_stacks, 1);
        assert_eq!(status.stack_policy, StackPolicy::Refresh);
        assert_eq!(status.dispel_types, vec![StatusDispelType::Magic]);
    }

    #[test]
    fn verdant_spirits_catalog_matches_passive_bestow_contract() {
        let definition = definition("VERDANT_SPIRITS");

        assert_eq!(definition.cooldown, Duration::from_millis(100));
        assert!(!definition.uses_global_cooldown);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.cast_mobility, SpellCastMobility::Mobile);
        assert_eq!(definition.behavior, SpellBehavior::DirectTarget);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::Assistable);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.max_distance, 18.0);
        assert_eq!(definition.damage, 0);
        assert_eq!(definition.damage_type, DamageType::Air);
        assert!(!definition.arms_auto_attack_on_cast);
    }

    #[test]
    fn rebuke_catalog_matches_conditional_holy_interrupt_defaults() {
        let definition = definition("REBUKE");

        assert_eq!(definition.kind.as_str(), "REBUKE");
        assert_eq!(definition.cooldown, Duration::from_secs(12));
        assert!(!definition.uses_global_cooldown);
        assert_eq!(definition.behavior, SpellBehavior::DirectTarget);
        assert_eq!(definition.targeting, SpellTargeting::Target);
        assert_eq!(definition.target_audience, TargetAudience::Hostile);
        assert!(definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(definition.damage, 0);
        assert_eq!(definition.damage_type, DamageType::Holy);
        assert!((definition.max_distance - 18.0).abs() < 0.0001);
        assert_eq!(definition.primary_resource_cost, 0.0);
        assert_eq!(definition.block_behavior, BlockBehavior::Unblockable);
        let direct_target = definition
            .secondary
            .direct_target
            .as_ref()
            .expect("Rebuke should define direct-target secondary data");
        assert_eq!(
            direct_target.parry_behavior,
            SpellParryBehavior::Unparryable
        );
        assert_eq!(
            direct_target.impact_effects,
            vec![ImpactEffect::InterruptCastWithDamage {
                damage: 30,
                damage_type: "HOLY".to_string(),
            }]
        );
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
        assert!(matches!(
            area.impact_effects[0],
            ImpactEffect::Knockback {
                distance_meters: 4.0
            }
        ));
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
        let status = area.impact_effects[0]
            .as_status()
            .expect("Intimidate impact effect must be a status");
        assert_eq!(status.payload().kind(), StatusEffectKind::Intimidated);
        assert_eq!(status.explicit_stack_group(), Some("INTIMIDATED"));
        assert_eq!(status.duration(), Duration::from_millis(4_000));
        assert_eq!(status.max_stacks(), 1);
        assert_eq!(status.stack_policy(), StackPolicy::Refresh);
    }

    #[test]
    fn rain_of_arrows_catalog_uses_the_standard_point_area_contract() {
        let definition = definition("RAIN_OF_ARROWS");

        assert_eq!(definition.cooldown, Duration::from_millis(6_000));
        assert!(definition.uses_global_cooldown);
        assert_eq!(definition.cast_time, Duration::ZERO);
        assert_eq!(
            definition.cast_mobility,
            SpellCastMobility::GroundedStationary
        );
        assert_eq!(definition.behavior, SpellBehavior::Area);
        assert_eq!(definition.targeting, SpellTargeting::Point);
        assert!(!definition.requires_target);
        assert!(definition.requires_target_los);
        assert_eq!(definition.damage, 38);
        assert_eq!(definition.damage_type, DamageType::Physical);
        assert!((definition.max_distance - 18.0).abs() < 0.0001);
        assert!((definition.radius - 4.0).abs() < 0.0001);
        assert_eq!(definition.aim_radius, Some(4.0));
        assert!((definition.primary_resource_cost - 30.0).abs() < 0.0001);
        assert_eq!(definition.block_behavior, BlockBehavior::Blockable);
        assert_eq!(
            definition
                .secondary
                .area
                .as_ref()
                .expect("Rain of Arrows should define area secondary data")
                .impact_delay_ms,
            850
        );
    }
}

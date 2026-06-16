//! Melee Attack System
//!
//! Weapon strikes are authored via a shared manifest exported from Unity editor tooling.
//! The server remains authoritative for timing, defense interaction, damage, and cooldowns.

use std::collections::HashSet;
use std::sync::OnceLock;
use std::time::Duration;

use serde::Deserialize;
use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::action_ids::{AuthoredActionId, RuntimeActionId};
use crate::action_prediction::{
    has_predicted_action_result, record_predicted_action_result, ActionPredictionToken,
    ActionResultKind, PredictedActionFamily,
};
use crate::arena::player_world as _;
use crate::arena::{
    arena_seed_for_identity, open_world_scene_name_for_identity, players_share_world_context,
};
use crate::auto_attack::arm_auto_attack_if_unarmed_with_cadence;
use crate::combat::player_snapshot::{player_snapshot_for, PlayerSnapshotSet};
use crate::combat::scene_query::{
    has_line_of_sight, is_direction_within_facing_arc, target_within_area_range_xz,
    terrain_surface_y_for_caster, CombatAreaShape,
};
use crate::combat::{
    combat_projectile_definition_for_id, has_active_disabling_status, has_active_status_group,
    has_due_pending_effects, mark_harmful_combat_action, queue_effects, remove_active_status_group,
    resolve_pending_effects, ActiveCombatProjectile, CombatEvent, CombatProjectileDefinition,
    DamageDelivery, EffectPacket, ProjectilePresentationEvent, StackPolicy, StatusEffectKind,
    StatusPayload, StatusPolarity, COMBAT_EVENT_AREA_IMPACT, COMBAT_EVENT_BLOCK, COMBAT_EVENT_CAST,
    COMBAT_EVENT_FIZZLE, COMBAT_EVENT_IMPACT, COMBAT_EVENT_PARRY, COMBAT_EVENT_RELEASE,
    COMBAT_METADATA_CONSUMED_MELEE_MODIFIER, COMBAT_METADATA_NONE,
    COMBAT_SCALAR_MELEE_RELEASE_DELAY_SECONDS, COMBAT_SCALAR_NONE, COMBAT_SEQUENCE_NONE,
};
use crate::defense::{
    clear_interruptible_defense_for_owner, resolve_defensible_combat_hit, CombatHitDeliveryKind,
    DefenseResolution, DefensibleCombatHit,
};
use crate::player::DEFAULT_COMBAT_PROFILE;
use crate::practice::is_training_instance;
use crate::progression::{
    active_loadout_assignment_debug_summary, active_selectable_ability_for_authored_action,
    combat_profile_has_modes, derived_combat_profile_id_for_owner,
    melee_impact_effects_for_ability_id, melee_timed_movement_for_ability_id,
    primary_resource_gain_on_action_accept, resolved_auto_attack_mode_for_owner, AbilityCatalog,
    AutoAttackCatalog, AutoAttackReplacementCatalog, MeleeAbilityCatalog, MeleeGapCloseCatalog,
    MeleeTimedMovementRuntime,
};
use crate::relations::{can_harm, combat_relation};
use crate::resources::{
    can_pay_action_resource_cost, grant_primary_resource_amount,
    grant_primary_resource_for_melee_hit, pay_action_resource_cost,
    resolve_ability_action_resource_cost, ResolvedActionResourceCost,
};
use crate::spells::{
    aoe_hits_player, approach_line_contact_point_xz, bake_linear_special_movement,
    begin_special_movement, begin_special_movement_with_facing_policy, contact_distance_from_radii,
    horizontal_movement_duration_ms, is_on_global_cooldown, is_on_named_cooldown,
    stamp_global_cooldown_for_duration, stamp_named_cooldown_for_duration, SpellVec3,
    SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK, SPECIAL_MOVEMENT_FACING_FACE_START,
};
use crate::world_collision::{
    resolve_world_horizontal_collision_y_with_layout_for_scene,
    surface_height_for_world_at_y_with_layout_for_scene,
};

#[allow(unused_imports)]
use crate::combat::active_combat_projectile as _;
#[allow(unused_imports)]
use crate::combat::combat_event as _;
#[allow(unused_imports)]
use crate::combat::projectile_presentation_event as _;
#[allow(unused_imports)]
use crate::melee::melee_attack_modifier_catalog as _;
#[allow(unused_imports)]
use crate::melee::melee_definition as _;
#[allow(unused_imports)]
use crate::melee::pending_melee_impact as _;
#[allow(unused_imports)]
use crate::melee::pending_melee_timed_movement as _;
#[allow(unused_imports)]
use crate::melee::pending_projectile_release as _;
#[allow(unused_imports)]
use crate::melee::queued_melee_followup as _;
#[allow(unused_imports)]
use crate::player::player as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::progression::ability_catalog as _;
#[allow(unused_imports)]
use crate::progression::auto_attack_catalog as _;
#[allow(unused_imports)]
use crate::progression::class_catalog as _;
#[allow(unused_imports)]
use crate::progression::melee_ability_catalog as _;
#[allow(unused_imports)]
use crate::progression::melee_gap_close_catalog as _;
#[allow(unused_imports)]
use crate::spells::global_cooldown as _;

const EVENT_CAST: &str = COMBAT_EVENT_CAST;
const EVENT_IMPACT: &str = COMBAT_EVENT_IMPACT;
const EVENT_AREA_IMPACT: &str = COMBAT_EVENT_AREA_IMPACT;
const EVENT_FIZZLE: &str = COMBAT_EVENT_FIZZLE;
const EVENT_BLOCK: &str = COMBAT_EVENT_BLOCK;
const EVENT_PARRY: &str = COMBAT_EVENT_PARRY;
const MELEE_MANIFEST_JSON: &str = include_str!("melee_manifest.shared.json");
const GIANT_SWING_STATUS_GROUP: &str = "GIANT_SWING";
const GIANT_SWING_MIN_RANGE_METERS: f32 = 5.0;
const GAP_CLOSE_KIND_LINEAR: &str = "LINEAR";
const GAP_CLOSE_KIND_LEAP: &str = "LEAP";
const GAP_CLOSE_KIND_TELEPORT: &str = "TELEPORT";
const GAP_CLOSE_KIND_TELEPORT_BEHIND: &str = "TELEPORT_BEHIND";
const GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT: &str = "NEAREST_CONTACT_POINT";
const GAP_CLOSE_DESTINATION_BEHIND_TARGET: &str = "BEHIND_TARGET";
const GAP_CLOSE_DESTINATION_TARGET_SIDE_LEFT: &str = "TARGET_SIDE_LEFT";
const GAP_CLOSE_DESTINATION_TARGET_SIDE_RIGHT: &str = "TARGET_SIDE_RIGHT";
const GAP_CLOSE_DESTINATION_CURRENT_LINE: &str = "CURRENT_LINE";
const GAP_CLOSE_COLLISION_REQUIRE_CLEAR_PATH: &str = "REQUIRE_CLEAR_PATH";
const MELEE_GAP_CLOSE_KIND_PREFIX: &str = "MELEE_GAP_CLOSE";
const MELEE_TARGET_FACING_ARC_RADIANS: f32 = std::f32::consts::PI;

#[derive(Clone, Copy, Debug, PartialEq)]
struct MeleeAttackModifierSpec {
    status_kind: StatusEffectKind,
    status_group: &'static str,
    min_range: Option<f32>,
    force_stagger: bool,
}

const MELEE_ATTACK_MODIFIER_SPECS: [MeleeAttackModifierSpec; 1] = [MeleeAttackModifierSpec {
    status_kind: StatusEffectKind::MeleeAttackModifier,
    status_group: GIANT_SWING_STATUS_GROUP,
    min_range: Some(GIANT_SWING_MIN_RANGE_METERS),
    force_stagger: true,
}];

#[table(accessor = melee_attack_modifier_catalog, public)]
pub struct MeleeAttackModifierCatalog {
    #[primary_key]
    pub key: String,
    pub status_kind: String,
    pub stack_group: String,
    pub min_range: f32,
    pub force_stagger: bool,
    pub sort_order: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct ConsumedMeleeAttackModifier {
    status_kind: StatusEffectKind,
    status_group: &'static str,
}

pub(crate) fn sync_melee_attack_modifier_catalog(ctx: &ReducerContext) {
    let expected: HashSet<_> = MELEE_ATTACK_MODIFIER_SPECS
        .iter()
        .map(melee_attack_modifier_catalog_key)
        .collect();

    for (index, spec) in MELEE_ATTACK_MODIFIER_SPECS.iter().enumerate() {
        let key = melee_attack_modifier_catalog_key(spec);
        let row = MeleeAttackModifierCatalog {
            key: key.clone(),
            status_kind: spec.status_kind.as_str().to_string(),
            stack_group: spec.status_group.to_string(),
            min_range: spec.min_range.unwrap_or(0.0).max(0.0),
            force_stagger: spec.force_stagger,
            sort_order: index as u32,
        };
        if ctx
            .db
            .melee_attack_modifier_catalog()
            .key()
            .find(key.clone())
            .is_some()
        {
            ctx.db.melee_attack_modifier_catalog().key().update(row);
        } else {
            ctx.db.melee_attack_modifier_catalog().insert(row);
        }
    }

    let stale: Vec<_> = ctx
        .db
        .melee_attack_modifier_catalog()
        .iter()
        .map(|row| row.key)
        .filter(|key| !expected.contains(key))
        .collect();
    for key in stale {
        ctx.db.melee_attack_modifier_catalog().key().delete(key);
    }
}

fn melee_attack_modifier_catalog_key(spec: &MeleeAttackModifierSpec) -> String {
    format!("{}:{}", spec.status_kind.as_str(), spec.status_group)
}

#[derive(Clone, Debug, Default, PartialEq)]
struct ResolvedMeleeAttackModifiers {
    min_range: Option<f32>,
    force_stagger: bool,
    consumed: Vec<ConsumedMeleeAttackModifier>,
}

impl ResolvedMeleeAttackModifiers {
    fn effective_range(&self, base_range: f32) -> f32 {
        self.min_range
            .map(|min_range| base_range.max(min_range))
            .unwrap_or(base_range)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum MeleeAttackDispatch {
    Rejected,
    Queued,
    Started,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum MeleeAuthorization {
    SelectableLoadout,
    IntrinsicAutoAttack,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum MeleeEventSource {
    PlayerInput,
    QueuedFollowup,
    AutoAttack,
    Practice,
}

impl MeleeEventSource {
    fn as_str(self) -> &'static str {
        match self {
            Self::PlayerInput => "player_input",
            Self::QueuedFollowup => "queued_followup",
            Self::AutoAttack => "auto_attack",
            Self::Practice => "practice",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct MeleeExecutionPolicy {
    authorization: MeleeAuthorization,
    event_source: MeleeEventSource,
    schedules_auto_attack_on_started_swing: bool,
    uses_shared_cooldowns: bool,
    grants_primary_resource_on_hit: bool,
}

impl MeleeExecutionPolicy {
    const PLAYER_INPUT: Self = Self {
        authorization: MeleeAuthorization::SelectableLoadout,
        event_source: MeleeEventSource::PlayerInput,
        schedules_auto_attack_on_started_swing: true,
        uses_shared_cooldowns: true,
        grants_primary_resource_on_hit: false,
    };

    const QUEUED_FOLLOWUP: Self = Self {
        authorization: MeleeAuthorization::SelectableLoadout,
        event_source: MeleeEventSource::QueuedFollowup,
        schedules_auto_attack_on_started_swing: true,
        uses_shared_cooldowns: true,
        grants_primary_resource_on_hit: false,
    };

    const PRACTICE: Self = Self {
        authorization: MeleeAuthorization::SelectableLoadout,
        event_source: MeleeEventSource::Practice,
        schedules_auto_attack_on_started_swing: false,
        uses_shared_cooldowns: true,
        grants_primary_resource_on_hit: false,
    };

    const INTRINSIC_AUTO_ATTACK: Self = Self {
        authorization: MeleeAuthorization::IntrinsicAutoAttack,
        event_source: MeleeEventSource::AutoAttack,
        schedules_auto_attack_on_started_swing: false,
        uses_shared_cooldowns: false,
        grants_primary_resource_on_hit: true,
    };

    const INTRINSIC_AUTO_ATTACK_REPLACEMENT: Self = Self {
        authorization: MeleeAuthorization::IntrinsicAutoAttack,
        event_source: MeleeEventSource::AutoAttack,
        schedules_auto_attack_on_started_swing: false,
        uses_shared_cooldowns: true,
        grants_primary_resource_on_hit: false,
    };

    fn source_label(self) -> &'static str {
        self.event_source.as_str()
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
enum ParryBehavior {
    Parryable,
    Unparryable,
}

impl ParryBehavior {
    fn from_wire(value: &str) -> Option<Self> {
        match value.trim().to_ascii_uppercase().as_str() {
            "PARRYABLE" => Some(Self::Parryable),
            "UNPARRYABLE" => Some(Self::Unparryable),
            _ => None,
        }
    }

    fn as_str(self) -> &'static str {
        match self {
            Self::Parryable => "PARRYABLE",
            Self::Unparryable => "UNPARRYABLE",
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
enum BlockBehavior {
    Blockable,
    Unblockable,
}

impl BlockBehavior {
    fn from_wire(value: &str) -> Option<Self> {
        match value.trim().to_ascii_uppercase().as_str() {
            "BLOCKABLE" => Some(Self::Blockable),
            "UNBLOCKABLE" => Some(Self::Unblockable),
            _ => None,
        }
    }

    fn as_str(self) -> &'static str {
        match self {
            Self::Blockable => "BLOCKABLE",
            Self::Unblockable => "UNBLOCKABLE",
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
enum AerialExecutionMode {
    GroundedOnly,
    GroundedOrAirborne,
    AirborneOnly,
}

fn default_aerial_execution_mode() -> AerialExecutionMode {
    AerialExecutionMode::GroundedOnly
}

fn default_projectile_id() -> String {
    "ARROW_STANDARD".to_string()
}

impl AerialExecutionMode {
    fn as_str(self) -> &'static str {
        match self {
            Self::GroundedOnly => "GROUNDED_ONLY",
            Self::GroundedOrAirborne => "GROUNDED_OR_AIRBORNE",
            Self::AirborneOnly => "AIRBORNE_ONLY",
        }
    }

    fn allows_caster(self, grounded: bool) -> bool {
        match self {
            Self::GroundedOnly => grounded,
            Self::GroundedOrAirborne => true,
            Self::AirborneOnly => !grounded,
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
enum AirborneTargetingMode {
    AnyTarget,
    GroundedTargetOnly,
}

impl AirborneTargetingMode {
    fn from_wire(value: &str) -> Option<Self> {
        match value.trim().to_ascii_uppercase().as_str() {
            "ANY_TARGET" => Some(Self::AnyTarget),
            "GROUNDED_TARGET_ONLY" => Some(Self::GroundedTargetOnly),
            _ => None,
        }
    }

    fn as_str(self) -> &'static str {
        match self {
            Self::AnyTarget => "ANY_TARGET",
            Self::GroundedTargetOnly => "GROUNDED_TARGET_ONLY",
        }
    }

    fn allows_target(self, caster_grounded: bool, target_grounded: bool) -> bool {
        if caster_grounded {
            return true;
        }

        match self {
            Self::AnyTarget => true,
            Self::GroundedTargetOnly => target_grounded,
        }
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct StrikeHitWindowData {
    impact_delay_ms: u64,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct StrikeProjectileData {
    #[serde(default = "default_projectile_id")]
    projectile_id: String,
    #[serde(default)]
    speed: Option<f32>,
    #[serde(default)]
    max_distance: Option<f32>,
    #[serde(default)]
    radius: Option<f32>,
    #[serde(default)]
    spawn_forward: Option<f32>,
    #[serde(default)]
    spawn_height: Option<f32>,
    #[serde(default)]
    aim_height_scale: Option<f32>,
    #[serde(default)]
    requires_initial_line_of_sight: Option<bool>,
    #[serde(default)]
    update_interval_seconds: Option<f32>,
}

#[derive(Clone)]
struct ResolvedStrikeProjectileData {
    projectile_id: String,
    speed: f32,
    max_distance: f32,
    radius: f32,
    spawn_forward: f32,
    spawn_height: f32,
    aim_height_scale: f32,
    requires_initial_line_of_sight: bool,
    update_interval_seconds: f32,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct StrikeData {
    id: String,
    #[serde(default)]
    slot_id: String,
    #[serde(default)]
    hit_windows: Vec<StrikeHitWindowData>,
    recovery_ms: u64,
    is_gap_closer: bool,
    #[serde(default)]
    combo_from: Option<String>,
    #[serde(default, alias = "combo_window_ms")]
    combo_open_ms: u64,
    #[serde(default)]
    combo_grace_ms: u64,
    #[serde(default = "default_aerial_execution_mode")]
    aerial_execution_mode: AerialExecutionMode,
    #[serde(default)]
    projectile: Option<StrikeProjectileData>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeManifest {
    #[serde(default)]
    profiles: Vec<MeleeProfileData>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct MeleeProfileData {
    combat_profile: String,
    stagger_duration_f_ms: u64,
    stagger_duration_b_ms: u64,
    stagger_duration_l_ms: u64,
    stagger_duration_r_ms: u64,
    #[serde(default)]
    auto_attack_strike_id: String,
    strikes: Vec<StrikeData>,
}

fn melee_manifest() -> &'static MeleeManifest {
    static MANIFEST: OnceLock<MeleeManifest> = OnceLock::new();
    MANIFEST.get_or_init(|| {
        serde_json::from_str(MELEE_MANIFEST_JSON)
            .expect("melee_manifest.shared.json must remain valid and schema-compatible")
    })
}

fn canonical_slot_id(strike: &StrikeData) -> String {
    let authored = strike.slot_id.trim();
    if !authored.is_empty() {
        return authored.to_ascii_lowercase();
    }

    strike.id.trim().to_ascii_lowercase()
}

fn runtime_action_id_for_strike(strike: &StrikeData) -> RuntimeActionId {
    RuntimeActionId::new(canonical_slot_id(strike).as_str())
}

#[derive(Clone)]
struct ResolvedMeleeStrike {
    authored_id: AuthoredActionId,
    runtime_id: RuntimeActionId,
    strike: &'static StrikeData,
}

fn strike_by_authored_id_in_strikes(
    strikes: &'static [StrikeData],
    authored_id: &AuthoredActionId,
) -> Option<&'static StrikeData> {
    if authored_id.is_empty() {
        return None;
    }

    strikes
        .iter()
        .find(|strike| AuthoredActionId::new(strike.id.as_str()).as_str() == authored_id.as_str())
}

fn strike_by_runtime_id_in_strikes(
    strikes: &'static [StrikeData],
    runtime_id: &RuntimeActionId,
) -> Option<&'static StrikeData> {
    if runtime_id.is_empty() {
        return None;
    }

    strikes
        .iter()
        .find(|strike| runtime_action_id_for_strike(strike).as_str() == runtime_id.as_str())
}

fn resolve_melee_authored_action_in_strikes(
    strikes: &'static [StrikeData],
    authored_id: &AuthoredActionId,
) -> Option<ResolvedMeleeStrike> {
    let strike = strike_by_authored_id_in_strikes(strikes, authored_id)?;
    Some(ResolvedMeleeStrike {
        authored_id: authored_id.clone(),
        runtime_id: runtime_action_id_for_strike(strike),
        strike,
    })
}

fn resolve_melee_action_reference_in_strikes(
    strikes: &'static [StrikeData],
    raw_action_id: &str,
) -> Option<ResolvedMeleeStrike> {
    let authored_id = AuthoredActionId::new(raw_action_id);
    if let Some(resolved) = resolve_melee_authored_action_in_strikes(strikes, &authored_id) {
        return Some(resolved);
    }

    let runtime_id = RuntimeActionId::new(raw_action_id);
    let strike = strike_by_runtime_id_in_strikes(strikes, &runtime_id)?;
    Some(ResolvedMeleeStrike {
        authored_id: AuthoredActionId::new(strike.id.as_str()),
        runtime_id,
        strike,
    })
}

fn resolve_melee_authored_action(
    combat_profile: &str,
    authored_id: &AuthoredActionId,
) -> Option<ResolvedMeleeStrike> {
    let manifest = melee_manifest();
    let profile = manifest
        .profiles
        .iter()
        .find(|profile| profile.combat_profile.eq_ignore_ascii_case(combat_profile))?;
    resolve_melee_authored_action_in_strikes(profile.strikes.as_slice(), authored_id)
}

/// Compatibility boundary for raw action references.
///
/// Reducers and this resolver are the only action-reference entry points that
/// should accept raw `&str` values. Internal melee interfaces should carry
/// `AuthoredActionId`, `RuntimeActionId`, or `ResolvedMeleeStrike` so authored
/// content identity cannot be confused with runtime slot plumbing.
fn resolve_melee_action_reference(
    combat_profile: &str,
    raw_action_id: &str,
) -> Option<ResolvedMeleeStrike> {
    let manifest = melee_manifest();
    let profile = manifest
        .profiles
        .iter()
        .find(|profile| profile.combat_profile.eq_ignore_ascii_case(combat_profile))?;
    resolve_melee_action_reference_in_strikes(profile.strikes.as_slice(), raw_action_id)
}

fn strike_by_runtime_action_id(
    combat_profile: &str,
    runtime_id: &RuntimeActionId,
) -> Option<&'static StrikeData> {
    let manifest = melee_manifest();
    let profile = manifest
        .profiles
        .iter()
        .find(|profile| profile.combat_profile.eq_ignore_ascii_case(combat_profile))?;
    strike_by_runtime_id_in_strikes(profile.strikes.as_slice(), runtime_id)
}

pub(crate) fn auto_attack_reference_for_profile(combat_profile: &str) -> Option<String> {
    let profile = melee_manifest()
        .profiles
        .iter()
        .find(|profile| profile.combat_profile.eq_ignore_ascii_case(combat_profile))?;
    let authored = profile.auto_attack_strike_id.trim();
    if !authored.is_empty() {
        return Some(authored.to_string());
    }

    profile.strikes.first().map(|strike| strike.id.clone())
}

pub(crate) fn authored_strike_id_for_profile_position(
    combat_profile: &str,
    zero_based_index: usize,
) -> Option<String> {
    melee_manifest()
        .profiles
        .iter()
        .find(|profile| profile.combat_profile.eq_ignore_ascii_case(combat_profile))
        .and_then(|profile| profile.strikes.get(zero_based_index))
        .map(|strike| strike.id.clone())
}

#[cfg(test)]
pub(crate) fn profile_supports_action_reference(
    combat_profile: &str,
    action_id: &AuthoredActionId,
) -> bool {
    resolve_melee_authored_action(combat_profile, action_id).is_some()
}

fn melee_definition_key_for_runtime(combat_profile: &str, runtime_id: &RuntimeActionId) -> String {
    format!(
        "{}:{}",
        combat_profile.to_ascii_uppercase(),
        runtime_id.as_str()
    )
}

#[derive(Clone)]
#[table(accessor = melee_definition, public)]
pub struct MeleeDefinition {
    #[primary_key]
    pub key: String,
    pub combat_profile: String,
    pub combat_style_id: String,
    pub slot_id: String,
    pub kind: String,
    pub impact_delay_ms: u64,
    pub recovery_ms: u64,
    pub is_gap_closer: bool,
    pub combo_from: String,
    pub combo_open_ms: u64,
    pub combo_grace_ms: u64,
    pub aerial_execution_mode: String,
}

#[derive(Clone)]
#[table(accessor = pending_melee_impact)]
pub struct PendingMeleeImpact {
    #[primary_key]
    #[auto_inc]
    pub impact_id: u64,
    pub source: Identity,
    pub event_source: String,
    pub target: Identity,
    pub spell_id: String,
    pub kind: String,
    pub ability_id: String,
    pub hit_index: u32,
    pub damage: i32,
    pub range: f32,
    pub impact_at: Timestamp,
    pub active_until: Timestamp,
    pub recovery_until: Timestamp,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub airborne_targeting_mode: String,
    pub targeting_kind: String,
    pub targeting_radius: f32,
    pub targeting_angle_degrees: f32,
    pub applies_stagger: bool,
    pub grants_primary_resource_on_hit: bool,
    pub impact_area_radius: f32,
    pub impact_area_damage: i32,
    pub impact_area_include_primary_target: bool,
    #[index(btree)]
    pub resolve_at_micros: i64,
}

#[derive(Clone)]
#[table(accessor = pending_melee_timed_movement)]
pub struct PendingMeleeTimedMovement {
    #[primary_key]
    #[auto_inc]
    pub movement_id: u64,
    #[index(btree)]
    pub owner: Identity,
    pub action_instance_id: String,
    pub action_kind: String,
    pub ability_id: String,
    pub kind: String,
    pub direction: String,
    pub distance: f32,
    pub speed: f32,
    pub collision_policy: String,
    pub facing_policy: String,
    pub yaw_start: f32,
    pub start_at: Timestamp,
    #[index(btree)]
    pub start_at_micros: i64,
}

#[table(accessor = pending_projectile_release)]
pub struct PendingProjectileRelease {
    #[primary_key]
    #[auto_inc]
    pub release_id: u64,
    pub source: Identity,
    pub event_source: String,
    pub target: Identity,
    pub action_instance_id: String,
    pub action_kind: String,
    pub ability_id: String,
    pub projectile_id: String,
    pub hit_index: u32,
    pub damage: i32,
    pub speed: f32,
    pub max_distance: f32,
    pub radius: f32,
    pub spawn_forward: f32,
    pub spawn_height: f32,
    pub aim_height_scale: f32,
    pub update_interval_seconds: f32,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub grants_primary_resource_on_hit: bool,
    pub release_at: Timestamp,
    pub recovery_until: Timestamp,
    #[index(btree)]
    pub release_at_micros: i64,
}

#[derive(Clone)]
struct ResolvedMeleeGameplay {
    ability_id: Option<String>,
    base_damage: i32,
    range: f32,
    minimum_range: f32,
    cooldown_ms: u64,
    uses_global_cooldown: bool,
    global_cooldown_ms: u64,
    parry_behavior: ParryBehavior,
    block_behavior: BlockBehavior,
    airborne_targeting_mode: AirborneTargetingMode,
    targeting: ResolvedMeleeTargeting,
    applies_stagger: bool,
    impact_area: Option<ResolvedMeleeImpactArea>,
    timed_movement: Option<MeleeTimedMovementRuntime>,
}

#[derive(Clone, Copy)]
enum ResolvedMeleeTargeting {
    Target,
    CasterRadius { radius: f32 },
    CasterCone { range: f32, angle_degrees: f32 },
}

impl ResolvedMeleeTargeting {
    fn from_catalog(melee: &MeleeAbilityCatalog) -> Option<Self> {
        match melee.targeting_kind.trim().to_ascii_uppercase().as_str() {
            "" | "TARGET" => Some(Self::Target),
            "CASTER_RADIUS" => {
                if melee.targeting_radius.is_finite() && melee.targeting_radius > 0.0 {
                    Some(Self::CasterRadius {
                        radius: melee.targeting_radius,
                    })
                } else {
                    None
                }
            }
            "CASTER_CONE" => {
                if melee.targeting_range.is_finite()
                    && melee.targeting_range > 0.0
                    && melee.targeting_angle_degrees.is_finite()
                    && melee.targeting_angle_degrees > 0.0
                    && melee.targeting_angle_degrees <= 360.0
                {
                    Some(Self::CasterCone {
                        range: melee.targeting_range,
                        angle_degrees: melee.targeting_angle_degrees,
                    })
                } else {
                    None
                }
            }
            _ => None,
        }
    }

    fn requires_target(self) -> bool {
        matches!(self, Self::Target)
    }

    fn pending_kind(self) -> &'static str {
        match self {
            Self::Target => "TARGET",
            Self::CasterRadius { .. } => "CASTER_RADIUS",
            Self::CasterCone { .. } => "CASTER_CONE",
        }
    }

    fn pending_range(self, target_range: f32) -> f32 {
        match self {
            Self::Target => target_range,
            Self::CasterRadius { radius } => radius,
            Self::CasterCone { range, .. } => range,
        }
    }

    fn pending_radius(self) -> f32 {
        match self {
            Self::CasterRadius { radius } => radius,
            _ => 0.0,
        }
    }

    fn pending_angle_degrees(self) -> f32 {
        match self {
            Self::CasterCone { angle_degrees, .. } => angle_degrees,
            _ => 0.0,
        }
    }
}

#[derive(Clone, Copy)]
struct ResolvedMeleeImpactArea {
    radius: f32,
    damage_multiplier: f32,
    hit_index: Option<u32>,
    include_primary_target: bool,
}

impl ResolvedMeleeImpactArea {
    fn applies_to_hit_index(self, hit_index: u32) -> bool {
        self.hit_index.is_none_or(|required| required == hit_index)
    }
}

#[derive(Clone, Copy)]
struct GapCloseActorSnapshot {
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    yaw: f32,
    hit_radius: f32,
    hit_height: f32,
}

#[derive(Clone, Copy)]
struct ResolvedMeleeGapClose {
    end: SpellVec3,
    duration_ms: u64,
    impact_range: f32,
}

#[derive(Clone, Debug)]
enum GapCloseResolveFailure {
    InvalidImpactRange {
        impact_range: f32,
    },
    UnsupportedKind {
        kind: String,
    },
    TargetFacingRequired,
    DestinationUnresolved {
        kind: String,
        destination: String,
    },
    DestinationBlocked {
        intended: SpellVec3,
        resolved: SpellVec3,
        delta: f32,
    },
    PathBlocked {
        intended: SpellVec3,
        end: SpellVec3,
        shortfall: f32,
    },
}

impl GapCloseResolveFailure {
    fn reason(&self) -> &'static str {
        match self {
            Self::InvalidImpactRange { .. } => "invalid_impact_range",
            Self::UnsupportedKind { .. } => "unsupported_kind",
            Self::TargetFacingRequired => "target_facing_required",
            Self::DestinationUnresolved { .. } => "destination_unresolved",
            Self::DestinationBlocked { .. } => "destination_blocked",
            Self::PathBlocked { .. } => "path_blocked",
        }
    }

    fn detail(&self) -> String {
        match self {
            Self::InvalidImpactRange { impact_range } => {
                format!("impact_range={impact_range:.2}")
            }
            Self::UnsupportedKind { kind } => format!("kind={kind}"),
            Self::TargetFacingRequired => String::new(),
            Self::DestinationUnresolved { kind, destination } => {
                format!("kind={kind} destination={destination}")
            }
            Self::DestinationBlocked {
                intended,
                resolved,
                delta,
            } => format!(
                "intended=({:.2},{:.2},{:.2}) resolved=({:.2},{:.2},{:.2}) delta={:.2}",
                intended.x, intended.y, intended.z, resolved.x, resolved.y, resolved.z, delta
            ),
            Self::PathBlocked {
                intended,
                end,
                shortfall,
            } => format!(
                "intended=({:.2},{:.2},{:.2}) baked_end=({:.2},{:.2},{:.2}) shortfall={:.2}",
                intended.x, intended.y, intended.z, end.x, end.y, end.z, shortfall
            ),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum GapClosePreCommitDecision {
    Continue,
    RejectBeforeCommit,
}

#[derive(Clone)]
#[table(accessor = queued_melee_followup)]
pub struct QueuedMeleeFollowup {
    #[primary_key]
    pub caster: Identity,
    pub strike_id: String,
    pub target: Identity,
    pub execute_not_before: Timestamp,
    #[index(btree)]
    pub execute_at_micros: i64,
}

#[derive(Clone)]
struct ComboFollowupWindow {
    queue_open_at: Timestamp,
    queue_close_at: Timestamp,
    execute_not_before: Timestamp,
}

#[derive(Clone, Copy)]
enum ComboInputDecision {
    Reject,
    ExecuteNow,
    QueueUntil(Timestamp),
}

fn is_combo_followup(strike: &StrikeData) -> bool {
    matches!(
        strike.combo_from.as_deref(),
        Some(required) if !required.is_empty()
    )
}

fn combo_release_allowed(
    caster_state: &crate::player_state::PlayerState,
    combat_profile: &str,
    strike: &StrikeData,
) -> bool {
    match strike
        .combo_from
        .as_deref()
        .filter(|required| !required.is_empty())
    {
        Some(required) => resolve_melee_action_reference(combat_profile, required)
            .map(|resolved| caster_state.last_strike_id == resolved.runtime_id.as_str())
            .unwrap_or(false),
        None => true,
    }
}

pub(crate) fn sync_melee_definitions(ctx: &ReducerContext) {
    let manifest = melee_manifest();
    let mut expected_keys = std::collections::HashSet::new();
    for profile in &manifest.profiles {
        let combat_profile = profile.combat_profile.as_str();
        let strikes = profile.strikes.as_slice();
        for strike in strikes {
            let runtime_action_id = runtime_action_id_for_strike(strike);
            let key = melee_definition_key_for_runtime(combat_profile, &runtime_action_id);
            expected_keys.insert(key.clone());
            let first_impact_delay_ms = strike
                .hit_windows
                .first()
                .map(|hit_window| hit_window.impact_delay_ms)
                .unwrap_or(0);
            let row = MeleeDefinition {
                key: key.clone(),
                combat_profile: combat_profile.to_string(),
                combat_style_id: combat_profile.to_string(),
                slot_id: runtime_action_id.as_str().to_string(),
                kind: runtime_action_id.as_str().to_string(),
                impact_delay_ms: first_impact_delay_ms,
                recovery_ms: strike.recovery_ms,
                is_gap_closer: strike.is_gap_closer,
                combo_from: strike
                    .combo_from
                    .as_deref()
                    .and_then(|required| resolve_melee_action_reference(combat_profile, required))
                    .map(|resolved| resolved.runtime_id)
                    .map(RuntimeActionId::into_string)
                    .unwrap_or_default(),
                combo_open_ms: strike.combo_open_ms,
                combo_grace_ms: strike.combo_grace_ms,
                aerial_execution_mode: strike.aerial_execution_mode.as_str().to_string(),
            };
            if ctx.db.melee_definition().key().find(key.clone()).is_some() {
                ctx.db.melee_definition().key().update(row);
            } else {
                ctx.db.melee_definition().insert(row);
            }
        }
    }

    let stale_keys: Vec<String> = ctx
        .db
        .melee_definition()
        .iter()
        .filter(|row| !expected_keys.contains(row.key.as_str()))
        .map(|row| row.key)
        .collect();
    for key in stale_keys {
        ctx.db.melee_definition().key().delete(key);
    }
}

pub(crate) fn get_melee_definition_for_authored(
    ctx: &ReducerContext,
    combat_profile: &str,
    authored_id: &AuthoredActionId,
) -> Option<MeleeDefinition> {
    let resolved = resolve_melee_authored_action(combat_profile, authored_id)?;
    get_melee_definition_for_runtime(ctx, combat_profile, &resolved.runtime_id)
}

fn get_melee_definition_for_runtime(
    ctx: &ReducerContext,
    combat_profile: &str,
    runtime_id: &RuntimeActionId,
) -> Option<MeleeDefinition> {
    let key = melee_definition_key_for_runtime(combat_profile, runtime_id);
    ctx.db.melee_definition().key().find(key)
}

pub(crate) fn melee_cadence_for(
    ctx: &ReducerContext,
    combat_profile: &str,
    authored_id: &AuthoredActionId,
) -> Option<Duration> {
    let gameplay = authored_melee_gameplay_for_profile(ctx, combat_profile, authored_id)?;
    Some(Duration::from_millis(gameplay.cooldown_ms.max(1) + 300))
}

pub(crate) fn melee_range_for(
    ctx: &ReducerContext,
    combat_profile: &str,
    authored_id: &AuthoredActionId,
) -> Option<f32> {
    authored_melee_gameplay_for_profile(ctx, combat_profile, authored_id)
        .map(|gameplay| gameplay.range)
}

pub(crate) fn scaled_auto_attack_cadence_ms(
    base_cooldown_ms: u64,
    attack_speed_multiplier: f32,
) -> u64 {
    let safe_multiplier = if attack_speed_multiplier.is_finite() {
        attack_speed_multiplier.max(0.05)
    } else {
        1.0
    };
    ((base_cooldown_ms.max(1) as f32) / safe_multiplier)
        .round()
        .max(1.0) as u64
}

fn projectile_max_distance_for_policy(
    projectile_definition_max_distance: f32,
    effective_range: f32,
    authorization: MeleeAuthorization,
) -> f32 {
    if authorization == MeleeAuthorization::IntrinsicAutoAttack {
        projectile_definition_max_distance.min(effective_range)
    } else {
        projectile_definition_max_distance
    }
}

#[cfg(test)]
fn strike_total_duration_ms(strike: &StrikeData) -> u64 {
    strike
        .hit_windows
        .last()
        .map(|hit_window| hit_window.impact_delay_ms)
        .unwrap_or(0)
        + strike.recovery_ms
}

fn melee_gameplay_from_catalog_rows(
    ability: &AbilityCatalog,
    melee: MeleeAbilityCatalog,
) -> Option<ResolvedMeleeGameplay> {
    if !ability.ability_kind.eq_ignore_ascii_case("MELEE") {
        return None;
    }
    if ability.ability_id != melee.ability_id {
        return None;
    }
    if ability.action_id != melee.action_id {
        return None;
    }
    Some(ResolvedMeleeGameplay {
        ability_id: Some(ability.ability_id.clone()),
        base_damage: melee.base_damage,
        range: melee.range,
        minimum_range: melee.minimum_range.max(0.0),
        cooldown_ms: melee.cooldown_ms,
        uses_global_cooldown: melee.uses_global_cooldown,
        global_cooldown_ms: melee.global_cooldown_ms,
        parry_behavior: ParryBehavior::from_wire(melee.parry_behavior.as_str())?,
        block_behavior: BlockBehavior::from_wire(melee.block_behavior.as_str())?,
        airborne_targeting_mode: AirborneTargetingMode::from_wire(
            melee.airborne_targeting_mode.as_str(),
        )?,
        targeting: ResolvedMeleeTargeting::from_catalog(&melee)?,
        applies_stagger: melee.applies_stagger,
        impact_area: resolved_melee_impact_area_from_catalog(&melee),
        timed_movement: melee_timed_movement_for_ability_id(ability.ability_id.as_str()),
    })
}

fn melee_gameplay_for_ability(
    ctx: &ReducerContext,
    ability: &AbilityCatalog,
) -> Option<ResolvedMeleeGameplay> {
    let melee = ctx
        .db
        .melee_ability_catalog()
        .ability_id()
        .find(ability.ability_id.clone())?;
    melee_gameplay_from_catalog_rows(ability, melee)
}

fn authored_melee_gameplay_for_profile(
    ctx: &ReducerContext,
    combat_profile: &str,
    authored_action_id: &AuthoredActionId,
) -> Option<ResolvedMeleeGameplay> {
    let profile = combat_profile.trim().to_ascii_uppercase();
    let ability = ctx.db.ability_catalog().iter().find(|ability| {
        ability.ability_kind.eq_ignore_ascii_case("MELEE")
            && ability.action_id == authored_action_id.as_str()
            && derived_combat_profile_id_for_ability(ctx, ability).is_some_and(|ability_profile| {
                ability_profile.eq_ignore_ascii_case(profile.as_str())
            })
    })?;
    melee_gameplay_for_ability(ctx, &ability)
}

fn derived_combat_profile_id_for_ability(
    ctx: &ReducerContext,
    ability: &AbilityCatalog,
) -> Option<String> {
    ctx.db
        .class_catalog()
        .class_id()
        .find(ability.class_id.clone())
        .map(|class| class.default_combat_profile_id)
}

fn active_melee_gameplay_for_resolved_action(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile: &str,
    resolved: &ResolvedMeleeStrike,
) -> Result<ResolvedMeleeGameplay, String> {
    if let Some(ability) =
        active_selectable_ability_for_authored_action(ctx, owner, &resolved.authored_id)
    {
        if let Some(gameplay) = melee_gameplay_for_ability(ctx, &ability) {
            return Ok(gameplay);
        }
        return Err(format!(
            "melee action '{}' is assigned as ability '{}' on the active spec, but that ability does not resolve to melee gameplay (ability_kind='{}')",
            resolved.authored_id.as_str(),
            ability.ability_id,
            ability.ability_kind
        ));
    }

    let root_authored_action_id =
        find_combo_root_for_authorization(combat_profile, &resolved.runtime_id);
    if root_authored_action_id.as_str() == resolved.authored_id.as_str() {
        return Err(format!(
            "melee action '{}' is not assigned on the active spec ({})",
            resolved.authored_id.as_str(),
            active_loadout_assignment_debug_summary(ctx, owner)
        ));
    }

    let Some(root_ability) =
        active_selectable_ability_for_authored_action(ctx, owner, &root_authored_action_id)
    else {
        return Err(format!(
            "melee combo root '{}' is not assigned on the active spec ({})",
            root_authored_action_id.as_str(),
            active_loadout_assignment_debug_summary(ctx, owner)
        ));
    };
    if !root_ability.ability_kind.eq_ignore_ascii_case("MELEE")
        || melee_gameplay_for_ability(ctx, &root_ability).is_none()
    {
        return Err(format!(
            "melee combo root '{}' is assigned as ability '{}' on the active spec, but that ability does not resolve to melee gameplay (ability_kind='{}')",
            root_authored_action_id.as_str(),
            root_ability.ability_id,
            root_ability.ability_kind
        ));
    }

    let Some(followup_ability) = ctx.db.ability_catalog().iter().find(|ability| {
        ability.ability_kind.eq_ignore_ascii_case("MELEE")
            && ability.class_id == root_ability.class_id
            && ability.action_id == resolved.authored_id.as_str()
    }) else {
        return Err(format!(
            "melee combo follow-up '{}' has no melee ability row",
            resolved.authored_id.as_str()
        ));
    };

    melee_gameplay_for_ability(ctx, &followup_ability).ok_or_else(|| {
        format!(
            "melee combo follow-up '{}' has no melee gameplay row",
            resolved.authored_id.as_str()
        )
    })
}

pub(crate) fn auto_attack_catalog_key(
    combat_profile: &str,
    action_id: &AuthoredActionId,
) -> String {
    auto_attack_catalog_key_for_mode(combat_profile, "", action_id)
}

pub(crate) fn auto_attack_catalog_key_for_mode(
    combat_profile: &str,
    mode_id: &str,
    action_id: &AuthoredActionId,
) -> String {
    let combat_profile = combat_profile.trim().to_ascii_uppercase();
    let mode_id = mode_id.trim().to_ascii_uppercase();
    if mode_id.is_empty() {
        return format!("{}:{}", combat_profile, action_id.as_str());
    }
    format!("{}:{}:{}", combat_profile, mode_id, action_id.as_str())
}

pub(crate) fn auto_attack_gameplay_for_profile_mode_action(
    ctx: &ReducerContext,
    combat_profile: &str,
    mode_id: &str,
    action_id: &AuthoredActionId,
) -> Option<AutoAttackCatalog> {
    let normalized_mode_id = mode_id.trim().to_ascii_uppercase();
    let mode_key = auto_attack_catalog_key_for_mode(combat_profile, mode_id, action_id);
    if let Some(row) = ctx.db.auto_attack_catalog().key().find(mode_key) {
        return Some(row);
    }

    if !normalized_mode_id.is_empty() && combat_profile_has_modes(ctx, combat_profile) {
        return None;
    }

    ctx.db
        .auto_attack_catalog()
        .key()
        .find(auto_attack_catalog_key(combat_profile, action_id))
}

fn auto_attack_melee_gameplay_from_catalog(
    row: AutoAttackCatalog,
) -> Option<ResolvedMeleeGameplay> {
    Some(ResolvedMeleeGameplay {
        ability_id: None,
        base_damage: row.base_damage,
        range: row.range,
        minimum_range: 0.0,
        cooldown_ms: row.cooldown_ms,
        uses_global_cooldown: row.uses_global_cooldown,
        global_cooldown_ms: row.global_cooldown_ms,
        parry_behavior: ParryBehavior::from_wire(row.parry_behavior.as_str())?,
        block_behavior: BlockBehavior::from_wire(row.block_behavior.as_str())?,
        airborne_targeting_mode: AirborneTargetingMode::from_wire(
            row.airborne_targeting_mode.as_str(),
        )?,
        targeting: ResolvedMeleeTargeting::Target,
        applies_stagger: row.applies_stagger,
        impact_area: None,
        timed_movement: None,
    })
}

fn auto_attack_replacement_melee_gameplay_from_catalog(
    row: &AutoAttackReplacementCatalog,
) -> Option<ResolvedMeleeGameplay> {
    Some(ResolvedMeleeGameplay {
        ability_id: None,
        base_damage: row.base_damage,
        range: row.range,
        minimum_range: 0.0,
        cooldown_ms: row.cooldown_ms,
        uses_global_cooldown: row.uses_global_cooldown,
        global_cooldown_ms: row.global_cooldown_ms,
        parry_behavior: ParryBehavior::from_wire(row.parry_behavior.as_str())?,
        block_behavior: BlockBehavior::from_wire(row.block_behavior.as_str())?,
        airborne_targeting_mode: AirborneTargetingMode::from_wire(
            row.airborne_targeting_mode.as_str(),
        )?,
        targeting: ResolvedMeleeTargeting::Target,
        applies_stagger: row.applies_stagger,
        impact_area: None,
        timed_movement: None,
    })
}

fn resolved_melee_impact_area_from_catalog(
    melee: &MeleeAbilityCatalog,
) -> Option<ResolvedMeleeImpactArea> {
    if !melee.impact_area_radius.is_finite()
        || melee.impact_area_radius <= 0.0
        || !melee.impact_area_damage_multiplier.is_finite()
        || melee.impact_area_damage_multiplier <= 0.0
    {
        return None;
    }

    Some(ResolvedMeleeImpactArea {
        radius: melee.impact_area_radius,
        damage_multiplier: melee.impact_area_damage_multiplier,
        hit_index: if melee.impact_area_hit_index >= 0 {
            Some(melee.impact_area_hit_index as u32)
        } else {
            None
        },
        include_primary_target: melee.impact_area_include_primary_target,
    })
}

fn resolved_hit_window_damages(strike: &StrikeData, total_damage: i32) -> Vec<i32> {
    if strike.hit_windows.is_empty() {
        return Vec::new();
    }

    if strike.hit_windows.len() == 1 {
        return vec![total_damage.max(0)];
    }

    let total_override = total_damage.max(0);
    let count = strike.hit_windows.len() as i32;
    let base = total_override / count;
    let remainder = total_override % count;
    let mut resolved = vec![base; strike.hit_windows.len()];
    for damage in resolved.iter_mut().take(remainder as usize) {
        *damage += 1;
    }
    resolved
}

fn yaw_direction(yaw: f32) -> (f32, f32) {
    (yaw.sin(), yaw.cos())
}

fn right_direction(yaw: f32) -> (f32, f32) {
    (yaw.cos(), -yaw.sin())
}

fn melee_gap_close_for_ability(
    ctx: &ReducerContext,
    ability_id: Option<&str>,
) -> Option<MeleeGapCloseCatalog> {
    let ability_id = ability_id?;
    ctx.db
        .melee_gap_close_catalog()
        .ability_id()
        .find(ability_id.to_string())
}

fn actor_contact_distance(
    caster: GapCloseActorSnapshot,
    target: GapCloseActorSnapshot,
    arrival_buffer: f32,
) -> f32 {
    contact_distance_from_radii(caster.hit_radius, target.hit_radius, arrival_buffer)
}

fn offset_from_target(
    target: GapCloseActorSnapshot,
    dir_x: f32,
    dir_z: f32,
    contact_distance: f32,
) -> SpellVec3 {
    SpellVec3::new(
        target.pos_x + dir_x * contact_distance,
        target.pos_y,
        target.pos_z + dir_z * contact_distance,
    )
}

fn resolve_gap_close_destination(
    gap_close: &MeleeGapCloseCatalog,
    caster: GapCloseActorSnapshot,
    target: GapCloseActorSnapshot,
) -> Option<SpellVec3> {
    let destination = if gap_close.kind.as_str() == GAP_CLOSE_KIND_TELEPORT_BEHIND {
        GAP_CLOSE_DESTINATION_BEHIND_TARGET
    } else {
        gap_close.destination.as_str()
    };
    let contact_distance = actor_contact_distance(caster, target, gap_close.arrival_buffer);

    match destination {
        GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT | GAP_CLOSE_DESTINATION_CURRENT_LINE => {
            let dx = target.pos_x - caster.pos_x;
            let dz = target.pos_z - caster.pos_z;
            let distance = (dx * dx + dz * dz).sqrt();
            if distance <= contact_distance + gap_close.arrival_epsilon.max(0.0) {
                return Some(SpellVec3::new(caster.pos_x, caster.pos_y, caster.pos_z));
            }

            let (x, z) = approach_line_contact_point_xz(
                caster.pos_x,
                caster.pos_z,
                caster.yaw,
                target.pos_x,
                target.pos_z,
                contact_distance,
            );
            Some(SpellVec3::new(x, target.pos_y, z))
        }
        GAP_CLOSE_DESTINATION_BEHIND_TARGET => {
            let (target_forward_x, target_forward_z) = yaw_direction(target.yaw);
            Some(offset_from_target(
                target,
                -target_forward_x,
                -target_forward_z,
                contact_distance,
            ))
        }
        GAP_CLOSE_DESTINATION_TARGET_SIDE_LEFT => {
            let (right_x, right_z) = right_direction(target.yaw);
            Some(offset_from_target(
                target,
                -right_x,
                -right_z,
                contact_distance,
            ))
        }
        GAP_CLOSE_DESTINATION_TARGET_SIDE_RIGHT => {
            let (right_x, right_z) = right_direction(target.yaw);
            Some(offset_from_target(
                target,
                right_x,
                right_z,
                contact_distance,
            ))
        }
        _ => None,
    }
}

fn gap_close_requires_clear_path(gap_close: &MeleeGapCloseCatalog) -> bool {
    gap_close.collision_policy.as_str() == GAP_CLOSE_COLLISION_REQUIRE_CLEAR_PATH
}

fn gap_close_destination_within_epsilon(
    actual: SpellVec3,
    intended: SpellVec3,
    arrival_epsilon: f32,
) -> bool {
    gap_close_destination_delta(actual, intended) <= arrival_epsilon.max(0.0)
}

fn gap_close_destination_delta(actual: SpellVec3, intended: SpellVec3) -> f32 {
    let dx = actual.x - intended.x;
    let dy = actual.y - intended.y;
    let dz = actual.z - intended.z;
    (dx * dx + dy * dy + dz * dz).sqrt()
}

fn gap_close_target_facing_satisfied(
    gap_close: &MeleeGapCloseCatalog,
    caster: GapCloseActorSnapshot,
    target: GapCloseActorSnapshot,
) -> bool {
    !gap_close.requires_target_facing
        || is_direction_within_facing_arc(
            caster.yaw,
            target.pos_x - caster.pos_x,
            target.pos_z - caster.pos_z,
            MELEE_TARGET_FACING_ARC_RADIANS,
            0.0,
        )
}

fn gap_close_uses_flat_training_collision(ctx: &ReducerContext, owner: Identity) -> bool {
    let Some(world) = ctx.db.player_world().identity().find(owner) else {
        return false;
    };
    let Some(instance_id) = world.instance_id else {
        return false;
    };
    is_training_instance(ctx, instance_id)
}

fn validate_gap_close_destination(
    ctx: &ReducerContext,
    owner: Identity,
    destination: SpellVec3,
    hit_radius: f32,
    hit_height: f32,
    arrival_epsilon: f32,
) -> Result<SpellVec3, GapCloseResolveFailure> {
    let arena_seed = arena_seed_for_identity(ctx, owner);
    let flat_ground_only = gap_close_uses_flat_training_collision(ctx, owner);
    let open_world_scene_name = open_world_scene_name_for_identity(ctx, owner);
    let ground_y = surface_height_for_world_at_y_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        Some(open_world_scene_name.as_str()),
        destination.x,
        destination.z,
        destination.y,
    );
    let (resolved_x, resolved_z) = resolve_world_horizontal_collision_y_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        Some(open_world_scene_name.as_str()),
        destination.x,
        destination.z,
        hit_radius,
        hit_height,
        ground_y,
    );
    let resolved = SpellVec3::new(resolved_x, destination.y, resolved_z);
    if !gap_close_destination_within_epsilon(resolved, destination, arrival_epsilon) {
        return Err(GapCloseResolveFailure::DestinationBlocked {
            intended: destination,
            resolved,
            delta: gap_close_destination_delta(resolved, destination),
        });
    }

    Ok(SpellVec3::new(destination.x, ground_y, destination.z))
}

fn resolve_melee_gap_close(
    ctx: &ReducerContext,
    owner: Identity,
    gap_close: &MeleeGapCloseCatalog,
    caster: GapCloseActorSnapshot,
    target: GapCloseActorSnapshot,
) -> Result<ResolvedMeleeGapClose, GapCloseResolveFailure> {
    if gap_close.impact_range <= 0.0 {
        return Err(GapCloseResolveFailure::InvalidImpactRange {
            impact_range: gap_close.impact_range,
        });
    }
    if !matches!(
        gap_close.kind.as_str(),
        GAP_CLOSE_KIND_LINEAR
            | GAP_CLOSE_KIND_LEAP
            | GAP_CLOSE_KIND_TELEPORT
            | GAP_CLOSE_KIND_TELEPORT_BEHIND
    ) {
        return Err(GapCloseResolveFailure::UnsupportedKind {
            kind: gap_close.kind.clone(),
        });
    }
    if !gap_close_target_facing_satisfied(gap_close, caster, target) {
        return Err(GapCloseResolveFailure::TargetFacingRequired);
    }

    let intended_end =
        resolve_gap_close_destination(gap_close, caster, target).ok_or_else(|| {
            GapCloseResolveFailure::DestinationUnresolved {
                kind: gap_close.kind.clone(),
                destination: gap_close.destination.clone(),
            }
        })?;
    let start = SpellVec3::new(caster.pos_x, caster.pos_y, caster.pos_z);
    let is_teleport = matches!(
        gap_close.kind.as_str(),
        GAP_CLOSE_KIND_TELEPORT | GAP_CLOSE_KIND_TELEPORT_BEHIND
    );
    let collision_policy = SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK;
    let end = if is_teleport {
        if gap_close_requires_clear_path(gap_close) {
            validate_gap_close_destination(
                ctx,
                owner,
                intended_end,
                caster.hit_radius,
                caster.hit_height,
                gap_close.arrival_epsilon,
            )?
        } else {
            intended_end
        }
    } else {
        bake_linear_special_movement(
            ctx,
            owner,
            start,
            intended_end,
            caster.hit_radius,
            caster.hit_height,
            collision_policy,
        )
        .end
    };

    if gap_close_requires_clear_path(gap_close) {
        if !gap_close_destination_within_epsilon(end, intended_end, gap_close.arrival_epsilon) {
            return Err(GapCloseResolveFailure::PathBlocked {
                intended: intended_end,
                end,
                shortfall: gap_close_destination_delta(end, intended_end),
            });
        }
    }

    let duration_ms = if is_teleport {
        0
    } else if !gap_close_has_horizontal_travel(start, end) {
        0
    } else {
        horizontal_movement_duration_ms(start.x, start.z, end.x, end.z, gap_close.speed, 1)
    };
    Ok(ResolvedMeleeGapClose {
        end,
        duration_ms,
        impact_range: gap_close.impact_range,
    })
}

fn gap_close_has_horizontal_travel(start: SpellVec3, end: SpellVec3) -> bool {
    let dx = end.x - start.x;
    let dz = end.z - start.z;
    dx * dx + dz * dz > 0.0001
}

fn timed_melee_movement_destination(start: SpellVec3, yaw: f32, distance: f32) -> SpellVec3 {
    let backward_x = -yaw.sin();
    let backward_z = -yaw.cos();
    SpellVec3::new(
        start.x + backward_x * distance.max(0.0),
        start.y,
        start.z + backward_z * distance.max(0.0),
    )
}

fn schedule_melee_timed_movement(
    ctx: &ReducerContext,
    owner: Identity,
    action_instance_id: &str,
    action_kind: &str,
    movement: &MeleeTimedMovementRuntime,
    yaw_start: f32,
    now: Timestamp,
) {
    let start_at = now + Duration::from_millis(movement.start_delay_ms);
    ctx.db
        .pending_melee_timed_movement()
        .insert(PendingMeleeTimedMovement {
            movement_id: 0,
            owner,
            action_instance_id: action_instance_id.to_string(),
            action_kind: action_kind.to_string(),
            ability_id: movement.ability_id.clone(),
            kind: movement.kind.clone(),
            direction: movement.direction.clone(),
            distance: movement.distance,
            speed: movement.speed,
            collision_policy: movement.collision_policy.clone(),
            facing_policy: movement.facing_policy.clone(),
            yaw_start,
            start_at,
            start_at_micros: timestamp_to_micros(start_at),
        });
}

fn gap_close_pre_commit_decision(
    gap_close: Option<&MeleeGapCloseCatalog>,
    resolved_gap_close: Option<ResolvedMeleeGapClose>,
) -> GapClosePreCommitDecision {
    if matches!(
        gap_close,
        Some(gap_close) if gap_close.require_arrival_for_swing && resolved_gap_close.is_none()
    ) {
        GapClosePreCommitDecision::RejectBeforeCommit
    } else {
        GapClosePreCommitDecision::Continue
    }
}

fn scheduled_melee_impact_at(
    now: Timestamp,
    hit_window_impact_delay_ms: u64,
    resolved_gap_close: Option<ResolvedMeleeGapClose>,
) -> Timestamp {
    let authored_impact_at = now + Duration::from_millis(hit_window_impact_delay_ms);
    let arrival_at = resolved_gap_close
        .map(|gap_close| now + Duration::from_millis(gap_close.duration_ms))
        .unwrap_or(now);
    authored_impact_at.max(arrival_at)
}

fn pending_melee_impact_range(
    effective_range: f32,
    gap_close: Option<&MeleeGapCloseCatalog>,
    resolved_gap_close: Option<ResolvedMeleeGapClose>,
) -> f32 {
    resolved_gap_close
        .map(|gap_close| gap_close.impact_range)
        .or_else(|| gap_close.map(|gap_close| gap_close.impact_range))
        .unwrap_or(effective_range)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum StaggerDirection {
    Forward,
    Back,
    Left,
    Right,
}

fn classify_stagger_direction(
    hit_dir_x: f32,
    hit_dir_z: f32,
    forward_x: f32,
    forward_z: f32,
) -> StaggerDirection {
    let hit_len_sq = hit_dir_x * hit_dir_x + hit_dir_z * hit_dir_z;
    let forward_len_sq = forward_x * forward_x + forward_z * forward_z;
    let (fwd_x, fwd_z) = if forward_len_sq > 0.0001 {
        let inv_len = 1.0 / forward_len_sq.sqrt();
        (forward_x * inv_len, forward_z * inv_len)
    } else {
        (0.0, 1.0)
    };
    let (hit_x, hit_z) = if hit_len_sq > 0.0001 {
        let inv_len = 1.0 / hit_len_sq.sqrt();
        (hit_dir_x * inv_len, hit_dir_z * inv_len)
    } else {
        (fwd_x, fwd_z)
    };

    let dot = (fwd_x * hit_x + fwd_z * hit_z).clamp(-1.0, 1.0);
    let cross_y = fwd_z * hit_x - fwd_x * hit_z;
    let angle_degrees = cross_y.atan2(dot).to_degrees();
    if !(-135.0..=135.0).contains(&angle_degrees) {
        StaggerDirection::Back
    } else if angle_degrees < -45.0 {
        StaggerDirection::Left
    } else if angle_degrees < 45.0 {
        StaggerDirection::Forward
    } else {
        StaggerDirection::Right
    }
}

fn stagger_hit_direction_for_physics(
    source_x: f32,
    source_z: f32,
    target_x: f32,
    target_z: f32,
    target_yaw: f32,
) -> (f32, f32) {
    let dx = target_x - source_x;
    let dz = target_z - source_z;
    let len_sq = dx * dx + dz * dz;
    if len_sq > 0.0001 {
        let inv_len = 1.0 / len_sq.sqrt();
        (dx * inv_len, dz * inv_len)
    } else {
        yaw_direction(target_yaw)
    }
}

fn stagger_duration_for_direction(profile: &MeleeProfileData, direction: StaggerDirection) -> u64 {
    match direction {
        StaggerDirection::Forward => profile.stagger_duration_f_ms,
        StaggerDirection::Back => profile.stagger_duration_b_ms,
        StaggerDirection::Left => profile.stagger_duration_l_ms,
        StaggerDirection::Right => profile.stagger_duration_r_ms,
    }
}

fn stagger_duration_for_target(ctx: &ReducerContext, source: Identity, target: Identity) -> u64 {
    let Some(source_phys) = ctx.db.player_physics().identity().find(source) else {
        return 0;
    };
    let Some(target_phys) = ctx.db.player_physics().identity().find(target) else {
        return 0;
    };
    let combat_profile = combat_profile_for_identity(ctx, target);
    let Some(profile) = melee_manifest().profiles.iter().find(|profile| {
        profile
            .combat_profile
            .eq_ignore_ascii_case(combat_profile.as_str())
    }) else {
        return 0;
    };
    let (hit_dir_x, hit_dir_z) = stagger_hit_direction_for_physics(
        source_phys.pos_x,
        source_phys.pos_z,
        target_phys.pos_x,
        target_phys.pos_z,
        target_phys.yaw,
    );
    let (forward_x, forward_z) = yaw_direction(target_phys.yaw);
    stagger_duration_for_direction(
        profile,
        classify_stagger_direction(hit_dir_x, hit_dir_z, forward_x, forward_z),
    )
}

fn finalize_melee_cast(
    ctx: &ReducerContext,
    caster: Identity,
    _strike: &StrikeData,
    runtime_action_id: &RuntimeActionId,
    cooldown_key_override: Option<&str>,
    gameplay: &ResolvedMeleeGameplay,
    now: Timestamp,
    policy: MeleeExecutionPolicy,
    mut caster_state: crate::player_state::PlayerState,
) {
    if policy.uses_shared_cooldowns {
        if gameplay.uses_global_cooldown {
            stamp_global_cooldown_for_duration(
                ctx,
                caster,
                Duration::from_millis(gameplay.global_cooldown_ms.max(1)),
                now,
            );
        }
        let cooldown_key = cooldown_key_override.unwrap_or(runtime_action_id.as_str());
        stamp_named_cooldown_for_duration(
            ctx,
            caster,
            cooldown_key,
            Duration::from_millis(gameplay.cooldown_ms.max(1)),
            now,
        );
    }

    caster_state.last_strike_id = runtime_action_id.as_str().to_string();
    caster_state.last_strike_at = now;
    ctx.db.player_state().player_id().update(caster_state);
    clear_queued_melee_followup(ctx, caster);
}

fn combo_followup_window(last_strike_at: Timestamp, successor: &StrikeData) -> ComboFollowupWindow {
    let queue_open_at = last_strike_at;
    let execute_not_before = last_strike_at + Duration::from_millis(successor.combo_open_ms);
    let queue_close_at = execute_not_before + Duration::from_millis(successor.combo_grace_ms);

    ComboFollowupWindow {
        queue_open_at,
        queue_close_at,
        execute_not_before,
    }
}

fn combo_input_decision(
    caster_state: &crate::player_state::PlayerState,
    combat_profile: &str,
    strike: &StrikeData,
    now: Timestamp,
) -> ComboInputDecision {
    let Some(required) = strike
        .combo_from
        .as_deref()
        .filter(|required| !required.is_empty())
    else {
        return ComboInputDecision::ExecuteNow;
    };
    let Some(required_slot_id) = resolve_melee_action_reference(combat_profile, required)
        .map(|resolved| resolved.runtime_id)
    else {
        return ComboInputDecision::Reject;
    };
    if caster_state.last_strike_id != required_slot_id.as_str() {
        return ComboInputDecision::Reject;
    }
    let Some(_) = strike_by_runtime_action_id(combat_profile, &required_slot_id) else {
        return ComboInputDecision::Reject;
    };

    let followup_window = combo_followup_window(caster_state.last_strike_at, strike);
    if now < followup_window.queue_open_at {
        return ComboInputDecision::Reject;
    }
    if now > followup_window.queue_close_at {
        return ComboInputDecision::Reject;
    }
    if now < followup_window.execute_not_before {
        return ComboInputDecision::QueueUntil(followup_window.execute_not_before);
    }

    ComboInputDecision::ExecuteNow
}

fn upsert_queued_melee_followup(
    ctx: &ReducerContext,
    caster: Identity,
    runtime_action_id: &RuntimeActionId,
    target: Identity,
    execute_not_before: Timestamp,
) {
    let row = QueuedMeleeFollowup {
        caster,
        strike_id: runtime_action_id.as_str().to_string(),
        target,
        execute_not_before,
        execute_at_micros: timestamp_to_micros(execute_not_before),
    };
    if ctx
        .db
        .queued_melee_followup()
        .caster()
        .find(caster)
        .is_some()
    {
        ctx.db.queued_melee_followup().caster().update(row);
    } else {
        ctx.db.queued_melee_followup().insert(row);
    }
}

fn clear_queued_melee_followup(ctx: &ReducerContext, caster: Identity) {
    if ctx
        .db
        .queued_melee_followup()
        .caster()
        .find(caster)
        .is_some()
    {
        ctx.db.queued_melee_followup().caster().delete(caster);
    }
}

fn combat_profile_for_identity(ctx: &ReducerContext, identity: Identity) -> String {
    derived_combat_profile_id_for_owner(ctx, identity)
        .filter(|profile| !profile.trim().is_empty())
        .unwrap_or_else(|| DEFAULT_COMBAT_PROFILE.to_string())
}

fn resolve_melee_action_reference_for_caster(
    ctx: &ReducerContext,
    caster: Identity,
    strike_id: &str,
) -> Result<(String, ResolvedMeleeStrike), String> {
    let combat_profile = combat_profile_for_identity(ctx, caster);
    let Some(resolved) = resolve_melee_action_reference(combat_profile.as_str(), strike_id) else {
        return Err(format!(
            "Unknown melee strike: {}:{}",
            combat_profile, strike_id
        ));
    };
    Ok((combat_profile, resolved))
}

fn resolve_melee_authored_action_for_caster(
    ctx: &ReducerContext,
    caster: Identity,
    authored_id: &AuthoredActionId,
) -> Result<(String, ResolvedMeleeStrike), String> {
    let combat_profile = combat_profile_for_identity(ctx, caster);
    let Some(resolved) = resolve_melee_authored_action(combat_profile.as_str(), authored_id) else {
        return Err(format!(
            "Unknown melee strike: {}:{}",
            combat_profile,
            authored_id.as_str()
        ));
    };
    Ok((combat_profile, resolved))
}

fn perform_predicted_melee_attack_for(
    ctx: &ReducerContext,
    caster: Identity,
    strike_id: &str,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
    token: ActionPredictionToken,
) -> Result<MeleeAttackDispatch, String> {
    let (combat_profile, resolved) =
        resolve_melee_action_reference_for_caster(ctx, caster, strike_id)?;

    perform_melee_attack_for_internal(
        ctx,
        caster,
        combat_profile,
        resolved,
        target_id,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        true,
        MeleeExecutionPolicy::PLAYER_INPUT,
        None,
        None,
        None,
        Some(token),
    )
}

fn perform_unpredicted_melee_attack_for(
    ctx: &ReducerContext,
    caster: Identity,
    strike_id: &str,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
) -> Result<MeleeAttackDispatch, String> {
    let (combat_profile, resolved) =
        resolve_melee_action_reference_for_caster(ctx, caster, strike_id)?;

    perform_melee_attack_for_internal(
        ctx,
        caster,
        combat_profile,
        resolved,
        target_id,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        true,
        MeleeExecutionPolicy::PLAYER_INPUT,
        None,
        None,
        None,
        None,
    )
}

pub(crate) fn perform_melee_attack_for_practice(
    ctx: &ReducerContext,
    caster: Identity,
    strike_id: &AuthoredActionId,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
) -> Result<(), String> {
    let (combat_profile, resolved) =
        resolve_melee_authored_action_for_caster(ctx, caster, strike_id)?;
    perform_melee_attack_for_internal(
        ctx,
        caster,
        combat_profile,
        resolved,
        target_id,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        true,
        MeleeExecutionPolicy::PRACTICE,
        None,
        None,
        None,
        None,
    )
    .map(|_| ())
}

pub(crate) fn perform_intrinsic_auto_attack_for(
    ctx: &ReducerContext,
    caster: Identity,
    strike_id: &AuthoredActionId,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
) -> Result<MeleeAttackDispatch, String> {
    let (combat_profile, resolved) =
        resolve_melee_authored_action_for_caster(ctx, caster, strike_id)?;
    let mode_id = resolved_auto_attack_mode_for_owner(ctx, caster, combat_profile.as_str());
    let auto_attack_row = auto_attack_gameplay_for_profile_mode_action(
        ctx,
        combat_profile.as_str(),
        mode_id.as_str(),
        &resolved.authored_id,
    )
    .ok_or_else(|| {
        format!(
            "auto-attack '{}' has no gameplay row for combat profile '{}' mode '{}'",
            resolved.authored_id.as_str(),
            combat_profile,
            mode_id
        )
    })?;
    let gameplay = auto_attack_melee_gameplay_from_catalog(auto_attack_row).ok_or_else(|| {
        format!(
            "auto-attack '{}' has invalid gameplay row for combat profile '{}' mode '{}'",
            resolved.authored_id.as_str(),
            combat_profile,
            mode_id
        )
    })?;
    perform_melee_attack_for_internal(
        ctx,
        caster,
        combat_profile,
        resolved,
        target_id,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        false,
        MeleeExecutionPolicy::INTRINSIC_AUTO_ATTACK,
        None,
        Some(gameplay),
        None,
        None,
    )
}

#[derive(Clone, Debug)]
struct ResolvedMeleeActionResourceCost {
    ability_id: String,
    ability_action_id: String,
    ability_resource_kind: String,
    cost: ResolvedActionResourceCost,
}

fn resolved_melee_action_resource_cost(
    ctx: &ReducerContext,
    owner: Identity,
    combat_profile: &str,
    resolved: &ResolvedMeleeStrike,
) -> Result<Option<ResolvedMeleeActionResourceCost>, String> {
    let root_authored_action_id =
        find_combo_root_for_authorization(combat_profile, &resolved.runtime_id);

    let ability = active_selectable_ability_for_authored_action(ctx, owner, &resolved.authored_id)
        .or_else(|| {
            active_selectable_ability_for_authored_action(ctx, owner, &root_authored_action_id)
        });

    let Some(ability) = ability else {
        return Err(format!(
            "melee action '{}' is not assigned on the active spec ({})",
            root_authored_action_id.as_str(),
            active_loadout_assignment_debug_summary(ctx, owner)
        ));
    };

    let cost = resolve_ability_action_resource_cost(ctx, owner, &ability).ok_or_else(|| {
        format!(
            "melee action '{}' resource kind does not match active primary resource",
            ability.action_id
        )
    })?;

    Ok(Some(ResolvedMeleeActionResourceCost {
        ability_id: ability.ability_id,
        ability_action_id: ability.action_id,
        ability_resource_kind: ability.resource_kind,
        cost,
    }))
}

pub(crate) fn perform_intrinsic_auto_attack_replacement_for(
    ctx: &ReducerContext,
    caster: Identity,
    ability: &AbilityCatalog,
    replacement: &AutoAttackReplacementCatalog,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
) -> Result<MeleeAttackDispatch, String> {
    let authored_strike_id = AuthoredActionId::new(replacement.authored_melee_strike_id.as_str());
    let (combat_profile, resolved) =
        resolve_melee_authored_action_for_caster(ctx, caster, &authored_strike_id)?;
    if !combat_profile.eq_ignore_ascii_case(replacement.combat_profile_id.as_str()) {
        return Err(format!(
            "auto-attack replacement '{}' is authored for combat profile '{}' but caster resolved '{}'",
            replacement.replacement_id, replacement.combat_profile_id, combat_profile
        ));
    }

    let cost = resolve_ability_action_resource_cost(ctx, caster, ability).ok_or_else(|| {
        format!(
            "auto-attack replacement '{}' resource kind does not match active primary resource",
            ability.ability_id
        )
    })?;
    let gameplay =
        auto_attack_replacement_melee_gameplay_from_catalog(replacement).ok_or_else(|| {
            format!(
                "auto-attack replacement '{}' has invalid gameplay row",
                replacement.replacement_id
            )
        })?;
    if gameplay.uses_global_cooldown && is_on_global_cooldown(ctx, caster, ctx.timestamp) {
        return Ok(MeleeAttackDispatch::Rejected);
    }
    let mut policy = MeleeExecutionPolicy::INTRINSIC_AUTO_ATTACK_REPLACEMENT;
    policy.grants_primary_resource_on_hit = replacement.grants_primary_resource_on_hit;

    perform_melee_attack_for_internal(
        ctx,
        caster,
        combat_profile,
        resolved,
        target_id,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        false,
        policy,
        Some(ResolvedMeleeActionResourceCost {
            ability_id: ability.ability_id.clone(),
            ability_action_id: ability.action_id.clone(),
            ability_resource_kind: ability.resource_kind.clone(),
            cost,
        }),
        Some(gameplay),
        Some(ability.ability_id.clone()),
        None,
    )
}

fn log_melee_resource_resolution(
    caster: Identity,
    policy: MeleeExecutionPolicy,
    strike: &StrikeData,
    slot_id: &str,
    cost: Option<&ResolvedMeleeActionResourceCost>,
) {
    if let Some(cost) = cost {
        log::info!(
            "[MELEE_RESOURCE] owner={} source={} strike={} slot={} ability={} ability_action={} resource_kind={} cost={:.3}",
            short_identity(caster),
            policy.source_label(),
            strike.id,
            slot_id,
            cost.ability_id,
            cost.ability_action_id,
            cost.ability_resource_kind,
            cost.cost.amount
        );
    } else {
        log::info!(
            "[MELEE_RESOURCE] owner={} source={} strike={} slot={} no_action_resource_cost",
            short_identity(caster),
            policy.source_label(),
            strike.id,
            slot_id,
        );
    }
}

fn resolve_melee_attack_modifiers(
    ctx: &ReducerContext,
    caster: Identity,
    now: Timestamp,
) -> ResolvedMeleeAttackModifiers {
    let mut resolved = ResolvedMeleeAttackModifiers::default();
    for spec in MELEE_ATTACK_MODIFIER_SPECS {
        if !has_active_status_group(ctx, caster, spec.status_kind, spec.status_group, now) {
            continue;
        }

        if let Some(min_range) = spec.min_range {
            resolved.min_range = Some(resolved.min_range.unwrap_or(0.0).max(min_range));
        }
        resolved.force_stagger |= spec.force_stagger;
        resolved.consumed.push(ConsumedMeleeAttackModifier {
            status_kind: spec.status_kind,
            status_group: spec.status_group,
        });
    }
    resolved
}

fn consume_melee_attack_modifiers(
    ctx: &ReducerContext,
    caster: Identity,
    modifiers: &ResolvedMeleeAttackModifiers,
) {
    for consumed in &modifiers.consumed {
        remove_active_status_group(ctx, caster, consumed.status_kind, consumed.status_group);
    }
}

fn consumed_melee_modifier_event_fields(
    modifiers: &ResolvedMeleeAttackModifiers,
) -> (&'static str, &'static str) {
    modifiers
        .consumed
        .first()
        .map(|consumed| (consumed.status_kind.as_str(), consumed.status_group))
        .unwrap_or(("", ""))
}

fn grant_primary_resource_for_melee_event_hit(
    ctx: &ReducerContext,
    owner: Identity,
    grants_primary_resource_on_hit: bool,
    now: Timestamp,
) {
    if grants_primary_resource_on_hit {
        grant_primary_resource_for_melee_hit(ctx, owner, now);
    }
}

fn resolve_strike_projectile_data(
    ctx: &ReducerContext,
    strike: &StrikeData,
) -> Result<Option<ResolvedStrikeProjectileData>, String> {
    let Some(projectile) = strike.projectile.as_ref() else {
        return Ok(None);
    };
    let projectile_id = projectile.projectile_id.trim().to_ascii_uppercase();
    let Some(definition) = combat_projectile_definition_for_id(ctx, projectile_id.as_str()) else {
        return Err(format!(
            "Projectile strike '{}' references unknown projectile '{}'",
            strike.id, projectile.projectile_id
        ));
    };

    let resolved = ResolvedStrikeProjectileData {
        projectile_id,
        speed: positive_projectile_override(projectile.speed, definition.speed),
        max_distance: positive_projectile_override(
            projectile.max_distance,
            definition.max_distance,
        ),
        radius: positive_projectile_override(projectile.radius, definition.radius),
        spawn_forward: positive_projectile_override(
            projectile.spawn_forward,
            definition.spawn_forward,
        ),
        spawn_height: positive_projectile_override(
            projectile.spawn_height,
            definition.spawn_height,
        ),
        aim_height_scale: positive_projectile_override(
            projectile.aim_height_scale,
            definition.aim_height_scale,
        ),
        requires_initial_line_of_sight: projectile
            .requires_initial_line_of_sight
            .unwrap_or(definition.requires_initial_line_of_sight),
        update_interval_seconds: positive_projectile_override(
            projectile.update_interval_seconds,
            definition.update_interval_seconds,
        ),
    };
    validate_resolved_projectile_data(&resolved, &definition, strike)?;
    Ok(Some(resolved))
}

fn positive_projectile_override(value: Option<f32>, fallback: f32) -> f32 {
    value
        .filter(|value| value.is_finite() && *value > 0.0)
        .unwrap_or(fallback)
}

fn validate_resolved_projectile_data(
    projectile: &ResolvedStrikeProjectileData,
    definition: &CombatProjectileDefinition,
    strike: &StrikeData,
) -> Result<(), String> {
    if !projectile.speed.is_finite() || projectile.speed <= 0.0 {
        return Err(format!(
            "Projectile strike '{}' has invalid speed for '{}'",
            strike.id, definition.projectile_id
        ));
    }
    if !projectile.max_distance.is_finite() || projectile.max_distance <= 0.0 {
        return Err(format!(
            "Projectile strike '{}' has invalid max_distance for '{}'",
            strike.id, definition.projectile_id
        ));
    }
    if !projectile.radius.is_finite() || projectile.radius <= 0.0 {
        return Err(format!(
            "Projectile strike '{}' has invalid radius for '{}'",
            strike.id, definition.projectile_id
        ));
    }
    if !projectile.spawn_forward.is_finite()
        || !projectile.spawn_height.is_finite()
        || !projectile.aim_height_scale.is_finite()
        || !projectile.update_interval_seconds.is_finite()
        || projectile.update_interval_seconds < 0.0
    {
        return Err(format!(
            "Projectile strike '{}' has invalid projectile offsets/timing for '{}'",
            strike.id, definition.projectile_id
        ));
    }
    Ok(())
}

fn perform_melee_attack_for_internal(
    ctx: &ReducerContext,
    caster: Identity,
    combat_profile: String,
    resolved: ResolvedMeleeStrike,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
    allow_queue: bool,
    policy: MeleeExecutionPolicy,
    action_resource_cost_override: Option<ResolvedMeleeActionResourceCost>,
    gameplay_override: Option<ResolvedMeleeGameplay>,
    cooldown_key_override: Option<String>,
    prediction_token: Option<ActionPredictionToken>,
) -> Result<MeleeAttackDispatch, String> {
    let _ = (cast_pos_x, cast_pos_y, cast_pos_z, cast_yaw);

    let authored_action_id = resolved.authored_id.clone();
    let runtime_action_id = resolved.runtime_id.clone();
    let strike = resolved.strike.clone();
    let action_resource_cost = if let Some(cost) = action_resource_cost_override {
        Some(cost)
    } else if policy.authorization == MeleeAuthorization::SelectableLoadout {
        resolved_melee_action_resource_cost(ctx, caster, combat_profile.as_str(), &resolved)?
    } else {
        None
    };
    let gameplay = if let Some(gameplay) = gameplay_override {
        gameplay
    } else {
        match policy.authorization {
            MeleeAuthorization::SelectableLoadout => active_melee_gameplay_for_resolved_action(
                ctx,
                caster,
                combat_profile.as_str(),
                &resolved,
            )?,
            MeleeAuthorization::IntrinsicAutoAttack => {
                let mode_id =
                    resolved_auto_attack_mode_for_owner(ctx, caster, combat_profile.as_str());
                let auto_attack_row = auto_attack_gameplay_for_profile_mode_action(
                    ctx,
                    combat_profile.as_str(),
                    mode_id.as_str(),
                    &authored_action_id,
                )
                .ok_or_else(|| {
                    format!(
                        "auto-attack '{}' has no gameplay row for combat profile '{}' mode '{}'",
                        authored_action_id.as_str(),
                        combat_profile,
                        mode_id
                    )
                })?;
                auto_attack_melee_gameplay_from_catalog(auto_attack_row).ok_or_else(|| {
                    format!(
                        "auto-attack '{}' has invalid gameplay row for combat profile '{}'",
                        authored_action_id.as_str(),
                        combat_profile
                    )
                })?
            }
        }
    };
    if strike.hit_windows.is_empty() {
        return Err(format!(
            "Melee strike has no hit windows: {}:{}",
            combat_profile,
            authored_action_id.as_str()
        ));
    }
    let projectile_delivery = resolve_strike_projectile_data(ctx, &strike)?;

    let Some(caster_state) = ctx.db.player_state().player_id().find(caster) else {
        log::warn!(
            "[MELEE] {} {} — caster has no player_state",
            &caster.to_hex()[..8],
            authored_action_id.as_str()
        );
        return Ok(MeleeAttackDispatch::Rejected);
    };
    if !caster_state.alive {
        log::warn!(
            "[MELEE] {} {} — caster is dead",
            &caster.to_hex()[..8],
            authored_action_id.as_str()
        );
        return Ok(MeleeAttackDispatch::Rejected);
    }
    if has_active_disabling_status(ctx, caster, ctx.timestamp) {
        return Ok(MeleeAttackDispatch::Rejected);
    }

    let now = ctx.timestamp;
    log_melee_resource_resolution(
        caster,
        policy,
        &strike,
        runtime_action_id.as_str(),
        action_resource_cost.as_ref(),
    );
    if let Some(resolved_cost) = action_resource_cost.as_ref() {
        if !can_pay_action_resource_cost(ctx, caster, &resolved_cost.cost, now) {
            log::info!(
                "[MELEE_RESOURCE] owner={} source={} strike={} slot={} rejected_insufficient_resource cost={:.3}",
                short_identity(caster),
                policy.source_label(),
                strike.id,
                runtime_action_id.as_str(),
                resolved_cost.cost.amount
            );
            return Ok(MeleeAttackDispatch::Rejected);
        }
    }

    let hit_window_damages = resolved_hit_window_damages(&strike, gameplay.base_damage);
    let melee_modifiers = resolve_melee_attack_modifiers(ctx, caster, now);
    let (consumed_modifier_status_kind, consumed_modifier_stack_group) =
        consumed_melee_modifier_event_fields(&melee_modifiers);
    let effective_range = melee_modifiers.effective_range(gameplay.range);
    let gap_close = melee_gap_close_for_ability(ctx, gameplay.ability_id.as_deref());
    let applies_stagger = gameplay.applies_stagger || melee_modifiers.force_stagger;

    let combo_decision = if allow_queue {
        // Combo follow-ups can be queued immediately after the predecessor starts.
        // They release on their authored combo transition timing rather than waiting
        // for the current global cooldown to finish.
        let decision = combo_input_decision(&caster_state, combat_profile.as_str(), &strike, now);
        if matches!(decision, ComboInputDecision::Reject) {
            return Ok(MeleeAttackDispatch::Rejected);
        }
        if matches!(decision, ComboInputDecision::ExecuteNow)
            && gameplay.uses_global_cooldown
            && !is_combo_followup(&strike)
            && is_on_global_cooldown(ctx, caster, now)
        {
            return Ok(MeleeAttackDispatch::Rejected);
        }
        decision
    } else {
        // Once a combo follow-up was validly queued, release-time execution should
        // not be rejected for missing the original input window on a later tick.
        if !combo_release_allowed(&caster_state, combat_profile.as_str(), &strike) {
            return Ok(MeleeAttackDispatch::Rejected);
        }
        ComboInputDecision::ExecuteNow
    };

    if policy.uses_shared_cooldowns
        && is_on_named_cooldown(
            ctx,
            caster,
            cooldown_key_override
                .as_deref()
                .unwrap_or(runtime_action_id.as_str()),
            now,
        )
    {
        return Ok(MeleeAttackDispatch::Rejected);
    }

    let Some(caster_phys) = ctx.db.player_physics().identity().find(caster) else {
        log::warn!(
            "[MELEE] {} {} — caster has no player_physics",
            &caster.to_hex()[..8],
            authored_action_id.as_str()
        );
        return Ok(MeleeAttackDispatch::Rejected);
    };
    if !strike
        .aerial_execution_mode
        .allows_caster(caster_phys.grounded)
    {
        log::info!(
            "[MELEE] owner={} source={} strike={} rejected_aerial_execution required={} caster_grounded={}",
            short_identity(caster),
            policy.source_label(),
            strike.id,
            strike.aerial_execution_mode.as_str(),
            caster_phys.grounded
        );
        return Ok(MeleeAttackDispatch::Rejected);
    }

    let target_context = if gameplay.targeting.requires_target() {
        let Ok(target) = Identity::from_hex(target_id) else {
            log::warn!(
                "[MELEE] {} {} — bad target_id {:?}",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                target_id
            );
            return Ok(MeleeAttackDispatch::Rejected);
        };
        if target == caster {
            return Ok(MeleeAttackDispatch::Rejected);
        }
        let Some(target_snapshot) = player_snapshot_for(ctx, target) else {
            log::warn!(
                "[MELEE] {} {} — target {} has no combat snapshot",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                &target.to_hex()[..8]
            );
            return Ok(MeleeAttackDispatch::Rejected);
        };
        if !target_snapshot.alive {
            log::warn!(
                "[MELEE] {} {} — target {} is dead",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                &target.to_hex()[..8]
            );
            return Ok(MeleeAttackDispatch::Rejected);
        }
        if !players_share_world_context(ctx, caster, target) {
            log::warn!(
                "[MELEE] {} {} — world context mismatch with target {}",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                &target.to_hex()[..8]
            );
            return Ok(MeleeAttackDispatch::Rejected);
        }
        if !can_harm(ctx, caster, target) {
            log::warn!(
                "[MELEE] {} {} — target {} is not hostile relation={:?}",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                &target.to_hex()[..8],
                combat_relation(ctx, caster, target)
            );
            return Ok(MeleeAttackDispatch::Rejected);
        }
        if !gameplay
            .airborne_targeting_mode
            .allows_target(caster_phys.grounded, target_snapshot.grounded)
        {
            log::info!(
                "[MELEE] owner={} source={} strike={} rejected_airborne_targeting required={} caster_grounded={} target_grounded={}",
                short_identity(caster),
                policy.source_label(),
                strike.id,
                gameplay.airborne_targeting_mode.as_str(),
                caster_phys.grounded,
                target_snapshot.grounded
            );
            return Ok(MeleeAttackDispatch::Rejected);
        }

        let dx = target_snapshot.pos_x - caster_phys.pos_x;
        let dz = target_snapshot.pos_z - caster_phys.pos_z;
        if !is_direction_within_facing_arc(
            caster_phys.yaw,
            dx,
            dz,
            MELEE_TARGET_FACING_ARC_RADIANS,
            0.0,
        ) {
            log::info!(
                "[MELEE] owner={} source={} strike={} rejected_target_facing caster=({:.2},{:.2}) target=({:.2},{:.2}) yaw={:.2}",
                short_identity(caster),
                policy.source_label(),
                strike.id,
                caster_phys.pos_x,
                caster_phys.pos_z,
                target_snapshot.pos_x,
                target_snapshot.pos_z,
                caster_phys.yaw
            );
            return Ok(MeleeAttackDispatch::Rejected);
        }
        let horiz_dist = (dx * dx + dz * dz).sqrt();
        if horiz_dist > effective_range + target_snapshot.hit_radius {
            log::warn!(
                "[MELEE] {} {} — range check failed: dist={:.2} max={:.2} (range={:.2} radius={:.2}) gap_close={}",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                horiz_dist,
                effective_range + target_snapshot.hit_radius,
                effective_range,
                target_snapshot.hit_radius,
                gap_close.as_ref().map(|row| row.ability_id.as_str()).unwrap_or("")
            );
            return Ok(MeleeAttackDispatch::Rejected);
        }
        if gameplay.minimum_range > 0.0 {
            let minimum_allowed_distance = gameplay.minimum_range + target_snapshot.hit_radius;
            if horiz_dist < minimum_allowed_distance {
                log::info!(
                    "[MELEE] owner={} source={} strike={} rejected_minimum_range dist={:.2} min={:.2} minimum_range={:.2} target_radius={:.2}",
                    short_identity(caster),
                    policy.source_label(),
                    strike.id,
                    horiz_dist,
                    minimum_allowed_distance,
                    gameplay.minimum_range,
                    target_snapshot.hit_radius
                );
                return Ok(MeleeAttackDispatch::Rejected);
            }
        }

        Some((target, target_snapshot, dx, dz, horiz_dist))
    } else {
        None
    };

    if let Some(projectile) = projectile_delivery.as_ref() {
        let Some(target_context) = target_context.as_ref() else {
            log::info!(
                "[MELEE_PROJECTILE] owner={} source={} strike={} rejected_targetless_projectile",
                short_identity(caster),
                policy.source_label(),
                strike.id
            );
            return Ok(MeleeAttackDispatch::Rejected);
        };
        if projectile.requires_initial_line_of_sight {
            let Some(caster_snapshot) = player_snapshot_for(ctx, caster) else {
                return Ok(MeleeAttackDispatch::Rejected);
            };
            let Some(target_snapshot) = player_snapshot_for(ctx, target_context.0) else {
                return Ok(MeleeAttackDispatch::Rejected);
            };
            if !has_line_of_sight(ctx, &caster_snapshot, &target_snapshot) {
                log::info!(
                    "[MELEE_PROJECTILE] owner={} source={} strike={} rejected_los target={}",
                    short_identity(caster),
                    policy.source_label(),
                    strike.id,
                    short_identity(target_context.0)
                );
                return Ok(MeleeAttackDispatch::Rejected);
            }
        }
    }

    let auto_attack_target = target_context.as_ref().map(|context| context.0);

    if allow_queue {
        if let ComboInputDecision::QueueUntil(execute_not_before) = combo_decision {
            let Some(auto_attack_target) = auto_attack_target else {
                return Ok(MeleeAttackDispatch::Rejected);
            };
            arm_auto_attack_for_melee_input_if_needed(
                ctx,
                caster,
                policy,
                &strike,
                runtime_action_id.as_str(),
                Some(auto_attack_target),
                now,
            );
            upsert_queued_melee_followup(
                ctx,
                caster,
                &runtime_action_id,
                auto_attack_target,
                execute_not_before,
            );
            return Ok(MeleeAttackDispatch::Queued);
        }
    }

    let mut gap_close_failure: Option<GapCloseResolveFailure> = None;
    let resolved_gap_close = if let Some(gap_close) = gap_close.as_ref() {
        let Some((_, target_snapshot, _, _, _)) = target_context.as_ref() else {
            return Ok(MeleeAttackDispatch::Rejected);
        };
        match resolve_melee_gap_close(
            ctx,
            caster,
            gap_close,
            GapCloseActorSnapshot {
                pos_x: caster_phys.pos_x,
                pos_y: caster_phys.pos_y,
                pos_z: caster_phys.pos_z,
                yaw: caster_phys.yaw,
                hit_radius: caster_state.hit_radius,
                hit_height: caster_state.hit_height,
            },
            GapCloseActorSnapshot {
                pos_x: target_snapshot.pos_x,
                pos_y: target_snapshot.pos_y,
                pos_z: target_snapshot.pos_z,
                yaw: target_snapshot.facing_yaw,
                hit_radius: target_snapshot.hit_radius,
                hit_height: target_snapshot.hit_height,
            },
        ) {
            Ok(resolved) => Some(resolved),
            Err(failure) => {
                gap_close_failure = Some(failure);
                None
            }
        }
    } else {
        None
    };
    if gap_close_pre_commit_decision(gap_close.as_ref(), resolved_gap_close)
        == GapClosePreCommitDecision::RejectBeforeCommit
    {
        if let Some(gap_close) = gap_close.as_ref() {
            let failure_reason = gap_close_failure
                .as_ref()
                .map(|failure| failure.reason())
                .unwrap_or("unresolved");
            let failure_detail = gap_close_failure
                .as_ref()
                .map(|failure| failure.detail())
                .unwrap_or_default();
            log::info!(
                "[MELEE_GAP_CLOSE] owner={} source={} strike={} ability={} rejected reason={} {}",
                short_identity(caster),
                policy.source_label(),
                strike.id,
                gap_close.ability_id,
                failure_reason,
                failure_detail
            );
        }
        return Ok(MeleeAttackDispatch::Rejected);
    }

    if let Some(resolved_cost) = action_resource_cost.as_ref() {
        if !pay_action_resource_cost(ctx, caster, &resolved_cost.cost, now) {
            log::info!(
                "[MELEE_RESOURCE] owner={} source={} strike={} slot={} rejected_spend_failed cost={:.3}",
                short_identity(caster),
                policy.source_label(),
                strike.id,
                runtime_action_id.as_str(),
                resolved_cost.cost.amount
            );
            return Ok(MeleeAttackDispatch::Rejected);
        }
        log::info!(
            "[MELEE_RESOURCE] owner={} source={} strike={} slot={} spent cost={:.3}",
            short_identity(caster),
            policy.source_label(),
            strike.id,
            runtime_action_id.as_str(),
            resolved_cost.cost.amount
        );
    }

    clear_interruptible_defense_for_owner(ctx, caster);
    arm_auto_attack_for_melee_input_if_needed(
        ctx,
        caster,
        policy,
        &strike,
        runtime_action_id.as_str(),
        auto_attack_target,
        now,
    );

    let spell_id = format!(
        "melee:{}:{}:{:?}",
        policy.source_label(),
        caster.to_hex(),
        now
    );
    let timed_movement = gameplay.timed_movement.clone();
    let (target, target_point_x, target_point_y, target_point_z, dx, dz, horiz_dist) =
        if let Some((target, target_snapshot, dx, dz, horiz_dist)) =
            target_context.as_ref()
        {
            mark_harmful_combat_action(ctx, caster, *target, now, strike.id.as_str());
            (
                *target,
                target_snapshot.pos_x,
                target_snapshot.pos_y,
                target_snapshot.pos_z,
                *dx,
                *dz,
                *horiz_dist,
            )
        } else {
            (
                Identity::ZERO,
                caster_phys.pos_x,
                caster_phys.pos_y,
                caster_phys.pos_z,
                caster_phys.yaw.sin(),
                caster_phys.yaw.cos(),
                1.0,
            )
        };
    let (dir_x, dir_z) = if horiz_dist > 0.001 {
        (dx / horiz_dist, dz / horiz_dist)
    } else {
        (0.0, 0.0)
    };

    if let Some(gap_close) = resolved_gap_close {
        let movement_start =
            SpellVec3::new(caster_phys.pos_x, caster_phys.pos_y, caster_phys.pos_z);
        if gap_close_has_horizontal_travel(movement_start, gap_close.end) {
            begin_special_movement(
                ctx,
                caster,
                &format!(
                    "{}:{}",
                    MELEE_GAP_CLOSE_KIND_PREFIX,
                    runtime_action_id.as_str()
                ),
                now,
                gap_close.duration_ms,
                movement_start,
                gap_close.end,
                caster_phys.yaw,
                SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK,
            );
        }
    }

    if let Some(movement) = timed_movement.as_ref() {
        schedule_melee_timed_movement(
            ctx,
            caster,
            spell_id.as_str(),
            strike.id.as_str(),
            movement,
            caster_phys.yaw,
            now,
        );
    }

    if let Some(ability_id) = gameplay.ability_id.as_deref() {
        grant_primary_resource_amount(
            ctx,
            caster,
            primary_resource_gain_on_action_accept(ability_id),
            now,
        );
    }

    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: spell_id.clone(),
        action_kind: strike.id.clone(),
        ability_id: gameplay.ability_id.clone().unwrap_or_default(),
        hit_index: -1,
        event_type: EVENT_CAST.to_string(),
        source_kind: policy.source_label().to_string(),
        caster,
        hit: caster,
        origin_x: caster_phys.pos_x,
        origin_y: caster_phys.pos_y,
        origin_z: caster_phys.pos_z,
        dir_x,
        dir_y: 0.0,
        dir_z,
        speed: 0.0,
        max_distance: effective_range,
        scalar_kind: COMBAT_SCALAR_MELEE_RELEASE_DELAY_SECONDS.to_string(),
        scalar_value: strike
            .hit_windows
            .last()
            .map(|hit_window| hit_window.impact_delay_ms as f32 / 1000.0)
            .unwrap_or(0.0),
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: target_point_x,
        point_y: target_point_y,
        point_z: target_point_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: if consumed_modifier_status_kind.is_empty()
            || consumed_modifier_stack_group.is_empty()
        {
            COMBAT_METADATA_NONE
        } else {
            COMBAT_METADATA_CONSUMED_MELEE_MODIFIER
        }
        .to_string(),
        metadata_key: consumed_modifier_status_kind.to_string(),
        metadata_value: consumed_modifier_stack_group.to_string(),
    });

    for (hit_index, hit_window) in strike.hit_windows.iter().enumerate() {
        let impact_at =
            scheduled_melee_impact_at(now, hit_window.impact_delay_ms, resolved_gap_close);
        let active_until = impact_at;
        let recovery_until = active_until + Duration::from_millis(strike.recovery_ms);
        let Some(damage) = hit_window_damages.get(hit_index).copied() else {
            return Err(format!(
                "melee strike '{}' missing resolved damage for hit window {}",
                strike.id, hit_index
            ));
        };
        if let Some(projectile) = projectile_delivery.as_ref() {
            let projectile_max_distance = projectile_max_distance_for_policy(
                projectile.max_distance,
                effective_range,
                policy.authorization,
            );
            ctx.db
                .pending_projectile_release()
                .insert(PendingProjectileRelease {
                    release_id: 0,
                    source: caster,
                    event_source: policy.source_label().to_string(),
                    target,
                    action_instance_id: spell_id.clone(),
                    action_kind: strike.id.clone(),
                    ability_id: gameplay.ability_id.clone().unwrap_or_default(),
                    projectile_id: projectile.projectile_id.clone(),
                    hit_index: hit_index as u32,
                    damage,
                    speed: projectile.speed,
                    max_distance: projectile_max_distance,
                    radius: projectile.radius,
                    spawn_forward: projectile.spawn_forward,
                    spawn_height: projectile.spawn_height,
                    aim_height_scale: projectile.aim_height_scale,
                    update_interval_seconds: projectile.update_interval_seconds,
                    parry_behavior: gameplay.parry_behavior.as_str().to_string(),
                    block_behavior: gameplay.block_behavior.as_str().to_string(),
                    grants_primary_resource_on_hit: policy.grants_primary_resource_on_hit,
                    release_at: impact_at,
                    recovery_until,
                    release_at_micros: timestamp_to_micros(impact_at),
                });
            log::info!(
                "[MELEE_PROJECTILE] owner={} source={} strike={} target={} scheduled_release hit_index={} projectile={} damage={} release_at_micros={}",
                short_identity(caster),
                policy.source_label(),
                strike.id,
                short_identity(target),
                hit_index,
                projectile.projectile_id,
                damage,
                impact_at.to_micros_since_unix_epoch()
            );
            continue;
        }
        let impact_area = gameplay
            .impact_area
            .filter(|area| area.applies_to_hit_index(hit_index as u32));
        let target_impact_range =
            pending_melee_impact_range(effective_range, gap_close.as_ref(), resolved_gap_close);
        let impact_range = gameplay.targeting.pending_range(target_impact_range);
        ctx.db.pending_melee_impact().insert(PendingMeleeImpact {
            impact_id: 0,
            source: caster,
            event_source: policy.source_label().to_string(),
            target,
            spell_id: spell_id.clone(),
            kind: strike.id.clone(),
            ability_id: gameplay.ability_id.clone().unwrap_or_default(),
            hit_index: hit_index as u32,
            damage,
            range: impact_range,
            impact_at,
            active_until,
            recovery_until,
            parry_behavior: gameplay.parry_behavior.as_str().to_string(),
            block_behavior: gameplay.block_behavior.as_str().to_string(),
            airborne_targeting_mode: gameplay.airborne_targeting_mode.as_str().to_string(),
            targeting_kind: gameplay.targeting.pending_kind().to_string(),
            targeting_radius: gameplay.targeting.pending_radius(),
            targeting_angle_degrees: gameplay.targeting.pending_angle_degrees(),
            applies_stagger,
            grants_primary_resource_on_hit: policy.grants_primary_resource_on_hit,
            impact_area_radius: impact_area.map(|area| area.radius).unwrap_or(0.0),
            impact_area_damage: impact_area
                .map(|area| scaled_impact_area_damage(damage, area.damage_multiplier))
                .unwrap_or(0),
            impact_area_include_primary_target: impact_area
                .map(|area| area.include_primary_target)
                .unwrap_or(false),
            resolve_at_micros: timestamp_to_micros(impact_at),
        });
        log::info!(
            "[MELEE] owner={} source={} strike={} target={} scheduled_impact hit_index={} damage={} impact_at_micros={}",
            short_identity(caster),
            policy.source_label(),
            strike.id,
            short_identity(target),
            hit_index,
            damage,
            impact_at.to_micros_since_unix_epoch()
        );
    }

    consume_melee_attack_modifiers(ctx, caster, &melee_modifiers);
    finalize_melee_cast(
        ctx,
        caster,
        &strike,
        &runtime_action_id,
        cooldown_key_override.as_deref(),
        &gameplay,
        now,
        policy,
        caster_state,
    );
    if let Some(token) = prediction_token.as_ref() {
        record_predicted_action_result(
            ctx,
            caster,
            PredictedActionFamily::Melee,
            token,
            spell_id.as_str(),
            ActionResultKind::Accepted,
            now,
        );
    }
    Ok(MeleeAttackDispatch::Started)
}

fn arm_auto_attack_for_melee_input_if_needed(
    ctx: &ReducerContext,
    caster: Identity,
    policy: MeleeExecutionPolicy,
    strike: &StrikeData,
    slot_id: &str,
    target: Option<Identity>,
    now: Timestamp,
) {
    if !policy.schedules_auto_attack_on_started_swing {
        return;
    }

    if let Some(target) = target {
        log::info!(
            "[MELEE] owner={} source={} strike={} slot={} started target={} scheduling_auto_attack=true",
            short_identity(caster),
            policy.source_label(),
            strike.id,
            slot_id,
            short_identity(target)
        );
        arm_auto_attack_if_unarmed_with_cadence(ctx, caster, target, now);
    } else {
        log::info!(
            "[MELEE] owner={} source={} strike={} slot={} started scheduling_auto_attack_skipped=no_target",
            short_identity(caster),
            policy.source_label(),
            strike.id,
            slot_id
        );
    }
}

#[reducer]
pub fn melee_attack(
    ctx: &ReducerContext,
    strike_id: String,
    target_id: String,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
    predicted_action_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    let Some(token) = ActionPredictionToken::new(predicted_action_id, client_action_seq) else {
        return perform_unpredicted_melee_attack_for(
            ctx,
            ctx.sender(),
            strike_id.as_str(),
            target_id.as_str(),
            cast_pos_x,
            cast_pos_y,
            cast_pos_z,
            cast_yaw,
        )
        .map(|_| ());
    };
    if has_predicted_action_result(ctx, ctx.sender(), PredictedActionFamily::Melee, &token) {
        return Ok(());
    }

    match perform_predicted_melee_attack_for(
        ctx,
        ctx.sender(),
        strike_id.as_str(),
        target_id.as_str(),
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        token.clone(),
    ) {
        Ok(MeleeAttackDispatch::Rejected) | Err(_) => {
            record_predicted_action_result(
                ctx,
                ctx.sender(),
                PredictedActionFamily::Melee,
                &token,
                "",
                ActionResultKind::Rejected,
                ctx.timestamp,
            );
            Ok(())
        }
        Ok(MeleeAttackDispatch::Queued) => Ok(()),
        Ok(MeleeAttackDispatch::Started) => Ok(()),
    }
}

pub(crate) fn resolve_pending_melee_impacts(ctx: &ReducerContext, now: Timestamp) {
    let now_micros = timestamp_to_micros(now);
    let mut due: Vec<PendingMeleeImpact> = ctx
        .db
        .pending_melee_impact()
        .resolve_at_micros()
        .filter(..=now_micros)
        .collect();
    due.sort_by_key(|row| (row.resolve_at_micros, row.hit_index));

    for row in due {
        if ctx
            .db
            .pending_melee_impact()
            .impact_id()
            .find(row.impact_id)
            .is_none()
        {
            continue;
        }

        resolve_pending_melee_impact(ctx, &row, now);
        if ctx
            .db
            .pending_melee_impact()
            .impact_id()
            .find(row.impact_id)
            .is_some()
        {
            ctx.db
                .pending_melee_impact()
                .impact_id()
                .delete(row.impact_id);
        }
        if has_due_pending_effects(ctx, now) {
            resolve_pending_effects(ctx, now);
        }
    }
}

pub(crate) fn tick_pending_melee_timed_movements(ctx: &ReducerContext, now: Timestamp) {
    let now_micros = timestamp_to_micros(now);
    let mut due: Vec<PendingMeleeTimedMovement> = ctx
        .db
        .pending_melee_timed_movement()
        .start_at_micros()
        .filter(..=now_micros)
        .collect();
    due.sort_by_key(|row| (row.start_at_micros, row.movement_id));

    for row in due {
        if ctx
            .db
            .pending_melee_timed_movement()
            .movement_id()
            .find(row.movement_id)
            .is_none()
        {
            continue;
        }

        start_melee_timed_movement(ctx, &row, now);
        ctx.db
            .pending_melee_timed_movement()
            .movement_id()
            .delete(row.movement_id);
    }
}

fn start_melee_timed_movement(
    ctx: &ReducerContext,
    row: &PendingMeleeTimedMovement,
    now: Timestamp,
) {
    if row.kind.as_str() != "BACKSTEP"
        || row.direction.as_str() != "BACKWARD"
        || row.collision_policy.as_str() != SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK
        || row.facing_policy.as_str() != SPECIAL_MOVEMENT_FACING_FACE_START
        || !row.distance.is_finite()
        || row.distance <= 0.0
        || !row.speed.is_finite()
        || row.speed <= 0.0
    {
        return;
    }

    let Some(state) = ctx.db.player_state().player_id().find(row.owner) else {
        return;
    };
    if !state.alive {
        return;
    }
    let Some(physics) = ctx.db.player_physics().identity().find(row.owner) else {
        return;
    };

    let start = SpellVec3::new(physics.pos_x, physics.pos_y, physics.pos_z);
    let intended_end = timed_melee_movement_destination(start, row.yaw_start, row.distance);
    let baked = bake_linear_special_movement(
        ctx,
        row.owner,
        start,
        intended_end,
        state.hit_radius,
        state.hit_height,
        row.collision_policy.as_str(),
    );
    if !gap_close_has_horizontal_travel(start, baked.end) {
        return;
    }

    let duration_ms =
        horizontal_movement_duration_ms(start.x, start.z, baked.end.x, baked.end.z, row.speed, 1);
    begin_special_movement_with_facing_policy(
        ctx,
        row.owner,
        &format!("MELEE_TIMED_MOVEMENT:{}", row.action_kind),
        now,
        duration_ms,
        start,
        baked.end,
        row.yaw_start,
        row.facing_policy.as_str(),
        row.collision_policy.as_str(),
    );
}

pub(crate) fn resolve_pending_projectile_releases(ctx: &ReducerContext, now: Timestamp) {
    let now_micros = timestamp_to_micros(now);
    let mut due: Vec<PendingProjectileRelease> = ctx
        .db
        .pending_projectile_release()
        .release_at_micros()
        .filter(..=now_micros)
        .collect();
    due.sort_by_key(|row| (row.release_at_micros, row.hit_index));

    for row in due {
        if ctx
            .db
            .pending_projectile_release()
            .release_id()
            .find(row.release_id)
            .is_none()
        {
            continue;
        }

        resolve_pending_projectile_release(ctx, &row, now);
        if ctx
            .db
            .pending_projectile_release()
            .release_id()
            .find(row.release_id)
            .is_some()
        {
            ctx.db
                .pending_projectile_release()
                .release_id()
                .delete(row.release_id);
        }
    }
}

pub(crate) fn tick_queued_melee_followups(ctx: &ReducerContext, now: Timestamp) {
    let due_rows: Vec<QueuedMeleeFollowup> = ctx
        .db
        .queued_melee_followup()
        .execute_at_micros()
        .filter(..=timestamp_to_micros(now))
        .collect();

    for row in due_rows {
        if ctx
            .db
            .queued_melee_followup()
            .caster()
            .find(row.caster)
            .is_none()
        {
            continue;
        }

        let combat_profile = combat_profile_for_identity(ctx, row.caster);
        let Some(resolved) =
            resolve_melee_action_reference(combat_profile.as_str(), row.strike_id.as_str())
        else {
            clear_queued_melee_followup(ctx, row.caster);
            continue;
        };
        let target_id = row.target.to_hex();
        let _ = perform_melee_attack_for_internal(
            ctx,
            row.caster,
            combat_profile,
            resolved,
            target_id.as_str(),
            0.0,
            0.0,
            0.0,
            0.0,
            false,
            MeleeExecutionPolicy::QUEUED_FOLLOWUP,
            None,
            None,
            None,
            None,
        );
        clear_queued_melee_followup(ctx, row.caster);
    }
}

pub(crate) fn has_due_pending_melee_impacts(ctx: &ReducerContext, now: Timestamp) -> bool {
    ctx.db
        .pending_melee_impact()
        .resolve_at_micros()
        .filter(..=timestamp_to_micros(now))
        .next()
        .is_some()
}

pub(crate) fn has_due_pending_projectile_releases(ctx: &ReducerContext, now: Timestamp) -> bool {
    ctx.db
        .pending_projectile_release()
        .release_at_micros()
        .filter(..=timestamp_to_micros(now))
        .next()
        .is_some()
}

fn resolve_pending_projectile_release(
    ctx: &ReducerContext,
    row: &PendingProjectileRelease,
    now: Timestamp,
) {
    if !row.speed.is_finite()
        || row.speed <= 0.0
        || !row.max_distance.is_finite()
        || row.max_distance <= 0.0
        || !row.radius.is_finite()
        || row.radius <= 0.0
    {
        return;
    }

    let Some(caster) = player_snapshot_for(ctx, row.source) else {
        return;
    };
    if !caster.alive || has_active_disabling_status(ctx, row.source, now) {
        emit_projectile_release_fizzle(ctx, row, &caster, now);
        return;
    }

    let Some(target) = player_snapshot_for(ctx, row.target) else {
        emit_projectile_release_fizzle(ctx, row, &caster, now);
        return;
    };
    if !target.alive || !players_share_world_context(ctx, row.source, row.target) {
        emit_projectile_release_fizzle(ctx, row, &caster, now);
        return;
    }

    let base_x = caster.pos_x;
    let base_y = caster.pos_y + row.spawn_height;
    let base_z = caster.pos_z;
    let target_y = target.pos_y + target.hit_height * row.aim_height_scale.clamp(0.0, 1.0);
    let desired = normalize_vec3(
        target.pos_x - base_x,
        target_y - base_y,
        target.pos_z - base_z,
    )
    .unwrap_or_else(|| (caster.facing_yaw.sin(), 0.0, caster.facing_yaw.cos()));
    let origin_x = base_x + desired.0 * row.spawn_forward;
    let origin_y = base_y + desired.1 * row.spawn_forward;
    let origin_z = base_z + desired.2 * row.spawn_forward;
    let lifetime = row.max_distance / row.speed;
    let projectile_instance_id = format!("{}:projectile:{}", row.action_instance_id, row.hit_index);

    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: row.action_instance_id.clone(),
        action_kind: row.action_kind.clone(),
        ability_id: row.ability_id.clone(),
        hit_index: row.hit_index as i32,
        event_type: EVENT_CAST.to_string(),
        source_kind: row.event_source.clone(),
        caster: row.source,
        hit: row.target,
        origin_x,
        origin_y,
        origin_z,
        dir_x: desired.0,
        dir_y: desired.1,
        dir_z: desired.2,
        speed: row.speed,
        max_distance: row.max_distance,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 1,
        point_x: origin_x,
        point_y: origin_y,
        point_z: origin_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });

    ctx.db
        .projectile_presentation_event()
        .insert(ProjectilePresentationEvent {
            event_id: 0,
            action_instance_id: row.action_instance_id.clone(),
            action_kind: row.action_kind.clone(),
            ability_id: row.ability_id.clone(),
            source_kind: row.event_source.clone(),
            projectile_id: row.projectile_id.clone(),
            projectile_instance_id: projectile_instance_id.clone(),
            hit_index: row.hit_index as i32,
            event_type: COMBAT_EVENT_RELEASE.to_string(),
            caster: row.source,
            hit: row.target,
            intended_target: row.target,
            origin_x,
            origin_y,
            origin_z,
            dir_x: desired.0,
            dir_y: desired.1,
            dir_z: desired.2,
            point_x: origin_x,
            point_y: origin_y,
            point_z: origin_z,
            speed: row.speed,
            max_distance: row.max_distance,
            radius: row.radius,
            motion_kind: "LINEAR".to_string(),
            update_interval_seconds: row.update_interval_seconds,
            orbit_initial_yaw: 0.0,
            orbit_radius: 0.0,
            orbit_height: 0.0,
            orbit_angular_speed_deg_per_sec: 0.0,
            orbit_phase_offset_deg: 0.0,
            boomerang_returning: false,
            boomerang_outbound_distance: 0.0,
            boomerang_return_speed: 0.0,
            sequence_index: 0,
            sequence_count: 1,
            terminal: false,
            created_at: now,
            created_at_micros: timestamp_to_micros(now),
        });

    ctx.db
        .active_combat_projectile()
        .insert(ActiveCombatProjectile {
            projectile_instance_id,
            action_instance_id: row.action_instance_id.clone(),
            projectile_sequence_index: row.hit_index,
            projectile_id: row.projectile_id.clone(),
            source_kind: row.event_source.clone(),
            action_kind: row.action_kind.clone(),
            ability_id: row.ability_id.clone(),
            motion_kind: "LINEAR".to_string(),
            caster: row.source,
            intended_target: row.target,
            origin_x,
            origin_y,
            origin_z,
            pos_x: origin_x,
            pos_y: origin_y,
            pos_z: origin_z,
            dir_x: desired.0,
            dir_y: desired.1,
            dir_z: desired.2,
            speed: row.speed,
            max_distance: row.max_distance,
            radius: row.radius,
            orbit_initial_yaw: 0.0,
            orbit_radius: 0.0,
            orbit_height: 0.0,
            orbit_angular_speed_deg_per_sec: 0.0,
            orbit_phase_offset_deg: 0.0,
            orbit_hit_cooldown_seconds: 0.0,
            orbit_max_hits_per_target: 0,
            boomerang_returning: false,
            boomerang_outbound_distance: 0.0,
            boomerang_return_speed: 0.0,
            boomerang_hit_cooldown_seconds: 0.0,
            boomerang_max_hits_per_target: 0,
            traveled: 0.0,
            age: 0.0,
            lifetime,
            update_accum: 0.0,
            update_interval_seconds: row.update_interval_seconds,
            damage: row.damage,
            parry_behavior: row.parry_behavior.clone(),
            block_behavior: row.block_behavior.clone(),
            grants_primary_resource_on_hit: row.grants_primary_resource_on_hit,
            hit_index: row.hit_index,
            created_at: now,
        });
}

fn emit_projectile_release_fizzle(
    ctx: &ReducerContext,
    row: &PendingProjectileRelease,
    caster: &crate::combat::player_snapshot::PlayerSnapshot,
    now: Timestamp,
) {
    let projectile_instance_id = format!("{}:projectile:{}", row.action_instance_id, row.hit_index);
    let origin_x = caster.pos_x;
    let origin_y = caster.pos_y + row.spawn_height;
    let origin_z = caster.pos_z;
    let dir_x = caster.facing_yaw.sin();
    let dir_z = caster.facing_yaw.cos();

    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: row.action_instance_id.clone(),
        action_kind: row.action_kind.clone(),
        ability_id: row.ability_id.clone(),
        hit_index: row.hit_index as i32,
        event_type: EVENT_FIZZLE.to_string(),
        source_kind: row.event_source.clone(),
        caster: row.source,
        hit: Identity::ZERO,
        origin_x,
        origin_y,
        origin_z,
        dir_x,
        dir_y: 0.0,
        dir_z,
        speed: row.speed,
        max_distance: row.max_distance,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 1,
        point_x: origin_x,
        point_y: origin_y,
        point_z: origin_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });

    ctx.db
        .projectile_presentation_event()
        .insert(ProjectilePresentationEvent {
            event_id: 0,
            action_instance_id: row.action_instance_id.clone(),
            action_kind: row.action_kind.clone(),
            ability_id: row.ability_id.clone(),
            source_kind: row.event_source.clone(),
            projectile_id: row.projectile_id.clone(),
            projectile_instance_id,
            hit_index: row.hit_index as i32,
            event_type: EVENT_FIZZLE.to_string(),
            caster: row.source,
            hit: Identity::ZERO,
            intended_target: row.target,
            origin_x,
            origin_y,
            origin_z,
            dir_x,
            dir_y: 0.0,
            dir_z,
            point_x: origin_x,
            point_y: origin_y,
            point_z: origin_z,
            speed: row.speed,
            max_distance: row.max_distance,
            radius: row.radius,
            motion_kind: "LINEAR".to_string(),
            update_interval_seconds: row.update_interval_seconds,
            orbit_initial_yaw: 0.0,
            orbit_radius: 0.0,
            orbit_height: 0.0,
            orbit_angular_speed_deg_per_sec: 0.0,
            orbit_phase_offset_deg: 0.0,
            boomerang_returning: false,
            boomerang_outbound_distance: 0.0,
            boomerang_return_speed: 0.0,
            sequence_index: 0,
            sequence_count: 1,
            terminal: true,
            created_at: now,
            created_at_micros: timestamp_to_micros(now),
        });
}

fn normalize_vec3(x: f32, y: f32, z: f32) -> Option<(f32, f32, f32)> {
    let len_sq = x * x + y * y + z * z;
    if len_sq <= 0.000001 {
        return None;
    }
    let inv_len = 1.0 / len_sq.sqrt();
    Some((x * inv_len, y * inv_len, z * inv_len))
}

fn resolve_pending_melee_impact(ctx: &ReducerContext, row: &PendingMeleeImpact, now: Timestamp) {
    let player_snapshots = PlayerSnapshotSet::collect(ctx);
    if row.target == Identity::ZERO {
        resolve_pending_melee_hit_volume(ctx, row, now, &player_snapshots);
        return;
    }

    resolve_pending_melee_target_impact(ctx, row, now, &player_snapshots);
}

fn resolve_pending_melee_hit_volume(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    player_snapshots: &PlayerSnapshotSet,
) {
    let Some(source_state) = ctx.db.player_state().player_id().find(row.source) else {
        return;
    };
    if !source_state.alive {
        return;
    }
    let Some(caster_phys) = ctx.db.player_physics().identity().find(row.source) else {
        return;
    };
    let Some(airborne_targeting_mode) =
        AirborneTargetingMode::from_wire(row.airborne_targeting_mode.as_str())
    else {
        return;
    };

    let players = player_snapshots.as_slice();
    let mut candidate_indices = Vec::new();
    player_snapshots.query_disc_indices(
        caster_phys.pos_x,
        caster_phys.pos_z,
        row.range,
        &mut candidate_indices,
    );

    // AREA_IMPACT is the area action/VFX signal and intentionally fires even when no targets pass
    // the later per-target filters; CONTACT/damage events remain the "hit landed" signal.
    emit_melee_area_impact_event(ctx, row, now, &caster_phys);

    for player in candidate_indices
        .iter()
        .filter_map(|index| players.get(*index))
    {
        if !player.alive || player.player_id == row.source {
            continue;
        }
        if !players_share_world_context(ctx, row.source, player.player_id) {
            continue;
        }
        if !can_harm(ctx, row.source, player.player_id) {
            continue;
        }
        if !airborne_targeting_mode.allows_target(caster_phys.grounded, player.grounded) {
            continue;
        }
        if !melee_hit_volume_contains_player(row, &caster_phys, &player) {
            continue;
        }

        let mut target_row = row.clone();
        target_row.target = player.player_id;
        resolve_pending_melee_target_impact(ctx, &target_row, now, player_snapshots);
    }
}

fn emit_melee_area_impact_event(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    caster_phys: &crate::player_physics::PlayerPhysics,
) {
    let dir_x = caster_phys.yaw.sin();
    let dir_z = caster_phys.yaw.cos();
    let origin_y = terrain_surface_y_for_caster(
        ctx,
        row.source,
        caster_phys.pos_x,
        caster_phys.pos_z,
        caster_phys.pos_y,
    );

    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: row.spell_id.clone(),
        action_kind: row.kind.clone(),
        ability_id: row.ability_id.clone(),
        hit_index: row.hit_index as i32,
        event_type: EVENT_AREA_IMPACT.to_string(),
        source_kind: row.event_source.clone(),
        caster: row.source,
        hit: Identity::ZERO,
        origin_x: caster_phys.pos_x,
        origin_y,
        origin_z: caster_phys.pos_z,
        dir_x,
        dir_y: 0.0,
        dir_z,
        speed: 0.0,
        max_distance: row.range,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 1,
        point_x: caster_phys.pos_x,
        point_y: origin_y,
        point_z: caster_phys.pos_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

fn melee_hit_volume_contains_player(
    row: &PendingMeleeImpact,
    caster_phys: &crate::player_physics::PlayerPhysics,
    player: &crate::combat::player_snapshot::PlayerSnapshot,
) -> bool {
    match row.targeting_kind.trim().to_ascii_uppercase().as_str() {
        "CASTER_RADIUS" => CombatAreaShape::Disc { radius: row.range }.contains_player_xz(
            caster_phys.pos_x,
            caster_phys.pos_z,
            caster_phys.yaw,
            player,
            0.0,
        ),
        "CASTER_CONE" => CombatAreaShape::Cone {
            range: row.range,
            angle_degrees: row.targeting_angle_degrees,
            vertical_tolerance: None,
        }
        .contains_player(
            caster_phys.pos_x,
            caster_phys.pos_y,
            caster_phys.pos_z,
            caster_phys.yaw,
            player,
            0.0,
        ),
        _ => false,
    }
}

fn resolve_pending_melee_target_impact(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    player_snapshots: &PlayerSnapshotSet,
) {
    let Some(source_state) = ctx.db.player_state().player_id().find(row.source) else {
        return;
    };
    if !source_state.alive {
        return;
    }

    let Some(target_snapshot) = player_snapshot_for(ctx, row.target) else {
        return;
    };
    if !target_snapshot.alive {
        return;
    }

    if !players_share_world_context(ctx, row.source, row.target) {
        return;
    }
    if !can_harm(ctx, row.source, row.target) {
        return;
    }

    let Some(caster_phys) = ctx.db.player_physics().identity().find(row.source) else {
        return;
    };
    let dx = target_snapshot.pos_x - caster_phys.pos_x;
    let dz = target_snapshot.pos_z - caster_phys.pos_z;
    let horiz_dist = (dx * dx + dz * dz).sqrt();
    let (dir_x, dir_z) = if horiz_dist > 0.001 {
        (dx / horiz_dist, dz / horiz_dist)
    } else {
        (0.0, 0.0)
    };
    if !target_within_area_range_xz(
        caster_phys.pos_x,
        caster_phys.pos_z,
        target_snapshot.pos_x,
        target_snapshot.pos_z,
        target_snapshot.hit_radius,
        row.range,
    ) {
        return;
    }

    match resolve_defensible_combat_hit(
        ctx,
        DefensibleCombatHit {
            delivery_kind: CombatHitDeliveryKind::Melee,
            defender: row.target,
            active_from: row.impact_at,
            active_until: row.active_until,
            parry_behavior: row.parry_behavior.as_str(),
            block_behavior: row.block_behavior.as_str(),
            source_x: caster_phys.pos_x,
            source_y: caster_phys.pos_y,
            source_z: caster_phys.pos_z,
            impact_x: target_snapshot.pos_x,
            impact_y: target_snapshot.pos_y + target_snapshot.hit_height * 0.5,
            impact_z: target_snapshot.pos_z,
            dir_x,
            dir_y: 0.0,
            dir_z,
            speed: 0.0,
        },
    ) {
        DefenseResolution::Parried => {
            mark_harmful_combat_action(ctx, row.source, row.target, now, row.kind.as_str());
            log::info!(
                "[MELEE_IMPACT] owner={} source={} strike={} target={} result=parried damage={} spell_id={}",
                short_identity(row.source),
                row.event_source,
                row.kind,
                short_identity(row.target),
                row.damage,
                row.spell_id
            );
            emit_parry_event(
                ctx,
                row,
                now,
                &caster_phys,
                target_snapshot.pos_x,
                target_snapshot.pos_y,
                target_snapshot.pos_z,
                dx,
                dz,
                horiz_dist,
            );
            cancel_remaining_pending_melee_impacts(ctx, row);
            return;
        }
        DefenseResolution::Blocked => {
            mark_harmful_combat_action(ctx, row.source, row.target, now, row.kind.as_str());
            log::info!(
                "[MELEE_IMPACT] owner={} source={} strike={} target={} result=blocked damage={} spell_id={}",
                short_identity(row.source),
                row.event_source,
                row.kind,
                short_identity(row.target),
                row.damage,
                row.spell_id
            );
            emit_block_event(
                ctx,
                row,
                now,
                &caster_phys,
                target_snapshot.pos_x,
                target_snapshot.pos_y,
                target_snapshot.pos_z,
                dx,
                dz,
                horiz_dist,
            );
            return;
        }
        DefenseResolution::None => {}
    }

    log::info!(
        "[MELEE_IMPACT] owner={} source={} strike={} target={} result=hit damage={} spell_id={}",
        short_identity(row.source),
        row.event_source,
        row.kind,
        short_identity(row.target),
        row.damage,
        row.spell_id
    );

    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: row.spell_id.clone(),
        action_kind: row.kind.clone(),
        ability_id: row.ability_id.clone(),
        hit_index: row.hit_index as i32,
        event_type: EVENT_IMPACT.to_string(),
        source_kind: row.event_source.clone(),
        caster: row.source,
        hit: row.target,
        origin_x: caster_phys.pos_x,
        origin_y: caster_phys.pos_y,
        origin_z: caster_phys.pos_z,
        dir_x,
        dir_y: 0.0,
        dir_z,
        speed: 0.0,
        max_distance: 0.0,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: target_snapshot.pos_x,
        point_y: target_snapshot.pos_y,
        point_z: target_snapshot.pos_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: row.damage,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });

    let mut effects = vec![EffectPacket::Damage {
        amount: row.damage,
        source: row.source,
        target: row.target,
        spell_id: format!("{}:damage:{}", row.spell_id, row.hit_index),
        delivery: DamageDelivery::Direct,
        direct_action_key: format!("{}:hit:{}", row.spell_id, row.hit_index),
    }];
    push_stagger_effect_if_applicable(
        ctx,
        &mut effects,
        row.source,
        row.target,
        format!("{}:stagger:{}", row.spell_id, row.hit_index),
        row.damage,
        row.applies_stagger,
    );
    push_melee_impact_status_effects(&mut effects, row);
    push_melee_impact_area_effects(
        ctx,
        &mut effects,
        row,
        player_snapshots,
        target_snapshot.pos_x,
        target_snapshot.pos_y,
        target_snapshot.pos_z,
    );
    queue_effects(ctx, effects);
    grant_primary_resource_for_melee_event_hit(
        ctx,
        row.source,
        row.grants_primary_resource_on_hit,
        now,
    );
}

fn push_melee_impact_area_effects(
    ctx: &ReducerContext,
    effects: &mut Vec<EffectPacket>,
    row: &PendingMeleeImpact,
    player_snapshots: &PlayerSnapshotSet,
    impact_x: f32,
    impact_y: f32,
    impact_z: f32,
) {
    if row.impact_area_radius <= 0.0 || row.impact_area_damage <= 0 {
        return;
    }

    let players = player_snapshots.as_slice();
    let mut candidate_indices = Vec::new();
    player_snapshots.query_disc_indices(
        impact_x,
        impact_z,
        row.impact_area_radius,
        &mut candidate_indices,
    );

    for player in candidate_indices
        .iter()
        .filter_map(|index| players.get(*index))
    {
        if !player.alive || player.player_id == row.source {
            continue;
        }
        if !row.impact_area_include_primary_target && player.player_id == row.target {
            continue;
        }
        if !players_share_world_context(ctx, row.source, player.player_id) {
            continue;
        }
        if !can_harm(ctx, row.source, player.player_id) {
            continue;
        }
        if !aoe_hits_player(
            impact_x,
            impact_y,
            impact_z,
            row.impact_area_radius,
            &player,
        ) {
            continue;
        }

        effects.push(EffectPacket::Damage {
            amount: row.impact_area_damage,
            source: row.source,
            target: player.player_id,
            spell_id: format!("{}:area:{}", row.spell_id, row.hit_index),
            delivery: DamageDelivery::Direct,
            direct_action_key: format!(
                "{}:area:{}:{}",
                row.spell_id,
                row.hit_index,
                short_identity(player.player_id)
            ),
        });
    }
}

fn push_melee_impact_status_effects(effects: &mut Vec<EffectPacket>, row: &PendingMeleeImpact) {
    if row.ability_id.trim().is_empty() {
        return;
    }

    for effect in melee_impact_effects_for_ability_id(row.ability_id.as_str()) {
        if effect.status.requires_positive_damage() && row.damage <= 0 {
            continue;
        }
        let status_spell_id = format!(
            "{}:{}:{}",
            row.spell_id,
            effect.status.payload().kind().as_str().to_ascii_lowercase(),
            row.hit_index
        );
        effects.push(effect.status.to_effect_packet(
            row.source,
            row.target,
            status_spell_id.as_str(),
            StatusPolarity::Debuff,
            row.kind.as_str(),
        ));
    }
}

fn scaled_impact_area_damage(primary_damage: i32, damage_multiplier: f32) -> i32 {
    if primary_damage <= 0 || !damage_multiplier.is_finite() || damage_multiplier <= 0.0 {
        return 0;
    }

    ((primary_damage as f32) * damage_multiplier)
        .round()
        .clamp(0.0, i32::MAX as f32) as i32
}

fn push_stagger_effect_if_applicable(
    ctx: &ReducerContext,
    effects: &mut Vec<EffectPacket>,
    source: Identity,
    target: Identity,
    spell_id: String,
    damage: i32,
    applies_stagger: bool,
) {
    if damage <= 0 || !applies_stagger {
        return;
    }
    let stagger_duration_ms = stagger_duration_for_target(ctx, source, target);
    if stagger_duration_ms == 0 {
        return;
    }

    push_stagger_effect_with_duration_if_applicable(
        effects,
        source,
        target,
        spell_id,
        damage,
        stagger_duration_ms,
    );
}

fn push_stagger_effect_with_duration_if_applicable(
    effects: &mut Vec<EffectPacket>,
    source: Identity,
    target: Identity,
    spell_id: String,
    damage: i32,
    stagger_duration_ms: u64,
) {
    if damage <= 0 || stagger_duration_ms == 0 {
        return;
    }

    effects.push(EffectPacket::ApplyStatus {
        source,
        target,
        spell_id,
        payload: StatusPayload::Stagger,
        polarity: StatusPolarity::Debuff,
        target_audience: crate::relations::TargetAudience::Hostile,
        duration: Duration::from_millis(stagger_duration_ms),
        stack_group: "STAGGER".to_string(),
        max_stacks: 1,
        stack_policy: StackPolicy::Refresh,
        dispel_types: Vec::new(),
    });
}

fn cancel_remaining_pending_melee_impacts(ctx: &ReducerContext, row: &PendingMeleeImpact) {
    let remaining_impact_ids: Vec<u64> = ctx
        .db
        .pending_melee_impact()
        .iter()
        .filter(|pending| pending.spell_id == row.spell_id && pending.impact_id != row.impact_id)
        .map(|pending| pending.impact_id)
        .collect();

    for impact_id in remaining_impact_ids {
        ctx.db.pending_melee_impact().impact_id().delete(impact_id);
    }
}

fn short_identity(identity: Identity) -> String {
    identity.to_hex().chars().take(8).collect()
}

fn emit_parry_event(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    caster_phys: &crate::player_physics::PlayerPhysics,
    target_x: f32,
    target_y: f32,
    target_z: f32,
    dx: f32,
    dz: f32,
    horiz_dist: f32,
) {
    let (dir_x, dir_z) = if horiz_dist > 0.001 {
        (dx / horiz_dist, dz / horiz_dist)
    } else {
        (0.0, 0.0)
    };
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: row.spell_id.clone(),
        action_kind: row.kind.clone(),
        ability_id: row.ability_id.clone(),
        hit_index: row.hit_index as i32,
        event_type: EVENT_PARRY.to_string(),
        source_kind: row.event_source.clone(),
        caster: row.source,
        hit: row.target,
        origin_x: caster_phys.pos_x,
        origin_y: caster_phys.pos_y,
        origin_z: caster_phys.pos_z,
        dir_x,
        dir_y: 0.0,
        dir_z,
        speed: 0.0,
        max_distance: 0.0,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: target_x,
        point_y: target_y,
        point_z: target_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

fn emit_block_event(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    caster_phys: &crate::player_physics::PlayerPhysics,
    target_x: f32,
    target_y: f32,
    target_z: f32,
    dx: f32,
    dz: f32,
    horiz_dist: f32,
) {
    let (dir_x, dir_z) = if horiz_dist > 0.001 {
        (dx / horiz_dist, dz / horiz_dist)
    } else {
        (0.0, 0.0)
    };

    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: row.spell_id.clone(),
        action_kind: row.kind.clone(),
        ability_id: row.ability_id.clone(),
        hit_index: row.hit_index as i32,
        event_type: EVENT_BLOCK.to_string(),
        source_kind: row.event_source.clone(),
        caster: row.source,
        hit: row.target,
        origin_x: caster_phys.pos_x,
        origin_y: caster_phys.pos_y,
        origin_z: caster_phys.pos_z,
        dir_x,
        dir_y: 0.0,
        dir_z,
        speed: 0.0,
        max_distance: 0.0,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: target_x,
        point_y: target_y,
        point_z: target_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

fn find_combo_root_for_authorization(
    combat_profile: &str,
    runtime_action_id: &RuntimeActionId,
) -> AuthoredActionId {
    let mut current = runtime_action_id.clone();
    for _ in 0..16 {
        let Some(strike) = strike_by_runtime_action_id(combat_profile, &current) else {
            return AuthoredActionId::new(current.as_str());
        };
        let Some(required) = strike
            .combo_from
            .as_deref()
            .filter(|required| !required.trim().is_empty())
        else {
            return AuthoredActionId::new(strike.id.as_str());
        };
        let Some(required_resolved) = resolve_melee_action_reference(combat_profile, required)
        else {
            return AuthoredActionId::new(current.as_str());
        };
        current = required_resolved.runtime_id;
    }
    AuthoredActionId::new(current.as_str())
}

fn timestamp_to_micros(timestamp: Timestamp) -> i64 {
    timestamp.to_micros_since_unix_epoch()
}

#[cfg(test)]
mod tests {
    use std::collections::{HashMap, HashSet};
    use std::sync::OnceLock;
    use std::time::Duration;

    use serde_json::Value;
    use spacetimedb::{Identity, Timestamp};

    const PROGRESSION_CATALOG_JSON: &str = include_str!("progression_catalog.shared.json");
    fn test_identity_with_byte(byte: u8) -> Identity {
        Identity::from_hex(format!("{byte:064x}").as_str())
            .expect("test identity hex should be valid")
    }

    use super::{
        auto_attack_reference_for_profile, canonical_slot_id, combo_input_decision,
        default_aerial_execution_mode, find_combo_root_for_authorization,
        gap_close_destination_within_epsilon, gap_close_has_horizontal_travel,
        gap_close_pre_commit_decision, gap_close_target_facing_satisfied,
        melee_hit_volume_contains_player, melee_manifest, pending_melee_impact_range,
        positive_projectile_override, projectile_max_distance_for_policy,
        push_melee_impact_status_effects, push_stagger_effect_with_duration_if_applicable,
        resolve_gap_close_destination, resolve_melee_action_reference,
        resolve_melee_action_reference_in_strikes, resolved_hit_window_damages,
        scaled_auto_attack_cadence_ms, scaled_impact_area_damage, scheduled_melee_impact_at,
        strike_total_duration_ms, timed_melee_movement_destination, AerialExecutionMode,
        AirborneTargetingMode, ComboInputDecision, ConsumedMeleeAttackModifier,
        GapCloseActorSnapshot, GapClosePreCommitDecision, MeleeAuthorization, PendingMeleeImpact,
        ResolvedMeleeAttackModifiers, ResolvedMeleeGapClose, SpellVec3, StaggerDirection,
        StrikeData, StrikeHitWindowData, GAP_CLOSE_COLLISION_REQUIRE_CLEAR_PATH,
        GAP_CLOSE_DESTINATION_BEHIND_TARGET, GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT,
        GAP_CLOSE_KIND_LINEAR, MELEE_MANIFEST_JSON, MELEE_TARGET_FACING_ARC_RADIANS,
    };
    use crate::action_ids::{AuthoredActionId, RuntimeActionId};
    use crate::animation_set_test_utils::animation_set_assets_by_combat_profile;
    use crate::combat::player_snapshot::PlayerSnapshot;
    use crate::combat::scene_query::{is_direction_within_facing_arc, target_within_area_range_xz};
    use crate::combat::{EffectPacket, StatusEffectKind, StatusPayload};
    use crate::player::{DEFAULT_COMBAT_PROFILE, TWO_HANDED_SWORD_COMBAT_PROFILE};
    use crate::player_physics::PlayerPhysics;
    use crate::player_state::PlayerState;
    use crate::progression::MeleeGapCloseCatalog;

    const TEST_GAP_CLOSE_DESTINATION_EPSILON_METERS: f32 = 0.10;

    fn test_player_physics(identity: Identity, pos_x: f32, pos_z: f32, yaw: f32) -> PlayerPhysics {
        PlayerPhysics {
            identity,
            pos_x,
            pos_y: 0.0,
            pos_z,
            vel_x: 0.0,
            vel_y: 0.0,
            vel_z: 0.0,
            yaw,
            grounded: true,
            last_processed_tick: 0,
            updated_at: Timestamp::UNIX_EPOCH,
        }
    }

    fn test_player_snapshot(identity: Identity, pos_x: f32, pos_z: f32) -> PlayerSnapshot {
        PlayerSnapshot {
            player_id: identity,
            alive: true,
            pos_x,
            pos_y: 0.0,
            pos_z,
            facing_yaw: 0.0,
            grounded: true,
            hit_radius: 0.35,
            hit_height: 2.0,
            last_processed_tick: 0,
        }
    }

    fn test_targetless_impact_row(
        kind: &str,
        range: f32,
        angle_degrees: f32,
    ) -> PendingMeleeImpact {
        let now = Timestamp::UNIX_EPOCH;
        PendingMeleeImpact {
            impact_id: 0,
            source: test_identity_with_byte(1),
            event_source: "test".to_string(),
            target: Identity::ZERO,
            spell_id: "test:targetless".to_string(),
            kind: "TEST_TARGETLESS".to_string(),
            ability_id: "TEST_TARGETLESS".to_string(),
            hit_index: 0,
            damage: 10,
            range,
            impact_at: now,
            active_until: now,
            recovery_until: now,
            parry_behavior: "UNPARRYABLE".to_string(),
            block_behavior: "BLOCKABLE".to_string(),
            airborne_targeting_mode: "ANY_TARGET".to_string(),
            targeting_kind: kind.to_string(),
            targeting_radius: if kind == "CASTER_RADIUS" { range } else { 0.0 },
            targeting_angle_degrees: angle_degrees,
            applies_stagger: false,
            grants_primary_resource_on_hit: false,
            impact_area_radius: 0.0,
            impact_area_damage: 0,
            impact_area_include_primary_target: false,
            resolve_at_micros: 0,
        }
    }

    #[test]
    fn melee_manifest_has_unique_ids_profiles_and_non_negative_windows() {
        let manifest = melee_manifest();
        let mut profile_ids = HashSet::new();
        let mut strike_count = 0usize;

        for profile in &manifest.profiles {
            assert!(
                !profile.combat_profile.trim().is_empty(),
                "combat profile id must not be empty"
            );
            assert!(
                profile_ids.insert(profile.combat_profile.clone()),
                "duplicate combat profile {}",
                profile.combat_profile
            );
            assert!(
                !profile.strikes.is_empty(),
                "combat profile {} must contain at least one strike",
                profile.combat_profile
            );
            assert!(
                !auto_attack_reference_for_profile(profile.combat_profile.as_str())
                    .unwrap_or_default()
                    .is_empty(),
                "combat profile {} must author an auto-attack strike",
                profile.combat_profile
            );
            let auto_attack_id = auto_attack_reference_for_profile(profile.combat_profile.as_str())
                .expect("auto-attack strike id should resolve");
            let mut strike_ids = HashSet::new();
            let mut slot_ids = HashSet::new();
            for strike in &profile.strikes {
                strike_count += 1;
                assert!(
                    strike_ids.insert(strike.id.clone()),
                    "duplicate strike id {} in profile {}",
                    strike.id,
                    profile.combat_profile
                );
                assert!(
                    !strike.hit_windows.is_empty(),
                    "hit windows required for {}",
                    strike.id
                );
                let slot_id = canonical_slot_id(strike);
                assert!(
                    !slot_id.trim().is_empty(),
                    "slot id required for {}",
                    strike.id
                );
                assert!(
                    slot_ids.insert(slot_id.clone()),
                    "duplicate slot id {} in profile {}",
                    slot_id,
                    profile.combat_profile
                );
            }
            let auto_attack_strike = profile
                .strikes
                .iter()
                .find(|strike| strike.id == auto_attack_id)
                .expect("auto-attack strike must exist in the owning combat profile");
            assert!(
                auto_attack_strike
                    .combo_from
                    .as_deref()
                    .unwrap_or("")
                    .is_empty(),
                "auto-attack strike '{}' for profile '{}' must be a root strike",
                auto_attack_strike.id,
                profile.combat_profile
            );
        }

        assert!(
            strike_count > 0,
            "melee manifest must contain at least one strike"
        );
        assert!(
            profile_ids.contains(DEFAULT_COMBAT_PROFILE),
            "melee manifest must contain the default combat profile {}",
            DEFAULT_COMBAT_PROFILE
        );
    }

    #[test]
    fn melee_manifest_does_not_serialize_removed_gameplay_keys() {
        let value: serde_json::Value =
            serde_json::from_str(MELEE_MANIFEST_JSON).expect("manifest json must parse");
        let removed_strike_keys = [
            "damage",
            "range",
            "cooldown_ms",
            "uses_global_cooldown",
            "parry_behavior",
            "block_behavior",
            "airborne_targeting_mode",
            "stagger_duration_ms",
        ];
        let profiles = value
            .get("profiles")
            .and_then(serde_json::Value::as_array)
            .expect("manifest profiles must be an array");

        for profile in profiles {
            let strikes = profile
                .get("strikes")
                .and_then(serde_json::Value::as_array)
                .expect("profile strikes must be an array");
            for strike in strikes {
                let object = strike.as_object().expect("strike must be an object");
                for key in removed_strike_keys {
                    assert!(
                        !object.contains_key(key),
                        "strike should not export {key}: {object:?}"
                    );
                }
                let hit_windows = object
                    .get("hit_windows")
                    .and_then(serde_json::Value::as_array)
                    .expect("strike hit_windows must be an array");
                for hit_window in hit_windows {
                    let hit_window_object = hit_window
                        .as_object()
                        .expect("hit window must be an object");
                    assert!(
                        !hit_window_object.contains_key("damage"),
                        "hit window should not export damage: {hit_window_object:?}"
                    );
                }
            }
        }
    }

    #[test]
    fn combo_successor_strikes_reachable_from_melee_roots_have_gameplay_rows() {
        let catalog: Value =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("catalog json must parse");
        let classes = catalog
            .get("classes")
            .and_then(Value::as_array)
            .expect("classes must be an array");
        let abilities = catalog
            .get("abilities")
            .and_then(Value::as_array)
            .expect("abilities must be an array");

        let mut class_profiles = HashMap::new();
        for class in classes {
            let class_id = class
                .get("class_id")
                .and_then(Value::as_str)
                .map(AuthoredActionId::new)
                .expect("class must declare class_id")
                .into_string();
            let profile_id = class
                .get("default_combat_profile_id")
                .and_then(Value::as_str)
                .map(AuthoredActionId::new)
                .expect("class must declare default_combat_profile_id")
                .into_string();
            class_profiles.insert(class_id, profile_id);
        }

        let mut melee_actions_by_profile: HashMap<String, HashSet<String>> = HashMap::new();
        let mut loadout_roots_by_profile: HashMap<String, HashSet<String>> = HashMap::new();
        for ability in abilities {
            let ability_kind = ability
                .get("gameplay")
                .and_then(Value::as_object)
                .and_then(|gameplay| gameplay.get("kind"))
                .and_then(Value::as_str)
                .unwrap_or_default();
            if !ability_kind.eq_ignore_ascii_case("MELEE") {
                continue;
            }

            let class_id = ability
                .get("class_id")
                .and_then(Value::as_str)
                .map(AuthoredActionId::new)
                .expect("melee ability must declare class_id")
                .into_string();
            let profile_id = class_profiles
                .get(&class_id)
                .unwrap_or_else(|| panic!("class {class_id} must map to a combat profile"));
            let action_id = ability
                .get("action_id")
                .and_then(Value::as_str)
                .map(AuthoredActionId::new)
                .expect("melee ability must declare action_id")
                .into_string();

            melee_actions_by_profile
                .entry(profile_id.clone())
                .or_default()
                .insert(action_id.clone());

            let is_loadout_root = ability
                .get("ability_tags")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(Value::as_str)
                .any(|tag| tag.eq_ignore_ascii_case("LOADOUT_ACTION"));
            if is_loadout_root {
                loadout_roots_by_profile
                    .entry(profile_id.clone())
                    .or_default()
                    .insert(action_id);
            }
        }

        for profile in &melee_manifest().profiles {
            let loadout_roots = loadout_roots_by_profile
                .get(&profile.combat_profile)
                .unwrap_or_else(|| {
                    panic!(
                        "combat profile {} must have at least one loadout melee root",
                        profile.combat_profile
                    )
                });
            let melee_actions = melee_actions_by_profile
                .get(&profile.combat_profile)
                .unwrap_or_else(|| {
                    panic!(
                        "combat profile {} must have melee gameplay rows",
                        profile.combat_profile
                    )
                });

            for strike in &profile.strikes {
                if strike.combo_from.as_deref().unwrap_or("").trim().is_empty() {
                    continue;
                }

                let resolved = resolve_melee_action_reference(&profile.combat_profile, &strike.id)
                    .unwrap_or_else(|| {
                        panic!(
                            "combo successor {} must resolve in profile {}",
                            strike.id, profile.combat_profile
                        )
                    });
                let root = find_combo_root_for_authorization(
                    &profile.combat_profile,
                    &resolved.runtime_id,
                )
                .into_string();
                if !loadout_roots.contains(&root) {
                    continue;
                }

                let successor_action = AuthoredActionId::new(strike.id.as_str()).into_string();
                assert!(
                    melee_actions.contains(&successor_action),
                    "combo successor {} in profile {} is reachable from root {} but has no melee ability gameplay row",
                    successor_action,
                    profile.combat_profile,
                    root
                );
            }
        }
    }

    #[test]
    fn melee_manifest_serializes_profile_stagger_durations() {
        for profile in &melee_manifest().profiles {
            assert!(profile.stagger_duration_f_ms > 0);
            assert!(profile.stagger_duration_b_ms > 0);
            assert!(profile.stagger_duration_l_ms > 0);
            assert!(profile.stagger_duration_r_ms > 0);
        }
    }

    #[test]
    fn server_stagger_direction_bands_match_player_animator_thresholds() {
        let forward = (0.0, 1.0);
        for (angle_degrees, expected) in [
            (-180.0, StaggerDirection::Back),
            (-136.0, StaggerDirection::Back),
            (-135.0, StaggerDirection::Left),
            (-46.0, StaggerDirection::Left),
            (-45.0, StaggerDirection::Forward),
            (0.0, StaggerDirection::Forward),
            (44.0, StaggerDirection::Forward),
            (45.0, StaggerDirection::Right),
            (135.0, StaggerDirection::Right),
            (136.0, StaggerDirection::Back),
            (180.0, StaggerDirection::Back),
        ] {
            let angle_degrees: f32 = angle_degrees;
            let radians = angle_degrees * std::f32::consts::PI / 180.0;
            let hit_dir = (radians.sin(), radians.cos());
            assert_eq!(
                super::classify_stagger_direction(hit_dir.0, hit_dir.1, forward.0, forward.1),
                expected,
                "angle {angle_degrees} should match PlayerAnimator directional thresholds"
            );
        }
    }

    #[test]
    fn combo_queue_window_uses_authored_chain_time_from_predecessor_start() {
        let manifest = melee_manifest();
        let profile = manifest
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == DEFAULT_COMBAT_PROFILE)
            .expect("expected sword and shield profile");
        let strike_1 = profile
            .strikes
            .iter()
            .find(|strike| strike.id == "SWORD_AND_SHIELD_LIGHT_COMBO_1")
            .expect("expected strike 1");
        let strike_2 = profile
            .strikes
            .iter()
            .find(|strike| strike.id == "SWORD_AND_SHIELD_LIGHT_COMBO_2")
            .expect("expected strike 2");

        let predecessor_total_ms = strike_total_duration_ms(strike_1);
        let execute_not_before_ms = strike_2.combo_open_ms;
        let queue_close_offset_ms = execute_not_before_ms + strike_2.combo_grace_ms;

        assert_eq!(predecessor_total_ms, 267);
        assert_eq!(execute_not_before_ms, strike_2.combo_open_ms);
        assert!(execute_not_before_ms >= predecessor_total_ms);
        assert_eq!(
            queue_close_offset_ms - execute_not_before_ms,
            strike_2.combo_grace_ms
        );
    }

    #[test]
    fn melee_manifest_contains_two_handed_sword_profile() {
        let manifest = melee_manifest();
        let greatsword_profile = manifest
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == TWO_HANDED_SWORD_COMBAT_PROFILE)
            .expect("expected greatsword profile");

        assert!(
            !greatsword_profile.strikes.is_empty(),
            "expected authored greatsword strikes"
        );
        for strike in &greatsword_profile.strikes {
            assert!(
                !canonical_slot_id(strike).is_empty(),
                "greatsword strike {} must resolve to a slot id",
                strike.id
            );
            assert!(
                !strike.hit_windows.is_empty(),
                "greatsword strike {} must contain hit windows",
                strike.id
            );
        }
    }

    #[test]
    fn greatsword_whirlwind_authored_strike_maps_to_finisher_slot() {
        let manifest = melee_manifest();
        let greatsword_profile = manifest
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == TWO_HANDED_SWORD_COMBAT_PROFILE)
            .expect("expected greatsword profile");
        let whirlwind = greatsword_profile
            .strikes
            .iter()
            .find(|strike| strike.id == "WHIRLWIND")
            .expect("expected Whirlwind strike");

        assert_eq!(canonical_slot_id(whirlwind), "finisher_1");
        assert_eq!(whirlwind.hit_windows.len(), 4);
    }

    #[test]
    fn greatsword_sunder_and_cleave_authored_strikes_remain_stable() {
        let greatsword_profile = melee_manifest()
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == TWO_HANDED_SWORD_COMBAT_PROFILE)
            .expect("expected greatsword profile");
        let sunder = greatsword_profile
            .strikes
            .iter()
            .find(|strike| strike.id == "COMBO_ATTACK_4_4_LUNGING_SLASH")
            .expect("expected Sunder strike");
        let cleave = greatsword_profile
            .strikes
            .iter()
            .find(|strike| strike.id == "COMBO_ATTACK_2_1_SPIN")
            .expect("expected Cleave strike");

        assert_eq!(canonical_slot_id(sunder), "finisher_4");
        assert!(!sunder.is_gap_closer);

        assert_eq!(canonical_slot_id(cleave), "alt_light_1");
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
                let trimmed = value.trim().to_string();
                if !trimmed.is_empty() {
                    values.insert(trimmed);
                }
            }
        }

        values
    }

    fn parse_authored_strike_ids_from_animation_set_asset(asset_contents: &str) -> HashSet<String> {
        parse_current_animation_set_melee_fields(asset_contents, "id: ")
    }

    fn parse_auto_attack_strike_id_from_animation_set_asset(
        asset_contents: &str,
    ) -> Option<String> {
        asset_contents
            .lines()
            .find_map(|line| line.strip_prefix("  autoAttackAuthoredStrikeId: "))
            .map(AuthoredActionId::new)
            .map(AuthoredActionId::into_string)
            .filter(|value| !value.is_empty())
    }

    fn parse_animation_set_clip_reference(
        asset_contents: &str,
        field_name: &str,
    ) -> Option<String> {
        let prefix = format!("  {field_name}: ");
        asset_contents
            .lines()
            .find_map(|line| line.strip_prefix(prefix.as_str()))
            .map(str::trim)
            .filter(|value| value.starts_with("{fileID:") && *value != "{fileID: 0}")
            .map(str::to_string)
    }

    #[test]
    fn melee_manifest_authored_ids_match_animation_set_assets() {
        for profile in &melee_manifest().profiles {
            let asset_contents = animation_set_assets_by_combat_profile()
                .get(profile.combat_profile.as_str())
                .unwrap_or_else(|| {
                    panic!(
                        "no CombatAnimationSet asset authors combatProfileId '{}'",
                        profile.combat_profile
                    )
                });
            let authored_ids =
                parse_authored_strike_ids_from_animation_set_asset(asset_contents.as_str());
            let auto_attack_strike_id =
                parse_auto_attack_strike_id_from_animation_set_asset(asset_contents.as_str())
                    .unwrap_or_else(|| {
                        panic!(
                            "expected auto-attack authoring for combat profile '{}'",
                            profile.combat_profile
                        )
                    });

            for strike in &profile.strikes {
                if strike.id.as_str() == auto_attack_strike_id.as_str() {
                    continue;
                }
                assert!(
                    authored_ids.contains(strike.id.as_str()),
                    "melee manifest strike '{}' for profile '{}' does not exist in the owning animation-set asset",
                    strike.id,
                    profile.combat_profile
                );
            }

            assert_eq!(
                auto_attack_reference_for_profile(profile.combat_profile.as_str()),
                Some(auto_attack_strike_id),
                "combat profile '{}' auto-attack authoring drifted from its animation-set asset",
                profile.combat_profile
            );
        }
    }

    #[test]
    fn animation_set_assets_assign_distinct_stagger_clips() {
        for (combat_profile, asset_contents) in animation_set_assets_by_combat_profile() {
            for (hit_field, stagger_field) in [
                ("hitF", "staggerF"),
                ("hitB", "staggerB"),
                ("hitL", "staggerL"),
                ("hitR", "staggerR"),
            ] {
                let hit_clip = parse_animation_set_clip_reference(asset_contents, hit_field)
                    .unwrap_or_else(|| {
                        panic!("{combat_profile} must assign {hit_field} before stagger validation")
                    });
                let stagger_clip =
                    parse_animation_set_clip_reference(asset_contents, stagger_field)
                        .unwrap_or_else(|| panic!("{combat_profile} must assign {stagger_field}"));
                assert_ne!(
                    stagger_clip, hit_clip,
                    "{combat_profile} must assign a distinct clip to {stagger_field}, not reuse {hit_field}"
                );
            }
        }
    }

    #[test]
    fn melee_stagger_packet_requires_positive_damage_and_duration() {
        let source = test_identity_with_byte(1);
        let target = test_identity_with_byte(2);

        let mut no_duration_effects = Vec::new();
        push_stagger_effect_with_duration_if_applicable(
            &mut no_duration_effects,
            source,
            target,
            "test:no-duration".to_string(),
            10,
            0,
        );
        assert!(no_duration_effects.is_empty());

        let mut zero_damage_effects = Vec::new();
        push_stagger_effect_with_duration_if_applicable(
            &mut zero_damage_effects,
            source,
            target,
            "test:zero-damage".to_string(),
            0,
            250,
        );
        assert!(zero_damage_effects.is_empty());

        let mut stagger_effects = Vec::new();
        push_stagger_effect_with_duration_if_applicable(
            &mut stagger_effects,
            source,
            target,
            "test:stagger".to_string(),
            10,
            250,
        );
        assert_eq!(stagger_effects.len(), 1);
        let EffectPacket::ApplyStatus {
            payload, duration, ..
        } = &stagger_effects[0]
        else {
            panic!("expected stagger apply-status effect");
        };
        assert_eq!(*payload, StatusPayload::Stagger);
        assert_eq!(*duration, Duration::from_millis(250));
    }

    #[test]
    fn melee_impact_effects_emit_authored_stun_status_packets() {
        let source = test_identity_with_byte(1);
        let target = test_identity_with_byte(2);
        let now = Timestamp::UNIX_EPOCH;
        let row = PendingMeleeImpact {
            impact_id: 0,
            source,
            event_source: "test".to_string(),
            target,
            spell_id: "test:charge".to_string(),
            kind: "WARRIOR_CHARGE".to_string(),
            ability_id: "WARRIOR_CHARGE".to_string(),
            hit_index: 0,
            damage: 32,
            range: 2.5,
            impact_at: now,
            active_until: now,
            recovery_until: now,
            parry_behavior: "PARRYABLE".to_string(),
            block_behavior: "BLOCKABLE".to_string(),
            airborne_targeting_mode: "ANY_TARGET".to_string(),
            targeting_kind: "TARGET".to_string(),
            targeting_radius: 0.0,
            targeting_angle_degrees: 0.0,
            applies_stagger: false,
            grants_primary_resource_on_hit: false,
            impact_area_radius: 0.0,
            impact_area_damage: 0,
            impact_area_include_primary_target: false,
            resolve_at_micros: 0,
        };

        let mut effects = Vec::new();
        push_melee_impact_status_effects(&mut effects, &row);

        assert_eq!(effects.len(), 1);
        let EffectPacket::ApplyStatus {
            source: effect_source,
            target: effect_target,
            payload,
            polarity,
            duration,
            stack_group,
            max_stacks,
            ..
        } = &effects[0]
        else {
            panic!("expected charge stun apply-status effect");
        };
        assert_eq!(*effect_source, source);
        assert_eq!(*effect_target, target);
        assert_eq!(*payload, StatusPayload::Stun);
        assert_eq!(*polarity, crate::combat::StatusPolarity::Debuff);
        assert_eq!(*duration, Duration::from_millis(5000));
        assert_eq!(stack_group, "WARRIOR_CHARGE_STUN");
        assert_eq!(*max_stacks, 1);
    }

    #[test]
    fn ability_damage_evenly_splits_multi_hit_windows() {
        let strike = StrikeData {
            id: "TEST_MULTI_HIT".to_string(),
            slot_id: "utility_1".to_string(),
            hit_windows: vec![
                StrikeHitWindowData {
                    impact_delay_ms: 100,
                },
                StrikeHitWindowData {
                    impact_delay_ms: 200,
                },
            ],
            recovery_ms: 250,
            is_gap_closer: false,
            combo_from: None,
            combo_open_ms: 0,
            combo_grace_ms: 0,
            aerial_execution_mode: default_aerial_execution_mode(),
            projectile: None,
        };

        let resolved = resolved_hit_window_damages(&strike, 31);
        assert_eq!(resolved.len(), strike.hit_windows.len());
        assert_eq!(resolved.iter().sum::<i32>(), 31);
        assert_eq!(resolved, vec![16, 15]);
    }

    #[test]
    fn melee_impact_area_damage_scales_from_confirmed_hit_damage() {
        assert_eq!(scaled_impact_area_damage(35, 0.65), 23);
        assert_eq!(scaled_impact_area_damage(10, 0.25), 3);
        assert_eq!(scaled_impact_area_damage(0, 1.0), 0);
        assert_eq!(scaled_impact_area_damage(10, 0.0), 0);
    }

    #[test]
    fn melee_attack_modifier_extends_short_reach_without_shortening_long_reach() {
        let mut modifiers = ResolvedMeleeAttackModifiers::default();
        modifiers.min_range = Some(5.0);

        assert!((modifiers.effective_range(2.5) - 5.0).abs() < 0.0001);
        assert!((modifiers.effective_range(5.5) - 5.5).abs() < 0.0001);
    }

    #[test]
    fn consumed_melee_modifier_event_fields_publish_giant_swing_identity() {
        let mut modifiers = ResolvedMeleeAttackModifiers::default();
        assert_eq!(
            super::consumed_melee_modifier_event_fields(&modifiers),
            ("", "")
        );

        modifiers.consumed.push(ConsumedMeleeAttackModifier {
            status_kind: StatusEffectKind::MeleeAttackModifier,
            status_group: "GIANT_SWING",
        });

        assert_eq!(
            super::consumed_melee_modifier_event_fields(&modifiers),
            ("MELEE_ATTACK_MODIFIER", "GIANT_SWING")
        );
    }

    fn collision_test_strikes() -> &'static [StrikeData] {
        static STRIKES: OnceLock<Vec<StrikeData>> = OnceLock::new();
        STRIKES
            .get_or_init(|| {
                vec![
                    StrikeData {
                        id: "UTILITY_1".to_string(),
                        slot_id: "authored_slot".to_string(),
                        hit_windows: vec![StrikeHitWindowData {
                            impact_delay_ms: 100,
                        }],
                        recovery_ms: 250,
                        is_gap_closer: false,
                        combo_from: None,
                        combo_open_ms: 0,
                        combo_grace_ms: 0,
                        aerial_execution_mode: default_aerial_execution_mode(),
                        projectile: None,
                    },
                    StrikeData {
                        id: "OTHER_STRIKE".to_string(),
                        slot_id: "utility_1".to_string(),
                        hit_windows: vec![StrikeHitWindowData {
                            impact_delay_ms: 100,
                        }],
                        recovery_ms: 250,
                        is_gap_closer: false,
                        combo_from: None,
                        combo_open_ms: 0,
                        combo_grace_ms: 0,
                        aerial_execution_mode: default_aerial_execution_mode(),
                        projectile: None,
                    },
                ]
            })
            .as_slice()
    }

    #[test]
    fn melee_action_reference_prefers_authored_id_on_collision() {
        let resolved =
            resolve_melee_action_reference_in_strikes(collision_test_strikes(), "utility_1")
                .expect("expected collision reference to resolve");

        assert_eq!(resolved.strike.id, "UTILITY_1");
        assert_eq!(resolved.authored_id.as_str(), "UTILITY_1");
        assert_eq!(resolved.runtime_id.as_str(), "authored_slot");
    }

    #[test]
    fn authored_melee_reference_resolves_to_runtime_slot_explicitly() {
        let resolved = resolve_melee_action_reference(
            TWO_HANDED_SWORD_COMBAT_PROFILE,
            "COMBO_ATTACK_3_1_LOW_TO_HIGH",
        )
        .expect("expected authored melee strike to resolve");

        assert_eq!(
            resolved.authored_id.as_str(),
            "COMBO_ATTACK_3_1_LOW_TO_HIGH"
        );
        assert_eq!(resolved.runtime_id.as_str(), "utility_1");
        assert_eq!(resolved.strike.id, "COMBO_ATTACK_3_1_LOW_TO_HIGH");
    }

    #[test]
    fn runtime_slot_melee_reference_remains_supported_at_boundary() {
        let resolved = resolve_melee_action_reference(DEFAULT_COMBAT_PROFILE, "utility_1")
            .expect("expected runtime slot compatibility lookup to resolve");

        assert_eq!(resolved.authored_id.as_str(), "SWORD_AND_SHIELD_UTILITY_1");
        assert_eq!(resolved.runtime_id.as_str(), "utility_1");
        assert_eq!(resolved.strike.id, "SWORD_AND_SHIELD_UTILITY_1");
    }

    #[test]
    fn combo_decision_reads_runtime_last_strike_state() {
        let profile = melee_manifest()
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == DEFAULT_COMBAT_PROFILE)
            .expect("expected sword and shield profile");
        let followup = profile
            .strikes
            .iter()
            .find(|strike| strike.id == "SWORD_AND_SHIELD_LIGHT_COMBO_2")
            .expect("expected followup strike");
        let state = PlayerState {
            player_id: Identity::ZERO,
            is_dummy: false,
            alive: true,
            eliminated: false,
            hp: 100,
            max_hp: 100,
            hit_radius: 0.5,
            hit_height: 1.8,
            movement_blocked: false,
            move_speed_multiplier: 1.0,
            movement_context_tick: 0,
            voluntary_move_epoch: 0,
            last_voluntary_move_input_tick: 0,
            respawn_at: Timestamp::UNIX_EPOCH,
            last_strike_id: RuntimeActionId::new("light_combo_1").into_string(),
            last_strike_at: Timestamp::UNIX_EPOCH,
        };
        let decision = combo_input_decision(
            &state,
            DEFAULT_COMBAT_PROFILE,
            followup,
            Timestamp::UNIX_EPOCH + Duration::from_millis(followup.combo_open_ms),
        );

        assert!(matches!(decision, ComboInputDecision::ExecuteNow));
    }

    #[test]
    fn auto_attack_cadence_scales_only_from_authored_cooldown_and_attack_speed() {
        assert_eq!(scaled_auto_attack_cadence_ms(900, 1.0), 900);
        assert_eq!(scaled_auto_attack_cadence_ms(900, 1.25), 720);
        assert_eq!(scaled_auto_attack_cadence_ms(1200, 1.5), 800);
    }

    #[test]
    fn intrinsic_auto_attack_projectile_clamp_uses_effective_range() {
        assert_eq!(
            projectile_max_distance_for_policy(35.0, 18.0, MeleeAuthorization::IntrinsicAutoAttack),
            18.0
        );
        assert_eq!(
            projectile_max_distance_for_policy(35.0, 24.0, MeleeAuthorization::IntrinsicAutoAttack),
            24.0
        );
    }

    #[test]
    fn selectable_projectile_keeps_authored_projectile_distance() {
        assert_eq!(
            projectile_max_distance_for_policy(35.0, 18.0, MeleeAuthorization::SelectableLoadout),
            35.0
        );
    }

    #[test]
    fn timed_melee_movement_backsteps_from_authored_yaw() {
        let start = SpellVec3::new(10.0, 2.0, 20.0);
        let end = timed_melee_movement_destination(start, 0.0, 7.0);
        assert!((end.x - 10.0).abs() < 0.001);
        assert!((end.y - 2.0).abs() < 0.001);
        assert!((end.z - 13.0).abs() < 0.001);

        let end = timed_melee_movement_destination(start, std::f32::consts::FRAC_PI_2, 7.0);
        assert!((end.x - 3.0).abs() < 0.001);
        assert!((end.y - 2.0).abs() < 0.001);
        assert!((end.z - 20.0).abs() < 0.001);
    }

    #[test]
    fn projectile_zero_and_missing_overrides_inherit_catalog_values() {
        assert_eq!(positive_projectile_override(None, 0.10), 0.10);
        assert_eq!(positive_projectile_override(Some(0.0), 0.10), 0.10);
        assert_eq!(positive_projectile_override(Some(-0.05), 0.10), 0.10);
        assert_eq!(positive_projectile_override(Some(f32::NAN), 0.10), 0.10);
        assert_eq!(positive_projectile_override(Some(0.05), 0.10), 0.05);
    }

    #[test]
    fn auto_attack_resource_gain_is_explicit_execution_policy() {
        assert!(super::MeleeExecutionPolicy::INTRINSIC_AUTO_ATTACK.grants_primary_resource_on_hit);
        assert!(
            !super::MeleeExecutionPolicy::INTRINSIC_AUTO_ATTACK_REPLACEMENT
                .grants_primary_resource_on_hit
        );
        assert!(!super::MeleeExecutionPolicy::PLAYER_INPUT.grants_primary_resource_on_hit);
        assert!(!super::MeleeExecutionPolicy::QUEUED_FOLLOWUP.grants_primary_resource_on_hit);
        assert!(!super::MeleeExecutionPolicy::PRACTICE.grants_primary_resource_on_hit);
    }

    #[test]
    fn auto_attack_authored_strikes_are_dedicated_manifest_entries() {
        for profile in &melee_manifest().profiles {
            let auto_attack_id = auto_attack_reference_for_profile(profile.combat_profile.as_str())
                .expect("expected auto-attack authored strike id");
            let auto_attack_strike = profile
                .strikes
                .iter()
                .find(|strike| strike.id == auto_attack_id)
                .expect("expected auto-attack strike in profile");
            let first_regular_strike = profile
                .strikes
                .first()
                .expect("expected at least one authored strike");

            assert_ne!(
                auto_attack_strike.id,
                first_regular_strike.id,
                "auto-attack should not reuse the first regular authored strike identity for profile '{}'",
                profile.combat_profile
            );
        }
    }

    #[test]
    fn archer_bow_marks_most_attacks_as_projectile_delivery() {
        let profile = melee_manifest()
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == "ARCHER_BOW")
            .expect("ARCHER_BOW profile should exist");
        let projectile_strikes: HashSet<_> = profile
            .strikes
            .iter()
            .filter(|strike| strike.projectile.is_some())
            .map(|strike| strike.id.as_str())
            .collect();

        for expected in [
            "ARCHER_QUICK_SHOT",
            "ARCHER_FOLLOW_THROUGH",
            "ARCHER_LOW_DRAW",
            "ARCHER_FINISHING_SHOT",
            "ARCHER_POWER_SHOT",
            "ARCHER_EVASIVE_SHOT",
            "ARCHER_AIR_SHOT",
            "AUTO_ATTACK_1",
        ] {
            assert!(
                projectile_strikes.contains(expected),
                "expected {expected} to release ARROW_STANDARD"
            );
        }

        assert!(
            !projectile_strikes.contains("ARCHER_RAIN_SHOT"),
            "Rain Shot should stay out of single-arrow V1 delivery"
        );
    }

    #[test]
    fn archer_projectile_rows_reference_standard_arrow() {
        let profile = melee_manifest()
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == "ARCHER_BOW")
            .expect("ARCHER_BOW profile should exist");

        for strike in profile
            .strikes
            .iter()
            .filter(|strike| strike.projectile.is_some())
        {
            let projectile = strike.projectile.as_ref().expect("checked is_some");
            assert_eq!(projectile.projectile_id, "ARROW_STANDARD");
            assert_eq!(projectile.speed, Some(0.0), "{}", strike.id);
            assert_eq!(projectile.max_distance, Some(0.0), "{}", strike.id);
            assert_eq!(projectile.radius, Some(0.0), "{}", strike.id);
            assert_eq!(projectile.spawn_forward, Some(0.0), "{}", strike.id);
            assert_eq!(projectile.spawn_height, Some(0.0), "{}", strike.id);
            assert_eq!(projectile.aim_height_scale, Some(0.0), "{}", strike.id);
            assert_eq!(
                projectile.update_interval_seconds,
                Some(0.0),
                "{}",
                strike.id
            );
        }
    }

    #[test]
    fn non_archer_profiles_do_not_author_projectile_delivery() {
        for profile in melee_manifest()
            .profiles
            .iter()
            .filter(|profile| profile.combat_profile != "ARCHER_BOW")
        {
            for strike in &profile.strikes {
                assert!(
                    strike.projectile.is_none(),
                    "profile '{}' strike '{}' should not author projectile delivery",
                    profile.combat_profile,
                    strike.id
                );
            }
        }
    }

    #[test]
    fn aerial_execution_mode_explicitly_gates_grounded_state() {
        assert!(AerialExecutionMode::GroundedOnly.allows_caster(true));
        assert!(!AerialExecutionMode::GroundedOnly.allows_caster(false));
        assert!(AerialExecutionMode::GroundedOrAirborne.allows_caster(true));
        assert!(AerialExecutionMode::GroundedOrAirborne.allows_caster(false));
        assert!(!AerialExecutionMode::AirborneOnly.allows_caster(true));
        assert!(AerialExecutionMode::AirborneOnly.allows_caster(false));
    }

    #[test]
    fn airborne_targeting_mode_only_restricts_airborne_casters() {
        assert!(AirborneTargetingMode::AnyTarget.allows_target(true, true));
        assert!(AirborneTargetingMode::AnyTarget.allows_target(true, false));
        assert!(AirborneTargetingMode::AnyTarget.allows_target(false, true));
        assert!(AirborneTargetingMode::AnyTarget.allows_target(false, false));
        assert!(AirborneTargetingMode::GroundedTargetOnly.allows_target(true, false));
        assert!(AirborneTargetingMode::GroundedTargetOnly.allows_target(false, true));
        assert!(!AirborneTargetingMode::GroundedTargetOnly.allows_target(false, false));
    }

    fn test_gap_close(destination: &str) -> MeleeGapCloseCatalog {
        MeleeGapCloseCatalog {
            ability_id: "TEST_GAP_CLOSE".to_string(),
            kind: GAP_CLOSE_KIND_LINEAR.to_string(),
            destination: destination.to_string(),
            speed: 18.0,
            arrival_buffer: 0.35,
            arrival_epsilon: TEST_GAP_CLOSE_DESTINATION_EPSILON_METERS,
            impact_range: 2.5,
            collision_policy: GAP_CLOSE_COLLISION_REQUIRE_CLEAR_PATH.to_string(),
            require_arrival_for_swing: true,
            requires_target_facing: false,
        }
    }

    fn gap_actor(pos_x: f32, pos_z: f32, yaw: f32, hit_radius: f32) -> GapCloseActorSnapshot {
        GapCloseActorSnapshot {
            pos_x,
            pos_y: 0.0,
            pos_z,
            yaw,
            hit_radius,
            hit_height: 2.0,
        }
    }

    #[test]
    fn gap_close_nearest_contact_destination_stops_on_approach_line() {
        let gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);
        let caster = gap_actor(0.0, 0.0, 0.0, 0.5);
        let target = gap_actor(0.0, 10.0, 0.0, 0.5);
        let destination = resolve_gap_close_destination(&gap, caster, target)
            .expect("destination should resolve");

        assert!((destination.x - 0.0).abs() < 0.001);
        assert!((destination.z - 8.65).abs() < 0.001);
    }

    #[test]
    fn gap_close_nearest_contact_does_not_backstep_when_already_in_contact_range() {
        let gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);
        let caster = gap_actor(0.0, 0.0, 0.0, 0.5);
        let target = gap_actor(0.0, 1.0, 0.0, 0.5);
        let destination = resolve_gap_close_destination(&gap, caster, target)
            .expect("destination should resolve");

        assert!((destination.x - caster.pos_x).abs() < 0.001);
        assert!((destination.y - caster.pos_y).abs() < 0.001);
        assert!((destination.z - caster.pos_z).abs() < 0.001);
    }

    #[test]
    fn gap_close_nearest_contact_overlap_stays_at_caster_position() {
        let gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);
        let caster = gap_actor(2.0, 3.0, std::f32::consts::PI, 0.5);
        let target = gap_actor(2.0, 3.0, 0.0, 0.5);
        let destination = resolve_gap_close_destination(&gap, caster, target)
            .expect("destination should resolve");

        assert!((destination.x - caster.pos_x).abs() < 0.001);
        assert!((destination.y - caster.pos_y).abs() < 0.001);
        assert!((destination.z - caster.pos_z).abs() < 0.001);
    }

    #[test]
    fn gap_close_zero_horizontal_travel_is_not_movement() {
        let start = SpellVec3::new(2.0, 0.0, 3.0);
        let end = SpellVec3::new(2.0, 1.0, 3.0);

        assert!(!gap_close_has_horizontal_travel(start, end));
        assert!(gap_close_has_horizontal_travel(
            start,
            SpellVec3::new(2.0, 0.0, 3.02)
        ));
    }

    #[test]
    fn gap_close_behind_destination_uses_target_yaw() {
        let gap = test_gap_close(GAP_CLOSE_DESTINATION_BEHIND_TARGET);
        let caster = gap_actor(0.0, 0.0, 0.0, 0.5);
        let target = gap_actor(0.0, 10.0, std::f32::consts::FRAC_PI_2, 0.5);
        let destination = resolve_gap_close_destination(&gap, caster, target)
            .expect("destination should resolve");

        assert!((destination.x + 1.35).abs() < 0.001);
        assert!((destination.z - 10.0).abs() < 0.001);
    }

    #[test]
    fn gap_close_target_facing_can_be_required() {
        let mut gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);
        gap.requires_target_facing = true;
        let target = gap_actor(0.0, 10.0, 0.0, 0.5);

        assert!(gap_close_target_facing_satisfied(
            &gap,
            gap_actor(0.0, 0.0, 0.0, 0.5),
            target
        ));
        assert!(!gap_close_target_facing_satisfied(
            &gap,
            gap_actor(0.0, 0.0, std::f32::consts::PI, 0.5),
            target
        ));
    }

    #[test]
    fn melee_target_facing_arc_rejects_targets_behind_caster() {
        assert!(is_direction_within_facing_arc(
            0.0,
            0.0,
            3.0,
            MELEE_TARGET_FACING_ARC_RADIANS,
            0.0,
        ));
        assert!(!is_direction_within_facing_arc(
            0.0,
            0.0,
            -3.0,
            MELEE_TARGET_FACING_ARC_RADIANS,
            0.0,
        ));
    }

    #[test]
    fn targetless_radius_melee_hit_volume_uses_caster_center() {
        let caster = test_player_physics(test_identity_with_byte(1), 0.0, 0.0, 0.0);
        let row = test_targetless_impact_row("CASTER_RADIUS", 3.25, 0.0);
        let inside = test_player_snapshot(test_identity_with_byte(2), 0.0, 3.0);
        let outside = test_player_snapshot(test_identity_with_byte(3), 0.0, 4.0);

        assert!(melee_hit_volume_contains_player(&row, &caster, &inside));
        assert!(!melee_hit_volume_contains_player(&row, &caster, &outside));
    }

    #[test]
    fn targetless_cone_melee_hit_volume_uses_caster_facing() {
        let caster = test_player_physics(test_identity_with_byte(1), 0.0, 0.0, 0.0);
        let row = test_targetless_impact_row("CASTER_CONE", 4.0, 60.0);
        let front = test_player_snapshot(test_identity_with_byte(2), 0.0, 3.0);
        let side = test_player_snapshot(test_identity_with_byte(3), 3.0, 0.0);

        assert!(melee_hit_volume_contains_player(&row, &caster, &front));
        assert!(!melee_hit_volume_contains_player(&row, &caster, &side));
    }

    #[test]
    fn gap_close_destination_epsilon_rejects_blocked_shortfalls() {
        let intended = SpellVec3::new(0.0, 0.0, 10.0);
        let epsilon = TEST_GAP_CLOSE_DESTINATION_EPSILON_METERS;
        assert!(gap_close_destination_within_epsilon(
            SpellVec3::new(0.0, 0.0, 10.0 - epsilon * 0.5),
            intended,
            epsilon
        ));
        assert!(!gap_close_destination_within_epsilon(
            SpellVec3::new(0.0, 0.0, 10.0 - epsilon * 2.0),
            intended,
            epsilon
        ));
    }

    #[test]
    fn required_arrival_gap_close_rejects_before_melee_commit_when_unresolved() {
        let mut gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);
        gap.require_arrival_for_swing = true;

        assert_eq!(
            gap_close_pre_commit_decision(Some(&gap), None),
            GapClosePreCommitDecision::RejectBeforeCommit
        );
    }

    #[test]
    fn optional_arrival_gap_close_can_continue_without_resolved_movement() {
        let mut gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);
        gap.require_arrival_for_swing = false;

        assert_eq!(
            gap_close_pre_commit_decision(Some(&gap), None),
            GapClosePreCommitDecision::Continue
        );
    }

    #[test]
    fn resolved_gap_close_continues_through_pre_commit_gate() {
        let gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);
        let resolved = ResolvedMeleeGapClose {
            end: SpellVec3::new(0.0, 0.0, 8.65),
            duration_ms: 500,
            impact_range: gap.impact_range,
        };

        assert_eq!(
            gap_close_pre_commit_decision(Some(&gap), Some(resolved)),
            GapClosePreCommitDecision::Continue
        );
    }

    #[test]
    fn gap_close_pending_impact_uses_impact_range_not_acquisition_range() {
        let mut gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);
        gap.impact_range = 2.5;
        let acquisition_range = 15.0;
        let resolved = ResolvedMeleeGapClose {
            end: SpellVec3::new(0.0, 0.0, 8.65),
            duration_ms: 500,
            impact_range: gap.impact_range,
        };

        assert_eq!(
            pending_melee_impact_range(acquisition_range, Some(&gap), Some(resolved)),
            2.5
        );
        assert_eq!(
            pending_melee_impact_range(acquisition_range, None, None),
            acquisition_range
        );
    }

    #[test]
    fn gap_close_impact_waits_until_arrival_when_animation_hit_is_early() {
        let now = Timestamp::UNIX_EPOCH;
        let resolved = ResolvedMeleeGapClose {
            end: SpellVec3::new(0.0, 0.0, 8.65),
            duration_ms: 700,
            impact_range: 2.5,
        };

        assert_eq!(
            scheduled_melee_impact_at(now, 100, Some(resolved)),
            now + Duration::from_millis(700)
        );
        assert_eq!(
            scheduled_melee_impact_at(now, 900, Some(resolved)),
            now + Duration::from_millis(900)
        );
    }

    #[test]
    fn target_moving_outside_gap_close_impact_range_can_miss_after_arrival() {
        assert!(target_within_area_range_xz(0.0, 0.0, 0.0, 2.9, 0.5, 2.5));
        assert!(!target_within_area_range_xz(0.0, 0.0, 0.0, 3.1, 0.5, 2.5));
    }

    #[test]
    fn melee_authorization_roots_resolve_to_authored_strike_ids_not_runtime_slots() {
        assert_eq!(
            find_combo_root_for_authorization(
                TWO_HANDED_SWORD_COMBAT_PROFILE,
                &RuntimeActionId::new("utility_1")
            )
            .as_str(),
            "COMBO_ATTACK_3_1_LOW_TO_HIGH"
        );
        assert_eq!(
            find_combo_root_for_authorization(
                TWO_HANDED_SWORD_COMBAT_PROFILE,
                &RuntimeActionId::new("utility_1")
            )
            .as_str(),
            "COMBO_ATTACK_3_1_LOW_TO_HIGH"
        );
        assert_eq!(
            find_combo_root_for_authorization(
                TWO_HANDED_SWORD_COMBAT_PROFILE,
                &RuntimeActionId::new("light_combo_2")
            )
            .as_str(),
            "COMBO_ATTACK_1_1_HIGH_TO_LOW"
        );
    }

    #[test]
    fn authored_strike_lookup_is_case_insensitive() {
        let resolved = resolve_melee_action_reference(
            TWO_HANDED_SWORD_COMBAT_PROFILE,
            "combo_attack_3_1_low_to_high",
        )
        .expect("expected lowercase authored strike lookup to resolve");
        assert_eq!(resolved.strike.id, "COMBO_ATTACK_3_1_LOW_TO_HIGH");
    }
}

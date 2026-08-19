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
    ActionRejectReason, ActionResultKind, PredictedActionFamily,
};
use crate::arena::instance_uses_flat_layout;
use crate::arena::player_world as _;
use crate::arena::{
    arena_seed_for_identity, open_world_scene_name_for_identity, players_share_world_context,
};
use crate::auto_attack::arm_auto_attack_if_unarmed_with_cadence;
use crate::combat::actor_snapshot::{
    actor_snapshot_for, CombatActorSnapshot, CombatActorSnapshotSet,
};
use crate::combat::position_history::{
    lag_comp_config, lag_comp_sweep_rewind_enabled, press_view_delay_micros,
    record_press_view_delay, rewound_pose_for, sweep_rewind_membership, view_delay_signal_label,
    SWEEP_REWIND_MARGIN_SPEED_MPS,
};
use crate::combat::scene_query::{
    has_line_of_sight, is_direction_within_facing_arc, target_within_area_range_xz,
    terrain_surface_y_for_caster, CombatAreaShape,
};
use crate::combat::status_effect as _;
use crate::combat::{
    advance_slipstream_after_movement_ability, arm_quickening_after_movement_ability,
    combat_projectile_definition_for_id, has_active_disabling_status, has_active_status,
    has_active_status_group, has_due_pending_effects, hostile_targeted_ability_misses, AttackAim,
    mark_harmful_combat_action, queue_effects, remove_active_status_group, resolve_pending_effects,
    status_matches_removal_filter, ActiveCombatProjectile, CombatEvent, CombatProjectileDefinition,
    DamageDelivery, DamageType, EffectPacket, ProjectilePresentationEvent, StackPolicy,
    StatusDispelType, StatusEffectKind, StatusPayload, StatusPolarity, COMBAT_EVENT_AREA_IMPACT,
    COMBAT_EVENT_BLOCK, COMBAT_EVENT_CAST, COMBAT_EVENT_EVADE, COMBAT_EVENT_FIZZLE,
    COMBAT_EVENT_IMPACT, COMBAT_EVENT_MISS, COMBAT_EVENT_PARRY, COMBAT_EVENT_RELEASE,
    COMBAT_METADATA_CONSUMED_MELEE_MODIFIER, COMBAT_METADATA_FLURRY_PROC, COMBAT_METADATA_NONE,
    COMBAT_SCALAR_MELEE_RELEASE_DELAY_SECONDS, COMBAT_SCALAR_NONE, COMBAT_SEQUENCE_NONE,
    DAMAGE_SOURCE_KIND_MELEE,
};
use crate::defense::{
    begin_evasion_window, clear_interruptible_defense_for_owner, resolve_defensible_combat_hit,
    CombatHitDeliveryKind, DefenseResolution, DefensibleCombatHit,
};
use crate::lingering_shade::arm_lingering_shade_for_voluntary_movement;
use crate::player::DEFAULT_COMBAT_PROFILE;
use crate::progression::{
    active_action_bar_assignment_debug_summary, active_selectable_ability_for_authored_action,
    derived_combat_profile_id_for_owner, melee_channel_for_ability_id,
    melee_channel_for_authored_strike,
    melee_evasive_leap_for_ability_id, melee_impact_effects_for_ability_id,
    melee_timed_movement_for_ability_id, primary_resource_gain_on_action_accept,
    resolved_auto_attack_mode_for_owner, ruin_flaming_weapon_on_hit_for_owner, AbilityCatalog,
    AutoAttackCatalog, AutoAttackReplacementCatalog, MeleeAbilityCatalog, MeleeChannelRuntime,
    MeleeEvasiveLeapRuntime, MeleeFireOnHitRuntime, MeleeGapCloseCatalog,
    MeleeTimedMovementRuntime,
};
use crate::relations::{can_harm, combat_relation, target_audience_allows, TargetAudience};
use crate::resources::{
    can_pay_action_resource_cost, grant_primary_resource_amount,
    grant_primary_resource_amount_for_kind, grant_primary_resource_for_melee_hit,
    pay_action_resource_cost, resolve_ability_action_resource_cost,
    resolve_ability_action_resource_cost_amount, ResolvedActionResourceCost,
    RESOURCE_KIND_MANA,
};
use crate::spells::{
    aoe_hits_player, approach_line_contact_point_xz, bake_linear_special_movement,
    begin_parabolic_arc_special_movement, begin_special_movement,
    begin_special_movement_with_facing_policy, contact_distance_from_radii,
    horizontal_movement_duration_ms, is_on_global_cooldown, is_on_named_cooldown,
    stamp_global_cooldown_for_duration, stamp_named_cooldown_for_duration, SpellVec3,
    SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK, SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y,
    SPECIAL_MOVEMENT_FACING_FACE_START,
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
use crate::melee::active_melee_channel as _;
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
use crate::npcs::npc_state as _;
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
#[allow(unused_imports)]
use crate::progression::melee_ability_catalog as _;
#[allow(unused_imports)]
use crate::progression::melee_gap_close_catalog as _;
#[allow(unused_imports)]
use crate::spells::global_cooldown as _;

const EVENT_CAST: &str = COMBAT_EVENT_CAST;
const EVENT_RELEASE: &str = COMBAT_EVENT_RELEASE;
const EVENT_IMPACT: &str = COMBAT_EVENT_IMPACT;
const EVENT_AREA_IMPACT: &str = COMBAT_EVENT_AREA_IMPACT;
const EVENT_FIZZLE: &str = COMBAT_EVENT_FIZZLE;
const EVENT_BLOCK: &str = COMBAT_EVENT_BLOCK;
const EVENT_PARRY: &str = COMBAT_EVENT_PARRY;
const EVENT_EVADE: &str = COMBAT_EVENT_EVADE;
const EVENT_MISS: &str = COMBAT_EVENT_MISS;
const MELEE_MANIFEST_JSON: &str = include_str!("melee_manifest.shared.json");
const GIGANTISM_STATUS_GROUP: &str = "GIGANTISM";
const GIGANTISM_MELEE_RANGE_BONUS_METERS: f32 = 1.5;
const SERRATED_BLADES_STATUS_GROUP: &str = "SERRATED_BLADES";
const SERRATED_BLADES_BLEED_STATUS_GROUP: &str = "SERRATED_BLADES_BLEED";
const SERRATED_BLADES_BLEED_DAMAGE_RATIO: f32 = 0.10;
const SERRATED_BLADES_BLEED_DURATION_MS: u64 = 2000;
const SERRATED_BLADES_BLEED_TICK_INTERVAL_MS: u64 = 1000;
const PALADIN_BRANDED_STATUS_GROUP: &str = "PALADIN_BRANDED";
const PALADIN_HALLOWED_THRUST_ABILITY_ID: &str = "PALADIN_HALLOWED_THRUST";
const PALADIN_HALLOWED_THRUST_MANA_GAIN: f32 = 20.0;
const DAGGER_COUP_DE_GRACE_ABILITY_ID: &str = "DAGGER_COUP_DE_GRACE";
const GAP_CLOSE_KIND_LINEAR: &str = "LINEAR";
const GAP_CLOSE_KIND_LEAP: &str = "LEAP";
const GAP_CLOSE_KIND_TELEPORT: &str = "TELEPORT";
const GAP_CLOSE_KIND_TELEPORT_BEHIND: &str = "TELEPORT_BEHIND";
const GAP_CLOSE_KIND_TELEPORT_BEHIND_TARGET_DISABLED: &str = "TELEPORT_BEHIND_TARGET_DISABLED";
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
    range_bonus: f32,
    force_stagger: bool,
    consume_on_attack: bool,
    bleed_damage_ratio: f32,
}

const MELEE_ATTACK_MODIFIER_SPECS: [MeleeAttackModifierSpec; 2] = [
    MeleeAttackModifierSpec {
        status_kind: StatusEffectKind::MeleeAttackModifier,
        status_group: SERRATED_BLADES_STATUS_GROUP,
        min_range: None,
        range_bonus: 0.0,
        force_stagger: false,
        consume_on_attack: false,
        bleed_damage_ratio: SERRATED_BLADES_BLEED_DAMAGE_RATIO,
    },
    MeleeAttackModifierSpec {
        status_kind: StatusEffectKind::Gigantism,
        status_group: GIGANTISM_STATUS_GROUP,
        min_range: None,
        range_bonus: GIGANTISM_MELEE_RANGE_BONUS_METERS,
        force_stagger: false,
        consume_on_attack: false,
        bleed_damage_ratio: 0.0,
    },
];

#[table(accessor = melee_attack_modifier_catalog, public)]
pub struct MeleeAttackModifierCatalog {
    #[primary_key]
    pub key: String,
    pub status_kind: String,
    pub stack_group: String,
    pub min_range: f32,
    pub range_bonus: f32,
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
            range_bonus: spec.range_bonus.max(0.0),
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
    range_bonus: f32,
    force_stagger: bool,
    bleed_damage_ratio: f32,
    consumed: Vec<ConsumedMeleeAttackModifier>,
}

impl ResolvedMeleeAttackModifiers {
    fn effective_range(&self, base_range: f32, include_range_bonus: bool) -> f32 {
        let range = self
            .min_range
            .map(|min_range| base_range.max(min_range))
            .unwrap_or(base_range);
        if include_range_bonus {
            range + self.range_bonus.max(0.0)
        } else {
            range
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum MeleeAttackDispatch {
    Rejected(ActionRejectReason),
    Queued,
    Started,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum MeleeAuthorization {
    ActionBar,
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
    presentation_metadata_kind: &'static str,
    schedules_auto_attack_on_started_swing: bool,
    uses_shared_cooldowns: bool,
    grants_primary_resource_on_hit: bool,
}

impl MeleeExecutionPolicy {
    const PLAYER_INPUT: Self = Self {
        authorization: MeleeAuthorization::ActionBar,
        event_source: MeleeEventSource::PlayerInput,
        presentation_metadata_kind: COMBAT_METADATA_NONE,
        schedules_auto_attack_on_started_swing: true,
        uses_shared_cooldowns: true,
        grants_primary_resource_on_hit: false,
    };

    const QUEUED_FOLLOWUP: Self = Self {
        authorization: MeleeAuthorization::ActionBar,
        event_source: MeleeEventSource::QueuedFollowup,
        presentation_metadata_kind: COMBAT_METADATA_NONE,
        schedules_auto_attack_on_started_swing: true,
        uses_shared_cooldowns: true,
        grants_primary_resource_on_hit: false,
    };

    const PRACTICE: Self = Self {
        authorization: MeleeAuthorization::ActionBar,
        event_source: MeleeEventSource::Practice,
        presentation_metadata_kind: COMBAT_METADATA_NONE,
        schedules_auto_attack_on_started_swing: false,
        uses_shared_cooldowns: true,
        grants_primary_resource_on_hit: false,
    };

    const INTRINSIC_AUTO_ATTACK: Self = Self {
        authorization: MeleeAuthorization::IntrinsicAutoAttack,
        event_source: MeleeEventSource::AutoAttack,
        presentation_metadata_kind: COMBAT_METADATA_NONE,
        schedules_auto_attack_on_started_swing: false,
        uses_shared_cooldowns: false,
        grants_primary_resource_on_hit: true,
    };

    const INTRINSIC_FLURRY_AUTO_ATTACK: Self = Self {
        authorization: MeleeAuthorization::IntrinsicAutoAttack,
        event_source: MeleeEventSource::AutoAttack,
        presentation_metadata_kind: COMBAT_METADATA_FLURRY_PROC,
        schedules_auto_attack_on_started_swing: false,
        uses_shared_cooldowns: false,
        grants_primary_resource_on_hit: true,
    };

    const INTRINSIC_AUTO_ATTACK_REPLACEMENT: Self = Self {
        authorization: MeleeAuthorization::IntrinsicAutoAttack,
        event_source: MeleeEventSource::AutoAttack,
        presentation_metadata_kind: COMBAT_METADATA_NONE,
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
    // Superseded by the ability-level requires_target_los targeting rule (S4);
    // still parsed so existing manifests stay valid, never consulted.
    #[serde(default)]
    #[allow(dead_code)]
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
    #[serde(default)]
    auto_attack_sequence: Vec<String>,
    #[serde(default)]
    auto_attack_sequence_interval_ms: u64,
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

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct AutoAttackSequenceStep {
    pub strike_id: AuthoredActionId,
    pub transition_delay_ms: u64,
    pub has_successor: bool,
}

pub(crate) fn auto_attack_sequence_len_for_profile(combat_profile: &str) -> Option<usize> {
    melee_manifest()
        .profiles
        .iter()
        .find(|profile| profile.combat_profile.eq_ignore_ascii_case(combat_profile))
        .map(|profile| profile.auto_attack_sequence.len())
}

pub(crate) fn auto_attack_sequence_step_for_profile(
    combat_profile: &str,
    sequence_index: usize,
) -> Option<AutoAttackSequenceStep> {
    if sequence_index == 0 {
        return None;
    }

    let profile = melee_manifest()
        .profiles
        .iter()
        .find(|profile| profile.combat_profile.eq_ignore_ascii_case(combat_profile))?;
    let current_action_id = profile.auto_attack_sequence.get(sequence_index)?;
    let previous_action_id = profile.auto_attack_sequence.get(sequence_index - 1)?;
    let current = resolve_melee_authored_action_in_strikes(
        profile.strikes.as_slice(),
        &AuthoredActionId::new(current_action_id.as_str()),
    )?;
    let previous = resolve_melee_authored_action_in_strikes(
        profile.strikes.as_slice(),
        &AuthoredActionId::new(previous_action_id.as_str()),
    )?;
    let required_predecessor = current.strike.combo_from.as_deref()?.trim();
    if required_predecessor.is_empty()
        || RuntimeActionId::new(required_predecessor).as_str() != previous.runtime_id.as_str()
        || profile.auto_attack_sequence_interval_ms == 0
    {
        return None;
    }

    Some(AutoAttackSequenceStep {
        strike_id: current.authored_id,
        transition_delay_ms: profile.auto_attack_sequence_interval_ms,
        has_successor: sequence_index + 1 < profile.auto_attack_sequence.len(),
    })
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
    /// This strike runs a melee channel that ends early on key release.
    pub holdable: bool,
}

#[derive(Clone)]
#[table(accessor = pending_melee_impact)]
pub struct PendingMeleeImpact {
    #[primary_key]
    #[auto_inc]
    pub impact_id: u64,
    #[index(btree)]
    pub source: Identity,
    pub event_source: String,
    pub target: Identity,
    pub spell_id: String,
    pub kind: String,
    pub ability_id: String,
    pub hit_index: u32,
    pub damage: i32,
    pub damage_type: String,
    pub target_health_damage_scaling_min_multiplier: f32,
    pub target_health_damage_scaling_max_multiplier: f32,
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
    /// Empty for legacy player/practice rows. Server-actor commitments persist
    /// their authored audience so impact-time relation validation cannot drift
    /// from the CAST-time gate.
    pub target_audience: String,
    /// Server-actor commitments use current authoritative facing and LOS at
    /// impact. Player rows leave these disabled and retain their existing
    /// prediction/rewind contract.
    pub requires_present_time_facing: bool,
    pub present_time_facing_arc_radians: f32,
    pub requires_present_time_los: bool,
    /// Player impacts historically publish max_distance=0 and derive their
    /// direct-action key from the action instance. Server actors can preserve
    /// their authored combat-event/effect identities explicitly.
    pub impact_event_max_distance: f32,
    pub direct_action_key: String,
    /// S8: the press's clamped attacker-view delay, frozen at press time. The
    /// impact-time reach re-check rewinds the target by this much (D2);
    /// 0 = present-time (no report, sweeps, autos, queued releases).
    pub view_delay_micros: i64,
    #[index(btree)]
    pub resolve_at_micros: i64,
    #[default(0.0f32)]
    pub targeting_width: f32,
}

#[derive(Clone)]
#[table(accessor = active_melee_channel)]
pub struct ActiveMeleeChannel {
    #[primary_key]
    pub owner: Identity,
    pub action_instance_id: String,
    pub action_kind: String,
    pub ability_id: String,
    pub source_kind: String,
    pub target: Identity,
    pub started_voluntary_move_epoch: u64,
    pub cancel_on_movement: bool,
    pub origin_x: f32,
    pub origin_y: f32,
    pub origin_z: f32,
    pub dir_x: f32,
    pub dir_z: f32,
    pub point_x: f32,
    pub point_y: f32,
    pub point_z: f32,
    pub ends_at: Timestamp,
    pub holdable: bool,
    #[index(btree)]
    pub ends_at_micros: i64,
}

/// Actor-generic server-side melee commitment used below player input and NPC
/// utility adapters. It owns authoritative present-time target validation,
/// the shared CAST event, and scheduling into `PendingMeleeImpact`; it does
/// not perform player authorization, action-bar, prediction, or rewind work.
pub(crate) struct ServerActorMeleeCommitment<'a> {
    pub source: Identity,
    pub target: Identity,
    pub action_instance_id: &'a str,
    pub action_kind: &'a str,
    pub ability_id: &'a str,
    pub event_source: &'a str,
    pub target_audience: TargetAudience,
    pub damage: i32,
    pub range: f32,
    pub windup_ms: u64,
    pub parry_behavior: &'a str,
    pub block_behavior: &'a str,
    pub requires_target_los: bool,
    pub facing_arc_radians: f32,
    pub direct_action_key: &'a str,
}

pub(crate) fn commit_server_actor_targeted_melee(
    ctx: &ReducerContext,
    now: Timestamp,
    commitment: ServerActorMeleeCommitment<'_>,
) -> bool {
    let Some(source) = actor_snapshot_for(ctx, commitment.source) else {
        return false;
    };
    let Some(target) = actor_snapshot_for(ctx, commitment.target) else {
        return false;
    };
    if !source.alive
        || !target.alive
        || source.player_id == target.player_id
        || has_active_disabling_status(ctx, commitment.source, now)
        || has_active_status(ctx, commitment.source, StatusEffectKind::Disarm, now)
        || !players_share_world_context(ctx, commitment.source, commitment.target)
        || !target_audience_allows(
            ctx,
            commitment.source,
            commitment.target,
            commitment.target_audience,
        )
        || !can_harm(ctx, commitment.source, commitment.target)
    {
        return false;
    }

    let dx = target.pos_x - source.pos_x;
    let dz = target.pos_z - source.pos_z;
    let distance = (dx * dx + dz * dz).sqrt();
    if distance > commitment.range + target.hit_radius
        || !is_direction_within_facing_arc(
            source.facing_yaw,
            dx,
            dz,
            commitment.facing_arc_radians,
            0.0,
        )
        || (commitment.requires_target_los && !has_line_of_sight(ctx, &source, &target))
    {
        return false;
    }

    let (dir_x, dir_z) = if distance > 0.001 {
        (dx / distance, dz / distance)
    } else {
        (0.0, 1.0)
    };
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: commitment.action_instance_id.to_string(),
        action_kind: commitment.action_kind.to_string(),
        ability_id: commitment.ability_id.to_string(),
        hit_index: 0,
        event_type: EVENT_CAST.to_string(),
        source_kind: commitment.event_source.to_string(),
        caster: commitment.source,
        hit: commitment.target,
        origin_x: source.pos_x,
        origin_y: source.pos_y,
        origin_z: source.pos_z,
        dir_x,
        dir_y: 0.0,
        dir_z,
        speed: 0.0,
        max_distance: commitment.range,
        scalar_kind: COMBAT_SCALAR_MELEE_RELEASE_DELAY_SECONDS.to_string(),
        scalar_value: commitment.windup_ms as f32 / 1000.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: target.pos_x,
        point_y: target.pos_y,
        point_z: target.pos_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });

    let impact_at = now + Duration::from_millis(commitment.windup_ms);
    ctx.db.pending_melee_impact().insert(PendingMeleeImpact {
        impact_id: 0,
        source: commitment.source,
        event_source: commitment.event_source.to_string(),
        target: commitment.target,
        spell_id: commitment.action_instance_id.to_string(),
        kind: commitment.action_kind.to_string(),
        ability_id: commitment.ability_id.to_string(),
        hit_index: 0,
        damage: commitment.damage,
        damage_type: DamageType::Physical.as_str().to_string(),
        target_health_damage_scaling_min_multiplier: 1.0,
        target_health_damage_scaling_max_multiplier: 1.0,
        range: commitment.range,
        impact_at,
        active_until: impact_at,
        recovery_until: impact_at,
        parry_behavior: commitment.parry_behavior.to_string(),
        block_behavior: commitment.block_behavior.to_string(),
        airborne_targeting_mode: "ANY_TARGET".to_string(),
        targeting_kind: "TARGET".to_string(),
        targeting_radius: 0.0,
        targeting_angle_degrees: 0.0,
        applies_stagger: false,
        grants_primary_resource_on_hit: false,
        impact_area_radius: 0.0,
        impact_area_damage: 0,
        impact_area_include_primary_target: false,
        target_audience: commitment.target_audience.as_str().to_string(),
        requires_present_time_facing: true,
        present_time_facing_arc_radians: commitment.facing_arc_radians,
        requires_present_time_los: commitment.requires_target_los,
        impact_event_max_distance: commitment.range,
        direct_action_key: commitment.direct_action_key.to_string(),
        view_delay_micros: 0,
        resolve_at_micros: timestamp_to_micros(impact_at),
        targeting_width: 0.0,
    });
    true
}

pub(crate) fn pending_melee_commitment_target_for_source(
    ctx: &ReducerContext,
    source: Identity,
    event_source: &str,
) -> Option<Identity> {
    ctx.db
        .pending_melee_impact()
        .source()
        .filter(source)
        .find(|row| row.event_source == event_source)
        .map(|row| row.target)
}

pub(crate) fn clear_pending_melee_impacts_for_source(ctx: &ReducerContext, source: Identity) {
    let impact_ids: Vec<u64> = ctx
        .db
        .pending_melee_impact()
        .source()
        .filter(source)
        .map(|row| row.impact_id)
        .collect();
    for impact_id in impact_ids {
        ctx.db.pending_melee_impact().impact_id().delete(impact_id);
    }
}

fn melee_channel_movement_canceled(
    cancel_on_movement: bool,
    started_voluntary_move_epoch: u64,
    current_voluntary_move_epoch: u64,
) -> bool {
    cancel_on_movement && current_voluntary_move_epoch != started_voluntary_move_epoch
}

fn pending_commitment_belongs_to_channel(
    channel_owner: Identity,
    channel_action_instance_id: &str,
    pending_owner: Identity,
    pending_action_instance_id: &str,
) -> bool {
    channel_owner == pending_owner && channel_action_instance_id == pending_action_instance_id
}

/// How an active melee channel ended. `Completed` lets the already-scheduled
/// commitments land (a channel that ran its authored duration owes its last
/// tick); `Canceled` and `ReleasedEarly` drop everything still pending, and
/// differ only in the lifecycle event they emit.
#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum MeleeChannelEnd {
    Completed,
    Canceled,
    ReleasedEarly,
}

impl MeleeChannelEnd {
    fn drops_pending_commitments(self) -> bool {
        !matches!(self, MeleeChannelEnd::Completed)
    }

    fn event_type(self) -> &'static str {
        match self {
            MeleeChannelEnd::Canceled => EVENT_FIZZLE,
            _ => EVENT_RELEASE,
        }
    }
}

fn finish_active_melee_channel(
    ctx: &ReducerContext,
    row: ActiveMeleeChannel,
    now: Timestamp,
    outcome: MeleeChannelEnd,
) {
    if outcome.drops_pending_commitments() {
        let pending_impact_ids: Vec<u64> = ctx
            .db
            .pending_melee_impact()
            .source()
            .filter(row.owner)
            .filter(|impact| {
                pending_commitment_belongs_to_channel(
                    row.owner,
                    row.action_instance_id.as_str(),
                    impact.source,
                    impact.spell_id.as_str(),
                )
            })
            .map(|impact| impact.impact_id)
            .collect();
        for impact_id in pending_impact_ids {
            ctx.db.pending_melee_impact().impact_id().delete(impact_id);
        }

        let pending_release_ids: Vec<u64> = ctx
            .db
            .pending_projectile_release()
            .iter()
            .filter(|release| {
                pending_commitment_belongs_to_channel(
                    row.owner,
                    row.action_instance_id.as_str(),
                    release.source,
                    release.action_instance_id.as_str(),
                )
            })
            .map(|release| release.release_id)
            .collect();
        for release_id in pending_release_ids {
            ctx.db
                .pending_projectile_release()
                .release_id()
                .delete(release_id);
        }
    }

    ctx.db.active_melee_channel().owner().delete(row.owner);
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: row.action_instance_id,
        action_kind: row.action_kind,
        ability_id: row.ability_id,
        hit_index: -1,
        event_type: outcome.event_type().to_string(),
        source_kind: row.source_kind,
        caster: row.owner,
        hit: row.target,
        origin_x: row.origin_x,
        origin_y: row.origin_y,
        origin_z: row.origin_z,
        dir_x: row.dir_x,
        dir_y: 0.0,
        dir_z: row.dir_z,
        speed: 0.0,
        max_distance: 0.0,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: row.point_x,
        point_y: row.point_y,
        point_z: row.point_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

pub(crate) fn cancel_active_melee_channel_for_interrupt(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> bool {
    let Some(row) = ctx.db.active_melee_channel().owner().find(owner) else {
        return false;
    };
    finish_active_melee_channel(ctx, row, now, MeleeChannelEnd::Canceled);
    true
}

/// Key-up for a holdable melee channel. Mirrors `release_cast` on the spell
/// side: the volley stops where the player let go, unfired commitments are
/// dropped, and the lifecycle RELEASE event drives the authored end clip.
#[reducer]
pub fn release_melee_channel(ctx: &ReducerContext, ability_id: String) -> Result<(), String> {
    let Some(row) = ctx.db.active_melee_channel().owner().find(ctx.sender()) else {
        return Ok(());
    };
    if !row.holdable {
        return Ok(());
    }
    // An empty ability id releases whatever the sender is channeling; a named
    // one must match so a stale key-up cannot cut a later channel short.
    if !ability_id.trim().is_empty()
        && !row.ability_id.eq_ignore_ascii_case(ability_id.trim())
        && !row.action_kind.eq_ignore_ascii_case(ability_id.trim())
    {
        return Ok(());
    }
    finish_active_melee_channel(ctx, row, ctx.timestamp, MeleeChannelEnd::ReleasedEarly);
    Ok(())
}

pub(crate) fn tick_active_melee_channels(ctx: &ReducerContext, now: Timestamp) {
    let now_micros = timestamp_to_micros(now);
    let rows: Vec<ActiveMeleeChannel> = ctx.db.active_melee_channel().iter().collect();
    for row in rows {
        if ctx
            .db
            .active_melee_channel()
            .owner()
            .find(row.owner)
            .is_none()
        {
            continue;
        }

        let canceled = ctx
            .db
            .player_state()
            .player_id()
            .find(row.owner)
            .is_none_or(|state| {
                !state.alive
                    || melee_channel_movement_canceled(
                        row.cancel_on_movement,
                        row.started_voluntary_move_epoch,
                        state.voluntary_move_epoch,
                    )
            })
            || has_active_disabling_status(ctx, row.owner, now)
            || has_active_status(ctx, row.owner, StatusEffectKind::Disarm, now);
        if canceled {
            finish_active_melee_channel(ctx, row, now, MeleeChannelEnd::Canceled);
        } else if now_micros >= row.ends_at_micros {
            finish_active_melee_channel(ctx, row, now, MeleeChannelEnd::Completed);
        }
    }
}

pub(crate) fn interrupt_server_actor_melee_commitments(
    ctx: &ReducerContext,
    source: Identity,
    event_source: &str,
    now: Timestamp,
) {
    let rows: Vec<PendingMeleeImpact> = ctx
        .db
        .pending_melee_impact()
        .source()
        .filter(source)
        .filter(|row| row.event_source == event_source)
        .collect();
    let mut fizzled_actions = HashSet::new();
    for row in rows {
        ctx.db
            .pending_melee_impact()
            .impact_id()
            .delete(row.impact_id);
        if !fizzled_actions.insert(row.spell_id.clone()) {
            continue;
        }
        let Some(source_pose) = actor_snapshot_for(ctx, row.source) else {
            continue;
        };
        let Some(target_pose) = actor_snapshot_for(ctx, row.target) else {
            continue;
        };
        if !target_pose.alive
            || !players_share_world_context(ctx, row.source, row.target)
            || !can_harm(ctx, row.source, row.target)
        {
            continue;
        }
        let dx = target_pose.pos_x - source_pose.pos_x;
        let dz = target_pose.pos_z - source_pose.pos_z;
        let distance = (dx * dx + dz * dz).sqrt();
        let (dir_x, dir_z) = if distance > 0.001 {
            (dx / distance, dz / distance)
        } else {
            (0.0, 1.0)
        };
        ctx.db.combat_event().insert(CombatEvent {
            event_id: 0,
            action_instance_id: row.spell_id,
            action_kind: row.kind,
            ability_id: row.ability_id,
            hit_index: row.hit_index as i32,
            event_type: EVENT_FIZZLE.to_string(),
            source_kind: row.event_source,
            caster: row.source,
            hit: row.target,
            origin_x: source_pose.pos_x,
            origin_y: source_pose.pos_y,
            origin_z: source_pose.pos_z,
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
            point_x: target_pose.pos_x,
            point_y: target_pose.pos_y,
            point_z: target_pose.pos_z,
            created_at: now,
            created_at_micros: timestamp_to_micros(now),
            damage: 0,
            metadata_kind: COMBAT_METADATA_NONE.to_string(),
            metadata_key: String::new(),
            metadata_value: String::new(),
        });
    }
}

pub(crate) fn resolve_due_pending_melee_impacts_for_event_source(
    ctx: &ReducerContext,
    now: Timestamp,
    event_source: &str,
) {
    let mut due: Vec<PendingMeleeImpact> = ctx
        .db
        .pending_melee_impact()
        .iter()
        .filter(|row| {
            row.event_source == event_source && row.resolve_at_micros <= timestamp_to_micros(now)
        })
        .collect();
    due.sort_by_key(|row| (row.resolve_at_micros, row.impact_id));
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
    }
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
    pub damage_type: String,
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
    damage_type: DamageType,
    target_health_damage_scaling: TargetHealthDamageScaling,
    range: f32,
    minimum_range: f32,
    cooldown_ms: u64,
    uses_global_cooldown: bool,
    global_cooldown_ms: u64,
    parry_behavior: ParryBehavior,
    block_behavior: BlockBehavior,
    airborne_targeting_mode: AirborneTargetingMode,
    targeting: ResolvedMeleeTargeting,
    requires_target_los: bool,
    applies_stagger: bool,
    impact_area: Option<ResolvedMeleeImpactArea>,
    timed_movement: Option<MeleeTimedMovementRuntime>,
    evasive_leap: Option<MeleeEvasiveLeapRuntime>,
    channel: Option<MeleeChannelRuntime>,
}

#[derive(Clone, Copy)]
struct TargetHealthDamageScaling {
    min_multiplier: f32,
    max_multiplier: f32,
}

impl TargetHealthDamageScaling {
    fn none() -> Self {
        Self {
            min_multiplier: 1.0,
            max_multiplier: 1.0,
        }
    }

    fn from_catalog(melee: &MeleeAbilityCatalog) -> Self {
        Self {
            min_multiplier: melee.target_health_damage_scaling_min_multiplier.max(0.0),
            max_multiplier: melee.target_health_damage_scaling_max_multiplier.max(0.0),
        }
    }
}

#[derive(Clone, Copy)]
enum ResolvedMeleeTargeting {
    Target,
    CasterRadius { radius: f32 },
    CasterCone { range: f32, angle_degrees: f32 },
    CasterRectangle { length: f32, width: f32 },
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
            "CASTER_RECTANGLE" => {
                if melee.targeting_range.is_finite()
                    && melee.targeting_range > 0.0
                    && melee.targeting_width.is_finite()
                    && melee.targeting_width > 0.0
                {
                    Some(Self::CasterRectangle {
                        length: melee.targeting_range,
                        width: melee.targeting_width,
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
            Self::CasterRectangle { .. } => "CASTER_RECTANGLE",
        }
    }

    fn pending_range(self, target_range: f32) -> f32 {
        match self {
            Self::Target => target_range,
            Self::CasterRadius { radius } => radius,
            Self::CasterCone { range, .. } => range,
            Self::CasterRectangle { length, .. } => length,
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

    fn pending_width(self) -> f32 {
        match self {
            Self::CasterRectangle { width, .. } => width,
            _ => 0.0,
        }
    }

    fn pending_requires_present_time_los(self, requires_target_los: bool) -> bool {
        !self.requires_target() && requires_target_los
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

#[reducer]
pub fn publish_melee_definitions(ctx: &ReducerContext) -> Result<(), String> {
    sync_melee_definitions(ctx);
    Ok(())
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
                holdable: melee_channel_for_authored_strike(combat_profile, strike.id.as_str())
                    .is_some_and(|channel| channel.holdable),
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

/// Local measurement aid: `ARENA_MELEE_RANGE_BONUS=<meters>` at build time
/// adds a flat reach bonus to every resolved melee/auto-attack range at the
/// single resolution choke points, so the reach gate, the swing dispatch, and
/// the impact re-check all agree. Baked at compile time like
/// `ARENA_NPC_HARMLESS` — absent from normal builds (default 0.0).
pub(crate) fn melee_range_bonus() -> f32 {
    static BONUS: std::sync::OnceLock<f32> = std::sync::OnceLock::new();
    *BONUS.get_or_init(|| {
        let bonus = option_env!("ARENA_MELEE_RANGE_BONUS")
            .and_then(|raw| raw.trim().parse::<f32>().ok())
            .filter(|v| v.is_finite() && *v > 0.0)
            .unwrap_or(0.0);
        if bonus > 0.0 {
            log::warn!(
                "[INIT] ARENA_MELEE_RANGE_BONUS baked in: +{bonus:.2} m melee/auto reach (local measurement build — do not deploy)"
            );
        }
        bonus
    })
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
        damage_type: DamageType::from_wire(melee.damage_type.as_str()),
        target_health_damage_scaling: TargetHealthDamageScaling::from_catalog(&melee),
        range: melee.range + melee_range_bonus(),
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
        requires_target_los: melee.requires_target_los,
        applies_stagger: melee.applies_stagger,
        impact_area: resolved_melee_impact_area_from_catalog(&melee),
        timed_movement: melee_timed_movement_for_ability_id(ability.ability_id.as_str()),
        evasive_leap: melee_evasive_leap_for_ability_id(ability.ability_id.as_str()),
        channel: melee_channel_for_ability_id(ability.ability_id.as_str()),
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
    let _ = ctx;
    let combat_profile_id = ability.combat_profile_id.trim().to_ascii_uppercase();
    if combat_profile_id.is_empty() {
        None
    } else {
        Some(combat_profile_id)
    }
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
            "melee action '{}' is assigned as ability '{}' on the action bar, but that ability does not resolve to melee gameplay (ability_kind='{}')",
            resolved.authored_id.as_str(),
            ability.ability_id,
            ability.ability_kind
        ));
    }

    let root_authored_action_id =
        find_combo_root_for_authorization(combat_profile, &resolved.runtime_id);
    if root_authored_action_id.as_str() == resolved.authored_id.as_str() {
        return Err(format!(
            "melee action '{}' is not assigned on the action bar ({})",
            resolved.authored_id.as_str(),
            active_action_bar_assignment_debug_summary(ctx, owner)
        ));
    }

    let Some(root_ability) =
        active_selectable_ability_for_authored_action(ctx, owner, &root_authored_action_id)
    else {
        return Err(format!(
            "melee combo root '{}' is not assigned on the action bar ({})",
            root_authored_action_id.as_str(),
            active_action_bar_assignment_debug_summary(ctx, owner)
        ));
    };
    if !root_ability.ability_kind.eq_ignore_ascii_case("MELEE")
        || melee_gameplay_for_ability(ctx, &root_ability).is_none()
    {
        return Err(format!(
            "melee combo root '{}' is assigned as ability '{}' on the action bar, but that ability does not resolve to melee gameplay (ability_kind='{}')",
            root_authored_action_id.as_str(),
            root_ability.ability_id,
            root_ability.ability_kind
        ));
    }

    let Some(followup_ability) = ctx.db.ability_catalog().iter().find(|ability| {
        ability.ability_kind.eq_ignore_ascii_case("MELEE")
            && derived_combat_profile_id_for_ability(ctx, ability)
                == derived_combat_profile_id_for_ability(ctx, &root_ability)
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

fn auto_attack_catalog_resolution_keys(
    combat_profile: &str,
    mode_id: &str,
    action_id: &AuthoredActionId,
) -> (Option<String>, String) {
    let mode_key = (!mode_id.trim().is_empty())
        .then(|| auto_attack_catalog_key_for_mode(combat_profile, mode_id, action_id));
    let profile_key = auto_attack_catalog_key(combat_profile, action_id);
    (mode_key, profile_key)
}

pub(crate) fn auto_attack_gameplay_for_profile_mode_action(
    ctx: &ReducerContext,
    combat_profile: &str,
    mode_id: &str,
    action_id: &AuthoredActionId,
) -> Option<AutoAttackCatalog> {
    let (mode_key, profile_key) =
        auto_attack_catalog_resolution_keys(combat_profile, mode_id, action_id);
    if let Some(mode_key) = mode_key {
        if let Some(row) = ctx.db.auto_attack_catalog().key().find(mode_key) {
            return Some(row);
        }
    }

    // Combat modes may override auto-attack gameplay, but a profile-level row
    // remains the shared default for every mode that does not define one.
    ctx.db.auto_attack_catalog().key().find(profile_key)
}

fn auto_attack_melee_gameplay_from_catalog(
    row: AutoAttackCatalog,
) -> Option<ResolvedMeleeGameplay> {
    Some(ResolvedMeleeGameplay {
        ability_id: None,
        base_damage: row.base_damage,
        damage_type: DamageType::from_wire(row.damage_type.as_str()),
        target_health_damage_scaling: TargetHealthDamageScaling::none(),
        range: row.range + melee_range_bonus(),
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
        requires_target_los: row.requires_target_los,
        applies_stagger: row.applies_stagger,
        impact_area: None,
        timed_movement: None,
        evasive_leap: None,
        channel: None,
    })
}

fn auto_attack_replacement_melee_gameplay_from_catalog(
    row: &AutoAttackReplacementCatalog,
) -> Option<ResolvedMeleeGameplay> {
    Some(ResolvedMeleeGameplay {
        ability_id: None,
        base_damage: row.base_damage,
        damage_type: DamageType::from_wire(row.damage_type.as_str()),
        target_health_damage_scaling: TargetHealthDamageScaling::none(),
        range: row.range + melee_range_bonus(),
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
        requires_target_los: row.requires_target_los,
        applies_stagger: row.applies_stagger,
        impact_area: None,
        timed_movement: None,
        evasive_leap: None,
        channel: None,
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

#[cfg(test)]
fn resolved_hit_window_damages(strike: &StrikeData, total_damage: i32) -> Vec<i32> {
    evenly_split_damage(total_damage, strike.hit_windows.len())
}

fn evenly_split_damage(total_damage: i32, count: usize) -> Vec<i32> {
    if count == 0 {
        return Vec::new();
    }
    let total_override = total_damage.max(0);
    let count_i32 = count as i32;
    let base = total_override / count_i32;
    let remainder = total_override % count_i32;
    let mut resolved = vec![base; count];
    for damage in resolved.iter_mut().take(remainder as usize) {
        *damage += 1;
    }
    resolved
}

fn melee_channel_tick_delays(channel: MeleeChannelRuntime) -> Vec<u64> {
    if channel.tick_interval_ms == 0 {
        return Vec::new();
    }

    let mut delays = Vec::new();
    let mut delay_ms = channel.first_tick_delay_ms;
    while delay_ms <= channel.duration_ms {
        delays.push(delay_ms);
        let Some(next_delay_ms) = delay_ms.checked_add(channel.tick_interval_ms) else {
            break;
        };
        delay_ms = next_delay_ms;
    }
    delays
}

fn melee_impact_delays(strike: &StrikeData, channel: Option<MeleeChannelRuntime>) -> Vec<u64> {
    match channel {
        Some(channel) if !channel.use_authored_hit_windows => melee_channel_tick_delays(channel),
        _ => strike
            .hit_windows
            .iter()
            .map(|hit_window| hit_window.impact_delay_ms)
            .collect(),
    }
}

fn yaw_direction(yaw: f32) -> (f32, f32) {
    (yaw.sin(), yaw.cos())
}

fn right_direction(yaw: f32) -> (f32, f32) {
    (yaw.cos(), -yaw.sin())
}

fn yaw_toward_xz(from_x: f32, from_z: f32, to_x: f32, to_z: f32, fallback_yaw: f32) -> f32 {
    let dx = to_x - from_x;
    let dz = to_z - from_z;
    if dx * dx + dz * dz <= 0.0001 {
        fallback_yaw
    } else {
        dx.atan2(dz)
    }
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

fn gap_close_activation_satisfied(
    gap_close: &MeleeGapCloseCatalog,
    target_is_disabled: bool,
) -> bool {
    gap_close.kind.as_str() != GAP_CLOSE_KIND_TELEPORT_BEHIND_TARGET_DISABLED || target_is_disabled
}

fn inactive_conditional_gap_close_range(
    effective_range: f32,
    gap_close: &MeleeGapCloseCatalog,
) -> f32 {
    effective_range.min(gap_close.impact_range.max(0.0))
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
    let destination = if matches!(
        gap_close.kind.as_str(),
        GAP_CLOSE_KIND_TELEPORT_BEHIND | GAP_CLOSE_KIND_TELEPORT_BEHIND_TARGET_DISABLED
    ) {
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

fn gap_close_uses_flat_layout(ctx: &ReducerContext, owner: Identity) -> bool {
    let Some(world) = ctx.db.player_world().identity().find(owner) else {
        return false;
    };
    let Some(instance_id) = world.instance_id else {
        return false;
    };
    instance_uses_flat_layout(ctx, instance_id)
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
    let flat_ground_only = gap_close_uses_flat_layout(ctx, owner);
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
            | GAP_CLOSE_KIND_TELEPORT_BEHIND_TARGET_DISABLED
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
        GAP_CLOSE_KIND_TELEPORT
            | GAP_CLOSE_KIND_TELEPORT_BEHIND
            | GAP_CLOSE_KIND_TELEPORT_BEHIND_TARGET_DISABLED
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

/// Inputs for one evaluation of the targeted positional gate (S8): every
/// positional accept/reject check — facing arc, range, minimum range, LOS —
/// judged against a single coherent target pose (`check_*`). Vitality,
/// status, aerial, and world-context gates live outside; they never rewind.
struct TargetedPositionalGate<'a> {
    caster: Identity,
    source_label: &'a str,
    strike_id: &'a str,
    caster_phys: &'a crate::player_physics::PlayerPhysics,
    target_snapshot: &'a CombatActorSnapshot,
    check_x: f32,
    check_y: f32,
    check_z: f32,
    effective_range: f32,
    minimum_range: f32,
    requires_target_los: bool,
    log_detail: bool,
}

fn evaluate_targeted_positional_gate(
    ctx: &ReducerContext,
    gate: TargetedPositionalGate<'_>,
) -> Option<ActionRejectReason> {
    let dx = gate.check_x - gate.caster_phys.pos_x;
    let dz = gate.check_z - gate.caster_phys.pos_z;
    if !is_direction_within_facing_arc(
        gate.caster_phys.yaw,
        dx,
        dz,
        MELEE_TARGET_FACING_ARC_RADIANS,
        0.0,
    ) {
        if gate.log_detail {
            log::info!(
                "[MELEE] owner={} source={} strike={} rejected_target_facing caster=({:.2},{:.2}) target=({:.2},{:.2}) yaw={:.2}",
                short_identity(gate.caster),
                gate.source_label,
                gate.strike_id,
                gate.caster_phys.pos_x,
                gate.caster_phys.pos_z,
                gate.check_x,
                gate.check_z,
                gate.caster_phys.yaw
            );
        }
        return Some(ActionRejectReason::NotFacingTarget);
    }

    let horiz_dist = (dx * dx + dz * dz).sqrt();
    if horiz_dist > gate.effective_range + gate.target_snapshot.hit_radius {
        if gate.log_detail {
            log::warn!(
                "[MELEE] {} {} — range check failed: dist={:.2} max={:.2} (range={:.2} radius={:.2})",
                &gate.caster.to_hex()[..8],
                gate.strike_id,
                horiz_dist,
                gate.effective_range + gate.target_snapshot.hit_radius,
                gate.effective_range,
                gate.target_snapshot.hit_radius
            );
        }
        return Some(ActionRejectReason::OutOfRange);
    }
    if gate.minimum_range > 0.0 {
        let minimum_allowed_distance = gate.minimum_range + gate.target_snapshot.hit_radius;
        if horiz_dist < minimum_allowed_distance {
            if gate.log_detail {
                log::info!(
                    "[MELEE] owner={} source={} strike={} rejected_minimum_range dist={:.2} min={:.2} minimum_range={:.2} target_radius={:.2}",
                    short_identity(gate.caster),
                    gate.source_label,
                    gate.strike_id,
                    horiz_dist,
                    minimum_allowed_distance,
                    gate.minimum_range,
                    gate.target_snapshot.hit_radius
                );
            }
            return Some(ActionRejectReason::OutOfRange);
        }
    }

    // LOS is a targeting rule (S4): every target-requiring action checks it
    // here, before delivery- or gap-close-specific validation, so a blocked
    // gap-close press reads LineOfSightBlocked, not GapCloseBlocked. Geometry
    // is static; only the target endpoint moves with the checked pose.
    if gate.requires_target_los {
        let Some(caster_snapshot) = actor_snapshot_for(ctx, gate.caster) else {
            return Some(ActionRejectReason::InvalidInput);
        };
        let mut los_target = *gate.target_snapshot;
        los_target.pos_x = gate.check_x;
        los_target.pos_y = gate.check_y;
        los_target.pos_z = gate.check_z;
        if !has_line_of_sight(ctx, &caster_snapshot, &los_target) {
            if gate.log_detail {
                log::info!(
                    "[MELEE] owner={} source={} strike={} rejected_target_los target={}",
                    short_identity(gate.caster),
                    gate.source_label,
                    gate.strike_id,
                    short_identity(gate.target_snapshot.player_id)
                );
            }
            return Some(ActionRejectReason::LineOfSightBlocked);
        }
    }

    None
}

fn reject_reason_audit_label(reject: Option<ActionRejectReason>) -> &'static str {
    match reject {
        None => "accept",
        Some(ActionRejectReason::NotFacingTarget) => "not_facing",
        Some(ActionRejectReason::OutOfRange) => "out_of_range",
        Some(ActionRejectReason::LineOfSightBlocked) => "los_blocked",
        Some(_) => "other",
    }
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
    perform_intrinsic_auto_attack_with_policy(
        ctx,
        caster,
        strike_id,
        target_id,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        MeleeExecutionPolicy::INTRINSIC_AUTO_ATTACK,
    )
}

pub(crate) fn perform_intrinsic_flurry_auto_attack_for(
    ctx: &ReducerContext,
    caster: Identity,
    strike_id: &AuthoredActionId,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
) -> Result<MeleeAttackDispatch, String> {
    perform_intrinsic_auto_attack_with_policy(
        ctx,
        caster,
        strike_id,
        target_id,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        MeleeExecutionPolicy::INTRINSIC_FLURRY_AUTO_ATTACK,
    )
}

fn perform_intrinsic_auto_attack_with_policy(
    ctx: &ReducerContext,
    caster: Identity,
    strike_id: &AuthoredActionId,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
    policy: MeleeExecutionPolicy,
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
        policy,
        None,
        Some(gameplay),
        None,
        None,
    )
}

pub(crate) fn perform_intrinsic_auto_attack_sequence_strike_for(
    ctx: &ReducerContext,
    caster: Identity,
    auto_attack_strike_id: &AuthoredActionId,
    sequence_strike_id: &AuthoredActionId,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
) -> Result<MeleeAttackDispatch, String> {
    let (combat_profile, sequence_strike) =
        resolve_melee_authored_action_for_caster(ctx, caster, sequence_strike_id)?;
    let mode_id = resolved_auto_attack_mode_for_owner(ctx, caster, combat_profile.as_str());
    let auto_attack_row = auto_attack_gameplay_for_profile_mode_action(
        ctx,
        combat_profile.as_str(),
        mode_id.as_str(),
        auto_attack_strike_id,
    )
    .ok_or_else(|| {
        format!(
            "auto-attack '{}' has no gameplay row for combat profile '{}' mode '{}'",
            auto_attack_strike_id.as_str(),
            combat_profile,
            mode_id
        )
    })?;
    let gameplay = auto_attack_melee_gameplay_from_catalog(auto_attack_row).ok_or_else(|| {
        format!(
            "auto-attack '{}' has invalid gameplay row for combat profile '{}' mode '{}'",
            auto_attack_strike_id.as_str(),
            combat_profile,
            mode_id
        )
    })?;

    perform_melee_attack_for_internal(
        ctx,
        caster,
        combat_profile,
        sequence_strike,
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
            "melee action '{}' is not assigned on the action bar ({})",
            root_authored_action_id.as_str(),
            active_action_bar_assignment_debug_summary(ctx, owner)
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
        return Ok(MeleeAttackDispatch::Rejected(
            ActionRejectReason::OnGlobalCooldown,
        ));
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
        resolved.range_bonus += spec.range_bonus.max(0.0);
        resolved.force_stagger |= spec.force_stagger;
        resolved.bleed_damage_ratio += spec.bleed_damage_ratio.max(0.0);
        if spec.consume_on_attack {
            resolved.consumed.push(ConsumedMeleeAttackModifier {
                status_kind: spec.status_kind,
                status_group: spec.status_group,
            });
        }
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
    } else if policy.authorization == MeleeAuthorization::ActionBar {
        resolved_melee_action_resource_cost(ctx, caster, combat_profile.as_str(), &resolved)?
    } else {
        None
    };
    let gameplay = if let Some(gameplay) = gameplay_override {
        gameplay
    } else {
        match policy.authorization {
            MeleeAuthorization::ActionBar => active_melee_gameplay_for_resolved_action(
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
        return Ok(MeleeAttackDispatch::Rejected(
            ActionRejectReason::InvalidInput,
        ));
    };
    if !caster_state.alive {
        log::warn!(
            "[MELEE] {} {} — caster is dead",
            &caster.to_hex()[..8],
            authored_action_id.as_str()
        );
        return Ok(MeleeAttackDispatch::Rejected(ActionRejectReason::Dead));
    }
    if has_active_disabling_status(ctx, caster, ctx.timestamp) {
        return Ok(MeleeAttackDispatch::Rejected(ActionRejectReason::Disabled));
    }
    if has_active_status(ctx, caster, StatusEffectKind::Disarm, ctx.timestamp) {
        return Ok(MeleeAttackDispatch::Rejected(ActionRejectReason::Disabled));
    }
    if policy.authorization == MeleeAuthorization::ActionBar
        && gameplay.targeting.requires_target()
        && has_active_status(ctx, caster, StatusEffectKind::Gouge, ctx.timestamp)
    {
        return Ok(MeleeAttackDispatch::Rejected(ActionRejectReason::Disabled));
    }
    if ctx.db.active_melee_channel().owner().find(caster).is_some() {
        return Ok(MeleeAttackDispatch::Rejected(ActionRejectReason::Busy));
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
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InsufficientResource,
            ));
        }
    }

    let impact_delays_ms = melee_impact_delays(&strike, gameplay.channel);
    if impact_delays_ms.is_empty() {
        return Err(format!(
            "Melee strike has no resolved impact schedule: {}:{}",
            combat_profile,
            authored_action_id.as_str()
        ));
    }
    if gameplay.channel.is_some_and(|channel| {
        impact_delays_ms
            .last()
            .is_some_and(|last_delay_ms| *last_delay_ms > channel.duration_ms)
    }) {
        return Err(format!(
            "Melee strike impact schedule exceeds channel duration: {}:{}",
            combat_profile,
            authored_action_id.as_str()
        ));
    }
    let hit_window_damages = evenly_split_damage(gameplay.base_damage, impact_delays_ms.len());
    let melee_modifiers = resolve_melee_attack_modifiers(ctx, caster, now);
    let (consumed_modifier_status_kind, consumed_modifier_stack_group) =
        consumed_melee_modifier_event_fields(&melee_modifiers);
    let (cast_metadata_kind, cast_metadata_key, cast_metadata_value) =
        if !policy.presentation_metadata_kind.is_empty() {
            (policy.presentation_metadata_kind, "", "")
        } else if !consumed_modifier_status_kind.is_empty()
            && !consumed_modifier_stack_group.is_empty()
        {
            (
                COMBAT_METADATA_CONSUMED_MELEE_MODIFIER,
                consumed_modifier_status_kind,
                consumed_modifier_stack_group,
            )
        } else {
            (COMBAT_METADATA_NONE, "", "")
        };
    let gap_close = melee_gap_close_for_ability(ctx, gameplay.ability_id.as_deref());
    let effective_range = melee_modifiers.effective_range(gameplay.range, gap_close.is_none());
    let mut resolved_effective_range = effective_range;
    let mut gap_close_active = gap_close.is_some();
    let applies_stagger = gameplay.applies_stagger || melee_modifiers.force_stagger;

    let combo_decision = if allow_queue {
        // Combo follow-ups can be queued immediately after the predecessor starts.
        // They release on their authored combo transition timing rather than waiting
        // for the current global cooldown to finish.
        let decision = combo_input_decision(&caster_state, combat_profile.as_str(), &strike, now);
        if matches!(decision, ComboInputDecision::Reject) {
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::ComboWindow,
            ));
        }
        if matches!(decision, ComboInputDecision::ExecuteNow)
            && gameplay.uses_global_cooldown
            && !is_combo_followup(&strike)
            && is_on_global_cooldown(ctx, caster, now)
        {
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::OnGlobalCooldown,
            ));
        }
        decision
    } else {
        // Once a combo follow-up was validly queued, release-time execution should
        // not be rejected for missing the original input window on a later tick.
        if policy.authorization == MeleeAuthorization::ActionBar
            && !combo_release_allowed(&caster_state, combat_profile.as_str(), &strike)
        {
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::ComboWindow,
            ));
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
        return Ok(MeleeAttackDispatch::Rejected(
            ActionRejectReason::OnCooldown,
        ));
    }

    let Some(caster_phys) = ctx.db.player_physics().identity().find(caster) else {
        log::warn!(
            "[MELEE] {} {} — caster has no player_physics",
            &caster.to_hex()[..8],
            authored_action_id.as_str()
        );
        return Ok(MeleeAttackDispatch::Rejected(
            ActionRejectReason::InvalidInput,
        ));
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
        return Ok(MeleeAttackDispatch::Rejected(
            ActionRejectReason::AerialMismatch,
        ));
    }

    let target_context = if gameplay.targeting.requires_target() {
        let Ok(target) = Identity::from_hex(target_id) else {
            log::warn!(
                "[MELEE] {} {} — bad target_id {:?}",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                target_id
            );
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InvalidTarget,
            ));
        };
        if target == caster {
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InvalidTarget,
            ));
        }
        let Some(target_snapshot) = actor_snapshot_for(ctx, target) else {
            log::warn!(
                "[MELEE] {} {} — target {} has no combat snapshot",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                &target.to_hex()[..8]
            );
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InvalidTarget,
            ));
        };
        if !target_snapshot.alive {
            log::warn!(
                "[MELEE] {} {} — target {} is dead",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                &target.to_hex()[..8]
            );
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InvalidTarget,
            ));
        }
        if !players_share_world_context(ctx, caster, target) {
            log::warn!(
                "[MELEE] {} {} — world context mismatch with target {}",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                &target.to_hex()[..8]
            );
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InvalidTarget,
            ));
        }
        if !can_harm(ctx, caster, target) {
            log::warn!(
                "[MELEE] {} {} — target {} is not hostile relation={:?}",
                &caster.to_hex()[..8],
                authored_action_id.as_str(),
                &target.to_hex()[..8],
                combat_relation(ctx, caster, target)
            );
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InvalidTarget,
            ));
        }
        if let Some(gap_close) = gap_close.as_ref() {
            gap_close_active = gap_close_activation_satisfied(
                gap_close,
                has_active_disabling_status(ctx, target, now),
            );
            if !gap_close_active {
                resolved_effective_range =
                    inactive_conditional_gap_close_range(effective_range, gap_close);
            }
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
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::AerialMismatch,
            ));
        }

        // S8: the whole positional gate judges one coherent target pose. When
        // the press carried a view time, the attacker-view pose decides
        // accept/reject (kill-switched) and the present-time verdict is
        // logged beside it — the flip rate is the audit's money metric.
        let view_delay_micros = press_view_delay_micros(ctx, caster);
        let (lag_comp_on, _) = lag_comp_config(ctx);
        let rewound_pose = if view_delay_micros > 0 {
            let pose = rewound_pose_for(ctx, target, view_delay_micros, now, &target_snapshot);
            (pose.rewound_by_micros > 0).then_some(pose)
        } else {
            None
        };
        let use_rewound = lag_comp_on && rewound_pose.is_some();
        let present_reject = evaluate_targeted_positional_gate(
            ctx,
            TargetedPositionalGate {
                caster,
                source_label: policy.source_label(),
                strike_id: strike.id.as_str(),
                caster_phys: &caster_phys,
                target_snapshot: &target_snapshot,
                check_x: target_snapshot.pos_x,
                check_y: target_snapshot.pos_y,
                check_z: target_snapshot.pos_z,
                effective_range: resolved_effective_range,
                minimum_range: gameplay.minimum_range,
                requires_target_los: gameplay.requires_target_los,
                log_detail: !use_rewound,
            },
        );
        let active_reject = if let Some(pose) = rewound_pose.as_ref() {
            let rewound_reject = evaluate_targeted_positional_gate(
                ctx,
                TargetedPositionalGate {
                    caster,
                    source_label: policy.source_label(),
                    strike_id: strike.id.as_str(),
                    caster_phys: &caster_phys,
                    target_snapshot: &target_snapshot,
                    check_x: pose.pos_x,
                    check_y: pose.pos_y,
                    check_z: pose.pos_z,
                    effective_range: resolved_effective_range,
                    minimum_range: gameplay.minimum_range,
                    requires_target_los: gameplay.requires_target_los,
                    log_detail: use_rewound,
                },
            );
            log::info!(
                "[LAG_COMP] melee_gate caster={} target={} strike={} rewound_ms={} source={} enabled={} present={} rewound={} flip={} signal={}",
                short_identity(caster),
                short_identity(target),
                strike.id,
                pose.rewound_by_micros / 1_000,
                pose.source.as_str(),
                lag_comp_on,
                reject_reason_audit_label(present_reject),
                reject_reason_audit_label(rewound_reject),
                present_reject != rewound_reject,
                view_delay_signal_label(ctx, caster)
            );
            if use_rewound {
                rewound_reject
            } else {
                present_reject
            }
        } else {
            present_reject
        };
        if let Some(reason) = active_reject {
            return Ok(MeleeAttackDispatch::Rejected(reason));
        }

        let dx = target_snapshot.pos_x - caster_phys.pos_x;
        let dz = target_snapshot.pos_z - caster_phys.pos_z;
        let horiz_dist = (dx * dx + dz * dz).sqrt();
        Some((target, target_snapshot, dx, dz, horiz_dist))
    } else {
        None
    };

    // Projectile deliveries no longer carry their own LOS gate: the
    // requires_target_los targeting check above covers every targeted strike
    // (S4). The manifest/definition requires_initial_line_of_sight flags are
    // superseded and no longer consulted here.
    if projectile_delivery.is_some() && target_context.is_none() {
        log::info!(
            "[MELEE_PROJECTILE] owner={} source={} strike={} rejected_targetless_projectile",
            short_identity(caster),
            policy.source_label(),
            strike.id
        );
        return Ok(MeleeAttackDispatch::Rejected(
            ActionRejectReason::InvalidTarget,
        ));
    }

    let auto_attack_target = target_context.as_ref().map(|context| context.0);

    if allow_queue {
        if let ComboInputDecision::QueueUntil(execute_not_before) = combo_decision {
            let Some(auto_attack_target) = auto_attack_target else {
                return Ok(MeleeAttackDispatch::Rejected(
                    ActionRejectReason::InvalidTarget,
                ));
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

    let active_gap_close = gap_close.as_ref().filter(|_| gap_close_active);
    let mut gap_close_failure: Option<GapCloseResolveFailure> = None;
    let resolved_gap_close = if let Some(gap_close) = active_gap_close {
        let Some((_, target_snapshot, _, _, _)) = target_context.as_ref() else {
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InvalidTarget,
            ));
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
    if gap_close_pre_commit_decision(active_gap_close, resolved_gap_close)
        == GapClosePreCommitDecision::RejectBeforeCommit
    {
        if let Some(gap_close) = active_gap_close {
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
        return Ok(MeleeAttackDispatch::Rejected(
            ActionRejectReason::GapCloseBlocked,
        ));
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
            return Ok(MeleeAttackDispatch::Rejected(
                ActionRejectReason::InsufficientResource,
            ));
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
    let evasive_leap = gameplay.evasive_leap;
    let (target, target_point_x, target_point_y, target_point_z, dx, dz, horiz_dist) =
        if let Some((target, target_snapshot, dx, dz, horiz_dist)) = target_context.as_ref() {
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

    crate::progression::arm_surprise_attacks_from_shroud(ctx, caster, spell_id.as_str(), now);
    crate::progression::break_shroud_on_attack(ctx, caster, now);
    if resolved_gap_close.is_some() || timed_movement.is_some() || evasive_leap.is_some() {
        arm_quickening_after_movement_ability(ctx, caster, now);
        advance_slipstream_after_movement_ability(ctx, caster, runtime_action_id.as_str(), now);
    }

    if let Some(leap) = evasive_leap {
        let position = SpellVec3::new(caster_phys.pos_x, caster_phys.pos_y, caster_phys.pos_z);
        begin_parabolic_arc_special_movement(
            ctx,
            caster,
            &format!("MELEE_EVASIVE_LEAP:{}", runtime_action_id.as_str()),
            now,
            leap.duration_ms,
            position,
            position,
            leap.arc_height,
            caster_phys.yaw,
            SPECIAL_MOVEMENT_FACING_FACE_START,
            SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y,
        );
        begin_evasion_window(
            ctx,
            caster,
            spell_id.as_str(),
            Duration::from_millis(leap.duration_ms),
            caster_phys.yaw,
            now,
        );
    }

    if let Some(gap_close) = resolved_gap_close {
        let movement_start =
            SpellVec3::new(caster_phys.pos_x, caster_phys.pos_y, caster_phys.pos_z);
        if gap_close_has_horizontal_travel(movement_start, gap_close.end) {
            let movement_facing_yaw =
                if gameplay.ability_id.as_deref() == Some(DAGGER_COUP_DE_GRACE_ABILITY_ID) {
                    yaw_toward_xz(
                        gap_close.end.x,
                        gap_close.end.z,
                        target_point_x,
                        target_point_z,
                        caster_phys.yaw,
                    )
                } else {
                    caster_phys.yaw
                };
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
                movement_facing_yaw,
                SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK,
            );
            arm_lingering_shade_for_voluntary_movement(
                ctx,
                caster,
                runtime_action_id.as_str(),
                gameplay.ability_id.as_deref().unwrap_or_default(),
                movement_start,
                gap_close.end,
                movement_facing_yaw,
                now,
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

    if let Some(channel) = gameplay.channel {
        let ends_at = now + Duration::from_millis(channel.duration_ms);
        ctx.db.active_melee_channel().insert(ActiveMeleeChannel {
            owner: caster,
            action_instance_id: spell_id.clone(),
            action_kind: strike.id.clone(),
            ability_id: gameplay.ability_id.clone().unwrap_or_default(),
            source_kind: policy.source_label().to_string(),
            target,
            started_voluntary_move_epoch: caster_state.voluntary_move_epoch,
            cancel_on_movement: channel.cancel_on_movement,
            origin_x: caster_phys.pos_x,
            origin_y: caster_phys.pos_y,
            origin_z: caster_phys.pos_z,
            dir_x,
            dir_z,
            point_x: target_point_x,
            point_y: target_point_y,
            point_z: target_point_z,
            ends_at,
            holdable: channel.holdable,
            ends_at_micros: timestamp_to_micros(ends_at),
        });
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
        max_distance: resolved_effective_range,
        scalar_kind: COMBAT_SCALAR_MELEE_RELEASE_DELAY_SECONDS.to_string(),
        scalar_value: gameplay.channel.map_or_else(
            || impact_delays_ms.last().copied().unwrap_or(0) as f32 / 1000.0,
            |channel| channel.duration_ms as f32 / 1000.0,
        ),
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: target_point_x,
        point_y: target_point_y,
        point_z: target_point_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: cast_metadata_kind.to_string(),
        metadata_key: cast_metadata_key.to_string(),
        metadata_value: cast_metadata_value.to_string(),
    });

    for (hit_index, impact_delay_ms) in impact_delays_ms.iter().copied().enumerate() {
        let impact_at = scheduled_melee_impact_at(now, impact_delay_ms, resolved_gap_close);
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
                resolved_effective_range,
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
                    damage_type: gameplay.damage_type.as_str().to_string(),
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
        let target_impact_range = pending_melee_impact_range(
            resolved_effective_range,
            active_gap_close,
            resolved_gap_close,
        );
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
            damage_type: gameplay.damage_type.as_str().to_string(),
            target_health_damage_scaling_min_multiplier: gameplay
                .target_health_damage_scaling
                .min_multiplier,
            target_health_damage_scaling_max_multiplier: gameplay
                .target_health_damage_scaling
                .max_multiplier,
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
            target_audience: String::new(),
            requires_present_time_facing: false,
            present_time_facing_arc_radians: 0.0,
            requires_present_time_los: gameplay
                .targeting
                .pending_requires_present_time_los(gameplay.requires_target_los),
            impact_event_max_distance: 0.0,
            direct_action_key: String::new(),
            view_delay_micros: press_view_delay_micros(ctx, caster),
            resolve_at_micros: timestamp_to_micros(impact_at),
            targeting_width: gameplay.targeting.pending_width(),
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
            ActionRejectReason::None,
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
    view_server_time_ms: u64,
) -> Result<(), String> {
    // S8: stamp the press's attacker-view claim for this transaction; 0 means
    // no report and the whole validation stays present-time.
    record_press_view_delay(ctx, ctx.sender(), view_server_time_ms);
    crate::world_interactions::cancel_active_world_interaction_for_actor(ctx, ctx.sender());
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
        Ok(MeleeAttackDispatch::Rejected(reason)) => {
            record_predicted_action_result(
                ctx,
                ctx.sender(),
                PredictedActionFamily::Melee,
                &token,
                "",
                ActionResultKind::Rejected,
                reason,
                ctx.timestamp,
            );
            Ok(())
        }
        Err(_) => {
            record_predicted_action_result(
                ctx,
                ctx.sender(),
                PredictedActionFamily::Melee,
                &token,
                "",
                ActionResultKind::Rejected,
                ActionRejectReason::Unspecified,
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
    arm_lingering_shade_for_voluntary_movement(
        ctx,
        row.owner,
        row.action_kind.as_str(),
        row.ability_id.as_str(),
        start,
        baked.end,
        row.yaw_start,
        now,
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

/// Melee channels that author `resource_cost_per_release` pay here — at the
/// shot, not the impact — so a volley costs exactly what it loosed, whether or
/// not the arrows connect. Running dry ends the channel rather than quietly
/// firing the rest for free.
fn commit_projectile_release_channel_cost(
    ctx: &ReducerContext,
    row: &PendingProjectileRelease,
    now: Timestamp,
) -> bool {
    let Some(channel) = melee_channel_for_ability_id(row.ability_id.as_str()) else {
        return true;
    };
    if channel.resource_cost_per_release <= 0.0 {
        return true;
    }
    let cost = if channel.resource_kind_per_release.is_empty() {
        let Some(ability) = active_selectable_ability_for_authored_action(
            ctx,
            row.source,
            &AuthoredActionId::new(row.action_kind.as_str()),
        ) else {
            return true;
        };
        let Some(cost) = resolve_ability_action_resource_cost_amount(
            ctx,
            row.source,
            &ability,
            channel.resource_cost_per_release,
        ) else {
            return true;
        };
        cost
    } else {
        ResolvedActionResourceCost::for_kind(
            channel.resource_kind_per_release,
            channel.resource_cost_per_release,
        )
    };
    pay_action_resource_cost(ctx, row.source, &cost, now)
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

    let Some(caster) = actor_snapshot_for(ctx, row.source) else {
        return;
    };
    if !caster.alive
        || has_active_disabling_status(ctx, row.source, now)
        || has_active_status(ctx, row.source, StatusEffectKind::Disarm, now)
    {
        emit_projectile_release_fizzle(ctx, row, &caster, now);
        return;
    }

    let Some(target) = actor_snapshot_for(ctx, row.target) else {
        emit_projectile_release_fizzle(ctx, row, &caster, now);
        return;
    };
    if !target.alive || !players_share_world_context(ctx, row.source, row.target) {
        emit_projectile_release_fizzle(ctx, row, &caster, now);
        return;
    }
    if !commit_projectile_release_channel_cost(ctx, row, now) {
        emit_projectile_release_fizzle(ctx, row, &caster, now);
        cancel_active_melee_channel_for_interrupt(ctx, row.source, now);
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
            projectile_trail_vfx_id: None,
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
            curve_control_x: 0.0,
            curve_control_y: 0.0,
            curve_control_z: 0.0,
            curve_end_x: 0.0,
            curve_end_y: 0.0,
            curve_end_z: 0.0,
            curve_progress: 0.0,
            sequence_index: 0,
            sequence_count: 1,
            damage: 0,
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
            curve_control_x: 0.0,
            curve_control_y: 0.0,
            curve_control_z: 0.0,
            curve_end_x: 0.0,
            curve_end_y: 0.0,
            curve_end_z: 0.0,
            traveled: 0.0,
            age: 0.0,
            lifetime,
            update_accum: 0.0,
            update_interval_seconds: row.update_interval_seconds,
            damage: row.damage,
            damage_type: row.damage_type.clone(),
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
    caster: &crate::combat::actor_snapshot::CombatActorSnapshot,
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
            projectile_trail_vfx_id: None,
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
            curve_control_x: 0.0,
            curve_control_y: 0.0,
            curve_control_z: 0.0,
            curve_end_x: 0.0,
            curve_end_y: 0.0,
            curve_end_z: 0.0,
            curve_progress: 0.0,
            sequence_index: 0,
            sequence_count: 1,
            damage: 0,
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
    if has_active_status(ctx, row.source, StatusEffectKind::Disarm, now) {
        return;
    }
    let actor_snapshots = CombatActorSnapshotSet::collect(ctx);
    if row.target == Identity::ZERO {
        resolve_pending_melee_hit_volume(ctx, row, now, &actor_snapshots);
        return;
    }

    resolve_pending_melee_target_impact(ctx, row, now, &actor_snapshots);
}

fn resolve_pending_melee_hit_volume(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    actor_snapshots: &CombatActorSnapshotSet,
) {
    let Some(caster_pose) = actor_snapshot_for(ctx, row.source) else {
        return;
    };
    if !caster_pose.alive {
        return;
    }
    let Some(airborne_targeting_mode) =
        AirborneTargetingMode::from_wire(row.airborne_targeting_mode.as_str())
    else {
        return;
    };

    // S10: rewind the melee caster-cone/radius sweep membership by the press's
    // frozen view delay, identically to the spell-area sweep (docs/sweep-
    // projectile-rewind-design-2026-07-05.md §1.0). The D2 impact re-check in
    // resolve_pending_melee_target_impact only rewinds TARGET presses, so this
    // is the sole rewind for the no-target cone/radius path. The caster frame
    // stays present (attacker pose never comes from history).
    let (lag_comp_on, max_rewind_ms) = lag_comp_config(ctx);
    let sweep_rewind_on = lag_comp_on && lag_comp_sweep_rewind_enabled(ctx);
    let view_delay_micros = row.view_delay_micros;
    // Widen the candidate disc so a victim who strafed out of the shape during
    // the view delay is still rewound-tested (§2.3); only adds candidates.
    let shape_query_radius = melee_hit_volume_shape(row)
        .map(CombatAreaShape::query_radius)
        .unwrap_or(row.range);
    let candidate_radius = if view_delay_micros > 0 {
        shape_query_radius + (max_rewind_ms as f32 / 1000.0) * SWEEP_REWIND_MARGIN_SPEED_MPS
    } else {
        shape_query_radius
    };

    let players = actor_snapshots.as_slice();
    let mut candidate_indices = Vec::new();
    actor_snapshots.query_disc_indices(
        caster_pose.pos_x,
        caster_pose.pos_z,
        candidate_radius,
        &mut candidate_indices,
    );

    // AREA_IMPACT is the area action/VFX signal and intentionally fires even when no targets pass
    // the later per-target filters; CONTACT/damage events remain the "hit landed" signal.
    emit_melee_area_impact_event(ctx, row, now, &caster_pose);

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
        if !airborne_targeting_mode.allows_target(caster_pose.grounded, player.grounded) {
            continue;
        }
        if !sweep_rewind_membership(
            ctx,
            row.source,
            player,
            view_delay_micros,
            now,
            sweep_rewind_on,
            row.kind.as_str(),
            |p| melee_hit_volume_contains_player(row, &caster_pose, p),
        ) {
            continue;
        }

        let mut target_row = row.clone();
        target_row.target = player.player_id;
        resolve_pending_melee_target_impact(ctx, &target_row, now, actor_snapshots);
    }
}

fn emit_melee_area_impact_event(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    caster_pose: &CombatActorSnapshot,
) {
    let dir_x = caster_pose.facing_yaw.sin();
    let dir_z = caster_pose.facing_yaw.cos();
    let origin_y = terrain_surface_y_for_caster(
        ctx,
        row.source,
        caster_pose.pos_x,
        caster_pose.pos_z,
        caster_pose.pos_y,
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
        origin_x: caster_pose.pos_x,
        origin_y,
        origin_z: caster_pose.pos_z,
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
        point_x: caster_pose.pos_x,
        point_y: origin_y,
        point_z: caster_pose.pos_z,
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
    caster_pose: &CombatActorSnapshot,
    player: &crate::combat::actor_snapshot::CombatActorSnapshot,
) -> bool {
    melee_hit_volume_shape(row).is_some_and(|shape| {
        shape.contains_player_xz(
            caster_pose.pos_x,
            caster_pose.pos_z,
            caster_pose.facing_yaw,
            player,
            0.0,
        )
    })
}

fn melee_hit_volume_shape(row: &PendingMeleeImpact) -> Option<CombatAreaShape> {
    match row.targeting_kind.trim().to_ascii_uppercase().as_str() {
        "CASTER_RADIUS" => Some(CombatAreaShape::Disc { radius: row.range }),
        "CASTER_CONE" => Some(CombatAreaShape::Cone {
            range: row.range,
            angle_degrees: row.targeting_angle_degrees,
            vertical_tolerance: None,
        }),
        "CASTER_RECTANGLE" => Some(CombatAreaShape::Rectangle {
            length: row.range,
            width: row.targeting_width,
            vertical_tolerance: None,
        }),
        _ => None,
    }
}

fn resolve_pending_melee_target_impact(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    actor_snapshots: &CombatActorSnapshotSet,
) {
    let Some(caster_pose) = actor_snapshot_for(ctx, row.source) else {
        return;
    };
    if !caster_pose.alive {
        return;
    }

    let Some(target_snapshot) = actor_snapshot_for(ctx, row.target) else {
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
    if !row.target_audience.is_empty()
        && !TargetAudience::from_wire(row.target_audience.as_str())
            .is_some_and(|audience| target_audience_allows(ctx, row.source, row.target, audience))
    {
        return;
    }

    let dx = target_snapshot.pos_x - caster_pose.pos_x;
    let dz = target_snapshot.pos_z - caster_pose.pos_z;
    let horiz_dist = (dx * dx + dz * dz).sqrt();
    let (dir_x, dir_z) = if horiz_dist > 0.001 {
        (dx / horiz_dist, dz / horiz_dist)
    } else {
        (0.0, 0.0)
    };
    let target_impact_y = melee_target_impact_point_y(&target_snapshot);
    if row.requires_present_time_facing
        && !is_direction_within_facing_arc(
            caster_pose.facing_yaw,
            dx,
            dz,
            row.present_time_facing_arc_radians,
            0.0,
        )
    {
        return;
    }
    if row.requires_present_time_los && !has_line_of_sight(ctx, &caster_pose, &target_snapshot) {
        return;
    }
    // S8 (D2, owner-signed): the impact-time reach re-check judges the target
    // pose the attacker is rendering *at impact* — the press's frozen view
    // delay applied to this moment. Sweeps and unreported presses stay
    // present-time; the whiff itself stays silent (player-melee parity).
    let present_in_reach = target_within_area_range_xz(
        caster_pose.pos_x,
        caster_pose.pos_z,
        target_snapshot.pos_x,
        target_snapshot.pos_z,
        target_snapshot.hit_radius,
        row.range,
    );
    let mut in_reach = present_in_reach;
    if row.view_delay_micros > 0 && row.targeting_kind.trim().eq_ignore_ascii_case("TARGET") {
        let (lag_comp_on, _) = lag_comp_config(ctx);
        let pose = rewound_pose_for(
            ctx,
            row.target,
            row.view_delay_micros,
            now,
            &target_snapshot,
        );
        if pose.rewound_by_micros > 0 {
            let rewound_in_reach = target_within_area_range_xz(
                caster_pose.pos_x,
                caster_pose.pos_z,
                pose.pos_x,
                pose.pos_z,
                target_snapshot.hit_radius,
                row.range,
            );
            log::info!(
                "[LAG_COMP] impact_recheck caster={} target={} strike={} rewound_ms={} source={} enabled={} present={} rewound={} flip={} signal={}",
                short_identity(row.source),
                short_identity(row.target),
                row.kind,
                pose.rewound_by_micros / 1_000,
                pose.source.as_str(),
                lag_comp_on,
                if present_in_reach { "in_reach" } else { "whiff" },
                if rewound_in_reach { "in_reach" } else { "whiff" },
                rewound_in_reach != present_in_reach,
                view_delay_signal_label(ctx, row.source)
            );
            if lag_comp_on {
                in_reach = rewound_in_reach;
            }
        }
    }
    if !in_reach {
        return;
    }

    // Sweeps reach a victim as a volume, a plain strike as a targeted blow, and a
    // strike carrying splash reaches the splashed as a volume. All three are
    // avoidable; only the targeted blow leaves its attacker Off Balance.
    let attack_aim = if !row.targeting_kind.trim().eq_ignore_ascii_case("TARGET")
        || (row.impact_area_radius > 0.0 && row.impact_area_damage > 0)
    {
        AttackAim::Volume
    } else {
        AttackAim::Targeted
    };
    if hostile_targeted_ability_misses(
        ctx,
        row.source,
        row.target,
        row.spell_id.as_str(),
        attack_aim,
        now,
    ) {
        mark_harmful_combat_action(ctx, row.source, row.target, now, row.kind.as_str());
        log::info!(
            "[MELEE_IMPACT] owner={} source={} strike={} target={} result=miss damage={} spell_id={}",
            short_identity(row.source),
            row.event_source,
            row.kind,
            short_identity(row.target),
            row.damage,
            row.spell_id
        );
        emit_miss_event(
            ctx,
            row,
            now,
            &caster_pose,
            target_snapshot.pos_x,
            target_impact_y,
            target_snapshot.pos_z,
            dx,
            dz,
            horiz_dist,
        );
        cancel_remaining_pending_melee_impacts(ctx, row);
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
            source_x: caster_pose.pos_x,
            source_y: caster_pose.pos_y,
            source_z: caster_pose.pos_z,
            impact_x: target_snapshot.pos_x,
            impact_y: target_impact_y,
            impact_z: target_snapshot.pos_z,
            dir_x,
            dir_y: 0.0,
            dir_z,
            speed: 0.0,
        },
    ) {
        DefenseResolution::Evaded => {
            mark_harmful_combat_action(ctx, row.source, row.target, now, row.kind.as_str());
            log::info!(
                "[MELEE_IMPACT] owner={} source={} strike={} target={} result=evaded damage={} spell_id={}",
                short_identity(row.source),
                row.event_source,
                row.kind,
                short_identity(row.target),
                row.damage,
                row.spell_id
            );
            emit_evade_event(
                ctx,
                row,
                now,
                &caster_pose,
                target_snapshot.pos_x,
                target_impact_y,
                target_snapshot.pos_z,
                dx,
                dz,
                horiz_dist,
            );
            return;
        }
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
                &caster_pose,
                target_snapshot.pos_x,
                target_impact_y,
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
                &caster_pose,
                target_snapshot.pos_x,
                target_impact_y,
                target_snapshot.pos_z,
                dx,
                dz,
                horiz_dist,
            );
            return;
        }
        DefenseResolution::None => {}
    }

    let damage = scaled_melee_damage_for_target(
        ctx,
        row.target,
        row.damage,
        row.target_health_damage_scaling_min_multiplier,
        row.target_health_damage_scaling_max_multiplier,
    );

    log::info!(
        "[MELEE_IMPACT] owner={} source={} strike={} target={} result=hit damage={} spell_id={}",
        short_identity(row.source),
        row.event_source,
        row.kind,
        short_identity(row.target),
        damage,
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
        origin_x: caster_pose.pos_x,
        origin_y: caster_pose.pos_y,
        origin_z: caster_pose.pos_z,
        dir_x,
        dir_y: 0.0,
        dir_z,
        speed: 0.0,
        max_distance: row.impact_event_max_distance,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: target_snapshot.pos_x,
        point_y: target_impact_y,
        point_z: target_snapshot.pos_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });

    let effect_spell_id = if row.direct_action_key.is_empty() {
        format!("{}:damage:{}", row.spell_id, row.hit_index)
    } else {
        row.spell_id.clone()
    };
    let mut effects = vec![EffectPacket::Damage {
        amount: damage,
        damage_type: DamageType::from_wire(row.damage_type.as_str()),
        source: row.source,
        target: row.target,
        spell_id: effect_spell_id,
        delivery: DamageDelivery::Direct,
        source_kind: DAMAGE_SOURCE_KIND_MELEE.to_string(),
        direct_action_key: if row.direct_action_key.is_empty() {
            format!("{}:hit:{}", row.spell_id, row.hit_index)
        } else {
            row.direct_action_key.clone()
        },
        is_area: row.impact_area_radius > 0.0 && row.impact_area_damage > 0,
    }];
    let flaming_weapon = ruin_flaming_weapon_on_hit_for_owner(ctx, row.source);
    push_flaming_weapon_effects(&mut effects, row, row.target, flaming_weapon.as_ref());
    push_stagger_effect_if_applicable(
        ctx,
        &mut effects,
        row.source,
        row.target,
        format!("{}:stagger:{}", row.spell_id, row.hit_index),
        damage,
        row.applies_stagger,
    );
    let (knockback_dir_x, knockback_dir_z) = if horiz_dist > 0.001 {
        (dir_x, dir_z)
    } else {
        (
            target_snapshot.facing_yaw.sin(),
            target_snapshot.facing_yaw.cos(),
        )
    };
    push_melee_impact_effects(ctx, &mut effects, row, knockback_dir_x, knockback_dir_z);
    let attack_modifiers = resolve_melee_attack_modifiers(ctx, row.source, now);
    push_melee_attack_modifier_bleed_effects(&mut effects, row, damage, &attack_modifiers);
    push_melee_impact_area_effects(
        ctx,
        &mut effects,
        row,
        damage,
        actor_snapshots,
        target_snapshot.pos_x,
        target_snapshot.pos_y,
        target_snapshot.pos_z,
        flaming_weapon.as_ref(),
    );
    queue_effects(ctx, effects);
    grant_primary_resource_for_melee_event_hit(
        ctx,
        row.source,
        row.grants_primary_resource_on_hit,
        now,
    );
    grant_hallowed_thrust_branded_mana(ctx, row, damage, now);
}

fn melee_target_impact_point_y(target: &CombatActorSnapshot) -> f32 {
    target.pos_y + target.hit_height.max(0.0) * 0.5
}

fn scaled_melee_damage_for_target(
    ctx: &ReducerContext,
    target: Identity,
    base_damage: i32,
    min_multiplier: f32,
    max_multiplier: f32,
) -> i32 {
    if base_damage <= 0 {
        return 0;
    }

    let min_multiplier = min_multiplier.max(0.0);
    let max_multiplier = max_multiplier.max(min_multiplier);
    if (min_multiplier - 1.0).abs() <= f32::EPSILON && (max_multiplier - 1.0).abs() <= f32::EPSILON
    {
        return base_damage;
    }

    let Some((hp, max_hp)) = target_real_health(ctx, target) else {
        return base_damage;
    };
    if max_hp <= 0 {
        return base_damage;
    }

    scaled_melee_damage_for_health(base_damage, hp, max_hp, min_multiplier, max_multiplier)
}

fn scaled_melee_damage_for_health(
    base_damage: i32,
    hp: i32,
    max_hp: i32,
    min_multiplier: f32,
    max_multiplier: f32,
) -> i32 {
    if base_damage <= 0 || max_hp <= 0 {
        return base_damage.max(0);
    }
    let health_pct = ((hp.max(0) as f32) / (max_hp as f32)).clamp(0.0, 1.0);
    let multiplier = max_multiplier - ((max_multiplier - min_multiplier) * health_pct);
    ((base_damage as f32) * multiplier).round().max(0.0) as i32
}

fn target_real_health(ctx: &ReducerContext, target: Identity) -> Option<(i32, i32)> {
    if let Some(state) = ctx.db.player_state().player_id().find(target) {
        return Some((state.hp, state.max_hp));
    }
    ctx.db
        .npc_state()
        .identity()
        .find(target)
        .map(|state| (state.hp, state.max_hp))
}

fn push_melee_impact_area_effects(
    ctx: &ReducerContext,
    effects: &mut Vec<EffectPacket>,
    row: &PendingMeleeImpact,
    primary_damage: i32,
    actor_snapshots: &CombatActorSnapshotSet,
    impact_x: f32,
    impact_y: f32,
    impact_z: f32,
    flaming_weapon: Option<&MeleeFireOnHitRuntime>,
) {
    if row.impact_area_radius <= 0.0 || primary_damage <= 0 {
        return;
    }
    let area_damage = if row.damage > 0 {
        scaled_impact_area_damage(
            primary_damage,
            row.impact_area_damage as f32 / row.damage as f32,
        )
    } else {
        row.impact_area_damage
    };
    if area_damage <= 0 {
        return;
    }

    let players = actor_snapshots.as_slice();
    let mut candidate_indices = Vec::new();
    actor_snapshots.query_disc_indices(
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
            amount: area_damage,
            damage_type: DamageType::from_wire(row.damage_type.as_str()),
            source: row.source,
            target: player.player_id,
            spell_id: format!("{}:area:{}", row.spell_id, row.hit_index),
            delivery: DamageDelivery::Direct,
            source_kind: DAMAGE_SOURCE_KIND_MELEE.to_string(),
            direct_action_key: format!(
                "{}:area:{}:{}",
                row.spell_id,
                row.hit_index,
                short_identity(player.player_id)
            ),
            is_area: true,
        });
        if player.player_id != row.target {
            push_flaming_weapon_effects(effects, row, player.player_id, flaming_weapon);
        }
    }
}

fn push_flaming_weapon_effects(
    effects: &mut Vec<EffectPacket>,
    row: &PendingMeleeImpact,
    target: Identity,
    tuning: Option<&MeleeFireOnHitRuntime>,
) {
    let Some(tuning) = tuning else {
        return;
    };

    let effect_key = format!(
        "{}:flaming_weapon:{}:{}",
        row.spell_id,
        row.hit_index,
        target.to_hex()
    );
    effects.push(EffectPacket::Damage {
        amount: tuning.bonus_damage,
        damage_type: DamageType::Fire,
        source: row.source,
        target,
        spell_id: format!("{effect_key}:fire"),
        delivery: DamageDelivery::Direct,
        source_kind: DAMAGE_SOURCE_KIND_MELEE.to_string(),
        direct_action_key: format!("{effect_key}:fire"),
        is_area: false,
    });
    effects.push(EffectPacket::ApplyStatus {
        source: row.source,
        target,
        spell_id: format!("{effect_key}:burn"),
        payload: StatusPayload::Dot {
            tick_damage: tuning.burn_tick_damage,
            damage_type: DamageType::Fire,
            tick_interval: tuning.burn_tick_interval,
        },
        polarity: StatusPolarity::Debuff,
        target_audience: TargetAudience::Hostile,
        duration: tuning.burn_duration,
        stack_group: format!("{}:{}", tuning.burn_status_stack_group, row.source.to_hex()),
        max_stacks: tuning.burn_max_stacks,
        stack_policy: StackPolicy::AddStackRefresh,
        dispel_types: tuning.burn_dispel_types.clone(),
    });
}

fn push_melee_impact_effects(
    ctx: &ReducerContext,
    effects: &mut Vec<EffectPacket>,
    row: &PendingMeleeImpact,
    dir_x: f32,
    dir_z: f32,
) {
    if row.ability_id.trim().is_empty() {
        return;
    }

    for effect in melee_impact_effects_for_ability_id(row.ability_id.as_str()) {
        match effect {
            crate::progression::MeleeImpactEffectRuntime::Knockback { distance_meters } => {
                effects.push(EffectPacket::Knockback {
                    source: row.source,
                    target: row.target,
                    spell_id: format!("{}:knockback:{}", row.spell_id, row.hit_index),
                    dir_x,
                    dir_z,
                    distance_meters,
                });
            }
            crate::progression::MeleeImpactEffectRuntime::ApplyStatus { status } => {
                push_melee_impact_status_effect(effects, row, &status);
            }
            crate::progression::MeleeImpactEffectRuntime::RemoveStatus {
                polarity,
                dispel_types,
                max_count,
            } => {
                push_melee_remove_status_effects(
                    ctx,
                    effects,
                    row.target,
                    polarity,
                    dispel_types.as_slice(),
                    max_count,
                );
            }
        }
    }
}

#[cfg(test)]
fn push_melee_impact_status_effects(effects: &mut Vec<EffectPacket>, row: &PendingMeleeImpact) {
    if row.ability_id.trim().is_empty() {
        return;
    }

    for effect in melee_impact_effects_for_ability_id(row.ability_id.as_str()) {
        if let crate::progression::MeleeImpactEffectRuntime::ApplyStatus { status } = effect {
            push_melee_impact_status_effect(effects, row, &status);
        }
    }
}

fn push_melee_attack_modifier_bleed_effects(
    effects: &mut Vec<EffectPacket>,
    row: &PendingMeleeImpact,
    damage: i32,
    modifiers: &ResolvedMeleeAttackModifiers,
) {
    let tick_damage = melee_attack_modifier_bleed_damage(damage, modifiers.bleed_damage_ratio);
    if tick_damage <= 0 {
        return;
    }

    effects.push(EffectPacket::ApplyStatus {
        source: row.source,
        target: row.target,
        spell_id: format!("{}:serrated_blades_bleed:{}", row.spell_id, row.hit_index),
        payload: StatusPayload::Dot {
            tick_damage,
            damage_type: DamageType::Physical,
            tick_interval: Duration::from_millis(SERRATED_BLADES_BLEED_TICK_INTERVAL_MS),
        },
        polarity: StatusPolarity::Debuff,
        target_audience: crate::relations::TargetAudience::Hostile,
        duration: Duration::from_millis(SERRATED_BLADES_BLEED_DURATION_MS),
        stack_group: format!(
            "{}:{}:{}",
            SERRATED_BLADES_BLEED_STATUS_GROUP, row.spell_id, row.hit_index
        ),
        max_stacks: 1,
        stack_policy: StackPolicy::Refresh,
        dispel_types: vec![StatusDispelType::Bleed],
    });
}

fn melee_attack_modifier_bleed_damage(damage: i32, damage_ratio: f32) -> i32 {
    if damage <= 0 || !damage_ratio.is_finite() || damage_ratio <= 0.0 {
        return 0;
    }

    ((damage as f32) * damage_ratio)
        .round()
        .clamp(1.0, i32::MAX as f32) as i32
}

fn grant_hallowed_thrust_branded_mana(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    damage: i32,
    now: Timestamp,
) {
    if damage <= 0 {
        return;
    }
    let ability_id = row.ability_id.trim().to_ascii_uppercase();
    if ability_id != PALADIN_HALLOWED_THRUST_ABILITY_ID
        || !target_has_branded_status(ctx, row.target, now)
    {
        return;
    }

    grant_primary_resource_amount_for_kind(
        ctx,
        row.source,
        RESOURCE_KIND_MANA,
        PALADIN_HALLOWED_THRUST_MANA_GAIN,
        now,
    );
}

fn target_has_branded_status(ctx: &ReducerContext, target: Identity, now: Timestamp) -> bool {
    ctx.db
        .status_effect()
        .target()
        .filter(target)
        .any(|effect| {
            effect.effect_kind == StatusEffectKind::Dot.as_str()
                && now < effect.expires_at
                && status_stack_group_matches_base(
                    effect.stack_group.as_str(),
                    PALADIN_BRANDED_STATUS_GROUP,
                )
        })
}

fn status_stack_group_matches_base(stack_group: &str, base: &str) -> bool {
    stack_group == base
        || stack_group
            .strip_prefix(base)
            .is_some_and(|suffix| suffix.starts_with(':'))
}

fn push_melee_impact_status_effect(
    effects: &mut Vec<EffectPacket>,
    row: &PendingMeleeImpact,
    status: &crate::combat::StatusApplication,
) {
    if status.requires_positive_damage() && row.damage <= 0 {
        return;
    }
    let status_spell_id = format!(
        "{}:{}:{}",
        row.spell_id,
        status.payload().kind().as_str().to_ascii_lowercase(),
        row.hit_index
    );
    effects.push(status.to_effect_packet(
        row.source,
        row.target,
        status_spell_id.as_str(),
        StatusPolarity::Debuff,
        row.kind.as_str(),
    ));
}

fn push_melee_remove_status_effects(
    ctx: &ReducerContext,
    effects: &mut Vec<EffectPacket>,
    target: Identity,
    polarity: Option<StatusPolarity>,
    dispel_types: &[crate::combat::StatusDispelType],
    max_count: u32,
) {
    if max_count == 0 || (polarity.is_none() && dispel_types.is_empty()) {
        return;
    }

    let mut matches: Vec<_> = ctx
        .db
        .status_effect()
        .target()
        .filter(target)
        .filter(|effect| status_matches_removal_filter(ctx, effect, polarity, dispel_types))
        .collect();
    matches.sort_by_key(|effect| effect.status_id);

    for effect in matches.into_iter().take(max_count as usize) {
        let Some(kind) = StatusEffectKind::from_wire(effect.effect_kind.as_str()) else {
            continue;
        };
        effects.push(EffectPacket::RemoveStatus {
            target,
            kind,
            stack_group: effect.stack_group,
            remove_stacks: 0,
        });
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
    caster_phys: &CombatActorSnapshot,
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

fn emit_evade_event(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    caster_phys: &CombatActorSnapshot,
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
        event_type: EVENT_EVADE.to_string(),
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
    caster_phys: &CombatActorSnapshot,
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

fn emit_miss_event(
    ctx: &ReducerContext,
    row: &PendingMeleeImpact,
    now: Timestamp,
    caster_phys: &CombatActorSnapshot,
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
        event_type: EVENT_MISS.to_string(),
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
        auto_attack_catalog_resolution_keys, auto_attack_reference_for_profile,
        auto_attack_sequence_step_for_profile, canonical_slot_id, combo_input_decision,
        default_aerial_execution_mode, find_combo_root_for_authorization,
        gap_close_activation_satisfied, gap_close_destination_within_epsilon,
        gap_close_has_horizontal_travel, gap_close_pre_commit_decision,
        gap_close_target_facing_satisfied, inactive_conditional_gap_close_range,
        melee_channel_movement_canceled, melee_channel_tick_delays,
        melee_hit_volume_contains_player, melee_impact_delays, melee_manifest,
        melee_target_impact_point_y, pending_commitment_belongs_to_channel,
        pending_melee_impact_range, positive_projectile_override,
        projectile_max_distance_for_policy, push_flaming_weapon_effects,
        push_melee_impact_status_effects, push_stagger_effect_with_duration_if_applicable,
        resolve_gap_close_destination, resolve_melee_action_reference,
        resolve_melee_action_reference_in_strikes, resolved_hit_window_damages,
        scaled_auto_attack_cadence_ms, scaled_impact_area_damage, scheduled_melee_impact_at,
        strike_total_duration_ms, timed_melee_movement_destination, yaw_toward_xz,
        AerialExecutionMode, AirborneTargetingMode, ComboInputDecision, GapCloseActorSnapshot,
        GapClosePreCommitDecision, MeleeAuthorization, PendingMeleeImpact,
        ResolvedMeleeAttackModifiers, ResolvedMeleeGapClose, ResolvedMeleeTargeting, SpellVec3,
        StaggerDirection, StrikeData, StrikeHitWindowData, GAP_CLOSE_COLLISION_REQUIRE_CLEAR_PATH,
        GAP_CLOSE_DESTINATION_BEHIND_TARGET, GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT,
        GAP_CLOSE_KIND_LINEAR, GAP_CLOSE_KIND_TELEPORT_BEHIND_TARGET_DISABLED, MELEE_MANIFEST_JSON,
        MELEE_TARGET_FACING_ARC_RADIANS,
    };
    use crate::action_ids::{AuthoredActionId, RuntimeActionId};
    use crate::animation_set_test_utils::animation_set_assets_by_combat_profile;
    use crate::combat::actor_snapshot::CombatActorSnapshot;
    use crate::combat::scene_query::{is_direction_within_facing_arc, target_within_area_range_xz};
    use crate::combat::{
        DamageType, EffectPacket, StackPolicy, StatusDispelType, StatusPayload, StatusPolarity,
    };
    use crate::player::{DEFAULT_COMBAT_PROFILE, TWO_HANDED_SWORD_COMBAT_PROFILE};
    use crate::player_state::PlayerState;
    use crate::progression::{MeleeChannelRuntime, MeleeFireOnHitRuntime, MeleeGapCloseCatalog};

    const TEST_GAP_CLOSE_DESTINATION_EPSILON_METERS: f32 = 0.10;

    fn test_actor_snapshot(identity: Identity, pos_x: f32, pos_z: f32) -> CombatActorSnapshot {
        CombatActorSnapshot {
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
        width: f32,
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
            damage_type: crate::combat::DamageType::Physical.as_str().to_string(),
            target_health_damage_scaling_min_multiplier: 1.0,
            target_health_damage_scaling_max_multiplier: 1.0,
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
            target_audience: String::new(),
            requires_present_time_facing: false,
            present_time_facing_arc_radians: 0.0,
            requires_present_time_los: false,
            impact_event_max_distance: 0.0,
            direct_action_key: String::new(),
            view_delay_micros: 0,
            resolve_at_micros: 0,
            targeting_width: width,
        }
    }

    #[test]
    fn player_pending_melee_fixture_keeps_server_actor_gates_disabled() {
        let row = test_targetless_impact_row("TARGET", 2.5, 0.0, 0.0);

        assert!(row.target_audience.is_empty());
        assert!(!row.requires_present_time_facing);
        assert_eq!(row.present_time_facing_arc_radians, 0.0);
        assert!(!row.requires_present_time_los);
        assert_eq!(row.impact_event_max_distance, 0.0);
        assert!(row.direct_action_key.is_empty());
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
            assert!(
                !profile.auto_attack_sequence.is_empty(),
                "combat profile {} must author a visual auto-attack sequence",
                profile.combat_profile
            );
            if profile.auto_attack_sequence.len() > 1 {
                assert!(
                    profile.auto_attack_sequence_interval_ms > 0,
                    "multi-strike auto-attack sequence for profile {} must author a positive sequence interval",
                    profile.combat_profile
                );
            }
            let mut auto_attack_sequence_ids = HashSet::new();
            for (sequence_index, action_id) in profile.auto_attack_sequence.iter().enumerate() {
                assert!(
                    auto_attack_sequence_ids.insert(action_id.clone()),
                    "auto-attack sequence for profile {} repeats strike {}",
                    profile.combat_profile,
                    action_id
                );
                assert!(
                    profile.strikes.iter().any(|strike| strike.id == *action_id),
                    "auto-attack sequence strike {} for profile {} must exist in the manifest",
                    action_id,
                    profile.combat_profile
                );
                if sequence_index > 0 {
                    assert!(
                        auto_attack_sequence_step_for_profile(
                            profile.combat_profile.as_str(),
                            sequence_index
                        )
                        .is_some(),
                        "auto-attack sequence strike {} for profile {} must chain from its predecessor and use the profile sequence interval",
                        action_id,
                        profile.combat_profile
                    );
                }
            }
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
    fn dagger_downward_slash_is_a_direct_root_strike() {
        let profile = melee_manifest()
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == "DAGGERS")
            .expect("DAGGERS melee profile must exist");
        let strike = profile
            .strikes
            .iter()
            .find(|strike| strike.id == "DAGGER_DOWNWARD_SLASH")
            .expect("DAGGER_DOWNWARD_SLASH strike must exist");

        assert_eq!(canonical_slot_id(strike), "dagger_downward_slash_slot");
        assert!(
            strike.combo_from.as_deref().unwrap_or("").trim().is_empty(),
            "Downward Slash must be directly castable rather than requiring the auto-attack opener"
        );
    }

    #[test]
    fn data_preserving_republish_workflows_publish_melee_definitions() {
        for (relative_path, source) in [
            (
                "ops/republish-local-clear.sh",
                include_str!("../../ops/republish-local-clear.sh"),
            ),
            (
                "ops/republish-catalog.sh",
                include_str!("../../ops/republish-catalog.sh"),
            ),
        ] {
            assert!(
                source.contains("spacetime call \"$ARENA_DATABASE\" publish_melee_definitions"),
                "{relative_path} must publish melee definitions after a data-preserving publish"
            );
        }
    }

    #[test]
    fn combo_successor_strikes_reachable_from_melee_roots_have_gameplay_rows() {
        let catalog: Value =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("catalog json must parse");
        let abilities = catalog
            .get("abilities")
            .and_then(Value::as_array)
            .expect("abilities must be an array");

        let mut melee_actions_by_profile: HashMap<String, HashSet<String>> = HashMap::new();
        let mut action_bar_roots_by_profile: HashMap<String, HashSet<String>> = HashMap::new();
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

            let profile_id = ability
                .get("combat_profile_id")
                .and_then(Value::as_str)
                .map(AuthoredActionId::new)
                .expect("melee ability must declare combat_profile_id")
                .into_string();
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

            let is_action_bar_root = ability
                .get("ability_tags")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(Value::as_str)
                .any(|tag| tag.eq_ignore_ascii_case("ACTION_BAR_ACTION"));
            if is_action_bar_root {
                action_bar_roots_by_profile
                    .entry(profile_id.clone())
                    .or_default()
                    .insert(action_id);
            }
        }

        for profile in &melee_manifest().profiles {
            let action_bar_roots = action_bar_roots_by_profile
                .get(&profile.combat_profile)
                .unwrap_or_else(|| {
                    panic!(
                        "combat profile {} must have at least one action-bar melee root",
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
                if !action_bar_roots.contains(&root) {
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

        assert_eq!(
            execute_not_before_ms, predecessor_total_ms,
            "the successor should execute when its predecessor's final hit and recovery complete"
        );
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

    fn parse_auto_attack_sequence_from_animation_set_asset(asset_contents: &str) -> Vec<String> {
        let mut sequence = Vec::new();
        let mut in_sequence = false;
        for line in asset_contents.lines() {
            if line == "  autoAttackVisualSequenceActionIds:" {
                in_sequence = true;
                continue;
            }
            if !in_sequence {
                continue;
            }
            if let Some(value) = line.strip_prefix("  - ") {
                let action_id = AuthoredActionId::new(value).into_string();
                if !action_id.is_empty() {
                    sequence.push(action_id);
                }
                continue;
            }
            break;
        }
        sequence
    }

    fn parse_auto_attack_sequence_interval_from_animation_set_asset(
        asset_contents: &str,
    ) -> Option<u64> {
        asset_contents
            .lines()
            .find_map(|line| line.strip_prefix("  autoAttackSequenceIntervalMs: "))
            .and_then(|value| value.trim().parse().ok())
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
            assert_eq!(
                profile.auto_attack_sequence,
                parse_auto_attack_sequence_from_animation_set_asset(asset_contents.as_str()),
                "combat profile '{}' auto-attack visual sequence drifted from its animation-set asset",
                profile.combat_profile
            );
            assert_eq!(
                Some(profile.auto_attack_sequence_interval_ms),
                parse_auto_attack_sequence_interval_from_animation_set_asset(
                    asset_contents.as_str()
                ),
                "combat profile '{}' auto-attack sequence interval drifted from its animation-set asset",
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
    fn melee_impact_effects_emit_cataclysm_stun_status_packets() {
        let source = test_identity_with_byte(1);
        let target = test_identity_with_byte(2);
        let now = Timestamp::UNIX_EPOCH;
        let row = PendingMeleeImpact {
            impact_id: 0,
            source,
            event_source: "test".to_string(),
            target,
            spell_id: "test:cataclysm".to_string(),
            kind: "CATACLYSM".to_string(),
            ability_id: "WARRIOR_CATACLYSM".to_string(),
            hit_index: 0,
            damage: 35,
            damage_type: crate::combat::DamageType::Physical.as_str().to_string(),
            target_health_damage_scaling_min_multiplier: 1.0,
            target_health_damage_scaling_max_multiplier: 1.0,
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
            target_audience: String::new(),
            requires_present_time_facing: false,
            present_time_facing_arc_radians: 0.0,
            requires_present_time_los: false,
            impact_event_max_distance: 0.0,
            direct_action_key: String::new(),
            view_delay_micros: 0,
            resolve_at_micros: 0,
            targeting_width: 0.0,
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
            panic!("expected Cataclysm stun apply-status effect");
        };
        assert_eq!(*effect_source, source);
        assert_eq!(*effect_target, target);
        assert_eq!(*payload, StatusPayload::Stun);
        assert_eq!(*polarity, crate::combat::StatusPolarity::Debuff);
        assert_eq!(*duration, Duration::from_millis(2000));
        assert_eq!(stack_group, "WARRIOR_CATACLYSM_STUN");
        assert_eq!(*max_stacks, 1);
    }

    #[test]
    fn melee_attack_modifier_bleed_uses_confirmed_hit_damage_and_stacks_by_hit() {
        let source = test_identity_with_byte(1);
        let target = test_identity_with_byte(2);
        let now = Timestamp::UNIX_EPOCH;
        let row = PendingMeleeImpact {
            impact_id: 0,
            source,
            event_source: "test".to_string(),
            target,
            spell_id: "test:serrated".to_string(),
            kind: "SWORD_AND_SHIELD_LIGHT_COMBO_1".to_string(),
            ability_id: "PALADIN_SHIELD_PUMMEL".to_string(),
            hit_index: 2,
            damage: 35,
            damage_type: DamageType::Physical.as_str().to_string(),
            target_health_damage_scaling_min_multiplier: 1.0,
            target_health_damage_scaling_max_multiplier: 1.0,
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
            target_audience: String::new(),
            requires_present_time_facing: false,
            present_time_facing_arc_radians: 0.0,
            requires_present_time_los: false,
            impact_event_max_distance: 0.0,
            direct_action_key: String::new(),
            view_delay_micros: 0,
            resolve_at_micros: 0,
            targeting_width: 0.0,
        };
        let modifiers = ResolvedMeleeAttackModifiers {
            bleed_damage_ratio: 0.10,
            ..Default::default()
        };

        let mut effects = Vec::new();
        super::push_melee_attack_modifier_bleed_effects(&mut effects, &row, 35, &modifiers);

        assert_eq!(super::melee_attack_modifier_bleed_damage(35, 0.10), 4);
        assert_eq!(effects.len(), 1);
        let EffectPacket::ApplyStatus {
            source: effect_source,
            target: effect_target,
            spell_id,
            payload,
            polarity,
            duration,
            stack_group,
            max_stacks,
            stack_policy,
            dispel_types,
            ..
        } = &effects[0]
        else {
            panic!("expected Serrated Blades bleed apply-status effect");
        };
        assert_eq!(*effect_source, source);
        assert_eq!(*effect_target, target);
        assert_eq!(spell_id, "test:serrated:serrated_blades_bleed:2");
        assert_eq!(
            *payload,
            StatusPayload::Dot {
                tick_damage: 4,
                damage_type: DamageType::Physical,
                tick_interval: Duration::from_secs(1),
            }
        );
        assert_eq!(*polarity, StatusPolarity::Debuff);
        assert_eq!(*duration, Duration::from_secs(2));
        assert_eq!(stack_group, "SERRATED_BLADES_BLEED:test:serrated:2");
        assert_eq!(*max_stacks, 1);
        assert_eq!(*stack_policy, StackPolicy::Refresh);
        assert_eq!(dispel_types, &vec![StatusDispelType::Bleed]);
    }

    #[test]
    fn flaming_weapon_adds_direct_fire_damage_and_source_scoped_burning_stack() {
        let source = test_identity_with_byte(1);
        let target = test_identity_with_byte(2);
        let now = Timestamp::UNIX_EPOCH;
        let row = PendingMeleeImpact {
            impact_id: 0,
            source,
            event_source: "test".to_string(),
            target,
            spell_id: "test:flaming_weapon".to_string(),
            kind: "SWORD_AND_SHIELD_LIGHT_COMBO_1".to_string(),
            ability_id: "PALADIN_SHIELD_PUMMEL".to_string(),
            hit_index: 2,
            damage: 35,
            damage_type: DamageType::Physical.as_str().to_string(),
            target_health_damage_scaling_min_multiplier: 1.0,
            target_health_damage_scaling_max_multiplier: 1.0,
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
            target_audience: String::new(),
            requires_present_time_facing: false,
            present_time_facing_arc_radians: 0.0,
            requires_present_time_los: false,
            impact_event_max_distance: 0.0,
            direct_action_key: String::new(),
            view_delay_micros: 0,
            resolve_at_micros: 0,
            targeting_width: 0.0,
        };
        let tuning = MeleeFireOnHitRuntime {
            bonus_damage: 5,
            burn_duration: Duration::from_secs(5),
            burn_tick_interval: Duration::from_secs(1),
            burn_tick_damage: 1,
            burn_max_stacks: 5,
            burn_status_stack_group: "FLAMING_WEAPON_BURN".to_string(),
            burn_dispel_types: vec![StatusDispelType::Magic],
        };

        let mut effects = Vec::new();
        push_flaming_weapon_effects(&mut effects, &row, target, Some(&tuning));

        assert_eq!(effects.len(), 2);
        let EffectPacket::Damage {
            amount,
            damage_type,
            source: effect_source,
            target: effect_target,
            delivery,
            source_kind,
            ..
        } = &effects[0]
        else {
            panic!("expected Flaming Weapon fire damage effect");
        };
        assert_eq!(*amount, 5);
        assert_eq!(*damage_type, DamageType::Fire);
        assert_eq!(*effect_source, source);
        assert_eq!(*effect_target, target);
        assert_eq!(*delivery, crate::combat::DamageDelivery::Direct);
        assert_eq!(source_kind, crate::combat::DAMAGE_SOURCE_KIND_MELEE);

        let EffectPacket::ApplyStatus {
            source: effect_source,
            target: effect_target,
            payload,
            polarity,
            duration,
            stack_group,
            max_stacks,
            stack_policy,
            dispel_types,
            ..
        } = &effects[1]
        else {
            panic!("expected Flaming Weapon Burning effect");
        };
        assert_eq!(*effect_source, source);
        assert_eq!(*effect_target, target);
        assert_eq!(
            *payload,
            StatusPayload::Dot {
                tick_damage: 1,
                damage_type: DamageType::Fire,
                tick_interval: Duration::from_secs(1),
            }
        );
        assert_eq!(*polarity, StatusPolarity::Debuff);
        assert_eq!(*duration, Duration::from_secs(5));
        assert_eq!(
            stack_group,
            &format!("FLAMING_WEAPON_BURN:{}", source.to_hex())
        );
        assert_eq!(*max_stacks, 5);
        assert_eq!(*stack_policy, StackPolicy::AddStackRefresh);
        assert_eq!(dispel_types, &vec![StatusDispelType::Magic]);
    }

    #[test]
    fn branded_status_group_match_accepts_instance_scoped_dot_rows() {
        assert!(super::status_stack_group_matches_base(
            "PALADIN_BRANDED",
            "PALADIN_BRANDED"
        ));
        assert!(super::status_stack_group_matches_base(
            "PALADIN_BRANDED:test:rebuke:0",
            "PALADIN_BRANDED"
        ));
        assert!(!super::status_stack_group_matches_base(
            "PALADIN_BRANDED_EXTRA:test",
            "PALADIN_BRANDED"
        ));
        assert!(!super::status_stack_group_matches_base(
            "OTHER_HOLY_DOT:test",
            "PALADIN_BRANDED"
        ));
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
    fn melee_channel_ticks_repeat_through_authored_duration() {
        assert_eq!(
            melee_channel_tick_delays(MeleeChannelRuntime {
                duration_ms: 2500,
                first_tick_delay_ms: 44,
                tick_interval_ms: 333,
                cancel_on_movement: true,
                use_authored_hit_windows: false,
                holdable: false,
                resource_cost_per_release: 0.0,
                resource_kind_per_release: "",
            }),
            vec![44, 377, 710, 1043, 1376, 1709, 2042, 2375]
        );
        assert_eq!(
            melee_channel_tick_delays(MeleeChannelRuntime {
                duration_ms: 3000,
                first_tick_delay_ms: 107,
                tick_interval_ms: 667,
                cancel_on_movement: true,
                use_authored_hit_windows: false,
                holdable: false,
                resource_cost_per_release: 0.0,
                resource_kind_per_release: "",
            }),
            vec![107, 774, 1441, 2108, 2775]
        );
    }

    #[test]
    fn movement_cancelable_sequence_can_use_authored_hit_windows() {
        let strike = StrikeData {
            id: "TEST_AUTHORED_SEQUENCE".to_string(),
            slot_id: "utility_1".to_string(),
            hit_windows: vec![
                StrikeHitWindowData {
                    impact_delay_ms: 320,
                },
                StrikeHitWindowData {
                    impact_delay_ms: 1503,
                },
                StrikeHitWindowData {
                    impact_delay_ms: 2743,
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

        assert_eq!(
            melee_impact_delays(
                &strike,
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
            ),
            vec![320, 1503, 2743]
        );
    }

    #[test]
    fn melee_channel_movement_cancel_requires_a_voluntary_epoch_change() {
        assert!(!melee_channel_movement_canceled(true, 7, 7));
        assert!(melee_channel_movement_canceled(true, 7, 8));
        assert!(!melee_channel_movement_canceled(false, 7, 8));
    }

    #[test]
    fn channel_cancellation_matches_only_the_same_owner_and_action_instance() {
        let owner = test_identity_with_byte(0x11);
        let other_owner = test_identity_with_byte(0x22);

        assert!(pending_commitment_belongs_to_channel(
            owner,
            "triple-shot:7",
            owner,
            "triple-shot:7"
        ));
        assert!(!pending_commitment_belongs_to_channel(
            owner,
            "triple-shot:7",
            other_owner,
            "triple-shot:7"
        ));
        assert!(!pending_commitment_belongs_to_channel(
            owner,
            "triple-shot:7",
            owner,
            "triple-shot:8"
        ));
    }

    #[test]
    fn target_health_damage_scaling_increases_as_target_health_drops() {
        assert_eq!(
            super::scaled_melee_damage_for_health(36, 100, 100, 1.0, 2.0),
            36
        );
        assert_eq!(
            super::scaled_melee_damage_for_health(36, 50, 100, 1.0, 2.0),
            54
        );
        assert_eq!(
            super::scaled_melee_damage_for_health(36, 0, 100, 1.0, 2.0),
            72
        );
        assert_eq!(
            super::scaled_melee_damage_for_health(36, -10, 100, 1.0, 2.0),
            72
        );
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

        assert!((modifiers.effective_range(2.5, true) - 5.0).abs() < 0.0001);
        assert!((modifiers.effective_range(5.5, true) - 5.5).abs() < 0.0001);
    }

    #[test]
    fn gigantism_adds_flat_reach_only_to_non_gap_close_melee() {
        let mut modifiers = ResolvedMeleeAttackModifiers::default();
        modifiers.range_bonus = super::GIGANTISM_MELEE_RANGE_BONUS_METERS;

        assert!((modifiers.effective_range(2.5, true) - 4.0).abs() < 0.0001);
        assert!((modifiers.effective_range(2.5, false) - 2.5).abs() < 0.0001);
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
            projectile_max_distance_for_policy(35.0, 18.0, MeleeAuthorization::ActionBar),
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
            super::MeleeExecutionPolicy::INTRINSIC_FLURRY_AUTO_ATTACK
                .grants_primary_resource_on_hit
        );
        assert!(
            !super::MeleeExecutionPolicy::INTRINSIC_AUTO_ATTACK_REPLACEMENT
                .grants_primary_resource_on_hit
        );
        assert!(!super::MeleeExecutionPolicy::PLAYER_INPUT.grants_primary_resource_on_hit);
        assert!(!super::MeleeExecutionPolicy::QUEUED_FOLLOWUP.grants_primary_resource_on_hit);
        assert!(!super::MeleeExecutionPolicy::PRACTICE.grants_primary_resource_on_hit);
    }

    #[test]
    fn flurry_auto_attacks_publish_ghost_presentation_metadata() {
        assert_eq!(
            super::MeleeExecutionPolicy::INTRINSIC_FLURRY_AUTO_ATTACK.presentation_metadata_kind,
            "FLURRY_PROC"
        );
        assert!(super::MeleeExecutionPolicy::INTRINSIC_AUTO_ATTACK
            .presentation_metadata_kind
            .is_empty());
    }

    #[test]
    fn mode_auto_attack_lookup_falls_back_to_shared_profile_row() {
        let action_id = AuthoredActionId::new("AUTO_ATTACK_1");
        let (mode_key, profile_key) =
            auto_attack_catalog_resolution_keys("DAGGERS", "READY", &action_id);

        assert_eq!(mode_key.as_deref(), Some("DAGGERS:READY:AUTO_ATTACK_1"));
        assert_eq!(profile_key, "DAGGERS:AUTO_ATTACK_1");
    }

    #[test]
    fn dagger_auto_attack_sequence_uses_profile_sequence_interval() {
        let step = auto_attack_sequence_step_for_profile("DAGGERS", 1)
            .expect("Dagger auto-attack should author a second sequence strike");

        assert_eq!(step.strike_id.as_str(), "DAGGER_COMBO_ATTACK_01_02");
        assert_eq!(step.transition_delay_ms, 500);
        assert!(!step.has_successor);
        assert!(auto_attack_sequence_step_for_profile("DAGGERS", 2).is_none());
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
    fn every_archer_bow_strike_delivers_a_projectile() {
        let profile = melee_manifest()
            .profiles
            .iter()
            .find(|profile| profile.combat_profile == "ARCHER_BOW")
            .expect("ARCHER_BOW profile should exist");
        let strike_ids: HashSet<_> = profile
            .strikes
            .iter()
            .map(|strike| strike.id.as_str())
            .collect();
        let projectile_strikes: HashSet<_> = profile
            .strikes
            .iter()
            .filter(|strike| strike.projectile.is_some())
            .map(|strike| strike.id.as_str())
            .collect();

        assert!(strike_ids.contains("AUTO_ATTACK_1"));
        assert_eq!(strike_ids, projectile_strikes);
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
    fn conditional_gap_close_only_activates_for_a_disabled_target() {
        let mut gap = test_gap_close(GAP_CLOSE_DESTINATION_BEHIND_TARGET);
        gap.kind = GAP_CLOSE_KIND_TELEPORT_BEHIND_TARGET_DISABLED.to_string();

        assert!(!gap_close_activation_satisfied(&gap, false));
        assert!(gap_close_activation_satisfied(&gap, true));
        assert_eq!(inactive_conditional_gap_close_range(12.0, &gap), 2.5);
    }

    #[test]
    fn unconditional_gap_close_preserves_existing_activation() {
        let gap = test_gap_close(GAP_CLOSE_DESTINATION_NEAREST_CONTACT_POINT);

        assert!(gap_close_activation_satisfied(&gap, false));
        assert!(gap_close_activation_satisfied(&gap, true));
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
    fn coup_de_grace_destination_facing_points_back_to_target() {
        let target = gap_actor(4.0, 10.0, std::f32::consts::FRAC_PI_2, 0.5);
        let gap = test_gap_close(GAP_CLOSE_DESTINATION_BEHIND_TARGET);
        let destination =
            resolve_gap_close_destination(&gap, gap_actor(0.0, 0.0, 0.0, 0.5), target)
                .expect("destination should resolve");

        let facing_yaw = yaw_toward_xz(
            destination.x,
            destination.z,
            target.pos_x,
            target.pos_z,
            0.0,
        );

        assert!((facing_yaw - target.yaw).abs() < 0.001);
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
        let caster = test_actor_snapshot(test_identity_with_byte(1), 0.0, 0.0);
        let row = test_targetless_impact_row("CASTER_RADIUS", 3.25, 0.0, 0.0);
        let inside = test_actor_snapshot(test_identity_with_byte(2), 0.0, 3.0);
        let outside = test_actor_snapshot(test_identity_with_byte(3), 0.0, 4.0);

        assert!(melee_hit_volume_contains_player(&row, &caster, &inside));
        assert!(!melee_hit_volume_contains_player(&row, &caster, &outside));
    }

    #[test]
    fn targetless_cone_melee_hit_volume_uses_caster_facing() {
        let caster = test_actor_snapshot(test_identity_with_byte(1), 0.0, 0.0);
        let row = test_targetless_impact_row("CASTER_CONE", 4.0, 60.0, 0.0);
        let front = test_actor_snapshot(test_identity_with_byte(2), 0.0, 3.0);
        let side = test_actor_snapshot(test_identity_with_byte(3), 3.0, 0.0);

        assert!(melee_hit_volume_contains_player(&row, &caster, &front));
        assert!(!melee_hit_volume_contains_player(&row, &caster, &side));
    }

    #[test]
    fn targetless_rectangle_melee_hit_volume_uses_caster_facing_and_authored_width() {
        let caster = test_actor_snapshot(test_identity_with_byte(1), 0.0, 0.0);
        let row = test_targetless_impact_row("CASTER_RECTANGLE", 5.0, 0.0, 1.25);
        let front = test_actor_snapshot(test_identity_with_byte(2), 0.0, 4.5);
        let edge_overlap = test_actor_snapshot(test_identity_with_byte(3), 0.9, 3.0);
        let side = test_actor_snapshot(test_identity_with_byte(4), 1.1, 3.0);
        let behind = test_actor_snapshot(test_identity_with_byte(5), 0.0, -0.6);

        assert!(melee_hit_volume_contains_player(&row, &caster, &front));
        assert!(melee_hit_volume_contains_player(
            &row,
            &caster,
            &edge_overlap
        ));
        assert!(!melee_hit_volume_contains_player(&row, &caster, &side));
        assert!(!melee_hit_volume_contains_player(&row, &caster, &behind));
    }

    #[test]
    fn targetless_melee_los_is_authored_without_changing_targeted_impact_rechecks() {
        let rectangle = ResolvedMeleeTargeting::CasterRectangle {
            length: 5.0,
            width: 1.25,
        };

        assert!(rectangle.pending_requires_present_time_los(true));
        assert!(!rectangle.pending_requires_present_time_los(false));
        assert!(!ResolvedMeleeTargeting::Target.pending_requires_present_time_los(true));
    }

    #[test]
    fn melee_impact_point_uses_target_capsule_center() {
        let mut target = test_actor_snapshot(test_identity_with_byte(2), 0.0, 0.0);
        target.pos_y = 3.0;
        target.hit_height = 2.0;

        assert_eq!(melee_target_impact_point_y(&target), 4.0);
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

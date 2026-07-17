use std::collections::{HashMap, HashSet};
use std::sync::OnceLock;
use std::time::Duration;

use serde::Deserialize;
use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::open_world_scene_name_for_identity;
use crate::arena::{resolve_player_world_context, world_contexts_share, ResolvedWorldContext};
use crate::combat::actor_snapshot::CombatActorSnapshotSet;
#[allow(unused_imports)]
use crate::combat::status_effect as _;
use crate::combat::{is_in_combat, timestamp_to_micros, MovementModifiers};
use crate::inventory::{clear_loot_for_anchor, corpse_loot_has_items};
use crate::melee::{
    clear_pending_melee_impacts_for_source, commit_server_actor_targeted_melee,
    interrupt_server_actor_melee_commitments, pending_melee_commitment_target_for_source,
    resolve_due_pending_melee_impacts_for_event_source, ServerActorMeleeCommitment,
};
use crate::movement::FIXED_TICK_SECONDS;
use crate::practice::is_training_instance;
use crate::progression::{ability_catalog as _, MeleeAbilityCatalog};
use crate::relations::{can_harm, combat_relation, CombatRelation, TargetAudience};
#[allow(unused_imports)]
use crate::spells::active_cast as _;
use crate::spells::{
    cast_spell_for_server_actor, clear_actor_cooldowns, is_on_named_cooldown, is_on_spell_cooldown,
    spell_definition_by_str, stamp_named_cooldown_for_duration, SpellId,
};
use crate::world_collision::{
    resolve_world_horizontal_sweep_collision_y_with_layout_for_scene,
    surface_height_for_world_at_y_with_layout_for_scene,
};
use crate::world_obstacles::resolve_active_world_obstacle_movement;

#[allow(unused_imports)]
use crate::arena::arena_instance as _;
#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::combat::combat_event as _;
#[allow(unused_imports)]
use crate::npcs::npc_combat_runtime as _;
#[allow(unused_imports)]
use crate::npcs::npc_decision_debug as _;
#[allow(unused_imports)]
use crate::npcs::npc_forced_movement as _;
#[allow(unused_imports)]
use crate::npcs::npc_instance as _;
#[allow(unused_imports)]
use crate::npcs::npc_physics as _;
#[allow(unused_imports)]
use crate::npcs::npc_return_home as _;
#[allow(unused_imports)]
use crate::npcs::npc_spawn_counter as _;
#[allow(unused_imports)]
use crate::npcs::npc_state as _;
#[allow(unused_imports)]
use crate::npcs::npc_target_override as _;
#[allow(unused_imports)]
use crate::npcs::npc_threat as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::progression::melee_ability_catalog as _;

pub(crate) const NPC_FACTION_HOSTILE: &str = "HOSTILE";
pub(crate) const NPC_FACTION_NEUTRAL: &str = "NEUTRAL";
pub(crate) const NPC_FACTION_FRIENDLY: &str = "FRIENDLY";

pub(crate) const NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD: &str =
    "KOBOLD_WARRIOR_RD_SWORD_SHIELD";
pub(crate) const NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR: &str = "KOBOLD_WARRIOR_GN_SPEAR";
pub(crate) const NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD: &str = "KOBOLD_THIEF_BK_DUAL_SWORD";
pub(crate) const NPC_TEMPLATE_KOBOLD_KNIGHT_RD_SWORD_SHIELD: &str = "KOBOLD_KNIGHT_RD_SWORD_SHIELD";

const WORLD_KIND_OPEN: &str = "OPEN";
const WORLD_KIND_INSTANCE: &str = "INSTANCE";
const NPC_SPAWN_FORWARD: f32 = 2.5;
const NPC_ID_MAGIC: u128 = 0x6172_656e_6132_5f6e_7063_0000_0000_0001;
const NPC_MELEE_SOURCE_KIND: &str = "NPC_MELEE";
const NPC_MELEE_ACTION_KIND: &str = "NPC_MELEE_ATTACK";
const NPC_MELEE_PARRY_BEHAVIOR: &str = "PARRYABLE";
const NPC_MELEE_BLOCK_BEHAVIOR: &str = "BLOCKABLE";
const DEFAULT_NPC_ATTACK_RECOVERY_MS: u64 = 500;
const MAX_NPC_ATTACK_RECOVERY_MS: u64 = 10_000;
const NPC_CHASE_COLLISION_STEP: f32 = 0.35;
const NPC_CHASE_STOP_EPSILON: f32 = 0.05;
const NPC_FACE_EPSILON: f32 = 0.001;
const NPC_LOOTED_CORPSE_DESPAWN_DELAY: Duration = Duration::from_secs(8);
const NPC_UNLOOTED_CORPSE_DESPAWN_DELAY: Duration = Duration::from_secs(60);
const NPC_CATALOG_JSON: &str = include_str!("npc_catalog.shared.json");

#[table(accessor = npc_template_catalog, public)]
pub struct NpcTemplateCatalog {
    #[primary_key]
    pub template_id: String,
    #[index(btree)]
    pub species_id: String,
    pub display_name: String,
    pub default_visual_id: String,
    pub brain_profile_id: String,
    pub resource_policy: String,
    pub max_hp: i32,
    pub hit_radius: f32,
    pub hit_height: f32,
}

#[table(accessor = npc_visual_catalog, public)]
pub struct NpcVisualCatalog {
    #[primary_key]
    pub visual_id: String,
    #[index(btree)]
    pub template_id: String,
}

#[table(accessor = npc_action_kit_catalog, public)]
pub struct NpcActionKitCatalog {
    #[primary_key]
    pub entry_id: String,
    #[index(btree)]
    pub template_id: String,
    #[index(btree)]
    pub ability_id: String,
    pub role: String,
    pub target_selector: String,
    pub base_utility: f32,
    pub min_self_health_pct: f32,
    pub max_self_health_pct: f32,
    pub preferred_min_distance: f32,
    pub preferred_max_distance: f32,
    pub min_nearby_allies: u32,
    pub min_nearby_enemies: u32,
    pub required_target_status: String,
    pub forbidden_target_status: String,
    pub movement_may_enable: bool,
    pub sort_order: u32,
    #[default(0u64)]
    pub windup_ms: u64,
}

#[table(accessor = npc_brain_catalog, public)]
pub struct NpcBrainCatalog {
    #[primary_key]
    pub brain_profile_id: String,
    pub decision_interval_ms: u64,
    pub perception_radius: f32,
    pub leash_radius: f32,
    pub target_stickiness: f32,
    pub preferred_min_distance: f32,
    pub preferred_max_distance: f32,
    pub retreat_tolerance: f32,
    pub support_urgency: f32,
    pub deterministic_variation: f32,
    pub damage_threat_weight: f32,
    pub healing_threat_weight: f32,
    pub proximity_threat_weight: f32,
    pub idle_policy: String,
    pub assist_policy: String,
}

#[table(accessor = npc_instance, public)]
#[derive(Clone)]
pub struct NpcInstance {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub spawned_by: Identity,
    #[index(btree)]
    pub template_id: String,
    #[index(btree)]
    pub visual_id: String,
    pub species_id: String,
    #[index(btree)]
    pub faction: String,
    #[index(btree)]
    pub combat_team_id: String,
    pub display_name: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    pub open_world_scene_name: String,
    pub home_x: f32,
    pub home_y: f32,
    pub home_z: f32,
    pub spawned_at: Timestamp,
}

#[table(accessor = npc_state, public)]
#[derive(Clone)]
pub struct NpcState {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub alive: bool,
    pub hp: i32,
    pub max_hp: i32,
    pub hit_radius: f32,
    pub hit_height: f32,
}

#[table(accessor = npc_physics, public)]
#[derive(Clone)]
pub struct NpcPhysics {
    #[primary_key]
    pub identity: Identity,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub yaw: f32,
    pub updated_at: Timestamp,
}

#[table(accessor = npc_spawn_counter)]
pub struct NpcSpawnCounter {
    #[primary_key]
    pub owner: Identity,
    pub next_sequence: u64,
}

#[table(accessor = npc_combat_runtime)]
#[derive(Clone, PartialEq)]
pub struct NpcCombatRuntime {
    #[primary_key]
    pub identity: Identity,
    pub target: Identity,
    pub planned_ability_id: String,
    pub decision_sequence: u64,
    pub next_decision_at: Timestamp,
    pub next_decision_at_micros: i64,
    #[default(0i64)]
    pub hold_movement_until_micros: i64,
}

#[table(accessor = npc_target_override)]
pub struct NpcTargetOverride {
    #[primary_key]
    pub identity: Identity,
    pub target: Identity,
    pub set_by: Identity,
    pub updated_at: Timestamp,
}

#[table(accessor = npc_return_home)]
pub struct NpcReturnHome {
    #[primary_key]
    pub identity: Identity,
    pub started_at: Timestamp,
}

#[table(accessor = npc_forced_movement)]
#[derive(Clone)]
pub struct NpcForcedMovement {
    #[primary_key]
    pub identity: Identity,
    pub started_at: Timestamp,
    pub duration_ms: u64,
    pub start_x: f32,
    pub start_y: f32,
    pub start_z: f32,
    pub end_x: f32,
    pub end_y: f32,
    pub end_z: f32,
}

#[table(accessor = npc_decision_debug)]
pub struct NpcDecisionDebug {
    #[primary_key]
    pub identity: Identity,
    pub decision_sequence: u64,
    pub considered_action_count: u32,
    pub chosen_ability_id: String,
    pub chosen_target: Identity,
    pub target_was_pinned: bool,
    pub score_summary: String,
    pub hard_reject_summary: String,
    pub threat_summary: String,
    pub updated_at: Timestamp,
}

#[table(accessor = npc_threat)]
pub struct NpcThreat {
    #[primary_key]
    pub threat_key: String,
    #[index(btree)]
    pub npc_identity: Identity,
    #[index(btree)]
    pub source_identity: Identity,
    pub damage_threat: f32,
    pub updated_at: Timestamp,
}

#[table(accessor = npc_despawn)]
pub struct NpcDespawn {
    #[primary_key]
    pub identity: Identity,
    pub despawn_at: Timestamp,
    #[index(btree)]
    pub despawn_at_micros: i64,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct NpcTemplate {
    pub template_id: String,
    pub species_id: String,
    pub display_name: String,
    pub visual_ids: Vec<String>,
    pub brain_profile_id: String,
    pub resource_policy: String,
    pub action_kit: Vec<NpcActionKitEntry>,
    pub max_hp: i32,
    pub hit_radius: f32,
    pub hit_height: f32,
    #[serde(default)]
    pub knockback_resistance: f32,
    pub aggro_radius: f32,
    pub move_speed: f32,
    /// Authored telegraph (S3): delay between the CAST event (swing start,
    /// what the victim's screen shows) and damage resolution. Must stay well
    /// above the victim's render delay (~100-166 ms) or the telegraph reads
    /// as nothing; design floor is 300 ms.
    pub attack_windup_ms: u64,
    /// Default follow-through after impact/release during which movement stays
    /// planted. Individual action-kit rows can override this value.
    #[serde(default = "default_npc_attack_recovery_ms")]
    pub attack_recovery_ms: u64,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct NpcActionKitEntry {
    pub ability_id: String,
    pub role: String,
    pub target_selector: String,
    pub base_utility: f32,
    pub min_self_health_pct: f32,
    pub max_self_health_pct: f32,
    pub preferred_min_distance: f32,
    pub preferred_max_distance: f32,
    pub min_nearby_allies: u32,
    pub min_nearby_enemies: u32,
    pub required_target_status: String,
    pub forbidden_target_status: String,
    pub movement_may_enable: bool,
    #[serde(default)]
    pub windup_ms: u64,
    #[serde(default)]
    pub recovery_ms: u64,
    pub sort_order: u32,
}

const fn default_npc_attack_recovery_ms() -> u64 {
    DEFAULT_NPC_ATTACK_RECOVERY_MS
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct NpcBrainProfile {
    brain_profile_id: String,
    decision_interval_ms: u64,
    perception_radius: f32,
    leash_radius: f32,
    target_stickiness: f32,
    preferred_min_distance: f32,
    preferred_max_distance: f32,
    retreat_tolerance: f32,
    support_urgency: f32,
    deterministic_variation: f32,
    damage_threat_weight: f32,
    healing_threat_weight: f32,
    proximity_threat_weight: f32,
    idle_policy: String,
    assist_policy: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct NpcCatalogDocument {
    brain_profiles: Vec<NpcBrainProfile>,
    templates: Vec<NpcTemplate>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum NpcFaction {
    Hostile,
    Neutral,
    Friendly,
}

impl NpcFaction {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::Hostile => NPC_FACTION_HOSTILE,
            Self::Neutral => NPC_FACTION_NEUTRAL,
            Self::Friendly => NPC_FACTION_FRIENDLY,
        }
    }

    pub(crate) fn from_wire(value: &str) -> Option<Self> {
        match value.trim().to_ascii_uppercase().as_str() {
            NPC_FACTION_HOSTILE => Some(Self::Hostile),
            NPC_FACTION_NEUTRAL => Some(Self::Neutral),
            NPC_FACTION_FRIENDLY => Some(Self::Friendly),
            _ => None,
        }
    }
}

#[derive(Clone)]
pub(crate) struct NpcRelationProfile {
    pub faction: NpcFaction,
    pub spawned_by: Identity,
    pub combat_team_id: String,
}

fn debug_combat_team_id(faction: NpcFaction, owner: Identity, npc: Identity) -> String {
    match faction {
        NpcFaction::Hostile => "DEBUG_HOSTILE".to_string(),
        NpcFaction::Neutral => format!("DEBUG_NEUTRAL:{}", npc.to_hex()),
        NpcFaction::Friendly => format!("PLAYER_ALLIANCE:{}", owner.to_hex()),
    }
}

/// Local measurement aid: `ARENA_NPC_HARMLESS=1` at build time zeroes NPC
/// attack damage at this single choke point so a solo tester can stand inside
/// an NPC pack (aggro, chase, cadence, and swing events stay real). Baked at
/// compile time like `ARENA_PROFILE_TICKS` — absent from normal builds.
fn npc_attacks_are_harmless() -> bool {
    static ENABLED: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *ENABLED.get_or_init(|| {
        let enabled = matches!(
            option_env!("ARENA_NPC_HARMLESS").map(str::trim),
            Some("1") | Some("true") | Some("on") | Some("yes")
        );
        if enabled {
            log::warn!(
                "[INIT] ARENA_NPC_HARMLESS baked in: NPC attack damage is zeroed (local measurement build — do not deploy)"
            );
        }
        enabled
    })
}

fn npc_ai_debug_enabled() -> bool {
    static ENABLED: OnceLock<bool> = OnceLock::new();
    *ENABLED.get_or_init(|| {
        matches!(
            option_env!("ARENA_NPC_AI_DEBUG").map(str::trim),
            Some("1") | Some("true") | Some("on") | Some("yes")
        )
    })
}

/// Local measurement aid: `ARENA_NPC_NO_ATTACK=1` at build time disables NPC
/// melee entirely — within reach the NPC holds position facing its target,
/// never swings, and never enters the post-swing cadence freeze, so chase
/// motion is continuous whenever the target moves (F4 A/B kiting legs).
/// Unlike `ARENA_NPC_HARMLESS` (real swings, zero damage — for contact-cue
/// checks inside a pack), this removes the swing events themselves. Baked at
/// compile time — absent from normal builds.
fn npc_attacks_are_disabled() -> bool {
    static DISABLED: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *DISABLED.get_or_init(|| {
        let disabled = matches!(
            option_env!("ARENA_NPC_NO_ATTACK").map(str::trim),
            Some("1") | Some("true") | Some("on") | Some("yes")
        );
        if disabled {
            log::warn!(
                "[INIT] ARENA_NPC_NO_ATTACK baked in: NPC melee disabled (local measurement build — do not deploy)"
            );
        }
        disabled
    })
}

/// Local measurement aid: `ARENA_NPC_AGGRO_RADIUS=<meters>` at build time
/// overrides every NPC template's aggro radius at this same choke point
/// (e.g. 100 for F4 A/B kiting legs, where the stock 8 m leash plus the
/// post-swing cadence freeze makes sustained chase unachievable by hand).
/// Baked at compile time like `ARENA_NPC_HARMLESS` — absent from normal
/// builds.
fn npc_aggro_radius_override() -> Option<f32> {
    static OVERRIDE: std::sync::OnceLock<Option<f32>> = std::sync::OnceLock::new();
    *OVERRIDE.get_or_init(|| {
        let raw = option_env!("ARENA_NPC_AGGRO_RADIUS")?.trim();
        match raw.parse::<f32>().ok().filter(|r| r.is_finite() && *r > 0.0) {
            Some(radius) => {
                log::warn!(
                    "[INIT] ARENA_NPC_AGGRO_RADIUS baked in: NPC aggro radius overridden to {radius} m (local measurement build — do not deploy)"
                );
                Some(radius)
            }
            None => {
                log::warn!(
                    "[INIT] ARENA_NPC_AGGRO_RADIUS='{raw}' ignored: not a positive finite number"
                );
                None
            }
        }
    })
}

pub(crate) fn npc_template(template_id: &str) -> Option<NpcTemplate> {
    let normalized = normalize_id(template_id);
    let mut template = npc_catalog()
        .templates
        .iter()
        .find(|row| row.template_id == normalized)?
        .clone();

    if npc_is_tanky() {
        template.max_hp = 1_000_000;
    }
    if let Some(radius) = npc_aggro_radius_override() {
        template.aggro_radius = radius;
    }
    Some(template)
}

fn visual_id_for_template(template: &NpcTemplate, visual_id: &str) -> Result<String, String> {
    let visual_id = normalize_id(visual_id);
    if visual_id.is_empty() {
        return Err("NPC visual_id is required".to_string());
    }

    // The current kobold templates each expose one appearance. This explicit
    // gate becomes visual-set membership validation when the authored catalog lands.
    if !template
        .visual_ids
        .iter()
        .any(|candidate| candidate == &visual_id)
    {
        return Err(format!(
            "NPC visual '{visual_id}' is not valid for template '{}'",
            template.template_id
        ));
    }
    Ok(visual_id)
}

fn npc_catalog() -> &'static NpcCatalogDocument {
    static CATALOG: OnceLock<NpcCatalogDocument> = OnceLock::new();
    CATALOG.get_or_init(|| {
        parse_npc_catalog(NPC_CATALOG_JSON)
            .unwrap_or_else(|error| panic!("Invalid npc_catalog.shared.json: {error}"))
    })
}

fn npc_brain_profile(brain_profile_id: &str) -> Option<&'static NpcBrainProfile> {
    let brain_profile_id = normalize_id(brain_profile_id);
    npc_catalog()
        .brain_profiles
        .iter()
        .find(|brain| brain.brain_profile_id == brain_profile_id)
}

fn parse_npc_catalog(json: &str) -> Result<NpcCatalogDocument, String> {
    let mut document: NpcCatalogDocument =
        serde_json::from_str(json).map_err(|error| error.to_string())?;
    let mut template_ids = HashSet::new();
    let mut visual_ids = HashSet::new();
    let mut brain_ids = HashSet::new();
    if document.brain_profiles.is_empty() {
        return Err("brain_profiles must not be empty".to_string());
    }
    if document.templates.is_empty() {
        return Err("templates must not be empty".to_string());
    }

    for brain in &mut document.brain_profiles {
        brain.brain_profile_id = normalize_id(brain.brain_profile_id.as_str());
        brain.idle_policy = normalize_id(brain.idle_policy.as_str());
        brain.assist_policy = normalize_id(brain.assist_policy.as_str());
        if brain.brain_profile_id.is_empty() || !brain_ids.insert(brain.brain_profile_id.clone()) {
            return Err(format!(
                "brain_profile_id '{}' is empty or duplicated",
                brain.brain_profile_id
            ));
        }
        let bounded_scalars = [
            brain.target_stickiness,
            brain.retreat_tolerance,
            brain.support_urgency,
            brain.deterministic_variation,
        ];
        let nonnegative_scalars = [
            brain.perception_radius,
            brain.leash_radius,
            brain.preferred_min_distance,
            brain.preferred_max_distance,
            brain.damage_threat_weight,
            brain.healing_threat_weight,
            brain.proximity_threat_weight,
        ];
        if brain.decision_interval_ms == 0
            || bounded_scalars
                .iter()
                .any(|value| !value.is_finite() || !(0.0..=1.0).contains(value))
            || nonnegative_scalars
                .iter()
                .any(|value| !value.is_finite() || *value < 0.0)
            || brain.preferred_min_distance > brain.preferred_max_distance
            || brain.leash_radius < brain.perception_radius
            || !matches!(brain.idle_policy.as_str(), "HOLD_POSITION" | "PATROL")
            || !matches!(brain.assist_policy.as_str(), "NONE" | "SAME_TEAM")
        {
            return Err(format!(
                "brain profile '{}' has invalid decision tuning",
                brain.brain_profile_id
            ));
        }
    }

    for template in &mut document.templates {
        template.template_id = normalize_id(template.template_id.as_str());
        template.species_id = normalize_id(template.species_id.as_str());
        template.display_name = template.display_name.trim().to_string();
        template.brain_profile_id = normalize_id(template.brain_profile_id.as_str());
        template.resource_policy = normalize_id(template.resource_policy.as_str());
        template.visual_ids = template
            .visual_ids
            .iter()
            .map(|visual_id| normalize_id(visual_id))
            .collect();
        for entry in &mut template.action_kit {
            entry.ability_id = normalize_id(entry.ability_id.as_str());
            entry.role = normalize_id(entry.role.as_str());
            entry.target_selector = normalize_id(entry.target_selector.as_str());
            entry.required_target_status = normalize_id(entry.required_target_status.as_str());
            entry.forbidden_target_status = normalize_id(entry.forbidden_target_status.as_str());
        }

        if template.template_id.is_empty() || !template_ids.insert(template.template_id.clone()) {
            return Err(format!(
                "template_id '{}' is empty or duplicated",
                template.template_id
            ));
        }
        if template.species_id.is_empty() || template.display_name.is_empty() {
            return Err(format!(
                "template '{}' requires species_id and display_name",
                template.template_id
            ));
        }
        if !brain_ids.contains(template.brain_profile_id.as_str()) {
            return Err(format!(
                "template '{}' references unknown brain_profile_id '{}'",
                template.template_id, template.brain_profile_id
            ));
        }
        if template.resource_policy != "FREE_ACTIONS_ONLY" {
            return Err(format!(
                "template '{}' has unsupported resource_policy '{}'",
                template.template_id, template.resource_policy
            ));
        }
        if template.visual_ids.is_empty() {
            return Err(format!(
                "template '{}' requires at least one visual_id",
                template.template_id
            ));
        }
        for visual_id in &template.visual_ids {
            if visual_id.is_empty() || !visual_ids.insert(visual_id.clone()) {
                return Err(format!("visual_id '{visual_id}' is empty or duplicated"));
            }
        }
        let mut action_ids = HashSet::new();
        for entry in &template.action_kit {
            if entry.ability_id.is_empty() || !action_ids.insert(entry.ability_id.clone()) {
                return Err(format!(
                    "template '{}' has an empty or duplicate action-kit ability_id '{}'",
                    template.template_id, entry.ability_id
                ));
            }
            if !matches!(
                entry.role.as_str(),
                "MELEE_OFFENSE"
                    | "RANGED_OFFENSE"
                    | "HEAL"
                    | "BUFF"
                    | "DEBUFF"
                    | "DEFENSE"
                    | "MOBILITY"
                    | "INTERRUPT"
                    | "SUMMON"
            ) {
                return Err(format!(
                    "template '{}' action '{}' has invalid role '{}'",
                    template.template_id, entry.ability_id, entry.role
                ));
            }
            if !matches!(
                entry.target_selector.as_str(),
                "SELF" | "CURRENT_ENEMY" | "LOWEST_HEALTH_ALLY" | "NEAREST_ENEMY"
            ) {
                return Err(format!(
                    "template '{}' action '{}' has invalid target_selector '{}'",
                    template.template_id, entry.ability_id, entry.target_selector
                ));
            }
            if !entry.base_utility.is_finite() || entry.base_utility < 0.0 {
                return Err(format!(
                    "template '{}' action '{}' has invalid base_utility",
                    template.template_id, entry.ability_id
                ));
            }
            if !entry.min_self_health_pct.is_finite()
                || !entry.max_self_health_pct.is_finite()
                || !(0.0..=1.0).contains(&entry.min_self_health_pct)
                || !(0.0..=1.0).contains(&entry.max_self_health_pct)
                || entry.min_self_health_pct > entry.max_self_health_pct
            {
                return Err(format!(
                    "template '{}' action '{}' has invalid self-health thresholds",
                    template.template_id, entry.ability_id
                ));
            }
            if !entry.preferred_min_distance.is_finite()
                || !entry.preferred_max_distance.is_finite()
                || entry.preferred_min_distance < 0.0
                || entry.preferred_min_distance > entry.preferred_max_distance
            {
                return Err(format!(
                    "template '{}' action '{}' has invalid preferred distance band",
                    template.template_id, entry.ability_id
                ));
            }
            if entry.windup_ms != 0 && !(300..=10_000).contains(&entry.windup_ms) {
                return Err(format!(
                    "template '{}' action '{}' has invalid windup_ms",
                    template.template_id, entry.ability_id
                ));
            }
            if entry.recovery_ms != 0
                && !(50..=MAX_NPC_ATTACK_RECOVERY_MS).contains(&entry.recovery_ms)
            {
                return Err(format!(
                    "template '{}' action '{}' has invalid recovery_ms",
                    template.template_id, entry.ability_id
                ));
            }
            match crate::progression::authored_ability_actor_scope(entry.ability_id.as_str()) {
                Some(scope) if matches!(normalize_id(scope).as_str(), "NPC" | "BOTH") => {}
                Some(scope) => {
                    return Err(format!(
                        "template '{}' action '{}' references {scope}-scoped ability",
                        template.template_id, entry.ability_id
                    ));
                }
                None => {
                    return Err(format!(
                        "template '{}' action '{}' references unknown ability",
                        template.template_id, entry.ability_id
                    ));
                }
            }
            let Some((resource_kind, resource_cost)) =
                crate::progression::authored_ability_resource(entry.ability_id.as_str())
            else {
                return Err(format!(
                    "template '{}' action '{}' has no authored resource contract",
                    template.template_id, entry.ability_id
                ));
            };
            if !resource_kind.trim().is_empty() || resource_cost != 0.0 {
                return Err(format!(
                    "template '{}' FREE_ACTIONS_ONLY kit cannot grant resource-spending ability '{}'",
                    template.template_id, entry.ability_id
                ));
            }
        }
        if template.max_hp <= 0
            || !template.hit_radius.is_finite()
            || template.hit_radius <= 0.0
            || !template.hit_height.is_finite()
            || template.hit_height <= 0.0
            || !template.knockback_resistance.is_finite()
            || !(0.0..=1.0).contains(&template.knockback_resistance)
            || !template.aggro_radius.is_finite()
            || template.aggro_radius <= 0.0
            || !template.move_speed.is_finite()
            || template.move_speed <= 0.0
            || template.attack_windup_ms == 0
            || !(50..=MAX_NPC_ATTACK_RECOVERY_MS).contains(&template.attack_recovery_ms)
        {
            return Err(format!(
                "template '{}' has invalid combat or geometry values",
                template.template_id
            ));
        }
    }
    Ok(document)
}

pub(crate) fn sync_npc_catalog(ctx: &ReducerContext) {
    let catalog = npc_catalog();
    let expected_templates: HashSet<_> = catalog
        .templates
        .iter()
        .map(|template| template.template_id.clone())
        .collect();
    let expected_brains: HashSet<_> = catalog
        .brain_profiles
        .iter()
        .map(|brain| brain.brain_profile_id.clone())
        .collect();
    let expected_visuals: HashSet<_> = catalog
        .templates
        .iter()
        .flat_map(|template| template.visual_ids.iter().cloned())
        .collect();
    let expected_actions: HashSet<_> = catalog
        .templates
        .iter()
        .flat_map(|template| {
            template
                .action_kit
                .iter()
                .map(|entry| format!("{}:{}", template.template_id, entry.ability_id))
        })
        .collect();

    for brain in &catalog.brain_profiles {
        let row = NpcBrainCatalog {
            brain_profile_id: brain.brain_profile_id.clone(),
            decision_interval_ms: brain.decision_interval_ms,
            perception_radius: brain.perception_radius,
            leash_radius: brain.leash_radius,
            target_stickiness: brain.target_stickiness,
            preferred_min_distance: brain.preferred_min_distance,
            preferred_max_distance: brain.preferred_max_distance,
            retreat_tolerance: brain.retreat_tolerance,
            support_urgency: brain.support_urgency,
            deterministic_variation: brain.deterministic_variation,
            damage_threat_weight: brain.damage_threat_weight,
            healing_threat_weight: brain.healing_threat_weight,
            proximity_threat_weight: brain.proximity_threat_weight,
            idle_policy: brain.idle_policy.clone(),
            assist_policy: brain.assist_policy.clone(),
        };
        if ctx
            .db
            .npc_brain_catalog()
            .brain_profile_id()
            .find(brain.brain_profile_id.clone())
            .is_some()
        {
            ctx.db.npc_brain_catalog().brain_profile_id().update(row);
        } else {
            ctx.db.npc_brain_catalog().insert(row);
        }
    }

    for template in &catalog.templates {
        let row = NpcTemplateCatalog {
            template_id: template.template_id.clone(),
            species_id: template.species_id.clone(),
            display_name: template.display_name.clone(),
            default_visual_id: template.visual_ids[0].clone(),
            brain_profile_id: template.brain_profile_id.clone(),
            resource_policy: template.resource_policy.clone(),
            max_hp: template.max_hp,
            hit_radius: template.hit_radius,
            hit_height: template.hit_height,
        };
        match ctx
            .db
            .npc_template_catalog()
            .template_id()
            .find(template.template_id.clone())
        {
            Some(existing)
                if existing.species_id == row.species_id
                    && existing.display_name == row.display_name
                    && existing.default_visual_id == row.default_visual_id
                    && existing.brain_profile_id == row.brain_profile_id
                    && existing.resource_policy == row.resource_policy
                    && existing.max_hp == row.max_hp
                    && existing.hit_radius == row.hit_radius
                    && existing.hit_height == row.hit_height => {}
            Some(_) => {
                ctx.db.npc_template_catalog().template_id().update(row);
            }
            None => {
                ctx.db.npc_template_catalog().insert(row);
            }
        }

        for visual_id in &template.visual_ids {
            let row = NpcVisualCatalog {
                visual_id: visual_id.clone(),
                template_id: template.template_id.clone(),
            };
            match ctx
                .db
                .npc_visual_catalog()
                .visual_id()
                .find(visual_id.clone())
            {
                Some(existing) if existing.template_id == row.template_id => {}
                Some(_) => {
                    ctx.db.npc_visual_catalog().visual_id().update(row);
                }
                None => {
                    ctx.db.npc_visual_catalog().insert(row);
                }
            }
        }
        for entry in &template.action_kit {
            let entry_id = format!("{}:{}", template.template_id, entry.ability_id);
            let row = NpcActionKitCatalog {
                entry_id: entry_id.clone(),
                template_id: template.template_id.clone(),
                ability_id: entry.ability_id.clone(),
                role: entry.role.clone(),
                target_selector: entry.target_selector.clone(),
                base_utility: entry.base_utility,
                min_self_health_pct: entry.min_self_health_pct,
                max_self_health_pct: entry.max_self_health_pct,
                preferred_min_distance: entry.preferred_min_distance,
                preferred_max_distance: entry.preferred_max_distance,
                min_nearby_allies: entry.min_nearby_allies,
                min_nearby_enemies: entry.min_nearby_enemies,
                required_target_status: entry.required_target_status.clone(),
                forbidden_target_status: entry.forbidden_target_status.clone(),
                movement_may_enable: entry.movement_may_enable,
                windup_ms: npc_action_windup_ms(template, entry.ability_id.as_str()),
                sort_order: entry.sort_order,
            };
            if ctx
                .db
                .npc_action_kit_catalog()
                .entry_id()
                .find(entry_id)
                .is_some()
            {
                ctx.db.npc_action_kit_catalog().entry_id().update(row);
            } else {
                ctx.db.npc_action_kit_catalog().insert(row);
            }
        }
    }

    let stale_templates: Vec<_> = ctx
        .db
        .npc_template_catalog()
        .iter()
        .map(|row| row.template_id)
        .filter(|template_id| !expected_templates.contains(template_id))
        .collect();
    for template_id in stale_templates {
        ctx.db
            .npc_template_catalog()
            .template_id()
            .delete(template_id);
    }
    let stale_brains: Vec<_> = ctx
        .db
        .npc_brain_catalog()
        .iter()
        .map(|row| row.brain_profile_id)
        .filter(|brain_id| !expected_brains.contains(brain_id))
        .collect();
    for brain_id in stale_brains {
        ctx.db
            .npc_brain_catalog()
            .brain_profile_id()
            .delete(brain_id);
    }
    let stale_visuals: Vec<_> = ctx
        .db
        .npc_visual_catalog()
        .iter()
        .map(|row| row.visual_id)
        .filter(|visual_id| !expected_visuals.contains(visual_id))
        .collect();
    for visual_id in stale_visuals {
        ctx.db.npc_visual_catalog().visual_id().delete(visual_id);
    }
    let stale_actions: Vec<_> = ctx
        .db
        .npc_action_kit_catalog()
        .iter()
        .map(|row| row.entry_id)
        .filter(|entry_id| !expected_actions.contains(entry_id))
        .collect();
    for entry_id in stale_actions {
        ctx.db.npc_action_kit_catalog().entry_id().delete(entry_id);
    }
}

/// Local measurement aid: `ARENA_NPC_TANKY=1` at build time gives every NPC a
/// huge health pool so a solo tester's auto-attacks can't kill the fixture
/// mid-test (S9 owner leg: a moving target must survive the whole leg). Baked
/// at compile time like `ARENA_NPC_HARMLESS` — absent from normal builds.
fn npc_is_tanky() -> bool {
    static ENABLED: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *ENABLED.get_or_init(|| {
        let enabled = matches!(
            option_env!("ARENA_NPC_TANKY").map(str::trim),
            Some("1") | Some("true") | Some("on") | Some("yes")
        );
        if enabled {
            log::warn!(
                "[INIT] ARENA_NPC_TANKY baked in: NPC max_hp forced to 1,000,000 (local measurement build — do not deploy)"
            );
        }
        enabled
    })
}

#[reducer]
pub fn spawn_npc(
    ctx: &ReducerContext,
    template_id: String,
    visual_id: String,
    faction: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let template = npc_template(template_id.as_str())
        .ok_or_else(|| format!("Unknown NPC template '{template_id}'"))?;
    let visual_id = visual_id_for_template(&template, visual_id.as_str())?;
    let faction = NpcFaction::from_wire(faction.as_str())
        .ok_or_else(|| format!("Unknown NPC faction '{faction}'"))?;

    if ctx.db.player_state().player_id().find(owner).is_none() {
        return Err("Cannot spawn NPC without a player_state row".to_string());
    }

    let Some(owner_physics) = ctx.db.player_physics().identity().find(owner) else {
        return Err("Cannot spawn NPC without owner physics".to_string());
    };
    let Some(owner_world) = ctx.db.player_world().identity().find(owner) else {
        return Err("Cannot spawn NPC without owner world".to_string());
    };

    let is_instance = owner_world
        .world_kind
        .eq_ignore_ascii_case(WORLD_KIND_INSTANCE);
    let instance_id =
        if is_instance {
            Some(owner_world.instance_id.ok_or_else(|| {
                "Cannot spawn NPC in instance without owner instance_id".to_string()
            })?)
        } else {
            None
        };
    let open_world_scene_name = if is_instance {
        String::new()
    } else if owner_world.world_kind.eq_ignore_ascii_case(WORLD_KIND_OPEN) {
        open_world_scene_name_for_identity(ctx, owner)
    } else {
        return Err(format!(
            "Cannot spawn NPC in unsupported owner world kind '{}'",
            owner_world.world_kind
        ));
    };

    let sequence = next_npc_sequence(ctx, owner);
    let identity = npc_identity(owner, sequence)?;
    let combat_team_id = debug_combat_team_id(faction, owner, identity);
    let target_yaw = wrap_yaw(owner_physics.yaw + std::f32::consts::PI);
    let spawn_x = owner_physics.pos_x + owner_physics.yaw.sin() * NPC_SPAWN_FORWARD;
    let spawn_z = owner_physics.pos_z + owner_physics.yaw.cos() * NPC_SPAWN_FORWARD;
    let arena_seed =
        instance_id.and_then(|id| ctx.db.arena_instance().id().find(id).map(|row| row.seed));
    let flat_ground_only = instance_id.is_some_and(|id| is_training_instance(ctx, id));
    let spawn_y = surface_height_for_world_at_y_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        (!is_instance).then_some(open_world_scene_name.as_str()),
        spawn_x,
        spawn_z,
        owner_physics.pos_y,
    );

    ctx.db.npc_instance().insert(NpcInstance {
        identity,
        spawned_by: owner,
        template_id: template.template_id.to_string(),
        visual_id,
        species_id: template.species_id.to_string(),
        faction: faction.as_str().to_string(),
        combat_team_id,
        display_name: template.display_name.to_string(),
        world_kind: if is_instance {
            WORLD_KIND_INSTANCE.to_string()
        } else {
            WORLD_KIND_OPEN.to_string()
        },
        instance_id,
        open_world_scene_name,
        home_x: spawn_x,
        home_y: spawn_y,
        home_z: spawn_z,
        spawned_at: ctx.timestamp,
    });

    ctx.db.npc_state().insert(NpcState {
        identity,
        alive: true,
        hp: template.max_hp,
        max_hp: template.max_hp,
        hit_radius: template.hit_radius,
        hit_height: template.hit_height,
    });

    ctx.db.npc_physics().insert(NpcPhysics {
        identity,
        pos_x: spawn_x,
        pos_y: spawn_y,
        pos_z: spawn_z,
        yaw: target_yaw,
        updated_at: ctx.timestamp,
    });

    Ok(())
}

#[reducer]
pub fn despawn_npc(ctx: &ReducerContext, identity: Identity) -> Result<(), String> {
    let owner = ctx.sender();
    let Some(row) = ctx.db.npc_instance().identity().find(identity) else {
        return Ok(());
    };

    if row.spawned_by != owner {
        return Err("Cannot despawn an NPC spawned by another identity".to_string());
    }

    despawn_npc_identity(ctx, identity);
    Ok(())
}

#[reducer]
pub fn despawn_all_npcs(ctx: &ReducerContext) -> Result<(), String> {
    despawn_all_npcs_for_owner(ctx, ctx.sender());
    Ok(())
}

#[reducer]
pub fn set_npc_target_override(
    ctx: &ReducerContext,
    identity: Identity,
    target: Option<Identity>,
) -> Result<(), String> {
    let owner = ctx.sender();
    let Some(npc) = ctx.db.npc_instance().identity().find(identity) else {
        return Err("Cannot pin a missing NPC".to_string());
    };
    if npc.spawned_by != owner {
        return Err("Cannot pin an NPC spawned by another identity".to_string());
    }
    let Some(target) = target else {
        ctx.db.npc_target_override().identity().delete(identity);
        return Ok(());
    };
    if ctx.db.player_state().player_id().find(target).is_none() {
        return Err("NPC target override requires a player actor target".to_string());
    }
    let row = NpcTargetOverride {
        identity,
        target,
        set_by: owner,
        updated_at: ctx.timestamp,
    };
    if ctx
        .db
        .npc_target_override()
        .identity()
        .find(identity)
        .is_some()
    {
        ctx.db.npc_target_override().identity().update(row);
    } else {
        ctx.db.npc_target_override().insert(row);
    }
    Ok(())
}

pub(crate) fn despawn_all_npcs_for_owner(ctx: &ReducerContext, owner: Identity) {
    let identities: Vec<_> = ctx
        .db
        .npc_instance()
        .spawned_by()
        .filter(owner)
        .map(|row| row.identity)
        .collect();

    for identity in identities {
        despawn_npc_identity(ctx, identity);
    }
}

pub(crate) fn despawn_dead_npcs_for_owner(ctx: &ReducerContext, owner: Identity) {
    let identities: Vec<_> = ctx
        .db
        .npc_instance()
        .spawned_by()
        .filter(owner)
        .filter(|row| {
            ctx.db
                .npc_state()
                .identity()
                .find(row.identity)
                .is_none_or(|state| !state.alive)
        })
        .map(|row| row.identity)
        .collect();

    for identity in identities {
        despawn_npc_identity(ctx, identity);
    }
}

pub(crate) fn schedule_npc_corpse_despawn(
    ctx: &ReducerContext,
    identity: Identity,
    now: Timestamp,
) {
    let delay = if corpse_loot_has_items(ctx, identity) {
        NPC_UNLOOTED_CORPSE_DESPAWN_DELAY
    } else {
        NPC_LOOTED_CORPSE_DESPAWN_DELAY
    };
    schedule_npc_corpse_despawn_after(ctx, identity, now, delay);
}

pub(crate) fn schedule_npc_looted_corpse_despawn(
    ctx: &ReducerContext,
    identity: Identity,
    now: Timestamp,
) {
    if corpse_loot_has_items(ctx, identity) {
        return;
    }

    schedule_npc_corpse_despawn_after(ctx, identity, now, NPC_LOOTED_CORPSE_DESPAWN_DELAY);
}

fn schedule_npc_corpse_despawn_after(
    ctx: &ReducerContext,
    identity: Identity,
    now: Timestamp,
    delay: Duration,
) {
    let despawn_at = now + delay;
    let row = NpcDespawn {
        identity,
        despawn_at,
        despawn_at_micros: timestamp_to_micros(despawn_at),
    };

    if ctx.db.npc_despawn().identity().find(identity).is_some() {
        ctx.db.npc_despawn().identity().update(row);
    } else {
        ctx.db.npc_despawn().insert(row);
    }
}

pub(crate) fn prune_due_npc_corpse_despawns(ctx: &ReducerContext, now: Timestamp) {
    let due: Vec<_> = ctx
        .db
        .npc_despawn()
        .despawn_at_micros()
        .filter(..=timestamp_to_micros(now))
        .map(|row| row.identity)
        .collect();

    for identity in due {
        match ctx.db.npc_state().identity().find(identity) {
            Some(state) if state.alive => {
                ctx.db.npc_despawn().identity().delete(identity);
            }
            _ => {
                despawn_npc_identity(ctx, identity);
            }
        }
    }
}

pub(crate) fn npc_relation_profile(
    ctx: &ReducerContext,
    identity: Identity,
) -> Option<NpcRelationProfile> {
    let row = ctx.db.npc_instance().identity().find(identity)?;
    Some(NpcRelationProfile {
        faction: NpcFaction::from_wire(row.faction.as_str())?,
        spawned_by: row.spawned_by,
        combat_team_id: row.combat_team_id,
    })
}

pub(crate) fn tick_npc_combat(
    ctx: &ReducerContext,
    now: Timestamp,
    movement_modifiers: &MovementModifiers,
) {
    // Due windups resolve before new swings begin, so a swing scheduled for
    // this tick lands before the same NPC's next cadence fire is considered.
    resolve_due_pending_melee_impacts_for_event_source(ctx, now, NPC_MELEE_SOURCE_KIND);

    let npcs: Vec<NpcInstance> = ctx.db.npc_instance().iter().collect();
    if npcs.is_empty() {
        return;
    }

    // Build the shared actor broadphase once. Ordinary target acquisition then
    // queries only the NPC's perception disc on its bounded decision tick;
    // committed and fixture-pinned targets use exact indexed lookup.
    let perception = NpcPerceptionIndex::collect(ctx);
    for npc in npcs {
        // Forced displacement exclusively owns NPC physics until its row is
        // removed. This gate must precede leash, facing, and all AI steering.
        if ctx
            .db
            .npc_forced_movement()
            .identity()
            .find(npc.identity)
            .is_some()
        {
            continue;
        }
        let Some(faction) = NpcFaction::from_wire(npc.faction.as_str()) else {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        };
        if faction != NpcFaction::Hostile {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        }

        let Some(template) = npc_template(npc.template_id.as_str()) else {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        };
        let Some(brain) = npc_brain_profile(template.brain_profile_id.as_str()) else {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        };
        let Some(state) = ctx.db.npc_state().identity().find(npc.identity) else {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        };
        if !state.alive {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        }
        let Some(physics) = ctx.db.npc_physics().identity().find(npc.identity) else {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        };
        let target_override = ctx.db.npc_target_override().identity().find(npc.identity);
        let target_is_pinned = target_override.is_some();
        if target_is_pinned {
            ctx.db.npc_return_home().identity().delete(npc.identity);
        } else if ctx
            .db
            .npc_return_home()
            .identity()
            .find(npc.identity)
            .is_some()
            || npc_is_outside_leash(&npc, &physics, brain.leash_radius)
        {
            if ctx
                .db
                .npc_return_home()
                .identity()
                .find(npc.identity)
                .is_none()
            {
                interrupt_npc_actions_for_crowd_control(ctx, npc.identity, now);
                clear_npc_threat(ctx, npc.identity);
                ctx.db.npc_return_home().insert(NpcReturnHome {
                    identity: npc.identity,
                    started_at: now,
                });
            }
            clear_npc_combat_runtime(ctx, npc.identity);
            return_npc_home(
                ctx,
                now,
                &npc,
                &physics,
                &template,
                movement_modifiers.move_speed_multiplier(&npc.identity, 0),
            );
            continue;
        }

        if let Some(target_identity) =
            pending_melee_commitment_target_for_source(ctx, npc.identity, NPC_MELEE_SOURCE_KIND)
        {
            if let Some(target) =
                resolve_npc_swing_target(ctx, npc.identity, &physics, target_identity)
            {
                face_npc_target(ctx, now, &physics, &target);
            }
            continue;
        }
        let existing_runtime = ctx.db.npc_combat_runtime().identity().find(npc.identity);
        if ctx.db.active_cast().caster().find(npc.identity).is_some() {
            if let Some(runtime) = existing_runtime.as_ref() {
                if let Some(target) = resolve_committed_npc_target(
                    ctx,
                    npc.identity,
                    &physics,
                    &template,
                    &perception,
                    runtime.target,
                    target_is_pinned,
                ) {
                    face_npc_target(ctx, now, &physics, &target);
                }
            }
            continue;
        }
        if existing_runtime
            .as_ref()
            .is_some_and(|runtime| npc_movement_hold_active(runtime, now))
        {
            if let Some(runtime) = existing_runtime.as_ref() {
                if let Some(target) = resolve_committed_npc_target(
                    ctx,
                    npc.identity,
                    &physics,
                    &template,
                    &perception,
                    runtime.target,
                    target_is_pinned,
                ) {
                    face_npc_target(ctx, now, &physics, &target);
                }
            }
            continue;
        }

        let committed_target = existing_runtime.as_ref().and_then(|runtime| {
            if now >= runtime.next_decision_at
                || target_override
                    .as_ref()
                    .is_some_and(|pinned| pinned.target != runtime.target)
            {
                return None;
            }
            resolve_committed_npc_target(
                ctx,
                npc.identity,
                &physics,
                &template,
                &perception,
                runtime.target,
                target_is_pinned,
            )
        });
        let decision_was_due = committed_target.is_none();
        if decision_was_due && !is_in_combat(ctx, npc.identity, now) {
            clear_npc_threat(ctx, npc.identity);
        }
        let target = committed_target.or_else(|| {
            acquire_npc_attack_target(
                ctx,
                npc.identity,
                &physics,
                &template,
                brain,
                &perception,
                existing_runtime.as_ref().map(|runtime| runtime.target),
                brain.target_stickiness,
            )
        });
        let Some(mut target) = target else {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        };
        let planned_action = if decision_was_due {
            let nearby = perception.nearby_counts(
                ctx,
                npc.identity,
                physics.pos_x,
                physics.pos_z,
                brain.perception_radius,
            );
            let selection = select_npc_melee_action(
                ctx,
                now,
                &template,
                &state,
                &physics,
                &target,
                &perception,
                brain.perception_radius,
                nearby,
            );
            if let Some(selected_target) = selection.target {
                target = selected_target;
            }
            record_npc_decision_debug(
                ctx,
                now,
                &npc,
                &template,
                &selection,
                &target,
                target_is_pinned,
            );
            selection.action
        } else {
            existing_runtime.as_ref().and_then(|runtime| {
                (!runtime.planned_ability_id.is_empty())
                    .then(|| {
                        npc_executable_action_for_ability(ctx, runtime.planned_ability_id.as_str())
                    })
                    .flatten()
            })
        };
        let mut runtime = if decision_was_due {
            let decision_sequence = existing_runtime
                .as_ref()
                .map_or(1, |runtime| runtime.decision_sequence.saturating_add(1));
            let interval_ms = npc_decision_interval_ms(
                npc.identity,
                decision_sequence,
                brain.decision_interval_ms,
                brain.deterministic_variation,
            );
            let next_decision_at = now + Duration::from_millis(interval_ms);
            NpcCombatRuntime {
                identity: npc.identity,
                target: target.identity,
                planned_ability_id: planned_action
                    .as_ref()
                    .map_or_else(String::new, |action| action.ability_id().to_string()),
                decision_sequence,
                next_decision_at,
                next_decision_at_micros: timestamp_to_micros(next_decision_at),
                hold_movement_until_micros: 0,
            }
        } else {
            existing_runtime.expect("committed target requires existing NPC runtime")
        };
        if movement_modifiers.is_disabled(&npc.identity) {
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }
        let Some(action) = planned_action else {
            face_npc_target(ctx, now, &physics, &target);
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        };
        let ranged_band = template
            .action_kit
            .iter()
            .find(|entry| entry.ability_id == action.ability_id() && entry.role == "RANGED_OFFENSE")
            .map(|entry| {
                (
                    npc_tactical_band(
                        target.distance,
                        entry.preferred_min_distance,
                        entry.preferred_max_distance,
                        brain.retreat_tolerance,
                    ),
                    entry.preferred_max_distance,
                )
            });
        if ranged_band.is_some_and(|(band, _)| band == NpcTacticalBand::Retreat) {
            let move_speed_multiplier = movement_modifiers.move_speed_multiplier(&npc.identity, 0);
            if move_speed_multiplier > 0.0 {
                retreat_npc_from_target(
                    ctx,
                    now,
                    &npc,
                    &physics,
                    &template,
                    &target,
                    move_speed_multiplier,
                );
            } else {
                face_npc_target(ctx, now, &physics, &target);
            }
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }
        if let Some((NpcTacticalBand::Approach, preferred_max)) = ranged_band {
            let move_speed_multiplier = movement_modifiers.move_speed_multiplier(&npc.identity, 0);
            if move_speed_multiplier > 0.0 {
                chase_npc_toward_target(
                    ctx,
                    now,
                    &npc,
                    &physics,
                    &template,
                    (preferred_max - target.hit_radius).max(0.0),
                    &target,
                    move_speed_multiplier,
                );
            } else {
                face_npc_target(ctx, now, &physics, &target);
            }
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }
        if target.distance > npc_attack_reach(action.range(), &target) {
            let move_speed_multiplier = movement_modifiers.move_speed_multiplier(&npc.identity, 0);
            if move_speed_multiplier > 0.0 {
                chase_npc_toward_target(
                    ctx,
                    now,
                    &npc,
                    &physics,
                    &template,
                    action.range(),
                    &target,
                    move_speed_multiplier,
                );
            } else {
                face_npc_target(ctx, now, &physics, &target);
            }
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }

        let physics = face_npc_target(ctx, now, &physics, &target);

        if npc_attacks_are_disabled() {
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }

        match &action {
            NpcExecutableAction::Melee(melee) => {
                if begin_npc_melee_swing(
                    ctx,
                    now,
                    &npc,
                    &physics,
                    &template,
                    melee,
                    &target,
                    &mut runtime,
                ) {
                    stamp_named_cooldown_for_duration(
                        ctx,
                        npc.identity,
                        action.ability_id(),
                        Duration::from_millis(action.cooldown_ms()),
                        now,
                    );
                }
            }
            NpcExecutableAction::Spell { spell_kind, .. } => {
                let cast_result = cast_spell_for_server_actor(
                    ctx,
                    npc.identity,
                    spell_kind,
                    target.identity.to_hex().as_str(),
                    target.pos_x,
                    target.pos_y,
                    target.pos_z,
                    physics.yaw,
                    now,
                );
                match cast_result {
                    Err(err) => log::warn!("[NPC_AI] spell execution failed: {err}"),
                    Ok(()) => {
                        let recovery_ms = npc_action_recovery_ms(&template, action.ability_id());
                        if let Some(active_cast) = ctx.db.active_cast().caster().find(npc.identity)
                        {
                            stamp_npc_movement_hold(
                                &mut runtime,
                                active_cast.ends_at + Duration::from_millis(recovery_ms),
                            );
                        } else if npc_spell_cast_started_at(ctx, npc.identity, now) {
                            stamp_npc_movement_hold(
                                &mut runtime,
                                now + Duration::from_millis(recovery_ms),
                            );
                        }
                    }
                }
                runtime.planned_ability_id.clear();
                runtime.next_decision_at = now;
                runtime.next_decision_at_micros = timestamp_to_micros(now);
            }
        }
        upsert_npc_combat_runtime(ctx, runtime);
    }
}

fn npc_decision_interval_ms(
    identity: Identity,
    decision_sequence: u64,
    base_interval_ms: u64,
    variation: f32,
) -> u64 {
    let identity_hex = identity.to_hex();
    let identity_tail = identity_hex
        .get(identity_hex.len().saturating_sub(16)..)
        .and_then(|tail| u64::from_str_radix(tail, 16).ok())
        .unwrap_or(0);
    let mut mixed = identity_tail ^ decision_sequence.wrapping_mul(0x9e37_79b9_7f4a_7c15);
    mixed ^= mixed >> 30;
    mixed = mixed.wrapping_mul(0xbf58_476d_1ce4_e5b9);
    mixed ^= mixed >> 27;
    mixed = mixed.wrapping_mul(0x94d0_49bb_1331_11eb);
    mixed ^= mixed >> 31;
    let unit = (mixed % 10_001) as f32 / 10_000.0;
    let scalar = 1.0 + (unit * 2.0 - 1.0) * variation.clamp(0.0, 1.0);
    ((base_interval_ms.max(1) as f32 * scalar).round() as u64).max(1)
}

#[derive(Clone, Copy)]
struct NpcAttackTarget {
    identity: Identity,
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    hit_radius: f32,
    distance: f32,
    dir_x: f32,
    dir_z: f32,
}

struct NpcTargetCandidate {
    identity: Identity,
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    hit_radius: f32,
    hp: i32,
    max_hp: i32,
    context: ResolvedWorldContext,
}

#[derive(Clone, Copy)]
struct NpcThreatComponents {
    damage: f32,
    proximity: f32,
}

impl NpcThreatComponents {
    fn total(self) -> f32 {
        self.damage + self.proximity
    }
}

#[derive(Clone, Copy)]
struct NpcScoredTarget {
    target: NpcAttackTarget,
    threat: NpcThreatComponents,
}

struct NpcPerceptionIndex {
    actors: CombatActorSnapshotSet,
    actor_contexts: HashMap<Identity, ResolvedWorldContext>,
    actor_health: HashMap<Identity, (i32, i32)>,
    ineligible_targets: HashSet<Identity>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum NpcCandidateRelation {
    Enemy,
    Ally,
}

#[derive(Clone, Copy, Default)]
struct NpcNearbyCounts {
    allies: u32,
    enemies: u32,
}

impl NpcPerceptionIndex {
    fn collect(ctx: &ReducerContext) -> Self {
        let actors = CombatActorSnapshotSet::collect(ctx);
        let ineligible_targets: HashSet<Identity> = ctx
            .db
            .player_state()
            .iter()
            .filter(|state| state.is_dummy)
            .map(|state| state.player_id)
            .collect();
        let mut actor_health: HashMap<Identity, (i32, i32)> = ctx
            .db
            .player_state()
            .iter()
            .map(|state| (state.player_id, (state.hp, state.max_hp)))
            .collect();
        actor_health.extend(
            ctx.db
                .npc_state()
                .iter()
                .map(|state| (state.identity, (state.hp, state.max_hp))),
        );
        let mut actor_contexts = HashMap::new();
        for actor in actors.as_slice() {
            if !actor.alive {
                continue;
            }
            let Some(context) = resolve_player_world_context(ctx, actor.player_id) else {
                log::warn!(
                    "[WORLD] Missing world context for NPC target candidate {}",
                    actor.player_id.to_hex()
                );
                continue;
            };
            actor_contexts.insert(actor.player_id, context);
        }
        Self {
            actors,
            actor_contexts,
            actor_health,
            ineligible_targets,
        }
    }

    fn exact(&self, identity: Identity) -> Option<NpcTargetCandidate> {
        let index = *self.actors.index_by_id().get(&identity)?;
        self.candidate_at(index)
    }

    fn query_disc(&self, center_x: f32, center_z: f32, radius: f32) -> Vec<NpcTargetCandidate> {
        let mut indices = Vec::new();
        self.actors
            .query_disc_indices(center_x, center_z, radius, &mut indices);
        // Snapshot insertion order matches the former player-table scan and is
        // the deterministic tie-breaker for exactly equidistant candidates.
        indices.sort_unstable();
        indices
            .into_iter()
            .filter_map(|index| self.candidate_at(index))
            .collect()
    }

    fn relation_candidates(
        &self,
        ctx: &ReducerContext,
        source: Identity,
        center_x: f32,
        center_z: f32,
        radius: f32,
        relation: NpcCandidateRelation,
    ) -> Vec<NpcTargetCandidate> {
        self.query_disc(center_x, center_z, radius)
            .into_iter()
            .filter(|candidate| candidate.identity != source)
            .filter(|candidate| {
                matches!(
                    (relation, combat_relation(ctx, source, candidate.identity)),
                    (NpcCandidateRelation::Enemy, CombatRelation::Hostile)
                        | (NpcCandidateRelation::Ally, CombatRelation::PartyAlly)
                )
            })
            .collect()
    }

    fn candidate_at(&self, index: usize) -> Option<NpcTargetCandidate> {
        let actor = self.actors.as_slice().get(index)?;
        if self.ineligible_targets.contains(&actor.player_id) {
            return None;
        }
        let context = self.actor_contexts.get(&actor.player_id)?.clone();
        let (hp, max_hp) = self.actor_health.get(&actor.player_id).copied()?;
        Some(NpcTargetCandidate {
            identity: actor.player_id,
            pos_x: actor.pos_x,
            pos_y: actor.pos_y,
            pos_z: actor.pos_z,
            hit_radius: actor.hit_radius,
            hp,
            max_hp,
            context,
        })
    }

    fn nearby_counts(
        &self,
        ctx: &ReducerContext,
        npc_identity: Identity,
        center_x: f32,
        center_z: f32,
        radius: f32,
    ) -> NpcNearbyCounts {
        let Some(npc_context) = self.actor_contexts.get(&npc_identity) else {
            return NpcNearbyCounts::default();
        };
        let radius_sq = radius.max(0.0) * radius.max(0.0);
        let mut indices = Vec::new();
        self.actors
            .query_disc_indices(center_x, center_z, radius, &mut indices);
        let mut counts = NpcNearbyCounts::default();
        for actor in indices
            .into_iter()
            .filter_map(|index| self.actors.as_slice().get(index))
        {
            if !actor.alive || actor.player_id == npc_identity {
                continue;
            }
            let Some(context) = self.actor_contexts.get(&actor.player_id) else {
                continue;
            };
            if !world_contexts_share(npc_context, context) {
                continue;
            }
            let dx = actor.pos_x - center_x;
            let dz = actor.pos_z - center_z;
            if dx * dx + dz * dz > radius_sq {
                continue;
            }
            match combat_relation(ctx, npc_identity, actor.player_id) {
                CombatRelation::PartyAlly => counts.allies = counts.allies.saturating_add(1),
                CombatRelation::Hostile => counts.enemies = counts.enemies.saturating_add(1),
                CombatRelation::Self_ | CombatRelation::Neutral => {}
            }
        }
        counts
    }
}

fn acquire_npc_attack_target(
    ctx: &ReducerContext,
    npc_identity: Identity,
    npc_physics: &NpcPhysics,
    template: &NpcTemplate,
    brain: &NpcBrainProfile,
    perception: &NpcPerceptionIndex,
    current_target: Option<Identity>,
    target_stickiness: f32,
) -> Option<NpcAttackTarget> {
    let Some(npc_context) = resolve_player_world_context(ctx, npc_identity) else {
        log::warn!(
            "[WORLD] Missing world context for NPC {}",
            npc_identity.to_hex()
        );
        return None;
    };
    if let Some(pinned) = ctx.db.npc_target_override().identity().find(npc_identity) {
        if let Some(candidate) = perception
            .exact(pinned.target)
            .filter(|candidate| world_contexts_share(&npc_context, &candidate.context))
            .filter(|candidate| can_harm(ctx, npc_identity, candidate.identity))
        {
            return Some(npc_attack_target_from_candidate(npc_physics, &candidate));
        }
    }
    let candidates = perception.relation_candidates(
        ctx,
        npc_identity,
        npc_physics.pos_x,
        npc_physics.pos_z,
        template.aggro_radius,
        NpcCandidateRelation::Enemy,
    );
    crate::tick_metrics::record_npc_target_pairs_scanned(candidates.len() as u64);

    let aggro_radius_sq = template.aggro_radius * template.aggro_radius;
    let mut best: Option<NpcScoredTarget> = None;
    let mut current: Option<NpcScoredTarget> = None;
    for candidate in &candidates {
        if !world_contexts_share(&npc_context, &candidate.context) {
            continue;
        }
        let dx = candidate.pos_x - npc_physics.pos_x;
        let dz = candidate.pos_z - npc_physics.pos_z;
        let dist_sq = dx * dx + dz * dz;
        // Squared-distance pre-check before the relation lookups (tick audit
        // T4); the eligible set and nearest-wins tie-breaking are unchanged.
        if dist_sq > aggro_radius_sq {
            continue;
        }
        if !can_harm(ctx, npc_identity, candidate.identity) {
            continue;
        }

        let resolved = npc_attack_target_from_candidate(npc_physics, candidate);
        let scored = NpcScoredTarget {
            target: resolved,
            threat: npc_target_threat_components(
                ctx,
                npc_identity,
                candidate.identity,
                resolved.distance,
                template.aggro_radius,
                brain,
            ),
        };
        if current_target == Some(candidate.identity) {
            current = Some(scored);
        }
        if best
            .as_ref()
            .is_none_or(|existing| npc_scored_target_is_better(&scored, existing))
        {
            best = Some(scored);
        }
    }
    match (current, best) {
        (Some(current), Some(best))
            if npc_target_stickiness_keeps_scored_current(current, best, target_stickiness) =>
        {
            Some(current.target)
        }
        (_, best) => best.map(|scored| scored.target),
    }
}

fn npc_target_threat_components(
    ctx: &ReducerContext,
    npc_identity: Identity,
    source_identity: Identity,
    distance: f32,
    perception_radius: f32,
    brain: &NpcBrainProfile,
) -> NpcThreatComponents {
    let damage = ctx
        .db
        .npc_threat()
        .threat_key()
        .find(npc_threat_key(npc_identity, source_identity))
        .map_or(0.0, |row| row.damage_threat)
        * brain.damage_threat_weight;
    let proximity = (1.0 - distance / perception_radius.max(0.001)).clamp(0.0, 1.0)
        * brain.proximity_threat_weight;
    NpcThreatComponents { damage, proximity }
}

fn npc_scored_target_is_better(challenger: &NpcScoredTarget, current: &NpcScoredTarget) -> bool {
    challenger.threat.total() > current.threat.total()
        || (challenger.threat.total() == current.threat.total()
            && challenger.target.distance < current.target.distance)
}

fn npc_target_stickiness_keeps_scored_current(
    current: NpcScoredTarget,
    challenger: NpcScoredTarget,
    target_stickiness: f32,
) -> bool {
    if current.target.identity == challenger.target.identity {
        return true;
    }
    if current.threat.damage <= 0.0 && challenger.threat.damage <= 0.0 {
        return npc_target_stickiness_keeps_current(
            current.target.distance,
            challenger.target.distance,
            target_stickiness,
        );
    }
    challenger.threat.total() <= current.threat.total() * (1.0 + target_stickiness.clamp(0.0, 1.0))
}

fn resolve_committed_npc_target(
    ctx: &ReducerContext,
    npc_identity: Identity,
    npc_physics: &NpcPhysics,
    template: &NpcTemplate,
    perception: &NpcPerceptionIndex,
    target_identity: Identity,
    target_is_pinned: bool,
) -> Option<NpcAttackTarget> {
    let npc_context = resolve_player_world_context(ctx, npc_identity)?;
    let candidate = perception.exact(target_identity)?;
    if !world_contexts_share(&npc_context, &candidate.context)
        || !can_harm(ctx, npc_identity, candidate.identity)
    {
        return None;
    }
    let target = npc_attack_target_from_candidate(npc_physics, &candidate);
    if !target_is_pinned && target.distance > template.aggro_radius {
        return None;
    }
    Some(target)
}

fn npc_target_stickiness_keeps_current(
    current_distance: f32,
    challenger_distance: f32,
    target_stickiness: f32,
) -> bool {
    challenger_distance >= current_distance * (1.0 - target_stickiness.clamp(0.0, 1.0))
}

fn npc_attack_target_from_candidate(
    npc_physics: &NpcPhysics,
    candidate: &NpcTargetCandidate,
) -> NpcAttackTarget {
    let dx = candidate.pos_x - npc_physics.pos_x;
    let dz = candidate.pos_z - npc_physics.pos_z;
    let distance = (dx * dx + dz * dz).sqrt();
    let (dir_x, dir_z) = if distance > 0.001 {
        (dx / distance, dz / distance)
    } else {
        (0.0, 1.0)
    };
    NpcAttackTarget {
        identity: candidate.identity,
        pos_x: candidate.pos_x,
        pos_y: candidate.pos_y,
        pos_z: candidate.pos_z,
        hit_radius: candidate.hit_radius,
        distance,
        dir_x,
        dir_z,
    }
}

fn npc_attack_reach(range: f32, target: &NpcAttackTarget) -> f32 {
    range + target.hit_radius
}

fn npc_is_outside_leash(npc: &NpcInstance, physics: &NpcPhysics, leash_radius: f32) -> bool {
    npc_is_outside_leash_from_positions(
        npc.home_x,
        npc.home_z,
        physics.pos_x,
        physics.pos_z,
        leash_radius,
    )
}

fn npc_is_outside_leash_from_positions(
    home_x: f32,
    home_z: f32,
    pos_x: f32,
    pos_z: f32,
    leash_radius: f32,
) -> bool {
    let dx = pos_x - home_x;
    let dz = pos_z - home_z;
    dx * dx + dz * dz > leash_radius * leash_radius
}

fn return_npc_home(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    template: &NpcTemplate,
    move_speed_multiplier: f32,
) {
    let dx = npc.home_x - physics.pos_x;
    let dz = npc.home_z - physics.pos_z;
    let distance = (dx * dx + dz * dz).sqrt();
    if distance <= NPC_CHASE_STOP_EPSILON {
        ctx.db.npc_return_home().identity().delete(npc.identity);
        return;
    }
    if move_speed_multiplier <= 0.0 {
        return;
    }
    let dir_x = dx / distance;
    let dir_z = dz / distance;
    let travel = (template.move_speed * move_speed_multiplier * FIXED_TICK_SECONDS).min(distance);
    move_npc_along(
        ctx,
        now,
        npc,
        physics,
        template,
        dir_x,
        dir_z,
        travel,
        yaw_for_direction(dir_x, dir_z),
    );
}

struct NpcActionSelection {
    action: Option<NpcExecutableAction>,
    target: Option<NpcAttackTarget>,
    score_summary: String,
    hard_reject_summary: String,
}

enum NpcExecutableAction {
    Melee(MeleeAbilityCatalog),
    Spell {
        ability_id: String,
        spell_kind: SpellId,
        range: f32,
        cooldown_ms: u64,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum NpcTacticalBand {
    Approach,
    Hold,
    Retreat,
}

impl NpcExecutableAction {
    fn ability_id(&self) -> &str {
        match self {
            Self::Melee(action) => action.ability_id.as_str(),
            Self::Spell { ability_id, .. } => ability_id.as_str(),
        }
    }

    fn range(&self) -> f32 {
        match self {
            Self::Melee(action) => action.range,
            Self::Spell { range, .. } => *range,
        }
    }

    fn cooldown_ms(&self) -> u64 {
        match self {
            Self::Melee(action) => action.cooldown_ms,
            Self::Spell { cooldown_ms, .. } => *cooldown_ms,
        }
    }
}

fn npc_executable_action_for_ability(
    ctx: &ReducerContext,
    ability_id: &str,
) -> Option<NpcExecutableAction> {
    let ability = ctx
        .db
        .ability_catalog()
        .ability_id()
        .find(ability_id.to_string())?;
    match ability.ability_kind.as_str() {
        "MELEE" => ctx
            .db
            .melee_ability_catalog()
            .ability_id()
            .find(ability.ability_id)
            .map(NpcExecutableAction::Melee),
        "SPELL" => {
            let spell = spell_definition_by_str(ability.action_id.as_str())?;
            Some(NpcExecutableAction::Spell {
                ability_id: ability.ability_id,
                spell_kind: spell.kind.clone(),
                range: spell.max_distance,
                cooldown_ms: spell.cooldown.as_millis() as u64,
            })
        }
        _ => None,
    }
}

fn npc_tactical_band(
    distance: f32,
    preferred_min_distance: f32,
    preferred_max_distance: f32,
    retreat_tolerance: f32,
) -> NpcTacticalBand {
    if distance + retreat_tolerance.max(0.0) < preferred_min_distance {
        NpcTacticalBand::Retreat
    } else if distance > preferred_max_distance {
        NpcTacticalBand::Approach
    } else {
        NpcTacticalBand::Hold
    }
}

#[derive(Default)]
struct NpcActionRejectCounts {
    role: u32,
    selector: u32,
    health: u32,
    target_health: u32,
    target_casting: u32,
    nearby_count: u32,
    required_status: u32,
    forbidden_status: u32,
    missing_ability: u32,
    cooldown: u32,
    distance: u32,
}

impl NpcActionRejectCounts {
    fn summary(&self) -> String {
        let ordered = [
            ("ROLE", self.role),
            ("SELECTOR", self.selector),
            ("SELF_HEALTH", self.health),
            ("TARGET_HEALTH", self.target_health),
            ("TARGET_CASTING", self.target_casting),
            ("NEARBY_COUNT", self.nearby_count),
            ("REQUIRED_STATUS", self.required_status),
            ("FORBIDDEN_STATUS", self.forbidden_status),
            ("MISSING_ABILITY", self.missing_ability),
            ("COOLDOWN", self.cooldown),
            ("DISTANCE", self.distance),
        ];
        let parts: Vec<String> = ordered
            .into_iter()
            .filter(|(_, count)| *count > 0)
            .map(|(reason, count)| format!("{reason}={count}"))
            .collect();
        if parts.is_empty() {
            String::new()
        } else {
            parts.join(" ")
        }
    }
}

fn select_npc_melee_action(
    ctx: &ReducerContext,
    now: Timestamp,
    template: &NpcTemplate,
    state: &NpcState,
    npc_physics: &NpcPhysics,
    current_enemy: &NpcAttackTarget,
    perception: &NpcPerceptionIndex,
    perception_radius: f32,
    nearby: NpcNearbyCounts,
) -> NpcActionSelection {
    let health_pct = if state.max_hp > 0 {
        state.hp.max(0) as f32 / state.max_hp as f32
    } else {
        0.0
    };
    let needs_target_statuses = template.action_kit.iter().any(|entry| {
        !entry.required_target_status.is_empty() || !entry.forbidden_target_status.is_empty()
    });
    let mut rejects = NpcActionRejectCounts::default();
    let mut best: Option<(f32, u32, f32, String, NpcExecutableAction, NpcAttackTarget)> = None;
    for entry in &template.action_kit {
        if !matches!(
            entry.role.as_str(),
            "MELEE_OFFENSE" | "RANGED_OFFENSE" | "BUFF" | "HEAL" | "DEBUFF" | "INTERRUPT"
        ) {
            rejects.role = rejects.role.saturating_add(1);
            continue;
        }
        let Some(target) = select_npc_action_target(
            ctx,
            state.identity,
            npc_physics,
            current_enemy,
            perception,
            perception_radius,
            entry.target_selector.as_str(),
        ) else {
            rejects.selector = rejects.selector.saturating_add(1);
            continue;
        };
        let target_statuses: HashSet<String> = if needs_target_statuses {
            ctx.db
                .status_effect()
                .target()
                .filter(target.identity)
                .filter(|status| now < status.expires_at)
                .flat_map(|status| {
                    [
                        normalize_id(status.effect_kind.as_str()),
                        normalize_id(status.stack_group.as_str()),
                    ]
                })
                .filter(|status| !status.is_empty())
                .collect()
        } else {
            HashSet::new()
        };
        if entry.role == "HEAL" && npc_target_is_full_health(ctx, target.identity) {
            rejects.target_health = rejects.target_health.saturating_add(1);
            continue;
        }
        if entry.role == "INTERRUPT"
            && ctx
                .db
                .active_cast()
                .caster()
                .find(target.identity)
                .is_none()
        {
            rejects.target_casting = rejects.target_casting.saturating_add(1);
            continue;
        }
        if health_pct < entry.min_self_health_pct || health_pct > entry.max_self_health_pct {
            rejects.health = rejects.health.saturating_add(1);
            continue;
        }
        if !npc_action_count_requirements_met(
            entry.min_nearby_allies,
            entry.min_nearby_enemies,
            nearby,
        ) {
            rejects.nearby_count = rejects.nearby_count.saturating_add(1);
            continue;
        }
        if !entry.required_target_status.is_empty()
            && !target_statuses.contains(entry.required_target_status.as_str())
        {
            rejects.required_status = rejects.required_status.saturating_add(1);
            continue;
        }
        if !entry.forbidden_target_status.is_empty()
            && target_statuses.contains(entry.forbidden_target_status.as_str())
        {
            rejects.forbidden_status = rejects.forbidden_status.saturating_add(1);
            continue;
        }
        let Some(ability) = ctx
            .db
            .ability_catalog()
            .ability_id()
            .find(entry.ability_id.clone())
        else {
            rejects.missing_ability = rejects.missing_ability.saturating_add(1);
            continue;
        };
        let action = if ability.ability_kind == "MELEE" && entry.role == "MELEE_OFFENSE" {
            let Some(melee) = ctx
                .db
                .melee_ability_catalog()
                .ability_id()
                .find(entry.ability_id.clone())
            else {
                rejects.missing_ability = rejects.missing_ability.saturating_add(1);
                continue;
            };
            NpcExecutableAction::Melee(melee)
        } else if ability.ability_kind == "SPELL"
            && matches!(
                entry.role.as_str(),
                "RANGED_OFFENSE" | "BUFF" | "HEAL" | "DEBUFF" | "INTERRUPT"
            )
        {
            let Some(spell) = spell_definition_by_str(ability.action_id.as_str()) else {
                rejects.missing_ability = rejects.missing_ability.saturating_add(1);
                continue;
            };
            NpcExecutableAction::Spell {
                ability_id: ability.ability_id,
                spell_kind: spell.kind.clone(),
                range: spell.max_distance,
                cooldown_ms: spell.cooldown.as_millis() as u64,
            }
        } else {
            rejects.role = rejects.role.saturating_add(1);
            continue;
        };
        let on_cooldown = match &action {
            NpcExecutableAction::Melee(action) => {
                is_on_named_cooldown(ctx, state.identity, action.ability_id.as_str(), now)
            }
            NpcExecutableAction::Spell { spell_kind, .. } => {
                is_on_spell_cooldown(ctx, state.identity, spell_kind, now)
            }
        };
        if on_cooldown {
            rejects.cooldown = rejects.cooldown.saturating_add(1);
            continue;
        }
        let Some(score) = npc_action_distance_score(
            entry.base_utility,
            entry.preferred_min_distance,
            entry.preferred_max_distance,
            target.distance,
            entry.movement_may_enable,
        ) else {
            rejects.distance = rejects.distance.saturating_add(1);
            continue;
        };
        let replace = best
            .as_ref()
            .is_none_or(|(best_score, best_order, _, _, _, _)| {
                score.total_cmp(best_score).is_gt()
                    || (score.total_cmp(best_score).is_eq() && entry.sort_order < *best_order)
            });
        if replace {
            best = Some((
                score,
                entry.sort_order,
                entry.base_utility,
                entry.target_selector.clone(),
                action,
                target,
            ));
        }
    }

    let hard_reject_summary = rejects.summary();
    let Some((score, _, base_utility, selector, action, target)) = best else {
        return NpcActionSelection {
            action: None,
            target: None,
            score_summary: format!("distance={:.3}", current_enemy.distance),
            hard_reject_summary: if hard_reject_summary.is_empty() {
                "NO_ACTIONS_AUTHORED".to_string()
            } else {
                hard_reject_summary
            },
        };
    };
    NpcActionSelection {
        action: Some(action),
        target: Some(target),
        score_summary: format!(
            "selector={selector} utility={score:.3} base={base_utility:.3} distance={:.3}",
            target.distance
        ),
        hard_reject_summary,
    }
}

fn npc_target_is_full_health(ctx: &ReducerContext, target: Identity) -> bool {
    if let Some(state) = ctx.db.npc_state().identity().find(target) {
        return !npc_health_needs_healing(state.hp, state.max_hp);
    }
    ctx.db
        .player_state()
        .player_id()
        .find(target)
        .is_some_and(|state| !npc_health_needs_healing(state.hp, state.max_hp))
}

fn npc_health_needs_healing(hp: i32, max_hp: i32) -> bool {
    max_hp > 0 && hp < max_hp
}

fn select_npc_action_target(
    ctx: &ReducerContext,
    npc_identity: Identity,
    npc_physics: &NpcPhysics,
    current_enemy: &NpcAttackTarget,
    perception: &NpcPerceptionIndex,
    perception_radius: f32,
    selector: &str,
) -> Option<NpcAttackTarget> {
    match selector {
        "CURRENT_ENEMY" => Some(*current_enemy),
        "SELF" => perception
            .exact(npc_identity)
            .map(|candidate| npc_attack_target_from_candidate(npc_physics, &candidate)),
        "NEAREST_ENEMY" | "LOWEST_HEALTH_ALLY" => {
            let relation = if selector == "NEAREST_ENEMY" {
                NpcCandidateRelation::Enemy
            } else {
                NpcCandidateRelation::Ally
            };
            let candidates = perception.relation_candidates(
                ctx,
                npc_identity,
                npc_physics.pos_x,
                npc_physics.pos_z,
                perception_radius,
                relation,
            );
            select_npc_candidate(selector, npc_physics, &candidates)
        }
        _ => None,
    }
}

fn select_npc_candidate(
    selector: &str,
    npc_physics: &NpcPhysics,
    candidates: &[NpcTargetCandidate],
) -> Option<NpcAttackTarget> {
    let candidate = match selector {
        "NEAREST_ENEMY" => candidates.iter().min_by(|a, b| {
            npc_candidate_distance_sq(npc_physics, a)
                .total_cmp(&npc_candidate_distance_sq(npc_physics, b))
                .then_with(|| npc_identity_cmp(a.identity, b.identity))
        }),
        "LOWEST_HEALTH_ALLY" => candidates.iter().min_by(|a, b| {
            npc_health_fraction(a.hp, a.max_hp)
                .total_cmp(&npc_health_fraction(b.hp, b.max_hp))
                .then_with(|| npc_identity_cmp(a.identity, b.identity))
        }),
        _ => None,
    }?;
    Some(npc_attack_target_from_candidate(npc_physics, candidate))
}

fn npc_candidate_distance_sq(physics: &NpcPhysics, candidate: &NpcTargetCandidate) -> f32 {
    let dx = candidate.pos_x - physics.pos_x;
    let dz = candidate.pos_z - physics.pos_z;
    dx * dx + dz * dz
}

fn npc_health_fraction(hp: i32, max_hp: i32) -> f32 {
    if max_hp > 0 {
        hp.max(0) as f32 / max_hp as f32
    } else {
        0.0
    }
}

fn npc_identity_cmp(left: Identity, right: Identity) -> std::cmp::Ordering {
    left.to_hex().cmp(&right.to_hex())
}

fn npc_action_count_requirements_met(
    min_nearby_allies: u32,
    min_nearby_enemies: u32,
    nearby: NpcNearbyCounts,
) -> bool {
    nearby.allies >= min_nearby_allies && nearby.enemies >= min_nearby_enemies
}

fn npc_action_distance_score(
    base_utility: f32,
    preferred_min_distance: f32,
    preferred_max_distance: f32,
    distance: f32,
    movement_may_enable: bool,
) -> Option<f32> {
    let distance_error = if distance < preferred_min_distance {
        preferred_min_distance - distance
    } else if distance > preferred_max_distance {
        if !movement_may_enable {
            return None;
        }
        distance - preferred_max_distance
    } else {
        0.0
    };
    Some(base_utility - distance_error / preferred_max_distance.max(1.0))
}

#[allow(clippy::too_many_arguments)]
fn record_npc_decision_debug(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    template: &NpcTemplate,
    selection: &NpcActionSelection,
    target: &NpcAttackTarget,
    target_was_pinned: bool,
) {
    if !npc_ai_debug_enabled() {
        return;
    }
    let threat_summary = if target_was_pinned {
        "PINNED_TARGET".to_string()
    } else if let Some(brain) = npc_brain_profile(template.brain_profile_id.as_str()) {
        let threat = npc_target_threat_components(
            ctx,
            npc.identity,
            target.identity,
            target.distance,
            template.aggro_radius,
            brain,
        );
        format!(
            "damage={:.3} proximity={:.3} total={:.3}",
            threat.damage,
            threat.proximity,
            threat.total()
        )
    } else {
        "BRAIN_PROFILE_MISSING".to_string()
    };
    let previous = ctx.db.npc_decision_debug().identity().find(npc.identity);
    let row = NpcDecisionDebug {
        identity: npc.identity,
        decision_sequence: previous
            .as_ref()
            .map_or(1, |row| row.decision_sequence.saturating_add(1)),
        considered_action_count: template.action_kit.len() as u32,
        chosen_ability_id: selection
            .action
            .as_ref()
            .map_or_else(String::new, |action| action.ability_id().to_string()),
        chosen_target: target.identity,
        target_was_pinned,
        score_summary: selection.score_summary.clone(),
        hard_reject_summary: selection.hard_reject_summary.clone(),
        threat_summary,
        updated_at: now,
    };
    if previous.is_some() {
        ctx.db.npc_decision_debug().identity().update(row);
    } else {
        ctx.db.npc_decision_debug().insert(row);
    }
}

fn chase_npc_toward_target(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    template: &NpcTemplate,
    action_range: f32,
    target: &NpcAttackTarget,
    move_speed_multiplier: f32,
) -> NpcPhysics {
    let desired_yaw = yaw_for_direction(target.dir_x, target.dir_z);
    let stop_distance = (npc_attack_reach(action_range, target) - NPC_CHASE_STOP_EPSILON).max(0.0);
    let remaining = (target.distance - stop_distance).max(0.0);
    let travel = (template.move_speed * move_speed_multiplier * FIXED_TICK_SECONDS).min(remaining);
    if travel <= f32::EPSILON {
        return update_npc_facing(ctx, now, physics, desired_yaw);
    }

    move_npc_along(
        ctx,
        now,
        npc,
        physics,
        template,
        target.dir_x,
        target.dir_z,
        travel,
        desired_yaw,
    )
}

fn retreat_npc_from_target(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    template: &NpcTemplate,
    target: &NpcAttackTarget,
    move_speed_multiplier: f32,
) -> NpcPhysics {
    let travel = template.move_speed * move_speed_multiplier * FIXED_TICK_SECONDS;
    if travel <= f32::EPSILON {
        return face_npc_target(ctx, now, physics, target);
    }
    move_npc_along(
        ctx,
        now,
        npc,
        physics,
        template,
        -target.dir_x,
        -target.dir_z,
        travel,
        yaw_for_direction(target.dir_x, target.dir_z),
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn start_npc_forced_movement(
    ctx: &ReducerContext,
    now: Timestamp,
    source: Identity,
    target: Identity,
    dir_x: f32,
    dir_z: f32,
    distance: f32,
    duration_ms: u64,
    movement_kind: &str,
) -> bool {
    let Some(npc) = ctx.db.npc_instance().identity().find(target) else {
        return false;
    };
    let Some(state) = ctx.db.npc_state().identity().find(target) else {
        return false;
    };
    if !state.alive {
        return false;
    }
    let Some(physics) = ctx.db.npc_physics().identity().find(target) else {
        return false;
    };

    let intended_x = physics.pos_x + dir_x * distance;
    let intended_z = physics.pos_z + dir_z * distance;
    let (end_x, end_y, end_z) = resolve_npc_movement_path(
        ctx,
        &npc,
        target,
        physics.pos_x,
        physics.pos_y,
        physics.pos_z,
        intended_x,
        intended_z,
        state.hit_radius,
        state.hit_height,
    );
    log::info!(
        "[NPC_FORCED_MOVEMENT] kind={} source={} target={} distance={:.3} duration_ms={} start=({:.3},{:.3},{:.3}) intended_end=({:.3},{:.3}) baked_end=({:.3},{:.3},{:.3})",
        movement_kind,
        source.to_hex(),
        target.to_hex(),
        distance,
        duration_ms,
        physics.pos_x,
        physics.pos_y,
        physics.pos_z,
        intended_x,
        intended_z,
        end_x,
        end_y,
        end_z
    );

    ctx.db.npc_forced_movement().identity().delete(target);
    ctx.db.npc_forced_movement().insert(NpcForcedMovement {
        identity: target,
        started_at: now,
        duration_ms,
        start_x: physics.pos_x,
        start_y: physics.pos_y,
        start_z: physics.pos_z,
        end_x,
        end_y,
        end_z,
    });
    true
}

pub(crate) fn tick_npc_forced_movement(ctx: &ReducerContext, now: Timestamp) {
    let runtimes: Vec<NpcForcedMovement> = ctx.db.npc_forced_movement().iter().collect();
    for runtime in runtimes {
        let Some(npc) = ctx.db.npc_instance().identity().find(runtime.identity) else {
            clear_npc_forced_movement(ctx, runtime.identity);
            continue;
        };
        let Some(state) = ctx.db.npc_state().identity().find(runtime.identity) else {
            clear_npc_forced_movement(ctx, runtime.identity);
            continue;
        };
        if !state.alive {
            clear_npc_forced_movement(ctx, runtime.identity);
            continue;
        }
        let Some(mut physics) = ctx.db.npc_physics().identity().find(runtime.identity) else {
            clear_npc_forced_movement(ctx, runtime.identity);
            continue;
        };

        let (desired_x, desired_y, desired_z, mut finished) =
            sample_npc_forced_movement_pose(&runtime, now);
        let (resolved_x, resolved_z) = resolve_active_world_obstacle_movement(
            ctx,
            runtime.identity,
            physics.pos_x,
            physics.pos_z,
            desired_x,
            desired_z,
            state.hit_radius.max(0.1),
            physics.pos_y,
            state.hit_height.max(0.5),
        );
        if (resolved_x - desired_x).abs() > 0.0001 || (resolved_z - desired_z).abs() > 0.0001 {
            finished = true;
        }
        let (arena_seed, flat_ground_only) = npc_movement_world(ctx, &npc);
        let open_world_scene_name = if npc.world_kind.eq_ignore_ascii_case(WORLD_KIND_OPEN) {
            Some(npc.open_world_scene_name.as_str())
        } else {
            None
        };
        let resolved_y = surface_height_for_world_at_y_with_layout_for_scene(
            arena_seed,
            flat_ground_only,
            open_world_scene_name,
            resolved_x,
            resolved_z,
            desired_y,
        );

        physics.pos_x = resolved_x;
        physics.pos_y = resolved_y;
        physics.pos_z = resolved_z;
        physics.updated_at = now;
        ctx.db.npc_physics().identity().update(physics.clone());
        crate::combat::position_history::record_position_sample(
            ctx,
            physics.identity,
            physics.pos_x,
            physics.pos_y,
            physics.pos_z,
            physics.yaw,
            now,
        );
        crate::combat::position_history::stamp_rewind_barrier(ctx, physics.identity, now);

        if finished {
            clear_npc_forced_movement(ctx, runtime.identity);
        }
    }
}

pub(crate) fn has_npc_forced_movement(ctx: &ReducerContext, identity: Identity) -> bool {
    ctx.db
        .npc_forced_movement()
        .identity()
        .find(identity)
        .is_some()
}

pub(crate) fn clear_npc_forced_movement(ctx: &ReducerContext, identity: Identity) {
    ctx.db.npc_forced_movement().identity().delete(identity);
}

pub(crate) fn npc_knockback_resistance(ctx: &ReducerContext, identity: Identity) -> f32 {
    ctx.db
        .npc_instance()
        .identity()
        .find(identity)
        .and_then(|npc| npc_template(npc.template_id.as_str()))
        .map(|template| template.knockback_resistance)
        .unwrap_or(0.0)
}

fn sample_npc_forced_movement_pose(
    runtime: &NpcForcedMovement,
    now: Timestamp,
) -> (f32, f32, f32, bool) {
    let start_micros = runtime.started_at.to_micros_since_unix_epoch();
    let duration_micros = (runtime.duration_ms as i64).saturating_mul(1000);
    let end_micros = start_micros.saturating_add(duration_micros);
    let now_micros = now.to_micros_since_unix_epoch();
    let finished = runtime.duration_ms == 0 || now_micros >= end_micros;
    let progress = if duration_micros <= 0 {
        1.0
    } else {
        ((now_micros - start_micros) as f64 / duration_micros as f64).clamp(0.0, 1.0) as f32
    };
    (
        runtime.start_x + (runtime.end_x - runtime.start_x) * progress,
        runtime.start_y + (runtime.end_y - runtime.start_y) * progress,
        runtime.start_z + (runtime.end_z - runtime.start_z) * progress,
        finished,
    )
}

#[allow(clippy::too_many_arguments)]
fn resolve_npc_movement_path(
    ctx: &ReducerContext,
    npc: &NpcInstance,
    identity: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    hit_radius: f32,
    hit_height: f32,
) -> (f32, f32, f32) {
    let (arena_seed, flat_ground_only) = npc_movement_world(ctx, npc);
    let open_world_scene_name = if npc.world_kind.eq_ignore_ascii_case(WORLD_KIND_OPEN) {
        Some(npc.open_world_scene_name.as_str())
    } else {
        None
    };
    let dx = target_x - start_x;
    let dz = target_z - start_z;
    let travel = (dx * dx + dz * dz).sqrt();
    let step_count = ((travel / NPC_CHASE_COLLISION_STEP).ceil() as usize).max(1);
    let step_x = dx / step_count as f32;
    let step_z = dz / step_count as f32;
    let mut next_x = start_x;
    let mut next_y = start_y;
    let mut next_z = start_z;

    for _ in 0..step_count {
        let step_target_x = next_x + step_x;
        let step_target_z = next_z + step_z;
        let (resolved_x, resolved_z) =
            resolve_world_horizontal_sweep_collision_y_with_layout_for_scene(
                arena_seed,
                flat_ground_only,
                open_world_scene_name,
                next_x,
                next_z,
                step_target_x,
                step_target_z,
                hit_radius.max(0.1),
                hit_height.max(0.5),
                next_y,
            );
        let (resolved_x, resolved_z) = resolve_active_world_obstacle_movement(
            ctx,
            identity,
            next_x,
            next_z,
            resolved_x,
            resolved_z,
            hit_radius.max(0.1),
            next_y,
            hit_height.max(0.5),
        );
        next_x = resolved_x;
        next_z = resolved_z;
        next_y = surface_height_for_world_at_y_with_layout_for_scene(
            arena_seed,
            flat_ground_only,
            open_world_scene_name,
            next_x,
            next_z,
            next_y,
        );
    }

    (next_x, next_y, next_z)
}

#[allow(clippy::too_many_arguments)]
fn move_npc_along(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    template: &NpcTemplate,
    dir_x: f32,
    dir_z: f32,
    travel: f32,
    desired_yaw: f32,
) -> NpcPhysics {
    let (next_x, next_y, next_z) = resolve_npc_movement_path(
        ctx,
        npc,
        npc.identity,
        physics.pos_x,
        physics.pos_y,
        physics.pos_z,
        physics.pos_x + dir_x * travel,
        physics.pos_z + dir_z * travel,
        template.hit_radius,
        template.hit_height,
    );

    let mut next = physics.clone();
    next.pos_x = next_x;
    next.pos_y = next_y;
    next.pos_z = next_z;
    next.yaw = desired_yaw;
    next.updated_at = now;
    ctx.db.npc_physics().identity().update(next.clone());
    crate::combat::position_history::record_position_sample(
        ctx,
        next.identity,
        next.pos_x,
        next.pos_y,
        next.pos_z,
        next.yaw,
        now,
    );
    next
}

fn face_npc_target(
    ctx: &ReducerContext,
    now: Timestamp,
    physics: &NpcPhysics,
    target: &NpcAttackTarget,
) -> NpcPhysics {
    update_npc_facing(
        ctx,
        now,
        physics,
        yaw_for_direction(target.dir_x, target.dir_z),
    )
}

fn update_npc_facing(
    ctx: &ReducerContext,
    now: Timestamp,
    physics: &NpcPhysics,
    desired_yaw: f32,
) -> NpcPhysics {
    if yaw_delta_abs(physics.yaw, desired_yaw) <= NPC_FACE_EPSILON {
        return physics.clone();
    }

    let mut next = physics.clone();
    next.yaw = desired_yaw;
    next.updated_at = now;
    ctx.db.npc_physics().identity().update(next.clone());
    crate::combat::position_history::record_position_sample(
        ctx,
        next.identity,
        next.pos_x,
        next.pos_y,
        next.pos_z,
        next.yaw,
        now,
    );
    next
}

fn npc_movement_world(ctx: &ReducerContext, npc: &NpcInstance) -> (Option<u64>, bool) {
    if !npc.world_kind.eq_ignore_ascii_case(WORLD_KIND_INSTANCE) {
        return (None, false);
    }

    let Some(instance_id) = npc.instance_id else {
        return (None, false);
    };
    let flat_ground_only = is_training_instance(ctx, instance_id);
    let arena_seed = ctx
        .db
        .arena_instance()
        .id()
        .find(instance_id)
        .map(|arena| arena.seed);
    (arena_seed, flat_ground_only)
}

/// Swing start (S3): emit the CAST telegraph and schedule damage resolution
/// `attack_windup_ms` later. The CAST carries the authored windup in the same
/// scalar contract player melee uses for its impact delay.
fn begin_npc_melee_swing(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    _physics: &NpcPhysics,
    template: &NpcTemplate,
    action: &MeleeAbilityCatalog,
    target: &NpcAttackTarget,
    runtime: &mut NpcCombatRuntime,
) -> bool {
    if pending_melee_commitment_target_for_source(ctx, npc.identity, NPC_MELEE_SOURCE_KIND)
        .is_some()
    {
        return false;
    }
    let Some(audience) = TargetAudience::from_wire(action.target_audience.as_str()) else {
        return false;
    };
    let action_instance_id = format!("npc:{}:{}", npc.identity.to_hex(), timestamp_to_micros(now));
    let damage = if npc_attacks_are_harmless() {
        0
    } else {
        action.base_damage
    };
    let windup_ms = npc_action_windup_ms(template, action.ability_id.as_str());
    let committed = commit_server_actor_targeted_melee(
        ctx,
        now,
        ServerActorMeleeCommitment {
            source: npc.identity,
            target: target.identity,
            action_instance_id: action_instance_id.as_str(),
            action_kind: NPC_MELEE_ACTION_KIND,
            ability_id: action.ability_id.as_str(),
            event_source: NPC_MELEE_SOURCE_KIND,
            target_audience: audience,
            damage,
            range: action.range,
            windup_ms,
            parry_behavior: NPC_MELEE_PARRY_BEHAVIOR,
            block_behavior: NPC_MELEE_BLOCK_BEHAVIOR,
            requires_target_los: action.requires_target_los,
            facing_arc_radians: std::f32::consts::PI,
            direct_action_key: action.ability_id.as_str(),
        },
    );
    if committed {
        let recovery_ms = npc_action_recovery_ms(template, action.ability_id.as_str());
        stamp_npc_movement_hold(
            runtime,
            now + Duration::from_millis(windup_ms.saturating_add(recovery_ms)),
        );
    }
    committed
}

fn npc_action_windup_ms(template: &NpcTemplate, ability_id: &str) -> u64 {
    template
        .action_kit
        .iter()
        .find(|entry| entry.ability_id == ability_id)
        .map_or(template.attack_windup_ms, |entry| {
            if entry.windup_ms == 0 {
                template.attack_windup_ms
            } else {
                entry.windup_ms
            }
        })
}

fn npc_action_recovery_ms(template: &NpcTemplate, ability_id: &str) -> u64 {
    template
        .action_kit
        .iter()
        .find(|entry| entry.ability_id == ability_id)
        .map_or(template.attack_recovery_ms, |entry| {
            if entry.recovery_ms == 0 {
                template.attack_recovery_ms
            } else {
                entry.recovery_ms
            }
        })
}

fn npc_movement_hold_active(runtime: &NpcCombatRuntime, now: Timestamp) -> bool {
    timestamp_to_micros(now) < runtime.hold_movement_until_micros
}

fn stamp_npc_movement_hold(runtime: &mut NpcCombatRuntime, hold_until: Timestamp) {
    let hold_until_micros = timestamp_to_micros(hold_until);
    if hold_until_micros <= runtime.hold_movement_until_micros {
        return;
    }
    runtime.hold_movement_until_micros = hold_until_micros;
}

fn npc_spell_cast_started_at(ctx: &ReducerContext, identity: Identity, now: Timestamp) -> bool {
    let now_micros = timestamp_to_micros(now);
    ctx.db.combat_event().caster().filter(identity).any(|row| {
        row.created_at_micros == now_micros
            && row.event_type == "CAST"
            && row.source_kind == "SPELL"
    })
}

pub(crate) fn interrupt_npc_actions_for_crowd_control(
    ctx: &ReducerContext,
    identity: Identity,
    now: Timestamp,
) {
    interrupt_server_actor_melee_commitments(ctx, identity, NPC_MELEE_SOURCE_KIND, now);
}

/// Present-time target state for a resolving swing: actor-generic current pose,
/// vitality, world, and relation checks for the single committed target.
fn resolve_npc_swing_target(
    ctx: &ReducerContext,
    npc_identity: Identity,
    npc_physics: &NpcPhysics,
    target_identity: Identity,
) -> Option<NpcAttackTarget> {
    if ctx
        .db
        .player_state()
        .player_id()
        .find(target_identity)
        .is_some_and(|state| state.is_dummy)
    {
        return None;
    }
    let actors = CombatActorSnapshotSet::collect(ctx);
    let target_snapshot = actors
        .index_by_id()
        .get(&target_identity)
        .and_then(|index| actors.as_slice().get(*index))?;
    if !target_snapshot.alive {
        return None;
    }
    let npc_context = resolve_player_world_context(ctx, npc_identity)?;
    let target_context = resolve_player_world_context(ctx, target_identity)?;
    if !world_contexts_share(&npc_context, &target_context) {
        return None;
    }
    if !can_harm(ctx, npc_identity, target_identity) {
        return None;
    }
    let dx = target_snapshot.pos_x - npc_physics.pos_x;
    let dz = target_snapshot.pos_z - npc_physics.pos_z;
    let distance = (dx * dx + dz * dz).sqrt();
    let (dir_x, dir_z) = if distance > 0.001 {
        (dx / distance, dz / distance)
    } else {
        (0.0, 1.0)
    };
    Some(NpcAttackTarget {
        identity: target_identity,
        pos_x: target_snapshot.pos_x,
        pos_y: target_snapshot.pos_y,
        pos_z: target_snapshot.pos_z,
        hit_radius: target_snapshot.hit_radius,
        distance,
        dir_x,
        dir_z,
    })
}

fn despawn_npc_identity(ctx: &ReducerContext, identity: Identity) {
    clear_npc_forced_movement(ctx, identity);
    clear_npc_combat_runtime(ctx, identity);
    clear_actor_cooldowns(ctx, identity);
    clear_npc_threat(ctx, identity);
    ctx.db.npc_target_override().identity().delete(identity);
    ctx.db.npc_return_home().identity().delete(identity);
    ctx.db.npc_decision_debug().identity().delete(identity);
    clear_pending_melee_impacts_for_source(ctx, identity);
    clear_loot_for_anchor(ctx, identity);
    if ctx.db.npc_despawn().identity().find(identity).is_some() {
        ctx.db.npc_despawn().identity().delete(identity);
    }
    if ctx.db.npc_instance().identity().find(identity).is_some() {
        ctx.db.npc_instance().identity().delete(identity);
    }
    if ctx.db.npc_state().identity().find(identity).is_some() {
        ctx.db.npc_state().identity().delete(identity);
    }
    if ctx.db.npc_physics().identity().find(identity).is_some() {
        ctx.db.npc_physics().identity().delete(identity);
    }
    crate::combat::position_history::clear_position_history(ctx, identity);
}

pub(crate) fn record_npc_damage_threat(
    ctx: &ReducerContext,
    npc_identity: Identity,
    source_identity: Identity,
    damage: i32,
) {
    if source_identity == Identity::ZERO || source_identity == npc_identity || damage <= 0 {
        return;
    }
    let threat_key = npc_threat_key(npc_identity, source_identity);
    let previous = ctx.db.npc_threat().threat_key().find(&threat_key);
    let row = NpcThreat {
        threat_key,
        npc_identity,
        source_identity,
        damage_threat: previous
            .as_ref()
            .map_or(damage as f32, |row| row.damage_threat + damage as f32),
        updated_at: ctx.timestamp,
    };
    if previous.is_some() {
        ctx.db.npc_threat().threat_key().update(row);
    } else {
        ctx.db.npc_threat().insert(row);
    }
}

fn clear_npc_threat(ctx: &ReducerContext, identity: Identity) {
    let mut keys: HashSet<String> = ctx
        .db
        .npc_threat()
        .npc_identity()
        .filter(identity)
        .map(|row| row.threat_key)
        .collect();
    keys.extend(
        ctx.db
            .npc_threat()
            .source_identity()
            .filter(identity)
            .map(|row| row.threat_key),
    );
    for key in keys {
        ctx.db.npc_threat().threat_key().delete(key);
    }
}

fn npc_threat_key(npc_identity: Identity, source_identity: Identity) -> String {
    format!("{}:{}", npc_identity.to_hex(), source_identity.to_hex())
}

fn clear_npc_combat_runtime(ctx: &ReducerContext, identity: Identity) {
    if ctx
        .db
        .npc_combat_runtime()
        .identity()
        .find(identity)
        .is_some()
    {
        ctx.db.npc_combat_runtime().identity().delete(identity);
    }
}

fn upsert_npc_combat_runtime(ctx: &ReducerContext, row: NpcCombatRuntime) {
    if let Some(existing) = ctx.db.npc_combat_runtime().identity().find(row.identity) {
        if existing == row {
            // Value-identical ("waiting for next attack" branch): skip the
            // per-tick rewrite (tick audit T3 slice 3).
            return;
        }
        crate::tick_metrics::record_table_write(
            crate::tick_metrics::TableWriteKind::NpcCombatRuntime,
        );
        ctx.db.npc_combat_runtime().identity().update(row);
    } else {
        crate::tick_metrics::record_table_write(
            crate::tick_metrics::TableWriteKind::NpcCombatRuntime,
        );
        ctx.db.npc_combat_runtime().insert(row);
    }
}

fn next_npc_sequence(ctx: &ReducerContext, owner: Identity) -> u64 {
    if let Some(mut counter) = ctx.db.npc_spawn_counter().owner().find(owner) {
        let sequence = counter.next_sequence;
        counter.next_sequence = counter.next_sequence.saturating_add(1);
        ctx.db.npc_spawn_counter().owner().update(counter);
        return sequence;
    }

    ctx.db.npc_spawn_counter().insert(NpcSpawnCounter {
        owner,
        next_sequence: 1,
    });
    0
}

fn npc_identity(owner: Identity, sequence: u64) -> Result<Identity, String> {
    let owner_hex = owner.to_hex();
    let owner_tail = owner_hex
        .get(32..64)
        .ok_or_else(|| format!("invalid owner identity hex '{}'", owner_hex))?;
    let owner_bits = u128::from_str_radix(owner_tail, 16)
        .map_err(|error| format!("invalid owner identity tail '{}': {}", owner_tail, error))?;
    let sequence_bits = u128::from(sequence) & 0x0000_0000_ffff_ffff;
    let encoded = ((owner_bits & 0xffff_ffff_ffff_ffff) << 32) | sequence_bits;
    let hex = format!("{NPC_ID_MAGIC:032x}{encoded:032x}");
    Identity::from_hex(hex.as_str()).map_err(|error| {
        format!(
            "invalid NPC identity owner={} sequence={} hex={} error={}",
            owner.to_hex(),
            sequence,
            hex,
            error
        )
    })
}

fn normalize_id(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

fn wrap_yaw(yaw: f32) -> f32 {
    yaw.rem_euclid(std::f32::consts::TAU)
}

fn yaw_for_direction(dir_x: f32, dir_z: f32) -> f32 {
    wrap_yaw(dir_x.atan2(dir_z))
}

fn yaw_delta_abs(a: f32, b: f32) -> f32 {
    ((a - b + std::f32::consts::PI).rem_euclid(std::f32::consts::TAU) - std::f32::consts::PI).abs()
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::Path;
    use std::time::Duration;

    use super::{
        npc_action_count_requirements_met, npc_action_distance_score, npc_action_recovery_ms,
        npc_catalog, npc_decision_interval_ms, npc_health_fraction, npc_health_needs_healing,
        npc_identity, npc_identity_cmp, npc_is_outside_leash_from_positions,
        npc_movement_hold_active, npc_tactical_band, npc_target_stickiness_keeps_current,
        npc_target_stickiness_keeps_scored_current, npc_template, npc_threat_key,
        parse_npc_catalog, sample_npc_forced_movement_pose, stamp_npc_movement_hold,
        visual_id_for_template, yaw_for_direction, NpcActionRejectCounts, NpcAttackTarget,
        NpcCombatRuntime, NpcFaction, NpcForcedMovement, NpcNearbyCounts, NpcScoredTarget,
        NpcTacticalBand, NpcThreatComponents, NPC_CATALOG_JSON, NPC_FACTION_FRIENDLY,
        NPC_FACTION_HOSTILE, NPC_FACTION_NEUTRAL, NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD,
        NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR, NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD,
    };
    use spacetimedb::{Identity, Timestamp};

    #[test]
    fn npc_faction_wire_values_are_stable() {
        assert_eq!(NpcFaction::Hostile.as_str(), NPC_FACTION_HOSTILE);
        assert_eq!(NpcFaction::Neutral.as_str(), NPC_FACTION_NEUTRAL);
        assert_eq!(NpcFaction::Friendly.as_str(), NPC_FACTION_FRIENDLY);
        assert_eq!(NpcFaction::from_wire("hostile"), Some(NpcFaction::Hostile));
        assert_eq!(NpcFaction::from_wire("party_member"), None);
    }

    #[test]
    fn npc_forced_movement_sampling_lerps_and_finishes_at_deadline() {
        let started_at = Timestamp::from_micros_since_unix_epoch(1_000_000);
        let runtime = NpcForcedMovement {
            identity: Identity::ZERO,
            started_at,
            duration_ms: 400,
            start_x: 2.0,
            start_y: 3.0,
            start_z: 4.0,
            end_x: 6.0,
            end_y: 5.0,
            end_z: 12.0,
        };

        assert_eq!(
            sample_npc_forced_movement_pose(
                &runtime,
                Timestamp::from_micros_since_unix_epoch(1_200_000)
            ),
            (4.0, 4.0, 8.0, false)
        );
        assert_eq!(
            sample_npc_forced_movement_pose(
                &runtime,
                Timestamp::from_micros_since_unix_epoch(1_400_000)
            ),
            (6.0, 5.0, 12.0, true)
        );
    }

    #[test]
    fn npc_leash_boundary_is_inclusive() {
        assert!(!npc_is_outside_leash_from_positions(
            10.0, 20.0, 13.0, 24.0, 5.0
        ));
        assert!(npc_is_outside_leash_from_positions(
            10.0, 20.0, 13.1, 24.0, 5.0
        ));
    }

    #[test]
    fn npc_action_distance_scoring_requires_movement_permission() {
        assert_eq!(
            npc_action_distance_score(1.0, 1.0, 3.0, 2.0, false),
            Some(1.0)
        );
        assert!(npc_action_distance_score(1.0, 1.0, 3.0, 5.0, false).is_none());
        assert!(npc_action_distance_score(1.0, 1.0, 3.0, 5.0, true).unwrap() < 1.0);
    }

    #[test]
    fn npc_ranged_tactical_band_respects_retreat_tolerance() {
        assert_eq!(
            npc_tactical_band(3.9, 6.0, 16.0, 1.0),
            NpcTacticalBand::Retreat
        );
        assert_eq!(
            npc_tactical_band(5.0, 6.0, 16.0, 1.0),
            NpcTacticalBand::Hold
        );
        assert_eq!(
            npc_tactical_band(10.0, 6.0, 16.0, 1.0),
            NpcTacticalBand::Hold
        );
        assert_eq!(
            npc_tactical_band(16.1, 6.0, 16.0, 1.0),
            NpcTacticalBand::Approach
        );
    }

    #[test]
    fn npc_action_count_requirements_use_indexed_perception_totals() {
        let nearby = NpcNearbyCounts {
            allies: 2,
            enemies: 3,
        };

        assert!(npc_action_count_requirements_met(2, 3, nearby));
        assert!(!npc_action_count_requirements_met(3, 3, nearby));
        assert!(!npc_action_count_requirements_met(2, 4, nearby));
    }

    #[test]
    fn npc_target_selectors_use_deterministic_health_and_identity_ordering() {
        assert!(npc_health_fraction(25, 100) < npc_health_fraction(2, 4));
        assert_eq!(npc_health_fraction(10, 0), 0.0);

        let first = Identity::from_hex(format!("{:064x}", 1).as_str()).unwrap();
        let second = Identity::from_hex(format!("{:064x}", 2).as_str()).unwrap();
        assert_eq!(npc_identity_cmp(first, second), std::cmp::Ordering::Less);
    }

    #[test]
    fn npc_heal_gate_and_inspector_reason_are_deterministic() {
        assert!(npc_health_needs_healing(99, 100));
        assert!(!npc_health_needs_healing(100, 100));
        assert!(!npc_health_needs_healing(1, 0));

        let rejects = NpcActionRejectCounts {
            target_health: 1,
            target_casting: 2,
            ..NpcActionRejectCounts::default()
        };
        assert_eq!(rejects.summary(), "TARGET_HEALTH=1 TARGET_CASTING=2");
    }

    #[test]
    fn npc_target_stickiness_requires_a_meaningfully_closer_challenger() {
        assert!(npc_target_stickiness_keeps_current(8.0, 6.0, 0.35));
        assert!(!npc_target_stickiness_keeps_current(8.0, 5.0, 0.35));
        assert!(!npc_target_stickiness_keeps_current(8.0, 7.9, 0.0));
    }

    #[test]
    fn npc_target_stickiness_requires_meaningfully_more_threat() {
        let target = |id: u8, distance: f32, damage: f32| NpcScoredTarget {
            target: NpcAttackTarget {
                identity: Identity::from_hex(format!("{id:064x}").as_str()).unwrap(),
                pos_x: distance,
                pos_y: 0.0,
                pos_z: 0.0,
                hit_radius: 0.5,
                distance,
                dir_x: 1.0,
                dir_z: 0.0,
            },
            threat: NpcThreatComponents {
                damage,
                proximity: 0.1,
            },
        };

        assert!(npc_target_stickiness_keeps_scored_current(
            target(1, 5.0, 10.0),
            target(2, 3.0, 12.0),
            0.35,
        ));
        assert!(!npc_target_stickiness_keeps_scored_current(
            target(1, 5.0, 10.0),
            target(2, 3.0, 14.0),
            0.35,
        ));
    }

    #[test]
    fn npc_decision_jitter_is_deterministic_and_bounded() {
        let identity = Identity::from_hex(format!("{:064x}", 42).as_str()).unwrap();
        let first = npc_decision_interval_ms(identity, 7, 150, 0.05);

        assert_eq!(first, npc_decision_interval_ms(identity, 7, 150, 0.05));
        assert!((143..=158).contains(&first));
        assert_eq!(npc_decision_interval_ms(identity, 7, 150, 0.0), 150);
    }

    #[test]
    fn npc_threat_keys_are_pair_specific() {
        let npc = Identity::from_hex(format!("{:064x}", 1).as_str()).unwrap();
        let first_source = Identity::from_hex(format!("{:064x}", 2).as_str()).unwrap();
        let second_source = Identity::from_hex(format!("{:064x}", 3).as_str()).unwrap();

        assert_ne!(
            npc_threat_key(npc, first_source),
            npc_threat_key(npc, second_source)
        );
        assert_ne!(
            npc_threat_key(npc, first_source),
            npc_threat_key(first_source, npc)
        );
    }

    #[test]
    fn kobold_template_ids_are_stable() {
        let template = npc_template(NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD).unwrap();
        assert_eq!(
            template.template_id,
            NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD
        );
        assert_eq!(template.species_id, "KOBOLD_WARRIOR");
        assert!(template.move_speed > 0.0);
        assert!(
            npc_template(NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD)
                .unwrap()
                .move_speed
                > template.move_speed
        );
    }

    #[test]
    fn authored_npc_catalog_is_valid_and_complete_for_current_templates() {
        let parsed = parse_npc_catalog(NPC_CATALOG_JSON).unwrap();
        assert_eq!(parsed.templates.len(), 65);
        assert_eq!(npc_catalog().templates.len(), 65);
        assert_eq!(
            parsed
                .templates
                .iter()
                .map(|template| template.visual_ids.len())
                .sum::<usize>(),
            204
        );
        assert!(parsed
            .templates
            .iter()
            .all(|template| !template.visual_ids.is_empty() && !template.action_kit.is_empty()));
        let undead_bundle_expectations = [
            ("BANSHEE", 15, 4),
            ("CURSED_KNIGHT", 3, 2),
            ("DARK_RITUALIST", 3, 4),
            ("GHOUL", 3, 4),
            ("GRAVE_BRUTE", 3, 4),
            ("LICH_BOSS", 3, 5),
            ("LICH_CASTER", 3, 4),
            ("LICH_WARRIOR", 3, 5),
            ("SKELETAL_DRAGON", 4, 5),
            ("SKELETAL_HOUND", 3, 4),
            ("SKELETON_CULTIST", 5, 5),
            ("UNDEAD_ABOMINATION", 10, 2),
        ];
        for (template_id, visual_count, action_count) in undead_bundle_expectations {
            let template = npc_template(template_id)
                .unwrap_or_else(|| panic!("{template_id} should be authored"));
            assert_eq!(template.visual_ids.len(), visual_count, "{template_id}");
            assert_eq!(template.action_kit.len(), action_count, "{template_id}");
        }
        assert_eq!(
            npc_template("SKELETAL_DRAGON").unwrap().action_kit[0].ability_id,
            "NPC_SKELETAL_DRAGON_BONE_BREATH"
        );
        let wizard = npc_template("SKELETON_WIZARD").expect("wizard exemplar should be authored");
        assert_eq!(wizard.attack_recovery_ms, 500);
        assert_eq!(wizard.visual_ids.len(), 3);
        assert_eq!(wizard.action_kit.len(), 5);
        assert_eq!(wizard.action_kit[0].role, "RANGED_OFFENSE");
        assert_eq!(wizard.action_kit[0].target_selector, "CURRENT_ENEMY");
        assert_eq!(wizard.action_kit[1].role, "DEBUFF");
        assert_eq!(wizard.action_kit[1].target_selector, "CURRENT_ENEMY");
        assert_eq!(
            wizard.action_kit[1].forbidden_target_status,
            "NPC_SKELETON_FROSTBITE"
        );
        assert_eq!(wizard.action_kit[2].role, "INTERRUPT");
        assert_eq!(wizard.action_kit[2].target_selector, "CURRENT_ENEMY");
        let lich = npc_template("LICH_SUPPORT").expect("support exemplar should be authored");
        assert_eq!(lich.visual_ids.len(), 6);
        assert_eq!(lich.action_kit.len(), 4);
        assert_eq!(lich.action_kit[0].role, "HEAL");
        assert_eq!(lich.action_kit[0].target_selector, "LOWEST_HEALTH_ALLY");
        assert!(lich.action_kit[0].base_utility > lich.action_kit[1].base_utility);
        assert_eq!(lich.action_kit[1].role, "BUFF");
        let archer = npc_template("SKELETON_ARCHER").expect("archer exemplar should be authored");
        assert_eq!(archer.visual_ids.len(), 3);
        assert_eq!(archer.action_kit[0].role, "RANGED_OFFENSE");
        let abomination =
            npc_template("ABOMINATION").expect("abomination family should be authored");
        assert_eq!(abomination.visual_ids.len(), 3);
        assert_eq!(abomination.action_kit.len(), 2);
        assert_eq!(abomination.action_kit[0].role, "MELEE_OFFENSE");
        assert_eq!(abomination.attack_recovery_ms, 900);
        assert_eq!(
            npc_action_recovery_ms(&abomination, "NPC_ABOMINATION_HEAVY_CLAW"),
            850
        );
        assert_eq!(
            npc_action_recovery_ms(&abomination, "NPC_ABOMINATION_CLAW"),
            900
        );
        let humanoid_scarab =
            npc_template("HUMANOID_SCARAB").expect("humanoid scarab family should be authored");
        assert_eq!(humanoid_scarab.visual_ids.len(), 4);
        assert_eq!(humanoid_scarab.action_kit.len(), 2);
        assert_eq!(humanoid_scarab.action_kit[0].role, "MELEE_OFFENSE");
        let slime_man = npc_template("SLIME_MAN").expect("slime man family should be authored");
        assert_eq!(slime_man.visual_ids.len(), 4);
        assert_eq!(slime_man.action_kit.len(), 2);
        assert_eq!(
            slime_man.action_kit[0].ability_id,
            "NPC_SLIME_MAN_HEAVY_SLAM"
        );
        assert_eq!(slime_man.action_kit[0].role, "MELEE_OFFENSE");
        assert_eq!(slime_man.action_kit[0].windup_ms, 950);
        assert_eq!(slime_man.action_kit[1].ability_id, "NPC_SLIME_MAN_SLAM");
        assert_eq!(slime_man.action_kit[1].windup_ms, 800);
        assert_eq!(
            super::npc_action_windup_ms(&slime_man, "NPC_SLIME_MAN_HEAVY_SLAM"),
            950
        );
        assert_eq!(
            super::npc_action_windup_ms(&slime_man, "NPC_SLIME_MAN_SLAM"),
            800
        );
        let air_warlord =
            npc_template("AIR_WARLORD").expect("air warlord family should be authored");
        assert_eq!(air_warlord.visual_ids.len(), 4);
        assert_eq!(air_warlord.action_kit.len(), 2);
        assert_eq!(air_warlord.action_kit[0].role, "MELEE_OFFENSE");
        let spider = npc_template("SPIDER").expect("spider family should be authored");
        assert_eq!(spider.visual_ids.len(), 4);
        assert_eq!(spider.action_kit.len(), 2);
        assert_eq!(spider.action_kit[0].role, "MELEE_OFFENSE");
        let slime = npc_template("SLIME").expect("slime family should be authored");
        assert_eq!(slime.visual_ids.len(), 4);
        assert_eq!(slime.action_kit.len(), 2);
        assert_eq!(slime.action_kit[0].role, "MELEE_OFFENSE");
        let imp = npc_template("IMP").expect("imp family should be authored");
        assert_eq!(imp.visual_ids.len(), 8);
        assert_eq!(imp.action_kit.len(), 5);
        assert_eq!(imp.action_kit[0].ability_id, "NPC_IMP_FIRE_BOLT");
        assert_eq!(imp.action_kit[0].role, "RANGED_OFFENSE");
        assert_eq!(imp.action_kit[1].role, "MELEE_OFFENSE");
        let deep_sea_lizard =
            npc_template("DEEP_SEA_LIZARD").expect("deep sea lizard family should be authored");
        assert_eq!(deep_sea_lizard.visual_ids.len(), 10);
        assert_eq!(deep_sea_lizard.action_kit.len(), 5);
        assert_eq!(
            deep_sea_lizard.action_kit[0].ability_id,
            "NPC_DEEP_SEA_LIZARD_TIDAL_BOLT"
        );
        assert_eq!(deep_sea_lizard.action_kit[0].role, "RANGED_OFFENSE");
        assert_eq!(deep_sea_lizard.action_kit[1].role, "MELEE_OFFENSE");
        assert_eq!(
            npc_template("DEMON_WARRIOR_2H").unwrap().action_kit.len(),
            2
        );
        assert_eq!(
            npc_template("DEMON_WARRIOR_DUAL_WIELD")
                .unwrap()
                .action_kit
                .len(),
            4
        );
        assert_eq!(
            npc_template("DEMON_WARRIOR_UNARMED")
                .unwrap()
                .action_kit
                .len(),
            2
        );
        assert_eq!(
            npc_template("DEMON_WARRIOR_WEP_L")
                .unwrap()
                .action_kit
                .len(),
            2
        );
        assert_eq!(
            npc_template("DEMON_WARRIOR_WEP_R")
                .unwrap()
                .action_kit
                .len(),
            2
        );
        assert_eq!(
            npc_template("SKELETON_WARRIOR_2H")
                .unwrap()
                .action_kit
                .len(),
            3
        );
        assert_eq!(
            npc_template("SKELETON_WARRIOR_AXES")
                .unwrap()
                .action_kit
                .len(),
            4
        );
        assert_eq!(
            npc_template("SKELETON_WARRIOR_SHIELD")
                .unwrap()
                .action_kit
                .len(),
            2
        );
        assert_eq!(
            npc_template("SKELETON_WARRIOR_SWORDS")
                .unwrap()
                .action_kit
                .len(),
            4
        );
        assert_eq!(
            npc_template("SKELETON_WARRIOR_UNARMED")
                .unwrap()
                .action_kit
                .len(),
            4
        );
        let skeleton_reaper =
            npc_template("SKELETON_REAPER").expect("skeleton reaper family should be authored");
        assert_eq!(skeleton_reaper.visual_ids.len(), 3);
        assert_eq!(skeleton_reaper.action_kit.len(), 4);
        assert_eq!(
            skeleton_reaper.action_kit[0].ability_id,
            "NPC_SKELETON_REAPER_SOUL_BOLT"
        );
        assert_eq!(skeleton_reaper.action_kit[0].role, "RANGED_OFFENSE");
        assert_eq!(skeleton_reaper.action_kit[1].role, "MELEE_OFFENSE");
        let tomb_shade = npc_template("TOMB_SHADE").expect("tomb shade family should be authored");
        assert_eq!(tomb_shade.visual_ids.len(), 3);
        assert_eq!(tomb_shade.action_kit.len(), 2);
        assert_eq!(tomb_shade.action_kit[0].role, "MELEE_OFFENSE");
        let undead_eagle =
            npc_template("UNDEAD_EAGLE").expect("undead eagle family should be authored");
        assert_eq!(undead_eagle.visual_ids.len(), 3);
        assert_eq!(
            undead_eagle.action_kit[0].ability_id,
            "NPC_UNDEAD_EAGLE_STRIKE"
        );
        assert_eq!(undead_eagle.action_kit[0].role, "MELEE_OFFENSE");
        let dragon_brute =
            npc_template("DRAGON_BRUTE").expect("dragon brute family should be authored");
        assert_eq!(dragon_brute.visual_ids.len(), 3);
        assert_eq!(dragon_brute.action_kit.len(), 2);
        assert_eq!(dragon_brute.action_kit[0].windup_ms, 850);
        assert_eq!(dragon_brute.action_kit[1].windup_ms, 650);
        let swamp_hound =
            npc_template("SWAMP_HOUND").expect("swamp hound family should be authored");
        assert_eq!(swamp_hound.visual_ids.len(), 4);
        assert_eq!(swamp_hound.action_kit.len(), 2);
        assert_eq!(npc_template("UNDEAD_BEAR").unwrap().action_kit.len(), 3);
        assert_eq!(npc_template("UNDEAD_BOAR").unwrap().action_kit.len(), 2);
        assert_eq!(npc_template("UNDEAD_RAT").unwrap().action_kit.len(), 3);
        assert_eq!(npc_template("BONE_GOLEM").unwrap().action_kit.len(), 3);
        assert_eq!(
            npc_template("BONE_GOLEM").unwrap().knockback_resistance,
            1.0
        );
        assert_eq!(npc_template("DEMON_SUMMONER").unwrap().action_kit.len(), 3);
        assert_eq!(npc_template("FOREST_DEMON").unwrap().action_kit.len(), 2);
        assert_eq!(npc_template("GRAVEDIGGER").unwrap().action_kit.len(), 2);
        assert_eq!(npc_template("MECHABOT").unwrap().action_kit.len(), 2);
        assert_eq!(npc_template("MUSHROOM").unwrap().action_kit.len(), 2);
        assert_eq!(npc_template("VAMPIRE").unwrap().action_kit.len(), 2);
        assert_eq!(npc_template("ZOMBIE_HOUND").unwrap().action_kit.len(), 2);
        assert_eq!(npc_template("ROCK_GOLEM").unwrap().visual_ids.len(), 6);
        assert_eq!(
            npc_template("ROCK_GOLEM").unwrap().knockback_resistance,
            1.0
        );
        assert_eq!(
            npc_template("SKELETAL_DRAGON")
                .unwrap()
                .knockback_resistance,
            1.0
        );
        assert_eq!(
            npc_template("HELLGUARD_ARMORED").unwrap().action_kit.len(),
            4
        );
        assert_eq!(
            npc_template("HELLGUARD_UNARMED").unwrap().action_kit.len(),
            2
        );
        assert_eq!(
            npc_template("ZOMBIE_DUAL_WIELD").unwrap().visual_ids.len(),
            4
        );
        assert_eq!(npc_template("ZOMBIE_UNARMED").unwrap().action_kit.len(), 2);
        assert_eq!(
            npc_template("KOBOLD_WARRIOR_BK_DUAL_SWORD")
                .unwrap()
                .action_kit
                .len(),
            4
        );
        assert_eq!(
            npc_template("KOBOLD_THIEF_GN_SPEAR")
                .unwrap()
                .action_kit
                .len(),
            5
        );
        assert_eq!(
            npc_template("KOBOLD_THIEF_RD_SWORD_SHIELD")
                .unwrap()
                .action_kit
                .len(),
            5
        );
        assert_eq!(
            npc_template("KOBOLD_KNIGHT_BK_DUAL_SWORD")
                .unwrap()
                .action_kit
                .len(),
            4
        );
        assert_eq!(
            npc_template("KOBOLD_KNIGHT_GN_SPEAR")
                .unwrap()
                .action_kit
                .len(),
            5
        );
    }

    #[test]
    fn npc_action_kits_reject_player_only_abilities() {
        let mut catalog: serde_json::Value = serde_json::from_str(NPC_CATALOG_JSON).unwrap();
        catalog["templates"][0]["action_kit"][0]["ability_id"] =
            serde_json::Value::String("WARRIOR_HEW".to_string());
        let error = parse_npc_catalog(&serde_json::to_string(&catalog).unwrap())
            .err()
            .expect("player-only grant should fail");
        assert!(error.contains("PLAYER-scoped ability"), "{error}");
    }

    #[test]
    fn npc_action_kits_reject_invalid_utility_thresholds() {
        let mut catalog: serde_json::Value = serde_json::from_str(NPC_CATALOG_JSON).unwrap();
        catalog["templates"][0]["action_kit"][0]["min_self_health_pct"] = serde_json::json!(0.8);
        catalog["templates"][0]["action_kit"][0]["max_self_health_pct"] = serde_json::json!(0.2);
        let error = parse_npc_catalog(&serde_json::to_string(&catalog).unwrap())
            .err()
            .expect("inverted health thresholds should fail");
        assert!(error.contains("invalid self-health thresholds"), "{error}");
    }

    #[test]
    fn npc_action_kits_reject_invalid_recovery_timing() {
        let mut catalog: serde_json::Value = serde_json::from_str(NPC_CATALOG_JSON).unwrap();
        catalog["templates"][0]["action_kit"][0]["recovery_ms"] = serde_json::json!(10);
        let error = parse_npc_catalog(&serde_json::to_string(&catalog).unwrap())
            .err()
            .expect("too-short recovery should fail");
        assert!(error.contains("invalid recovery_ms"), "{error}");
    }

    #[test]
    fn npc_movement_hold_is_monotonic_and_expires_at_the_deadline() {
        let now = Timestamp::UNIX_EPOCH + Duration::from_secs(10);
        let first_deadline = now + Duration::from_millis(850);
        let later_deadline = now + Duration::from_millis(900);
        let mut runtime = NpcCombatRuntime {
            identity: Identity::ZERO,
            target: Identity::ZERO,
            planned_ability_id: String::new(),
            decision_sequence: 1,
            next_decision_at: now,
            next_decision_at_micros: now.to_micros_since_unix_epoch(),
            hold_movement_until_micros: 0,
        };

        stamp_npc_movement_hold(&mut runtime, first_deadline);
        stamp_npc_movement_hold(&mut runtime, now + Duration::from_millis(500));
        stamp_npc_movement_hold(&mut runtime, later_deadline);

        assert_eq!(
            runtime.hold_movement_until_micros,
            later_deadline.to_micros_since_unix_epoch()
        );
        assert!(npc_movement_hold_active(
            &runtime,
            later_deadline - Duration::from_micros(1)
        ));
        assert!(!npc_movement_hold_active(&runtime, later_deadline));
    }

    #[test]
    fn authored_npc_catalog_rejects_duplicate_visual_ids() {
        let mut catalog: serde_json::Value = serde_json::from_str(NPC_CATALOG_JSON).unwrap();
        let shared_visual = catalog["templates"][0]["visual_ids"][0].clone();
        catalog["templates"][1]["visual_ids"][0] = shared_visual;
        let error = parse_npc_catalog(&serde_json::to_string(&catalog).unwrap())
            .err()
            .expect("duplicate visuals should fail");
        assert!(error.contains("visual_id"), "{error}");
    }

    #[test]
    fn npc_templates_reject_unknown_brain_profiles() {
        let mut catalog: serde_json::Value = serde_json::from_str(NPC_CATALOG_JSON).unwrap();
        catalog["templates"][0]["brain_profile_id"] =
            serde_json::Value::String("UNKNOWN_BRAIN".to_string());
        let error = parse_npc_catalog(&serde_json::to_string(&catalog).unwrap())
            .err()
            .expect("unknown brain reference should fail");
        assert!(error.contains("unknown brain_profile_id"), "{error}");
    }

    #[test]
    fn visual_identity_is_normalized_and_scoped_to_its_template() {
        let template = npc_template(NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD).unwrap();
        assert_eq!(
            visual_id_for_template(&template, " kobold_warrior_rd ").unwrap(),
            "KOBOLD_WARRIOR_RD"
        );
        assert!(visual_id_for_template(&template, "").is_err());
        assert!(visual_id_for_template(&template, NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR).is_err());
    }

    #[test]
    fn npc_identity_is_deterministic_per_owner_and_sequence() {
        let owner =
            Identity::from_hex("1111111111111111111111111111111122222222222222222222222222222222")
                .expect("test identity should parse");
        let first_a = npc_identity(owner, 0).unwrap();
        let first_b = npc_identity(owner, 0).unwrap();
        let second = npc_identity(owner, 1).unwrap();

        assert_eq!(first_a, first_b);
        assert_ne!(first_a, second);
    }

    #[test]
    fn npc_yaw_matches_player_forward_convention() {
        assert_eq!(yaw_for_direction(0.0, 1.0), 0.0);
        assert!((yaw_for_direction(1.0, 0.0) - std::f32::consts::FRAC_PI_2).abs() < 0.0001);
    }

    #[test]
    fn npc_melee_uses_shared_pending_impact_executor() {
        let source = fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("src/npcs.rs"))
            .expect("npcs.rs should be readable");
        let runtime_source = source
            .split("#[cfg(test)]")
            .next()
            .expect("runtime source should precede tests");

        assert!(runtime_source.contains("commit_server_actor_targeted_melee"));
        assert!(runtime_source.contains("resolve_due_pending_melee_impacts_for_event_source"));
        assert!(!runtime_source.contains("struct NpcPendingSwing"));
        assert!(!runtime_source.contains("npc_pending_swing()"));
    }
}

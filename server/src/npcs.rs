use std::collections::HashSet;
use std::sync::OnceLock;
use std::time::Duration;

use serde::Deserialize;
use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::open_world_scene_name_for_identity;
use crate::arena::{resolve_player_world_context, world_contexts_share, ResolvedWorldContext};
use crate::combat::{
    mark_harmful_combat_action, queue_effects, timestamp_to_micros, CombatEvent, DamageDelivery,
    EffectPacket, MovementModifiers, COMBAT_EVENT_BLOCK, COMBAT_EVENT_CAST, COMBAT_EVENT_IMPACT,
    COMBAT_EVENT_PARRY, COMBAT_METADATA_NONE, COMBAT_SCALAR_MELEE_RELEASE_DELAY_SECONDS,
    COMBAT_SCALAR_NONE, COMBAT_SEQUENCE_NONE, DAMAGE_SOURCE_KIND_MELEE,
};
use crate::defense::{
    resolve_defensible_combat_hit, CombatHitDeliveryKind, DefenseResolution, DefensibleCombatHit,
};
use crate::inventory::{clear_loot_for_anchor, corpse_loot_has_items};
use crate::movement::FIXED_TICK_SECONDS;
use crate::practice::is_training_instance;
use crate::relations::can_harm;
use crate::world_collision::{
    resolve_world_horizontal_sweep_collision_y_with_layout_for_scene,
    surface_height_for_world_at_y_with_layout_for_scene,
};

#[allow(unused_imports)]
use crate::arena::arena_instance as _;
#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::combat::combat_event as _;
#[allow(unused_imports)]
use crate::npcs::npc_combat_runtime as _;
#[allow(unused_imports)]
use crate::npcs::npc_instance as _;
#[allow(unused_imports)]
use crate::npcs::npc_pending_swing as _;
#[allow(unused_imports)]
use crate::npcs::npc_physics as _;
#[allow(unused_imports)]
use crate::npcs::npc_spawn_counter as _;
#[allow(unused_imports)]
use crate::npcs::npc_state as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

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
    pub display_name: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    pub open_world_scene_name: String,
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
    pub next_attack_at: Timestamp,
    #[index(btree)]
    pub next_attack_at_micros: i64,
}

/// One in-flight telegraphed swing per NPC (S3): the CAST event is emitted at
/// swing start, this row schedules damage resolution `attack_windup_ms` later.
/// At most one exists per NPC (cadence >> windup); a retarget mid-windup
/// replaces — i.e. cancels — the in-flight swing. Resolution re-validates
/// everything against present-time state, so rows never need proactive
/// cleanup beyond NPC despawn.
#[table(accessor = npc_pending_swing)]
#[derive(Clone)]
pub struct NpcPendingSwing {
    #[primary_key]
    pub identity: Identity,
    pub target: Identity,
    pub action_instance_id: String,
    pub cast_at: Timestamp,
    pub resolve_at: Timestamp,
    #[index(btree)]
    pub resolve_at_micros: i64,
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
    pub max_hp: i32,
    pub hit_radius: f32,
    pub hit_height: f32,
    pub aggro_radius: f32,
    pub attack_range: f32,
    pub move_speed: f32,
    pub attack_damage: i32,
    pub attack_cadence_ms: u64,
    /// Authored telegraph (S3): delay between the CAST event (swing start,
    /// what the victim's screen shows) and damage resolution. Must stay well
    /// above the victim's render delay (~100-166 ms) or the telegraph reads
    /// as nothing; design floor is 300 ms.
    pub attack_windup_ms: u64,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct NpcCatalogDocument {
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

    if npc_attacks_are_harmless() {
        template.attack_damage = 0;
    }
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

fn parse_npc_catalog(json: &str) -> Result<NpcCatalogDocument, String> {
    let mut document: NpcCatalogDocument =
        serde_json::from_str(json).map_err(|error| error.to_string())?;
    let mut template_ids = HashSet::new();
    let mut visual_ids = HashSet::new();
    if document.templates.is_empty() {
        return Err("templates must not be empty".to_string());
    }

    for template in &mut document.templates {
        template.template_id = normalize_id(template.template_id.as_str());
        template.species_id = normalize_id(template.species_id.as_str());
        template.display_name = template.display_name.trim().to_string();
        template.visual_ids = template
            .visual_ids
            .iter()
            .map(|visual_id| normalize_id(visual_id))
            .collect();

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
        if template.max_hp <= 0
            || !template.hit_radius.is_finite()
            || template.hit_radius <= 0.0
            || !template.hit_height.is_finite()
            || template.hit_height <= 0.0
            || !template.aggro_radius.is_finite()
            || template.aggro_radius <= 0.0
            || !template.attack_range.is_finite()
            || template.attack_range <= 0.0
            || !template.move_speed.is_finite()
            || template.move_speed <= 0.0
            || template.attack_damage < 0
            || template.attack_cadence_ms == 0
            || template.attack_windup_ms == 0
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
    let expected_visuals: HashSet<_> = catalog
        .templates
        .iter()
        .flat_map(|template| template.visual_ids.iter().cloned())
        .collect();

    for template in &catalog.templates {
        let row = NpcTemplateCatalog {
            template_id: template.template_id.clone(),
            species_id: template.species_id.clone(),
            display_name: template.display_name.clone(),
            default_visual_id: template.visual_ids[0].clone(),
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
    let target_yaw = wrap_yaw(owner_physics.yaw + std::f32::consts::PI);
    let spawn_x = owner_physics.pos_x + owner_physics.yaw.sin() * NPC_SPAWN_FORWARD;
    let spawn_z = owner_physics.pos_z + owner_physics.yaw.cos() * NPC_SPAWN_FORWARD;

    ctx.db.npc_instance().insert(NpcInstance {
        identity,
        spawned_by: owner,
        template_id: template.template_id.to_string(),
        visual_id,
        species_id: template.species_id.to_string(),
        faction: faction.as_str().to_string(),
        display_name: template.display_name.to_string(),
        world_kind: if is_instance {
            WORLD_KIND_INSTANCE.to_string()
        } else {
            WORLD_KIND_OPEN.to_string()
        },
        instance_id,
        open_world_scene_name,
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
        pos_y: owner_physics.pos_y,
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

pub(crate) fn npc_faction(ctx: &ReducerContext, identity: Identity) -> Option<NpcFaction> {
    let row = ctx.db.npc_instance().identity().find(identity)?;
    NpcFaction::from_wire(row.faction.as_str())
}

pub(crate) fn tick_npc_combat(
    ctx: &ReducerContext,
    now: Timestamp,
    movement_modifiers: &MovementModifiers,
) {
    // Due windups resolve before new swings begin, so a swing scheduled for
    // this tick lands before the same NPC's next cadence fire is considered.
    resolve_due_npc_pending_swings(ctx, now, movement_modifiers);

    let npcs: Vec<NpcInstance> = ctx.db.npc_instance().iter().collect();
    if npcs.is_empty() {
        return;
    }

    // Candidates resolved once per tick (tick audit T4): world context and
    // physics per alive non-dummy player, instead of per NPC x player pair.
    let candidates = collect_npc_target_candidates(ctx);
    for npc in npcs {
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

        let Some(target) =
            acquire_npc_attack_target(ctx, npc.identity, &physics, &template, &candidates)
        else {
            clear_npc_combat_runtime(ctx, npc.identity);
            continue;
        };

        let mut runtime = ctx
            .db
            .npc_combat_runtime()
            .identity()
            .find(npc.identity)
            .filter(|row| row.target == target.identity)
            .unwrap_or_else(|| NpcCombatRuntime {
                identity: npc.identity,
                target: target.identity,
                next_attack_at: now,
                next_attack_at_micros: timestamp_to_micros(now),
            });

        if movement_modifiers.is_disabled(&npc.identity) {
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }

        if now < runtime.next_attack_at {
            face_npc_target(ctx, now, &physics, &target);
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }

        if target.distance > npc_attack_reach(&template, &target) {
            let move_speed_multiplier = movement_modifiers.move_speed_multiplier(&npc.identity, 0);
            if move_speed_multiplier > 0.0 {
                chase_npc_toward_target(
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
            // Don't slide next_attack_at to `now` while chasing: the flow is
            // identical with the stale past timestamp (`now < next_attack_at`
            // stays false), nothing range-queries the btree, and the slide
            // forced a genuine row write every chase tick. The upsert below
            // still persists new/retargeted rows; steady chase skips via the
            // value gate.
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }

        let physics = face_npc_target(ctx, now, &physics, &target);

        if npc_attacks_are_disabled() {
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }

        begin_npc_melee_swing(ctx, now, &npc, &physics, &template, &target);
        runtime.next_attack_at = now + Duration::from_millis(template.attack_cadence_ms);
        runtime.next_attack_at_micros = timestamp_to_micros(runtime.next_attack_at);
        upsert_npc_combat_runtime(ctx, runtime);
    }
}

#[derive(Clone, Copy)]
struct NpcAttackTarget {
    identity: Identity,
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    hit_radius: f32,
    hit_height: f32,
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
    hit_height: f32,
    context: ResolvedWorldContext,
}

/// One pass over `player_state` per tick, in table order (target tie-breaking
/// depends on it): alive non-dummy players with their world context and
/// physics resolved once, shared by every NPC's target acquisition.
fn collect_npc_target_candidates(ctx: &ReducerContext) -> Vec<NpcTargetCandidate> {
    let mut candidates = Vec::new();
    for state in ctx.db.player_state().iter() {
        if !state.alive || state.is_dummy {
            continue;
        }
        let Some(context) = resolve_player_world_context(ctx, state.player_id) else {
            log::warn!(
                "[WORLD] Missing player_world row for NPC target candidate {}",
                state.player_id.to_hex()
            );
            continue;
        };
        let Some(physics) = ctx.db.player_physics().identity().find(state.player_id) else {
            continue;
        };
        candidates.push(NpcTargetCandidate {
            identity: state.player_id,
            pos_x: physics.pos_x,
            pos_y: physics.pos_y,
            pos_z: physics.pos_z,
            hit_radius: state.hit_radius,
            hit_height: state.hit_height,
            context,
        });
    }
    candidates
}

fn acquire_npc_attack_target(
    ctx: &ReducerContext,
    npc_identity: Identity,
    npc_physics: &NpcPhysics,
    template: &NpcTemplate,
    candidates: &[NpcTargetCandidate],
) -> Option<NpcAttackTarget> {
    let Some(npc_context) = resolve_player_world_context(ctx, npc_identity) else {
        log::warn!(
            "[WORLD] Missing world context for NPC {}",
            npc_identity.to_hex()
        );
        return None;
    };
    crate::tick_metrics::record_npc_target_pairs_scanned(candidates.len() as u64);

    let aggro_radius_sq = template.aggro_radius * template.aggro_radius;
    let mut best: Option<NpcAttackTarget> = None;
    for candidate in candidates {
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
        if best
            .as_ref()
            .is_some_and(|existing| dist_sq >= existing.distance * existing.distance)
        {
            continue;
        }
        if !can_harm(ctx, npc_identity, candidate.identity) {
            continue;
        }

        let distance = dist_sq.sqrt();
        let (dir_x, dir_z) = if distance > 0.001 {
            (dx / distance, dz / distance)
        } else {
            (0.0, 1.0)
        };
        best = Some(NpcAttackTarget {
            identity: candidate.identity,
            pos_x: candidate.pos_x,
            pos_y: candidate.pos_y,
            pos_z: candidate.pos_z,
            hit_radius: candidate.hit_radius,
            hit_height: candidate.hit_height,
            distance,
            dir_x,
            dir_z,
        });
    }
    best
}

fn npc_attack_reach(template: &NpcTemplate, target: &NpcAttackTarget) -> f32 {
    template.attack_range + target.hit_radius
}

fn chase_npc_toward_target(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    template: &NpcTemplate,
    target: &NpcAttackTarget,
    move_speed_multiplier: f32,
) -> NpcPhysics {
    let desired_yaw = yaw_for_direction(target.dir_x, target.dir_z);
    let stop_distance = (npc_attack_reach(template, target) - NPC_CHASE_STOP_EPSILON).max(0.0);
    let remaining = (target.distance - stop_distance).max(0.0);
    let travel = (template.move_speed * move_speed_multiplier * FIXED_TICK_SECONDS).min(remaining);
    if travel <= f32::EPSILON {
        return update_npc_facing(ctx, now, physics, desired_yaw);
    }

    let (arena_seed, flat_ground_only) = npc_movement_world(ctx, npc);
    let open_world_scene_name = if npc.world_kind.eq_ignore_ascii_case(WORLD_KIND_OPEN) {
        Some(npc.open_world_scene_name.as_str())
    } else {
        None
    };
    let step_count = ((travel / NPC_CHASE_COLLISION_STEP).ceil() as usize).max(1);
    let step_x = target.dir_x * travel / step_count as f32;
    let step_z = target.dir_z * travel / step_count as f32;
    let mut next_x = physics.pos_x;
    let mut next_y = physics.pos_y;
    let mut next_z = physics.pos_z;

    for _ in 0..step_count {
        let target_x = next_x + step_x;
        let target_z = next_z + step_z;
        let (resolved_x, resolved_z) =
            resolve_world_horizontal_sweep_collision_y_with_layout_for_scene(
                arena_seed,
                flat_ground_only,
                open_world_scene_name,
                next_x,
                next_z,
                target_x,
                target_z,
                template.hit_radius.max(0.1),
                template.hit_height.max(0.5),
                next_y,
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
    physics: &NpcPhysics,
    template: &NpcTemplate,
    target: &NpcAttackTarget,
) {
    let action_instance_id = format!("npc:{}:{}", npc.identity.to_hex(), timestamp_to_micros(now));
    emit_npc_combat_event(
        ctx,
        now,
        npc,
        physics,
        target,
        action_instance_id.as_str(),
        COMBAT_EVENT_CAST,
        0,
        COMBAT_SCALAR_MELEE_RELEASE_DELAY_SECONDS,
        template.attack_windup_ms as f32 / 1000.0,
    );

    let resolve_at = now + Duration::from_millis(template.attack_windup_ms);
    let row = NpcPendingSwing {
        identity: npc.identity,
        target: target.identity,
        action_instance_id,
        cast_at: now,
        resolve_at,
        resolve_at_micros: timestamp_to_micros(resolve_at),
    };
    if ctx
        .db
        .npc_pending_swing()
        .identity()
        .find(row.identity)
        .is_some()
    {
        ctx.db.npc_pending_swing().identity().update(row);
    } else {
        ctx.db.npc_pending_swing().insert(row);
    }
}

fn resolve_due_npc_pending_swings(
    ctx: &ReducerContext,
    now: Timestamp,
    movement_modifiers: &MovementModifiers,
) {
    let due: Vec<NpcPendingSwing> = ctx
        .db
        .npc_pending_swing()
        .resolve_at_micros()
        .filter(..=timestamp_to_micros(now))
        .collect();

    for swing in due {
        ctx.db.npc_pending_swing().identity().delete(swing.identity);
        resolve_npc_pending_swing(ctx, now, &swing, movement_modifiers);
    }
}

/// Swing resolution (S3), re-validated against present-time state: the swing
/// cancels when the NPC is gone, dead, or disabled at impact time (crowd
/// control mid-windup interrupts the hit), and whiffs silently — mirroring
/// player melee — when the target is gone, unharmable, or outside authored
/// reach (stepping out during the windup is the dodge counterplay).
fn resolve_npc_pending_swing(
    ctx: &ReducerContext,
    now: Timestamp,
    swing: &NpcPendingSwing,
    movement_modifiers: &MovementModifiers,
) {
    let Some(npc) = ctx.db.npc_instance().identity().find(swing.identity) else {
        return;
    };
    let Some(template) = npc_template(npc.template_id.as_str()) else {
        return;
    };
    let Some(state) = ctx.db.npc_state().identity().find(swing.identity) else {
        return;
    };
    if !state.alive {
        return;
    }
    if movement_modifiers.is_disabled(&swing.identity) {
        return;
    }
    let Some(physics) = ctx.db.npc_physics().identity().find(swing.identity) else {
        return;
    };

    let Some(target) = resolve_npc_swing_target(ctx, swing.identity, &physics, swing.target) else {
        return;
    };
    if target.distance > npc_attack_reach(&template, &target) {
        return;
    }

    if let Some(defense_event_type) = resolve_npc_melee_defense(ctx, now, &physics, &target) {
        mark_harmful_combat_action(
            ctx,
            npc.identity,
            target.identity,
            now,
            NPC_MELEE_ACTION_KIND,
        );
        emit_npc_combat_event(
            ctx,
            now,
            &npc,
            &physics,
            &target,
            swing.action_instance_id.as_str(),
            defense_event_type,
            0,
            COMBAT_SCALAR_NONE,
            0.0,
        );
        return;
    }

    emit_npc_combat_event(
        ctx,
        now,
        &npc,
        &physics,
        &target,
        swing.action_instance_id.as_str(),
        COMBAT_EVENT_IMPACT,
        template.attack_damage,
        COMBAT_SCALAR_NONE,
        0.0,
    );
    queue_effects(
        ctx,
        vec![EffectPacket::Damage {
            amount: template.attack_damage,
            damage_type: crate::combat::DamageType::Physical,
            source: npc.identity,
            target: target.identity,
            spell_id: swing.action_instance_id.clone(),
            delivery: DamageDelivery::Direct,
            direct_action_key: NPC_MELEE_ACTION_KIND.to_string(),
            source_kind: DAMAGE_SOURCE_KIND_MELEE.to_string(),
        }],
    );
}

/// Present-time target state for a resolving swing: same eligibility rules as
/// acquisition (alive, non-dummy, shared world, harmable), but looked up
/// directly for the single stored target instead of scanning candidates.
fn resolve_npc_swing_target(
    ctx: &ReducerContext,
    npc_identity: Identity,
    npc_physics: &NpcPhysics,
    target_identity: Identity,
) -> Option<NpcAttackTarget> {
    let state = ctx.db.player_state().player_id().find(target_identity)?;
    if !state.alive || state.is_dummy {
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
    let physics = ctx.db.player_physics().identity().find(target_identity)?;

    let dx = physics.pos_x - npc_physics.pos_x;
    let dz = physics.pos_z - npc_physics.pos_z;
    let distance = (dx * dx + dz * dz).sqrt();
    let (dir_x, dir_z) = if distance > 0.001 {
        (dx / distance, dz / distance)
    } else {
        (0.0, 1.0)
    };
    Some(NpcAttackTarget {
        identity: target_identity,
        pos_x: physics.pos_x,
        pos_y: physics.pos_y,
        pos_z: physics.pos_z,
        hit_radius: state.hit_radius,
        hit_height: state.hit_height,
        distance,
        dir_x,
        dir_z,
    })
}

fn resolve_npc_melee_defense(
    ctx: &ReducerContext,
    now: Timestamp,
    physics: &NpcPhysics,
    target: &NpcAttackTarget,
) -> Option<&'static str> {
    npc_melee_defense_event_type(resolve_defensible_combat_hit(
        ctx,
        DefensibleCombatHit {
            delivery_kind: CombatHitDeliveryKind::Melee,
            defender: target.identity,
            active_from: now,
            active_until: now + Duration::from_millis(1),
            parry_behavior: NPC_MELEE_PARRY_BEHAVIOR,
            block_behavior: NPC_MELEE_BLOCK_BEHAVIOR,
            source_x: physics.pos_x,
            source_y: physics.pos_y,
            source_z: physics.pos_z,
            impact_x: target.pos_x,
            impact_y: target.pos_y + target.hit_height * 0.5,
            impact_z: target.pos_z,
            dir_x: target.dir_x,
            dir_y: 0.0,
            dir_z: target.dir_z,
            speed: 0.0,
        },
    ))
}

fn npc_melee_defense_event_type(resolution: DefenseResolution) -> Option<&'static str> {
    match resolution {
        DefenseResolution::Parried => Some(COMBAT_EVENT_PARRY),
        DefenseResolution::Blocked => Some(COMBAT_EVENT_BLOCK),
        DefenseResolution::None => None,
    }
}

#[allow(clippy::too_many_arguments)]
fn emit_npc_combat_event(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    target: &NpcAttackTarget,
    action_instance_id: &str,
    event_type: &str,
    damage: i32,
    scalar_kind: &str,
    scalar_value: f32,
) {
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: action_instance_id.to_string(),
        action_kind: NPC_MELEE_ACTION_KIND.to_string(),
        ability_id: npc.template_id.clone(),
        hit_index: 0,
        event_type: event_type.to_string(),
        source_kind: NPC_MELEE_SOURCE_KIND.to_string(),
        caster: npc.identity,
        hit: target.identity,
        origin_x: physics.pos_x,
        origin_y: physics.pos_y,
        origin_z: physics.pos_z,
        dir_x: target.dir_x,
        dir_y: 0.0,
        dir_z: target.dir_z,
        speed: 0.0,
        max_distance: template_attack_range_for_event(npc.template_id.as_str()),
        scalar_kind: scalar_kind.to_string(),
        scalar_value,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 0,
        point_x: target.pos_x,
        point_y: target.pos_y,
        point_z: target.pos_z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

fn template_attack_range_for_event(template_id: &str) -> f32 {
    npc_template(template_id)
        .map(|template| template.attack_range)
        .unwrap_or(0.0)
}

fn despawn_npc_identity(ctx: &ReducerContext, identity: Identity) {
    clear_npc_combat_runtime(ctx, identity);
    if ctx
        .db
        .npc_pending_swing()
        .identity()
        .find(identity)
        .is_some()
    {
        ctx.db.npc_pending_swing().identity().delete(identity);
    }
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
    use super::{
        npc_catalog, npc_identity, npc_melee_defense_event_type, npc_template, parse_npc_catalog,
        visual_id_for_template, yaw_for_direction, NpcFaction, NPC_CATALOG_JSON,
        NPC_FACTION_FRIENDLY, NPC_FACTION_HOSTILE, NPC_FACTION_NEUTRAL,
        NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD, NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR,
        NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD,
    };
    use crate::combat::{COMBAT_EVENT_BLOCK, COMBAT_EVENT_PARRY};
    use crate::defense::DefenseResolution;
    use spacetimedb::Identity;

    #[test]
    fn npc_faction_wire_values_are_stable() {
        assert_eq!(NpcFaction::Hostile.as_str(), NPC_FACTION_HOSTILE);
        assert_eq!(NpcFaction::Neutral.as_str(), NPC_FACTION_NEUTRAL);
        assert_eq!(NpcFaction::Friendly.as_str(), NPC_FACTION_FRIENDLY);
        assert_eq!(NpcFaction::from_wire("hostile"), Some(NpcFaction::Hostile));
        assert_eq!(NpcFaction::from_wire("party_member"), None);
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
        assert_eq!(parsed.templates.len(), 4);
        assert_eq!(npc_catalog().templates.len(), 4);
        assert!(parsed
            .templates
            .iter()
            .all(|template| !template.visual_ids.is_empty()));
    }

    #[test]
    fn authored_npc_catalog_rejects_duplicate_visual_ids() {
        let invalid = r#"{
            "templates": [
                {
                    "template_id": "ONE", "species_id": "TEST", "display_name": "One",
                    "visual_ids": ["SHARED"], "max_hp": 1, "hit_radius": 1.0,
                    "hit_height": 1.0, "aggro_radius": 1.0, "attack_range": 1.0,
                    "move_speed": 1.0, "attack_damage": 0, "attack_cadence_ms": 1,
                    "attack_windup_ms": 1
                },
                {
                    "template_id": "TWO", "species_id": "TEST", "display_name": "Two",
                    "visual_ids": ["SHARED"], "max_hp": 1, "hit_radius": 1.0,
                    "hit_height": 1.0, "aggro_radius": 1.0, "attack_range": 1.0,
                    "move_speed": 1.0, "attack_damage": 0, "attack_cadence_ms": 1,
                    "attack_windup_ms": 1
                }
            ]
        }"#;
        assert!(parse_npc_catalog(invalid).is_err());
    }

    #[test]
    fn visual_identity_is_normalized_and_scoped_to_its_template() {
        let template = npc_template(NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD).unwrap();
        assert_eq!(
            visual_id_for_template(&template, " kobold_warrior_rd_sword_shield ").unwrap(),
            NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD
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
    fn npc_melee_defense_resolution_uses_canonical_combat_events() {
        assert_eq!(
            npc_melee_defense_event_type(DefenseResolution::Parried),
            Some(COMBAT_EVENT_PARRY)
        );
        assert_eq!(
            npc_melee_defense_event_type(DefenseResolution::Blocked),
            Some(COMBAT_EVENT_BLOCK)
        );
        assert_eq!(npc_melee_defense_event_type(DefenseResolution::None), None);
    }
}

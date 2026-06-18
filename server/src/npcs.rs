use std::time::Duration;

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::open_world_scene_name_for_identity;
use crate::arena::players_share_world_context;
use crate::combat::{
    movement_modifiers, queue_effects, timestamp_to_micros, CombatEvent, DamageDelivery,
    EffectPacket, COMBAT_EVENT_CAST, COMBAT_EVENT_IMPACT, COMBAT_METADATA_NONE, COMBAT_SCALAR_NONE,
    COMBAT_SEQUENCE_NONE,
};
use crate::inventory::clear_loot_for_anchor;
use crate::movement::{FIXED_TICK_SECONDS, MOVE_SPEED};
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
const NPC_CHASE_COLLISION_STEP: f32 = 0.35;
const NPC_CHASE_STOP_EPSILON: f32 = 0.05;
const NPC_FACE_EPSILON: f32 = 0.001;

#[table(accessor = npc_instance, public)]
#[derive(Clone)]
pub struct NpcInstance {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub spawned_by: Identity,
    #[index(btree)]
    pub template_id: String,
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
#[derive(Clone)]
pub struct NpcCombatRuntime {
    #[primary_key]
    pub identity: Identity,
    pub target: Identity,
    pub next_attack_at: Timestamp,
    #[index(btree)]
    pub next_attack_at_micros: i64,
}

#[derive(Clone, Copy)]
pub(crate) struct NpcTemplate {
    pub template_id: &'static str,
    pub species_id: &'static str,
    pub display_name: &'static str,
    pub max_hp: i32,
    pub hit_radius: f32,
    pub hit_height: f32,
    pub aggro_radius: f32,
    pub attack_range: f32,
    pub move_speed: f32,
    pub attack_damage: i32,
    pub attack_cadence_ms: u64,
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

pub(crate) fn npc_template(template_id: &str) -> Option<NpcTemplate> {
    match normalize_id(template_id).as_str() {
        NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD => Some(NpcTemplate {
            template_id: NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD,
            species_id: "KOBOLD_WARRIOR",
            display_name: "Kobold Warrior",
            max_hp: 125,
            hit_radius: 0.45,
            hit_height: 1.35,
            aggro_radius: 8.0,
            attack_range: 1.90,
            move_speed: MOVE_SPEED,
            attack_damage: 8,
            attack_cadence_ms: 1800,
        }),
        NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR => Some(NpcTemplate {
            template_id: NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR,
            species_id: "KOBOLD_WARRIOR",
            display_name: "Kobold Spearman",
            max_hp: 113,
            hit_radius: 0.45,
            hit_height: 1.35,
            aggro_radius: 9.0,
            attack_range: 2.40,
            move_speed: MOVE_SPEED,
            attack_damage: 7,
            attack_cadence_ms: 1900,
        }),
        NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD => Some(NpcTemplate {
            template_id: NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD,
            species_id: "KOBOLD_THIEF",
            display_name: "Kobold Thief",
            max_hp: 90,
            hit_radius: 0.4,
            hit_height: 1.25,
            aggro_radius: 8.5,
            attack_range: 1.80,
            move_speed: MOVE_SPEED + 0.5,
            attack_damage: 6,
            attack_cadence_ms: 1400,
        }),
        NPC_TEMPLATE_KOBOLD_KNIGHT_RD_SWORD_SHIELD => Some(NpcTemplate {
            template_id: NPC_TEMPLATE_KOBOLD_KNIGHT_RD_SWORD_SHIELD,
            species_id: "KOBOLD_KNIGHT",
            display_name: "Kobold Knight",
            max_hp: 163,
            hit_radius: 0.5,
            hit_height: 1.45,
            aggro_radius: 8.0,
            attack_range: 1.95,
            move_speed: MOVE_SPEED,
            attack_damage: 10,
            attack_cadence_ms: 2100,
        }),
        _ => None,
    }
}

#[reducer]
pub fn spawn_npc(ctx: &ReducerContext, template_id: String, faction: String) -> Result<(), String> {
    let owner = ctx.sender();
    let template = npc_template(template_id.as_str())
        .ok_or_else(|| format!("Unknown NPC template '{template_id}'"))?;
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
    let owner = ctx.sender();
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

    Ok(())
}

pub(crate) fn npc_faction(ctx: &ReducerContext, identity: Identity) -> Option<NpcFaction> {
    let row = ctx.db.npc_instance().identity().find(identity)?;
    NpcFaction::from_wire(row.faction.as_str())
}

pub(crate) fn tick_npc_combat(ctx: &ReducerContext, now: Timestamp) {
    let movement_modifiers = movement_modifiers(ctx, now);
    let npcs: Vec<NpcInstance> = ctx.db.npc_instance().iter().collect();
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

        let Some(target) = acquire_npc_attack_target(ctx, npc.identity, &physics, template) else {
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

        if target.distance > npc_attack_reach(template, &target) {
            let move_speed_multiplier = movement_modifiers.move_speed_multiplier(&npc.identity, 0);
            if move_speed_multiplier > 0.0 {
                chase_npc_toward_target(
                    ctx,
                    now,
                    &npc,
                    &physics,
                    template,
                    &target,
                    move_speed_multiplier,
                );
            } else {
                face_npc_target(ctx, now, &physics, &target);
            }
            runtime.next_attack_at = now;
            runtime.next_attack_at_micros = timestamp_to_micros(now);
            upsert_npc_combat_runtime(ctx, runtime);
            continue;
        }

        let physics = face_npc_target(ctx, now, &physics, &target);

        perform_npc_melee_attack(ctx, now, &npc, &physics, template, &target);
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
    distance: f32,
    dir_x: f32,
    dir_z: f32,
}

fn acquire_npc_attack_target(
    ctx: &ReducerContext,
    npc_identity: Identity,
    npc_physics: &NpcPhysics,
    template: NpcTemplate,
) -> Option<NpcAttackTarget> {
    let aggro_radius_sq = template.aggro_radius * template.aggro_radius;
    let mut best: Option<NpcAttackTarget> = None;
    for state in ctx.db.player_state().iter() {
        if !state.alive || state.is_dummy {
            continue;
        }
        if !players_share_world_context(ctx, npc_identity, state.player_id) {
            continue;
        }
        if !can_harm(ctx, npc_identity, state.player_id) {
            continue;
        }
        let Some(physics) = ctx.db.player_physics().identity().find(state.player_id) else {
            continue;
        };
        let dx = physics.pos_x - npc_physics.pos_x;
        let dz = physics.pos_z - npc_physics.pos_z;
        let dist_sq = dx * dx + dz * dz;
        if dist_sq > aggro_radius_sq {
            continue;
        }
        if best
            .as_ref()
            .is_some_and(|existing| dist_sq >= existing.distance * existing.distance)
        {
            continue;
        }

        let distance = dist_sq.sqrt();
        let (dir_x, dir_z) = if distance > 0.001 {
            (dx / distance, dz / distance)
        } else {
            (0.0, 1.0)
        };
        best = Some(NpcAttackTarget {
            identity: state.player_id,
            pos_x: physics.pos_x,
            pos_y: physics.pos_y,
            pos_z: physics.pos_z,
            hit_radius: state.hit_radius,
            distance,
            dir_x,
            dir_z,
        });
    }
    best
}

fn npc_attack_reach(template: NpcTemplate, target: &NpcAttackTarget) -> f32 {
    template.attack_range + target.hit_radius
}

fn chase_npc_toward_target(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    template: NpcTemplate,
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

fn perform_npc_melee_attack(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    template: NpcTemplate,
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
    );
    emit_npc_combat_event(
        ctx,
        now,
        npc,
        physics,
        target,
        action_instance_id.as_str(),
        COMBAT_EVENT_IMPACT,
        template.attack_damage,
    );
    queue_effects(
        ctx,
        vec![EffectPacket::Damage {
            amount: template.attack_damage,
            damage_type: crate::combat::DamageType::Physical,
            source: npc.identity,
            target: target.identity,
            spell_id: action_instance_id,
            delivery: DamageDelivery::Direct,
            direct_action_key: NPC_MELEE_ACTION_KIND.to_string(),
        }],
    );
}

fn emit_npc_combat_event(
    ctx: &ReducerContext,
    now: Timestamp,
    npc: &NpcInstance,
    physics: &NpcPhysics,
    target: &NpcAttackTarget,
    action_instance_id: &str,
    event_type: &str,
    damage: i32,
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
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
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
    clear_loot_for_anchor(ctx, identity);
    if ctx.db.npc_instance().identity().find(identity).is_some() {
        ctx.db.npc_instance().identity().delete(identity);
    }
    if ctx.db.npc_state().identity().find(identity).is_some() {
        ctx.db.npc_state().identity().delete(identity);
    }
    if ctx.db.npc_physics().identity().find(identity).is_some() {
        ctx.db.npc_physics().identity().delete(identity);
    }
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
    if ctx
        .db
        .npc_combat_runtime()
        .identity()
        .find(row.identity)
        .is_some()
    {
        ctx.db.npc_combat_runtime().identity().update(row);
    } else {
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
        npc_identity, npc_template, yaw_for_direction, NpcFaction, NPC_FACTION_FRIENDLY,
        NPC_FACTION_HOSTILE, NPC_FACTION_NEUTRAL, NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD,
        NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD,
    };
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
}

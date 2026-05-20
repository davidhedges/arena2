//! Development-only projectile load harness.
//!
//! This module is intentionally siloed: normal combat code must not depend on
//! harness scenario ids, types, or helpers. The harness seeds normal runtime rows
//! and then existing projectile simulation owns the result.

use std::f32::consts::{PI, TAU};

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::actor_lifecycle::{
    despawn_actor_bundle, spawn_actor_bundle, ActorDespawnOptions, ActorSpawnSpec,
    ActorWorldAssignment, ActorWorldCleanup,
};
use crate::arena::{open_world_scene_name_for_identity, upsert_player_open_world_scene};
use crate::combat::{
    finish_combat_projectile_with_event, record_empty_combat_projectile_tick_metrics,
    timestamp_to_micros, ActiveCombatProjectile, CombatEvent, ProjectilePresentationEvent,
    COMBAT_EVENT_FIZZLE, COMBAT_EVENT_RELEASE, COMBAT_METADATA_NONE, COMBAT_SCALAR_NONE,
    COMBAT_SEQUENCE_NONE, DEFAULT_HIT_HEIGHT, DEFAULT_HIT_RADIUS,
};
use crate::player_state::PlayerState;
use crate::progression::projectile_body_vfx_id_for_spell;
use crate::spells::{spell_definition_by_str, SpellBehavior, SpellRuntimeDefinition};

#[allow(unused_imports)]
use crate::arena::player_open_world_scene as _;
#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::combat::active_combat_projectile as _;
#[allow(unused_imports)]
use crate::combat::active_combat_projectile_target_state as _;
#[allow(unused_imports)]
use crate::combat::combat_event as _;
#[allow(unused_imports)]
use crate::combat::projectile_presentation_event as _;
#[allow(unused_imports)]
use crate::player_intent::player_intent as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

const HARNESS_MAGIC: u128 = 0x7072_6f6a_6c6f_6164_6861_726e_6573_7300;
const HARNESS_ACTION_PREFIX: &str = "PROJECTILE_LOAD_HARNESS";
const HARNESS_HP: i32 = 10_000_000;
const MAX_PROJECTILE_COUNT: u32 = 5_000;
const MAX_TARGET_COUNT: u32 = 512;
const DEFAULT_TARGET_COUNT: u32 = 24;
const TARGET_SPAWN_Y_OFFSET: f32 = 0.0;
const PROJECTILE_SPAWN_Y_OFFSET: f32 = 1.2;
const STANDARD_ARROW_PROJECTILE_ID: &str = "ARROW_STANDARD";
const STANDARD_ARROW_SPEED: f32 = 45.0;
const STANDARD_ARROW_MAX_DISTANCE: f32 = 35.0;
const STANDARD_ARROW_RADIUS: f32 = 0.10;
const STANDARD_ARROW_UPDATE_INTERVAL: f32 = 0.10;

#[table(accessor = projectile_load_harness_run)]
pub struct ProjectileLoadHarnessRun {
    #[primary_key]
    pub owner: Identity,
    pub scenario: String,
    pub seed: u64,
    pub projectile_count: u32,
    pub target_count: u32,
    pub started_at: Timestamp,
}

#[table(accessor = projectile_load_harness_actor)]
pub struct ProjectileLoadHarnessActor {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub owner: Identity,
    pub slot: u32,
    pub kind: String,
    pub spawned_at: Timestamp,
}

#[reducer]
pub fn run_projectile_load_harness(
    ctx: &ReducerContext,
    scenario: String,
    projectile_count: u32,
    target_count: u32,
    seed: u64,
) -> Result<(), String> {
    crate::game_loop::ensure_game_loop_schedule(ctx);

    let owner = ctx.sender();
    let scenario = HarnessScenario::parse(scenario.as_str())?;
    let projectile_count = projectile_count.clamp(1, MAX_PROJECTILE_COUNT);
    let target_count = if target_count == 0 {
        DEFAULT_TARGET_COUNT
    } else {
        target_count.min(MAX_TARGET_COUNT)
    };

    let owner_physics = ctx
        .db
        .player_physics()
        .identity()
        .find(owner)
        .ok_or_else(|| "Cannot run projectile load harness without player_physics".to_string())?;
    if ctx.db.player_state().player_id().find(owner).is_none() {
        return Err("Cannot run projectile load harness without player_state".to_string());
    }
    let owner_world = ctx
        .db
        .player_world()
        .identity()
        .find(owner)
        .ok_or_else(|| "Cannot run projectile load harness without player_world".to_string())?;

    cleanup_projectile_load_harness_for_owner(ctx, owner)?;

    let targets = spawn_targets(
        ctx,
        owner,
        &owner_world,
        &scenario,
        target_count,
        seed,
        owner_physics.pos_x,
        owner_physics.pos_y,
        owner_physics.pos_z,
    )?;
    spawn_projectiles(
        ctx,
        owner,
        &scenario,
        projectile_count,
        seed,
        owner_physics.pos_x,
        owner_physics.pos_y,
        owner_physics.pos_z,
        &targets,
    )?;

    ctx.db
        .projectile_load_harness_run()
        .insert(ProjectileLoadHarnessRun {
            owner,
            scenario: scenario.as_str().to_string(),
            seed,
            projectile_count,
            target_count,
            started_at: ctx.timestamp,
        });

    log::info!(
        "[PROJECTILE_LOAD_HARNESS] owner={} scenario={} projectiles={} targets={} seed={}",
        owner.to_hex(),
        scenario.as_str(),
        projectile_count,
        target_count,
        seed
    );
    Ok(())
}

#[reducer]
pub fn cleanup_projectile_load_harness(ctx: &ReducerContext) -> Result<(), String> {
    cleanup_projectile_load_harness_for_owner(ctx, ctx.sender())
}

fn cleanup_projectile_load_harness_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<(), String> {
    let action_prefix = harness_action_prefix(owner);
    let projectiles: Vec<ActiveCombatProjectile> = ctx
        .db
        .active_combat_projectile()
        .iter()
        .filter(|projectile| {
            projectile.caster == owner && projectile.action_instance_id.starts_with(&action_prefix)
        })
        .collect();

    for projectile in projectiles {
        finish_combat_projectile_with_event(
            ctx,
            ctx.timestamp,
            &projectile,
            COMBAT_EVENT_FIZZLE,
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
        );
    }

    let actor_ids: Vec<Identity> = ctx
        .db
        .projectile_load_harness_actor()
        .owner()
        .filter(owner)
        .map(|actor| actor.identity)
        .collect();
    for identity in actor_ids {
        if ctx
            .db
            .projectile_load_harness_actor()
            .identity()
            .find(identity)
            .is_some()
        {
            ctx.db
                .projectile_load_harness_actor()
                .identity()
                .delete(identity);
        }
        let _ = despawn_actor_bundle(
            ctx,
            identity,
            ActorDespawnOptions {
                world_cleanup: ActorWorldCleanup::DeleteOnly,
            },
        );
        if ctx
            .db
            .player_open_world_scene()
            .identity()
            .find(identity)
            .is_some()
        {
            ctx.db.player_open_world_scene().identity().delete(identity);
        }
    }

    if ctx
        .db
        .projectile_load_harness_run()
        .owner()
        .find(owner)
        .is_some()
    {
        ctx.db.projectile_load_harness_run().owner().delete(owner);
    }

    record_empty_combat_projectile_tick_metrics(ctx, ctx.timestamp);

    Ok(())
}

fn spawn_targets(
    ctx: &ReducerContext,
    owner: Identity,
    owner_world: &crate::arena::PlayerWorld,
    scenario: &HarnessScenario,
    count: u32,
    seed: u64,
    owner_x: f32,
    owner_y: f32,
    owner_z: f32,
) -> Result<Vec<HarnessTarget>, String> {
    let mut targets = Vec::with_capacity(count as usize);
    let owner_scene = open_world_scene_name_for_identity(ctx, owner);
    for slot in 0..count {
        let identity = harness_identity(owner, HarnessIdentityKind::Target, slot, seed)?;
        let (pos_x, pos_z) = scenario.target_position(slot, count, owner_x, owner_z);
        let yaw = scenario.target_yaw(slot);
        spawn_actor_bundle(
            ctx,
            ActorSpawnSpec {
                identity,
                username: format!("Projectile Load Target {}", slot + 1),
                class_id: "WARRIOR".to_string(),
                pos_x,
                pos_y: owner_y + TARGET_SPAWN_Y_OFFSET,
                pos_z,
                yaw,
                vel_x: 0.0,
                vel_y: 0.0,
                vel_z: 0.0,
                grounded: true,
                last_processed_tick: 0,
                state: harness_player_state(identity, ctx.timestamp, scenario.targets_move()),
                world: Some(actor_world_assignment(owner_world)?),
            },
        )?;
        if scenario.targets_move() {
            if let Some(mut intent) = ctx.db.player_intent().identity().find(identity) {
                intent.forward = 1.0;
                intent.strafe = 0.0;
                intent.yaw = yaw;
                intent.updated_at = ctx.timestamp;
                ctx.db.player_intent().identity().update(intent);
            }
        }
        if owner_world.world_kind.eq_ignore_ascii_case("OPEN") {
            upsert_player_open_world_scene(ctx, identity, owner_scene.as_str());
            crate::arena::upsert_player_world(ctx, identity, "OPEN", None);
        }

        ctx.db
            .projectile_load_harness_actor()
            .insert(ProjectileLoadHarnessActor {
                identity,
                owner,
                slot,
                kind: "TARGET".to_string(),
                spawned_at: ctx.timestamp,
            });
        targets.push(HarnessTarget {
            identity,
            pos_x,
            pos_y: owner_y + TARGET_SPAWN_Y_OFFSET,
            pos_z,
        });
    }
    Ok(targets)
}

fn spawn_projectiles(
    ctx: &ReducerContext,
    owner: Identity,
    scenario: &HarnessScenario,
    projectile_count: u32,
    seed: u64,
    owner_x: f32,
    owner_y: f32,
    owner_z: f32,
    targets: &[HarnessTarget],
) -> Result<(), String> {
    for index in 0..projectile_count {
        let bucket = scenario.bucket_for_index(index);
        let target = targets.get(index as usize % targets.len());
        let spec = projectile_spec(bucket, index, target)?;
        let lane_offset = deterministic_lane_offset(index, seed);
        let origin = origin_for_projectile(&spec, index, owner_x, owner_y, owner_z, lane_offset);
        let direction = direction_for_projectile(&spec, origin, target);
        let action_instance_id = format!("{}:{}:{}", harness_action_prefix(owner), seed, index);
        let projectile_instance_id = format!("{action_instance_id}:p{}", spec.sequence_index);

        ctx.db
            .active_combat_projectile()
            .insert(ActiveCombatProjectile {
                projectile_instance_id: projectile_instance_id.clone(),
                action_instance_id: action_instance_id.clone(),
                projectile_sequence_index: spec.sequence_index,
                projectile_id: spec.projectile_id.clone(),
                source_kind: spec.source_kind.to_string(),
                action_kind: spec.action_kind.to_string(),
                ability_id: spec.ability_id.to_string(),
                motion_kind: spec.motion_kind.to_string(),
                caster: owner,
                intended_target: target
                    .map(|target| target.identity)
                    .unwrap_or(Identity::ZERO),
                origin_x: origin.x,
                origin_y: origin.y,
                origin_z: origin.z,
                pos_x: origin.x,
                pos_y: origin.y,
                pos_z: origin.z,
                dir_x: direction.x,
                dir_y: direction.y,
                dir_z: direction.z,
                speed: spec.speed,
                max_distance: spec.max_distance,
                radius: spec.radius,
                orbit_initial_yaw: spec.orbit_initial_yaw,
                orbit_radius: spec.orbit_radius,
                orbit_height: spec.orbit_height,
                orbit_angular_speed_deg_per_sec: spec.orbit_angular_speed_deg_per_sec,
                orbit_phase_offset_deg: spec.orbit_phase_offset_deg,
                orbit_hit_cooldown_seconds: spec.orbit_hit_cooldown_seconds,
                orbit_max_hits_per_target: spec.orbit_max_hits_per_target,
                boomerang_returning: false,
                boomerang_outbound_distance: spec.boomerang_outbound_distance,
                boomerang_return_speed: spec.boomerang_return_speed,
                boomerang_hit_cooldown_seconds: spec.boomerang_hit_cooldown_seconds,
                boomerang_max_hits_per_target: spec.boomerang_max_hits_per_target,
                traveled: 0.0,
                age: spec.initial_age,
                lifetime: spec.lifetime,
                update_accum: 0.0,
                update_interval_seconds: spec.update_interval_seconds,
                damage: spec.damage,
                parry_behavior: "PARRYABLE".to_string(),
                block_behavior: "BLOCKABLE".to_string(),
                grants_primary_resource_on_hit: false,
                hit_index: index,
                created_at: ctx.timestamp,
            });

        emit_release_event(
            ctx,
            &action_instance_id,
            &projectile_instance_id,
            &spec,
            owner,
            target
                .map(|target| target.identity)
                .unwrap_or(Identity::ZERO),
            origin,
            direction,
        );
    }
    Ok(())
}

fn emit_release_event(
    ctx: &ReducerContext,
    action_instance_id: &str,
    projectile_instance_id: &str,
    spec: &ProjectileSpec,
    caster: Identity,
    intended_target: Identity,
    origin: Vec3,
    direction: Vec3,
) {
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: action_instance_id.to_string(),
        action_kind: spec.action_kind.to_string(),
        ability_id: spec.ability_id.to_string(),
        hit_index: 0,
        event_type: COMBAT_EVENT_RELEASE.to_string(),
        source_kind: spec.source_kind.to_string(),
        caster,
        hit: Identity::ZERO,
        origin_x: origin.x,
        origin_y: origin.y,
        origin_z: origin.z,
        dir_x: direction.x,
        dir_y: direction.y,
        dir_z: direction.z,
        speed: spec.speed,
        max_distance: spec.max_distance,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: spec.sequence_index,
        sequence_count: 1,
        point_x: origin.x,
        point_y: origin.y,
        point_z: origin.z,
        created_at: ctx.timestamp,
        created_at_micros: timestamp_to_micros(ctx.timestamp),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });

    ctx.db
        .projectile_presentation_event()
        .insert(ProjectilePresentationEvent {
            event_id: 0,
            action_instance_id: action_instance_id.to_string(),
            action_kind: spec.action_kind.to_string(),
            ability_id: spec.ability_id.to_string(),
            source_kind: spec.source_kind.to_string(),
            projectile_id: spec.projectile_id.clone(),
            projectile_instance_id: projectile_instance_id.to_string(),
            hit_index: 0,
            event_type: COMBAT_EVENT_RELEASE.to_string(),
            caster,
            hit: Identity::ZERO,
            intended_target,
            origin_x: origin.x,
            origin_y: origin.y,
            origin_z: origin.z,
            dir_x: direction.x,
            dir_y: direction.y,
            dir_z: direction.z,
            point_x: origin.x,
            point_y: origin.y,
            point_z: origin.z,
            speed: spec.speed,
            max_distance: spec.max_distance,
            radius: spec.radius,
            motion_kind: spec.motion_kind.to_string(),
            update_interval_seconds: spec.update_interval_seconds,
            orbit_initial_yaw: spec.orbit_initial_yaw,
            orbit_radius: spec.orbit_radius,
            orbit_height: spec.orbit_height,
            orbit_angular_speed_deg_per_sec: spec.orbit_angular_speed_deg_per_sec,
            orbit_phase_offset_deg: spec.orbit_phase_offset_deg,
            boomerang_returning: false,
            boomerang_outbound_distance: spec.boomerang_outbound_distance,
            boomerang_return_speed: spec.boomerang_return_speed,
            sequence_index: spec.sequence_index,
            sequence_count: 1,
            terminal: false,
            created_at: ctx.timestamp,
            created_at_micros: timestamp_to_micros(ctx.timestamp),
        });
}

fn projectile_spec(
    bucket: ProjectileBucket,
    index: u32,
    target: Option<&HarnessTarget>,
) -> Result<ProjectileSpec, String> {
    match bucket {
        ProjectileBucket::LinearWeapon => Ok(ProjectileSpec {
            source_kind: "WEAPON",
            action_kind: "HARNESS_STANDARD_ARROW",
            ability_id: "HARNESS_STANDARD_ARROW",
            projectile_id: STANDARD_ARROW_PROJECTILE_ID.to_string(),
            motion_kind: "LINEAR",
            sequence_index: 0,
            speed: STANDARD_ARROW_SPEED,
            max_distance: STANDARD_ARROW_MAX_DISTANCE,
            radius: STANDARD_ARROW_RADIUS,
            damage: 1,
            lifetime: STANDARD_ARROW_MAX_DISTANCE / STANDARD_ARROW_SPEED,
            update_interval_seconds: STANDARD_ARROW_UPDATE_INTERVAL,
            ..ProjectileSpec::default()
        }),
        ProjectileBucket::LinearSpell => {
            spell_projectile_spec("ICICLE", "WARRIOR_ICICLE", index, target, Some(0.20))
        }
        ProjectileBucket::HomingSpell => {
            spell_projectile_spec("FIREBALL", "WARRIOR_FIREBALL", index, target, Some(0.0))
        }
        ProjectileBucket::Orbit => spell_projectile_spec(
            "ORBITING_BLADES",
            "WARRIOR_ORBITING_BLADES",
            index,
            target,
            None,
        ),
        ProjectileBucket::Boomerang => spell_projectile_spec(
            "BOOMERANG_ORB",
            "WARRIOR_BOOMERANG_ORB",
            index,
            target,
            None,
        ),
    }
}

fn spell_projectile_spec(
    spell_kind: &'static str,
    ability_id: &'static str,
    index: u32,
    _target: Option<&HarnessTarget>,
    initial_age_override: Option<f32>,
) -> Result<ProjectileSpec, String> {
    let definition = projectile_spell_definition(spell_kind)?;
    let projectile = definition
        .secondary
        .projectile
        .as_ref()
        .ok_or_else(|| format!("{spell_kind} does not define projectile secondary tunables"))?;
    let sequence_index = projectile
        .motion
        .orbit()
        .map(|orbit| index % orbit.projectile_count.max(1))
        .unwrap_or(0);
    let projectile_id = projectile_body_vfx_id_for_spell(ability_id, spell_kind, sequence_index)
        .unwrap_or_else(|| STANDARD_ARROW_PROJECTILE_ID.to_string());
    let mut spec = ProjectileSpec {
        source_kind: "SPELL",
        action_kind: spell_kind,
        ability_id,
        projectile_id,
        motion_kind: projectile.motion.kind(),
        sequence_index,
        speed: definition.speed,
        max_distance: definition.max_distance,
        radius: definition.radius,
        damage: definition.damage,
        lifetime: if definition.speed > 0.0 && definition.max_distance > 0.0 {
            definition.max_distance / definition.speed
        } else {
            definition.duration.max(1.0)
        },
        update_interval_seconds: definition.update_interval,
        initial_age: initial_age_override.unwrap_or(0.0),
        ..ProjectileSpec::default()
    };

    if let Some(orbit) = projectile.motion.orbit() {
        let phase = orbit.phase_offset_deg
            + 360.0 * sequence_index as f32 / orbit.projectile_count.max(1) as f32;
        spec.speed = 0.0;
        spec.max_distance = 0.0;
        spec.radius = orbit.hit_radius;
        spec.lifetime = orbit.lifetime_seconds;
        spec.orbit_radius = orbit.orbit_radius;
        spec.orbit_height = orbit.orbit_height;
        spec.orbit_angular_speed_deg_per_sec = orbit.angular_speed_deg_per_sec;
        spec.orbit_phase_offset_deg = phase;
        spec.orbit_hit_cooldown_seconds = orbit.hit_cooldown_seconds;
        spec.orbit_max_hits_per_target = orbit.max_hits_per_target;
    }

    if let Some(boomerang) = projectile.motion.boomerang() {
        spec.max_distance =
            definition.speed.max(boomerang.return_speed) * boomerang.lifetime_seconds;
        spec.lifetime = boomerang.lifetime_seconds;
        spec.radius = boomerang.hit_radius;
        spec.boomerang_outbound_distance = boomerang.outbound_distance;
        spec.boomerang_return_speed = boomerang.return_speed;
        spec.boomerang_hit_cooldown_seconds = boomerang.hit_cooldown_seconds;
        spec.boomerang_max_hits_per_target = boomerang.max_hits_per_target;
    }

    Ok(spec)
}

fn projectile_spell_definition(kind: &str) -> Result<&'static SpellRuntimeDefinition, String> {
    let definition =
        spell_definition_by_str(kind).ok_or_else(|| format!("Unknown harness spell '{kind}'"))?;
    if definition.behavior != SpellBehavior::Projectile {
        return Err(format!("Harness spell '{kind}' is not a projectile spell"));
    }
    Ok(definition)
}

fn origin_for_projectile(
    spec: &ProjectileSpec,
    index: u32,
    owner_x: f32,
    owner_y: f32,
    owner_z: f32,
    lane_offset: f32,
) -> Vec3 {
    if spec.motion_kind == "ORBIT_CASTER" {
        let angle = spec.orbit_initial_yaw + spec.orbit_phase_offset_deg.to_radians();
        return Vec3 {
            x: owner_x + angle.sin() * spec.orbit_radius,
            y: owner_y + spec.orbit_height,
            z: owner_z + angle.cos() * spec.orbit_radius,
        };
    }

    Vec3 {
        x: owner_x - 10.0 + (index % 5) as f32 * 0.35,
        y: owner_y + PROJECTILE_SPAWN_Y_OFFSET,
        z: owner_z + lane_offset,
    }
}

fn direction_for_projectile(
    spec: &ProjectileSpec,
    origin: Vec3,
    target: Option<&HarnessTarget>,
) -> Vec3 {
    if spec.motion_kind == "ORBIT_CASTER" {
        let angle = spec.orbit_initial_yaw + spec.orbit_phase_offset_deg.to_radians();
        return Vec3 {
            x: angle.cos(),
            y: 0.0,
            z: -angle.sin(),
        };
    }

    if let Some(target) = target {
        return normalize_vec3(
            target.pos_x - origin.x,
            target.pos_y + DEFAULT_HIT_HEIGHT * 0.5 - origin.y,
            target.pos_z - origin.z,
        )
        .unwrap_or(Vec3 {
            x: 1.0,
            y: 0.0,
            z: 0.0,
        });
    }

    Vec3 {
        x: 1.0,
        y: 0.0,
        z: 0.0,
    }
}

fn actor_world_assignment(
    world: &crate::arena::PlayerWorld,
) -> Result<ActorWorldAssignment, String> {
    if world.world_kind.eq_ignore_ascii_case("INSTANCE") {
        Ok(ActorWorldAssignment::Instance(
            world.instance_id.ok_or_else(|| {
                "Harness owner is in INSTANCE world without instance_id".to_string()
            })?,
        ))
    } else {
        Ok(ActorWorldAssignment::Open)
    }
}

fn harness_player_state(identity: Identity, now: Timestamp, moving_target: bool) -> PlayerState {
    let mut state = crate::combat::new_dummy_player_state(
        identity,
        now,
        HARNESS_HP,
        DEFAULT_HIT_RADIUS,
        DEFAULT_HIT_HEIGHT,
    );
    if moving_target {
        state.is_dummy = false;
    }
    state
}

fn harness_action_prefix(owner: Identity) -> String {
    format!("{HARNESS_ACTION_PREFIX}:{}", owner_tail(owner))
}

fn harness_identity(
    owner: Identity,
    kind: HarnessIdentityKind,
    slot: u32,
    seed: u64,
) -> Result<Identity, String> {
    let encoded = (u128::from(kind.code()) << 120)
        | (u128::from(slot) << 88)
        | ((u128::from(seed) & 0x0000_ffff_ffff_ffff) << 40)
        | owner_low_40(owner)?;
    let hex = format!("{HARNESS_MAGIC:032x}{encoded:032x}");
    Identity::from_hex(hex.as_str()).map_err(|error| {
        format!(
            "invalid projectile load harness identity owner={} slot={} seed={} error={}",
            owner.to_hex(),
            slot,
            seed,
            error
        )
    })
}

fn owner_tail(owner: Identity) -> String {
    owner
        .to_hex()
        .get(48..64)
        .unwrap_or("0000000000000000")
        .to_string()
}

fn owner_low_40(owner: Identity) -> Result<u128, String> {
    let hex = owner.to_hex();
    let tail = hex
        .get(54..64)
        .ok_or_else(|| format!("invalid owner identity hex '{}'", hex))?;
    u128::from_str_radix(tail, 16)
        .map_err(|error| format!("invalid owner identity tail '{}': {}", tail, error))
}

fn deterministic_lane_offset(index: u32, seed: u64) -> f32 {
    let mixed = splitmix64(u64::from(index) ^ seed);
    let unit = (mixed & 0xffff) as f32 / 65535.0;
    (unit - 0.5) * 10.0
}

fn splitmix64(mut value: u64) -> u64 {
    value = value.wrapping_add(0x9e37_79b9_7f4a_7c15);
    let mut z = value;
    z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    z ^ (z >> 31)
}

fn normalize_vec3(x: f32, y: f32, z: f32) -> Option<Vec3> {
    let len_sq = x * x + y * y + z * z;
    if len_sq <= 0.000001 {
        return None;
    }
    let inv_len = 1.0 / len_sq.sqrt();
    Some(Vec3 {
        x: x * inv_len,
        y: y * inv_len,
        z: z * inv_len,
    })
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum HarnessScenario {
    BaselineLinearArrows,
    LinearSpellProjectiles,
    HomingProjectiles,
    OrbitProjectiles,
    BoomerangProjectiles,
    MixedRealistic,
    MixedWorstCaseDenseTargets,
    MovingLaneHomingBoomerang,
}

impl HarnessScenario {
    fn parse(value: &str) -> Result<Self, String> {
        match value.trim().to_ascii_lowercase().as_str() {
            "baseline_linear_arrows" | "linear_arrows" | "arrows" => Ok(Self::BaselineLinearArrows),
            "linear_spell_projectiles" | "linear_spell" => Ok(Self::LinearSpellProjectiles),
            "homing_projectiles" | "homing" => Ok(Self::HomingProjectiles),
            "orbit_projectiles" | "orbit" => Ok(Self::OrbitProjectiles),
            "boomerang_projectiles" | "boomerang" => Ok(Self::BoomerangProjectiles),
            "mixed_realistic" | "mixed" => Ok(Self::MixedRealistic),
            "mixed_worst_case_dense_targets" | "mixed_dense" | "worst_dense" => {
                Ok(Self::MixedWorstCaseDenseTargets)
            }
            "moving_lane_homing_boomerang" | "moving_lane" | "moving_targets" => {
                Ok(Self::MovingLaneHomingBoomerang)
            }
            _ => Err(format!(
                "Unknown projectile load harness scenario '{value}'"
            )),
        }
    }

    fn as_str(self) -> &'static str {
        match self {
            Self::BaselineLinearArrows => "baseline_linear_arrows",
            Self::LinearSpellProjectiles => "linear_spell_projectiles",
            Self::HomingProjectiles => "homing_projectiles",
            Self::OrbitProjectiles => "orbit_projectiles",
            Self::BoomerangProjectiles => "boomerang_projectiles",
            Self::MixedRealistic => "mixed_realistic",
            Self::MixedWorstCaseDenseTargets => "mixed_worst_case_dense_targets",
            Self::MovingLaneHomingBoomerang => "moving_lane_homing_boomerang",
        }
    }

    fn bucket_for_index(self, index: u32) -> ProjectileBucket {
        match self {
            Self::BaselineLinearArrows => ProjectileBucket::LinearWeapon,
            Self::LinearSpellProjectiles => ProjectileBucket::LinearSpell,
            Self::HomingProjectiles => ProjectileBucket::HomingSpell,
            Self::OrbitProjectiles => ProjectileBucket::Orbit,
            Self::BoomerangProjectiles => ProjectileBucket::Boomerang,
            Self::MixedRealistic => match index % 100 {
                0..=59 => ProjectileBucket::LinearWeapon,
                60..=79 => ProjectileBucket::LinearSpell,
                80..=89 => ProjectileBucket::HomingSpell,
                90..=94 => ProjectileBucket::Orbit,
                _ => ProjectileBucket::Boomerang,
            },
            Self::MixedWorstCaseDenseTargets => match index % 100 {
                0..=34 => ProjectileBucket::LinearWeapon,
                35..=54 => ProjectileBucket::LinearSpell,
                55..=69 => ProjectileBucket::HomingSpell,
                70..=84 => ProjectileBucket::Orbit,
                _ => ProjectileBucket::Boomerang,
            },
            Self::MovingLaneHomingBoomerang => {
                if index % 2 == 0 {
                    ProjectileBucket::HomingSpell
                } else {
                    ProjectileBucket::Boomerang
                }
            }
        }
    }

    fn target_position(self, index: u32, count: u32, owner_x: f32, owner_z: f32) -> (f32, f32) {
        match self {
            Self::MovingLaneHomingBoomerang => {
                let center = (count.saturating_sub(1)) as f32 * 0.5;
                (
                    owner_x + 10.0 + (index % 4) as f32 * 0.8,
                    owner_z + (index as f32 - center) * 1.4,
                )
            }
            Self::OrbitProjectiles => {
                let angle = TAU * index as f32 / count.max(1) as f32;
                (owner_x + angle.sin() * 2.0, owner_z + angle.cos() * 2.0)
            }
            Self::MixedWorstCaseDenseTargets
            | Self::BoomerangProjectiles
            | Self::HomingProjectiles => {
                let side = (count as f32).sqrt().ceil().max(1.0) as u32;
                let x = index % side;
                let z = index / side;
                (
                    owner_x + 12.0 + x as f32 * 0.9,
                    owner_z + (z as f32 - side as f32 * 0.5) * 0.9,
                )
            }
            _ => {
                let center = (count.saturating_sub(1)) as f32 * 0.5;
                (
                    owner_x + 18.0 + (index % 5) as f32 * 0.5,
                    owner_z + (index as f32 - center) * 2.4,
                )
            }
        }
    }

    fn targets_move(self) -> bool {
        matches!(self, Self::MovingLaneHomingBoomerang)
    }

    fn target_yaw(self, index: u32) -> f32 {
        if !self.targets_move() {
            return PI;
        }

        if index % 2 == 0 {
            PI * 0.5
        } else {
            -PI * 0.5
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ProjectileBucket {
    LinearWeapon,
    LinearSpell,
    HomingSpell,
    Orbit,
    Boomerang,
}

#[derive(Clone, Copy)]
enum HarnessIdentityKind {
    Target,
}

impl HarnessIdentityKind {
    fn code(self) -> u8 {
        match self {
            Self::Target => 1,
        }
    }
}

struct HarnessTarget {
    identity: Identity,
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
}

#[derive(Clone)]
struct ProjectileSpec {
    source_kind: &'static str,
    action_kind: &'static str,
    ability_id: &'static str,
    projectile_id: String,
    motion_kind: &'static str,
    sequence_index: u32,
    speed: f32,
    max_distance: f32,
    radius: f32,
    damage: i32,
    lifetime: f32,
    update_interval_seconds: f32,
    initial_age: f32,
    orbit_initial_yaw: f32,
    orbit_radius: f32,
    orbit_height: f32,
    orbit_angular_speed_deg_per_sec: f32,
    orbit_phase_offset_deg: f32,
    orbit_hit_cooldown_seconds: f32,
    orbit_max_hits_per_target: u32,
    boomerang_outbound_distance: f32,
    boomerang_return_speed: f32,
    boomerang_hit_cooldown_seconds: f32,
    boomerang_max_hits_per_target: u32,
}

impl Default for ProjectileSpec {
    fn default() -> Self {
        Self {
            source_kind: "WEAPON",
            action_kind: "",
            ability_id: "",
            projectile_id: String::new(),
            motion_kind: "LINEAR",
            sequence_index: 0,
            speed: 0.0,
            max_distance: 0.0,
            radius: 0.0,
            damage: 0,
            lifetime: 1.0,
            update_interval_seconds: 0.10,
            initial_age: 0.0,
            orbit_initial_yaw: 0.0,
            orbit_radius: 0.0,
            orbit_height: 0.0,
            orbit_angular_speed_deg_per_sec: 0.0,
            orbit_phase_offset_deg: 0.0,
            orbit_hit_cooldown_seconds: 0.0,
            orbit_max_hits_per_target: 0,
            boomerang_outbound_distance: 0.0,
            boomerang_return_speed: 0.0,
            boomerang_hit_cooldown_seconds: 0.0,
            boomerang_max_hits_per_target: 0,
        }
    }
}

#[derive(Clone, Copy)]
struct Vec3 {
    x: f32,
    y: f32,
    z: f32,
}

#[cfg(test)]
mod tests {
    use super::{HarnessScenario, ProjectileBucket};

    #[test]
    fn scenario_aliases_are_supported() {
        assert_eq!(
            HarnessScenario::parse("mixed_dense").unwrap(),
            HarnessScenario::MixedWorstCaseDenseTargets
        );
        assert_eq!(
            HarnessScenario::parse("arrows").unwrap(),
            HarnessScenario::BaselineLinearArrows
        );
        assert_eq!(
            HarnessScenario::parse("moving_targets").unwrap(),
            HarnessScenario::MovingLaneHomingBoomerang
        );
    }

    #[test]
    fn mixed_realistic_uses_expected_bucket_ratios() {
        let scenario = HarnessScenario::MixedRealistic;
        let mut counts = [0_u32; 5];
        for index in 0..100 {
            let bucket = scenario.bucket_for_index(index);
            let slot = match bucket {
                ProjectileBucket::LinearWeapon => 0,
                ProjectileBucket::LinearSpell => 1,
                ProjectileBucket::HomingSpell => 2,
                ProjectileBucket::Orbit => 3,
                ProjectileBucket::Boomerang => 4,
            };
            counts[slot] += 1;
        }
        assert_eq!(counts, [60, 20, 10, 5, 5]);
    }

    #[test]
    fn moving_lane_scenario_uses_homing_and_boomerang_only() {
        let scenario = HarnessScenario::MovingLaneHomingBoomerang;
        assert!(scenario.targets_move());
        assert_eq!(scenario.bucket_for_index(0), ProjectileBucket::HomingSpell);
        assert_eq!(scenario.bucket_for_index(1), ProjectileBucket::Boomerang);
        assert_eq!(scenario.bucket_for_index(2), ProjectileBucket::HomingSpell);
    }
}

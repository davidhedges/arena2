use std::time::Duration;

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::action_ids::AuthoredActionId;
use crate::actor_lifecycle::{
    despawn_actor_bundle, spawn_actor_bundle, ActorDespawnOptions,
    ActorSpawnSpec as LifecycleActorSpawnSpec, ActorWorldAssignment, ActorWorldCleanup,
};
use crate::arena::{
    create_arena_instance_with_options, join_identity_into_instance, upsert_player_world,
    ArenaInstance,
};
use crate::combat::{
    clear_combat_engagement_for_identity, clear_statuses_for_identity, new_dummy_player_state,
    DEFAULT_HIT_HEIGHT, DEFAULT_HIT_RADIUS, DEFAULT_MAX_HP,
};
use crate::melee::{
    authored_strike_id_for_profile_position, get_melee_definition_for_authored, melee_cadence_for,
    melee_range_for, perform_melee_attack_for_practice,
};
use crate::player::DEFAULT_COMBAT_PROFILE;
use crate::spells::{cast_spell_for, spell_definition_by_str};
use crate::world_collision::resolve_world_spawn_position_with_layout;

#[allow(unused_imports)]
use crate::arena::arena_instance as _;
#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::player::player as _;
#[allow(unused_imports)]
use crate::player_input::player_command as _;
#[allow(unused_imports)]
use crate::player_input::player_input_cursor as _;
#[allow(unused_imports)]
use crate::player_intent::player_intent as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
use crate::player_physics::{commit_player_physics, PhysicsWriteMode};
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::practice::practice_actor as _;
#[allow(unused_imports)]
use crate::practice::practice_instance as _;

const TRAINING_MAX_PLAYERS: u32 = 1;
const TRAINING_SCENARIO_BASIC: &str = "BASIC";
const TRAINING_ACTOR_MAGIC: u128 = 0xfeed_u128 << 112;
const TRAINING_PLAYER_SPAWN_X: f32 = -8.0;
const TRAINING_PLAYER_SPAWN_Z: f32 = 0.0;
const TRAINING_PLAYER_SPAWN_YAW: f32 = std::f32::consts::FRAC_PI_2;
const TRAINING_DUMMY_HP: i32 = DEFAULT_MAX_HP * 250;
const TRAINING_CASTER_HP: i32 = DEFAULT_MAX_HP * 50;
const NO_TARGET_RETRY_DELAY: Duration = Duration::from_millis(250);
const FIREBALL_TURRET_CADENCE: Duration = Duration::from_millis(1400);

const BEHAVIOR_STATIC_DUMMY: &str = "STATIC_DUMMY";
const BEHAVIOR_FIREBALL_TURRET: &str = "FIREBALL_TURRET";
const BEHAVIOR_MELEE_TRAINER: &str = "MELEE_TRAINER";

const SLOT_DUMMY_LEFT: u8 = 1;
const SLOT_DUMMY_CENTER: u8 = 2;
const SLOT_DUMMY_RIGHT: u8 = 3;
const SLOT_FIREBALL_TURRET: u8 = 4;
const FIRST_TRAINER_SLOT: u8 = 5;
const TRAINER_LANE_X: [f32; 4] = [42.0, 50.0, 58.0, 66.0];
const TRAINER_LANE_Z: [f32; 4] = [-12.0, -4.0, 4.0, 12.0];

#[table(accessor = practice_instance)]
pub struct PracticeInstance {
    #[primary_key]
    pub arena_id: u64,
    pub scenario: String,
}

#[table(accessor = practice_actor)]
pub struct PracticeActor {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub arena_id: u64,
    pub slot: u8,
    pub behavior: String,
    pub home_x: f32,
    pub home_z: f32,
    pub home_yaw: f32,
    pub combat_profile: String,
    pub assigned_strike: String,
    pub cadence_ms: u64,
    pub next_action_at: Timestamp,
}

#[reducer]
pub fn join_training_instance(ctx: &ReducerContext) -> Result<(), String> {
    let arena_id = ensure_training_instance(ctx);
    join_identity_into_instance(ctx, ctx.sender(), arena_id)
}

pub(crate) fn is_training_instance(ctx: &ReducerContext, arena_id: u64) -> bool {
    ctx.db
        .practice_instance()
        .arena_id()
        .find(arena_id)
        .is_some()
}

pub(crate) fn resolve_instance_spawn_override(
    ctx: &ReducerContext,
    arena_id: u64,
    identity: Identity,
) -> Option<(f32, f32, f32)> {
    if !is_training_instance(ctx, arena_id) {
        return None;
    }

    if let Some(actor) = ctx.db.practice_actor().identity().find(identity) {
        return Some((actor.home_x, actor.home_z, actor.home_yaw));
    }

    Some((
        TRAINING_PLAYER_SPAWN_X,
        TRAINING_PLAYER_SPAWN_Z,
        TRAINING_PLAYER_SPAWN_YAW,
    ))
}

pub(crate) fn resolve_respawn_pose(
    ctx: &ReducerContext,
    identity: Identity,
    hit_radius: f32,
    hit_height: f32,
) -> Option<(f32, f32, f32, f32)> {
    let world = ctx.db.player_world().identity().find(identity)?;
    let arena_id = world.instance_id?;
    let (target_x, target_z, yaw) = resolve_instance_spawn_override(ctx, arena_id, identity)?;
    let arena = ctx.db.arena_instance().id().find(arena_id)?;
    let (spawn_x, spawn_y, spawn_z) = resolve_world_spawn_position_with_layout(
        Some(arena.seed),
        true,
        target_x,
        target_z,
        hit_radius.max(0.1),
        hit_height.max(0.5),
    );
    Some((spawn_x, spawn_y, spawn_z, yaw))
}

pub(crate) fn despawn_training_instance(ctx: &ReducerContext, arena_id: u64) {
    let actor_ids: Vec<Identity> = ctx
        .db
        .practice_actor()
        .arena_id()
        .filter(arena_id)
        .map(|actor| actor.identity)
        .collect();

    for identity in actor_ids {
        clear_statuses_for_identity(ctx, identity);
        clear_combat_engagement_for_identity(ctx, identity);
        ctx.db.practice_actor().identity().delete(identity);
        if let Err(error) = despawn_actor_bundle(
            ctx,
            identity,
            ActorDespawnOptions {
                world_cleanup: ActorWorldCleanup::DeleteOnly,
            },
        ) {
            log::error!(
                "[TRAINING] Failed to despawn training actor {}: {}",
                identity.to_hex(),
                error
            );
        }
    }

    if ctx
        .db
        .practice_instance()
        .arena_id()
        .find(arena_id)
        .is_some()
    {
        ctx.db.practice_instance().arena_id().delete(arena_id);
    }
}

pub(crate) fn tick_practice(ctx: &ReducerContext, now: Timestamp) -> Result<(), String> {
    let actor_ids: Vec<Identity> = ctx
        .db
        .practice_actor()
        .iter()
        .map(|actor| actor.identity)
        .collect();
    for identity in actor_ids {
        let Some(mut actor) = ctx.db.practice_actor().identity().find(identity) else {
            continue;
        };
        if actor.behavior == BEHAVIOR_STATIC_DUMMY {
            continue;
        }
        if now < actor.next_action_at {
            continue;
        }

        let Some(target_id) = select_training_target(ctx, actor.arena_id, actor.identity) else {
            actor.next_action_at = now + NO_TARGET_RETRY_DELAY;
            ctx.db.practice_actor().identity().update(actor);
            continue;
        };

        let Some((caster_x, caster_z, target_x, target_z)) =
            resolve_horizontal_positions(ctx, actor.identity, target_id)
        else {
            actor.next_action_at = now + NO_TARGET_RETRY_DELAY;
            ctx.db.practice_actor().identity().update(actor);
            continue;
        };

        let yaw = (target_x - caster_x).atan2(target_z - caster_z);
        orient_actor_towards(ctx, actor.identity, yaw, now);

        match actor.behavior.as_str() {
            BEHAVIOR_FIREBALL_TURRET => {
                let Some(spell_definition) = spell_definition_by_str("FIREBALL") else {
                    continue;
                };
                cast_spell_for(
                    ctx,
                    actor.identity,
                    &spell_definition.kind,
                    target_id.to_hex().as_str(),
                    0.0,
                    0.0,
                    0.0,
                    0,
                    0.0,
                    0.0,
                    0.0,
                    yaw,
                    now,
                    String::new(),
                    0,
                )?;
                actor.next_action_at = now + duration_from_millis(actor.cadence_ms);
            }
            BEHAVIOR_MELEE_TRAINER => {
                let authored_strike_id = AuthoredActionId::new(actor.assigned_strike.as_str());
                let Some(_strike) = get_melee_definition_for_authored(
                    ctx,
                    actor.combat_profile.as_str(),
                    &authored_strike_id,
                ) else {
                    actor.next_action_at = now + NO_TARGET_RETRY_DELAY;
                    ctx.db.practice_actor().identity().update(actor);
                    continue;
                };
                let Some(strike_range) =
                    melee_range_for(ctx, actor.combat_profile.as_str(), &authored_strike_id)
                else {
                    actor.next_action_at = now + NO_TARGET_RETRY_DELAY;
                    ctx.db.practice_actor().identity().update(actor);
                    continue;
                };

                let Some(target_state) = ctx.db.player_state().player_id().find(target_id) else {
                    actor.next_action_at = now + NO_TARGET_RETRY_DELAY;
                    ctx.db.practice_actor().identity().update(actor);
                    continue;
                };

                let dx = target_x - caster_x;
                let dz = target_z - caster_z;
                let horiz_dist = (dx * dx + dz * dz).sqrt();
                if horiz_dist > strike_range + target_state.hit_radius {
                    actor.next_action_at = now + NO_TARGET_RETRY_DELAY;
                    ctx.db.practice_actor().identity().update(actor);
                    continue;
                }

                perform_melee_attack_for_practice(
                    ctx,
                    actor.identity,
                    &authored_strike_id,
                    target_id.to_hex().as_str(),
                    caster_x,
                    0.0,
                    caster_z,
                    yaw,
                )?;
                actor.next_action_at = now + duration_from_millis(actor.cadence_ms);
            }
            _ => continue,
        }

        ctx.db.practice_actor().identity().update(actor);
    }

    Ok(())
}

fn ensure_training_instance(ctx: &ReducerContext) -> u64 {
    let open_training = ctx.db.practice_instance().iter().find_map(|practice| {
        let arena = ctx.db.arena_instance().id().find(practice.arena_id)?;
        (arena.player_count < arena.max_players).then_some(arena.id)
    });

    if let Some(arena_id) = open_training {
        return arena_id;
    }

    let arena_id = create_arena_instance_with_options(ctx, TRAINING_MAX_PLAYERS, true);
    let Some(arena) = ctx.db.arena_instance().id().find(arena_id) else {
        return arena_id;
    };

    ctx.db.practice_instance().insert(PracticeInstance {
        arena_id,
        scenario: TRAINING_SCENARIO_BASIC.to_string(),
    });
    spawn_training_actors(ctx, &arena);
    arena_id
}

fn spawn_training_actors(ctx: &ReducerContext, arena: &ArenaInstance) {
    spawn_training_actor(
        ctx,
        arena,
        TrainingActorSpawnSpec {
            slot: SLOT_DUMMY_LEFT,
            username: "Target Dummy A".to_string(),
            behavior: BEHAVIOR_STATIC_DUMMY,
            home_x: 3.5,
            home_z: -2.2,
            home_yaw: -std::f32::consts::FRAC_PI_2,
            combat_profile: DEFAULT_COMBAT_PROFILE,
            assigned_strike: String::new(),
            max_hp: TRAINING_DUMMY_HP,
            cadence: Duration::ZERO,
        },
    );
    spawn_training_actor(
        ctx,
        arena,
        TrainingActorSpawnSpec {
            slot: SLOT_DUMMY_CENTER,
            username: "Target Dummy B".to_string(),
            behavior: BEHAVIOR_STATIC_DUMMY,
            home_x: 4.5,
            home_z: 0.0,
            home_yaw: -std::f32::consts::FRAC_PI_2,
            combat_profile: DEFAULT_COMBAT_PROFILE,
            assigned_strike: String::new(),
            max_hp: TRAINING_DUMMY_HP,
            cadence: Duration::ZERO,
        },
    );
    spawn_training_actor(
        ctx,
        arena,
        TrainingActorSpawnSpec {
            slot: SLOT_DUMMY_RIGHT,
            username: "Target Dummy C".to_string(),
            behavior: BEHAVIOR_STATIC_DUMMY,
            home_x: 3.5,
            home_z: 2.2,
            home_yaw: -std::f32::consts::FRAC_PI_2,
            combat_profile: DEFAULT_COMBAT_PROFILE,
            assigned_strike: String::new(),
            max_hp: TRAINING_DUMMY_HP,
            cadence: Duration::ZERO,
        },
    );
    spawn_training_actor(
        ctx,
        arena,
        TrainingActorSpawnSpec {
            slot: SLOT_FIREBALL_TURRET,
            username: "Fireball Turret".to_string(),
            behavior: BEHAVIOR_FIREBALL_TURRET,
            home_x: 8.0,
            home_z: 0.0,
            home_yaw: -std::f32::consts::FRAC_PI_2,
            combat_profile: DEFAULT_COMBAT_PROFILE,
            assigned_strike: String::new(),
            max_hp: TRAINING_CASTER_HP,
            cadence: FIREBALL_TURRET_CADENCE,
        },
    );

    for (column, home_x) in TRAINER_LANE_X.iter().enumerate() {
        for (row, home_z) in TRAINER_LANE_Z.iter().enumerate() {
            let strike_index = column * TRAINER_LANE_Z.len() + row + 1;
            let slot = FIRST_TRAINER_SLOT + strike_index as u8 - 1;
            spawn_melee_trainer(
                ctx,
                arena,
                slot,
                trainer_username(strike_index),
                *home_x,
                *home_z,
                trainer_strike_id(strike_index),
            );
        }
    }
}

struct TrainingActorSpawnSpec {
    slot: u8,
    username: String,
    behavior: &'static str,
    home_x: f32,
    home_z: f32,
    home_yaw: f32,
    combat_profile: &'static str,
    assigned_strike: String,
    max_hp: i32,
    cadence: Duration,
}

fn spawn_melee_trainer(
    ctx: &ReducerContext,
    arena: &ArenaInstance,
    slot: u8,
    username: String,
    home_x: f32,
    home_z: f32,
    assigned_strike: String,
) {
    let authored_strike_id = AuthoredActionId::new(assigned_strike.as_str());
    let cadence = melee_cadence_for(ctx, DEFAULT_COMBAT_PROFILE, &authored_strike_id)
        .unwrap_or_else(|| Duration::from_millis(1200));
    spawn_training_actor(
        ctx,
        arena,
        TrainingActorSpawnSpec {
            slot,
            username,
            behavior: BEHAVIOR_MELEE_TRAINER,
            home_x,
            home_z,
            home_yaw: -std::f32::consts::FRAC_PI_2,
            combat_profile: DEFAULT_COMBAT_PROFILE,
            assigned_strike,
            max_hp: TRAINING_CASTER_HP,
            cadence,
        },
    );
}

fn spawn_training_actor(ctx: &ReducerContext, arena: &ArenaInstance, spec: TrainingActorSpawnSpec) {
    let identity = match training_actor_identity(arena.id, spec.slot) {
        Ok(identity) => identity,
        Err(error) => {
            log::error!("[TRAINING] {}", error);
            return;
        }
    };

    if ctx.db.practice_actor().identity().find(identity).is_some() {
        upsert_player_world(ctx, identity, "INSTANCE", Some(arena.id));
        return;
    }

    let now = ctx.timestamp;
    let (pos_x, pos_y, pos_z) = resolve_world_spawn_position_with_layout(
        Some(arena.seed),
        true,
        spec.home_x,
        spec.home_z,
        DEFAULT_HIT_RADIUS,
        DEFAULT_HIT_HEIGHT,
    );

    if let Err(error) = spawn_actor_bundle(
        ctx,
        LifecycleActorSpawnSpec {
            identity,
            username: spec.username,
            class_id: "WARRIOR".to_string(),
            pos_x,
            pos_y,
            pos_z,
            yaw: spec.home_yaw,
            vel_x: 0.0,
            vel_y: 0.0,
            vel_z: 0.0,
            grounded: true,
            last_processed_tick: 0,
            state: new_dummy_player_state(
                identity,
                now,
                spec.max_hp,
                DEFAULT_HIT_RADIUS,
                DEFAULT_HIT_HEIGHT,
            ),
            world: Some(ActorWorldAssignment::Instance(arena.id)),
        },
    ) {
        log::error!(
            "[TRAINING] Failed to spawn training actor arena={} slot={}: {}",
            arena.id,
            spec.slot,
            error
        );
        return;
    }
    ctx.db.practice_actor().insert(PracticeActor {
        identity,
        arena_id: arena.id,
        slot: spec.slot,
        behavior: spec.behavior.to_string(),
        home_x: spec.home_x,
        home_z: spec.home_z,
        home_yaw: spec.home_yaw,
        combat_profile: spec.combat_profile.to_string(),
        assigned_strike: spec.assigned_strike,
        cadence_ms: spec.cadence.as_millis() as u64,
        next_action_at: now + spec.cadence,
    });
}

fn trainer_username(strike_index: usize) -> String {
    format!("Sword Trainer {}", strike_index)
}

fn trainer_strike_id(strike_index: usize) -> String {
    authored_strike_id_for_profile_position(DEFAULT_COMBAT_PROFILE, strike_index.saturating_sub(1))
        .unwrap_or_else(|| format!("TRAINER_STRIKE_{}", strike_index))
}

fn training_actor_identity(arena_id: u64, slot: u8) -> Result<Identity, String> {
    let encoded = (u128::from(slot) << 56) | u128::from(arena_id & 0x00ff_ffff_ffff_ffff);
    let hex = format!("{:064x}", TRAINING_ACTOR_MAGIC | encoded);
    Identity::from_hex(hex.as_str()).map_err(|error| {
        format!(
            "invalid training actor identity for arena {} slot {} ({}): {}",
            arena_id, slot, hex, error
        )
    })
}

fn select_training_target(
    ctx: &ReducerContext,
    arena_id: u64,
    caster: Identity,
) -> Option<Identity> {
    let caster_physics = ctx.db.player_physics().identity().find(caster)?;
    let mut best: Option<(Identity, f32)> = None;

    for state in ctx.db.player_state().alive().filter(true) {
        if state.player_id == caster || state.is_dummy {
            continue;
        }
        let Some(world) = ctx.db.player_world().identity().find(state.player_id) else {
            continue;
        };
        if world.instance_id != Some(arena_id) {
            continue;
        }
        let Some(physics) = ctx.db.player_physics().identity().find(state.player_id) else {
            continue;
        };
        let dx = physics.pos_x - caster_physics.pos_x;
        let dz = physics.pos_z - caster_physics.pos_z;
        let dist_sq = dx * dx + dz * dz;
        if best.is_none_or(|(_, best_dist_sq)| dist_sq < best_dist_sq) {
            best = Some((state.player_id, dist_sq));
        }
    }

    best.map(|(identity, _)| identity)
}

fn resolve_horizontal_positions(
    ctx: &ReducerContext,
    caster: Identity,
    target: Identity,
) -> Option<(f32, f32, f32, f32)> {
    let caster_physics = ctx.db.player_physics().identity().find(caster)?;
    let target_physics = ctx.db.player_physics().identity().find(target)?;
    Some((
        caster_physics.pos_x,
        caster_physics.pos_z,
        target_physics.pos_x,
        target_physics.pos_z,
    ))
}

fn orient_actor_towards(ctx: &ReducerContext, identity: Identity, yaw: f32, now: Timestamp) {
    if let Some(mut physics) = ctx.db.player_physics().identity().find(identity) {
        physics.yaw = yaw;
        physics.updated_at = now;
        commit_player_physics(
            ctx,
            physics,
            PhysicsWriteMode::Normal,
            "orient_actor_towards",
        );
    }
    if let Some(mut intent) = ctx.db.player_intent().identity().find(identity) {
        intent.yaw = yaw;
        intent.updated_at = now;
        ctx.db.player_intent().identity().update(intent);
    }
}

fn duration_from_millis(millis: u64) -> Duration {
    Duration::from_millis(millis.max(1))
}

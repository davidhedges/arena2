use spacetimedb::{table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::{arena_seed_for_identity, open_world_scene_name_for_identity};
use crate::arena::{resolve_player_world_context, ResolvedWorldContext, WorldRayHit};
use crate::combat::actor_snapshot::CombatActorSnapshot;
use crate::combat::timestamp_to_micros;
use crate::practice::is_training_instance;
use crate::spells::WorldObstacleSecondaryTunables;
use crate::world_collision::surface_height_for_world_at_y_with_layout_for_scene;

const COLLISION_EPSILON: f32 = 0.001;

#[table(accessor = active_world_obstacle, public)]
#[derive(Clone)]
pub struct ActiveWorldObstacle {
    #[primary_key]
    #[auto_inc]
    pub obstacle_id: u64,
    #[index(btree)]
    pub owner: Identity,
    pub spell_id: String,
    pub ability_id: String,
    pub visual_resource_path: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    pub open_world_scene_name: String,
    pub root_x: f32,
    pub root_y: f32,
    pub root_z: f32,
    pub center_x: f32,
    pub center_y: f32,
    pub center_z: f32,
    pub yaw: f32,
    pub half_width: f32,
    pub half_height: f32,
    pub half_depth: f32,
    pub spawned_at: Timestamp,
    pub expires_at: Timestamp,
    #[index(btree)]
    pub expires_at_micros: i64,
}

pub(crate) fn spawn_world_obstacle(
    ctx: &ReducerContext,
    caster: Identity,
    caster_state: &CombatActorSnapshot,
    spell_id: &str,
    ability_id: &str,
    tunables: &WorldObstacleSecondaryTunables,
    now: Timestamp,
) -> Result<(), String> {
    let Some(world_context) = resolve_player_world_context(ctx, caster) else {
        return Err("Cannot place a world obstacle without a resolved world context".to_string());
    };

    let forward_x = caster_state.facing_yaw.sin();
    let forward_z = caster_state.facing_yaw.cos();
    let right_x = forward_z;
    let right_z = -forward_x;
    let root_x = caster_state.pos_x + forward_x * tunables.forward_distance;
    let root_z = caster_state.pos_z + forward_z * tunables.forward_distance;
    let arena_seed = arena_seed_for_identity(ctx, caster);
    let flat_ground_only = matches!(
        &world_context,
        ResolvedWorldContext::Instance(instance_id) if is_training_instance(ctx, *instance_id)
    );
    let scene_name = open_world_scene_name_for_identity(ctx, caster);
    let root_y = surface_height_for_world_at_y_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        Some(scene_name.as_str()),
        root_x,
        root_z,
        caster_state.pos_y,
    );
    let center_x = root_x
        + right_x * tunables.center_right_offset
        + forward_x * tunables.center_forward_offset;
    let center_z = root_z
        + right_z * tunables.center_right_offset
        + forward_z * tunables.center_forward_offset;
    let center_y = root_y + tunables.center_up_offset;
    let yaw = caster_state.facing_yaw + tunables.yaw_offset_degrees.to_radians();
    let expires_at = now + tunables.duration;
    let (world_kind, instance_id, open_world_scene_name) = match world_context {
        ResolvedWorldContext::Open(scene) => ("OPEN".to_string(), None, scene),
        ResolvedWorldContext::Instance(instance_id) => {
            ("INSTANCE".to_string(), Some(instance_id), String::new())
        }
    };

    ctx.db.active_world_obstacle().insert(ActiveWorldObstacle {
        obstacle_id: 0,
        owner: caster,
        spell_id: spell_id.trim().to_ascii_uppercase(),
        ability_id: ability_id.trim().to_ascii_uppercase(),
        visual_resource_path: tunables.visual_resource_path.clone(),
        world_kind,
        instance_id,
        open_world_scene_name,
        root_x,
        root_y,
        root_z,
        center_x,
        center_y,
        center_z,
        yaw,
        half_width: tunables.width * 0.5,
        half_height: tunables.height * 0.5,
        half_depth: tunables.depth * 0.5,
        spawned_at: now,
        expires_at,
        expires_at_micros: timestamp_to_micros(expires_at),
    });
    Ok(())
}

pub(crate) fn expire_world_obstacles(ctx: &ReducerContext, now: Timestamp) {
    let due: Vec<u64> = ctx
        .db
        .active_world_obstacle()
        .expires_at_micros()
        .filter(..=timestamp_to_micros(now))
        .map(|row| row.obstacle_id)
        .collect();
    for obstacle_id in due {
        ctx.db
            .active_world_obstacle()
            .obstacle_id()
            .delete(obstacle_id);
    }
}

pub(crate) fn clear_world_obstacles_for_owner(ctx: &ReducerContext, owner: Identity) {
    ctx.db.active_world_obstacle().owner().delete(owner);
}

fn obstacle_shares_world(
    ctx: &ReducerContext,
    actor: Identity,
    obstacle: &ActiveWorldObstacle,
) -> bool {
    let Some(context) = resolve_player_world_context(ctx, actor) else {
        return false;
    };
    match context {
        ResolvedWorldContext::Open(scene) => {
            obstacle.world_kind == "OPEN" && obstacle.open_world_scene_name == scene
        }
        ResolvedWorldContext::Instance(instance_id) => {
            obstacle.world_kind == "INSTANCE" && obstacle.instance_id == Some(instance_id)
        }
    }
}

fn active_obstacles_for_actor(
    ctx: &ReducerContext,
    actor: Identity,
) -> impl Iterator<Item = ActiveWorldObstacle> + '_ {
    ctx.db
        .active_world_obstacle()
        .iter()
        .filter(move |row| row.expires_at > ctx.timestamp && obstacle_shares_world(ctx, actor, row))
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn resolve_active_world_obstacle_movement(
    ctx: &ReducerContext,
    actor: Identity,
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    radius: f32,
    foot_y: f32,
    height: f32,
) -> (f32, f32) {
    let mut out_x = target_x;
    let mut out_z = target_z;
    for obstacle in active_obstacles_for_actor(ctx, actor) {
        let obstacle_min_y = obstacle.center_y - obstacle.half_height;
        let obstacle_max_y = obstacle.center_y + obstacle.half_height;
        if foot_y + height <= obstacle_min_y || foot_y >= obstacle_max_y {
            continue;
        }
        let Some(t) =
            segment_obb_hit_fraction_xz(&obstacle, start_x, start_z, out_x, out_z, radius.max(0.0))
        else {
            continue;
        };
        let safe_t = (t - COLLISION_EPSILON).max(0.0);
        out_x = start_x + (out_x - start_x) * safe_t;
        out_z = start_z + (out_z - start_z) * safe_t;
    }
    (out_x, out_z)
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn first_active_world_obstacle_hit(
    ctx: &ReducerContext,
    actor: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
) -> Option<WorldRayHit> {
    let dx = end_x - start_x;
    let dy = end_y - start_y;
    let dz = end_z - start_z;
    let distance = (dx * dx + dy * dy + dz * dz).sqrt();
    if distance <= f32::EPSILON {
        return None;
    }

    let mut best: Option<WorldRayHit> = None;
    for obstacle in active_obstacles_for_actor(ctx, actor) {
        let Some(fraction) = segment_obb_hit_fraction_3d(
            &obstacle, start_x, start_y, start_z, end_x, end_y, end_z, radius,
        ) else {
            continue;
        };
        let hit = WorldRayHit {
            t: distance * fraction,
            x: start_x + dx * fraction,
            y: start_y + dy * fraction,
            z: start_z + dz * fraction,
        };
        if best.is_none_or(|existing| hit.t < existing.t) {
            best = Some(hit);
        }
    }
    best
}

fn segment_obb_hit_fraction_xz(
    obstacle: &ActiveWorldObstacle,
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    radius: f32,
) -> Option<f32> {
    let (start_local_x, start_local_z) = obstacle_local_xz(obstacle, start_x, start_z);
    let (end_local_x, end_local_z) = obstacle_local_xz(obstacle, end_x, end_z);
    let half_x = obstacle.half_width + radius;
    let half_z = obstacle.half_depth + radius;
    if start_local_x.abs() <= half_x && start_local_z.abs() <= half_z {
        return None;
    }
    segment_aabb_fraction(
        [start_local_x, start_local_z],
        [end_local_x, end_local_z],
        [-half_x, -half_z],
        [half_x, half_z],
    )
}

#[allow(clippy::too_many_arguments)]
fn segment_obb_hit_fraction_3d(
    obstacle: &ActiveWorldObstacle,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
) -> Option<f32> {
    let (start_local_x, start_local_z) = obstacle_local_xz(obstacle, start_x, start_z);
    let (end_local_x, end_local_z) = obstacle_local_xz(obstacle, end_x, end_z);
    segment_aabb_fraction(
        [start_local_x, start_y - obstacle.center_y, start_local_z],
        [end_local_x, end_y - obstacle.center_y, end_local_z],
        [
            -obstacle.half_width - radius,
            -obstacle.half_height - radius,
            -obstacle.half_depth - radius,
        ],
        [
            obstacle.half_width + radius,
            obstacle.half_height + radius,
            obstacle.half_depth + radius,
        ],
    )
}

fn obstacle_local_xz(obstacle: &ActiveWorldObstacle, world_x: f32, world_z: f32) -> (f32, f32) {
    let dx = world_x - obstacle.center_x;
    let dz = world_z - obstacle.center_z;
    let sin_yaw = obstacle.yaw.sin();
    let cos_yaw = obstacle.yaw.cos();
    (dx * cos_yaw - dz * sin_yaw, dx * sin_yaw + dz * cos_yaw)
}

fn segment_aabb_fraction<const N: usize>(
    start: [f32; N],
    end: [f32; N],
    min: [f32; N],
    max: [f32; N],
) -> Option<f32> {
    let mut enter = 0.0_f32;
    let mut exit = 1.0_f32;
    for axis in 0..N {
        let delta = end[axis] - start[axis];
        if delta.abs() <= f32::EPSILON {
            if start[axis] < min[axis] || start[axis] > max[axis] {
                return None;
            }
            continue;
        }
        let inverse = 1.0 / delta;
        let mut near = (min[axis] - start[axis]) * inverse;
        let mut far = (max[axis] - start[axis]) * inverse;
        if near > far {
            std::mem::swap(&mut near, &mut far);
        }
        enter = enter.max(near);
        exit = exit.min(far);
        if enter > exit {
            return None;
        }
    }
    (enter <= 1.0 && exit >= 0.0).then_some(enter.max(0.0))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn obstacle() -> ActiveWorldObstacle {
        ActiveWorldObstacle {
            obstacle_id: 1,
            owner: Identity::ZERO,
            spell_id: "STONESPIRE".to_string(),
            ability_id: "SPELL_STONESPIRE".to_string(),
            visual_resource_path: "CombatVFX/playground/test".to_string(),
            world_kind: "OPEN".to_string(),
            instance_id: None,
            open_world_scene_name: "Oasis_Day".to_string(),
            root_x: 0.0,
            root_y: 0.0,
            root_z: 0.0,
            center_x: 0.0,
            center_y: 4.5,
            center_z: 0.0,
            yaw: 0.0,
            half_width: 2.25,
            half_height: 4.5,
            half_depth: 1.25,
            spawned_at: Timestamp::UNIX_EPOCH,
            expires_at: Timestamp::UNIX_EPOCH + std::time::Duration::from_secs(3),
            expires_at_micros: 3_000_000,
        }
    }

    #[test]
    fn movement_segment_stops_at_expanded_obstacle_face() {
        let hit = segment_obb_hit_fraction_xz(&obstacle(), 0.0, -4.0, 0.0, 4.0, 0.25);
        assert!(hit.is_some_and(|fraction| (fraction - 0.3125).abs() < 0.001));
    }

    #[test]
    fn sight_segment_hits_vertical_obstacle() {
        let hit = segment_obb_hit_fraction_3d(&obstacle(), 0.0, 1.5, -4.0, 0.0, 1.5, 4.0, 0.05);
        assert!(hit.is_some());
    }
}

use spacetimedb::{table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::{
    arena_seed_for_identity, instance_uses_flat_layout, open_world_scene_name_for_identity,
};
use crate::arena::{resolve_player_world_context, ResolvedWorldContext, WorldRayHit};
use crate::combat::actor_snapshot::CombatActorSnapshot;
use crate::combat::timestamp_to_micros;
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
    /// Query-safe scalar mirror of `instance_id`; zero means open world.
    #[index(btree)]
    pub instance_scope_id: u64,
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
    #[default(0.0f32)]
    pub collision_rotation_x: f32,
    #[default(0.0f32)]
    pub collision_rotation_y: f32,
    #[default(0.0f32)]
    pub collision_rotation_z: f32,
    #[default(1.0f32)]
    pub collision_rotation_w: f32,
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
    let root_x = caster_state.pos_x + forward_x * tunables.forward_distance;
    let root_z = caster_state.pos_z + forward_z * tunables.forward_distance;
    let arena_seed = arena_seed_for_identity(ctx, caster);
    let flat_ground_only = matches!(
        &world_context,
        ResolvedWorldContext::Instance(instance_id) if instance_uses_flat_layout(ctx, *instance_id)
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
    let yaw = caster_state.facing_yaw + tunables.visual_yaw_offset_degrees.to_radians();
    let local_center = tunables.collider_local_center;
    let center_x = root_x + local_center[0] * yaw.cos() + local_center[2] * yaw.sin();
    let center_y = root_y + local_center[1];
    let center_z = root_z - local_center[0] * yaw.sin() + local_center[2] * yaw.cos();
    let root_rotation = [0.0, (yaw * 0.5).sin(), 0.0, (yaw * 0.5).cos()];
    let collision_rotation = normalize_quaternion(quaternion_multiply(
        root_rotation,
        tunables.collider_local_rotation,
    ));
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
        instance_scope_id: instance_id.unwrap_or_default(),
        open_world_scene_name,
        root_x,
        root_y,
        root_z,
        center_x,
        center_y,
        center_z,
        yaw,
        half_width: tunables.collider_size[0] * 0.5,
        half_height: tunables.collider_size[1] * 0.5,
        half_depth: tunables.collider_size[2] * 0.5,
        spawned_at: now,
        expires_at,
        expires_at_micros: timestamp_to_micros(expires_at),
        collision_rotation_x: collision_rotation[0],
        collision_rotation_y: collision_rotation[1],
        collision_rotation_z: collision_rotation[2],
        collision_rotation_w: collision_rotation[3],
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
        let Some(t) = segment_obb_movement_hit_fraction(
            &obstacle,
            start_x,
            start_z,
            out_x,
            out_z,
            radius.max(0.0),
            foot_y,
            height.max(0.0),
        ) else {
            continue;
        };
        let safe_t = (t - COLLISION_EPSILON).max(0.0);
        out_x = start_x + (out_x - start_x) * safe_t;
        out_z = start_z + (out_z - start_z) * safe_t;
    }
    let (out_x, out_z) = crate::spells::resolve_hostile_sanctuary_movement(
        ctx, actor, start_x, start_z, out_x, out_z, radius,
    );
    let (out_x, out_z) = crate::spells::resolve_hostile_necro_prison_movement(
        ctx, actor, start_x, start_z, out_x, out_z, radius,
    );
    crate::world_interactions::resolve_closed_door_movement(
        ctx, actor, start_x, start_z, out_x, out_z, radius, foot_y, height,
    )
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
    let obstacle_hit = first_active_spell_world_obstacle_hit(
        ctx, actor, start_x, start_y, start_z, end_x, end_y, end_z, radius,
    );
    let door_hit = crate::world_interactions::first_closed_door_hit(
        ctx, actor, start_x, start_y, start_z, end_x, end_y, end_z, radius,
    );
    match (obstacle_hit, door_hit) {
        (Some(obstacle), Some(door)) => Some(if door.t < obstacle.t { door } else { obstacle }),
        (Some(hit), None) | (None, Some(hit)) => Some(hit),
        (None, None) => None,
    }
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn first_active_spell_world_obstacle_hit(
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

fn segment_obb_movement_hit_fraction(
    obstacle: &ActiveWorldObstacle,
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    radius: f32,
    foot_y: f32,
    height: f32,
) -> Option<f32> {
    let half_height = height * 0.5;
    let actor_center_y = foot_y + half_height;
    let start_local = obstacle_local_point(obstacle, [start_x, actor_center_y, start_z]);
    let end_local = obstacle_local_point(obstacle, [end_x, actor_center_y, end_z]);
    let rotation = obstacle_collision_rotation(obstacle);
    let world_x_local = inverse_rotate_vector(rotation, [1.0, 0.0, 0.0]);
    let world_y_local = inverse_rotate_vector(rotation, [0.0, 1.0, 0.0]);
    let world_z_local = inverse_rotate_vector(rotation, [0.0, 0.0, 1.0]);
    let mut actor_extent_local = [0.0; 3];
    for axis in 0..3 {
        actor_extent_local[axis] = world_x_local[axis].abs() * radius
            + world_y_local[axis].abs() * half_height
            + world_z_local[axis].abs() * radius;
    }
    let half_extents = [
        obstacle.half_width + actor_extent_local[0],
        obstacle.half_height + actor_extent_local[1],
        obstacle.half_depth + actor_extent_local[2],
    ];
    if (0..3).all(|axis| start_local[axis].abs() <= half_extents[axis]) {
        return None;
    }
    segment_aabb_fraction(
        start_local,
        end_local,
        [-half_extents[0], -half_extents[1], -half_extents[2]],
        half_extents,
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
    let start_local = obstacle_local_point(obstacle, [start_x, start_y, start_z]);
    let end_local = obstacle_local_point(obstacle, [end_x, end_y, end_z]);
    segment_aabb_fraction(
        start_local,
        end_local,
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

fn obstacle_collision_rotation(obstacle: &ActiveWorldObstacle) -> [f32; 4] {
    normalize_quaternion([
        obstacle.collision_rotation_x,
        obstacle.collision_rotation_y,
        obstacle.collision_rotation_z,
        obstacle.collision_rotation_w,
    ])
}

fn obstacle_local_point(obstacle: &ActiveWorldObstacle, world: [f32; 3]) -> [f32; 3] {
    inverse_rotate_vector(
        obstacle_collision_rotation(obstacle),
        [
            world[0] - obstacle.center_x,
            world[1] - obstacle.center_y,
            world[2] - obstacle.center_z,
        ],
    )
}

fn normalize_quaternion(quaternion: [f32; 4]) -> [f32; 4] {
    let magnitude_squared = quaternion.iter().map(|value| value * value).sum::<f32>();
    if magnitude_squared <= f32::EPSILON {
        return [0.0, 0.0, 0.0, 1.0];
    }
    let inverse_magnitude = magnitude_squared.sqrt().recip();
    [
        quaternion[0] * inverse_magnitude,
        quaternion[1] * inverse_magnitude,
        quaternion[2] * inverse_magnitude,
        quaternion[3] * inverse_magnitude,
    ]
}

fn quaternion_multiply(left: [f32; 4], right: [f32; 4]) -> [f32; 4] {
    [
        left[3] * right[0] + left[0] * right[3] + left[1] * right[2] - left[2] * right[1],
        left[3] * right[1] - left[0] * right[2] + left[1] * right[3] + left[2] * right[0],
        left[3] * right[2] + left[0] * right[1] - left[1] * right[0] + left[2] * right[3],
        left[3] * right[3] - left[0] * right[0] - left[1] * right[1] - left[2] * right[2],
    ]
}

fn inverse_rotate_vector(quaternion: [f32; 4], vector: [f32; 3]) -> [f32; 3] {
    rotate_vector(
        [
            -quaternion[0],
            -quaternion[1],
            -quaternion[2],
            quaternion[3],
        ],
        vector,
    )
}

fn rotate_vector(quaternion: [f32; 4], vector: [f32; 3]) -> [f32; 3] {
    let vector_quaternion = [vector[0], vector[1], vector[2], 0.0];
    let conjugate = [
        -quaternion[0],
        -quaternion[1],
        -quaternion[2],
        quaternion[3],
    ];
    let rotated = quaternion_multiply(
        quaternion_multiply(quaternion, vector_quaternion),
        conjugate,
    );
    [rotated[0], rotated[1], rotated[2]]
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
            spell_id: "UPHEAVAL".to_string(),
            ability_id: "SPELL_UPHEAVAL".to_string(),
            visual_resource_path: "CombatVFX/playground/test".to_string(),
            world_kind: "OPEN".to_string(),
            instance_id: None,
            instance_scope_id: 0,
            open_world_scene_name: "Oasis_Day".to_string(),
            root_x: 0.0,
            root_y: 0.0,
            root_z: 0.0,
            center_x: 0.0,
            center_y: 3.5,
            center_z: 0.0,
            yaw: 0.0,
            half_width: 1.0,
            half_height: 3.5,
            half_depth: 1.25,
            spawned_at: Timestamp::UNIX_EPOCH,
            expires_at: Timestamp::UNIX_EPOCH + std::time::Duration::from_secs(3),
            expires_at_micros: 3_000_000,
            collision_rotation_x: 0.0,
            collision_rotation_y: 0.0,
            collision_rotation_z: 0.0,
            collision_rotation_w: 1.0,
        }
    }

    #[test]
    fn movement_segment_stops_at_expanded_obstacle_face() {
        let hit =
            segment_obb_movement_hit_fraction(&obstacle(), 0.0, -4.0, 0.0, 4.0, 0.25, 0.0, 1.8);
        assert!(hit.is_some_and(|fraction| (fraction - 0.3125).abs() < 0.001));
    }

    #[test]
    fn sight_segment_hits_vertical_obstacle() {
        let hit = segment_obb_hit_fraction_3d(&obstacle(), 0.0, 1.5, -4.0, 0.0, 1.5, 4.0, 0.05);
        assert!(hit.is_some());
    }

    #[test]
    fn local_point_uses_full_collision_rotation() {
        let mut rotated = obstacle();
        let half_sqrt_two = 0.5_f32.sqrt();
        rotated.collision_rotation_z = half_sqrt_two;
        rotated.collision_rotation_w = half_sqrt_two;
        rotated.center_y = 0.0;

        let local = obstacle_local_point(&rotated, [0.0, 1.0, 0.0]);
        assert!((local[0] - 1.0).abs() < 0.001);
        assert!(local[1].abs() < 0.001);
        assert!(local[2].abs() < 0.001);
    }
}

use crate::arena::{
    raycast_arena_world_with, resolve_arena_horizontal_collision_y, surface_height_at_y,
    WorldRayHit, WorldRaycastRequest, COLLISION_EPSILON, SURFACE_SNAP_UP,
};
use crate::movement::GROUND_Y;
use crate::open_world_scene::{
    default_open_world_scene_profile, open_world_scene_profile_for_scene, OpenWorldSceneProfile,
    ADVENTURE_ISLAND_PROFILE, DESERT_DAY_PROFILE, DOCKS_DAY_PROFILE,
    GOLDEN_VALLEY_OVERCAST_PROFILE, GOLDEN_VALLEY_SUNNY_PROFILE, GREAT_HALL_DAY_PROFILE,
    IDOL_DAY_PROFILE, OASIS_DAY_PROFILE, OPEN_WORLD_GAMEPLAY_COLLISION_JSON,
    TEMPLE_GARDENS_PROFILE,
};
use crate::open_world_terrain::{
    open_world_half_size_for_profile, open_world_heightfield_enabled_for_profile,
    open_world_max_x_for_profile, open_world_max_z_for_profile, open_world_min_x_for_profile,
    open_world_min_z_for_profile, open_world_seed, open_world_surface_height_for_profile,
    procedural_open_world_enabled,
};
use serde::Deserialize;
use std::collections::HashMap;
use std::sync::OnceLock;

const GAMEPLAY_COLLISION_JSON: &str = include_str!("gameplay_collision.shared.json");
const OPEN_WORLD_DECOR_MARGIN: f32 = 8.0;

const OPEN_WORLD_TREE_COUNT: usize = 64;
const OPEN_WORLD_TREE_MAX_ATTEMPTS: usize = 2200;
const OPEN_WORLD_TREE_CLEARING_RADIUS: f32 = 32.0;
const OPEN_WORLD_TREE_RADIUS_MIN: f32 = 0.88;
const OPEN_WORLD_TREE_RADIUS_MAX: f32 = 1.48;
const OPEN_WORLD_TREE_MIN_SPACING: f32 = 1.4;
const OPEN_WORLD_TREE_OCCUPANCY_PADDING: f32 = 0.5;
const OPEN_WORLD_TREE_HEIGHT_MIN: f32 = 7.0;
const OPEN_WORLD_TREE_HEIGHT_MAX: f32 = 13.8;

const OPEN_WORLD_ROCK_COUNT: usize = 46;
const OPEN_WORLD_ROCK_MAX_ATTEMPTS: usize = 1800;
const OPEN_WORLD_ROCK_CLEARING_RADIUS: f32 = 20.0;
const OPEN_WORLD_ROCK_RADIUS_MIN: f32 = 0.9;
const OPEN_WORLD_ROCK_RADIUS_MAX: f32 = 1.9;
const OPEN_WORLD_ROCK_HEIGHT_MIN: f32 = 0.72;
const OPEN_WORLD_ROCK_HEIGHT_MAX: f32 = 1.78;
const OPEN_WORLD_ROCK_MIN_SPACING: f32 = 0.9;
const OPEN_WORLD_ROCK_OCCUPANCY_PADDING: f32 = 0.3;
const OPEN_WORLD_TREE_OCCUPIED_RADIUS_MAX: f32 =
    OPEN_WORLD_TREE_RADIUS_MAX + OPEN_WORLD_TREE_OCCUPANCY_PADDING;
const OPEN_WORLD_ROCK_OCCUPIED_RADIUS_MAX: f32 =
    OPEN_WORLD_ROCK_RADIUS_MAX + OPEN_WORLD_ROCK_OCCUPANCY_PADDING;
const OPEN_WORLD_OCCUPANCY_MAX_RADIUS: f32 =
    if OPEN_WORLD_TREE_OCCUPIED_RADIUS_MAX > OPEN_WORLD_ROCK_OCCUPIED_RADIUS_MAX {
        OPEN_WORLD_TREE_OCCUPIED_RADIUS_MAX
    } else {
        OPEN_WORLD_ROCK_OCCUPIED_RADIUS_MAX
    };
const OPEN_WORLD_OCCUPANCY_CELL_SIZE: f32 = 4.0;

const OPEN_WORLD_COLLISION_ITERS: usize = 2;
const OPEN_WORLD_RAYCAST_STEP: f32 = 0.25;
const OPEN_WORLD_RAYCAST_REFINE_ITERS: usize = 7;

const GAMEPLAY_BOX_STEP_UP_HEIGHT: f32 = 0.35;
const WALKABLE_TOP_EPSILON: f32 = 0.05;
#[derive(Clone, Copy, Debug)]
enum ColliderShape2d {
    Circle {
        cx: f32,
        cz: f32,
        radius: f32,
    },
    RadialSlope {
        cx: f32,
        cz: f32,
        radius_bottom: f32,
        radius_top: f32,
    },
}

#[derive(Clone, Copy, Debug)]
struct Collider {
    shape: ColliderShape2d,
    y_min: f32,
    y_max: f32,
    walkable_top: bool,
}

#[derive(Deserialize)]
struct GameplayCollisionLayoutFile {
    #[serde(default)]
    boxes: Vec<GameplayCollisionBoxFile>,
}

#[derive(Deserialize)]
struct GameplayCollisionBoxFile {
    #[serde(default = "default_gameplay_collision_shape")]
    shape: String,
    center: [f32; 3],
    size: [f32; 3],
    rotation_y_deg: f32,
}

#[derive(Clone, Copy, Debug)]
enum GameplayCollisionBox {
    ObbY {
        center_x: f32,
        center_y: f32,
        center_z: f32,
        half_x: f32,
        half_y: f32,
        half_z: f32,
        sin_y: f32,
        cos_y: f32,
    },
    Aabb {
        center_x: f32,
        center_y: f32,
        center_z: f32,
        half_x: f32,
        half_y: f32,
        half_z: f32,
    },
}

fn default_gameplay_collision_shape() -> String {
    "obb_y".to_string()
}

#[allow(dead_code)]
pub fn resolve_world_horizontal_collision_y(
    arena_seed: Option<u64>,
    x: f32,
    z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    resolve_world_horizontal_collision_y_with_layout(
        arena_seed,
        false,
        x,
        z,
        player_radius,
        player_height,
        current_y,
    )
}

pub fn resolve_world_horizontal_collision_y_with_layout(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    x: f32,
    z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    resolve_world_horizontal_collision_y_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        None,
        x,
        z,
        player_radius,
        player_height,
        current_y,
    )
}

#[allow(clippy::too_many_arguments)]
pub fn resolve_world_horizontal_collision_y_with_layout_for_scene(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    open_world_scene_name: Option<&str>,
    x: f32,
    z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    if flat_ground_only {
        return (x, z);
    }

    let profile = open_world_profile_from_name(open_world_scene_name);
    let (out_x, out_z) = if let Some(seed) = arena_seed {
        resolve_arena_horizontal_collision_y(seed, x, z, player_radius, current_y, player_height)
    } else {
        resolve_open_world_horizontal_collision_y(
            profile,
            x,
            z,
            player_radius,
            player_height,
            current_y,
        )
    };

    if arena_seed.is_some() {
        resolve_gameplay_horizontal_collision_y(
            out_x,
            out_z,
            player_radius,
            player_height,
            current_y,
        )
    } else {
        resolve_open_world_gameplay_horizontal_collision_y(
            profile,
            out_x,
            out_z,
            player_radius,
            player_height,
            current_y,
        )
    }
}

#[allow(dead_code)]
pub fn surface_height_for_world_at_y(
    arena_seed: Option<u64>,
    x: f32,
    z: f32,
    current_y: f32,
) -> f32 {
    surface_height_for_world_at_y_with_layout(arena_seed, false, x, z, current_y)
}

pub fn surface_height_for_world_at_y_with_layout(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    x: f32,
    z: f32,
    current_y: f32,
) -> f32 {
    surface_height_for_world_at_y_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        None,
        x,
        z,
        current_y,
    )
}

pub fn surface_height_for_world_at_y_with_layout_for_scene(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    open_world_scene_name: Option<&str>,
    x: f32,
    z: f32,
    current_y: f32,
) -> f32 {
    if flat_ground_only {
        return GROUND_Y;
    }

    if let Some(seed) = arena_seed {
        return surface_height_at_y(seed, x, z, current_y);
    }
    open_world_surface_height_at_y(
        open_world_profile_from_name(open_world_scene_name),
        x,
        z,
        current_y,
    )
}

pub fn resolve_world_spawn_position(
    arena_seed: Option<u64>,
    desired_x: f32,
    desired_z: f32,
    player_radius: f32,
    player_height: f32,
) -> (f32, f32, f32) {
    resolve_world_spawn_position_with_layout(
        arena_seed,
        false,
        desired_x,
        desired_z,
        player_radius,
        player_height,
    )
}

pub fn resolve_world_spawn_position_with_layout(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    desired_x: f32,
    desired_z: f32,
    player_radius: f32,
    player_height: f32,
) -> (f32, f32, f32) {
    resolve_world_spawn_position_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        None,
        desired_x,
        desired_z,
        player_radius,
        player_height,
    )
}

pub fn resolve_world_spawn_position_with_layout_for_scene(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    open_world_scene_name: Option<&str>,
    desired_x: f32,
    desired_z: f32,
    player_radius: f32,
    player_height: f32,
) -> (f32, f32, f32) {
    let mut x = desired_x;
    let mut z = desired_z;
    let open_world_profile = if arena_seed.is_none() && !flat_ground_only {
        Some(open_world_profile_from_name(open_world_scene_name))
    } else {
        None
    };
    let sample_spawn_surface = |x: f32, z: f32| -> f32 {
        if let Some(profile) = open_world_profile {
            let terrain_y = open_world_surface_height_for_profile(profile, x, z);
            return open_world_surface_height_at_y(profile, x, z, terrain_y);
        }

        surface_height_for_world_at_y_with_layout_for_scene(
            arena_seed,
            flat_ground_only,
            open_world_scene_name,
            x,
            z,
            f32::MAX,
        )
    };
    let mut y = sample_spawn_surface(x, z);

    for _ in 0..OPEN_WORLD_COLLISION_ITERS {
        let (resolved_x, resolved_z) = resolve_world_horizontal_collision_y_with_layout_for_scene(
            arena_seed,
            flat_ground_only,
            open_world_scene_name,
            x,
            z,
            player_radius,
            player_height,
            y,
        );
        x = resolved_x;
        z = resolved_z;
        y = sample_spawn_surface(x, z);
    }

    (x, y, z)
}

#[derive(Clone, Copy, Debug)]
pub struct OpenWorldRayHit {
    pub t: f32,
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

#[allow(dead_code)]
pub fn raycast_open_world(
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    max_distance: f32,
    radius: f32,
) -> Option<OpenWorldRayHit> {
    raycast_open_world_for_profile(
        default_open_world_scene_profile(),
        origin_x,
        origin_y,
        origin_z,
        dir_x,
        dir_y,
        dir_z,
        max_distance,
        radius,
    )
}

fn raycast_open_world_for_profile(
    profile: &OpenWorldSceneProfile,
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    max_distance: f32,
    radius: f32,
) -> Option<OpenWorldRayHit> {
    if max_distance <= 0.0 {
        return None;
    }

    let intersects_at = |t: f32| -> bool {
        let x = origin_x + dir_x * t;
        let y = origin_y + dir_y * t;
        let z = origin_z + dir_z * t;
        open_world_point_hits_geometry(profile, x, y, z, radius)
    };

    if intersects_at(0.0) {
        return Some(OpenWorldRayHit {
            t: 0.0,
            x: origin_x,
            y: origin_y,
            z: origin_z,
        });
    }

    let step = OPEN_WORLD_RAYCAST_STEP.max(COLLISION_EPSILON);
    let mut prev_t = 0.0;
    let mut t = step;
    while t <= max_distance + COLLISION_EPSILON {
        let clamped_t = t.min(max_distance);
        if intersects_at(clamped_t) {
            let mut lo = prev_t;
            let mut hi = clamped_t;
            for _ in 0..OPEN_WORLD_RAYCAST_REFINE_ITERS {
                let mid = (lo + hi) * 0.5;
                if intersects_at(mid) {
                    hi = mid;
                } else {
                    lo = mid;
                }
            }

            let hit_t = hi;
            return Some(OpenWorldRayHit {
                t: hit_t,
                x: origin_x + dir_x * hit_t,
                y: origin_y + dir_y * hit_t,
                z: origin_z + dir_z * hit_t,
            });
        }

        prev_t = clamped_t;
        if clamped_t >= max_distance {
            break;
        }
        t += step;
    }

    None
}

#[allow(dead_code)]
pub fn raycast_world_with(
    arena_seed: Option<u64>,
    request: WorldRaycastRequest,
) -> Option<WorldRayHit> {
    raycast_world_with_layout(arena_seed, false, request)
}

pub fn raycast_world_with_layout(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    request: WorldRaycastRequest,
) -> Option<WorldRayHit> {
    raycast_world_with_layout_for_scene(arena_seed, flat_ground_only, None, request)
}

pub fn raycast_world_with_layout_for_scene(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    open_world_scene_name: Option<&str>,
    request: WorldRaycastRequest,
) -> Option<WorldRayHit> {
    if flat_ground_only {
        return None;
    }

    let mut best = if let Some(seed) = arena_seed {
        raycast_arena_world_with(seed, request)
    } else {
        raycast_open_world_for_profile(
            open_world_profile_from_name(open_world_scene_name),
            request.origin_x,
            request.origin_y,
            request.origin_z,
            request.dir_x,
            request.dir_y,
            request.dir_z,
            request.max_distance,
            request.radius,
        )
        .map(|hit| WorldRayHit {
            t: hit.t,
            x: hit.x,
            y: hit.y,
            z: hit.z,
        })
    };

    let gameplay_colliders = if arena_seed.is_some() {
        gameplay_collision_boxes()
    } else {
        open_world_gameplay_collision_boxes(open_world_profile_from_name(open_world_scene_name))
    };

    for collider in gameplay_colliders {
        try_world_gameplay_box_hit(&mut best, request, *collider);
    }

    best
}

fn open_world_profile_from_name(scene_name: Option<&str>) -> &'static OpenWorldSceneProfile {
    scene_name
        .and_then(open_world_scene_profile_for_scene)
        .unwrap_or_else(default_open_world_scene_profile)
}

fn resolve_open_world_horizontal_collision_y(
    profile: &OpenWorldSceneProfile,
    x: f32,
    z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    let mut out_x = x;
    let mut out_z = z;
    let min_x = open_world_min_x_for_profile(profile) + player_radius;
    let max_x = open_world_max_x_for_profile(profile) - player_radius;
    let min_z = open_world_min_z_for_profile(profile) + player_radius;
    let max_z = open_world_max_z_for_profile(profile) - player_radius;
    out_x = out_x.clamp(min_x, max_x);
    out_z = out_z.clamp(min_z, max_z);

    for _ in 0..OPEN_WORLD_COLLISION_ITERS {
        for_each_open_world_collider(profile, |collider| {
            if !collider_overlaps_player_band(collider, current_y, player_height) {
                return;
            }

            match collider.shape {
                ColliderShape2d::Circle { cx, cz, radius } => {
                    (out_x, out_z) =
                        push_out_circle_2d(out_x, out_z, cx, cz, radius + player_radius);
                }
                ColliderShape2d::RadialSlope {
                    cx,
                    cz,
                    radius_bottom,
                    radius_top,
                } => {
                    let dx = out_x - cx;
                    let dz = out_z - cz;
                    let planar_radius = (dx * dx + dz * dz).sqrt();

                    if let Some(surface_y) = slope_surface_height_from_radius(
                        collider.y_min,
                        collider.y_max,
                        radius_bottom,
                        radius_top,
                        planar_radius,
                    ) {
                        // Walkable slopes should be traversable whenever they are within
                        // normal step/snap-up range, otherwise they behave as a wall.
                        if surface_y <= current_y + SURFACE_SNAP_UP {
                            return;
                        }
                    } else {
                        // Outside the slope footprint is never blocked by terrain-like slopes.
                        return;
                    }

                    let block_radius = slope_radius_at_y(
                        collider.y_min,
                        collider.y_max,
                        radius_bottom,
                        radius_top,
                        current_y,
                    );
                    (out_x, out_z) =
                        push_out_circle_2d(out_x, out_z, cx, cz, block_radius + player_radius);
                }
            }
        });

        out_x = out_x.clamp(min_x, max_x);
        out_z = out_z.clamp(min_z, max_z);
    }

    (out_x, out_z)
}

fn resolve_gameplay_horizontal_collision_y(
    x: f32,
    z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    let mut out_x = x;
    let mut out_z = z;

    for _ in 0..OPEN_WORLD_COLLISION_ITERS {
        for collider in gameplay_collision_boxes() {
            if !gameplay_box_overlaps_player_band(*collider, current_y, player_height) {
                continue;
            }

            (out_x, out_z) = match *collider {
                GameplayCollisionBox::ObbY {
                    center_x,
                    center_z,
                    half_x,
                    half_z,
                    sin_y,
                    cos_y,
                    ..
                } => push_out_obb_y_2d(
                    out_x,
                    out_z,
                    center_x,
                    center_z,
                    half_x,
                    half_z,
                    player_radius,
                    sin_y,
                    cos_y,
                ),
                GameplayCollisionBox::Aabb {
                    center_x,
                    center_z,
                    half_x,
                    half_z,
                    ..
                } => push_out_aabb_2d(
                    out_x,
                    out_z,
                    center_x,
                    center_z,
                    half_x,
                    half_z,
                    player_radius,
                ),
            };
        }
    }

    (out_x, out_z)
}

fn resolve_open_world_gameplay_horizontal_collision_y(
    profile: &OpenWorldSceneProfile,
    x: f32,
    z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    let mut out_x = x;
    let mut out_z = z;

    for _ in 0..OPEN_WORLD_COLLISION_ITERS {
        for collider in open_world_gameplay_collision_boxes(profile) {
            if !gameplay_box_overlaps_player_band(*collider, current_y, player_height) {
                continue;
            }
            if gameplay_box_can_step_up(*collider, current_y) {
                continue;
            }

            (out_x, out_z) = match *collider {
                GameplayCollisionBox::ObbY {
                    center_x,
                    center_z,
                    half_x,
                    half_z,
                    sin_y,
                    cos_y,
                    ..
                } => push_out_obb_y_2d(
                    out_x,
                    out_z,
                    center_x,
                    center_z,
                    half_x,
                    half_z,
                    player_radius,
                    sin_y,
                    cos_y,
                ),
                GameplayCollisionBox::Aabb {
                    center_x,
                    center_z,
                    half_x,
                    half_z,
                    ..
                } => push_out_aabb_2d(
                    out_x,
                    out_z,
                    center_x,
                    center_z,
                    half_x,
                    half_z,
                    player_radius,
                ),
            };
        }
    }

    (out_x, out_z)
}

fn open_world_point_hits_geometry(
    profile: &OpenWorldSceneProfile,
    x: f32,
    y: f32,
    z: f32,
    radius: f32,
) -> bool {
    if y <= open_world_surface_height_at_y(profile, x, z, f32::MAX) + radius {
        return true;
    }

    let mut blocked = false;
    for_each_open_world_collider(profile, |collider| {
        if blocked {
            return;
        }
        if y < collider.y_min - radius || y > collider.y_max + radius {
            return;
        }

        let inside = match collider.shape {
            ColliderShape2d::Circle {
                cx,
                cz,
                radius: collider_radius,
            } => {
                let dx = x - cx;
                let dz = z - cz;
                dx * dx + dz * dz <= (collider_radius + radius).powi(2)
            }
            ColliderShape2d::RadialSlope {
                cx,
                cz,
                radius_bottom,
                radius_top,
            } => {
                let sample_y = y.clamp(collider.y_min, collider.y_max);
                let allowed_radius = slope_radius_at_y(
                    collider.y_min,
                    collider.y_max,
                    radius_bottom,
                    radius_top,
                    sample_y,
                ) + radius;
                let dx = x - cx;
                let dz = z - cz;
                dx * dx + dz * dz <= allowed_radius * allowed_radius
            }
        };

        if inside {
            blocked = true;
        }
    });

    blocked
}

fn open_world_surface_height_at_y(
    profile: &OpenWorldSceneProfile,
    x: f32,
    z: f32,
    current_y: f32,
) -> f32 {
    let mut surface = open_world_surface_height_for_profile(profile, x, z);
    let ceiling = current_y + SURFACE_SNAP_UP;
    let gameplay_step_ceiling = current_y + GAMEPLAY_BOX_STEP_UP_HEIGHT;

    for_each_open_world_collider(profile, |collider| {
        if !collider.walkable_top {
            return;
        }
        if collider.y_max > ceiling {
            return;
        }

        let inside = match collider.shape {
            ColliderShape2d::Circle { cx, cz, radius } => {
                let dx = x - cx;
                let dz = z - cz;
                dx * dx + dz * dz <= radius * radius
            }
            ColliderShape2d::RadialSlope {
                cx,
                cz,
                radius_bottom,
                radius_top,
            } => {
                let dx = x - cx;
                let dz = z - cz;
                let radius = (dx * dx + dz * dz).sqrt();
                if let Some(y) = slope_surface_height_from_radius(
                    collider.y_min,
                    collider.y_max,
                    radius_bottom,
                    radius_top,
                    radius,
                ) {
                    if y > surface {
                        surface = y;
                    }
                }
                false
            }
        };

        if inside && collider.y_max > surface {
            surface = collider.y_max;
        }
    });

    for collider in open_world_gameplay_collision_boxes(profile) {
        let top_y = gameplay_box_top_y(*collider);
        if top_y > gameplay_step_ceiling {
            continue;
        }
        if gameplay_box_contains_point_2d(*collider, x, z) && top_y > surface {
            surface = top_y;
        }
    }

    surface
}

fn collider_overlaps_player_band(collider: Collider, foot_y: f32, player_height: f32) -> bool {
    if collider.walkable_top && foot_y >= collider.y_max - WALKABLE_TOP_EPSILON {
        return false;
    }

    let head_y = foot_y + player_height.max(0.1);
    head_y > collider.y_min + COLLISION_EPSILON && foot_y < collider.y_max - COLLISION_EPSILON
}

fn gameplay_box_overlaps_player_band(
    collider: GameplayCollisionBox,
    foot_y: f32,
    player_height: f32,
) -> bool {
    let head_y = foot_y + player_height.max(0.1);
    let (center_y, half_y) = match collider {
        GameplayCollisionBox::ObbY {
            center_y, half_y, ..
        }
        | GameplayCollisionBox::Aabb {
            center_y, half_y, ..
        } => (center_y, half_y),
    };

    head_y > center_y - half_y + COLLISION_EPSILON && foot_y < center_y + half_y - COLLISION_EPSILON
}

fn gameplay_box_can_step_up(collider: GameplayCollisionBox, foot_y: f32) -> bool {
    gameplay_box_top_y(collider) <= foot_y + GAMEPLAY_BOX_STEP_UP_HEIGHT
}

fn gameplay_box_top_y(collider: GameplayCollisionBox) -> f32 {
    match collider {
        GameplayCollisionBox::ObbY {
            center_y, half_y, ..
        }
        | GameplayCollisionBox::Aabb {
            center_y, half_y, ..
        } => center_y + half_y,
    }
}

fn gameplay_box_contains_point_2d(collider: GameplayCollisionBox, x: f32, z: f32) -> bool {
    match collider {
        GameplayCollisionBox::Aabb {
            center_x,
            center_z,
            half_x,
            half_z,
            ..
        } => {
            (x - center_x).abs() <= half_x + COLLISION_EPSILON
                && (z - center_z).abs() <= half_z + COLLISION_EPSILON
        }
        GameplayCollisionBox::ObbY {
            center_x,
            center_z,
            half_x,
            half_z,
            sin_y,
            cos_y,
            ..
        } => {
            let rel_x = x - center_x;
            let rel_z = z - center_z;
            let local_x = rel_x * cos_y - rel_z * sin_y;
            let local_z = rel_x * sin_y + rel_z * cos_y;
            local_x.abs() <= half_x + COLLISION_EPSILON
                && local_z.abs() <= half_z + COLLISION_EPSILON
        }
    }
}

fn for_each_open_world_collider(profile: &OpenWorldSceneProfile, mut emit: impl FnMut(Collider)) {
    for collider in open_world_colliders(profile) {
        emit(*collider);
    }
}

fn open_world_colliders(profile: &OpenWorldSceneProfile) -> &'static [Collider] {
    static OPEN_WORLD_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static ADVENTURE_ISLAND_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static DESERT_DAY_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static DOCKS_DAY_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static GOLDEN_VALLEY_OVERCAST_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static GOLDEN_VALLEY_SUNNY_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static GREAT_HALL_DAY_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static IDOL_DAY_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static TEMPLE_GARDENS_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();

    if profile.scene_name == ADVENTURE_ISLAND_PROFILE.scene_name {
        ADVENTURE_ISLAND_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    } else if profile.scene_name == DESERT_DAY_PROFILE.scene_name {
        DESERT_DAY_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    } else if profile.scene_name == DOCKS_DAY_PROFILE.scene_name {
        DOCKS_DAY_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    } else if profile.scene_name == GOLDEN_VALLEY_OVERCAST_PROFILE.scene_name {
        GOLDEN_VALLEY_OVERCAST_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    } else if profile.scene_name == GOLDEN_VALLEY_SUNNY_PROFILE.scene_name {
        GOLDEN_VALLEY_SUNNY_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    } else if profile.scene_name == GREAT_HALL_DAY_PROFILE.scene_name {
        GREAT_HALL_DAY_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    } else if profile.scene_name == IDOL_DAY_PROFILE.scene_name {
        IDOL_DAY_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    } else if profile.scene_name == TEMPLE_GARDENS_PROFILE.scene_name {
        TEMPLE_GARDENS_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    } else {
        OPEN_WORLD_COLLIDERS
            .get_or_init(|| generate_open_world_colliders(profile))
            .as_slice()
    }
}

fn gameplay_collision_boxes() -> &'static [GameplayCollisionBox] {
    static GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    GAMEPLAY_BOXES
        .get_or_init(load_gameplay_collision_boxes)
        .as_slice()
}

fn open_world_gameplay_collision_boxes(
    profile: &OpenWorldSceneProfile,
) -> &'static [GameplayCollisionBox] {
    static OPEN_WORLD_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static ADVENTURE_ISLAND_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static DESERT_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static DOCKS_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static GOLDEN_VALLEY_OVERCAST_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> =
        OnceLock::new();
    static GOLDEN_VALLEY_SUNNY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> =
        OnceLock::new();
    static GREAT_HALL_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static IDOL_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static TEMPLE_GARDENS_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();

    if profile.scene_name == ADVENTURE_ISLAND_PROFILE.scene_name {
        ADVENTURE_ISLAND_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == DESERT_DAY_PROFILE.scene_name {
        DESERT_DAY_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == DOCKS_DAY_PROFILE.scene_name {
        DOCKS_DAY_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == GOLDEN_VALLEY_OVERCAST_PROFILE.scene_name {
        GOLDEN_VALLEY_OVERCAST_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == GOLDEN_VALLEY_SUNNY_PROFILE.scene_name {
        GOLDEN_VALLEY_SUNNY_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == GREAT_HALL_DAY_PROFILE.scene_name {
        GREAT_HALL_DAY_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == IDOL_DAY_PROFILE.scene_name {
        IDOL_DAY_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == TEMPLE_GARDENS_PROFILE.scene_name {
        TEMPLE_GARDENS_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else {
        OPEN_WORLD_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    }
}

fn load_gameplay_collision_boxes() -> Vec<GameplayCollisionBox> {
    let file: GameplayCollisionLayoutFile = serde_json::from_str(GAMEPLAY_COLLISION_JSON)
        .expect("failed to parse gameplay_collision.shared.json");

    parse_gameplay_collision_boxes(file)
}

fn load_open_world_gameplay_collision_boxes(
    profile: &OpenWorldSceneProfile,
) -> Vec<GameplayCollisionBox> {
    let json = if profile.scene_name == OASIS_DAY_PROFILE.scene_name {
        OPEN_WORLD_GAMEPLAY_COLLISION_JSON
    } else {
        profile.gameplay_collision_json
    };
    let file: GameplayCollisionLayoutFile =
        serde_json::from_str(json).expect("failed to parse open-world gameplay collision JSON");

    parse_gameplay_collision_boxes(file)
}

fn parse_gameplay_collision_boxes(file: GameplayCollisionLayoutFile) -> Vec<GameplayCollisionBox> {
    file.boxes
        .into_iter()
        .map(|box_file| {
            let center_x = box_file.center[0];
            let center_y = box_file.center[1];
            let center_z = box_file.center[2];
            let half_x = box_file.size[0].abs() * 0.5;
            let half_y = box_file.size[1].abs() * 0.5;
            let half_z = box_file.size[2].abs() * 0.5;

            match box_file.shape.as_str() {
                "aabb" => GameplayCollisionBox::Aabb {
                    center_x,
                    center_y,
                    center_z,
                    half_x,
                    half_y,
                    half_z,
                },
                _ => {
                    let yaw = box_file.rotation_y_deg.to_radians();
                    GameplayCollisionBox::ObbY {
                        center_x,
                        center_y,
                        center_z,
                        half_x,
                        half_y,
                        half_z,
                        sin_y: yaw.sin(),
                        cos_y: yaw.cos(),
                    }
                }
            }
        })
        .collect()
}

fn generate_open_world_colliders(profile: &OpenWorldSceneProfile) -> Vec<Collider> {
    if open_world_heightfield_enabled_for_profile(profile) {
        return Vec::new();
    }
    if !profile.use_procedural_fallback_colliders {
        return Vec::new();
    }

    let mut rng = OpenWorldRng::new(open_world_seed());
    let mut occupied = OpenWorldOccupancy::new(OPEN_WORLD_TREE_COUNT + OPEN_WORLD_ROCK_COUNT);
    let mut colliders = Vec::with_capacity(OPEN_WORLD_TREE_COUNT + OPEN_WORLD_ROCK_COUNT);
    let mut push_collider = |collider: Collider| {
        colliders.push(collider);
    };
    emit_open_world_tree_colliders(profile, &mut push_collider, &mut rng, &mut occupied);
    emit_open_world_rock_colliders(profile, &mut push_collider, &mut rng, &mut occupied);
    colliders
}

fn emit_open_world_tree_colliders(
    profile: &OpenWorldSceneProfile,
    emit: &mut impl FnMut(Collider),
    rng: &mut OpenWorldRng,
    occupied: &mut OpenWorldOccupancy,
) {
    let mut placed = 0_usize;
    let mut attempts = 0_usize;

    while placed < OPEN_WORLD_TREE_COUNT && attempts < OPEN_WORLD_TREE_MAX_ATTEMPTS {
        attempts += 1;
        let half_size = open_world_half_size_for_profile(profile);
        let x = rng.random_range(
            -half_size + OPEN_WORLD_DECOR_MARGIN,
            half_size - OPEN_WORLD_DECOR_MARGIN,
        );
        let z = rng.random_range(
            -half_size + OPEN_WORLD_DECOR_MARGIN,
            half_size - OPEN_WORLD_DECOR_MARGIN,
        );
        let tree_radius = rng.random_range(OPEN_WORLD_TREE_RADIUS_MIN, OPEN_WORLD_TREE_RADIUS_MAX);

        if (x * x + z * z).sqrt() < OPEN_WORLD_TREE_CLEARING_RADIUS {
            continue;
        }
        if !occupied.is_free(x, z, tree_radius + OPEN_WORLD_TREE_MIN_SPACING) {
            continue;
        }

        let normalized = inverse_lerp(
            OPEN_WORLD_TREE_RADIUS_MIN,
            OPEN_WORLD_TREE_RADIUS_MAX,
            tree_radius,
        );
        let tree_height = OPEN_WORLD_TREE_HEIGHT_MIN
            + (OPEN_WORLD_TREE_HEIGHT_MAX - OPEN_WORLD_TREE_HEIGHT_MIN) * normalized;
        let base_y = if procedural_open_world_enabled() {
            open_world_surface_height_for_profile(profile, x, z)
        } else {
            profile.ground_y
        };

        emit(Collider {
            shape: ColliderShape2d::Circle {
                cx: x,
                cz: z,
                radius: tree_radius,
            },
            y_min: base_y,
            y_max: base_y + tree_height,
            walkable_top: false,
        });
        occupied.insert(OpenWorldDisc {
            x,
            z,
            radius: tree_radius + OPEN_WORLD_TREE_OCCUPANCY_PADDING,
        });
        placed += 1;
    }

    if placed < OPEN_WORLD_TREE_COUNT {
        log::warn!(
            "[WORLD] Placed {}/{} open-world trees after {} attempts",
            placed,
            OPEN_WORLD_TREE_COUNT,
            attempts
        );
    }
}

fn emit_open_world_rock_colliders(
    profile: &OpenWorldSceneProfile,
    emit: &mut impl FnMut(Collider),
    rng: &mut OpenWorldRng,
    occupied: &mut OpenWorldOccupancy,
) {
    let mut placed = 0_usize;
    let mut attempts = 0_usize;

    while placed < OPEN_WORLD_ROCK_COUNT && attempts < OPEN_WORLD_ROCK_MAX_ATTEMPTS {
        attempts += 1;
        let half_size = open_world_half_size_for_profile(profile);
        let x = rng.random_range(
            -half_size + OPEN_WORLD_DECOR_MARGIN,
            half_size - OPEN_WORLD_DECOR_MARGIN,
        );
        let z = rng.random_range(
            -half_size + OPEN_WORLD_DECOR_MARGIN,
            half_size - OPEN_WORLD_DECOR_MARGIN,
        );
        let rock_radius = rng.random_range(OPEN_WORLD_ROCK_RADIUS_MIN, OPEN_WORLD_ROCK_RADIUS_MAX);
        let rock_height = rng.random_range(OPEN_WORLD_ROCK_HEIGHT_MIN, OPEN_WORLD_ROCK_HEIGHT_MAX);

        if (x * x + z * z).sqrt() < OPEN_WORLD_ROCK_CLEARING_RADIUS {
            continue;
        }
        if !occupied.is_free(x, z, rock_radius + OPEN_WORLD_ROCK_MIN_SPACING) {
            continue;
        }
        let base_y = if procedural_open_world_enabled() {
            open_world_surface_height_for_profile(profile, x, z)
        } else {
            profile.ground_y
        };

        emit(Collider {
            shape: ColliderShape2d::RadialSlope {
                cx: x,
                cz: z,
                radius_bottom: rock_radius,
                radius_top: (rock_radius * 0.24).max(0.18),
            },
            y_min: base_y,
            y_max: base_y + rock_height,
            walkable_top: true,
        });
        occupied.insert(OpenWorldDisc {
            x,
            z,
            radius: rock_radius + OPEN_WORLD_ROCK_OCCUPANCY_PADDING,
        });
        placed += 1;
    }

    if placed < OPEN_WORLD_ROCK_COUNT {
        log::warn!(
            "[WORLD] Placed {}/{} open-world rocks after {} attempts",
            placed,
            OPEN_WORLD_ROCK_COUNT,
            attempts
        );
    }
}

fn inverse_lerp(min: f32, max: f32, value: f32) -> f32 {
    if (max - min).abs() <= COLLISION_EPSILON {
        return 0.0;
    }
    ((value - min) / (max - min)).clamp(0.0, 1.0)
}

#[derive(Clone, Copy, Debug)]
struct OpenWorldDisc {
    x: f32,
    z: f32,
    radius: f32,
}

struct OpenWorldOccupancy {
    discs: Vec<OpenWorldDisc>,
    buckets: HashMap<(i32, i32), Vec<usize>>,
}

impl OpenWorldOccupancy {
    fn new(capacity: usize) -> Self {
        Self {
            discs: Vec::with_capacity(capacity),
            buckets: HashMap::with_capacity(capacity * 2),
        }
    }

    fn insert(&mut self, disc: OpenWorldDisc) {
        let index = self.discs.len();
        let cell = grid_cell(disc.x, disc.z);
        self.discs.push(disc);
        self.buckets.entry(cell).or_default().push(index);
    }

    fn is_free(&self, x: f32, z: f32, radius: f32) -> bool {
        let (cell_x, cell_z) = grid_cell(x, z);
        let search_radius = radius + OPEN_WORLD_OCCUPANCY_MAX_RADIUS;
        let cell_steps = (search_radius / OPEN_WORLD_OCCUPANCY_CELL_SIZE).ceil() as i32;

        for cx in (cell_x - cell_steps)..=(cell_x + cell_steps) {
            for cz in (cell_z - cell_steps)..=(cell_z + cell_steps) {
                let Some(indices) = self.buckets.get(&(cx, cz)) else {
                    continue;
                };
                for index in indices {
                    let disc = &self.discs[*index];
                    let dx = x - disc.x;
                    let dz = z - disc.z;
                    let min_distance = radius + disc.radius;
                    if dx * dx + dz * dz < min_distance * min_distance {
                        return false;
                    }
                }
            }
        }

        true
    }
}

fn grid_cell(x: f32, z: f32) -> (i32, i32) {
    (
        (x / OPEN_WORLD_OCCUPANCY_CELL_SIZE).floor() as i32,
        (z / OPEN_WORLD_OCCUPANCY_CELL_SIZE).floor() as i32,
    )
}

struct OpenWorldRng {
    state: u32,
}

impl OpenWorldRng {
    fn new(seed: u32) -> Self {
        Self { state: seed }
    }

    fn next_f32(&mut self) -> f32 {
        self.state = self
            .state
            .wrapping_mul(1_664_525)
            .wrapping_add(1_013_904_223);
        (self.state as f64 / 4_294_967_296.0) as f32
    }

    fn random_range(&mut self, min: f32, max: f32) -> f32 {
        min + (max - min) * self.next_f32()
    }
}

fn slope_radius_at_y(y_min: f32, y_max: f32, radius_bottom: f32, radius_top: f32, y: f32) -> f32 {
    let height = (y_max - y_min).abs();
    if height <= COLLISION_EPSILON {
        return radius_bottom.max(radius_top);
    }
    if y <= y_min {
        return radius_bottom;
    }
    if y >= y_max {
        return radius_top;
    }
    let t = (y - y_min) / (y_max - y_min);
    radius_bottom + (radius_top - radius_bottom) * t
}

fn slope_surface_height_from_radius(
    y_min: f32,
    y_max: f32,
    radius_bottom: f32,
    radius_top: f32,
    radius: f32,
) -> Option<f32> {
    let radial_delta = radius_top - radius_bottom;
    if radial_delta.abs() <= COLLISION_EPSILON {
        if radius <= radius_top {
            return Some(y_max);
        }
        return None;
    }

    if radial_delta < 0.0 {
        if radius > radius_bottom {
            return None;
        }
        if radius <= radius_top {
            return Some(y_max);
        }
        let t = (radius_bottom - radius) / (radius_bottom - radius_top);
        return Some(y_min + (y_max - y_min) * t);
    }

    if radius > radius_top {
        return None;
    }
    if radius <= radius_bottom {
        return Some(y_max);
    }
    let t = (radius - radius_bottom) / (radius_top - radius_bottom);
    Some(y_min + (y_max - y_min) * t)
}

fn push_out_circle_2d(x: f32, z: f32, cx: f32, cz: f32, radius: f32) -> (f32, f32) {
    let mut dx = x - cx;
    let mut dz = z - cz;
    let dist_sq = dx * dx + dz * dz;
    let radius_sq = radius * radius;
    if dist_sq >= radius_sq {
        return (x, z);
    }

    if dist_sq <= COLLISION_EPSILON {
        // Degenerate overlap (exact center): use a stable pseudo-random angle
        // derived from collider position to avoid biasing all exits toward +X.
        const HASH_A: u64 = 0x9e37_79b9_7f4a_7c15;
        const HASH_B: u64 = 0xc2b2_ae3d_27d4_eb4f;
        let hash = u64::from(cx.to_bits())
            .wrapping_mul(HASH_A)
            .wrapping_add(u64::from(cz.to_bits()).wrapping_mul(HASH_B))
            .max(1);
        let angle = ((hash % 65_536) as f32 / 65_536.0) * std::f32::consts::TAU;
        dx = angle.cos() * radius;
        dz = angle.sin() * radius;
    }

    let dist = (dx * dx + dz * dz).sqrt().max(COLLISION_EPSILON);
    let push = (radius - dist).max(0.0);
    let nx = dx / dist;
    let nz = dz / dist;
    (x + nx * push, z + nz * push)
}

fn push_out_obb_y_2d(
    x: f32,
    z: f32,
    cx: f32,
    cz: f32,
    half_x: f32,
    half_z: f32,
    padding: f32,
    sin_y: f32,
    cos_y: f32,
) -> (f32, f32) {
    let rel_x = x - cx;
    let rel_z = z - cz;
    let local_x = rel_x * cos_y - rel_z * sin_y;
    let local_z = rel_x * sin_y + rel_z * cos_y;

    let (pushed_local_x, pushed_local_z) =
        push_out_aabb_2d(local_x, local_z, 0.0, 0.0, half_x, half_z, padding);

    let world_x = pushed_local_x * cos_y + pushed_local_z * sin_y + cx;
    let world_z = -pushed_local_x * sin_y + pushed_local_z * cos_y + cz;
    (world_x, world_z)
}

fn push_out_aabb_2d(
    x: f32,
    z: f32,
    cx: f32,
    cz: f32,
    half_x: f32,
    half_z: f32,
    padding: f32,
) -> (f32, f32) {
    let expanded_half_x = half_x + padding;
    let expanded_half_z = half_z + padding;

    let dx = x - cx;
    let dz = z - cz;
    let abs_dx = dx.abs();
    let abs_dz = dz.abs();

    if abs_dx >= expanded_half_x || abs_dz >= expanded_half_z {
        return (x, z);
    }

    let pen_x = expanded_half_x - abs_dx;
    let pen_z = expanded_half_z - abs_dz;

    if pen_x < pen_z {
        let sign = if dx >= 0.0 { 1.0 } else { -1.0 };
        (x + sign * pen_x, z)
    } else {
        let sign = if dz >= 0.0 { 1.0 } else { -1.0 };
        (x, z + sign * pen_z)
    }
}

fn try_world_gameplay_box_hit(
    best: &mut Option<WorldRayHit>,
    request: WorldRaycastRequest,
    collider: GameplayCollisionBox,
) {
    let t = match collider {
        GameplayCollisionBox::ObbY {
            center_x,
            center_y,
            center_z,
            half_x,
            half_y,
            half_z,
            sin_y,
            cos_y,
        } => {
            let rel_origin_x = request.origin_x - center_x;
            let rel_origin_z = request.origin_z - center_z;
            let local_origin_x = rel_origin_x * cos_y - rel_origin_z * sin_y;
            let local_origin_z = rel_origin_x * sin_y + rel_origin_z * cos_y;
            let local_origin_y = request.origin_y - center_y;

            let local_dir_x = request.dir_x * cos_y - request.dir_z * sin_y;
            let local_dir_z = request.dir_x * sin_y + request.dir_z * cos_y;

            raycast_centered_aabb(
                local_origin_x,
                local_origin_y,
                local_origin_z,
                local_dir_x,
                request.dir_y,
                local_dir_z,
                request.max_distance,
                half_x + request.radius,
                half_y + request.radius,
                half_z + request.radius,
            )
        }
        GameplayCollisionBox::Aabb {
            center_x,
            center_y,
            center_z,
            half_x,
            half_y,
            half_z,
        } => raycast_centered_aabb(
            request.origin_x - center_x,
            request.origin_y - center_y,
            request.origin_z - center_z,
            request.dir_x,
            request.dir_y,
            request.dir_z,
            request.max_distance,
            half_x + request.radius,
            half_y + request.radius,
            half_z + request.radius,
        ),
    };

    let Some(t) = t else {
        return;
    };

    let hit = WorldRayHit {
        t,
        x: request.origin_x + request.dir_x * t,
        y: request.origin_y + request.dir_y * t,
        z: request.origin_z + request.dir_z * t,
    };

    if best.is_none_or(|existing| hit.t < existing.t) {
        *best = Some(hit);
    }
}

fn raycast_centered_aabb(
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    max_distance: f32,
    half_x: f32,
    half_y: f32,
    half_z: f32,
) -> Option<f32> {
    let mut t_min = 0.0_f32;
    let mut t_max = max_distance;

    for (origin, dir, half_extent) in [
        (origin_x, dir_x, half_x),
        (origin_y, dir_y, half_y),
        (origin_z, dir_z, half_z),
    ] {
        if dir.abs() <= COLLISION_EPSILON {
            if origin.abs() > half_extent {
                return None;
            }
            continue;
        }

        let inv_dir = 1.0 / dir;
        let mut t1 = (-half_extent - origin) * inv_dir;
        let mut t2 = (half_extent - origin) * inv_dir;
        if t1 > t2 {
            std::mem::swap(&mut t1, &mut t2);
        }

        t_min = t_min.max(t1);
        t_max = t_max.min(t2);
        if t_min > t_max {
            return None;
        }
    }

    if t_min > max_distance {
        return None;
    }

    Some(t_min.max(0.0))
}

#[cfg(test)]
mod tests {
    use super::resolve_world_spawn_position_with_layout_for_scene;
    use crate::open_world_scene::{
        DOCKS_DAY_PROFILE, GREAT_HALL_DAY_PROFILE, IDOL_DAY_PROFILE, TEMPLE_GARDENS_PROFILE,
    };

    const TEST_PLAYER_RADIUS: f32 = 0.45;
    const TEST_PLAYER_HEIGHT: f32 = 1.8;

    #[test]
    fn open_world_spawn_resolution_ignores_high_gameplay_box_tops() {
        let (_, docks_y, _) = resolve_world_spawn_position_with_layout_for_scene(
            None,
            false,
            Some(DOCKS_DAY_PROFILE.scene_name),
            DOCKS_DAY_PROFILE.spawn_x,
            DOCKS_DAY_PROFILE.spawn_z,
            TEST_PLAYER_RADIUS,
            TEST_PLAYER_HEIGHT,
        );
        let (_, idol_y, _) = resolve_world_spawn_position_with_layout_for_scene(
            None,
            false,
            Some(IDOL_DAY_PROFILE.scene_name),
            IDOL_DAY_PROFILE.spawn_x,
            IDOL_DAY_PROFILE.spawn_z,
            TEST_PLAYER_RADIUS,
            TEST_PLAYER_HEIGHT,
        );
        let (_, great_hall_y, _) = resolve_world_spawn_position_with_layout_for_scene(
            None,
            false,
            Some(GREAT_HALL_DAY_PROFILE.scene_name),
            GREAT_HALL_DAY_PROFILE.spawn_x,
            GREAT_HALL_DAY_PROFILE.spawn_z,
            TEST_PLAYER_RADIUS,
            TEST_PLAYER_HEIGHT,
        );
        let (_, temple_y, _) = resolve_world_spawn_position_with_layout_for_scene(
            None,
            false,
            Some(TEMPLE_GARDENS_PROFILE.scene_name),
            TEMPLE_GARDENS_PROFILE.spawn_x,
            TEMPLE_GARDENS_PROFILE.spawn_z,
            TEST_PLAYER_RADIUS,
            TEST_PLAYER_HEIGHT,
        );

        assert!(docks_y < 60.0, "Docks spawn resolved too high: {docks_y}");
        assert!(idol_y < 75.0, "Idol spawn resolved too high: {idol_y}");
        assert!(
            great_hall_y < 0.0,
            "Great Hall spawn resolved too high: {great_hall_y}"
        );
        assert!(
            temple_y < 12.0,
            "Temple spawn resolved too high: {temple_y}"
        );
    }
}

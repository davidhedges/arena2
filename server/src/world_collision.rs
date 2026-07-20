use crate::arena::{
    raycast_arena_world_with, resolve_arena_horizontal_collision_y, surface_height_at_y,
    WorldRayHit, WorldRaycastRequest, COLLISION_EPSILON, SURFACE_SNAP_UP,
};
use crate::movement::GROUND_Y;
use crate::open_world_scene::{
    default_open_world_scene_profile, open_world_scene_profile_for_scene, OpenWorldSceneProfile,
    ADVENTURE_ISLAND_PROFILE, DESERT_DAY_PROFILE, DOCKS_DAY_PROFILE, GIANT_SKELETON_PROFILE,
    GOLDEN_VALLEY_OVERCAST_PROFILE, GOLDEN_VALLEY_SUNNY_PROFILE, GREAT_HALL_DAY_PROFILE,
    IDOL_DAY_PROFILE, OASIS_DAY_PROFILE, OPEN_WORLD_GAMEPLAY_COLLISION_JSON,
    OPEN_WORLD_GAMEPLAY_QUERY_COLLISION_JSON, OPEN_WORLD_SCENE_PROFILES, RANDOM_DUNGEON_PROFILE,
    TEMPLE_GARDENS_PROFILE,
};
use crate::open_world_terrain::{
    open_world_half_size_for_profile, open_world_heightfield_enabled_for_profile,
    open_world_max_x_for_profile, open_world_max_z_for_profile, open_world_min_x_for_profile,
    open_world_min_z_for_profile, open_world_seed, open_world_surface_height_for_profile,
    procedural_open_world_enabled,
};
use serde::Deserialize;
use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::{
    atomic::{AtomicU32, Ordering},
    OnceLock,
};

const GAMEPLAY_COLLISION_JSON: &str = include_str!("gameplay_collision.shared.json");
const GAMEPLAY_QUERY_COLLISION_JSON: &str = include_str!("gameplay_query_collision.shared.json");
const MAX_QUERY_MESH_TRIANGLES_PER_COLLIDER: usize = 4096;
const MAX_QUERY_MESH_TRIANGLES_PER_SCENE: usize = 200000;
const QUERY_MESH_DEGENERATE_TRIANGLE_AREA_SQUARED_EPSILON: f32 = 1.0e-12;
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
const GAMEPLAY_MESH_STEP_UP_HEIGHT: f32 = 0.65;
const GAMEPLAY_MESH_GROUND_MIN_NORMAL_Y: f32 = 0.35;
const OPEN_WORLD_RAYCAST_STEP: f32 = 0.25;
const OPEN_WORLD_RAYCAST_REFINE_ITERS: usize = 7;

const GAMEPLAY_BOX_STEP_UP_HEIGHT: f32 = 0.35;
const WALKABLE_TOP_EPSILON: f32 = 0.05;
const GAMEPLAY_BROADPHASE_MIN_CELL_SIZE: f32 = 2.0;
const GAMEPLAY_BROADPHASE_MAX_CELL_SIZE: f32 = 16.0;
const GAMEPLAY_BROADPHASE_FALLBACK_CELL_COUNT: i64 = 256;
const GAMEPLAY_BROADPHASE_FALLBACK_CANDIDATE_DIVISOR: usize = 4;
const QUERY_MESH_BVH_LEAF_TRIANGLE_COUNT: usize = 4;
const MOVEMENT_BLOCKER_LOG_LIMIT: u32 = 100;

static MOVEMENT_BLOCKER_LOG_COUNT: AtomicU32 = AtomicU32::new(0);

thread_local! {
    static GAMEPLAY_BROADPHASE_CANDIDATE_SCRATCH: RefCell<Vec<usize>> = RefCell::new(Vec::new());
}
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
    #[serde(default)]
    mesh_geometries: Vec<GameplayQueryMeshGeometryFile>,
    #[serde(default)]
    mesh_instances: Vec<GameplayQueryMeshInstanceFile>,
}

#[derive(Deserialize)]
struct GameplayCollisionBoxFile {
    #[serde(default = "default_gameplay_collision_shape")]
    shape: String,
    center: [f32; 3],
    size: [f32; 3],
    #[serde(default)]
    rotation: Vec<f32>,
    rotation_y_deg: f32,
}

#[derive(Deserialize)]
struct GameplayQueryMeshGeometryFile {
    id: String,
    source: String,
    vertex_count: usize,
    triangle_count: usize,
    vertices: Vec<f32>,
    indices: Vec<usize>,
}

#[derive(Deserialize)]
struct GameplayQueryMeshInstanceFile {
    name: String,
    geometry_id: String,
    transform: Vec<f32>,
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
    ObbXyz {
        center_x: f32,
        center_y: f32,
        center_z: f32,
        half_x: f32,
        half_y: f32,
        half_z: f32,
        axis_x_x: f32,
        axis_x_y: f32,
        axis_x_z: f32,
        axis_y_x: f32,
        axis_y_y: f32,
        axis_y_z: f32,
        axis_z_x: f32,
        axis_z_y: f32,
        axis_z_z: f32,
    },
}

#[derive(Clone, Copy, Debug)]
struct Aabb3 {
    min_x: f32,
    min_y: f32,
    min_z: f32,
    max_x: f32,
    max_y: f32,
    max_z: f32,
}

#[derive(Debug)]
struct GameplayQueryMeshGeometry {
    #[allow(dead_code)]
    id: String,
    #[allow(dead_code)]
    source: String,
    vertices: Vec<[f32; 3]>,
    indices: Vec<usize>,
    #[allow(dead_code)]
    local_bounds: Aabb3,
    bvh: GameplayQueryMeshBvh,
}

#[derive(Debug)]
struct GameplayQueryMeshBvh {
    nodes: Vec<GameplayQueryMeshBvhNode>,
    triangle_indices: Vec<usize>,
}

#[derive(Clone, Copy, Debug)]
struct GameplayQueryMeshBvhNode {
    bounds: Aabb3,
    left: usize,
    right: usize,
    first_triangle: usize,
    triangle_count: usize,
}

#[derive(Debug)]
struct GameplayQueryMeshInstance {
    #[allow(dead_code)]
    name: String,
    geometry_index: usize,
    transform: [f32; 16],
    bounds: Aabb3,
}

#[derive(Debug, Default)]
struct GameplayQueryMeshSet {
    geometries: Vec<GameplayQueryMeshGeometry>,
    instances: Vec<GameplayQueryMeshInstance>,
}

#[derive(Debug)]
struct GameplayMovementMeshHull {
    #[allow(dead_code)]
    name: String,
    y_min: f32,
    y_max: f32,
    footprint: Vec<[f32; 2]>,
    triangle: [[f32; 3]; 3],
    ground_normal_y_abs: f32,
    #[allow(dead_code)]
    bounds: Aabb3,
}

#[derive(Debug)]
struct GameplayBoxBroadphase {
    cell_size: f32,
    cells: HashMap<(i32, i32, i32), Vec<usize>>,
    collider_count: usize,
    index_entries: usize,
    max_cell_occupancy: usize,
    max_cells_per_collider: usize,
    unindexed_collider_count: usize,
}

#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct WorldCollisionPreloadSummary {
    pub scene_count: u32,
    pub arena_gameplay_boxes: u32,
    #[allow(dead_code)]
    pub arena_gameplay_mesh_hulls: u32,
    pub arena_query_boxes: u32,
    pub arena_query_mesh_geometries: u32,
    pub arena_query_mesh_instances: u32,
    pub arena_query_mesh_triangles: u32,
    pub open_world_gameplay_boxes: u32,
    #[allow(dead_code)]
    pub open_world_gameplay_mesh_hulls: u32,
    pub open_world_query_boxes: u32,
    pub open_world_query_mesh_geometries: u32,
    pub open_world_query_mesh_instances: u32,
    pub open_world_query_mesh_triangles: u32,
    pub query_mesh_bvh_nodes: u32,
    pub broadphase_cells: u32,
    pub broadphase_index_entries: u32,
    pub broadphase_max_cell_occupancy: u32,
    pub broadphase_max_cells_per_collider: u32,
    pub broadphase_unindexed_colliders: u32,
    pub generated_open_world_colliders: u32,
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

#[allow(clippy::too_many_arguments)]
pub fn resolve_world_horizontal_sweep_collision_y_with_layout_for_scene(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    open_world_scene_name: Option<&str>,
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    if flat_ground_only {
        return (target_x, target_z);
    }

    let profile = open_world_profile_from_name(open_world_scene_name);
    let (out_x, out_z) = if let Some(seed) = arena_seed {
        resolve_arena_horizontal_collision_y(
            seed,
            target_x,
            target_z,
            player_radius,
            current_y,
            player_height,
        )
    } else {
        resolve_open_world_horizontal_collision_y(
            profile,
            target_x,
            target_z,
            player_radius,
            player_height,
            current_y,
        )
    };

    if arena_seed.is_some() {
        resolve_gameplay_horizontal_sweep_collision_y(
            start_x,
            start_z,
            out_x,
            out_z,
            player_radius,
            player_height,
            current_y,
        )
    } else {
        resolve_open_world_gameplay_horizontal_sweep_collision_y(
            profile,
            start_x,
            start_z,
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

#[derive(Clone, Copy, Debug, Default)]
pub struct WorldRaycastStats {
    pub raycast_queries: u32,
    pub world_gameplay_broadphase_candidates: u32,
    pub world_gameplay_narrowphase_tests: u32,
    pub world_gameplay_full_scan_fallbacks: u32,
    pub world_query_mesh_broadphase_candidates: u32,
    pub world_query_mesh_bvh_node_tests: u32,
    pub world_query_mesh_triangles_tested: u32,
    pub world_query_mesh_full_scan_fallbacks: u32,
    pub open_world_geometry_point_checks: u32,
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
    raycast_open_world_for_profile_with_stats(
        default_open_world_scene_profile(),
        origin_x,
        origin_y,
        origin_z,
        dir_x,
        dir_y,
        dir_z,
        max_distance,
        radius,
        None,
    )
}

fn raycast_open_world_for_profile_with_stats(
    profile: &OpenWorldSceneProfile,
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    max_distance: f32,
    radius: f32,
    mut stats: Option<&mut WorldRaycastStats>,
) -> Option<OpenWorldRayHit> {
    if max_distance <= 0.0 {
        return None;
    }

    let mut intersects_at = |t: f32| -> bool {
        if let Some(stats) = stats.as_deref_mut() {
            stats.open_world_geometry_point_checks =
                stats.open_world_geometry_point_checks.saturating_add(1);
        }
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
    raycast_world_with_layout_for_scene_with_stats(
        arena_seed,
        flat_ground_only,
        open_world_scene_name,
        request,
        None,
    )
}

pub fn raycast_world_with_layout_for_scene_with_stats(
    arena_seed: Option<u64>,
    flat_ground_only: bool,
    open_world_scene_name: Option<&str>,
    request: WorldRaycastRequest,
    mut stats: Option<&mut WorldRaycastStats>,
) -> Option<WorldRayHit> {
    if let Some(stats) = stats.as_deref_mut() {
        stats.raycast_queries = stats.raycast_queries.saturating_add(1);
    }
    if flat_ground_only {
        return None;
    }

    let mut best = if let Some(seed) = arena_seed {
        raycast_arena_world_with(seed, request)
    } else {
        raycast_open_world_for_profile_with_stats(
            open_world_profile_from_name(open_world_scene_name),
            request.origin_x,
            request.origin_y,
            request.origin_z,
            request.dir_x,
            request.dir_y,
            request.dir_z,
            request.max_distance,
            request.radius,
            stats.as_deref_mut(),
        )
        .map(|hit| WorldRayHit {
            t: hit.t,
            x: hit.x,
            y: hit.y,
            z: hit.z,
        })
    };

    // RULING (S4 LOS unification, decided by owner 2026-07-04): query
    // raycasts (line of sight + projectile impact) test the AUTHORED query
    // geometry only — terrain, arena layout, query boxes, query meshes.
    // Fat MOVEMENT boxes never block sight or projectiles: they are authored
    // oversized to keep capsules out (a tree's 0.8 m movement box vs its
    // 0.2 m LOS box), and testing them here made props block sight lines
    // their visuals do not. Props that should block sight author
    // ArenaGameplayQueryCollision; seeded arenas block via their layout
    // raycast plus whatever query geometry gets authored for them.
    if arena_seed.is_some() {
        raycast_gameplay_collision_boxes(
            &mut best,
            request,
            gameplay_query_collision_boxes(),
            gameplay_query_collision_broadphase(),
            stats.as_deref_mut(),
        );
        raycast_gameplay_query_meshes(
            &mut best,
            request,
            gameplay_query_meshes(),
            gameplay_query_mesh_broadphase(),
            stats.as_deref_mut(),
        );
    } else {
        let profile = open_world_profile_from_name(open_world_scene_name);
        raycast_gameplay_collision_boxes(
            &mut best,
            request,
            open_world_gameplay_query_collision_boxes(profile),
            open_world_gameplay_query_collision_broadphase(profile),
            stats.as_deref_mut(),
        );
        raycast_gameplay_query_meshes(
            &mut best,
            request,
            open_world_gameplay_query_meshes(profile),
            open_world_gameplay_query_mesh_broadphase(profile),
            stats.as_deref_mut(),
        );
    }

    best
}

fn open_world_profile_from_name(scene_name: Option<&str>) -> &'static OpenWorldSceneProfile {
    scene_name
        .and_then(open_world_scene_profile_for_scene)
        .unwrap_or_else(default_open_world_scene_profile)
}

pub(crate) fn preload_world_collision_data() -> WorldCollisionPreloadSummary {
    let arena_gameplay_boxes = gameplay_collision_boxes().len();
    let arena_gameplay_mesh_hulls = gameplay_movement_mesh_hulls().len();
    let arena_query_boxes = gameplay_query_collision_boxes().len();
    let arena_query_meshes = gameplay_query_meshes();
    let arena_broadphase = gameplay_collision_broadphase();
    let arena_query_broadphase = gameplay_query_collision_broadphase();
    let arena_query_mesh_broadphase = gameplay_query_mesh_broadphase();
    let mut summary = WorldCollisionPreloadSummary {
        scene_count: OPEN_WORLD_SCENE_PROFILES.len().min(u32::MAX as usize) as u32,
        arena_gameplay_boxes: arena_gameplay_boxes.min(u32::MAX as usize) as u32,
        arena_gameplay_mesh_hulls: arena_gameplay_mesh_hulls.min(u32::MAX as usize) as u32,
        arena_query_boxes: arena_query_boxes.min(u32::MAX as usize) as u32,
        arena_query_mesh_geometries: arena_query_meshes.geometries.len().min(u32::MAX as usize)
            as u32,
        arena_query_mesh_instances: arena_query_meshes.instances.len().min(u32::MAX as usize)
            as u32,
        arena_query_mesh_triangles: query_mesh_triangle_count(arena_query_meshes),
        query_mesh_bvh_nodes: query_mesh_bvh_node_count(arena_query_meshes),
        ..Default::default()
    };
    accumulate_broadphase_preload_summary(&mut summary, arena_broadphase);
    accumulate_broadphase_preload_summary(&mut summary, arena_query_broadphase);
    accumulate_broadphase_preload_summary(&mut summary, arena_query_mesh_broadphase);

    for profile in OPEN_WORLD_SCENE_PROFILES {
        let generated_colliders = open_world_colliders(profile).len();
        let gameplay_boxes = open_world_gameplay_collision_boxes(profile).len();
        let gameplay_mesh_hulls = open_world_gameplay_movement_mesh_hulls(profile).len();
        let query_boxes = open_world_gameplay_query_collision_boxes(profile).len();
        let query_meshes = open_world_gameplay_query_meshes(profile);
        let broadphase = open_world_gameplay_collision_broadphase(profile);
        let query_broadphase = open_world_gameplay_query_collision_broadphase(profile);
        let query_mesh_broadphase = open_world_gameplay_query_mesh_broadphase(profile);

        summary.generated_open_world_colliders = summary
            .generated_open_world_colliders
            .saturating_add(generated_colliders.min(u32::MAX as usize) as u32);
        summary.open_world_gameplay_boxes = summary
            .open_world_gameplay_boxes
            .saturating_add(gameplay_boxes.min(u32::MAX as usize) as u32);
        summary.open_world_gameplay_mesh_hulls = summary
            .open_world_gameplay_mesh_hulls
            .saturating_add(gameplay_mesh_hulls.min(u32::MAX as usize) as u32);
        summary.open_world_query_boxes = summary
            .open_world_query_boxes
            .saturating_add(query_boxes.min(u32::MAX as usize) as u32);
        summary.open_world_query_mesh_geometries = summary
            .open_world_query_mesh_geometries
            .saturating_add(query_meshes.geometries.len().min(u32::MAX as usize) as u32);
        summary.open_world_query_mesh_instances = summary
            .open_world_query_mesh_instances
            .saturating_add(query_meshes.instances.len().min(u32::MAX as usize) as u32);
        summary.open_world_query_mesh_triangles = summary
            .open_world_query_mesh_triangles
            .saturating_add(query_mesh_triangle_count(query_meshes));
        summary.query_mesh_bvh_nodes = summary
            .query_mesh_bvh_nodes
            .saturating_add(query_mesh_bvh_node_count(query_meshes));
        accumulate_broadphase_preload_summary(&mut summary, broadphase);
        accumulate_broadphase_preload_summary(&mut summary, query_broadphase);
        accumulate_broadphase_preload_summary(&mut summary, query_mesh_broadphase);
    }

    summary
}

fn query_mesh_triangle_count(meshes: &GameplayQueryMeshSet) -> u32 {
    meshes
        .geometries
        .iter()
        .map(|geometry| geometry.indices.len() / 3)
        .sum::<usize>()
        .min(u32::MAX as usize) as u32
}

fn query_mesh_bvh_node_count(meshes: &GameplayQueryMeshSet) -> u32 {
    meshes
        .geometries
        .iter()
        .map(|geometry| geometry.bvh.nodes.len())
        .sum::<usize>()
        .min(u32::MAX as usize) as u32
}

fn accumulate_broadphase_preload_summary(
    summary: &mut WorldCollisionPreloadSummary,
    broadphase: &GameplayBoxBroadphase,
) {
    summary.broadphase_cells = summary
        .broadphase_cells
        .saturating_add(broadphase.cells.len().min(u32::MAX as usize) as u32);
    summary.broadphase_index_entries = summary
        .broadphase_index_entries
        .saturating_add(broadphase.index_entries.min(u32::MAX as usize) as u32);
    summary.broadphase_max_cell_occupancy = summary
        .broadphase_max_cell_occupancy
        .max(broadphase.max_cell_occupancy.min(u32::MAX as usize) as u32);
    summary.broadphase_max_cells_per_collider = summary
        .broadphase_max_cells_per_collider
        .max(broadphase.max_cells_per_collider.min(u32::MAX as usize) as u32);
    summary.broadphase_unindexed_colliders = summary
        .broadphase_unindexed_colliders
        .saturating_add(broadphase.unindexed_collider_count.min(u32::MAX as usize) as u32);
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
        let query_bounds = movement_sweep_bounds(
            out_x,
            out_z,
            out_x,
            out_z,
            player_radius,
            current_y,
            player_height,
        );

        for collider in movement_box_candidates(
            gameplay_collision_boxes(),
            gameplay_collision_broadphase(),
            query_bounds,
        ) {
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
                GameplayCollisionBox::ObbXyz { .. } => {
                    // Movement files reject obb_xyz at load. This fallback exists only
                    // to keep helper semantics conservative if test data constructs one.
                    let bounds = Aabb3::from_gameplay_box(*collider);
                    push_out_aabb_2d(
                        out_x,
                        out_z,
                        (bounds.min_x + bounds.max_x) * 0.5,
                        (bounds.min_z + bounds.max_z) * 0.5,
                        (bounds.max_x - bounds.min_x) * 0.5,
                        (bounds.max_z - bounds.min_z) * 0.5,
                        player_radius,
                    )
                }
            };
        }

        for hull in movement_mesh_hull_candidates(
            gameplay_movement_mesh_hulls(),
            gameplay_movement_mesh_broadphase(),
            query_bounds,
        ) {
            if !gameplay_movement_mesh_hull_overlaps_player_band(hull, current_y, player_height) {
                continue;
            }

            (out_x, out_z) =
                push_out_convex_footprint_2d(out_x, out_z, &hull.footprint, player_radius);
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
        let query_bounds = movement_sweep_bounds(
            out_x,
            out_z,
            out_x,
            out_z,
            player_radius,
            current_y,
            player_height,
        );

        for collider in movement_box_candidates(
            open_world_gameplay_collision_boxes(profile),
            open_world_gameplay_collision_broadphase(profile),
            query_bounds,
        ) {
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
                GameplayCollisionBox::ObbXyz { .. } => {
                    // Movement files reject obb_xyz at load. This fallback exists only
                    // to keep helper semantics conservative if test data constructs one.
                    let bounds = Aabb3::from_gameplay_box(*collider);
                    push_out_aabb_2d(
                        out_x,
                        out_z,
                        (bounds.min_x + bounds.max_x) * 0.5,
                        (bounds.min_z + bounds.max_z) * 0.5,
                        (bounds.max_x - bounds.min_x) * 0.5,
                        (bounds.max_z - bounds.min_z) * 0.5,
                        player_radius,
                    )
                }
            };
        }

        for hull in movement_mesh_hull_candidates(
            open_world_gameplay_movement_mesh_hulls(profile),
            open_world_gameplay_movement_mesh_broadphase(profile),
            query_bounds,
        ) {
            if !gameplay_movement_mesh_hull_overlaps_player_band(hull, current_y, player_height) {
                continue;
            }
            if gameplay_movement_mesh_hull_can_step_up(hull, current_y) {
                continue;
            }

            (out_x, out_z) =
                push_out_convex_footprint_2d(out_x, out_z, &hull.footprint, player_radius);
        }
    }

    (out_x, out_z)
}

fn resolve_gameplay_horizontal_sweep_collision_y(
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    let mut out_x = target_x;
    let mut out_z = target_z;

    for _ in 0..OPEN_WORLD_COLLISION_ITERS {
        let query_bounds = movement_sweep_bounds(
            start_x,
            start_z,
            out_x,
            out_z,
            player_radius,
            current_y,
            player_height,
        );

        for collider in movement_box_candidates(
            gameplay_collision_boxes(),
            gameplay_collision_broadphase(),
            query_bounds,
        ) {
            if !gameplay_box_overlaps_player_band(*collider, current_y, player_height) {
                continue;
            }

            if let Some((resolved_x, resolved_z)) = resolve_swept_gameplay_box_2d(
                *collider,
                start_x,
                start_z,
                out_x,
                out_z,
                player_radius,
            ) {
                if resolved_x == start_x && resolved_z == start_z {
                    return (start_x, start_z);
                }
                out_x = resolved_x;
                out_z = resolved_z;
            }
        }

        for hull in movement_mesh_hull_candidates(
            gameplay_movement_mesh_hulls(),
            gameplay_movement_mesh_broadphase(),
            query_bounds,
        ) {
            if !gameplay_movement_mesh_hull_overlaps_player_band(hull, current_y, player_height) {
                continue;
            }

            if let Some((resolved_x, resolved_z)) = resolve_swept_convex_footprint_2d(
                &hull.footprint,
                start_x,
                start_z,
                out_x,
                out_z,
                player_radius,
            ) {
                if resolved_x == start_x && resolved_z == start_z {
                    return (start_x, start_z);
                }
                out_x = resolved_x;
                out_z = resolved_z;
            }
        }
    }

    (out_x, out_z)
}

fn resolve_open_world_gameplay_horizontal_sweep_collision_y(
    profile: &OpenWorldSceneProfile,
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    player_radius: f32,
    player_height: f32,
    current_y: f32,
) -> (f32, f32) {
    let mut out_x = target_x;
    let mut out_z = target_z;

    for _ in 0..OPEN_WORLD_COLLISION_ITERS {
        let query_bounds = movement_sweep_bounds(
            start_x,
            start_z,
            out_x,
            out_z,
            player_radius,
            current_y,
            player_height,
        );

        for collider in movement_box_candidates(
            open_world_gameplay_collision_boxes(profile),
            open_world_gameplay_collision_broadphase(profile),
            query_bounds,
        ) {
            if !gameplay_box_overlaps_player_band(*collider, current_y, player_height) {
                continue;
            }
            if gameplay_box_can_step_up(*collider, current_y) {
                continue;
            }

            if let Some((resolved_x, resolved_z)) = resolve_swept_gameplay_box_2d(
                *collider,
                start_x,
                start_z,
                out_x,
                out_z,
                player_radius,
            ) {
                if resolved_x == start_x && resolved_z == start_z {
                    return (start_x, start_z);
                }
                out_x = resolved_x;
                out_z = resolved_z;
            }
        }

        for hull in movement_mesh_hull_candidates(
            open_world_gameplay_movement_mesh_hulls(profile),
            open_world_gameplay_movement_mesh_broadphase(profile),
            query_bounds,
        ) {
            if !gameplay_movement_mesh_hull_overlaps_player_band(hull, current_y, player_height) {
                continue;
            }
            if gameplay_movement_mesh_hull_can_step_up(hull, current_y) {
                continue;
            }

            if let Some((resolved_x, resolved_z)) = resolve_swept_convex_footprint_2d(
                &hull.footprint,
                start_x,
                start_z,
                out_x,
                out_z,
                player_radius,
            ) {
                log_open_world_movement_blocker(
                    profile, hull, start_x, start_z, out_x, out_z, resolved_x, resolved_z,
                    current_y,
                );
                if resolved_x == start_x && resolved_z == start_z {
                    return (start_x, start_z);
                }
                out_x = resolved_x;
                out_z = resolved_z;
            }
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
    // LOS/projectile raycasts should only use the terrain heightfield for this
    // broad open-world ground test. `open_world_surface_height_at_y` also folds
    // walkable gameplay boxes and movement mesh hulls into a single projected
    // surface, which is correct for grounding but creates invisible LOS blockers
    // under/near overhanging authored meshes. Those colliders are tested by
    // their precise box/query-mesh raycasts below.
    if y <= open_world_surface_height_for_profile(profile, x, z) + radius {
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

    let mesh_query_bounds = if ceiling.is_finite() {
        Some(Aabb3 {
            min_x: x - COLLISION_EPSILON,
            min_y: surface - COLLISION_EPSILON,
            min_z: z - COLLISION_EPSILON,
            max_x: x + COLLISION_EPSILON,
            max_y: ceiling,
            max_z: z + COLLISION_EPSILON,
        })
    } else {
        None
    };

    for hull in movement_mesh_hull_candidates(
        open_world_gameplay_movement_mesh_hulls(profile),
        open_world_gameplay_movement_mesh_broadphase(profile),
        mesh_query_bounds,
    ) {
        if hull.y_min > ceiling + COLLISION_EPSILON {
            continue;
        }
        let Some(y) = gameplay_movement_mesh_hull_surface_height_at_xz(hull, x, z) else {
            continue;
        };
        if y <= ceiling + COLLISION_EPSILON && y > surface {
            surface = y;
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
        GameplayCollisionBox::ObbXyz { .. } => {
            let bounds = Aabb3::from_gameplay_box(collider);
            (
                (bounds.min_y + bounds.max_y) * 0.5,
                (bounds.max_y - bounds.min_y) * 0.5,
            )
        }
    };

    head_y > center_y - half_y + COLLISION_EPSILON && foot_y < center_y + half_y - COLLISION_EPSILON
}

fn gameplay_box_can_step_up(collider: GameplayCollisionBox, foot_y: f32) -> bool {
    gameplay_box_top_y(collider) <= foot_y + GAMEPLAY_BOX_STEP_UP_HEIGHT
}

fn gameplay_movement_mesh_hull_overlaps_player_band(
    hull: &GameplayMovementMeshHull,
    foot_y: f32,
    player_height: f32,
) -> bool {
    let head_y = foot_y + player_height;
    head_y > hull.y_min + COLLISION_EPSILON && foot_y < hull.y_max - COLLISION_EPSILON
}

fn gameplay_movement_mesh_hull_can_step_up(hull: &GameplayMovementMeshHull, foot_y: f32) -> bool {
    hull.y_max <= foot_y + GAMEPLAY_MESH_STEP_UP_HEIGHT
}

fn gameplay_movement_mesh_hull_surface_height_at_xz(
    hull: &GameplayMovementMeshHull,
    x: f32,
    z: f32,
) -> Option<f32> {
    if hull.ground_normal_y_abs < GAMEPLAY_MESH_GROUND_MIN_NORMAL_Y {
        return None;
    }
    triangle_height_at_xz(hull.triangle[0], hull.triangle[1], hull.triangle[2], x, z)
}

fn triangle_height_at_xz(a: [f32; 3], b: [f32; 3], c: [f32; 3], x: f32, z: f32) -> Option<f32> {
    let denom = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2]);
    if !denom.is_finite() || denom.abs() <= COLLISION_EPSILON {
        return None;
    }

    let alpha = ((b[2] - c[2]) * (x - c[0]) + (c[0] - b[0]) * (z - c[2])) / denom;
    let beta = ((c[2] - a[2]) * (x - c[0]) + (a[0] - c[0]) * (z - c[2])) / denom;
    let gamma = 1.0 - alpha - beta;
    if alpha < -COLLISION_EPSILON || beta < -COLLISION_EPSILON || gamma < -COLLISION_EPSILON {
        return None;
    }

    let y = alpha * a[1] + beta * b[1] + gamma * c[1];
    y.is_finite().then_some(y)
}

fn movement_sweep_bounds(
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    player_radius: f32,
    foot_y: f32,
    player_height: f32,
) -> Option<Aabb3> {
    if !start_x.is_finite()
        || !start_z.is_finite()
        || !target_x.is_finite()
        || !target_z.is_finite()
        || !player_radius.is_finite()
        || !foot_y.is_finite()
        || !player_height.is_finite()
    {
        return None;
    }

    let radius = player_radius.max(0.0);
    let height = player_height.max(0.1);
    Some(Aabb3 {
        min_x: start_x.min(target_x) - radius,
        min_y: foot_y,
        min_z: start_z.min(target_z) - radius,
        max_x: start_x.max(target_x) + radius,
        max_y: foot_y + height,
        max_z: start_z.max(target_z) + radius,
    })
}

fn movement_box_candidates<'a>(
    colliders: &'a [GameplayCollisionBox],
    broadphase: &GameplayBoxBroadphase,
    query_bounds: Option<Aabb3>,
) -> Vec<&'a GameplayCollisionBox> {
    match movement_broadphase_indices(broadphase, query_bounds) {
        Some(indices) => indices
            .into_iter()
            .filter_map(|index| colliders.get(index))
            .collect(),
        None => colliders.iter().collect(),
    }
}

fn movement_mesh_hull_candidates<'a>(
    hulls: &'a [GameplayMovementMeshHull],
    broadphase: &GameplayBoxBroadphase,
    query_bounds: Option<Aabb3>,
) -> Vec<&'a GameplayMovementMeshHull> {
    match movement_broadphase_indices(broadphase, query_bounds) {
        Some(indices) => indices
            .into_iter()
            .filter_map(|index| hulls.get(index))
            .collect(),
        None => hulls.iter().collect(),
    }
}

fn movement_broadphase_indices(
    broadphase: &GameplayBoxBroadphase,
    query_bounds: Option<Aabb3>,
) -> Option<Vec<usize>> {
    let query_bounds = query_bounds?;
    GAMEPLAY_BROADPHASE_CANDIDATE_SCRATCH.with(|scratch| {
        let mut candidate_indices = scratch.borrow_mut();
        if broadphase.query_aabb_into(query_bounds, &mut candidate_indices) {
            Some(candidate_indices.clone())
        } else {
            None
        }
    })
}

fn log_open_world_movement_blocker(
    profile: &OpenWorldSceneProfile,
    hull: &GameplayMovementMeshHull,
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    resolved_x: f32,
    resolved_z: f32,
    current_y: f32,
) {
    let count = MOVEMENT_BLOCKER_LOG_COUNT.fetch_add(1, Ordering::Relaxed);
    if count >= MOVEMENT_BLOCKER_LOG_LIMIT {
        return;
    }

    log::warn!(
        "[WORLD_COLLISION] movement blocked scene={} blocker='{}' y=[{:.2},{:.2}] start=({:.2},{:.2},{:.2}) target=({:.2},{:.2},{:.2}) resolved=({:.2},{:.2},{:.2})",
        profile.scene_name,
        hull.name,
        hull.y_min,
        hull.y_max,
        start_x,
        current_y,
        start_z,
        target_x,
        current_y,
        target_z,
        resolved_x,
        current_y,
        resolved_z,
    );
}

fn gameplay_box_top_y(collider: GameplayCollisionBox) -> f32 {
    match collider {
        GameplayCollisionBox::ObbY {
            center_y, half_y, ..
        }
        | GameplayCollisionBox::Aabb {
            center_y, half_y, ..
        } => center_y + half_y,
        GameplayCollisionBox::ObbXyz { .. } => Aabb3::from_gameplay_box(collider).max_y,
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
        GameplayCollisionBox::ObbXyz { .. } => {
            let bounds = Aabb3::from_gameplay_box(collider);
            x >= bounds.min_x - COLLISION_EPSILON
                && x <= bounds.max_x + COLLISION_EPSILON
                && z >= bounds.min_z - COLLISION_EPSILON
                && z <= bounds.max_z + COLLISION_EPSILON
        }
    }
}

fn gameplay_box_overlaps_point_2d(
    collider: GameplayCollisionBox,
    x: f32,
    z: f32,
    padding: f32,
) -> bool {
    if !padding.is_finite() || padding < 0.0 {
        return false;
    }

    match collider {
        GameplayCollisionBox::Aabb {
            center_x,
            center_z,
            half_x,
            half_z,
            ..
        } => aabb_overlaps_point_2d(x, z, center_x, center_z, half_x, half_z, padding),
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
            aabb_overlaps_point_2d(local_x, local_z, 0.0, 0.0, half_x, half_z, padding)
        }
        GameplayCollisionBox::ObbXyz { .. } => {
            let bounds = Aabb3::from_gameplay_box(collider);
            aabb_overlaps_point_2d(
                x,
                z,
                (bounds.min_x + bounds.max_x) * 0.5,
                (bounds.min_z + bounds.max_z) * 0.5,
                (bounds.max_x - bounds.min_x) * 0.5,
                (bounds.max_z - bounds.min_z) * 0.5,
                padding,
            )
        }
    }
}

fn resolve_swept_gameplay_box_2d(
    collider: GameplayCollisionBox,
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    padding: f32,
) -> Option<(f32, f32)> {
    let target_overlaps = gameplay_box_overlaps_point_2d(collider, target_x, target_z, padding);
    if !target_overlaps {
        return None;
    }

    let start_overlaps = gameplay_box_overlaps_point_2d(collider, start_x, start_z, padding);
    if !start_overlaps {
        return Some((start_x, start_z));
    }

    Some(push_out_gameplay_box_2d(
        collider, target_x, target_z, padding,
    ))
}

fn push_out_gameplay_box_2d(
    collider: GameplayCollisionBox,
    x: f32,
    z: f32,
    padding: f32,
) -> (f32, f32) {
    match collider {
        GameplayCollisionBox::ObbY {
            center_x,
            center_z,
            half_x,
            half_z,
            sin_y,
            cos_y,
            ..
        } => push_out_obb_y_2d(
            x, z, center_x, center_z, half_x, half_z, padding, sin_y, cos_y,
        ),
        GameplayCollisionBox::Aabb {
            center_x,
            center_z,
            half_x,
            half_z,
            ..
        } => push_out_aabb_2d(x, z, center_x, center_z, half_x, half_z, padding),
        GameplayCollisionBox::ObbXyz { .. } => {
            let bounds = Aabb3::from_gameplay_box(collider);
            push_out_aabb_2d(
                x,
                z,
                (bounds.min_x + bounds.max_x) * 0.5,
                (bounds.min_z + bounds.max_z) * 0.5,
                (bounds.max_x - bounds.min_x) * 0.5,
                (bounds.max_z - bounds.min_z) * 0.5,
                padding,
            )
        }
    }
}

fn aabb_overlaps_point_2d(
    x: f32,
    z: f32,
    cx: f32,
    cz: f32,
    half_x: f32,
    half_z: f32,
    padding: f32,
) -> bool {
    (x - cx).abs() < half_x + padding && (z - cz).abs() < half_z + padding
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
    static GIANT_SKELETON_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static GOLDEN_VALLEY_OVERCAST_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static GOLDEN_VALLEY_SUNNY_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static GREAT_HALL_DAY_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static IDOL_DAY_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
    static RANDOM_DUNGEON_COLLIDERS: OnceLock<Vec<Collider>> = OnceLock::new();
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
    } else if profile.scene_name == GIANT_SKELETON_PROFILE.scene_name {
        GIANT_SKELETON_COLLIDERS
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
    } else if profile.scene_name == RANDOM_DUNGEON_PROFILE.scene_name {
        RANDOM_DUNGEON_COLLIDERS
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

fn gameplay_collision_broadphase() -> &'static GameplayBoxBroadphase {
    static GAMEPLAY_BOX_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    GAMEPLAY_BOX_BROADPHASE.get_or_init(|| GameplayBoxBroadphase::build(gameplay_collision_boxes()))
}

fn gameplay_query_collision_boxes() -> &'static [GameplayCollisionBox] {
    static GAMEPLAY_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    GAMEPLAY_QUERY_BOXES
        .get_or_init(load_gameplay_query_collision_boxes)
        .as_slice()
}

fn gameplay_query_collision_broadphase() -> &'static GameplayBoxBroadphase {
    static GAMEPLAY_QUERY_BOX_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    GAMEPLAY_QUERY_BOX_BROADPHASE
        .get_or_init(|| GameplayBoxBroadphase::build(gameplay_query_collision_boxes()))
}

fn gameplay_query_meshes() -> &'static GameplayQueryMeshSet {
    static GAMEPLAY_QUERY_MESHES: OnceLock<GameplayQueryMeshSet> = OnceLock::new();
    GAMEPLAY_QUERY_MESHES.get_or_init(load_gameplay_query_meshes)
}

fn gameplay_query_mesh_broadphase() -> &'static GameplayBoxBroadphase {
    static GAMEPLAY_QUERY_MESH_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    GAMEPLAY_QUERY_MESH_BROADPHASE.get_or_init(|| {
        GameplayBoxBroadphase::build_from_aabbs(
            gameplay_query_meshes().instances.len(),
            gameplay_query_meshes()
                .instances
                .iter()
                .map(|instance| instance.bounds),
        )
    })
}

fn gameplay_movement_mesh_hulls() -> &'static [GameplayMovementMeshHull] {
    static GAMEPLAY_MOVEMENT_MESH_HULLS: OnceLock<Vec<GameplayMovementMeshHull>> = OnceLock::new();
    GAMEPLAY_MOVEMENT_MESH_HULLS
        .get_or_init(load_gameplay_movement_mesh_hulls)
        .as_slice()
}

fn gameplay_movement_mesh_broadphase() -> &'static GameplayBoxBroadphase {
    static GAMEPLAY_MOVEMENT_MESH_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    GAMEPLAY_MOVEMENT_MESH_BROADPHASE.get_or_init(|| {
        GameplayBoxBroadphase::build_from_aabbs(
            gameplay_movement_mesh_hulls().len(),
            gameplay_movement_mesh_hulls()
                .iter()
                .map(|hull| hull.bounds),
        )
    })
}

fn open_world_gameplay_collision_boxes(
    profile: &OpenWorldSceneProfile,
) -> &'static [GameplayCollisionBox] {
    static OPEN_WORLD_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static ADVENTURE_ISLAND_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static DESERT_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static DOCKS_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static GIANT_SKELETON_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static GOLDEN_VALLEY_OVERCAST_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> =
        OnceLock::new();
    static GOLDEN_VALLEY_SUNNY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> =
        OnceLock::new();
    static GREAT_HALL_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static IDOL_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static OASIS_DAY_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static RANDOM_DUNGEON_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static TEMPLE_GARDENS_GAMEPLAY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();

    if profile.scene_name == OASIS_DAY_PROFILE.scene_name {
        OASIS_DAY_GAMEPLAY_BOXES
            .get_or_init(|| load_open_world_gameplay_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == ADVENTURE_ISLAND_PROFILE.scene_name {
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
    } else if profile.scene_name == GIANT_SKELETON_PROFILE.scene_name {
        GIANT_SKELETON_GAMEPLAY_BOXES
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
    } else if profile.scene_name == RANDOM_DUNGEON_PROFILE.scene_name {
        RANDOM_DUNGEON_GAMEPLAY_BOXES
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

fn open_world_gameplay_query_collision_boxes(
    profile: &OpenWorldSceneProfile,
) -> &'static [GameplayCollisionBox] {
    static OPEN_WORLD_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static ADVENTURE_ISLAND_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static DESERT_DAY_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static DOCKS_DAY_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static GIANT_SKELETON_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static GOLDEN_VALLEY_OVERCAST_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> =
        OnceLock::new();
    static GOLDEN_VALLEY_SUNNY_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static GREAT_HALL_DAY_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static IDOL_DAY_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static OASIS_DAY_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static RANDOM_DUNGEON_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();
    static TEMPLE_GARDENS_QUERY_BOXES: OnceLock<Vec<GameplayCollisionBox>> = OnceLock::new();

    if profile.scene_name == OASIS_DAY_PROFILE.scene_name {
        OASIS_DAY_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == ADVENTURE_ISLAND_PROFILE.scene_name {
        ADVENTURE_ISLAND_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == DESERT_DAY_PROFILE.scene_name {
        DESERT_DAY_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == DOCKS_DAY_PROFILE.scene_name {
        DOCKS_DAY_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == GIANT_SKELETON_PROFILE.scene_name {
        GIANT_SKELETON_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == GOLDEN_VALLEY_OVERCAST_PROFILE.scene_name {
        GOLDEN_VALLEY_OVERCAST_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == GOLDEN_VALLEY_SUNNY_PROFILE.scene_name {
        GOLDEN_VALLEY_SUNNY_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == GREAT_HALL_DAY_PROFILE.scene_name {
        GREAT_HALL_DAY_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == IDOL_DAY_PROFILE.scene_name {
        IDOL_DAY_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == RANDOM_DUNGEON_PROFILE.scene_name {
        RANDOM_DUNGEON_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else if profile.scene_name == TEMPLE_GARDENS_PROFILE.scene_name {
        TEMPLE_GARDENS_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    } else {
        OPEN_WORLD_QUERY_BOXES
            .get_or_init(|| load_open_world_gameplay_query_collision_boxes(profile))
            .as_slice()
    }
}

fn open_world_gameplay_collision_broadphase(
    profile: &OpenWorldSceneProfile,
) -> &'static GameplayBoxBroadphase {
    static OPEN_WORLD_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static ADVENTURE_ISLAND_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static DESERT_DAY_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static DOCKS_DAY_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static GIANT_SKELETON_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static GOLDEN_VALLEY_OVERCAST_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> =
        OnceLock::new();
    static GOLDEN_VALLEY_SUNNY_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> =
        OnceLock::new();
    static GREAT_HALL_DAY_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static IDOL_DAY_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static OASIS_DAY_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static RANDOM_DUNGEON_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static TEMPLE_GARDENS_GAMEPLAY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();

    if profile.scene_name == OASIS_DAY_PROFILE.scene_name {
        OASIS_DAY_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == ADVENTURE_ISLAND_PROFILE.scene_name {
        ADVENTURE_ISLAND_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == DESERT_DAY_PROFILE.scene_name {
        DESERT_DAY_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == DOCKS_DAY_PROFILE.scene_name {
        DOCKS_DAY_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == GIANT_SKELETON_PROFILE.scene_name {
        GIANT_SKELETON_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == GOLDEN_VALLEY_OVERCAST_PROFILE.scene_name {
        GOLDEN_VALLEY_OVERCAST_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == GOLDEN_VALLEY_SUNNY_PROFILE.scene_name {
        GOLDEN_VALLEY_SUNNY_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == GREAT_HALL_DAY_PROFILE.scene_name {
        GREAT_HALL_DAY_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == IDOL_DAY_PROFILE.scene_name {
        IDOL_DAY_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == RANDOM_DUNGEON_PROFILE.scene_name {
        RANDOM_DUNGEON_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else if profile.scene_name == TEMPLE_GARDENS_PROFILE.scene_name {
        TEMPLE_GARDENS_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    } else {
        OPEN_WORLD_GAMEPLAY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_collision_boxes(profile))
        })
    }
}

fn open_world_gameplay_query_collision_broadphase(
    profile: &OpenWorldSceneProfile,
) -> &'static GameplayBoxBroadphase {
    static OPEN_WORLD_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static ADVENTURE_ISLAND_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static DESERT_DAY_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static DOCKS_DAY_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static GIANT_SKELETON_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static GOLDEN_VALLEY_OVERCAST_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> =
        OnceLock::new();
    static GOLDEN_VALLEY_SUNNY_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static GREAT_HALL_DAY_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static IDOL_DAY_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static OASIS_DAY_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static RANDOM_DUNGEON_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static TEMPLE_GARDENS_QUERY_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();

    if profile.scene_name == OASIS_DAY_PROFILE.scene_name {
        OASIS_DAY_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == ADVENTURE_ISLAND_PROFILE.scene_name {
        ADVENTURE_ISLAND_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == DESERT_DAY_PROFILE.scene_name {
        DESERT_DAY_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == DOCKS_DAY_PROFILE.scene_name {
        DOCKS_DAY_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == GIANT_SKELETON_PROFILE.scene_name {
        GIANT_SKELETON_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == GOLDEN_VALLEY_OVERCAST_PROFILE.scene_name {
        GOLDEN_VALLEY_OVERCAST_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == GOLDEN_VALLEY_SUNNY_PROFILE.scene_name {
        GOLDEN_VALLEY_SUNNY_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == GREAT_HALL_DAY_PROFILE.scene_name {
        GREAT_HALL_DAY_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == IDOL_DAY_PROFILE.scene_name {
        IDOL_DAY_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == RANDOM_DUNGEON_PROFILE.scene_name {
        RANDOM_DUNGEON_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else if profile.scene_name == TEMPLE_GARDENS_PROFILE.scene_name {
        TEMPLE_GARDENS_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    } else {
        OPEN_WORLD_QUERY_BROADPHASE.get_or_init(|| {
            GameplayBoxBroadphase::build(open_world_gameplay_query_collision_boxes(profile))
        })
    }
}

fn open_world_gameplay_query_meshes(
    profile: &OpenWorldSceneProfile,
) -> &'static GameplayQueryMeshSet {
    static EMPTY_QUERY_MESH_SET: OnceLock<GameplayQueryMeshSet> = OnceLock::new();
    static OPEN_WORLD_QUERY_MESHES: OnceLock<HashMap<&'static str, GameplayQueryMeshSet>> =
        OnceLock::new();

    OPEN_WORLD_QUERY_MESHES
        .get_or_init(|| {
            let mut meshes_by_scene = HashMap::new();
            for profile in OPEN_WORLD_SCENE_PROFILES {
                meshes_by_scene.insert(
                    profile.scene_name,
                    load_open_world_gameplay_query_meshes(profile),
                );
            }
            meshes_by_scene
        })
        .get(profile.scene_name)
        .unwrap_or_else(|| EMPTY_QUERY_MESH_SET.get_or_init(GameplayQueryMeshSet::default))
}

fn open_world_gameplay_query_mesh_broadphase(
    profile: &OpenWorldSceneProfile,
) -> &'static GameplayBoxBroadphase {
    static EMPTY_QUERY_MESH_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static OPEN_WORLD_QUERY_MESH_BROADPHASES: OnceLock<
        HashMap<&'static str, GameplayBoxBroadphase>,
    > = OnceLock::new();

    OPEN_WORLD_QUERY_MESH_BROADPHASES
        .get_or_init(|| {
            let mut broadphases_by_scene = HashMap::new();
            for profile in OPEN_WORLD_SCENE_PROFILES {
                let meshes = open_world_gameplay_query_meshes(profile);
                broadphases_by_scene.insert(
                    profile.scene_name,
                    GameplayBoxBroadphase::build_from_aabbs(
                        meshes.instances.len(),
                        meshes.instances.iter().map(|instance| instance.bounds),
                    ),
                );
            }
            broadphases_by_scene
        })
        .get(profile.scene_name)
        .unwrap_or_else(|| {
            EMPTY_QUERY_MESH_BROADPHASE.get_or_init(|| {
                GameplayBoxBroadphase::build_from_aabbs(0, std::iter::empty::<Aabb3>())
            })
        })
}

fn open_world_gameplay_movement_mesh_hulls(
    profile: &OpenWorldSceneProfile,
) -> &'static [GameplayMovementMeshHull] {
    static EMPTY_MOVEMENT_MESH_HULLS: OnceLock<Vec<GameplayMovementMeshHull>> = OnceLock::new();
    static OPEN_WORLD_MOVEMENT_MESH_HULLS: OnceLock<
        HashMap<&'static str, Vec<GameplayMovementMeshHull>>,
    > = OnceLock::new();

    OPEN_WORLD_MOVEMENT_MESH_HULLS
        .get_or_init(|| {
            let mut hulls_by_scene = HashMap::new();
            for profile in OPEN_WORLD_SCENE_PROFILES {
                hulls_by_scene.insert(
                    profile.scene_name,
                    load_open_world_gameplay_movement_mesh_hulls(profile),
                );
            }
            hulls_by_scene
        })
        .get(profile.scene_name)
        .map(Vec::as_slice)
        .unwrap_or_else(|| EMPTY_MOVEMENT_MESH_HULLS.get_or_init(Vec::new).as_slice())
}

fn open_world_gameplay_movement_mesh_broadphase(
    profile: &OpenWorldSceneProfile,
) -> &'static GameplayBoxBroadphase {
    static EMPTY_MOVEMENT_MESH_BROADPHASE: OnceLock<GameplayBoxBroadphase> = OnceLock::new();
    static OPEN_WORLD_MOVEMENT_MESH_BROADPHASES: OnceLock<
        HashMap<&'static str, GameplayBoxBroadphase>,
    > = OnceLock::new();

    OPEN_WORLD_MOVEMENT_MESH_BROADPHASES
        .get_or_init(|| {
            let mut broadphases_by_scene = HashMap::new();
            for profile in OPEN_WORLD_SCENE_PROFILES {
                let hulls = open_world_gameplay_movement_mesh_hulls(profile);
                broadphases_by_scene.insert(
                    profile.scene_name,
                    GameplayBoxBroadphase::build_from_aabbs(
                        hulls.len(),
                        hulls.iter().map(|hull| hull.bounds),
                    ),
                );
            }
            broadphases_by_scene
        })
        .get(profile.scene_name)
        .unwrap_or_else(|| {
            EMPTY_MOVEMENT_MESH_BROADPHASE.get_or_init(|| {
                GameplayBoxBroadphase::build_from_aabbs(0, std::iter::empty::<Aabb3>())
            })
        })
}

fn load_gameplay_collision_boxes() -> Vec<GameplayCollisionBox> {
    let file: GameplayCollisionLayoutFile = serde_json::from_str(GAMEPLAY_COLLISION_JSON)
        .expect("failed to parse gameplay_collision.shared.json");

    let boxes = parse_gameplay_collision_boxes(file);
    assert_no_full_rotation_movement_boxes(&boxes, "gameplay_collision.shared.json");
    boxes
}

fn load_gameplay_query_collision_boxes() -> Vec<GameplayCollisionBox> {
    let file: GameplayCollisionLayoutFile = serde_json::from_str(GAMEPLAY_QUERY_COLLISION_JSON)
        .expect("failed to parse gameplay_query_collision.shared.json");

    parse_gameplay_collision_boxes(file)
}

fn load_gameplay_query_meshes() -> GameplayQueryMeshSet {
    let file: GameplayCollisionLayoutFile = serde_json::from_str(GAMEPLAY_QUERY_COLLISION_JSON)
        .expect("failed to parse gameplay_query_collision.shared.json");

    parse_gameplay_query_meshes(file)
}

fn load_gameplay_movement_mesh_hulls() -> Vec<GameplayMovementMeshHull> {
    let file: GameplayCollisionLayoutFile = serde_json::from_str(GAMEPLAY_COLLISION_JSON)
        .expect("failed to parse gameplay_collision.shared.json");

    parse_gameplay_movement_mesh_hulls(file)
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

    let boxes = parse_gameplay_collision_boxes(file);
    assert_no_full_rotation_movement_boxes(&boxes, profile.scene_name);
    boxes
}

fn load_open_world_gameplay_query_collision_boxes(
    profile: &OpenWorldSceneProfile,
) -> Vec<GameplayCollisionBox> {
    let file: GameplayCollisionLayoutFile =
        serde_json::from_str(query_collision_json_for_profile(profile))
            .expect("failed to parse open-world gameplay query collision JSON");

    parse_gameplay_collision_boxes(file)
}

fn load_open_world_gameplay_query_meshes(profile: &OpenWorldSceneProfile) -> GameplayQueryMeshSet {
    let file: GameplayCollisionLayoutFile =
        serde_json::from_str(query_collision_json_for_profile(profile))
            .expect("failed to parse open-world gameplay query collision JSON");

    parse_gameplay_query_meshes(file)
}

fn load_open_world_gameplay_movement_mesh_hulls(
    profile: &OpenWorldSceneProfile,
) -> Vec<GameplayMovementMeshHull> {
    let json = if profile.scene_name == OASIS_DAY_PROFILE.scene_name {
        OPEN_WORLD_GAMEPLAY_COLLISION_JSON
    } else {
        profile.gameplay_collision_json
    };
    let file: GameplayCollisionLayoutFile =
        serde_json::from_str(json).expect("failed to parse open-world gameplay collision JSON");

    parse_gameplay_movement_mesh_hulls(file)
}

fn query_collision_json_for_profile(profile: &OpenWorldSceneProfile) -> &'static str {
    if profile.scene_name == OASIS_DAY_PROFILE.scene_name {
        OPEN_WORLD_GAMEPLAY_QUERY_COLLISION_JSON
    } else {
        profile.gameplay_query_collision_json
    }
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
                "obb_xyz" => {
                    let (
                        axis_x_x,
                        axis_x_y,
                        axis_x_z,
                        axis_y_x,
                        axis_y_y,
                        axis_y_z,
                        axis_z_x,
                        axis_z_y,
                        axis_z_z,
                    ) = quaternion_to_axes(&box_file.rotation)
                        .expect("invalid obb_xyz rotation quaternion in gameplay collision data");
                    GameplayCollisionBox::ObbXyz {
                        center_x,
                        center_y,
                        center_z,
                        half_x,
                        half_y,
                        half_z,
                        axis_x_x,
                        axis_x_y,
                        axis_x_z,
                        axis_y_x,
                        axis_y_y,
                        axis_y_z,
                        axis_z_x,
                        axis_z_y,
                        axis_z_z,
                    }
                }
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

fn parse_gameplay_query_meshes(file: GameplayCollisionLayoutFile) -> GameplayQueryMeshSet {
    let mut scene_triangle_count = 0usize;
    let mut geometry_indices_by_id = HashMap::new();
    let geometries: Vec<GameplayQueryMeshGeometry> = file
        .mesh_geometries
        .into_iter()
        .enumerate()
        .map(|(geometry_index, mesh_file)| {
            assert!(
                !mesh_file.id.is_empty(),
                "query mesh geometry at index {geometry_index} has an empty id"
            );
            assert!(
                geometry_indices_by_id
                    .insert(mesh_file.id.clone(), geometry_index)
                    .is_none(),
                "duplicate query mesh geometry id '{}'",
                mesh_file.id
            );
            assert!(
                mesh_file.triangle_count <= MAX_QUERY_MESH_TRIANGLES_PER_COLLIDER,
                "query mesh geometry '{}' has {} triangles, exceeding per-collider budget {}",
                mesh_file.id,
                mesh_file.triangle_count,
                MAX_QUERY_MESH_TRIANGLES_PER_COLLIDER
            );
            scene_triangle_count = scene_triangle_count.saturating_add(mesh_file.triangle_count);
            assert!(
                scene_triangle_count <= MAX_QUERY_MESH_TRIANGLES_PER_SCENE,
                "query mesh geometry data exceeds triangle budget {}",
                MAX_QUERY_MESH_TRIANGLES_PER_SCENE
            );
            assert!(
                mesh_file.vertex_count > 0 && mesh_file.triangle_count > 0,
                "query mesh geometry '{}' is empty",
                mesh_file.id
            );
            assert!(
                mesh_file.vertices.len() == mesh_file.vertex_count * 3,
                "query mesh geometry '{}' vertex buffer length {} does not match vertex_count {}",
                mesh_file.id,
                mesh_file.vertices.len(),
                mesh_file.vertex_count
            );
            assert!(
                mesh_file.indices.len() == mesh_file.triangle_count * 3,
                "query mesh geometry '{}' index buffer length {} does not match triangle_count {}",
                mesh_file.id,
                mesh_file.indices.len(),
                mesh_file.triangle_count
            );

            let mut vertices = Vec::with_capacity(mesh_file.vertex_count);
            let mut bounds: Option<Aabb3> = None;
            for vertex in mesh_file.vertices.chunks_exact(3) {
                let x = vertex[0];
                let y = vertex[1];
                let z = vertex[2];
                assert!(
                    x.is_finite() && y.is_finite() && z.is_finite(),
                    "query mesh geometry '{}' contains non-finite vertex data",
                    mesh_file.id
                );
                vertices.push([x, y, z]);
                bounds = Some(match bounds {
                    Some(bounds) => Aabb3 {
                        min_x: bounds.min_x.min(x),
                        min_y: bounds.min_y.min(y),
                        min_z: bounds.min_z.min(z),
                        max_x: bounds.max_x.max(x),
                        max_y: bounds.max_y.max(y),
                        max_z: bounds.max_z.max(z),
                    },
                    None => Aabb3 {
                        min_x: x,
                        min_y: y,
                        min_z: z,
                        max_x: x,
                        max_y: y,
                        max_z: z,
                    },
                });
            }

            for index in &mesh_file.indices {
                assert!(
                    *index < mesh_file.vertex_count,
                    "query mesh geometry '{}' index {} is outside vertex_count {}",
                    mesh_file.id,
                    index,
                    mesh_file.vertex_count
                );
            }

            for triangle in mesh_file.indices.chunks_exact(3) {
                let a = vertices[triangle[0]];
                let b = vertices[triangle[1]];
                let c = vertices[triangle[2]];
                assert!(
                    triangle_area_squared(a, b, c)
                        > QUERY_MESH_DEGENERATE_TRIANGLE_AREA_SQUARED_EPSILON,
                    "query mesh geometry '{}' contains a degenerate triangle",
                    mesh_file.id
                );
            }

            GameplayQueryMeshGeometry {
                id: mesh_file.id,
                source: mesh_file.source,
                bvh: GameplayQueryMeshBvh::build(&vertices, &mesh_file.indices),
                vertices,
                indices: mesh_file.indices,
                local_bounds: bounds
                    .expect("query mesh bounds should exist for non-empty geometry"),
            }
        })
        .collect();

    let instances = file
        .mesh_instances
        .into_iter()
        .map(|instance_file| {
            assert!(
                !instance_file.name.is_empty(),
                "query mesh instance has an empty name"
            );
            let geometry_index = *geometry_indices_by_id
                .get(&instance_file.geometry_id)
                .unwrap_or_else(|| {
                    panic!(
                        "query mesh instance '{}' references missing geometry '{}'",
                        instance_file.name, instance_file.geometry_id
                    )
                });
            assert!(
                instance_file.transform.len() == 16,
                "query mesh instance '{}' transform length {} does not equal 16",
                instance_file.name,
                instance_file.transform.len()
            );
            let mut transform = [0.0; 16];
            for (index, value) in instance_file.transform.into_iter().enumerate() {
                assert!(
                    value.is_finite(),
                    "query mesh instance '{}' contains non-finite transform data",
                    instance_file.name
                );
                transform[index] = value;
            }

            let bounds = mesh_instance_bounds(&geometries[geometry_index], &transform);
            assert!(
                bounds.is_finite(),
                "query mesh instance '{}' computed non-finite bounds",
                instance_file.name
            );

            GameplayQueryMeshInstance {
                name: instance_file.name,
                geometry_index,
                transform,
                bounds,
            }
        })
        .collect();

    GameplayQueryMeshSet {
        geometries,
        instances,
    }
}

fn parse_gameplay_movement_mesh_hulls(
    file: GameplayCollisionLayoutFile,
) -> Vec<GameplayMovementMeshHull> {
    let mesh_set = parse_gameplay_query_meshes(file);
    mesh_set
        .instances
        .iter()
        .flat_map(|instance| {
            let geometry = &mesh_set.geometries[instance.geometry_index];
            build_movement_mesh_hulls(instance, geometry)
        })
        .collect()
}

fn build_movement_mesh_hulls(
    instance: &GameplayQueryMeshInstance,
    geometry: &GameplayQueryMeshGeometry,
) -> Vec<GameplayMovementMeshHull> {
    let mut segments = Vec::new();
    for (triangle_index, triangle) in geometry.indices.chunks_exact(3).enumerate() {
        let a = transform_point(&instance.transform, geometry.vertices[triangle[0]]);
        let b = transform_point(&instance.transform, geometry.vertices[triangle[1]]);
        let c = transform_point(&instance.transform, geometry.vertices[triangle[2]]);
        if !point3_is_finite(a) || !point3_is_finite(b) || !point3_is_finite(c) {
            continue;
        }

        let y_min = a[1].min(b[1]).min(c[1]);
        let y_max = a[1].max(b[1]).max(c[1]);
        let footprint = movement_triangle_footprint_2d([a[0], a[2]], [b[0], b[2]], [c[0], c[2]]);
        if footprint.len() < 2 {
            continue;
        }

        segments.push(GameplayMovementMeshHull {
            name: format!("{}#tri{}", instance.name, triangle_index),
            y_min,
            y_max,
            footprint,
            triangle: [a, b, c],
            ground_normal_y_abs: triangle_normal_y_abs(a, b, c),
            bounds: Aabb3 {
                min_x: a[0].min(b[0]).min(c[0]),
                min_y: y_min,
                min_z: a[2].min(b[2]).min(c[2]),
                max_x: a[0].max(b[0]).max(c[0]),
                max_y: y_max,
                max_z: a[2].max(b[2]).max(c[2]),
            },
        });
    }
    segments
}

fn triangle_normal_y_abs(a: [f32; 3], b: [f32; 3], c: [f32; 3]) -> f32 {
    let ab = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
    let ac = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
    let cross = cross3(ab, ac);
    let length = (cross[0] * cross[0] + cross[1] * cross[1] + cross[2] * cross[2]).sqrt();
    if length <= COLLISION_EPSILON {
        return 0.0;
    }
    (cross[1] / length).abs()
}

fn movement_triangle_footprint_2d(a: [f32; 2], b: [f32; 2], c: [f32; 2]) -> Vec<[f32; 2]> {
    let mut points = vec![a, b, c];
    points.retain(|point| point[0].is_finite() && point[1].is_finite());
    points.sort_by(|left, right| {
        left[0]
            .total_cmp(&right[0])
            .then_with(|| left[1].total_cmp(&right[1]))
    });
    points.dedup_by(|left, right| {
        (left[0] - right[0]).abs() <= COLLISION_EPSILON
            && (left[1] - right[1]).abs() <= COLLISION_EPSILON
    });
    if points.len() <= 2 {
        return points;
    }

    let area = polygon_area_signed(&[a, b, c]);
    if area.abs() <= COLLISION_EPSILON {
        let mut best = [points[0], points[1]];
        let mut best_len_sq = distance_sq_2d(points[0], points[1]);
        for pair in [[points[0], points[2]], [points[1], points[2]]] {
            let len_sq = distance_sq_2d(pair[0], pair[1]);
            if len_sq > best_len_sq {
                best = pair;
                best_len_sq = len_sq;
            }
        }
        return best.to_vec();
    }

    if area > 0.0 {
        vec![a, b, c]
    } else {
        vec![a, c, b]
    }
}

fn point3_is_finite(point: [f32; 3]) -> bool {
    point[0].is_finite() && point[1].is_finite() && point[2].is_finite()
}

fn mesh_instance_bounds(geometry: &GameplayQueryMeshGeometry, transform: &[f32; 16]) -> Aabb3 {
    let mut bounds: Option<Aabb3> = None;
    for vertex in &geometry.vertices {
        let [x, y, z] = transform_point(transform, *vertex);
        bounds = Some(match bounds {
            Some(bounds) => Aabb3 {
                min_x: bounds.min_x.min(x),
                min_y: bounds.min_y.min(y),
                min_z: bounds.min_z.min(z),
                max_x: bounds.max_x.max(x),
                max_y: bounds.max_y.max(y),
                max_z: bounds.max_z.max(z),
            },
            None => Aabb3 {
                min_x: x,
                min_y: y,
                min_z: z,
                max_x: x,
                max_y: y,
                max_z: z,
            },
        });
    }
    bounds.expect("query mesh bounds should exist for non-empty geometry")
}

fn transform_point(transform: &[f32; 16], point: [f32; 3]) -> [f32; 3] {
    // Future mesh narrowphase code must account for mirrored transforms before using winding for
    // one-sided ray tests; this helper intentionally only applies the authored affine transform.
    [
        transform[0] * point[0] + transform[1] * point[1] + transform[2] * point[2] + transform[3],
        transform[4] * point[0] + transform[5] * point[1] + transform[6] * point[2] + transform[7],
        transform[8] * point[0]
            + transform[9] * point[1]
            + transform[10] * point[2]
            + transform[11],
    ]
}

fn triangle_area_squared(a: [f32; 3], b: [f32; 3], c: [f32; 3]) -> f32 {
    let ab = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
    let ac = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
    let cross = [
        ab[1] * ac[2] - ab[2] * ac[1],
        ab[2] * ac[0] - ab[0] * ac[2],
        ab[0] * ac[1] - ab[1] * ac[0],
    ];
    cross[0] * cross[0] + cross[1] * cross[1] + cross[2] * cross[2]
}

fn assert_no_full_rotation_movement_boxes(boxes: &[GameplayCollisionBox], source: &str) {
    assert!(
        !boxes
            .iter()
            .any(|collider| matches!(collider, GameplayCollisionBox::ObbXyz { .. })),
        "{source} contains obb_xyz boxes; full-rotation boxes are query-only until movement pushout supports true OBBs"
    );
}

fn quaternion_to_axes(rotation: &[f32]) -> Option<(f32, f32, f32, f32, f32, f32, f32, f32, f32)> {
    if rotation.len() < 4 {
        return None;
    }

    let x = rotation[0];
    let y = rotation[1];
    let z = rotation[2];
    let w = rotation[3];
    if !x.is_finite() || !y.is_finite() || !z.is_finite() || !w.is_finite() {
        return None;
    }

    let length_squared = x * x + y * y + z * z + w * w;
    if length_squared <= COLLISION_EPSILON {
        return None;
    }

    let inv_length = 1.0 / length_squared.sqrt();
    let x = x * inv_length;
    let y = y * inv_length;
    let z = z * inv_length;
    let w = w * inv_length;

    let xx = x * x;
    let yy = y * y;
    let zz = z * z;
    let xy = x * y;
    let xz = x * z;
    let yz = y * z;
    let wx = w * x;
    let wy = w * y;
    let wz = w * z;

    Some((
        1.0 - 2.0 * (yy + zz),
        2.0 * (xy + wz),
        2.0 * (xz - wy),
        2.0 * (xy - wz),
        1.0 - 2.0 * (xx + zz),
        2.0 * (yz + wx),
        2.0 * (xz + wy),
        2.0 * (yz - wx),
        1.0 - 2.0 * (xx + yy),
    ))
}

impl Aabb3 {
    fn from_gameplay_box(collider: GameplayCollisionBox) -> Self {
        match collider {
            GameplayCollisionBox::Aabb {
                center_x,
                center_y,
                center_z,
                half_x,
                half_y,
                half_z,
            } => Self {
                min_x: center_x - half_x,
                min_y: center_y - half_y,
                min_z: center_z - half_z,
                max_x: center_x + half_x,
                max_y: center_y + half_y,
                max_z: center_z + half_z,
            },
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
                let world_half_x = cos_y.abs() * half_x + sin_y.abs() * half_z;
                let world_half_z = sin_y.abs() * half_x + cos_y.abs() * half_z;
                Self {
                    min_x: center_x - world_half_x,
                    min_y: center_y - half_y,
                    min_z: center_z - world_half_z,
                    max_x: center_x + world_half_x,
                    max_y: center_y + half_y,
                    max_z: center_z + world_half_z,
                }
            }
            GameplayCollisionBox::ObbXyz {
                center_x,
                center_y,
                center_z,
                half_x,
                half_y,
                half_z,
                axis_x_x,
                axis_x_y,
                axis_x_z,
                axis_y_x,
                axis_y_y,
                axis_y_z,
                axis_z_x,
                axis_z_y,
                axis_z_z,
            } => {
                let world_half_x =
                    axis_x_x.abs() * half_x + axis_y_x.abs() * half_y + axis_z_x.abs() * half_z;
                let world_half_y =
                    axis_x_y.abs() * half_x + axis_y_y.abs() * half_y + axis_z_y.abs() * half_z;
                let world_half_z =
                    axis_x_z.abs() * half_x + axis_y_z.abs() * half_y + axis_z_z.abs() * half_z;
                Self {
                    min_x: center_x - world_half_x,
                    min_y: center_y - world_half_y,
                    min_z: center_z - world_half_z,
                    max_x: center_x + world_half_x,
                    max_y: center_y + world_half_y,
                    max_z: center_z + world_half_z,
                }
            }
        }
    }

    fn from_raycast_request(request: WorldRaycastRequest) -> Option<Self> {
        if !request.origin_x.is_finite()
            || !request.origin_y.is_finite()
            || !request.origin_z.is_finite()
            || !request.dir_x.is_finite()
            || !request.dir_y.is_finite()
            || !request.dir_z.is_finite()
            || !request.max_distance.is_finite()
            || !request.radius.is_finite()
            || request.max_distance < 0.0
        {
            return None;
        }

        let end_x = request.origin_x + request.dir_x * request.max_distance;
        let end_y = request.origin_y + request.dir_y * request.max_distance;
        let end_z = request.origin_z + request.dir_z * request.max_distance;
        if !end_x.is_finite() || !end_y.is_finite() || !end_z.is_finite() {
            return None;
        }

        let radius = request.radius.max(0.0);
        Some(Self {
            min_x: request.origin_x.min(end_x) - radius,
            min_y: request.origin_y.min(end_y) - radius,
            min_z: request.origin_z.min(end_z) - radius,
            max_x: request.origin_x.max(end_x) + radius,
            max_y: request.origin_y.max(end_y) + radius,
            max_z: request.origin_z.max(end_z) + radius,
        })
    }

    fn is_finite(self) -> bool {
        self.min_x.is_finite()
            && self.min_y.is_finite()
            && self.min_z.is_finite()
            && self.max_x.is_finite()
            && self.max_y.is_finite()
            && self.max_z.is_finite()
            && self.min_x <= self.max_x
            && self.min_y <= self.max_y
            && self.min_z <= self.max_z
    }

    fn max_extent(self) -> f32 {
        (self.max_x - self.min_x)
            .max(self.max_y - self.min_y)
            .max(self.max_z - self.min_z)
    }

    fn union(self, other: Self) -> Self {
        Self {
            min_x: self.min_x.min(other.min_x),
            min_y: self.min_y.min(other.min_y),
            min_z: self.min_z.min(other.min_z),
            max_x: self.max_x.max(other.max_x),
            max_y: self.max_y.max(other.max_y),
            max_z: self.max_z.max(other.max_z),
        }
    }

    fn expanded(self, amount: f32) -> Self {
        if !amount.is_finite() || amount <= 0.0 {
            return self;
        }
        Self {
            min_x: self.min_x - amount,
            min_y: self.min_y - amount,
            min_z: self.min_z - amount,
            max_x: self.max_x + amount,
            max_y: self.max_y + amount,
            max_z: self.max_z + amount,
        }
    }

    fn from_triangle(a: [f32; 3], b: [f32; 3], c: [f32; 3]) -> Self {
        Self {
            min_x: a[0].min(b[0]).min(c[0]),
            min_y: a[1].min(b[1]).min(c[1]),
            min_z: a[2].min(b[2]).min(c[2]),
            max_x: a[0].max(b[0]).max(c[0]),
            max_y: a[1].max(b[1]).max(c[1]),
            max_z: a[2].max(b[2]).max(c[2]),
        }
    }

    fn extent_axis(self) -> usize {
        let x = self.max_x - self.min_x;
        let y = self.max_y - self.min_y;
        let z = self.max_z - self.min_z;
        if x >= y && x >= z {
            0
        } else if y >= z {
            1
        } else {
            2
        }
    }
}

impl GameplayQueryMeshBvh {
    fn build(vertices: &[[f32; 3]], indices: &[usize]) -> Self {
        let triangle_count = indices.len() / 3;
        let mut triangle_bounds = Vec::with_capacity(triangle_count);
        let mut centroids = Vec::with_capacity(triangle_count);
        for triangle in indices.chunks_exact(3) {
            let a = vertices[triangle[0]];
            let b = vertices[triangle[1]];
            let c = vertices[triangle[2]];
            triangle_bounds.push(Aabb3::from_triangle(a, b, c));
            centroids.push([
                (a[0] + b[0] + c[0]) / 3.0,
                (a[1] + b[1] + c[1]) / 3.0,
                (a[2] + b[2] + c[2]) / 3.0,
            ]);
        }

        let mut ordered_triangles: Vec<usize> = (0..triangle_count).collect();
        let mut bvh = Self {
            nodes: Vec::with_capacity(triangle_count.saturating_mul(2).saturating_sub(1)),
            triangle_indices: Vec::with_capacity(triangle_count),
        };
        if triangle_count > 0 {
            build_query_mesh_bvh_node(
                &mut bvh.nodes,
                &mut bvh.triangle_indices,
                &mut ordered_triangles,
                &triangle_bounds,
                &centroids,
            );
        }
        bvh
    }
}

fn build_query_mesh_bvh_node(
    nodes: &mut Vec<GameplayQueryMeshBvhNode>,
    leaf_triangles: &mut Vec<usize>,
    ordered_triangles: &mut [usize],
    triangle_bounds: &[Aabb3],
    centroids: &[[f32; 3]],
) -> usize {
    let mut bounds = triangle_bounds[ordered_triangles[0]];
    let mut centroid_bounds = Aabb3 {
        min_x: centroids[ordered_triangles[0]][0],
        min_y: centroids[ordered_triangles[0]][1],
        min_z: centroids[ordered_triangles[0]][2],
        max_x: centroids[ordered_triangles[0]][0],
        max_y: centroids[ordered_triangles[0]][1],
        max_z: centroids[ordered_triangles[0]][2],
    };
    for triangle_index in ordered_triangles.iter().copied().skip(1) {
        bounds = bounds.union(triangle_bounds[triangle_index]);
        let centroid = centroids[triangle_index];
        centroid_bounds = centroid_bounds.union(Aabb3 {
            min_x: centroid[0],
            min_y: centroid[1],
            min_z: centroid[2],
            max_x: centroid[0],
            max_y: centroid[1],
            max_z: centroid[2],
        });
    }

    let node_index = nodes.len();
    nodes.push(GameplayQueryMeshBvhNode {
        bounds,
        left: usize::MAX,
        right: usize::MAX,
        first_triangle: 0,
        triangle_count: 0,
    });

    if ordered_triangles.len() <= QUERY_MESH_BVH_LEAF_TRIANGLE_COUNT {
        let first_triangle = leaf_triangles.len();
        leaf_triangles.extend_from_slice(ordered_triangles);
        nodes[node_index] = GameplayQueryMeshBvhNode {
            bounds,
            left: usize::MAX,
            right: usize::MAX,
            first_triangle,
            triangle_count: ordered_triangles.len(),
        };
        return node_index;
    }

    let axis = centroid_bounds.extent_axis();
    ordered_triangles.sort_by(|a, b| {
        centroids[*a][axis]
            .total_cmp(&centroids[*b][axis])
            .then_with(|| a.cmp(b))
    });
    let split = ordered_triangles.len() / 2;
    let (left_triangles, right_triangles) = ordered_triangles.split_at_mut(split);
    let left = build_query_mesh_bvh_node(
        nodes,
        leaf_triangles,
        left_triangles,
        triangle_bounds,
        centroids,
    );
    let right = build_query_mesh_bvh_node(
        nodes,
        leaf_triangles,
        right_triangles,
        triangle_bounds,
        centroids,
    );
    nodes[node_index] = GameplayQueryMeshBvhNode {
        bounds,
        left,
        right,
        first_triangle: 0,
        triangle_count: 0,
    };
    node_index
}

impl GameplayBoxBroadphase {
    fn build(colliders: &[GameplayCollisionBox]) -> Self {
        Self::build_from_aabbs(
            colliders.len(),
            colliders
                .iter()
                .map(|collider| Aabb3::from_gameplay_box(*collider)),
        )
    }

    fn build_from_aabbs(
        collider_count: usize,
        bounds_iter: impl IntoIterator<Item = Aabb3>,
    ) -> Self {
        let bounds: Vec<Aabb3> = bounds_iter.into_iter().collect();
        let mut extents = Vec::with_capacity(bounds.len());
        for aabb in &bounds {
            if aabb.is_finite() {
                extents.push(aabb.max_extent());
            }
        }
        extents.sort_by(|a, b| a.total_cmp(b));
        let median_extent = extents
            .get(extents.len().saturating_sub(1) / 2)
            .copied()
            .unwrap_or(OPEN_WORLD_OCCUPANCY_CELL_SIZE);
        let cell_size = (median_extent * 2.0).clamp(
            GAMEPLAY_BROADPHASE_MIN_CELL_SIZE,
            GAMEPLAY_BROADPHASE_MAX_CELL_SIZE,
        );

        let mut cells: HashMap<(i32, i32, i32), Vec<usize>> = HashMap::new();
        let mut max_cells_per_collider = 0usize;
        let mut unindexed_collider_count = 0usize;
        for (index, aabb) in bounds.into_iter().enumerate() {
            if !aabb.is_finite() {
                unindexed_collider_count = unindexed_collider_count.saturating_add(1);
                continue;
            }

            let Some((min_x, min_y, min_z, max_x, max_y, max_z)) =
                cell_range_for_aabb(aabb, cell_size)
            else {
                unindexed_collider_count = unindexed_collider_count.saturating_add(1);
                continue;
            };

            let cells_x = i64::from(max_x) - i64::from(min_x) + 1;
            let cells_y = i64::from(max_y) - i64::from(min_y) + 1;
            let cells_z = i64::from(max_z) - i64::from(min_z) + 1;
            let cells_for_collider = cells_x
                .checked_mul(cells_y)
                .and_then(|count| count.checked_mul(cells_z))
                .and_then(|count| usize::try_from(count).ok())
                .unwrap_or(usize::MAX);
            max_cells_per_collider = max_cells_per_collider.max(cells_for_collider);

            for cell_x in min_x..=max_x {
                for cell_y in min_y..=max_y {
                    for cell_z in min_z..=max_z {
                        cells
                            .entry((cell_x, cell_y, cell_z))
                            .or_default()
                            .push(index);
                    }
                }
            }
        }
        let index_entries = cells.values().map(Vec::len).sum();
        let max_cell_occupancy = cells.values().map(Vec::len).max().unwrap_or(0);

        Self {
            cell_size,
            cells,
            collider_count,
            index_entries,
            max_cell_occupancy,
            max_cells_per_collider,
            unindexed_collider_count,
        }
    }

    fn query_into(&self, request: WorldRaycastRequest, candidates: &mut Vec<usize>) -> bool {
        let Some(query_bounds) = Aabb3::from_raycast_request(request) else {
            return false;
        };
        self.query_aabb_into(query_bounds, candidates)
    }

    fn query_aabb_into(&self, query_bounds: Aabb3, candidates: &mut Vec<usize>) -> bool {
        candidates.clear();
        if self.collider_count == 0 {
            return true;
        }
        if self.unindexed_collider_count > 0 {
            return false;
        }
        if !query_bounds.is_finite() {
            return false;
        }

        let Some((min_x, min_y, min_z, max_x, max_y, max_z)) =
            cell_range_for_aabb(query_bounds, self.cell_size)
        else {
            return false;
        };
        let cell_count_x = i64::from(max_x) - i64::from(min_x) + 1;
        let cell_count_y = i64::from(max_y) - i64::from(min_y) + 1;
        let cell_count_z = i64::from(max_z) - i64::from(min_z) + 1;
        let Some(cell_count) = cell_count_x
            .checked_mul(cell_count_y)
            .and_then(|count| count.checked_mul(cell_count_z))
        else {
            return false;
        };
        if cell_count > GAMEPLAY_BROADPHASE_FALLBACK_CELL_COUNT {
            return false;
        }

        for cell_x in min_x..=max_x {
            for cell_y in min_y..=max_y {
                for cell_z in min_z..=max_z {
                    if let Some(indices) = self.cells.get(&(cell_x, cell_y, cell_z)) {
                        candidates.extend(indices.iter().copied());
                    }
                }
            }
        }

        candidates.sort_unstable();
        candidates.dedup();

        if candidates.len() * GAMEPLAY_BROADPHASE_FALLBACK_CANDIDATE_DIVISOR > self.collider_count {
            candidates.clear();
            return false;
        }

        true
    }
}

fn cell_range_for_aabb(aabb: Aabb3, cell_size: f32) -> Option<(i32, i32, i32, i32, i32, i32)> {
    if !aabb.is_finite() || !cell_size.is_finite() || cell_size <= 0.0 {
        return None;
    }

    let min_x = cell_coord(aabb.min_x, cell_size)?;
    let min_y = cell_coord(aabb.min_y, cell_size)?;
    let min_z = cell_coord(aabb.min_z, cell_size)?;
    let max_x = cell_coord(aabb.max_x, cell_size)?;
    let max_y = cell_coord(aabb.max_y, cell_size)?;
    let max_z = cell_coord(aabb.max_z, cell_size)?;
    Some((min_x, min_y, min_z, max_x, max_y, max_z))
}

fn cell_coord(value: f32, cell_size: f32) -> Option<i32> {
    let cell = (value / cell_size).floor();
    if cell < i32::MIN as f32 || cell > i32::MAX as f32 {
        return None;
    }
    Some(cell as i32)
}

fn raycast_gameplay_collision_boxes(
    best: &mut Option<WorldRayHit>,
    request: WorldRaycastRequest,
    colliders: &[GameplayCollisionBox],
    broadphase: &GameplayBoxBroadphase,
    stats: Option<&mut WorldRaycastStats>,
) {
    let mut stats = stats;
    let broadphase_hit_tested = GAMEPLAY_BROADPHASE_CANDIDATE_SCRATCH.with(|scratch| {
        let mut candidate_indices = scratch.borrow_mut();
        if broadphase.query_into(request, &mut candidate_indices) {
            if let Some(stats) = stats.as_deref_mut() {
                let candidate_count = candidate_indices.len().min(u32::MAX as usize) as u32;
                stats.world_gameplay_broadphase_candidates = stats
                    .world_gameplay_broadphase_candidates
                    .saturating_add(candidate_count);
                stats.world_gameplay_narrowphase_tests = stats
                    .world_gameplay_narrowphase_tests
                    .saturating_add(candidate_count);
            }
            for index in candidate_indices.iter().copied() {
                if let Some(collider) = colliders.get(index) {
                    try_world_gameplay_box_hit(best, request, *collider);
                }
            }
            true
        } else {
            false
        }
    });
    if broadphase_hit_tested {
        return;
    }

    if let Some(stats) = stats.as_deref_mut() {
        let collider_count = colliders.len().min(u32::MAX as usize) as u32;
        stats.world_gameplay_full_scan_fallbacks =
            stats.world_gameplay_full_scan_fallbacks.saturating_add(1);
        stats.world_gameplay_narrowphase_tests = stats
            .world_gameplay_narrowphase_tests
            .saturating_add(collider_count);
    }
    for collider in colliders {
        try_world_gameplay_box_hit(best, request, *collider);
    }
}

fn raycast_gameplay_query_meshes(
    best: &mut Option<WorldRayHit>,
    request: WorldRaycastRequest,
    meshes: &GameplayQueryMeshSet,
    broadphase: &GameplayBoxBroadphase,
    stats: Option<&mut WorldRaycastStats>,
) {
    if meshes.instances.is_empty() || meshes.geometries.is_empty() {
        return;
    }

    let mut stats = stats;
    let broadphase_hit_tested = GAMEPLAY_BROADPHASE_CANDIDATE_SCRATCH.with(|scratch| {
        let mut candidate_indices = scratch.borrow_mut();
        if broadphase.query_into(request, &mut candidate_indices) {
            if let Some(stats) = stats.as_deref_mut() {
                let candidate_count = candidate_indices.len().min(u32::MAX as usize) as u32;
                stats.world_query_mesh_broadphase_candidates = stats
                    .world_query_mesh_broadphase_candidates
                    .saturating_add(candidate_count);
            }
            for index in candidate_indices.iter().copied() {
                if let Some(instance) = meshes.instances.get(index) {
                    try_world_gameplay_mesh_instance_hit(
                        best,
                        request,
                        meshes,
                        instance,
                        stats.as_deref_mut(),
                    );
                }
            }
            true
        } else {
            false
        }
    });
    if broadphase_hit_tested {
        return;
    }

    if let Some(stats) = stats.as_deref_mut() {
        stats.world_query_mesh_full_scan_fallbacks =
            stats.world_query_mesh_full_scan_fallbacks.saturating_add(1);
    }
    for instance in &meshes.instances {
        try_world_gameplay_mesh_instance_hit(best, request, meshes, instance, stats.as_deref_mut());
    }
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

fn push_out_convex_footprint_2d(
    x: f32,
    z: f32,
    footprint: &[[f32; 2]],
    padding: f32,
) -> (f32, f32) {
    if footprint.len() < 2 || !padding.is_finite() || padding < 0.0 {
        return (x, z);
    }
    if footprint.len() == 2 {
        return push_out_segment_2d(x, z, footprint[0], footprint[1], padding);
    }

    let area = polygon_area_signed(footprint);
    if area.abs() <= COLLISION_EPSILON {
        return (x, z);
    }

    let ccw = area > 0.0;
    let mut inside = true;
    let mut best_inside_push = (f32::INFINITY, 0.0, 0.0);
    let mut best_outside_distance_sq = f32::INFINITY;
    let mut best_outside_delta = (0.0, 0.0);
    let mut best_outside_normal = (0.0, 0.0);

    for index in 0..footprint.len() {
        let a = footprint[index];
        let b = footprint[(index + 1) % footprint.len()];
        let edge_x = b[0] - a[0];
        let edge_z = b[1] - a[1];
        let edge_len_sq = edge_x * edge_x + edge_z * edge_z;
        if edge_len_sq <= COLLISION_EPSILON {
            continue;
        }

        let edge_len = edge_len_sq.sqrt();
        let outward = if ccw {
            [edge_z / edge_len, -edge_x / edge_len]
        } else {
            [-edge_z / edge_len, edge_x / edge_len]
        };
        let signed_outward_distance = (x - a[0]) * outward[0] + (z - a[1]) * outward[1];
        if signed_outward_distance > COLLISION_EPSILON {
            inside = false;
        }

        let push_amount = padding - signed_outward_distance;
        if push_amount >= 0.0 && push_amount < best_inside_push.0 {
            best_inside_push = (push_amount, outward[0], outward[1]);
        }

        let segment_t = (((x - a[0]) * edge_x + (z - a[1]) * edge_z) / edge_len_sq).clamp(0.0, 1.0);
        let closest_x = a[0] + edge_x * segment_t;
        let closest_z = a[1] + edge_z * segment_t;
        let delta_x = x - closest_x;
        let delta_z = z - closest_z;
        let distance_sq = delta_x * delta_x + delta_z * delta_z;
        if distance_sq < best_outside_distance_sq {
            best_outside_distance_sq = distance_sq;
            best_outside_delta = (delta_x, delta_z);
            best_outside_normal = (outward[0], outward[1]);
        }
    }

    if inside {
        if best_inside_push.0.is_finite() {
            return (
                x + best_inside_push.1 * best_inside_push.0,
                z + best_inside_push.2 * best_inside_push.0,
            );
        }
        return (x, z);
    }

    let padding_sq = padding * padding;
    if best_outside_distance_sq >= padding_sq {
        return (x, z);
    }

    if best_outside_distance_sq <= COLLISION_EPSILON {
        return (
            x + best_outside_normal.0 * padding,
            z + best_outside_normal.1 * padding,
        );
    }

    let distance = best_outside_distance_sq.sqrt();
    let push_amount = padding - distance;
    (
        x + best_outside_delta.0 / distance * push_amount,
        z + best_outside_delta.1 / distance * push_amount,
    )
}

fn convex_footprint_overlaps_point_2d(
    footprint: &[[f32; 2]],
    x: f32,
    z: f32,
    padding: f32,
) -> bool {
    if footprint.len() < 2 || !padding.is_finite() || padding < 0.0 {
        return false;
    }
    if footprint.len() == 2 {
        return segment_overlaps_point_2d(footprint[0], footprint[1], x, z, padding);
    }

    let area = polygon_area_signed(footprint);
    if area.abs() <= COLLISION_EPSILON {
        return false;
    }

    let ccw = area > 0.0;
    let mut inside = true;
    let mut best_outside_distance_sq = f32::INFINITY;

    for index in 0..footprint.len() {
        let a = footprint[index];
        let b = footprint[(index + 1) % footprint.len()];
        let edge_x = b[0] - a[0];
        let edge_z = b[1] - a[1];
        let edge_len_sq = edge_x * edge_x + edge_z * edge_z;
        if edge_len_sq <= COLLISION_EPSILON {
            continue;
        }

        let edge_len = edge_len_sq.sqrt();
        let outward = if ccw {
            [edge_z / edge_len, -edge_x / edge_len]
        } else {
            [-edge_z / edge_len, edge_x / edge_len]
        };
        let signed_outward_distance = (x - a[0]) * outward[0] + (z - a[1]) * outward[1];
        if signed_outward_distance > COLLISION_EPSILON {
            inside = false;
        }

        let segment_t = (((x - a[0]) * edge_x + (z - a[1]) * edge_z) / edge_len_sq).clamp(0.0, 1.0);
        let closest_x = a[0] + edge_x * segment_t;
        let closest_z = a[1] + edge_z * segment_t;
        let delta_x = x - closest_x;
        let delta_z = z - closest_z;
        best_outside_distance_sq =
            best_outside_distance_sq.min(delta_x * delta_x + delta_z * delta_z);
    }

    inside || best_outside_distance_sq < padding * padding
}

fn push_out_segment_2d(x: f32, z: f32, a: [f32; 2], b: [f32; 2], padding: f32) -> (f32, f32) {
    let edge_x = b[0] - a[0];
    let edge_z = b[1] - a[1];
    let edge_len_sq = edge_x * edge_x + edge_z * edge_z;
    if edge_len_sq <= COLLISION_EPSILON {
        return (x, z);
    }

    let t = (((x - a[0]) * edge_x + (z - a[1]) * edge_z) / edge_len_sq).clamp(0.0, 1.0);
    let closest_x = a[0] + edge_x * t;
    let closest_z = a[1] + edge_z * t;
    let delta_x = x - closest_x;
    let delta_z = z - closest_z;
    let distance_sq = delta_x * delta_x + delta_z * delta_z;
    let padding_sq = padding * padding;
    if distance_sq >= padding_sq {
        return (x, z);
    }

    if distance_sq <= COLLISION_EPSILON {
        let edge_len = edge_len_sq.sqrt();
        let normal_x = edge_z / edge_len;
        let normal_z = -edge_x / edge_len;
        return (x + normal_x * padding, z + normal_z * padding);
    }

    let distance = distance_sq.sqrt();
    let push_amount = padding - distance;
    (
        x + delta_x / distance * push_amount,
        z + delta_z / distance * push_amount,
    )
}

fn segment_overlaps_point_2d(a: [f32; 2], b: [f32; 2], x: f32, z: f32, padding: f32) -> bool {
    let edge_x = b[0] - a[0];
    let edge_z = b[1] - a[1];
    let edge_len_sq = edge_x * edge_x + edge_z * edge_z;
    if edge_len_sq <= COLLISION_EPSILON {
        return false;
    }

    let t = (((x - a[0]) * edge_x + (z - a[1]) * edge_z) / edge_len_sq).clamp(0.0, 1.0);
    let closest_x = a[0] + edge_x * t;
    let closest_z = a[1] + edge_z * t;
    let delta_x = x - closest_x;
    let delta_z = z - closest_z;
    delta_x * delta_x + delta_z * delta_z < padding * padding
}

fn distance_sq_2d(a: [f32; 2], b: [f32; 2]) -> f32 {
    let dx = a[0] - b[0];
    let dz = a[1] - b[1];
    dx * dx + dz * dz
}

fn resolve_swept_convex_footprint_2d(
    footprint: &[[f32; 2]],
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    padding: f32,
) -> Option<(f32, f32)> {
    let target_overlaps =
        convex_footprint_overlaps_point_2d(footprint, target_x, target_z, padding);
    if !target_overlaps {
        return None;
    }

    let start_overlaps = convex_footprint_overlaps_point_2d(footprint, start_x, start_z, padding);
    if !start_overlaps {
        return Some((start_x, start_z));
    }

    Some(push_out_convex_footprint_2d(
        target_x, target_z, footprint, padding,
    ))
}

fn polygon_area_signed(points: &[[f32; 2]]) -> f32 {
    if points.len() < 3 {
        return 0.0;
    }

    let mut area = 0.0;
    for index in 0..points.len() {
        let a = points[index];
        let b = points[(index + 1) % points.len()];
        area += a[0] * b[1] - b[0] * a[1];
    }
    area * 0.5
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
        GameplayCollisionBox::ObbXyz {
            center_x,
            center_y,
            center_z,
            half_x,
            half_y,
            half_z,
            axis_x_x,
            axis_x_y,
            axis_x_z,
            axis_y_x,
            axis_y_y,
            axis_y_z,
            axis_z_x,
            axis_z_y,
            axis_z_z,
        } => {
            let rel_origin_x = request.origin_x - center_x;
            let rel_origin_y = request.origin_y - center_y;
            let rel_origin_z = request.origin_z - center_z;

            let local_origin_x =
                rel_origin_x * axis_x_x + rel_origin_y * axis_x_y + rel_origin_z * axis_x_z;
            let local_origin_y =
                rel_origin_x * axis_y_x + rel_origin_y * axis_y_y + rel_origin_z * axis_y_z;
            let local_origin_z =
                rel_origin_x * axis_z_x + rel_origin_y * axis_z_y + rel_origin_z * axis_z_z;

            let local_dir_x =
                request.dir_x * axis_x_x + request.dir_y * axis_x_y + request.dir_z * axis_x_z;
            let local_dir_y =
                request.dir_x * axis_y_x + request.dir_y * axis_y_y + request.dir_z * axis_y_z;
            let local_dir_z =
                request.dir_x * axis_z_x + request.dir_y * axis_z_y + request.dir_z * axis_z_z;

            raycast_centered_aabb(
                local_origin_x,
                local_origin_y,
                local_origin_z,
                local_dir_x,
                local_dir_y,
                local_dir_z,
                request.max_distance,
                half_x + request.radius,
                half_y + request.radius,
                half_z + request.radius,
            )
        }
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

fn try_world_gameplay_mesh_instance_hit(
    best: &mut Option<WorldRayHit>,
    request: WorldRaycastRequest,
    meshes: &GameplayQueryMeshSet,
    instance: &GameplayQueryMeshInstance,
    stats: Option<&mut WorldRaycastStats>,
) {
    let Some(geometry) = meshes.geometries.get(instance.geometry_index) else {
        return;
    };
    let Some(inverse_transform) = invert_affine_transform(&instance.transform) else {
        return;
    };
    let local_origin = transform_point(
        &inverse_transform,
        [request.origin_x, request.origin_y, request.origin_z],
    );
    let local_dir = transform_vector(
        &inverse_transform,
        [request.dir_x, request.dir_y, request.dir_z],
    );
    let local_radius = local_radius_for_world_sphere(&inverse_transform, request.radius.max(0.0));

    let mut closest_t = best.map(|hit| hit.t).unwrap_or(request.max_distance);
    if closest_t > request.max_distance {
        closest_t = request.max_distance;
    }
    let mut stats = stats;
    let hit_t = raycast_query_mesh_geometry_bvh(
        geometry,
        &instance.transform,
        [request.origin_x, request.origin_y, request.origin_z],
        [request.dir_x, request.dir_y, request.dir_z],
        local_origin,
        local_dir,
        request.radius.max(0.0),
        local_radius,
        closest_t,
        stats.as_deref_mut(),
    );

    let Some(t) = hit_t else {
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

fn raycast_query_mesh_geometry_bvh(
    geometry: &GameplayQueryMeshGeometry,
    instance_transform: &[f32; 16],
    world_origin: [f32; 3],
    world_dir: [f32; 3],
    local_origin: [f32; 3],
    local_dir: [f32; 3],
    radius: f32,
    local_radius: f32,
    max_distance: f32,
    stats: Option<&mut WorldRaycastStats>,
) -> Option<f32> {
    if geometry.bvh.nodes.is_empty() {
        return None;
    }
    let mut stats = stats;
    let mut closest_t = max_distance;
    let mut hit_t = None;
    if raycast_query_mesh_bvh_node_bounds(
        geometry,
        0,
        local_origin,
        local_dir,
        local_radius,
        closest_t,
        &mut stats,
    )
    .is_some()
    {
        raycast_query_mesh_bvh_node_after_bounds_hit(
            geometry,
            instance_transform,
            world_origin,
            world_dir,
            0,
            local_origin,
            local_dir,
            radius,
            local_radius,
            &mut closest_t,
            &mut hit_t,
            &mut stats,
        );
    }
    hit_t
}

fn raycast_query_mesh_bvh_node_bounds(
    geometry: &GameplayQueryMeshGeometry,
    node_index: usize,
    local_origin: [f32; 3],
    local_dir: [f32; 3],
    local_radius: f32,
    max_distance: f32,
    stats: &mut Option<&mut WorldRaycastStats>,
) -> Option<f32> {
    if let Some(stats) = stats.as_deref_mut() {
        stats.world_query_mesh_bvh_node_tests =
            stats.world_query_mesh_bvh_node_tests.saturating_add(1);
    }
    raycast_aabb_bounds(
        local_origin,
        local_dir,
        geometry.bvh.nodes[node_index]
            .bounds
            .expanded(local_radius.max(0.0)),
        max_distance,
    )
}

fn raycast_query_mesh_bvh_node_after_bounds_hit(
    geometry: &GameplayQueryMeshGeometry,
    instance_transform: &[f32; 16],
    world_origin: [f32; 3],
    world_dir: [f32; 3],
    node_index: usize,
    local_origin: [f32; 3],
    local_dir: [f32; 3],
    radius: f32,
    local_radius: f32,
    closest_t: &mut f32,
    hit_t: &mut Option<f32>,
    stats: &mut Option<&mut WorldRaycastStats>,
) {
    let node = geometry.bvh.nodes[node_index];

    if node.triangle_count > 0 {
        for triangle_slot in
            node.first_triangle..node.first_triangle.saturating_add(node.triangle_count)
        {
            let triangle_index = geometry.bvh.triangle_indices[triangle_slot];
            if let Some(stats) = stats.as_deref_mut() {
                stats.world_query_mesh_triangles_tested =
                    stats.world_query_mesh_triangles_tested.saturating_add(1);
            }
            let offset = triangle_index * 3;
            let triangle = &geometry.indices[offset..offset + 3];
            let a = geometry.vertices[triangle[0]];
            let b = geometry.vertices[triangle[1]];
            let c = geometry.vertices[triangle[2]];
            let t = if radius > COLLISION_EPSILON {
                raycast_swept_sphere_triangle(
                    world_origin,
                    world_dir,
                    radius,
                    transform_point(instance_transform, a),
                    transform_point(instance_transform, b),
                    transform_point(instance_transform, c),
                    *closest_t,
                )
            } else {
                raycast_triangle(local_origin, local_dir, a, b, c)
            };
            let Some(t) = t else { continue };
            if t >= 0.0 && t <= *closest_t {
                *closest_t = t;
                *hit_t = Some(t);
            }
        }
        return;
    }

    let left_t = raycast_query_mesh_bvh_node_bounds(
        geometry,
        node.left,
        local_origin,
        local_dir,
        local_radius,
        *closest_t,
        stats,
    );
    let right_t = raycast_query_mesh_bvh_node_bounds(
        geometry,
        node.right,
        local_origin,
        local_dir,
        local_radius,
        *closest_t,
        stats,
    );

    match (left_t, right_t) {
        (Some(left_t), Some(right_t)) => {
            if left_t <= right_t {
                raycast_query_mesh_bvh_node_after_bounds_hit(
                    geometry,
                    instance_transform,
                    world_origin,
                    world_dir,
                    node.left,
                    local_origin,
                    local_dir,
                    radius,
                    local_radius,
                    closest_t,
                    hit_t,
                    stats,
                );
                raycast_query_mesh_bvh_node_after_bounds_hit(
                    geometry,
                    instance_transform,
                    world_origin,
                    world_dir,
                    node.right,
                    local_origin,
                    local_dir,
                    radius,
                    local_radius,
                    closest_t,
                    hit_t,
                    stats,
                );
            } else {
                raycast_query_mesh_bvh_node_after_bounds_hit(
                    geometry,
                    instance_transform,
                    world_origin,
                    world_dir,
                    node.right,
                    local_origin,
                    local_dir,
                    radius,
                    local_radius,
                    closest_t,
                    hit_t,
                    stats,
                );
                raycast_query_mesh_bvh_node_after_bounds_hit(
                    geometry,
                    instance_transform,
                    world_origin,
                    world_dir,
                    node.left,
                    local_origin,
                    local_dir,
                    radius,
                    local_radius,
                    closest_t,
                    hit_t,
                    stats,
                );
            }
        }
        (Some(_), None) => raycast_query_mesh_bvh_node_after_bounds_hit(
            geometry,
            instance_transform,
            world_origin,
            world_dir,
            node.left,
            local_origin,
            local_dir,
            radius,
            local_radius,
            closest_t,
            hit_t,
            stats,
        ),
        (None, Some(_)) => raycast_query_mesh_bvh_node_after_bounds_hit(
            geometry,
            instance_transform,
            world_origin,
            world_dir,
            node.right,
            local_origin,
            local_dir,
            radius,
            local_radius,
            closest_t,
            hit_t,
            stats,
        ),
        (None, None) => {}
    }
}

#[cfg(test)]
fn raycast_query_mesh_geometry_linear(
    geometry: &GameplayQueryMeshGeometry,
    local_origin: [f32; 3],
    local_dir: [f32; 3],
    max_distance: f32,
) -> Option<f32> {
    let mut closest_t = max_distance;
    let mut hit_t = None;
    for triangle in geometry.indices.chunks_exact(3) {
        let a = geometry.vertices[triangle[0]];
        let b = geometry.vertices[triangle[1]];
        let c = geometry.vertices[triangle[2]];
        let Some(t) = raycast_triangle(local_origin, local_dir, a, b, c) else {
            continue;
        };
        if t >= 0.0 && t <= closest_t {
            closest_t = t;
            hit_t = Some(t);
        }
    }
    hit_t
}

fn invert_affine_transform(transform: &[f32; 16]) -> Option<[f32; 16]> {
    let m00 = transform[0];
    let m01 = transform[1];
    let m02 = transform[2];
    let tx = transform[3];
    let m10 = transform[4];
    let m11 = transform[5];
    let m12 = transform[6];
    let ty = transform[7];
    let m20 = transform[8];
    let m21 = transform[9];
    let m22 = transform[10];
    let tz = transform[11];

    let c00 = m11 * m22 - m12 * m21;
    let c01 = m02 * m21 - m01 * m22;
    let c02 = m01 * m12 - m02 * m11;
    let c10 = m12 * m20 - m10 * m22;
    let c11 = m00 * m22 - m02 * m20;
    let c12 = m02 * m10 - m00 * m12;
    let c20 = m10 * m21 - m11 * m20;
    let c21 = m01 * m20 - m00 * m21;
    let c22 = m00 * m11 - m01 * m10;
    let det = m00 * c00 + m01 * c10 + m02 * c20;
    if !det.is_finite() || det.abs() <= COLLISION_EPSILON {
        return None;
    }
    let inv_det = 1.0 / det;
    let i00 = c00 * inv_det;
    let i01 = c01 * inv_det;
    let i02 = c02 * inv_det;
    let i10 = c10 * inv_det;
    let i11 = c11 * inv_det;
    let i12 = c12 * inv_det;
    let i20 = c20 * inv_det;
    let i21 = c21 * inv_det;
    let i22 = c22 * inv_det;
    let itx = -(i00 * tx + i01 * ty + i02 * tz);
    let ity = -(i10 * tx + i11 * ty + i12 * tz);
    let itz = -(i20 * tx + i21 * ty + i22 * tz);

    Some([
        i00, i01, i02, itx, i10, i11, i12, ity, i20, i21, i22, itz, 0.0, 0.0, 0.0, 1.0,
    ])
}

fn transform_vector(transform: &[f32; 16], vector: [f32; 3]) -> [f32; 3] {
    [
        transform[0] * vector[0] + transform[1] * vector[1] + transform[2] * vector[2],
        transform[4] * vector[0] + transform[5] * vector[1] + transform[6] * vector[2],
        transform[8] * vector[0] + transform[9] * vector[1] + transform[10] * vector[2],
    ]
}

fn local_radius_for_world_sphere(inverse_transform: &[f32; 16], world_radius: f32) -> f32 {
    if !world_radius.is_finite() || world_radius <= 0.0 {
        return 0.0;
    }

    let x = transform_vector(inverse_transform, [world_radius, 0.0, 0.0]);
    let y = transform_vector(inverse_transform, [0.0, world_radius, 0.0]);
    let z = transform_vector(inverse_transform, [0.0, 0.0, world_radius]);
    vector_length3(x)
        .max(vector_length3(y))
        .max(vector_length3(z))
}

fn raycast_triangle(
    origin: [f32; 3],
    dir: [f32; 3],
    a: [f32; 3],
    b: [f32; 3],
    c: [f32; 3],
) -> Option<f32> {
    let edge1 = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
    let edge2 = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
    let pvec = cross3(dir, edge2);
    let det = dot3(edge1, pvec);
    if !det.is_finite() || det.abs() <= COLLISION_EPSILON {
        return None;
    }

    let inv_det = 1.0 / det;
    let tvec = [origin[0] - a[0], origin[1] - a[1], origin[2] - a[2]];
    let u = dot3(tvec, pvec) * inv_det;
    if !(0.0..=1.0).contains(&u) {
        return None;
    }

    let qvec = cross3(tvec, edge1);
    let v = dot3(dir, qvec) * inv_det;
    if v < 0.0 || u + v > 1.0 {
        return None;
    }

    let t = dot3(edge2, qvec) * inv_det;
    if t.is_finite() {
        Some(t)
    } else {
        None
    }
}

fn raycast_swept_sphere_triangle(
    origin: [f32; 3],
    dir: [f32; 3],
    radius: f32,
    a: [f32; 3],
    b: [f32; 3],
    c: [f32; 3],
    max_distance: f32,
) -> Option<f32> {
    if !radius.is_finite() || radius <= COLLISION_EPSILON {
        return raycast_triangle(origin, dir, a, b, c).filter(|t| *t >= 0.0 && *t <= max_distance);
    }

    let mut best = None;
    consider_hit(
        &mut best,
        raycast_swept_sphere_triangle_face(origin, dir, radius, a, b, c, max_distance),
        max_distance,
    );
    for (edge_a, edge_b) in [(a, b), (b, c), (c, a)] {
        consider_hit(
            &mut best,
            raycast_capsule_segment(origin, dir, edge_a, edge_b, radius, max_distance),
            max_distance,
        );
    }
    best
}

fn raycast_swept_sphere_triangle_face(
    origin: [f32; 3],
    dir: [f32; 3],
    radius: f32,
    a: [f32; 3],
    b: [f32; 3],
    c: [f32; 3],
    max_distance: f32,
) -> Option<f32> {
    let edge1 = sub3(b, a);
    let edge2 = sub3(c, a);
    let normal = cross3(edge1, edge2);
    let normal_len = vector_length3(normal);
    if normal_len <= COLLISION_EPSILON {
        return None;
    }
    let n = [
        normal[0] / normal_len,
        normal[1] / normal_len,
        normal[2] / normal_len,
    ];
    let start_distance = dot3(sub3(origin, a), n);
    let denom = dot3(dir, n);

    let mut candidates = [f32::INFINITY; 3];
    let mut count = 0usize;
    if start_distance.abs() <= radius + COLLISION_EPSILON {
        candidates[count] = 0.0;
        count += 1;
    }
    if denom.abs() > COLLISION_EPSILON {
        for target_distance in [-radius, radius] {
            let t = (target_distance - start_distance) / denom;
            if t >= -COLLISION_EPSILON && t <= max_distance + COLLISION_EPSILON {
                candidates[count] = t.max(0.0);
                count += 1;
            }
        }
    }

    let mut best = None;
    for t in candidates.into_iter().take(count) {
        let center = add3(origin, mul3(dir, t));
        let signed_distance = dot3(sub3(center, a), n);
        let projected = sub3(center, mul3(n, signed_distance));
        if point_in_triangle_3d(projected, a, b, c) {
            consider_hit(&mut best, Some(t), max_distance);
        }
    }
    best
}

fn raycast_capsule_segment(
    origin: [f32; 3],
    dir: [f32; 3],
    a: [f32; 3],
    b: [f32; 3],
    radius: f32,
    max_distance: f32,
) -> Option<f32> {
    let ba = sub3(b, a);
    let oa = sub3(origin, a);
    let baba = dot3(ba, ba);
    if baba <= COLLISION_EPSILON {
        return raycast_sphere(origin, dir, a, radius, max_distance);
    }

    let bard = dot3(ba, dir);
    let baoa = dot3(ba, oa);
    let rdoa = dot3(dir, oa);
    let oaoa = dot3(oa, oa);
    let cyl_a = baba - bard * bard;
    let cyl_b = baba * rdoa - baoa * bard;
    let cyl_c = baba * oaoa - baoa * baoa - radius * radius * baba;

    let mut best = None;
    if cyl_a.abs() > COLLISION_EPSILON {
        let h = cyl_b * cyl_b - cyl_a * cyl_c;
        if h >= 0.0 {
            let t = (-cyl_b - h.sqrt()) / cyl_a;
            let y = baoa + t * bard;
            if y > 0.0 && y < baba {
                consider_hit(&mut best, Some(t), max_distance);
            }
        }
    }

    consider_hit(
        &mut best,
        raycast_sphere(origin, dir, a, radius, max_distance),
        max_distance,
    );
    consider_hit(
        &mut best,
        raycast_sphere(origin, dir, b, radius, max_distance),
        max_distance,
    );
    best
}

fn raycast_sphere(
    origin: [f32; 3],
    dir: [f32; 3],
    center: [f32; 3],
    radius: f32,
    max_distance: f32,
) -> Option<f32> {
    let oc = sub3(origin, center);
    let a = dot3(dir, dir);
    let b = 2.0 * dot3(oc, dir);
    let c = dot3(oc, oc) - radius * radius;
    if a <= COLLISION_EPSILON {
        return None;
    }
    let discriminant = b * b - 4.0 * a * c;
    if discriminant < 0.0 {
        return None;
    }
    let sqrt_d = discriminant.sqrt();
    let t0 = (-b - sqrt_d) / (2.0 * a);
    let t1 = (-b + sqrt_d) / (2.0 * a);
    if t0 >= -COLLISION_EPSILON && t0 <= max_distance + COLLISION_EPSILON {
        Some(t0.max(0.0))
    } else if t1 >= -COLLISION_EPSILON && t1 <= max_distance + COLLISION_EPSILON {
        Some(t1.max(0.0))
    } else {
        None
    }
}

fn consider_hit(best: &mut Option<f32>, candidate: Option<f32>, max_distance: f32) {
    let Some(t) = candidate else {
        return;
    };
    if !t.is_finite() || t < -COLLISION_EPSILON || t > max_distance + COLLISION_EPSILON {
        return;
    }
    let t = t.max(0.0);
    if best.is_none_or(|existing| t < existing) {
        *best = Some(t);
    }
}

fn point_in_triangle_3d(p: [f32; 3], a: [f32; 3], b: [f32; 3], c: [f32; 3]) -> bool {
    let v0 = sub3(b, a);
    let v1 = sub3(c, a);
    let v2 = sub3(p, a);
    let d00 = dot3(v0, v0);
    let d01 = dot3(v0, v1);
    let d11 = dot3(v1, v1);
    let d20 = dot3(v2, v0);
    let d21 = dot3(v2, v1);
    let denom = d00 * d11 - d01 * d01;
    if denom.abs() <= COLLISION_EPSILON {
        return false;
    }
    let v = (d11 * d20 - d01 * d21) / denom;
    let w = (d00 * d21 - d01 * d20) / denom;
    let u = 1.0 - v - w;
    u >= -COLLISION_EPSILON && v >= -COLLISION_EPSILON && w >= -COLLISION_EPSILON
}

fn raycast_aabb_bounds(
    origin: [f32; 3],
    dir: [f32; 3],
    bounds: Aabb3,
    max_distance: f32,
) -> Option<f32> {
    if !max_distance.is_finite() || max_distance < 0.0 || !bounds.is_finite() {
        return None;
    }

    let mut t_min = 0.0_f32;
    let mut t_max = max_distance;
    for (origin, dir, min, max) in [
        (origin[0], dir[0], bounds.min_x, bounds.max_x),
        (origin[1], dir[1], bounds.min_y, bounds.max_y),
        (origin[2], dir[2], bounds.min_z, bounds.max_z),
    ] {
        if !origin.is_finite() || !dir.is_finite() {
            return None;
        }
        if dir.abs() <= COLLISION_EPSILON {
            if origin < min || origin > max {
                return None;
            }
            continue;
        }

        let inv_dir = 1.0 / dir;
        let mut near = (min - origin) * inv_dir;
        let mut far = (max - origin) * inv_dir;
        if near > far {
            std::mem::swap(&mut near, &mut far);
        }
        t_min = t_min.max(near);
        t_max = t_max.min(far);
        if t_min > t_max {
            return None;
        }
    }

    Some(t_min)
}

fn dot3(a: [f32; 3], b: [f32; 3]) -> f32 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

fn add3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

fn sub3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

fn mul3(a: [f32; 3], scalar: f32) -> [f32; 3] {
    [a[0] * scalar, a[1] * scalar, a[2] * scalar]
}

fn vector_length3(a: [f32; 3]) -> f32 {
    dot3(a, a).sqrt()
}

fn cross3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
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
    use super::{
        assert_no_full_rotation_movement_boxes, gameplay_movement_mesh_hull_can_step_up,
        gameplay_movement_mesh_hull_surface_height_at_xz, mesh_instance_bounds,
        movement_broadphase_indices, movement_sweep_bounds, parse_gameplay_collision_boxes,
        parse_gameplay_movement_mesh_hulls, parse_gameplay_query_meshes, polygon_area_signed,
        push_out_convex_footprint_2d, quaternion_to_axes, raycast_gameplay_collision_boxes,
        raycast_gameplay_query_meshes, raycast_query_mesh_geometry_bvh,
        raycast_query_mesh_geometry_linear, resolve_swept_convex_footprint_2d,
        resolve_swept_gameplay_box_2d, resolve_world_spawn_position_with_layout_for_scene,
        transform_point, transform_vector, triangle_normal_y_abs, try_world_gameplay_box_hit,
        Aabb3, GameplayBoxBroadphase, GameplayCollisionBox, GameplayCollisionBoxFile,
        GameplayCollisionLayoutFile, GameplayMovementMeshHull, GameplayQueryMeshBvh,
        GameplayQueryMeshGeometry, GameplayQueryMeshGeometryFile, GameplayQueryMeshInstance,
        GameplayQueryMeshInstanceFile, GameplayQueryMeshSet, MAX_QUERY_MESH_TRIANGLES_PER_COLLIDER,
    };
    use crate::arena::{WorldRayHit, WorldRaycastRequest};
    use crate::open_world_scene::{
        DESERT_DAY_PROFILE, DOCKS_DAY_PROFILE, GIANT_SKELETON_PROFILE, GREAT_HALL_DAY_PROFILE,
        IDOL_DAY_PROFILE, OASIS_DAY_PROFILE, RANDOM_DUNGEON_PROFILE, TEMPLE_GARDENS_PROFILE,
    };

    const TEST_PLAYER_RADIUS: f32 = 0.45;
    const TEST_PLAYER_HEIGHT: f32 = 1.8;

    fn test_aabb(center_x: f32, center_y: f32, center_z: f32) -> GameplayCollisionBox {
        GameplayCollisionBox::Aabb {
            center_x,
            center_y,
            center_z,
            half_x: 0.45,
            half_y: 0.45,
            half_z: 0.45,
        }
    }

    fn test_obb(center_x: f32, center_y: f32, center_z: f32, yaw_deg: f32) -> GameplayCollisionBox {
        let yaw = yaw_deg.to_radians();
        GameplayCollisionBox::ObbY {
            center_x,
            center_y,
            center_z,
            half_x: 0.65,
            half_y: 0.4,
            half_z: 0.3,
            sin_y: yaw.sin(),
            cos_y: yaw.cos(),
        }
    }

    fn test_obb_xyz_z_roll(center_x: f32, center_y: f32, center_z: f32) -> GameplayCollisionBox {
        let half_angle = 90.0_f32.to_radians() * 0.5;
        test_obb_xyz_from_quat(
            center_x,
            center_y,
            center_z,
            0.5,
            2.0,
            0.5,
            [0.0, 0.0, half_angle.sin(), half_angle.cos()],
        )
    }

    fn test_obb_xyz_from_quat(
        center_x: f32,
        center_y: f32,
        center_z: f32,
        half_x: f32,
        half_y: f32,
        half_z: f32,
        rotation: [f32; 4],
    ) -> GameplayCollisionBox {
        let (
            axis_x_x,
            axis_x_y,
            axis_x_z,
            axis_y_x,
            axis_y_y,
            axis_y_z,
            axis_z_x,
            axis_z_y,
            axis_z_z,
        ) = quaternion_to_axes(&rotation).expect("test quaternion should be valid");
        GameplayCollisionBox::ObbXyz {
            center_x,
            center_y,
            center_z,
            half_x,
            half_y,
            half_z,
            axis_x_x,
            axis_x_y,
            axis_x_z,
            axis_y_x,
            axis_y_y,
            axis_y_z,
            axis_z_x,
            axis_z_y,
            axis_z_z,
        }
    }

    #[test]
    fn movement_mesh_footprint_pushout_exits_inside_point() {
        let footprint = vec![[-1.0, -1.0], [1.0, -1.0], [1.0, 1.0], [-1.0, 1.0]];
        assert!(polygon_area_signed(&footprint) > 0.0);

        let (x, z) = push_out_convex_footprint_2d(0.0, 0.0, &footprint, TEST_PLAYER_RADIUS);

        assert_eq!(x, 0.0);
        assert!((z + 1.0 + TEST_PLAYER_RADIUS).abs() < 0.0001);
    }

    #[test]
    fn movement_mesh_footprint_pushout_handles_near_outside_point() {
        let footprint = vec![[-1.0, -1.0], [1.0, -1.0], [1.0, 1.0], [-1.0, 1.0]];

        let (x, z) = push_out_convex_footprint_2d(1.2, 0.0, &footprint, TEST_PLAYER_RADIUS);

        assert!((x - (1.0 + TEST_PLAYER_RADIUS)).abs() < 0.0001);
        assert_eq!(z, 0.0);
    }

    #[test]
    fn movement_mesh_segments_preserve_projected_triangles() {
        let segments = parse_gameplay_movement_mesh_hulls(test_query_mesh_layout(
            test_query_mesh_geometry(
                "wall",
                vec![
                    0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 2.0, 0.0, 0.0, 3.0, 0.0, 0.0, 2.0,
                    0.0, 1.0,
                ],
                vec![0, 1, 2, 3, 4, 5],
            ),
            test_query_mesh_instance("wall_instance", "wall", identity_transform()),
        ));

        assert_eq!(segments.len(), 2);
        assert_eq!(
            segments[0].footprint.len(),
            2,
            "vertical triangle should become a line segment"
        );
        assert_eq!(
            segments[1].footprint.len(),
            3,
            "horizontal triangle should keep area"
        );
    }

    #[test]
    fn swept_movement_blocks_outside_to_inside_mesh_line_segment() {
        let footprint = vec![[0.0, 0.0], [0.0, 1.0]];

        let resolved =
            resolve_swept_convex_footprint_2d(&footprint, -1.0, 0.5, -0.2, 0.5, TEST_PLAYER_RADIUS);

        assert_eq!(resolved, Some((-1.0, 0.5)));
    }

    #[test]
    fn movement_box_broadphase_candidates_preserve_full_scan_blockers() {
        let mut colliders = vec![
            GameplayCollisionBox::Aabb {
                center_x: 0.0,
                center_y: 0.5,
                center_z: 0.0,
                half_x: 0.5,
                half_y: 0.5,
                half_z: 0.5,
            },
            GameplayCollisionBox::Aabb {
                center_x: 50.0,
                center_y: 0.5,
                center_z: 50.0,
                half_x: 0.5,
                half_y: 0.5,
                half_z: 0.5,
            },
        ];
        for index in 0..8 {
            colliders.push(GameplayCollisionBox::Aabb {
                center_x: 50.0 + index as f32 * 4.0,
                center_y: 0.5,
                center_z: 50.0,
                half_x: 0.5,
                half_y: 0.5,
                half_z: 0.5,
            });
        }
        let broadphase = GameplayBoxBroadphase::build(&colliders);
        let query_bounds =
            movement_sweep_bounds(-2.0, 0.0, 0.25, 0.0, TEST_PLAYER_RADIUS, 0.0, 1.8);
        let indices = movement_broadphase_indices(&broadphase, query_bounds)
            .expect("small movement sweep should use broadphase");

        assert_eq!(indices, vec![0]);
        assert_eq!(
            resolve_swept_gameplay_box_2d(colliders[0], -2.0, 0.0, 0.25, 0.0, TEST_PLAYER_RADIUS),
            Some((-2.0, 0.0))
        );
    }

    #[test]
    fn movement_mesh_broadphase_candidates_preserve_full_scan_blockers() {
        let mut hulls = vec![test_movement_hull("near", 0.0, 0.0)];
        for index in 0..8 {
            hulls.push(test_movement_hull(
                &format!("far_{index}"),
                50.0 + index as f32 * 4.0,
                50.0,
            ));
        }
        let broadphase =
            GameplayBoxBroadphase::build_from_aabbs(hulls.len(), hulls.iter().map(|h| h.bounds));
        let query_bounds =
            movement_sweep_bounds(-2.0, 0.25, 0.25, 0.25, TEST_PLAYER_RADIUS, 0.0, 1.8);
        let indices = movement_broadphase_indices(&broadphase, query_bounds)
            .expect("small movement sweep should use broadphase");

        assert_eq!(indices, vec![0]);
        assert_eq!(
            resolve_swept_convex_footprint_2d(
                &hulls[0].footprint,
                -2.0,
                0.25,
                0.25,
                0.25,
                TEST_PLAYER_RADIUS
            ),
            Some((-2.0, 0.25))
        );
    }

    #[test]
    fn low_mesh_segments_are_step_over_but_taller_mesh_segments_block() {
        let low_segment = GameplayMovementMeshHull {
            name: "low".to_string(),
            y_min: 9.07,
            y_max: 9.63,
            footprint: vec![[0.0, 0.0], [0.0, 1.0]],
            triangle: [[0.0, 9.07, 0.0], [0.0, 9.63, 1.0], [1.0, 9.07, 0.0]],
            ground_normal_y_abs: 0.7,
            bounds: Aabb3 {
                min_x: 0.0,
                min_y: 9.07,
                min_z: 0.0,
                max_x: 0.0,
                max_y: 9.63,
                max_z: 1.0,
            },
        };
        let tall_segment = GameplayMovementMeshHull {
            name: "tall".to_string(),
            y_min: 9.07,
            y_max: 9.75,
            footprint: vec![[0.0, 0.0], [0.0, 1.0]],
            triangle: [[0.0, 9.07, 0.0], [0.0, 9.75, 1.0], [1.0, 9.07, 0.0]],
            ground_normal_y_abs: 0.7,
            bounds: Aabb3 {
                min_x: 0.0,
                min_y: 9.07,
                min_z: 0.0,
                max_x: 0.0,
                max_y: 9.75,
                max_z: 1.0,
            },
        };

        assert!(gameplay_movement_mesh_hull_can_step_up(&low_segment, 9.04));
        assert!(!gameplay_movement_mesh_hull_can_step_up(
            &tall_segment,
            9.04
        ));
    }

    #[test]
    fn movement_mesh_surface_height_samples_walkable_triangle_only() {
        let floor = GameplayMovementMeshHull {
            name: "floor".to_string(),
            y_min: 2.0,
            y_max: 2.5,
            footprint: vec![[0.0, 0.0], [2.0, 0.0], [0.0, 2.0]],
            triangle: [[0.0, 2.0, 0.0], [2.0, 2.0, 0.0], [0.0, 2.5, 2.0]],
            ground_normal_y_abs: triangle_normal_y_abs(
                [0.0, 2.0, 0.0],
                [2.0, 2.0, 0.0],
                [0.0, 2.5, 2.0],
            ),
            bounds: Aabb3 {
                min_x: 0.0,
                min_y: 2.0,
                min_z: 0.0,
                max_x: 2.0,
                max_y: 2.5,
                max_z: 2.0,
            },
        };
        let wall = GameplayMovementMeshHull {
            name: "wall".to_string(),
            y_min: 0.0,
            y_max: 2.0,
            footprint: vec![[0.0, 0.0], [0.0, 2.0]],
            triangle: [[0.0, 0.0, 0.0], [0.0, 2.0, 0.0], [0.0, 0.0, 2.0]],
            ground_normal_y_abs: triangle_normal_y_abs(
                [0.0, 0.0, 0.0],
                [0.0, 2.0, 0.0],
                [0.0, 0.0, 2.0],
            ),
            bounds: Aabb3 {
                min_x: 0.0,
                min_y: 0.0,
                min_z: 0.0,
                max_x: 0.0,
                max_y: 2.0,
                max_z: 2.0,
            },
        };

        let sampled = gameplay_movement_mesh_hull_surface_height_at_xz(&floor, 0.5, 0.5).unwrap();
        assert!((sampled - 2.125).abs() < 0.0001);
        assert!(gameplay_movement_mesh_hull_surface_height_at_xz(&floor, 2.0, 2.0).is_none());
        assert!(gameplay_movement_mesh_hull_surface_height_at_xz(&wall, 0.0, 0.5).is_none());
    }

    #[test]
    fn swept_movement_blocks_outside_to_inside_box_entry() {
        let collider = GameplayCollisionBox::Aabb {
            center_x: 0.0,
            center_y: 0.0,
            center_z: 0.0,
            half_x: 0.5,
            half_y: 1.0,
            half_z: 0.5,
        };

        let resolved =
            resolve_swept_gameplay_box_2d(collider, -1.0, 0.0, -0.9, 0.0, TEST_PLAYER_RADIUS);

        assert_eq!(resolved, Some((-1.0, 0.0)));
    }

    #[test]
    fn swept_movement_blocks_outside_to_inside_convex_entry() {
        let footprint = vec![[-0.5, -0.5], [0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]];

        let resolved =
            resolve_swept_convex_footprint_2d(&footprint, -1.0, 0.0, -0.9, 0.0, TEST_PLAYER_RADIUS);

        assert_eq!(resolved, Some((-1.0, 0.0)));
    }

    fn assert_hit_t_close(actual: Option<WorldRayHit>, expected: Option<WorldRayHit>) {
        match (actual, expected) {
            (Some(actual), Some(expected)) => assert!(
                (actual.t - expected.t).abs() < 0.001,
                "expected hit t {}, got {}",
                expected.t,
                actual.t
            ),
            (None, None) => {}
            (actual, expected) => panic!("hit mismatch: actual={actual:?}, expected={expected:?}"),
        }
    }

    fn hit_at(request: &WorldRaycastRequest, t: f32) -> WorldRayHit {
        WorldRayHit {
            t,
            x: request.origin_x + request.dir_x * t,
            y: request.origin_y + request.dir_y * t,
            z: request.origin_z + request.dir_z * t,
        }
    }

    fn request(
        origin_x: f32,
        origin_y: f32,
        origin_z: f32,
        dir_x: f32,
        dir_y: f32,
        dir_z: f32,
        max_distance: f32,
    ) -> WorldRaycastRequest {
        WorldRaycastRequest {
            origin_x,
            origin_y,
            origin_z,
            dir_x,
            dir_y,
            dir_z,
            max_distance,
            radius: 0.0,
        }
    }

    fn request_with_radius(
        origin_x: f32,
        origin_y: f32,
        origin_z: f32,
        dir_x: f32,
        dir_y: f32,
        dir_z: f32,
        max_distance: f32,
        radius: f32,
    ) -> WorldRaycastRequest {
        WorldRaycastRequest {
            radius,
            ..request(
                origin_x,
                origin_y,
                origin_z,
                dir_x,
                dir_y,
                dir_z,
                max_distance,
            )
        }
    }

    fn full_scan_hit(
        colliders: &[GameplayCollisionBox],
        request: WorldRaycastRequest,
    ) -> Option<WorldRayHit> {
        let mut hit = None;
        for collider in colliders {
            try_world_gameplay_box_hit(&mut hit, request, *collider);
        }
        hit
    }

    fn broadphase_hit(
        colliders: &[GameplayCollisionBox],
        request: WorldRaycastRequest,
    ) -> Option<WorldRayHit> {
        let broadphase = GameplayBoxBroadphase::build(colliders);
        let mut hit = None;
        raycast_gameplay_collision_boxes(&mut hit, request, colliders, &broadphase, None);
        hit
    }

    #[test]
    fn obb_xyz_raycast_uses_full_rotation_axes() {
        let collider = test_obb_xyz_z_roll(0.0, 0.0, 0.0);
        let mut hit = None;

        try_world_gameplay_box_hit(
            &mut hit,
            request_with_radius(-3.0, 0.0, 0.0, 1.0, 0.0, 0.0, 8.0, 0.1),
            collider,
        );

        let hit = hit.expect("rolled OBB should expose its long local Y axis in world X");
        assert!(
            (hit.t - 0.9).abs() < 0.001,
            "expected swept ray to hit expanded rolled OBB at t=0.9, got {}",
            hit.t
        );
    }

    #[test]
    fn obb_xyz_identity_matches_aabb() {
        let xyz = test_obb_xyz_from_quat(0.0, 0.0, 0.0, 0.45, 0.45, 0.45, [0.0, 0.0, 0.0, 1.0]);
        let aabb = test_aabb(0.0, 0.0, 0.0);

        for request in [
            request(-2.0, 0.0, 0.0, 1.0, 0.0, 0.0, 5.0),
            request(0.0, -2.0, 0.0, 0.0, 1.0, 0.0, 5.0),
            request(0.0, 0.0, -2.0, 0.0, 0.0, 1.0, 5.0),
            request(2.0, 2.0, 2.0, -0.57735026, -0.57735026, -0.57735026, 6.0),
        ] {
            assert_hit_t_close(
                full_scan_hit(&[xyz], request),
                full_scan_hit(&[aabb], request),
            );
        }
    }

    #[test]
    fn obb_xyz_y_only_quaternion_matches_obb_y() {
        let yaw_deg = 37.0_f32;
        let half_yaw = yaw_deg.to_radians() * 0.5;
        let xyz = test_obb_xyz_from_quat(
            1.0,
            0.5,
            -2.0,
            0.65,
            0.4,
            0.3,
            [0.0, half_yaw.sin(), 0.0, half_yaw.cos()],
        );
        let obb_y = test_obb(1.0, 0.5, -2.0, yaw_deg);

        for request in [
            request(-2.0, 0.5, -2.0, 1.0, 0.0, 0.0, 8.0),
            request(1.0, 0.5, -5.0, 0.0, 0.0, 1.0, 8.0),
            request(3.0, 1.0, 1.0, -0.5345225, -0.26726124, -0.8017837, 8.0),
        ] {
            assert_hit_t_close(
                full_scan_hit(&[xyz], request),
                full_scan_hit(&[obb_y], request),
            );
        }
    }

    #[test]
    fn obb_xyz_quaternion_sign_is_equivalent() {
        let q = [0.2, -0.3, 0.4, 0.84];
        let neg_q = [-q[0], -q[1], -q[2], -q[3]];
        let positive = test_obb_xyz_from_quat(0.25, -0.5, 1.0, 0.75, 1.2, 0.5, q);
        let negative = test_obb_xyz_from_quat(0.25, -0.5, 1.0, 0.75, 1.2, 0.5, neg_q);

        for request in [
            request(-4.0, -0.5, 1.0, 1.0, 0.0, 0.0, 8.0),
            request(0.25, 3.0, 1.0, 0.0, -1.0, 0.0, 8.0),
            request(2.5, 1.5, -2.0, -0.557086, -0.371391, 0.742781, 8.0),
        ] {
            assert_hit_t_close(
                full_scan_hit(&[positive], request),
                full_scan_hit(&[negative], request),
            );
        }
    }

    #[test]
    fn obb_xyz_broadphase_matches_full_scan_hits() {
        let mut colliders = vec![
            test_obb_xyz_from_quat(2.0, 0.0, 0.0, 0.75, 1.2, 0.5, [0.2, -0.3, 0.4, 0.84]),
            test_obb_xyz_z_roll(8.0, 0.0, 0.0),
        ];
        for offset in 0..8 {
            colliders.push(test_aabb(100.0 + offset as f32 * 4.0, 0.0, 0.0));
        }

        for request in [
            request(-4.0, 0.0, 0.0, 1.0, 0.0, 0.0, 16.0),
            request(2.0, -4.0, 0.0, 0.0, 1.0, 0.0, 10.0),
            request(8.0, 0.0, -4.0, 0.0, 0.0, 1.0, 10.0),
            request(12.0, 2.0, 2.0, -0.8728715, -0.21821788, -0.43643576, 12.0),
        ] {
            assert_hit_t_close(
                broadphase_hit(&colliders, request),
                full_scan_hit(&colliders, request),
            );
        }
    }

    #[test]
    #[should_panic(expected = "invalid obb_xyz rotation quaternion")]
    fn obb_xyz_rejects_invalid_quaternion_at_parse() {
        let _ = parse_gameplay_collision_boxes(GameplayCollisionLayoutFile {
            boxes: vec![GameplayCollisionBoxFile {
                shape: "obb_xyz".to_string(),
                center: [0.0, 0.0, 0.0],
                size: [1.0, 1.0, 1.0],
                rotation: vec![0.0, 0.0, 0.0, 0.0],
                rotation_y_deg: 0.0,
            }],
            mesh_geometries: Vec::new(),
            mesh_instances: Vec::new(),
        });
    }

    #[test]
    #[should_panic(expected = "full-rotation boxes are query-only")]
    fn movement_collision_rejects_obb_xyz() {
        let boxes = vec![test_obb_xyz_z_roll(0.0, 0.0, 0.0)];
        assert_no_full_rotation_movement_boxes(&boxes, "test_movement.shared.json");
    }

    #[test]
    fn query_mesh_parse_validates_and_builds_bounds() {
        let meshes = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "test_geometry",
                multi_triangle_vertices(),
                vec![0, 1, 2, 1, 3, 2],
            ),
            test_query_mesh_instance(
                "test_mesh",
                "test_geometry",
                translated_transform(10.0, -5.0, 2.0),
            ),
        ));

        assert_eq!(meshes.geometries.len(), 1);
        assert_eq!(meshes.instances.len(), 1);
        assert_eq!(meshes.geometries[0].id, "test_geometry");
        assert_eq!(meshes.geometries[0].source, "test_source");
        assert_eq!(meshes.geometries[0].vertices.len(), 4);
        assert_eq!(meshes.geometries[0].indices.len(), 6);
        assert_eq!(meshes.geometries[0].local_bounds.min_x, -1.0);
        assert_eq!(meshes.geometries[0].local_bounds.max_x, 3.0);
        assert_eq!(meshes.geometries[0].local_bounds.min_y, -2.0);
        assert_eq!(meshes.geometries[0].local_bounds.max_y, 4.0);
        assert_eq!(meshes.geometries[0].local_bounds.min_z, -3.0);
        assert_eq!(meshes.geometries[0].local_bounds.max_z, 2.5);
        assert_eq!(meshes.instances[0].name, "test_mesh");
        assert_eq!(meshes.instances[0].geometry_index, 0);
        assert_eq!(meshes.instances[0].transform[3], 10.0);
        assert_eq!(meshes.instances[0].bounds.min_x, 9.0);
        assert_eq!(meshes.instances[0].bounds.max_x, 13.0);
        assert_eq!(meshes.instances[0].bounds.min_y, -7.0);
        assert_eq!(meshes.instances[0].bounds.max_y, -1.0);
        assert_eq!(meshes.instances[0].bounds.min_z, -1.0);
        assert_eq!(meshes.instances[0].bounds.max_z, 4.5);
    }

    #[test]
    fn query_mesh_parse_supports_multiple_instances_sharing_one_geometry() {
        let meshes = parse_gameplay_query_meshes(test_query_mesh_layout_many(
            vec![test_query_mesh_geometry(
                "shared_geometry",
                vec![0.0, 0.0, 0.0, 2.0, 0.0, 0.0, 0.0, 3.0, 0.0],
                vec![0, 1, 2],
            )],
            vec![
                test_query_mesh_instance(
                    "instance_a",
                    "shared_geometry",
                    translated_transform(1.0, 2.0, 3.0),
                ),
                test_query_mesh_instance(
                    "instance_b",
                    "shared_geometry",
                    translated_transform(-4.0, 0.5, 8.0),
                ),
            ],
        ));

        assert_eq!(meshes.geometries.len(), 1);
        assert_eq!(meshes.instances.len(), 2);
        assert_eq!(meshes.instances[0].geometry_index, 0);
        assert_eq!(meshes.instances[1].geometry_index, 0);
        assert_eq!(meshes.instances[0].bounds.min_x, 1.0);
        assert_eq!(meshes.instances[0].bounds.max_y, 5.0);
        assert_eq!(meshes.instances[1].bounds.min_x, -4.0);
        assert_eq!(meshes.instances[1].bounds.max_z, 8.0);
    }

    #[test]
    fn query_mesh_parse_matches_json_utility_shape() {
        let file: GameplayCollisionLayoutFile = serde_json::from_str(
            r#"{
                "version": 1,
                "boxes": [],
                "mesh_geometries": [{
                    "id": "guid:mesh:3:1",
                    "source": "Assets/Test.prefab",
                    "vertex_count": 3,
                    "triangle_count": 1,
                    "vertices": [0.0,0.0,0.0, 1.0,0.0,0.0, 0.0,1.0,0.0],
                    "indices": [0,1,2]
                }],
                "mesh_instances": [{
                    "name": "Root/MeshCollider",
                    "geometry_id": "guid:mesh:3:1",
                    "transform": [1.0,0.0,0.0,2.0, 0.0,1.0,0.0,3.0, 0.0,0.0,1.0,4.0, 0.0,0.0,0.0,1.0]
                }]
            }"#,
        )
        .expect("test JSON should parse");
        let meshes = parse_gameplay_query_meshes(file);
        assert_eq!(meshes.geometries.len(), 1);
        assert_eq!(meshes.instances.len(), 1);
        assert_eq!(meshes.instances[0].bounds.min_x, 2.0);
        assert_eq!(meshes.instances[0].bounds.max_z, 4.0);
    }

    #[test]
    #[should_panic(expected = "non-finite vertex data")]
    fn query_mesh_parse_rejects_non_finite_vertex() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "bad_geometry",
                vec![0.0, 0.0, 0.0, f32::NAN, 0.0, 0.0, 0.0, 1.0, 0.0],
                vec![0, 1, 2],
            ),
            test_query_mesh_instance("bad_mesh", "bad_geometry", identity_transform()),
        ));
    }

    #[test]
    #[should_panic(expected = "does not match vertex_count")]
    fn query_mesh_parse_rejects_mismatched_vertex_count() {
        let _ = parse_gameplay_query_meshes(GameplayCollisionLayoutFile {
            boxes: Vec::new(),
            mesh_geometries: vec![GameplayQueryMeshGeometryFile {
                id: "bad_geometry".to_string(),
                source: "test".to_string(),
                vertex_count: 4,
                triangle_count: 1,
                vertices: vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                indices: vec![0, 1, 2],
            }],
            mesh_instances: vec![test_query_mesh_instance(
                "bad_mesh",
                "bad_geometry",
                identity_transform(),
            )],
        });
    }

    #[test]
    #[should_panic(expected = "exceeding per-collider budget")]
    fn query_mesh_parse_rejects_per_geometry_triangle_budget_overflow() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            GameplayQueryMeshGeometryFile {
                id: "too_big".to_string(),
                source: "test_source".to_string(),
                vertex_count: 0,
                triangle_count: MAX_QUERY_MESH_TRIANGLES_PER_COLLIDER + 1,
                vertices: Vec::new(),
                indices: Vec::new(),
            },
            test_query_mesh_instance("bad_mesh", "too_big", identity_transform()),
        ));
    }

    #[test]
    #[should_panic(expected = "exceeds triangle budget")]
    fn query_mesh_parse_rejects_scene_triangle_budget_overflow() {
        let mut geometries = Vec::new();
        let mut instances = Vec::new();
        for index in 0..98 {
            let id = format!("geometry_{index}");
            geometries.push(test_repeated_triangle_geometry(
                &id,
                MAX_QUERY_MESH_TRIANGLES_PER_COLLIDER,
            ));
            instances.push(test_query_mesh_instance(
                &format!("instance_{index}"),
                &id,
                identity_transform(),
            ));
        }

        let _ = parse_gameplay_query_meshes(test_query_mesh_layout_many(geometries, instances));
    }

    #[test]
    #[should_panic(expected = "duplicate query mesh geometry id")]
    fn query_mesh_parse_rejects_duplicate_geometry_id() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout_many(
            vec![
                test_query_mesh_geometry(
                    "duplicate",
                    vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                    vec![0, 1, 2],
                ),
                test_query_mesh_geometry(
                    "duplicate",
                    vec![0.0, 0.0, 0.0, 2.0, 0.0, 0.0, 0.0, 2.0, 0.0],
                    vec![0, 1, 2],
                ),
            ],
            vec![test_query_mesh_instance(
                "bad_mesh",
                "duplicate",
                identity_transform(),
            )],
        ));
    }

    #[test]
    #[should_panic(expected = "has an empty id")]
    fn query_mesh_parse_rejects_empty_geometry_id() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "",
                vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                vec![0, 1, 2],
            ),
            test_query_mesh_instance("bad_mesh", "", identity_transform()),
        ));
    }

    #[test]
    #[should_panic(expected = "index buffer length 3 does not match triangle_count 2")]
    fn query_mesh_parse_rejects_mismatched_triangle_count() {
        let _ = parse_gameplay_query_meshes(GameplayCollisionLayoutFile {
            boxes: Vec::new(),
            mesh_geometries: vec![GameplayQueryMeshGeometryFile {
                id: "bad_geometry".to_string(),
                source: "test".to_string(),
                vertex_count: 3,
                triangle_count: 2,
                vertices: vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                indices: vec![0, 1, 2],
            }],
            mesh_instances: vec![test_query_mesh_instance(
                "bad_mesh",
                "bad_geometry",
                identity_transform(),
            )],
        });
    }

    #[test]
    #[should_panic(expected = "index 3 is outside vertex_count 3")]
    fn query_mesh_parse_rejects_out_of_range_index() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "bad_geometry",
                vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                vec![0, 1, 3],
            ),
            test_query_mesh_instance("bad_mesh", "bad_geometry", identity_transform()),
        ));
    }

    #[test]
    #[should_panic(expected = "contains a degenerate triangle")]
    fn query_mesh_parse_rejects_degenerate_triangle() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "bad_geometry",
                vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 2.0, 0.0, 0.0],
                vec![0, 1, 2],
            ),
            test_query_mesh_instance("bad_mesh", "bad_geometry", identity_transform()),
        ));
    }

    #[test]
    #[should_panic(expected = "transform length 15 does not equal 16")]
    fn query_mesh_parse_rejects_wrong_length_transform() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "good_geometry",
                vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                vec![0, 1, 2],
            ),
            test_query_mesh_instance("bad_mesh", "good_geometry", vec![0.0; 15]),
        ));
    }

    #[test]
    #[should_panic(expected = "contains non-finite transform data")]
    fn query_mesh_parse_rejects_non_finite_transform() {
        let mut transform = identity_transform();
        transform[3] = f32::INFINITY;
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "good_geometry",
                vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                vec![0, 1, 2],
            ),
            test_query_mesh_instance("bad_mesh", "good_geometry", transform),
        ));
    }

    #[test]
    #[should_panic(expected = "references missing geometry")]
    fn query_mesh_parse_rejects_missing_geometry_reference() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "good_geometry",
                vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                vec![0, 1, 2],
            ),
            test_query_mesh_instance("bad_mesh", "missing_geometry", identity_transform()),
        ));
    }

    #[test]
    #[should_panic(expected = "query mesh instance has an empty name")]
    fn query_mesh_parse_rejects_empty_instance_name() {
        let _ = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_query_mesh_geometry(
                "good_geometry",
                vec![0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0],
                vec![0, 1, 2],
            ),
            test_query_mesh_instance("", "good_geometry", identity_transform()),
        ));
    }

    #[test]
    fn query_mesh_raycast_hits_transformed_instance_triangle() {
        let meshes = test_runtime_mesh_set(vec![
            translated_transform(5.0, 0.0, 0.0),
            translated_transform(100.0, 0.0, 0.0),
            translated_transform(110.0, 0.0, 0.0),
            translated_transform(120.0, 0.0, 0.0),
            translated_transform(130.0, 0.0, 0.0),
        ]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(5.25, 0.25, -2.0, 0.0, 0.0, 1.0, 10.0);
        let mut hit = None;

        let mut stats = super::WorldRaycastStats::default();
        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, Some(&mut stats));

        assert_hit_t_close(hit, Some(hit_at(&request, 2.0)));
        assert_eq!(stats.world_query_mesh_broadphase_candidates, 1);
        assert!(stats.world_query_mesh_bvh_node_tests > 0);
        assert_eq!(stats.world_query_mesh_triangles_tested, 1);
        assert_eq!(stats.world_query_mesh_full_scan_fallbacks, 0);
    }

    #[test]
    fn query_mesh_raycast_uses_projectile_radius_for_triangle_edges() {
        let meshes = test_runtime_mesh_set(vec![identity_transform()]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request_with_radius(0.5, -0.05, -2.0, 0.0, 0.0, 1.0, 10.0, 0.1);
        let mut hit = None;

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, None);

        let expected_t = 2.0 - (0.1_f32 * 0.1 - 0.05_f32 * 0.05).sqrt();
        assert_hit_t_close(hit, Some(hit_at(&request, expected_t)));
    }

    #[test]
    fn query_mesh_raycast_hits_rotated_instance_triangle() {
        let meshes = test_runtime_mesh_set(vec![z_rotation_transform(90.0, 0.0, 0.0, 0.0)]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(-0.25, 0.25, -2.0, 0.0, 0.0, 1.0, 10.0);
        let mut hit = None;

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, None);

        assert_hit_t_close(hit, Some(hit_at(&request, 2.0)));
    }

    #[test]
    fn query_mesh_raycast_preserves_t_for_uniform_scale() {
        let meshes = test_runtime_mesh_set(vec![scale_transform(2.0, 2.0, 2.0, 0.0, 0.0, 0.0)]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(1.5, 0.25, -3.0, 0.0, 0.0, 1.0, 10.0);
        let mut hit = None;

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, None);

        assert_hit_t_close(hit, Some(hit_at(&request, 3.0)));
    }

    #[test]
    fn query_mesh_raycast_preserves_t_for_non_uniform_scale() {
        let meshes = test_runtime_mesh_set(vec![scale_transform(2.0, 1.0, 1.0, 0.0, 0.0, 0.0)]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(1.5, 0.25, -4.0, 0.0, 0.0, 1.0, 10.0);
        let mut hit = None;

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, None);

        assert_hit_t_close(hit, Some(hit_at(&request, 4.0)));
    }

    #[test]
    fn query_mesh_raycast_bvh_handles_rotated_scaled_instance() {
        let transform = multiply_transform(
            &y_rotation_transform(45.0, 0.0, 0.0, 0.0),
            &scale_transform(2.0, 2.0, 2.0, 0.0, 0.0, 0.0),
        );
        let meshes = test_runtime_mesh_set(vec![transform]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let local_point = [0.25, 0.25, 0.0];
        let origin = [local_point[0], local_point[1], -2.0];
        let world_origin = transform_point(&meshes.instances[0].transform, origin);
        let world_direction = transform_vector(&meshes.instances[0].transform, [0.0, 0.0, 1.0]);
        let request = request(
            world_origin[0],
            world_origin[1],
            world_origin[2],
            world_direction[0],
            world_direction[1],
            world_direction[2],
            10.0,
        );
        let geometry = &meshes.geometries[0];
        let expected = raycast_query_mesh_geometry_linear(geometry, origin, [0.0, 0.0, 1.0], 10.0);
        let mut hit = None;
        let mut stats = super::WorldRaycastStats::default();

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, Some(&mut stats));

        assert_hit_t_close(hit, expected.map(|t| hit_at(&request, t)));
        assert!(stats.world_query_mesh_bvh_node_tests > 0);
        assert!(stats.world_query_mesh_triangles_tested > 0);
    }

    #[test]
    fn query_mesh_raycast_misses_triangle() {
        let meshes = test_runtime_mesh_set(vec![identity_transform()]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(2.0, 2.0, -2.0, 0.0, 0.0, 1.0, 10.0);
        let mut hit = None;

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, None);

        assert_hit_t_close(hit, None);
    }

    #[test]
    fn query_mesh_raycast_is_two_sided() {
        let meshes = test_runtime_mesh_set(vec![identity_transform()]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(0.25, 0.25, 2.0, 0.0, 0.0, -1.0, 10.0);
        let mut hit = None;

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, None);

        assert_hit_t_close(hit, Some(hit_at(&request, 2.0)));
    }

    #[test]
    fn query_mesh_raycast_skips_non_invertible_transform() {
        let meshes = test_runtime_mesh_set(vec![scale_transform(0.0, 1.0, 1.0, 0.0, 0.0, 0.0)]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(0.0, 0.25, -2.0, 0.0, 0.0, 1.0, 10.0);
        let mut hit = None;
        let mut stats = super::WorldRaycastStats::default();

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, Some(&mut stats));

        assert_hit_t_close(hit, None);
        assert_eq!(stats.world_query_mesh_triangles_tested, 0);
    }

    #[test]
    fn query_mesh_bvh_matches_linear_scan_and_prunes_triangles() {
        let meshes = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_repeated_triangle_geometry("many_triangles", 16),
            test_query_mesh_instance("test_mesh", "many_triangles", identity_transform()),
        ));
        let geometry = &meshes.geometries[0];
        let local_origin = [10.25, 0.25, -1.0];
        let local_dir = [0.0, 0.0, 1.0];
        let mut stats = super::WorldRaycastStats::default();

        let bvh_hit = raycast_query_mesh_geometry_bvh(
            geometry,
            &identity_transform_array(),
            local_origin,
            local_dir,
            local_origin,
            local_dir,
            0.0,
            0.0,
            10.0,
            Some(&mut stats),
        );
        let linear_hit =
            raycast_query_mesh_geometry_linear(geometry, local_origin, local_dir, 10.0);

        assert_eq!(bvh_hit, linear_hit);
        assert_eq!(bvh_hit, Some(1.0));
        assert!(stats.world_query_mesh_bvh_node_tests > 0);
        assert!(
            stats.world_query_mesh_triangles_tested < geometry.indices.len() as u32 / 3,
            "BVH should test fewer triangles than a full scan; tested {} of {}",
            stats.world_query_mesh_triangles_tested,
            geometry.indices.len() / 3
        );
    }

    #[test]
    fn query_mesh_bvh_miss_matches_linear_scan() {
        let meshes = parse_gameplay_query_meshes(test_query_mesh_layout(
            test_repeated_triangle_geometry("many_triangles", 16),
            test_query_mesh_instance("test_mesh", "many_triangles", identity_transform()),
        ));
        let geometry = &meshes.geometries[0];
        let local_origin = [10.25, 10.0, -1.0];
        let local_dir = [0.0, 0.0, 1.0];

        let bvh_hit = raycast_query_mesh_geometry_bvh(
            geometry,
            &identity_transform_array(),
            local_origin,
            local_dir,
            local_origin,
            local_dir,
            0.0,
            0.0,
            10.0,
            None,
        );
        let linear_hit =
            raycast_query_mesh_geometry_linear(geometry, local_origin, local_dir, 10.0);

        assert_eq!(bvh_hit, linear_hit);
        assert_eq!(bvh_hit, None);
    }

    #[test]
    fn query_mesh_raycast_prefers_nearest_mesh_instance() {
        let meshes = test_runtime_mesh_set(vec![
            translated_transform(0.0, 0.0, 5.0),
            translated_transform(0.0, 0.0, 2.0),
        ]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(0.25, 0.25, 0.0, 0.0, 0.0, 1.0, 10.0);
        let mut hit = None;

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, None);

        assert_hit_t_close(hit, Some(hit_at(&request, 2.0)));
    }

    #[test]
    fn query_mesh_raycast_keeps_nearer_existing_box_hit() {
        let meshes = test_runtime_mesh_set(vec![translated_transform(0.0, 0.0, 5.0)]);
        let broadphase = GameplayBoxBroadphase::build_from_aabbs(
            meshes.instances.len(),
            meshes.instances.iter().map(|instance| instance.bounds),
        );
        let request = request(0.25, 0.25, 0.0, 0.0, 0.0, 1.0, 10.0);
        let mut hit = Some(hit_at(&request, 1.0));

        raycast_gameplay_query_meshes(&mut hit, request, &meshes, &broadphase, None);

        assert_hit_t_close(hit, Some(hit_at(&request, 1.0)));
    }

    fn test_query_mesh_layout(
        geometry: GameplayQueryMeshGeometryFile,
        instance: GameplayQueryMeshInstanceFile,
    ) -> GameplayCollisionLayoutFile {
        test_query_mesh_layout_many(vec![geometry], vec![instance])
    }

    fn test_query_mesh_layout_many(
        geometries: Vec<GameplayQueryMeshGeometryFile>,
        instances: Vec<GameplayQueryMeshInstanceFile>,
    ) -> GameplayCollisionLayoutFile {
        GameplayCollisionLayoutFile {
            boxes: Vec::new(),
            mesh_geometries: geometries,
            mesh_instances: instances,
        }
    }

    fn test_query_mesh_geometry(
        id: &str,
        vertices: Vec<f32>,
        indices: Vec<usize>,
    ) -> GameplayQueryMeshGeometryFile {
        GameplayQueryMeshGeometryFile {
            id: id.to_string(),
            source: "test_source".to_string(),
            vertex_count: vertices.len() / 3,
            triangle_count: indices.len() / 3,
            vertices,
            indices,
        }
    }

    fn test_query_mesh_instance(
        name: &str,
        geometry_id: &str,
        transform: Vec<f32>,
    ) -> GameplayQueryMeshInstanceFile {
        GameplayQueryMeshInstanceFile {
            name: name.to_string(),
            geometry_id: geometry_id.to_string(),
            transform,
        }
    }

    fn identity_transform() -> Vec<f32> {
        translated_transform(0.0, 0.0, 0.0)
    }

    fn identity_transform_array() -> [f32; 16] {
        [
            1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
        ]
    }

    fn test_movement_hull(name: &str, x: f32, z: f32) -> GameplayMovementMeshHull {
        GameplayMovementMeshHull {
            name: name.to_string(),
            y_min: 0.0,
            y_max: 1.0,
            footprint: vec![[x, z], [x + 1.0, z], [x, z + 1.0]],
            triangle: [[x, 0.0, z], [x + 1.0, 0.0, z], [x, 0.0, z + 1.0]],
            ground_normal_y_abs: 1.0,
            bounds: Aabb3 {
                min_x: x,
                min_y: 0.0,
                min_z: z,
                max_x: x + 1.0,
                max_y: 1.0,
                max_z: z + 1.0,
            },
        }
    }

    fn translated_transform(x: f32, y: f32, z: f32) -> Vec<f32> {
        vec![
            1.0, 0.0, 0.0, x, 0.0, 1.0, 0.0, y, 0.0, 0.0, 1.0, z, 0.0, 0.0, 0.0, 1.0,
        ]
    }

    fn z_rotation_transform(degrees: f32, x: f32, y: f32, z: f32) -> Vec<f32> {
        let radians = degrees.to_radians();
        let sin = radians.sin();
        let cos = radians.cos();
        vec![
            cos, -sin, 0.0, x, sin, cos, 0.0, y, 0.0, 0.0, 1.0, z, 0.0, 0.0, 0.0, 1.0,
        ]
    }

    fn y_rotation_transform(degrees: f32, x: f32, y: f32, z: f32) -> Vec<f32> {
        let radians = degrees.to_radians();
        let sin = radians.sin();
        let cos = radians.cos();
        vec![
            cos, 0.0, sin, x, 0.0, 1.0, 0.0, y, -sin, 0.0, cos, z, 0.0, 0.0, 0.0, 1.0,
        ]
    }

    fn scale_transform(sx: f32, sy: f32, sz: f32, x: f32, y: f32, z: f32) -> Vec<f32> {
        vec![
            sx, 0.0, 0.0, x, 0.0, sy, 0.0, y, 0.0, 0.0, sz, z, 0.0, 0.0, 0.0, 1.0,
        ]
    }

    fn multiply_transform(a: &[f32], b: &[f32]) -> Vec<f32> {
        let mut result = vec![0.0; 16];
        for row in 0..4 {
            for col in 0..4 {
                result[row * 4 + col] = a[row * 4] * b[col]
                    + a[row * 4 + 1] * b[4 + col]
                    + a[row * 4 + 2] * b[8 + col]
                    + a[row * 4 + 3] * b[12 + col];
            }
        }
        result
    }

    fn multi_triangle_vertices() -> Vec<f32> {
        vec![
            -1.0, 2.0, 0.5, 3.0, -2.0, 1.0, 0.0, 4.0, -3.0, 2.0, 3.0, 2.5,
        ]
    }

    fn test_repeated_triangle_geometry(
        id: &str,
        triangle_count: usize,
    ) -> GameplayQueryMeshGeometryFile {
        let mut vertices = Vec::with_capacity(triangle_count * 9);
        let mut indices = Vec::with_capacity(triangle_count * 3);
        for triangle_index in 0..triangle_count {
            let base_vertex = triangle_index * 3;
            let offset = triangle_index as f32 * 2.0;
            vertices.extend_from_slice(&[
                offset,
                0.0,
                0.0,
                offset + 1.0,
                0.0,
                0.0,
                offset,
                1.0,
                0.0,
            ]);
            indices.extend_from_slice(&[base_vertex, base_vertex + 1, base_vertex + 2]);
        }

        test_query_mesh_geometry(id, vertices, indices)
    }

    fn test_runtime_mesh_set(transforms: Vec<Vec<f32>>) -> GameplayQueryMeshSet {
        let geometry = GameplayQueryMeshGeometry {
            id: "runtime_geometry".to_string(),
            source: "test_source".to_string(),
            vertices: vec![[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]],
            indices: vec![0, 1, 2],
            local_bounds: Aabb3 {
                min_x: 0.0,
                min_y: 0.0,
                min_z: 0.0,
                max_x: 1.0,
                max_y: 1.0,
                max_z: 0.0,
            },
            bvh: GameplayQueryMeshBvh::build(
                &[[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]],
                &[0, 1, 2],
            ),
        };
        let instances = transforms
            .into_iter()
            .enumerate()
            .map(|(index, transform)| {
                let transform: [f32; 16] =
                    transform.try_into().expect("test transform has 16 values");
                let bounds = mesh_instance_bounds(&geometry, &transform);
                GameplayQueryMeshInstance {
                    name: format!("instance_{index}"),
                    geometry_index: 0,
                    transform,
                    bounds,
                }
            })
            .collect();
        GameplayQueryMeshSet {
            geometries: vec![geometry],
            instances,
        }
    }

    #[test]
    fn gameplay_box_broadphase_candidates_preserve_original_index_order() {
        let mut colliders = vec![test_aabb(10.0, 0.0, 0.0), test_aabb(0.0, 0.0, 0.0)];
        for offset in 0..8 {
            colliders.push(test_aabb(100.0 + offset as f32 * 4.0, 0.0, 0.0));
        }

        let broadphase = GameplayBoxBroadphase::build(&colliders);
        let mut candidates = Vec::new();
        assert!(
            broadphase.query_into(
                request(-1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 12.0),
                &mut candidates
            ),
            "small query should use broadphase candidates"
        );

        assert_eq!(candidates, vec![0, 1]);
    }

    #[test]
    fn gameplay_box_broadphase_prunes_vertical_stacks() {
        let colliders = vec![
            test_aabb(0.0, 0.0, 0.0),
            test_aabb(0.0, 10.0, 0.0),
            test_aabb(10.0, 0.0, 0.0),
            test_aabb(20.0, 0.0, 0.0),
            test_aabb(30.0, 0.0, 0.0),
        ];

        let broadphase = GameplayBoxBroadphase::build(&colliders);
        let mut candidates = Vec::new();
        assert!(
            broadphase.query_into(
                request(-1.0, 10.0, 0.0, 1.0, 0.0, 0.0, 2.0),
                &mut candidates
            ),
            "small query should use broadphase candidates"
        );

        assert_eq!(candidates, vec![1]);
    }

    #[test]
    fn gameplay_box_broadphase_falls_back_for_huge_queries() {
        let colliders = vec![test_aabb(0.0, 0.0, 0.0), test_aabb(1000.0, 0.0, 0.0)];
        let broadphase = GameplayBoxBroadphase::build(&colliders);
        let mut candidates = Vec::new();

        assert!(
            !broadphase.query_into(
                request(
                    -1_000_000_000.0,
                    0.0,
                    -1_000_000_000.0,
                    1.0,
                    0.0,
                    1.0,
                    2_000_000_000.0,
                ),
                &mut candidates,
            ),
            "huge queries should fall back to full scan"
        );
        assert!(candidates.is_empty());
    }

    #[test]
    fn gameplay_box_raycast_stats_track_pruned_and_fallback_work() {
        let mut colliders = vec![test_aabb(0.0, 0.0, 0.0), test_aabb(4.0, 0.0, 0.0)];
        for offset in 0..16 {
            colliders.push(test_aabb(100.0 + offset as f32 * 4.0, 0.0, 0.0));
        }
        let broadphase = GameplayBoxBroadphase::build(&colliders);

        let mut hit = None;
        let mut stats = super::WorldRaycastStats::default();
        raycast_gameplay_collision_boxes(
            &mut hit,
            request(-1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 8.0),
            &colliders,
            &broadphase,
            Some(&mut stats),
        );
        assert_eq!(stats.world_gameplay_full_scan_fallbacks, 0);
        assert!(
            stats.world_gameplay_narrowphase_tests < colliders.len() as u32,
            "broadphase should prune far-away colliders"
        );
        assert_eq!(
            stats.world_gameplay_broadphase_candidates,
            stats.world_gameplay_narrowphase_tests
        );

        let mut fallback_hit = None;
        let mut fallback_stats = super::WorldRaycastStats::default();
        raycast_gameplay_collision_boxes(
            &mut fallback_hit,
            request(
                -1_000_000_000.0,
                0.0,
                -1_000_000_000.0,
                1.0,
                0.0,
                1.0,
                2_000_000_000.0,
            ),
            &colliders,
            &broadphase,
            Some(&mut fallback_stats),
        );
        assert_eq!(fallback_stats.world_gameplay_full_scan_fallbacks, 1);
        assert_eq!(fallback_stats.world_gameplay_broadphase_candidates, 0);
        assert_eq!(
            fallback_stats.world_gameplay_narrowphase_tests,
            colliders.len() as u32
        );
    }

    // The former query_collision_augments_movement_collision_during_migration
    // test pinned the pre-ruling contract (movement boxes blocking query
    // raycasts) and was deleted with the 2026-07-04 ruling: query raycasts
    // test authored query geometry only.

    #[test]
    fn gameplay_box_broadphase_falls_back_when_any_collider_is_unindexed() {
        let colliders = vec![
            GameplayCollisionBox::Aabb {
                center_x: 0.0,
                center_y: 0.0,
                center_z: 0.0,
                half_x: f32::NAN,
                half_y: 0.45,
                half_z: 0.45,
            },
            test_aabb(4.0, 0.0, 0.0),
        ];
        let broadphase = GameplayBoxBroadphase::build(&colliders);
        assert_eq!(broadphase.unindexed_collider_count, 1);

        let mut candidates = Vec::new();
        assert!(
            !broadphase.query_into(request(-1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 8.0), &mut candidates),
            "queries must fall back while any collider is unindexed"
        );

        let mut hit = None;
        let mut stats = super::WorldRaycastStats::default();
        raycast_gameplay_collision_boxes(
            &mut hit,
            request(-1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 8.0),
            &colliders,
            &broadphase,
            Some(&mut stats),
        );
        assert_eq!(stats.world_gameplay_full_scan_fallbacks, 1);
        assert_eq!(stats.world_gameplay_broadphase_candidates, 0);
        assert_eq!(
            stats.world_gameplay_narrowphase_tests,
            colliders.len() as u32
        );
    }

    #[test]
    fn world_raycast_stats_track_open_world_step_checks() {
        let mut stats = super::WorldRaycastStats::default();
        let _ = super::raycast_world_with_layout_for_scene_with_stats(
            None,
            false,
            Some(DESERT_DAY_PROFILE.scene_name),
            request(0.0, 4.0, 0.0, 1.0, 0.0, 0.0, 4.0),
            Some(&mut stats),
        );

        assert_eq!(stats.raycast_queries, 1);
        assert!(
            stats.open_world_geometry_point_checks > 0,
            "open-world fixed-step geometry path should be visible in stats"
        );
    }

    #[test]
    fn preload_world_collision_data_initializes_known_scene_indexes() {
        let summary = super::preload_world_collision_data();
        assert_eq!(
            summary.scene_count as usize,
            super::OPEN_WORLD_SCENE_PROFILES.len()
        );
        assert!(summary.arena_gameplay_boxes > 0);
        assert!(summary.open_world_gameplay_boxes > 0);
        assert!(summary.broadphase_cells > 0);
        assert!(summary.broadphase_index_entries >= summary.broadphase_cells);
        assert!(summary.broadphase_max_cell_occupancy > 0);
        assert!(summary.broadphase_max_cells_per_collider > 0);
    }

    #[test]
    fn gameplay_box_broadphase_matches_full_scan_hits() {
        let mut colliders = Vec::new();
        for x in 0..6 {
            for z in 0..4 {
                let center_x = x as f32 * 4.0;
                let center_z = z as f32 * 4.0;
                if (x + z) % 2 == 0 {
                    colliders.push(test_aabb(center_x, 0.0, center_z));
                } else {
                    colliders.push(test_obb(center_x, 0.0, center_z, 35.0));
                }
            }
        }

        let requests = [
            request(-2.0, 0.0, 0.0, 1.0, 0.0, 0.0, 8.0),
            request(6.0, 0.0, -2.0, 0.0, 0.0, 1.0, 14.0),
            request(20.0, 0.0, 14.0, -0.70710677, 0.0, -0.70710677, 12.0),
            request(1.5, 6.0, 1.5, 1.0, 0.0, 0.0, 8.0),
            request(18.0, 0.0, -1.0, -0.4472136, 0.0, 0.8944272, 15.0),
        ];

        for request in requests {
            let full = full_scan_hit(&colliders, request);
            let broadphase = broadphase_hit(&colliders, request);
            match (full, broadphase) {
                (Some(full), Some(broadphase)) => {
                    assert!(
                        (full.t - broadphase.t).abs() < 0.0001,
                        "full t={} broadphase t={}",
                        full.t,
                        broadphase.t
                    );
                }
                (None, None) => {}
                (full, broadphase) => panic!(
                    "full scan and broadphase disagreed: full={full:?} broadphase={broadphase:?}"
                ),
            }
        }
    }

    #[test]
    fn gameplay_box_broadphase_matches_full_scan_for_exported_scene_samples() {
        let scenes = [
            DESERT_DAY_PROFILE.scene_name,
            GIANT_SKELETON_PROFILE.scene_name,
            GREAT_HALL_DAY_PROFILE.scene_name,
            OASIS_DAY_PROFILE.scene_name,
            TEMPLE_GARDENS_PROFILE.scene_name,
        ];
        let requests = [
            request(-12.0, 4.0, -12.0, 1.0, 0.0, 0.0, 36.0),
            request(0.0, 4.0, -18.0, 0.0, 0.0, 1.0, 36.0),
            request(18.0, 8.0, 18.0, -0.70710677, 0.0, -0.70710677, 50.0),
            request(-20.0, 14.0, 4.0, 0.8944272, -0.0, 0.4472136, 50.0),
            request(4.0, 28.0, -20.0, 0.0, -0.4472136, 0.8944272, 45.0),
        ];

        for scene_name in scenes {
            let profile = super::open_world_profile_from_name(Some(scene_name));
            let colliders = super::open_world_gameplay_collision_boxes(profile);
            assert!(
                !colliders.is_empty(),
                "expected exported gameplay colliders for scene {scene_name}"
            );

            for request in requests {
                let full = full_scan_hit(colliders, request);
                let mut broadphase = None;
                raycast_gameplay_collision_boxes(
                    &mut broadphase,
                    request,
                    colliders,
                    super::open_world_gameplay_collision_broadphase(profile),
                    None,
                );
                match (full, broadphase) {
                    (Some(full), Some(broadphase)) => {
                        assert!(
                            (full.t - broadphase.t).abs() < 0.0001,
                            "scene={scene_name} full t={} broadphase t={}",
                            full.t,
                            broadphase.t
                        );
                    }
                    (None, None) => {}
                    (full, broadphase) => panic!(
                        "scene={scene_name} full scan and broadphase disagreed: full={full:?} broadphase={broadphase:?}"
                    ),
                }
            }
        }
    }

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

    #[test]
    fn random_dungeon_spawn_resolves_on_baked_origin_floor() {
        let (spawn_x, spawn_y, spawn_z) = resolve_world_spawn_position_with_layout_for_scene(
            None,
            false,
            Some(RANDOM_DUNGEON_PROFILE.scene_name),
            RANDOM_DUNGEON_PROFILE.spawn_x,
            RANDOM_DUNGEON_PROFILE.spawn_z,
            TEST_PLAYER_RADIUS,
            TEST_PLAYER_HEIGHT,
        );

        assert!(spawn_x.abs() < 0.01, "Dungeon spawn moved on X: {spawn_x}");
        assert!(spawn_z.abs() < 0.01, "Dungeon spawn moved on Z: {spawn_z}");
        assert!(
            spawn_y.abs() < 0.01,
            "Dungeon spawn missed origin floor: {spawn_y}"
        );
    }

    #[test]
    fn giant_skeleton_los_raycast_does_not_use_projected_movement_mesh_surface() {
        let caster = (22.26_f32, 10.44_f32, -66.28_f32);
        let target = (27.41_f32, 9.32_f32, -73.61_f32);
        let hit_height = 1.8_f32;
        let hit_radius = 0.28_f32;
        let origin = (caster.0, caster.1 + hit_height * 0.85, caster.2);
        let dx = target.0 - caster.0;
        let dz = target.2 - caster.2;
        let len = (dx * dx + dz * dz).sqrt();
        let side_offset = hit_radius * 0.75;
        let side_x = -dz / len * side_offset;
        let side_z = dx / len * side_offset;

        for height_fraction in [0.75_f32, 0.6_f32] {
            let base = (target.0, target.1 + hit_height * height_fraction, target.2);
            for endpoint in [
                base,
                (base.0 + side_x, base.1, base.2 + side_z),
                (base.0 - side_x, base.1, base.2 - side_z),
            ] {
                let ray_dx = endpoint.0 - origin.0;
                let ray_dy = endpoint.1 - origin.1;
                let ray_dz = endpoint.2 - origin.2;
                let distance = (ray_dx * ray_dx + ray_dy * ray_dy + ray_dz * ray_dz).sqrt();
                let hit = super::raycast_world_with_layout_for_scene_with_stats(
                    None,
                    false,
                    Some(GIANT_SKELETON_PROFILE.scene_name),
                    WorldRaycastRequest {
                        origin_x: origin.0,
                        origin_y: origin.1,
                        origin_z: origin.2,
                        dir_x: ray_dx / distance,
                        dir_y: ray_dy / distance,
                        dir_z: ray_dz / distance,
                        max_distance: distance,
                        radius: 0.05,
                    },
                    None,
                );

                assert!(
                    hit.is_none(),
                    "torso LOS probe should be clear but hit {hit:?} toward endpoint {endpoint:?}"
                );
            }
        }
    }
}

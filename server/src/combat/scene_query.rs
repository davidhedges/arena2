use spacetimedb::{Identity, ReducerContext};

use crate::arena::{
    arena_seed_for_identity, open_world_scene_name_for_identity, players_share_world_context,
    resolve_player_world_context, shared_arena_seed_for_identities, ResolvedWorldContext,
    WorldRayHit, WorldRaycastRequest,
};
use crate::combat::actor_snapshot::CombatActorSnapshot;
use crate::practice::is_training_instance;
use crate::world_collision::{
    raycast_world_with_layout_for_scene, raycast_world_with_layout_for_scene_with_stats,
    surface_height_for_world_at_y_with_layout_for_scene, WorldRaycastStats,
};
use crate::world_obstacles::first_active_world_obstacle_hit;

#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::player_intent::player_intent as _;
const EPS: f32 = 0.0001;
const LOS_PROBE_RADIUS: f32 = 0.05;
const LOS_BLOCK_EPSILON: f32 = 0.01;
const LOS_TARGET_SIDE_PROBE_FRACTION: f32 = 0.75;

#[derive(Clone, Copy, Debug)]
pub(crate) enum SceneHitKind {
    World,
    Player(Identity),
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct SceneHit {
    pub kind: SceneHitKind,
    pub t: f32,
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct LineOfSightBlocker {
    pub target_x: f32,
    pub target_y: f32,
    pub target_z: f32,
    pub hit: SceneHit,
}

pub(crate) fn is_direction_within_facing_arc(
    facing_yaw: f32,
    dir_x: f32,
    dir_z: f32,
    facing_arc_radians: f32,
    dot_epsilon: f32,
) -> bool {
    let dir_len_sq = dir_x * dir_x + dir_z * dir_z;
    if dir_len_sq <= EPS {
        return true;
    }

    let inv_len = 1.0 / dir_len_sq.sqrt();
    let to_target_x = dir_x * inv_len;
    let to_target_z = dir_z * inv_len;
    let forward_x = facing_yaw.sin();
    let forward_z = facing_yaw.cos();
    let dot = forward_x * to_target_x + forward_z * to_target_z;
    let min_dot = (facing_arc_radians * 0.5).cos();
    dot + dot_epsilon.max(0.0) >= min_dot
}

pub(crate) fn has_line_of_sight(
    ctx: &ReducerContext,
    caster: &CombatActorSnapshot,
    target: &CombatActorSnapshot,
) -> bool {
    if !players_share_world_context(ctx, caster.player_id, target.player_id) {
        return false;
    }

    line_of_sight_blocker(ctx, caster, target).is_none()
}

pub(crate) fn line_of_sight_blocker(
    ctx: &ReducerContext,
    caster: &CombatActorSnapshot,
    target: &CombatActorSnapshot,
) -> Option<LineOfSightBlocker> {
    let seed = shared_arena_seed_for_identities(ctx, caster.player_id, target.player_id);
    let flat_ground_only =
        shared_flat_ground_only_for_identities(ctx, caster.player_id, target.player_id);
    let open_world_scene_name = if seed.is_some() {
        None
    } else {
        Some(open_world_scene_name_for_identity(ctx, caster.player_id))
    };

    let origin_x = caster.pos_x;
    let origin_y = caster.pos_y + caster.hit_height * 0.85;
    let origin_z = caster.pos_z;

    // A probe that reaches the target's personal space sees the target
    // (owner ruling, S4 follow-up): geometry the target legally stands
    // against — padded movement boxes especially — cannot conceal them.
    // Cover only blocks when it interposes deeper than the target's radius.
    let clear_tolerance = target.hit_radius.max(0.0) + LOS_BLOCK_EPSILON;

    let target_points = line_of_sight_target_points(caster, target);
    let mut best_blocker: Option<LineOfSightBlocker> = None;
    for (target_x, target_y, target_z) in target_points {
        let hit = match world_hit_on_segment(
            ctx,
            caster.player_id,
            seed,
            flat_ground_only,
            open_world_scene_name.as_deref(),
            origin_x,
            origin_y,
            origin_z,
            target_x,
            target_y,
            target_z,
            LOS_PROBE_RADIUS,
        ) {
            None => return None,
            Some((hit, distance)) if hit.t >= distance - clear_tolerance => return None,
            Some((hit, _)) => SceneHit {
                kind: SceneHitKind::World,
                t: hit.t,
                x: hit.x,
                y: hit.y,
                z: hit.z,
            },
        };
        let blocker = LineOfSightBlocker {
            target_x,
            target_y,
            target_z,
            hit,
        };
        if best_blocker.is_none_or(|best| blocker.hit.t < best.hit.t) {
            best_blocker = Some(blocker);
        }
    }

    best_blocker
}

fn line_of_sight_target_points(
    caster: &CombatActorSnapshot,
    target: &CombatActorSnapshot,
) -> Vec<(f32, f32, f32)> {
    let heights = [
        target.pos_y + target.hit_height * 0.75,
        target.pos_y + target.hit_height * 0.6,
    ];

    let dx = target.pos_x - caster.pos_x;
    let dz = target.pos_z - caster.pos_z;
    let horizontal_len_sq = dx * dx + dz * dz;
    let side_offset = target.hit_radius.max(0.0) * LOS_TARGET_SIDE_PROBE_FRACTION;

    let offsets = if horizontal_len_sq > EPS && side_offset > EPS {
        let inv_len = 1.0 / horizontal_len_sq.sqrt();
        let side_x = -dz * inv_len * side_offset;
        let side_z = dx * inv_len * side_offset;
        [(0.0, 0.0), (side_x, side_z), (-side_x, -side_z)]
    } else {
        [(0.0, 0.0), (0.0, 0.0), (0.0, 0.0)]
    };

    let mut points = Vec::with_capacity(heights.len() * offsets.len());
    for target_y in heights {
        for (offset_x, offset_z) in offsets {
            if offset_x == 0.0 && offset_z == 0.0 && points.iter().any(|(_, y, _)| *y == target_y) {
                continue;
            }
            points.push((target.pos_x + offset_x, target_y, target.pos_z + offset_z));
        }
    }
    points
}

pub(crate) fn first_hit_on_segment(
    ctx: &ReducerContext,
    caster: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    players: &[CombatActorSnapshot],
) -> Option<SceneHit> {
    first_hit_on_segment_with_players(
        ctx,
        caster,
        start_x,
        start_y,
        start_z,
        end_x,
        end_y,
        end_z,
        radius,
        players.iter(),
        None,
    )
}

// Player-only variant for terrain-conforming projectiles (Ground Slash and similar low-ground sweeps)
// that must not fizzle into terrain. Skips the world raycast; otherwise identical to first_hit_on_segment.
#[allow(clippy::too_many_arguments)]
pub(crate) fn first_player_hit_on_segment_candidates(
    ctx: &ReducerContext,
    caster: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    players: &[CombatActorSnapshot],
    candidate_indices: &[usize],
) -> Option<SceneHit> {
    let dx = end_x - start_x;
    let dy = end_y - start_y;
    let dz = end_z - start_z;
    let dist_sq = dx * dx + dy * dy + dz * dz;
    if dist_sq <= EPS {
        return None;
    }
    let distance = dist_sq.sqrt();
    let inv = 1.0 / distance;
    first_player_hit_on_segment(
        caster,
        start_x,
        start_y,
        start_z,
        dx * inv,
        dy * inv,
        dz * inv,
        distance,
        radius,
        candidate_indices
            .iter()
            .filter_map(|index| players.get(*index)),
        |target| players_share_world_context(ctx, caster, target),
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn first_hit_on_segment_candidates_with_world_stats(
    ctx: &ReducerContext,
    caster: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    players: &[CombatActorSnapshot],
    candidate_indices: &[usize],
    world_stats: &mut WorldRaycastStats,
) -> Option<SceneHit> {
    first_hit_on_segment_candidates_with_stats(
        ctx,
        caster,
        start_x,
        start_y,
        start_z,
        end_x,
        end_y,
        end_z,
        radius,
        players,
        candidate_indices,
        Some(world_stats),
    )
}

#[allow(clippy::too_many_arguments)]
fn first_hit_on_segment_candidates_with_stats(
    ctx: &ReducerContext,
    caster: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    players: &[CombatActorSnapshot],
    candidate_indices: &[usize],
    world_stats: Option<&mut WorldRaycastStats>,
) -> Option<SceneHit> {
    first_hit_on_segment_with_players(
        ctx,
        caster,
        start_x,
        start_y,
        start_z,
        end_x,
        end_y,
        end_z,
        radius,
        candidate_indices
            .iter()
            .filter_map(|index| players.get(*index)),
        world_stats,
    )
}

#[allow(clippy::too_many_arguments)]
fn first_hit_on_segment_with_players<'a, I>(
    ctx: &ReducerContext,
    caster: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    players: I,
    mut world_stats: Option<&mut WorldRaycastStats>,
) -> Option<SceneHit>
where
    I: IntoIterator<Item = &'a CombatActorSnapshot>,
{
    let dx = end_x - start_x;
    let dy = end_y - start_y;
    let dz = end_z - start_z;
    let dist_sq = dx * dx + dy * dy + dz * dz;
    if dist_sq <= EPS {
        return None;
    }
    let distance = dist_sq.sqrt();
    let inv = 1.0 / distance;
    let dir_x = dx * inv;
    let dir_y = dy * inv;
    let dir_z = dz * inv;

    let mut best: Option<SceneHit> = None;
    let flat_ground_only = flat_ground_only_for_identity(ctx, caster);
    let seed = arena_seed_for_identity(ctx, caster);
    let open_world_scene_name = if seed.is_some() {
        None
    } else {
        Some(open_world_scene_name_for_identity(ctx, caster))
    };

    if let Some(hit) = raycast_world_with_layout_for_scene_with_stats(
        seed,
        flat_ground_only,
        open_world_scene_name.as_deref(),
        WorldRaycastRequest {
            origin_x: start_x,
            origin_y: start_y,
            origin_z: start_z,
            dir_x,
            dir_y,
            dir_z,
            max_distance: distance,
            radius,
        },
        world_stats.as_deref_mut(),
    ) {
        let world_hit = SceneHit {
            kind: SceneHitKind::World,
            t: hit.t,
            x: hit.x,
            y: hit.y,
            z: hit.z,
        };
        if best.is_none_or(|existing| world_hit.t < existing.t) {
            best = Some(world_hit);
        }
    }

    if let Some(hit) = first_active_world_obstacle_hit(
        ctx, caster, start_x, start_y, start_z, end_x, end_y, end_z, radius,
    ) {
        let world_hit = SceneHit {
            kind: SceneHitKind::World,
            t: hit.t,
            x: hit.x,
            y: hit.y,
            z: hit.z,
        };
        if best.is_none_or(|existing| world_hit.t < existing.t) {
            best = Some(world_hit);
        }
    }

    if let Some(player_hit) = first_player_hit_on_segment(
        caster,
        start_x,
        start_y,
        start_z,
        dir_x,
        dir_y,
        dir_z,
        distance,
        radius,
        players,
        |target| players_share_world_context(ctx, caster, target),
    ) {
        if best.is_none_or(|existing| player_hit.t < existing.t) {
            best = Some(player_hit);
        }
    }

    best
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn first_world_hit_on_segment_with_stats(
    ctx: &ReducerContext,
    caster: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    world_stats: &mut WorldRaycastStats,
) -> Option<SceneHit> {
    first_world_hit_on_segment_with_optional_stats(
        ctx,
        caster,
        start_x,
        start_y,
        start_z,
        end_x,
        end_y,
        end_z,
        radius,
        Some(world_stats),
    )
}

#[allow(clippy::too_many_arguments)]
fn first_world_hit_on_segment_with_optional_stats(
    ctx: &ReducerContext,
    caster: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    world_stats: Option<&mut WorldRaycastStats>,
) -> Option<SceneHit> {
    let dx = end_x - start_x;
    let dy = end_y - start_y;
    let dz = end_z - start_z;
    let dist_sq = dx * dx + dy * dy + dz * dz;
    if dist_sq <= EPS {
        return None;
    }
    let distance = dist_sq.sqrt();
    let inv = 1.0 / distance;
    let flat_ground_only = flat_ground_only_for_identity(ctx, caster);
    let seed = arena_seed_for_identity(ctx, caster);
    let open_world_scene_name = if seed.is_some() {
        None
    } else {
        Some(open_world_scene_name_for_identity(ctx, caster))
    };

    let static_hit = raycast_world_with_layout_for_scene_with_stats(
        seed,
        flat_ground_only,
        open_world_scene_name.as_deref(),
        WorldRaycastRequest {
            origin_x: start_x,
            origin_y: start_y,
            origin_z: start_z,
            dir_x: dx * inv,
            dir_y: dy * inv,
            dir_z: dz * inv,
            max_distance: distance,
            radius,
        },
        world_stats,
    )
    .map(|hit| SceneHit {
        kind: SceneHitKind::World,
        t: hit.t,
        x: hit.x,
        y: hit.y,
        z: hit.z,
    });
    let dynamic_hit = first_active_world_obstacle_hit(
        ctx, caster, start_x, start_y, start_z, end_x, end_y, end_z, radius,
    )
    .map(|hit| SceneHit {
        kind: SceneHitKind::World,
        t: hit.t,
        x: hit.x,
        y: hit.y,
        z: hit.z,
    });
    match (static_hit, dynamic_hit) {
        (Some(static_hit), Some(dynamic_hit)) => Some(if dynamic_hit.t < static_hit.t {
            dynamic_hit
        } else {
            static_hit
        }),
        (Some(hit), None) | (None, Some(hit)) => Some(hit),
        (None, None) => None,
    }
}

#[allow(clippy::too_many_arguments)]
fn first_player_hit_on_segment<'a, I>(
    caster: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    distance: f32,
    radius: f32,
    players: I,
    shares_world: impl Fn(Identity) -> bool,
) -> Option<SceneHit>
where
    I: IntoIterator<Item = &'a CombatActorSnapshot>,
{
    let mut best: Option<SceneHit> = None;

    for player in players {
        if !player.alive || player.player_id == caster {
            continue;
        }
        if !shares_world(player.player_id) {
            continue;
        }

        let Some(t) = raycast_capsule_with_padding(
            start_x, start_y, start_z, dir_x, dir_y, dir_z, distance, player, radius,
        ) else {
            continue;
        };

        if best.is_some_and(|existing| t >= existing.t) {
            continue;
        }

        best = Some(SceneHit {
            kind: SceneHitKind::Player(player.player_id),
            t,
            x: start_x + dir_x * t,
            y: start_y + dir_y * t,
            z: start_z + dir_z * t,
        });
    }

    best
}

pub(crate) fn raycast_capsule_with_padding(
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    max_distance: f32,
    player: &CombatActorSnapshot,
    radius_padding: f32,
) -> Option<f32> {
    let radius = player.hit_radius + radius_padding;
    let capsule_bottom = player.pos_y;
    let capsule_top = player.pos_y + player.hit_height;

    if point_inside_capsule(origin_x, origin_y, origin_z, player, radius_padding) {
        return Some(0.0);
    }

    let mut best_t: Option<f32> = None;

    let ox = origin_x - player.pos_x;
    let oz = origin_z - player.pos_z;
    let a = dir_x * dir_x + dir_z * dir_z;

    if a > 0.000001 {
        let b = 2.0 * (ox * dir_x + oz * dir_z);
        let c = ox * ox + oz * oz - radius * radius;
        let disc = b * b - 4.0 * a * c;
        if disc >= 0.0 {
            let sqrt_disc = disc.sqrt();
            let inv_denom = 0.5 / a;
            let t1 = (-b - sqrt_disc) * inv_denom;
            let t2 = (-b + sqrt_disc) * inv_denom;
            for t in [t1, t2] {
                if t < 0.0 || t > max_distance {
                    continue;
                }
                let y = origin_y + dir_y * t;
                if y >= capsule_bottom && y <= capsule_top && best_t.is_none_or(|best| t < best) {
                    best_t = Some(t);
                }
            }
        }
    } else {
        let dist_sq = ox * ox + oz * oz;
        if dist_sq <= radius * radius {
            if dir_y.abs() <= 0.000001 {
                if origin_y >= capsule_bottom && origin_y <= capsule_top {
                    best_t = Some(0.0);
                }
            } else {
                let t_bottom = (capsule_bottom - origin_y) / dir_y;
                let t_top = (capsule_top - origin_y) / dir_y;
                let t_entry = t_bottom.min(t_top);
                let t_exit = t_bottom.max(t_top);
                if t_exit >= 0.0 {
                    let t = if t_entry >= 0.0 { t_entry } else { 0.0 };
                    if t <= max_distance {
                        best_t = Some(t);
                    }
                }
            }
        }
    }

    if let Some(t) = ray_sphere_hit(
        origin_x,
        origin_y,
        origin_z,
        dir_x,
        dir_y,
        dir_z,
        player.pos_x,
        capsule_bottom,
        player.pos_z,
        radius,
    ) {
        if t <= max_distance && best_t.is_none_or(|best| t < best) {
            best_t = Some(t);
        }
    }

    if let Some(t) = ray_sphere_hit(
        origin_x,
        origin_y,
        origin_z,
        dir_x,
        dir_y,
        dir_z,
        player.pos_x,
        capsule_top,
        player.pos_z,
        radius,
    ) {
        if t <= max_distance && best_t.is_none_or(|best| t < best) {
            best_t = Some(t);
        }
    }

    best_t
}

pub(crate) fn aoe_hits_player(
    impact_x: f32,
    impact_y: f32,
    impact_z: f32,
    aoe_radius: f32,
    player: &CombatActorSnapshot,
) -> bool {
    let dx = player.pos_x - impact_x;
    let dz = player.pos_z - impact_z;
    let radius = player.hit_radius + aoe_radius;
    let horiz_sq = dx * dx + dz * dz;
    if horiz_sq > radius * radius {
        return false;
    }

    let capsule_bottom = player.pos_y - aoe_radius;
    let capsule_top = player.pos_y + player.hit_height + aoe_radius;
    impact_y >= capsule_bottom && impact_y <= capsule_top
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) enum CombatAreaShape {
    Disc {
        radius: f32,
    },
    Cone {
        range: f32,
        angle_degrees: f32,
        vertical_tolerance: Option<f32>,
    },
    Rectangle {
        length: f32,
        width: f32,
        vertical_tolerance: Option<f32>,
    },
}

impl CombatAreaShape {
    pub(crate) fn query_radius(self) -> f32 {
        match self {
            Self::Disc { radius } => radius,
            Self::Cone { range, .. } => range,
            Self::Rectangle { length, width, .. } => length.max(0.0).hypot(width.max(0.0) * 0.5),
        }
    }

    pub(crate) fn contains_player_xz(
        self,
        origin_x: f32,
        origin_z: f32,
        facing_yaw: f32,
        player: &CombatActorSnapshot,
        dot_epsilon: f32,
    ) -> bool {
        match self {
            Self::Disc { radius } => target_within_area_range_xz(
                origin_x,
                origin_z,
                player.pos_x,
                player.pos_z,
                player.hit_radius,
                radius,
            ),
            Self::Cone {
                range,
                angle_degrees,
                ..
            } => {
                if !target_within_area_range_xz(
                    origin_x,
                    origin_z,
                    player.pos_x,
                    player.pos_z,
                    player.hit_radius,
                    range,
                ) {
                    return false;
                }

                is_direction_within_facing_arc(
                    facing_yaw,
                    player.pos_x - origin_x,
                    player.pos_z - origin_z,
                    angle_degrees.to_radians(),
                    dot_epsilon,
                )
            }
            Self::Rectangle { length, width, .. } => {
                let forward_x = facing_yaw.sin();
                let forward_z = facing_yaw.cos();
                let right_x = forward_z;
                let right_z = -forward_x;
                let dx = player.pos_x - origin_x;
                let dz = player.pos_z - origin_z;
                let local_forward = dx * forward_x + dz * forward_z;
                let local_right = dx * right_x + dz * right_z;
                let half_width = width.max(0.0) * 0.5;
                let closest_forward = local_forward.clamp(0.0, length.max(0.0));
                let closest_right = local_right.clamp(-half_width, half_width);
                let forward_delta = local_forward - closest_forward;
                let right_delta = local_right - closest_right;
                let target_radius = player.hit_radius.max(0.0);
                forward_delta * forward_delta + right_delta * right_delta
                    <= target_radius * target_radius
            }
        }
    }

    pub(crate) fn contains_player(
        self,
        origin_x: f32,
        origin_y: f32,
        origin_z: f32,
        facing_yaw: f32,
        player: &CombatActorSnapshot,
        dot_epsilon: f32,
    ) -> bool {
        match self {
            Self::Disc { radius } => aoe_hits_player(origin_x, origin_y, origin_z, radius, player),
            Self::Cone {
                vertical_tolerance, ..
            }
            | Self::Rectangle {
                vertical_tolerance, ..
            } => {
                if !self.contains_player_xz(origin_x, origin_z, facing_yaw, player, dot_epsilon) {
                    return false;
                }
                vertical_tolerance
                    .map(|tolerance| y_within_player_capsule(origin_y, tolerance, player))
                    .unwrap_or(true)
            }
        }
    }
}

pub(crate) fn y_within_player_capsule(
    y: f32,
    vertical_tolerance: f32,
    player: &CombatActorSnapshot,
) -> bool {
    let capsule_bottom = player.pos_y - vertical_tolerance;
    let capsule_top = player.pos_y + player.hit_height + vertical_tolerance;
    y >= capsule_bottom && y <= capsule_top
}

pub(crate) fn target_within_area_range_xz(
    source_x: f32,
    source_z: f32,
    target_x: f32,
    target_z: f32,
    target_radius: f32,
    range: f32,
) -> bool {
    let dx = target_x - source_x;
    let dz = target_z - source_z;
    let reach = range.max(0.0) + target_radius.max(0.0);
    dx * dx + dz * dz <= reach * reach
}

fn point_inside_capsule(
    x: f32,
    y: f32,
    z: f32,
    player: &CombatActorSnapshot,
    radius_padding: f32,
) -> bool {
    let capsule_bottom = player.pos_y;
    let capsule_top = player.pos_y + player.hit_height;
    let closest_y = y.clamp(capsule_bottom, capsule_top);
    let dx = x - player.pos_x;
    let dy = y - closest_y;
    let dz = z - player.pos_z;
    let radius = player.hit_radius + radius_padding;
    dx * dx + dy * dy + dz * dz <= radius * radius
}

fn ray_sphere_hit(
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    center_x: f32,
    center_y: f32,
    center_z: f32,
    radius: f32,
) -> Option<f32> {
    let oc_x = origin_x - center_x;
    let oc_y = origin_y - center_y;
    let oc_z = origin_z - center_z;
    let a = dir_x * dir_x + dir_y * dir_y + dir_z * dir_z;
    let b = 2.0 * (oc_x * dir_x + oc_y * dir_y + oc_z * dir_z);
    let c = oc_x * oc_x + oc_y * oc_y + oc_z * oc_z - radius * radius;
    let disc = b * b - 4.0 * a * c;
    if disc < 0.0 {
        return None;
    }
    let sqrt_disc = disc.sqrt();
    let inv_denom = 0.5 / a;
    let t1 = (-b - sqrt_disc) * inv_denom;
    let t2 = (-b + sqrt_disc) * inv_denom;
    if t1 >= 0.0 {
        Some(t1)
    } else if t2 >= 0.0 {
        Some(t2)
    } else {
        None
    }
}

fn world_hit_on_segment(
    ctx: &ReducerContext,
    actor: Identity,
    seed: Option<u64>,
    flat_ground_only: bool,
    open_world_scene_name: Option<&str>,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
) -> Option<(WorldRayHit, f32)> {
    let dx = end_x - start_x;
    let dy = end_y - start_y;
    let dz = end_z - start_z;
    let dist_sq = dx * dx + dy * dy + dz * dz;
    if dist_sq <= EPS {
        return None;
    }

    let distance = dist_sq.sqrt();
    let inv = 1.0 / distance;
    let dir_x = dx * inv;
    let dir_y = dy * inv;
    let dir_z = dz * inv;

    let static_hit = raycast_world_with_layout_for_scene(
        seed,
        flat_ground_only,
        open_world_scene_name,
        WorldRaycastRequest {
            origin_x: start_x,
            origin_y: start_y,
            origin_z: start_z,
            dir_x,
            dir_y,
            dir_z,
            max_distance: distance,
            radius,
        },
    );
    let dynamic_hit = first_active_world_obstacle_hit(
        ctx, actor, start_x, start_y, start_z, end_x, end_y, end_z, radius,
    );
    let hit = match (static_hit, dynamic_hit) {
        (Some(static_hit), Some(dynamic_hit)) => {
            if dynamic_hit.t < static_hit.t {
                dynamic_hit
            } else {
                static_hit
            }
        }
        (Some(hit), None) | (None, Some(hit)) => hit,
        (None, None) => return None,
    };

    Some((hit, distance))
}

// Resolves the terrain/ground surface Y at (x, z) using the caster's world context (arena seed vs.
// open-world scene). Use this for terrain-conforming projectiles that need to hug the ground.
pub(crate) fn terrain_surface_y_for_caster(
    ctx: &ReducerContext,
    caster: Identity,
    x: f32,
    z: f32,
    sample_y: f32,
) -> f32 {
    let seed = arena_seed_for_identity(ctx, caster);
    let flat_ground_only = flat_ground_only_for_identity(ctx, caster);
    let open_world_scene_name = if seed.is_some() {
        None
    } else {
        Some(open_world_scene_name_for_identity(ctx, caster))
    };
    surface_height_for_world_at_y_with_layout_for_scene(
        seed,
        flat_ground_only,
        open_world_scene_name.as_deref(),
        x,
        z,
        sample_y,
    )
}

fn flat_ground_only_for_identity(ctx: &ReducerContext, identity: Identity) -> bool {
    let Some(ResolvedWorldContext::Instance(instance_id)) =
        resolve_player_world_context(ctx, identity)
    else {
        return false;
    };
    is_training_instance(ctx, instance_id)
}

fn shared_flat_ground_only_for_identities(ctx: &ReducerContext, a: Identity, b: Identity) -> bool {
    let Some(ResolvedWorldContext::Instance(instance_a)) = resolve_player_world_context(ctx, a)
    else {
        return false;
    };
    let Some(ResolvedWorldContext::Instance(instance_b)) = resolve_player_world_context(ctx, b)
    else {
        return false;
    };
    if instance_a != instance_b {
        return false;
    }
    is_training_instance(ctx, instance_a)
}

#[cfg(test)]
mod tests {
    use super::{
        aoe_hits_player, first_player_hit_on_segment, line_of_sight_target_points,
        raycast_capsule_with_padding, CombatActorSnapshot, CombatAreaShape, SceneHitKind,
    };
    use spacetimedb::Identity;

    fn test_identity(byte: u8) -> Identity {
        Identity::from_hex(format!("{byte:064x}").as_str()).expect("test identity should parse")
    }

    fn snapshot(id: Identity, pos_x: f32, pos_y: f32, pos_z: f32) -> CombatActorSnapshot {
        CombatActorSnapshot {
            player_id: id,
            alive: true,
            pos_x,
            pos_y,
            pos_z,
            facing_yaw: 0.0,
            grounded: true,
            hit_radius: 0.5,
            hit_height: 1.8,
            last_processed_tick: 0,
        }
    }

    #[test]
    fn capsule_raycast_hits_when_fast_projectile_crosses_target_between_ticks() {
        let target = snapshot(test_identity(2), 5.0, 0.0, 0.0);

        let hit_t = raycast_capsule_with_padding(0.0, 1.0, 0.0, 1.0, 0.0, 0.0, 10.0, &target, 0.1)
            .expect("swept segment should hit the capsule even though the endpoint is past it");

        assert!(
            (hit_t - 4.4).abs() < 0.001,
            "expected front edge of padded capsule at t=4.4, got {hit_t}"
        );
    }

    #[test]
    fn capsule_raycast_misses_when_target_moves_out_of_arrow_path() {
        let target = snapshot(test_identity(2), 5.0, 0.0, 1.0);

        assert!(
            raycast_capsule_with_padding(0.0, 1.0, 0.0, 1.0, 0.0, 0.0, 10.0, &target, 0.1)
                .is_none(),
            "path is outside the target capsule plus projectile radius"
        );
    }

    #[test]
    fn first_player_hit_on_segment_returns_selected_target_when_unobstructed() {
        let caster = test_identity(1);
        let target = test_identity(2);
        let players = [snapshot(target, 6.0, 0.0, 0.0)];

        let hit = first_player_hit_on_segment(
            caster,
            0.0,
            1.0,
            0.0,
            1.0,
            0.0,
            0.0,
            10.0,
            0.1,
            &players,
            |_| true,
        )
        .expect("unobstructed target should be hit");

        assert!(matches!(hit.kind, SceneHitKind::Player(id) if id == target));
        assert!((hit.t - 5.4).abs() < 0.001);
    }

    #[test]
    fn line_of_sight_target_points_include_center_and_capsule_sides() {
        let caster = snapshot(test_identity(1), 0.0, 0.0, 0.0);
        let target = snapshot(test_identity(2), 10.0, 2.0, 0.0);

        let points = line_of_sight_target_points(&caster, &target);

        assert_eq!(points.len(), 6);
        assert!((points[0].0 - 10.0).abs() < 0.001);
        assert!((points[0].1 - 3.35).abs() < 0.001);
        assert!((points[0].2 - 0.0).abs() < 0.001);
        assert!((points[1].0 - 10.0).abs() < 0.001);
        assert!((points[1].2 - 0.375).abs() < 0.001);
        assert!((points[2].2 + 0.375).abs() < 0.001);
    }

    #[test]
    fn first_player_hit_on_segment_prefers_intervening_enemy_before_selected_target() {
        let caster = test_identity(1);
        let blocker = test_identity(2);
        let selected = test_identity(3);
        let players = [
            snapshot(selected, 8.0, 0.0, 0.0),
            snapshot(blocker, 4.0, 0.0, 0.0),
        ];

        let hit = first_player_hit_on_segment(
            caster,
            0.0,
            1.0,
            0.0,
            1.0,
            0.0,
            0.0,
            12.0,
            0.1,
            &players,
            |_| true,
        )
        .expect("intervening enemy should be the first player hit");

        assert!(matches!(hit.kind, SceneHitKind::Player(id) if id == blocker));
        assert!((hit.t - 3.4).abs() < 0.001);
    }

    #[test]
    fn combat_area_disc_contains_by_legacy_radius_volume() {
        let target = snapshot(test_identity(2), 2.4, 0.0, 0.0);
        let outside = snapshot(test_identity(3), 3.6, 0.0, 0.0);

        assert!(aoe_hits_player(0.0, 0.0, 0.0, 2.0, &target));
        assert!(!aoe_hits_player(0.0, 0.0, 0.0, 2.0, &outside));
        assert!(
            CombatAreaShape::Disc { radius: 2.0 }.contains_player_xz(0.0, 0.0, 0.0, &target, 0.0)
        );
        assert!(
            !CombatAreaShape::Disc { radius: 2.0 }.contains_player_xz(0.0, 0.0, 0.0, &outside, 0.0)
        );
    }

    #[test]
    fn combat_area_cone_contains_front_and_rejects_side_and_range() {
        let shape = CombatAreaShape::Cone {
            range: 5.0,
            angle_degrees: 70.0,
            vertical_tolerance: None,
        };
        let front = snapshot(test_identity(2), 0.0, 0.0, 4.0);
        let side = snapshot(test_identity(3), 4.0, 0.0, 0.0);
        let far = snapshot(test_identity(4), 0.0, 0.0, 5.75);

        assert!(shape.contains_player_xz(0.0, 0.0, 0.0, &front, 0.0));
        assert!(!shape.contains_player_xz(0.0, 0.0, 0.0, &side, 0.0));
        assert!(!shape.contains_player_xz(0.0, 0.0, 0.0, &far, 0.0));
    }

    #[test]
    fn combat_area_cone_optional_vertical_tolerance_rejects_targets_far_above_origin() {
        let shape = CombatAreaShape::Cone {
            range: 5.0,
            angle_degrees: 70.0,
            vertical_tolerance: Some(2.5),
        };
        let near_y = snapshot(test_identity(2), 0.0, 0.0, 4.0);
        let high_y = snapshot(test_identity(3), 0.0, 5.0, 4.0);

        assert!(shape.contains_player(0.0, 0.0, 0.0, 0.0, &near_y, 0.0));
        assert!(!shape.contains_player(0.0, 0.0, 0.0, 0.0, &high_y, 0.0));
    }

    #[test]
    fn combat_area_rectangle_contains_forward_strip_and_rejects_side_and_behind() {
        let shape = CombatAreaShape::Rectangle {
            length: 5.0,
            width: 1.25,
            vertical_tolerance: None,
        };
        let front = snapshot(test_identity(2), 0.0, 0.0, 4.5);
        let edge_overlap = snapshot(test_identity(3), 0.9, 0.0, 3.0);
        let side = snapshot(test_identity(4), 1.2, 0.0, 3.0);
        let behind = snapshot(test_identity(5), 0.0, 0.0, -0.6);
        let far = snapshot(test_identity(6), 0.0, 0.0, 5.6);

        assert!(shape.contains_player_xz(0.0, 0.0, 0.0, &front, 0.0));
        assert!(shape.contains_player_xz(0.0, 0.0, 0.0, &edge_overlap, 0.0));
        assert!(!shape.contains_player_xz(0.0, 0.0, 0.0, &side, 0.0));
        assert!(!shape.contains_player_xz(0.0, 0.0, 0.0, &behind, 0.0));
        assert!(!shape.contains_player_xz(0.0, 0.0, 0.0, &far, 0.0));
    }

    #[test]
    fn first_player_hit_on_segment_ignores_dead_caster_and_other_worlds() {
        let caster = test_identity(1);
        let same_world = test_identity(2);
        let other_world = test_identity(3);
        let dead = test_identity(4);
        let mut dead_player = snapshot(dead, 2.0, 0.0, 0.0);
        dead_player.alive = false;
        let players = [
            snapshot(caster, 1.0, 0.0, 0.0),
            dead_player,
            snapshot(other_world, 3.0, 0.0, 0.0),
            snapshot(same_world, 5.0, 0.0, 0.0),
        ];

        let hit = first_player_hit_on_segment(
            caster,
            0.0,
            1.0,
            0.0,
            1.0,
            0.0,
            0.0,
            8.0,
            0.1,
            &players,
            |target| target == same_world,
        )
        .expect("same-world live target should be hit");

        assert!(matches!(hit.kind, SceneHitKind::Player(id) if id == same_world));
    }
}

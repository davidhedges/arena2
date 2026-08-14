use spacetimedb::{table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::{resolve_player_world_context, ResolvedWorldContext, WorldRayHit};
use crate::combat::timestamp_to_micros;
use crate::relations::can_harm;

use super::SanctuarySecondaryTunables;

pub(super) const COLLISION_EPSILON: f32 = 0.001;

#[table(accessor = active_sanctuary_zone, public)]
#[derive(Clone)]
pub struct ActiveSanctuaryZone {
    #[primary_key]
    #[auto_inc]
    pub zone_id: u64,
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
    pub center_x: f32,
    pub center_y: f32,
    pub center_z: f32,
    pub radius: f32,
    pub spawned_at: Timestamp,
    pub expires_at: Timestamp,
    #[index(btree)]
    pub expires_at_micros: i64,
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn spawn_sanctuary_zone(
    ctx: &ReducerContext,
    caster: Identity,
    spell_id: &str,
    ability_id: &str,
    center_x: f32,
    center_y: f32,
    center_z: f32,
    radius: f32,
    tunables: &SanctuarySecondaryTunables,
    now: Timestamp,
) -> Result<(), String> {
    let Some(world_context) = resolve_player_world_context(ctx, caster) else {
        return Err("Cannot place Sanctuary without a resolved world context".to_string());
    };
    let (world_kind, instance_id, open_world_scene_name) = match world_context {
        ResolvedWorldContext::Open(scene) => ("OPEN".to_string(), None, scene),
        ResolvedWorldContext::Instance(instance_id) => {
            ("INSTANCE".to_string(), Some(instance_id), String::new())
        }
    };
    let expires_at = now + tunables.duration;

    // A caster owns at most one Sanctuary. This keeps overlap/collision state
    // deterministic if cooldown modifiers ever permit an early recast.
    ctx.db.active_sanctuary_zone().owner().delete(caster);
    ctx.db.active_sanctuary_zone().insert(ActiveSanctuaryZone {
        zone_id: 0,
        owner: caster,
        spell_id: spell_id.trim().to_ascii_uppercase(),
        ability_id: ability_id.trim().to_ascii_uppercase(),
        visual_resource_path: tunables.visual_resource_path.clone(),
        world_kind,
        instance_id,
        instance_scope_id: instance_id.unwrap_or_default(),
        open_world_scene_name,
        center_x,
        center_y,
        center_z,
        radius: radius.max(0.0),
        spawned_at: now,
        expires_at,
        expires_at_micros: timestamp_to_micros(expires_at),
    });
    Ok(())
}

pub(crate) fn expire_sanctuary_zones(ctx: &ReducerContext, now: Timestamp) {
    let due: Vec<u64> = ctx
        .db
        .active_sanctuary_zone()
        .expires_at_micros()
        .filter(..=timestamp_to_micros(now))
        .map(|row| row.zone_id)
        .collect();
    for zone_id in due {
        ctx.db.active_sanctuary_zone().zone_id().delete(zone_id);
    }
}

pub(crate) fn clear_sanctuary_zones_for_owner(ctx: &ReducerContext, owner: Identity) {
    ctx.db.active_sanctuary_zone().owner().delete(owner);
}

pub(super) fn actor_shares_zone_world(
    ctx: &ReducerContext,
    actor: Identity,
    world_kind: &str,
    zone_instance_id: Option<u64>,
    open_world_scene_name: &str,
) -> bool {
    let Some(context) = resolve_player_world_context(ctx, actor) else {
        return false;
    };
    match context {
        ResolvedWorldContext::Open(scene) => world_kind == "OPEN" && open_world_scene_name == scene,
        ResolvedWorldContext::Instance(instance_id) => {
            world_kind == "INSTANCE" && zone_instance_id == Some(instance_id)
        }
    }
}

fn hostile_sanctuaries_for_actor(
    ctx: &ReducerContext,
    actor: Identity,
) -> impl Iterator<Item = ActiveSanctuaryZone> + '_ {
    ctx.db.active_sanctuary_zone().iter().filter(move |zone| {
        zone.expires_at > ctx.timestamp
            && actor_shares_zone_world(
                ctx,
                actor,
                zone.world_kind.as_str(),
                zone.instance_id,
                zone.open_world_scene_name.as_str(),
            )
            && can_harm(ctx, zone.owner, actor)
    })
}

/// Blocks hostile actors from entering a Sanctuary. An actor already inside
/// when the wall appears may leave, preventing the cast from trapping an enemy
/// in a newly-created collision band.
#[allow(clippy::too_many_arguments)]
pub(crate) fn resolve_hostile_sanctuary_movement(
    ctx: &ReducerContext,
    actor: Identity,
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    actor_radius: f32,
) -> (f32, f32) {
    let mut out_x = target_x;
    let mut out_z = target_z;
    for zone in hostile_sanctuaries_for_actor(ctx, actor) {
        let collision_radius = zone.radius + actor_radius.max(0.0);
        let Some(fraction) = segment_circle_entry_fraction(
            start_x,
            start_z,
            out_x,
            out_z,
            zone.center_x,
            zone.center_z,
            collision_radius,
        ) else {
            continue;
        };
        (out_x, out_z) = clip_movement_before_boundary(start_x, start_z, out_x, out_z, fraction);
    }
    (out_x, out_z)
}

/// Shared movement-only wall policy: stop immediately before the first
/// authoritative boundary crossing. Shape-specific spells supply the crossing
/// fraction, while Sanctuary owns the established collision epsilon behavior.
pub(super) fn clip_movement_before_boundary(
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    fraction: f32,
) -> (f32, f32) {
    let safe_fraction = (fraction - COLLISION_EPSILON).max(0.0);
    (
        start_x + (target_x - start_x) * safe_fraction,
        start_z + (target_z - start_z) * safe_fraction,
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn first_hostile_sanctuary_projectile_hit(
    ctx: &ReducerContext,
    projectile_owner: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    projectile_radius: f32,
) -> Option<WorldRayHit> {
    let dx = end_x - start_x;
    let dy = end_y - start_y;
    let dz = end_z - start_z;
    let distance = (dx * dx + dy * dy + dz * dz).sqrt();
    if distance <= f32::EPSILON {
        return None;
    }

    let mut best: Option<WorldRayHit> = None;
    for zone in hostile_sanctuaries_for_actor(ctx, projectile_owner) {
        let Some(fraction) = segment_circle_boundary_fraction(
            start_x,
            start_z,
            end_x,
            end_z,
            zone.center_x,
            zone.center_z,
            zone.radius,
            projectile_radius.max(0.0),
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

pub(crate) fn area_overlaps_hostile_sanctuary(
    ctx: &ReducerContext,
    caster: Identity,
    center_x: f32,
    center_z: f32,
    area_radius: f32,
) -> bool {
    hostile_sanctuaries_for_actor(ctx, caster).any(|zone| {
        let combined_radius = zone.radius + area_radius.max(0.0);
        let dx = center_x - zone.center_x;
        let dz = center_z - zone.center_z;
        dx * dx + dz * dz <= combined_radius * combined_radius
    })
}

fn segment_circle_entry_fraction(
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    center_x: f32,
    center_z: f32,
    radius: f32,
) -> Option<f32> {
    let start_dx = start_x - center_x;
    let start_dz = start_z - center_z;
    let radius = radius.max(0.0);
    if start_dx * start_dx + start_dz * start_dz <= radius * radius {
        return None;
    }
    segment_circle_intersections(start_x, start_z, end_x, end_z, center_x, center_z, radius)
        .and_then(|(entry, _)| (0.0..=1.0).contains(&entry).then_some(entry))
}

fn segment_circle_boundary_fraction(
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    center_x: f32,
    center_z: f32,
    radius: f32,
    padding: f32,
) -> Option<f32> {
    let start_dx = start_x - center_x;
    let start_dz = start_z - center_z;
    let start_distance = (start_dx * start_dx + start_dz * start_dz).sqrt();
    let outer_radius = radius.max(0.0) + padding.max(0.0);
    let inner_radius = (radius.max(0.0) - padding.max(0.0)).max(0.0);

    if start_distance > outer_radius {
        return segment_circle_intersections(
            start_x,
            start_z,
            end_x,
            end_z,
            center_x,
            center_z,
            outer_radius,
        )
        .and_then(|(entry, _)| (0.0..=1.0).contains(&entry).then_some(entry));
    }
    if start_distance < inner_radius {
        return segment_circle_intersections(
            start_x,
            start_z,
            end_x,
            end_z,
            center_x,
            center_z,
            inner_radius,
        )
        .and_then(|(_, exit)| (0.0..=1.0).contains(&exit).then_some(exit));
    }

    Some(0.0)
}

fn segment_circle_intersections(
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    center_x: f32,
    center_z: f32,
    radius: f32,
) -> Option<(f32, f32)> {
    let dx = end_x - start_x;
    let dz = end_z - start_z;
    let a = dx * dx + dz * dz;
    if a <= f32::EPSILON {
        return None;
    }
    let offset_x = start_x - center_x;
    let offset_z = start_z - center_z;
    let b = 2.0 * (offset_x * dx + offset_z * dz);
    let c = offset_x * offset_x + offset_z * offset_z - radius * radius;
    let discriminant = b * b - 4.0 * a * c;
    if discriminant < 0.0 {
        return None;
    }
    let root = discriminant.sqrt();
    let entry = (-b - root) / (2.0 * a);
    let exit = (-b + root) / (2.0 * a);
    Some((entry, exit))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn movement_from_outside_stops_at_sanctuary_edge() {
        let fraction = segment_circle_entry_fraction(-5.0, 0.0, 5.0, 0.0, 0.0, 0.0, 2.5)
            .expect("segment should enter circle");
        assert!((fraction - 0.25).abs() < 0.0001);
    }

    #[test]
    fn movement_starting_inside_is_allowed_to_leave() {
        assert_eq!(
            segment_circle_entry_fraction(0.0, 0.0, 5.0, 0.0, 0.0, 0.0, 2.5),
            None
        );
    }

    #[test]
    fn projectiles_hit_the_wall_from_either_side() {
        let entering = segment_circle_boundary_fraction(-5.0, 0.0, 5.0, 0.0, 0.0, 0.0, 2.0, 0.25)
            .expect("outside projectile should enter wall");
        let exiting = segment_circle_boundary_fraction(0.0, 0.0, 5.0, 0.0, 0.0, 0.0, 2.0, 0.25)
            .expect("inside projectile should exit through wall");
        assert!((entering - 0.275).abs() < 0.0001);
        assert!((exiting - 0.35).abs() < 0.0001);
    }

    #[test]
    fn short_segments_that_do_not_reach_the_wall_do_not_collide() {
        assert_eq!(
            segment_circle_entry_fraction(-5.0, 0.0, -4.0, 0.0, 0.0, 0.0, 2.0),
            None
        );
        assert_eq!(
            segment_circle_boundary_fraction(0.0, 0.0, 0.5, 0.0, 0.0, 0.0, 2.0, 0.25),
            None
        );
    }
}

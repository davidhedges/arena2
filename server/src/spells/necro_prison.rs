use spacetimedb::{table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::{resolve_player_world_context, ResolvedWorldContext};
use crate::combat::timestamp_to_micros;
use crate::relations::can_harm;

use super::sanctuary::{actor_shares_zone_world, clip_movement_before_boundary};
use super::NecroPrisonSecondaryTunables;

#[table(accessor = active_necro_prison, public)]
#[derive(Clone)]
pub struct ActiveNecroPrison {
    #[primary_key]
    #[auto_inc]
    pub prison_id: u64,
    #[index(btree)]
    pub owner: Identity,
    pub spell_id: String,
    pub ability_id: String,
    pub visual_resource_path: String,
    pub dissipate_visual_resource_path: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    /// Query-safe scalar mirror of `instance_id`; zero means open world.
    #[index(btree)]
    pub instance_scope_id: u64,
    pub open_world_scene_name: String,
    pub center_x: f32,
    pub center_y: f32,
    pub center_z: f32,
    /// Unity/server yaw in radians; zero faces +Z.
    pub facing_yaw: f32,
    /// Circumradius of the equilateral triangular movement boundary.
    pub radius: f32,
    pub spawned_at: Timestamp,
    pub expires_at: Timestamp,
    #[index(btree)]
    pub expires_at_micros: i64,
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn spawn_necro_prison(
    ctx: &ReducerContext,
    caster: Identity,
    spell_id: &str,
    ability_id: &str,
    center_x: f32,
    center_y: f32,
    center_z: f32,
    facing_yaw: f32,
    radius: f32,
    tunables: &NecroPrisonSecondaryTunables,
    now: Timestamp,
) -> Result<(), String> {
    let Some(world_context) = resolve_player_world_context(ctx, caster) else {
        return Err("Cannot place Necro Prison without a resolved world context".to_string());
    };
    let (world_kind, instance_id, open_world_scene_name) = match world_context {
        ResolvedWorldContext::Open(scene) => ("OPEN".to_string(), None, scene),
        ResolvedWorldContext::Instance(instance_id) => {
            ("INSTANCE".to_string(), Some(instance_id), String::new())
        }
    };
    let expires_at = now + tunables.duration;

    // A caster owns at most one prison, matching Sanctuary's deterministic
    // replacement policy if cooldown modifiers permit an early recast.
    ctx.db.active_necro_prison().owner().delete(caster);
    ctx.db.active_necro_prison().insert(ActiveNecroPrison {
        prison_id: 0,
        owner: caster,
        spell_id: spell_id.trim().to_ascii_uppercase(),
        ability_id: ability_id.trim().to_ascii_uppercase(),
        visual_resource_path: tunables.visual_resource_path.clone(),
        dissipate_visual_resource_path: tunables.dissipate_visual_resource_path.clone(),
        world_kind,
        instance_id,
        instance_scope_id: instance_id.unwrap_or_default(),
        open_world_scene_name,
        center_x,
        center_y,
        center_z,
        facing_yaw: facing_yaw.rem_euclid(std::f32::consts::TAU),
        radius: radius.max(0.0),
        spawned_at: now,
        expires_at,
        expires_at_micros: timestamp_to_micros(expires_at),
    });
    Ok(())
}

pub(crate) fn expire_necro_prisons(ctx: &ReducerContext, now: Timestamp) {
    let due: Vec<u64> = ctx
        .db
        .active_necro_prison()
        .expires_at_micros()
        .filter(..=timestamp_to_micros(now))
        .map(|row| row.prison_id)
        .collect();
    for prison_id in due {
        ctx.db.active_necro_prison().prison_id().delete(prison_id);
    }
}

pub(crate) fn clear_necro_prisons_for_owner(ctx: &ReducerContext, owner: Identity) {
    ctx.db.active_necro_prison().owner().delete(owner);
}

/// Traps hostile actors already inside the triangular wall. Movement within
/// the prison remains free, but the first inside-to-outside crossing is clipped
/// with Sanctuary's established movement-only boundary policy. This function
/// is intentionally not called by LOS, projectile, or area-overlap queries.
#[allow(clippy::too_many_arguments)]
pub(crate) fn resolve_hostile_necro_prison_movement(
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
    for prison in ctx.db.active_necro_prison().iter().filter(|prison| {
        prison.expires_at > ctx.timestamp
            && actor_shares_zone_world(
                ctx,
                actor,
                prison.world_kind.as_str(),
                prison.instance_id,
                prison.open_world_scene_name.as_str(),
            )
            && can_harm(ctx, prison.owner, actor)
    }) {
        let Some(fraction) = segment_equilateral_triangle_exit_fraction(
            start_x,
            start_z,
            out_x,
            out_z,
            prison.center_x,
            prison.center_z,
            prison.facing_yaw,
            prison.radius,
            actor_radius.max(0.0),
        ) else {
            continue;
        };
        (out_x, out_z) = clip_movement_before_boundary(start_x, start_z, out_x, out_z, fraction);
    }
    (out_x, out_z)
}

#[allow(clippy::too_many_arguments)]
fn segment_equilateral_triangle_exit_fraction(
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    center_x: f32,
    center_z: f32,
    yaw: f32,
    circumradius: f32,
    padding: f32,
) -> Option<f32> {
    let radius = circumradius.max(0.0);
    if radius <= f32::EPSILON {
        return None;
    }

    // The requested prefab's single point faces local -Z; use the same local
    // orientation so presentation, aim indicator, and collision stay aligned.
    let local_vertices = [
        (0.0, -radius),
        (radius * 0.866_025_4, radius * 0.5),
        (-radius * 0.866_025_4, radius * 0.5),
    ];
    let sin = yaw.sin();
    let cos = yaw.cos();
    let vertices =
        local_vertices.map(|(x, z)| (center_x + x * cos + z * sin, center_z - x * sin + z * cos));

    let mut exit_fraction: Option<f32> = None;
    for index in 0..3 {
        let a = vertices[index];
        let b = vertices[(index + 1) % 3];
        let edge_x = b.0 - a.0;
        let edge_z = b.1 - a.1;
        let edge_length = (edge_x * edge_x + edge_z * edge_z).sqrt();
        let threshold = padding * edge_length;
        let start_side = edge_x * (start_z - a.1) - edge_z * (start_x - a.0);
        if start_side < threshold {
            return None;
        }
        let end_side = edge_x * (end_z - a.1) - edge_z * (end_x - a.0);
        if end_side >= threshold {
            continue;
        }
        let denominator = start_side - end_side;
        if denominator <= f32::EPSILON {
            continue;
        }
        let fraction = (start_side - threshold) / denominator;
        if (0.0..=1.0).contains(&fraction)
            && exit_fraction.is_none_or(|existing| fraction < existing)
        {
            exit_fraction = Some(fraction);
        }
    }
    exit_fraction
}

#[cfg(test)]
mod tests {
    use super::segment_equilateral_triangle_exit_fraction;

    #[test]
    fn hostile_actor_inside_is_clipped_at_triangle_exit() {
        let fraction = segment_equilateral_triangle_exit_fraction(
            0.0, 0.0, 0.0, -6.0, 0.0, 0.0, 0.0, 4.0, 0.0,
        )
        .expect("inside movement should meet the rear point");
        assert!((fraction - (2.0 / 3.0)).abs() < 0.0001);
    }

    #[test]
    fn movement_inside_triangle_remains_free() {
        assert_eq!(
            segment_equilateral_triangle_exit_fraction(0.0, 0.0, 0.5, 0.5, 0.0, 0.0, 0.0, 4.0, 0.0,),
            None
        );
    }

    #[test]
    fn movement_from_outside_is_not_treated_as_trapped() {
        assert_eq!(
            segment_equilateral_triangle_exit_fraction(
                0.0, -6.0, 0.0, 0.0, 0.0, 0.0, 0.0, 4.0, 0.0,
            ),
            None
        );
    }

    #[test]
    fn actor_radius_insets_the_triangle_wall() {
        let fraction = segment_equilateral_triangle_exit_fraction(
            0.0, 0.0, 0.0, -6.0, 0.0, 0.0, 0.0, 4.0, 0.5,
        )
        .expect("inside actor should meet the inset wall");
        assert!(fraction < 2.0 / 3.0);
    }
}

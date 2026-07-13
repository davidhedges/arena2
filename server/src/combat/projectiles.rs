use std::collections::{HashMap, HashSet};
use std::time::Duration;

use spacetimedb::{Identity, ReducerContext, Table, Timestamp};

use crate::arena::players_share_world_context;
use crate::combat::actor_snapshot::{CombatActorSnapshot, CombatActorSnapshotSet};
use crate::combat::scene_query::{
    first_hit_on_segment_candidates_with_world_stats, first_player_hit_on_segment_candidates,
    first_world_hit_on_segment_with_stats, raycast_capsule_with_padding,
    terrain_surface_y_for_caster, SceneHitKind,
};
use crate::combat::{
    hostile_targeted_ability_misses, queue_effects, timestamp_to_micros, ActiveCombatProjectile,
    ActiveCombatProjectileTargetState, CombatEvent, CombatProjectileTickMetrics, DamageDelivery,
    EffectPacket, ProjectilePresentationEvent, StatusPolarity, COMBAT_EVENT_BLOCK,
    COMBAT_EVENT_CONTACT, COMBAT_EVENT_FIZZLE, COMBAT_EVENT_IMPACT, COMBAT_EVENT_MISS,
    COMBAT_EVENT_PARRY, COMBAT_EVENT_UPDATE, COMBAT_METADATA_NONE, COMBAT_SCALAR_NONE,
    COMBAT_SEQUENCE_NONE, DAMAGE_SOURCE_KIND_PROJECTILE,
};
use crate::defense::{
    resolve_defensible_combat_hit, CombatHitDeliveryKind, DefenseResolution, DefensibleCombatHit,
};
use crate::relations::can_harm;
use crate::resources::grant_primary_resource_for_melee_hit;
use crate::spells::{
    spell_definition_by_str, ImpactEffect, SpellBehavior, SpellId, SpellRuntimeDefinition,
};
use crate::world_collision::WorldRaycastStats;

#[allow(unused_imports)]
use crate::combat::active_combat_projectile as _;
#[allow(unused_imports)]
use crate::combat::active_combat_projectile_target_state as _;
#[allow(unused_imports)]
use crate::combat::combat_event as _;
#[allow(unused_imports)]
use crate::combat::combat_projectile_tick_metrics as _;
#[allow(unused_imports)]
use crate::combat::projectile_presentation_event as _;

const METRICS_ROW_KEY: &str = "latest";
const WORLD_COLLISION_FALLBACK_WARN_RATIO_PER_MILLE: u32 = 100;
const WORLD_COLLISION_FALLBACK_WARN_MIN_QUERIES: u32 = 10;

struct ProjectileAdvance {
    impacted: bool,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    impact_target: Option<Identity>,
}

#[derive(Default)]
struct ProjectileTickMetricsFrame {
    active_projectile_count: u32,
    rows_updated: u32,
    collision_candidate_scans: u32,
    world_collision_queries: u32,
    contacts_resolved: u32,
    update_events_emitted: u32,
    contact_events_emitted: u32,
    terminal_events_emitted: u32,
    block_parry_events_emitted: u32,
    linear_projectile_count: u32,
    homing_projectile_count: u32,
    orbit_projectile_count: u32,
    boomerang_projectile_count: u32,
    curved_projectile_count: u32,
    tick_micros: u32,
    world_gameplay_broadphase_candidates: u32,
    world_gameplay_narrowphase_tests: u32,
    world_gameplay_full_scan_fallbacks: u32,
    world_query_mesh_broadphase_candidates: u32,
    world_query_mesh_bvh_node_tests: u32,
    world_query_mesh_triangles_tested: u32,
    world_query_mesh_full_scan_fallbacks: u32,
    open_world_geometry_point_checks: u32,
}

#[allow(dead_code)]
pub(crate) fn tick_combat_projectiles(ctx: &ReducerContext, dt: f32) -> Result<(), String> {
    let actor_snapshots = CombatActorSnapshotSet::collect(ctx);
    tick_combat_projectiles_with_snapshots(ctx, dt, &actor_snapshots)
}

pub(crate) fn tick_combat_projectiles_with_snapshots(
    ctx: &ReducerContext,
    dt: f32,
    actor_snapshots: &CombatActorSnapshotSet,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let projectiles: Vec<ActiveCombatProjectile> =
        ctx.db.active_combat_projectile().iter().collect();
    if projectiles.is_empty() {
        maybe_record_empty_projectile_tick_metrics(ctx, now);
        return Ok(());
    }

    let players = actor_snapshots.as_slice();
    let player_index_by_id = actor_snapshots.index_by_id();
    let mut candidate_indices = Vec::new();
    let mut metrics = ProjectileTickMetricsFrame {
        active_projectile_count: projectiles.len().min(u32::MAX as usize) as u32,
        ..Default::default()
    };

    for projectile in projectiles {
        if ctx
            .db
            .active_combat_projectile()
            .projectile_instance_id()
            .find(projectile.projectile_instance_id.clone())
            .is_none()
        {
            continue;
        }

        tick_projectile_instance(
            ctx,
            now,
            dt,
            projectile,
            actor_snapshots,
            players,
            player_index_by_id,
            &mut candidate_indices,
            &mut metrics,
        );
    }

    record_projectile_tick_metrics(ctx, now, metrics);
    Ok(())
}

const PROJECTILE_MOTION_ORBIT_CASTER: &str = "ORBIT_CASTER";
const PROJECTILE_MOTION_BOOMERANG_CASTER: &str = "BOOMERANG_CASTER";
const PROJECTILE_MOTION_CURVED_TARGET: &str = "CURVED_TARGET";
const PROJECTILE_CONTACT_METADATA_KIND: &str = "PROJECTILE_CONTACT";
const PROJECTILE_CONTACT_TERMINAL_KEY: &str = "TERMINAL";
const PROJECTILE_CONTACT_NON_TERMINAL_VALUE: &str = "FALSE";

fn spell_definition_drives_active_projectile(definition: &SpellRuntimeDefinition) -> bool {
    definition.behavior == SpellBehavior::Projectile
        || definition.secondary.channel_projectile.is_some()
}

fn tick_projectile_instance(
    ctx: &ReducerContext,
    now: Timestamp,
    dt: f32,
    mut projectile: ActiveCombatProjectile,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    player_index_by_id: &HashMap<Identity, usize>,
    candidate_indices: &mut Vec<usize>,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    let spell_definition = if projectile.source_kind == "SPELL" {
        let Ok(spell_id) = SpellId::new(projectile.action_kind.as_str()) else {
            fizzle_projectile_and_finish(ctx, now, &projectile, metrics);
            return;
        };
        let Some(definition) = spell_definition_by_str(spell_id.as_str()) else {
            fizzle_projectile_and_finish(ctx, now, &projectile, metrics);
            return;
        };
        if !spell_definition_drives_active_projectile(definition) {
            fizzle_projectile_and_finish(ctx, now, &projectile, metrics);
            return;
        }
        Some(definition)
    } else {
        None
    };

    projectile.age += dt;
    record_motion_kind(&projectile, spell_definition, metrics);

    if projectile.motion_kind == PROJECTILE_MOTION_ORBIT_CASTER {
        tick_orbit_projectile_instance(
            ctx,
            now,
            dt,
            projectile,
            spell_definition,
            actor_snapshots,
            players,
            player_index_by_id,
            candidate_indices,
            metrics,
        );
        return;
    }
    if projectile.motion_kind == PROJECTILE_MOTION_BOOMERANG_CASTER {
        tick_boomerang_projectile_instance(
            ctx,
            now,
            dt,
            projectile,
            spell_definition,
            actor_snapshots,
            players,
            candidate_indices,
            metrics,
        );
        return;
    }

    let uses_curved_target = projectile.motion_kind == PROJECTILE_MOTION_CURVED_TARGET;
    if let Some(definition) = spell_definition {
        if !uses_curved_target && projectile.age <= projectile_homing_window_seconds(definition) {
            retarget_projectile_towards_live_target(
                ctx,
                &mut projectile,
                players,
                player_index_by_id,
                definition.spawn_height,
                definition.turn_rate,
                dt,
            );
        }
    }

    let advance = if uses_curved_target {
        advance_curved_target_projectile_with_collision(
            ctx,
            &mut projectile,
            spell_definition,
            actor_snapshots,
            players,
            candidate_indices,
            dt,
            metrics,
        )
    } else {
        advance_projectile_with_collision(
            ctx,
            &mut projectile,
            spell_definition,
            actor_snapshots,
            players,
            candidate_indices,
            dt,
            metrics,
        )
    };
    if advance.impacted {
        if let Some(target) = advance.impact_target {
            let Some(target_state) = players
                .iter()
                .find(|player| player.player_id == target)
                .copied()
            else {
                fizzle_projectile_and_finish(ctx, now, &projectile, metrics);
                return;
            };
            if projectile_targeted_hit_misses(ctx, &projectile, target, now) {
                emit_projectile_event(
                    ctx,
                    &projectile,
                    COMBAT_EVENT_MISS,
                    Some(target),
                    advance.end_x,
                    advance.end_y,
                    advance.end_z,
                    0,
                    now,
                    metrics,
                );
                finish_projectile_without_event(ctx, &projectile);
                return;
            }
            if resolve_projectile_defense(
                ctx,
                &projectile,
                &target_state,
                advance.end_x,
                advance.end_y,
                advance.end_z,
                now,
                metrics,
            ) {
                finish_projectile_without_event(ctx, &projectile);
                return;
            }
            let impact_damage = spell_definition
                .map(|definition| projectile_damage_at_current_lifetime(&projectile, definition))
                .unwrap_or(projectile.damage);
            emit_projectile_event(
                ctx,
                &projectile,
                COMBAT_EVENT_IMPACT,
                Some(target),
                advance.end_x,
                advance.end_y,
                advance.end_z,
                impact_damage,
                now,
                metrics,
            );
            if let Some(definition) = spell_definition {
                queue_spell_projectile_hit_effects(ctx, &projectile, definition, target);
            } else {
                queue_weapon_projectile_hit_effects(ctx, &projectile, target, now);
            }
            finish_projectile_without_event(ctx, &projectile);
        } else {
            emit_projectile_event(
                ctx,
                &projectile,
                COMBAT_EVENT_IMPACT,
                None,
                advance.end_x,
                advance.end_y,
                advance.end_z,
                0,
                now,
                metrics,
            );
            finish_projectile_without_event(ctx, &projectile);
        }
        return;
    }

    if projectile.age >= projectile.lifetime || projectile.traveled >= projectile.max_distance {
        emit_projectile_event(
            ctx,
            &projectile,
            COMBAT_EVENT_FIZZLE,
            None,
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
            0,
            now,
            metrics,
        );
        finish_projectile_without_event(ctx, &projectile);
        return;
    }

    let update_interval = spell_definition
        .map(|definition| definition.update_interval)
        .unwrap_or(projectile.update_interval_seconds);
    maybe_emit_projectile_update(ctx, &mut projectile, update_interval, now, dt, metrics);
    update_projectile_row(ctx, projectile, metrics);
}

fn projectile_homing_window_seconds(definition: &SpellRuntimeDefinition) -> f32 {
    definition
        .secondary
        .projectile
        .as_ref()
        .map(|projectile| projectile.homing_window_seconds)
        .unwrap_or(0.0)
}

fn push_impact_effect_packets(
    effects: &mut Vec<EffectPacket>,
    impact_effects: &[ImpactEffect],
    source: Identity,
    target: Identity,
    spell_id: &str,
    action_key: &str,
    positive_damage: bool,
) {
    for effect in impact_effects {
        if effect.requires_positive_damage() && !positive_damage {
            continue;
        }
        effects.push(effect.to_effect_packet(
            source,
            target,
            spell_id,
            StatusPolarity::Debuff,
            action_key,
        ));
    }
}

fn tick_orbit_projectile_instance(
    ctx: &ReducerContext,
    now: Timestamp,
    dt: f32,
    mut projectile: ActiveCombatProjectile,
    spell_definition: Option<&SpellRuntimeDefinition>,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    player_index_by_id: &HashMap<Identity, usize>,
    candidate_indices: &mut Vec<usize>,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    let Some(definition) = spell_definition else {
        fizzle_projectile_and_finish(ctx, now, &projectile, metrics);
        return;
    };
    let Some(caster) = players
        .iter()
        .find(|player| player.player_id == projectile.caster)
        .copied()
        .filter(|player| player.alive)
    else {
        emit_projectile_event(
            ctx,
            &projectile,
            COMBAT_EVENT_FIZZLE,
            None,
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
            0,
            now,
            metrics,
        );
        finish_projectile_without_event(ctx, &projectile);
        return;
    };

    if projectile.age >= projectile.lifetime {
        emit_projectile_event(
            ctx,
            &projectile,
            COMBAT_EVENT_FIZZLE,
            None,
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
            0,
            now,
            metrics,
        );
        finish_projectile_without_event(ctx, &projectile);
        return;
    }

    update_orbit_projectile_position(&mut projectile, &caster);
    if resolve_orbit_projectile_contacts(
        ctx,
        now,
        &projectile,
        definition,
        actor_snapshots,
        players,
        player_index_by_id,
        candidate_indices,
        metrics,
    ) {
        return;
    }

    maybe_emit_projectile_update(
        ctx,
        &mut projectile,
        definition.update_interval,
        now,
        dt,
        metrics,
    );
    update_projectile_row(ctx, projectile, metrics);
}

fn update_orbit_projectile_position(
    projectile: &mut ActiveCombatProjectile,
    caster: &CombatActorSnapshot,
) {
    let angle = projectile.orbit_initial_yaw
        + projectile.orbit_phase_offset_deg.to_radians()
        + projectile.orbit_angular_speed_deg_per_sec.to_radians() * projectile.age;
    projectile.pos_x = caster.pos_x + angle.sin() * projectile.orbit_radius;
    projectile.pos_y = caster.pos_y + projectile.orbit_height;
    projectile.pos_z = caster.pos_z + angle.cos() * projectile.orbit_radius;
    projectile.dir_x = angle.cos();
    projectile.dir_y = 0.0;
    projectile.dir_z = -angle.sin();
    projectile.traveled = projectile.orbit_angular_speed_deg_per_sec.to_radians()
        * projectile.orbit_radius
        * projectile.age;
}

fn resolve_orbit_projectile_contacts(
    ctx: &ReducerContext,
    now: Timestamp,
    projectile: &ActiveCombatProjectile,
    definition: &SpellRuntimeDefinition,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    player_index_by_id: &HashMap<Identity, usize>,
    candidate_indices: &mut Vec<usize>,
    metrics: &mut ProjectileTickMetricsFrame,
) -> bool {
    actor_snapshots.query_disc_indices(
        projectile.pos_x,
        projectile.pos_z,
        projectile.radius,
        candidate_indices,
    );

    let mut seen_targets = HashSet::new();
    let mut candidates = Vec::with_capacity(candidate_indices.len());
    for target in candidate_indices
        .iter()
        .filter_map(|index| players.get(*index))
    {
        if seen_targets.insert(target.player_id) {
            candidates.push(*target);
        }
    }

    let tracked_targets: Vec<Identity> = ctx
        .db
        .active_combat_projectile_target_state()
        .projectile_instance_id()
        .filter(&projectile.projectile_instance_id)
        .map(|state| state.target)
        .collect();
    for target_id in tracked_targets {
        if !seen_targets.insert(target_id) {
            continue;
        }
        if let Some(target) = player_index_by_id
            .get(&target_id)
            .and_then(|index| players.get(*index))
        {
            candidates.push(*target);
        }
    }

    for target in candidates {
        metrics.collision_candidate_scans = metrics.collision_candidate_scans.saturating_add(1);
        if target.player_id == projectile.caster || !target.alive {
            continue;
        }
        if !players_share_world_context(ctx, projectile.caster, target.player_id) {
            continue;
        }
        if !can_harm(ctx, projectile.caster, target.player_id) {
            continue;
        }

        let key = projectile_target_state_key(
            projectile.projectile_instance_id.as_str(),
            target.player_id,
        );
        let overlapping = orbit_projectile_hits_target(projectile, &target);
        let state = ctx
            .db
            .active_combat_projectile_target_state()
            .key()
            .find(key.clone());

        if !overlapping {
            if let Some(mut state) = state {
                if state.is_overlapping {
                    state.is_overlapping = false;
                    ctx.db
                        .active_combat_projectile_target_state()
                        .key()
                        .update(state);
                }
            }
            continue;
        }

        let mut state = state.unwrap_or(ActiveCombatProjectileTargetState {
            key,
            projectile_instance_id: projectile.projectile_instance_id.clone(),
            target: target.player_id,
            hit_count: 0,
            next_allowed_at: Timestamp::UNIX_EPOCH,
            is_overlapping: false,
        });

        if state.is_overlapping
            || (projectile.orbit_max_hits_per_target > 0
                && state.hit_count >= projectile.orbit_max_hits_per_target)
            || now < state.next_allowed_at
        {
            state.is_overlapping = true;
            upsert_projectile_target_state(ctx, state);
            continue;
        }

        if projectile_targeted_hit_misses(ctx, projectile, target.player_id, now) {
            emit_projectile_event_with_metadata(
                ctx,
                projectile,
                COMBAT_EVENT_MISS,
                Some(target.player_id),
                projectile.pos_x,
                projectile.pos_y,
                projectile.pos_z,
                0,
                now,
                PROJECTILE_CONTACT_METADATA_KIND,
                PROJECTILE_CONTACT_TERMINAL_KEY,
                PROJECTILE_CONTACT_NON_TERMINAL_VALUE,
                metrics,
            );
            finish_projectile_without_event(ctx, projectile);
            return true;
        }

        if resolve_projectile_defense_with_metadata(
            ctx,
            projectile,
            &target,
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
            now,
            PROJECTILE_CONTACT_METADATA_KIND,
            PROJECTILE_CONTACT_TERMINAL_KEY,
            PROJECTILE_CONTACT_NON_TERMINAL_VALUE,
            metrics,
        ) {
            metrics.contacts_resolved = metrics.contacts_resolved.saturating_add(1);
            finish_projectile_without_event(ctx, projectile);
            return true;
        }

        metrics.contacts_resolved = metrics.contacts_resolved.saturating_add(1);
        let impact_damage = projectile_damage_at_current_lifetime(projectile, definition);
        emit_projectile_event_with_metadata(
            ctx,
            projectile,
            COMBAT_EVENT_CONTACT,
            Some(target.player_id),
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
            impact_damage,
            now,
            PROJECTILE_CONTACT_METADATA_KIND,
            PROJECTILE_CONTACT_TERMINAL_KEY,
            PROJECTILE_CONTACT_NON_TERMINAL_VALUE,
            metrics,
        );
        queue_spell_projectile_hit_effects(ctx, projectile, definition, target.player_id);

        state.hit_count = state.hit_count.saturating_add(1);
        state.next_allowed_at =
            now + Duration::from_secs_f32(projectile.orbit_hit_cooldown_seconds.max(0.0));
        state.is_overlapping = true;
        upsert_projectile_target_state(ctx, state);
    }
    false
}

fn orbit_projectile_hits_target(
    projectile: &ActiveCombatProjectile,
    target: &CombatActorSnapshot,
) -> bool {
    let combined_radius = projectile.radius.max(0.0) + target.hit_radius.max(0.0);
    let dx = projectile.pos_x - target.pos_x;
    let dz = projectile.pos_z - target.pos_z;
    if dx * dx + dz * dz > combined_radius * combined_radius {
        return false;
    }

    let min_y = target.pos_y - projectile.radius.max(0.0);
    let max_y = target.pos_y + target.hit_height.max(0.0) + projectile.radius.max(0.0);
    projectile.pos_y >= min_y && projectile.pos_y <= max_y
}

fn tick_boomerang_projectile_instance(
    ctx: &ReducerContext,
    now: Timestamp,
    dt: f32,
    mut projectile: ActiveCombatProjectile,
    spell_definition: Option<&SpellRuntimeDefinition>,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    candidate_indices: &mut Vec<usize>,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    let Some(definition) = spell_definition else {
        fizzle_projectile_and_finish(ctx, now, &projectile, metrics);
        return;
    };
    let Some(caster) = players
        .iter()
        .find(|player| player.player_id == projectile.caster)
        .copied()
        .filter(|player| player.alive)
    else {
        emit_projectile_event(
            ctx,
            &projectile,
            COMBAT_EVENT_FIZZLE,
            None,
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
            0,
            now,
            metrics,
        );
        finish_projectile_without_event(ctx, &projectile);
        return;
    };

    if projectile.age >= projectile.lifetime {
        emit_projectile_event(
            ctx,
            &projectile,
            COMBAT_EVENT_FIZZLE,
            None,
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
            0,
            now,
            metrics,
        );
        finish_projectile_without_event(ctx, &projectile);
        return;
    }

    let tick_dt = (projectile.lifetime - projectile.age).max(0.0).min(dt);
    let mut remaining_dt = tick_dt;
    if !projectile.boomerang_returning {
        let outbound_speed = projectile.speed.max(0.0);
        if outbound_speed <= 0.0 {
            fizzle_projectile_and_finish(ctx, now, &projectile, metrics);
            return;
        }

        let distance_to_turn =
            (projectile.boomerang_outbound_distance - projectile.traveled).max(0.0);
        let outbound_dt = remaining_dt.min(distance_to_turn / outbound_speed);
        if outbound_dt > 0.0
            && advance_boomerang_segment(
                ctx,
                now,
                &mut projectile,
                definition,
                actor_snapshots,
                players,
                candidate_indices,
                outbound_speed * outbound_dt,
                false,
                metrics,
            )
        {
            return;
        }

        remaining_dt -= outbound_dt;
        if projectile.traveled >= projectile.boomerang_outbound_distance - 0.001 {
            projectile.boomerang_returning = true;
            remaining_dt = 0.0;
        }
    }

    if projectile.boomerang_returning && remaining_dt > 0.0 {
        if !update_boomerang_return_direction(&mut projectile, &caster) {
            emit_projectile_event(
                ctx,
                &projectile,
                COMBAT_EVENT_IMPACT,
                Some(projectile.caster),
                projectile.pos_x,
                projectile.pos_y,
                projectile.pos_z,
                0,
                now,
                metrics,
            );
            finish_projectile_without_event(ctx, &projectile);
            return;
        }

        let return_speed = projectile.boomerang_return_speed.max(0.0);
        if return_speed <= 0.0 {
            fizzle_projectile_and_finish(ctx, now, &projectile, metrics);
            return;
        }
        if advance_boomerang_segment(
            ctx,
            now,
            &mut projectile,
            definition,
            actor_snapshots,
            players,
            candidate_indices,
            return_speed * remaining_dt,
            true,
            metrics,
        ) {
            return;
        }
    }

    if projectile.age >= projectile.lifetime {
        emit_projectile_event(
            ctx,
            &projectile,
            COMBAT_EVENT_FIZZLE,
            None,
            projectile.pos_x,
            projectile.pos_y,
            projectile.pos_z,
            0,
            now,
            metrics,
        );
        finish_projectile_without_event(ctx, &projectile);
        return;
    }

    maybe_emit_projectile_update(
        ctx,
        &mut projectile,
        definition.update_interval,
        now,
        tick_dt,
        metrics,
    );
    update_projectile_row(ctx, projectile, metrics);
}

fn update_boomerang_return_direction(
    projectile: &mut ActiveCombatProjectile,
    caster: &CombatActorSnapshot,
) -> bool {
    let target_y = caster.pos_y + caster.hit_height.max(0.0) * 0.5;
    let Some((dir_x, dir_y, dir_z)) = normalize_vec3(
        caster.pos_x - projectile.pos_x,
        target_y - projectile.pos_y,
        caster.pos_z - projectile.pos_z,
    ) else {
        return false;
    };
    projectile.dir_x = dir_x;
    projectile.dir_y = dir_y;
    projectile.dir_z = dir_z;
    true
}

fn advance_boomerang_segment(
    ctx: &ReducerContext,
    now: Timestamp,
    projectile: &mut ActiveCombatProjectile,
    definition: &SpellRuntimeDefinition,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    candidate_indices: &mut Vec<usize>,
    distance: f32,
    can_hit_caster: bool,
    metrics: &mut ProjectileTickMetricsFrame,
) -> bool {
    if distance <= 0.0 {
        return false;
    }

    let start_x = projectile.pos_x;
    let start_y = projectile.pos_y;
    let start_z = projectile.pos_z;
    let end_x = start_x + projectile.dir_x * distance;
    let end_y = start_y + projectile.dir_y * distance;
    let end_z = start_z + projectile.dir_z * distance;

    let mut world_stats = WorldRaycastStats::default();
    let world_hit = first_world_hit_on_segment_with_stats(
        ctx,
        projectile.caster,
        start_x,
        start_y,
        start_z,
        end_x,
        end_y,
        end_z,
        projectile.radius,
        &mut world_stats,
    );
    record_world_raycast_stats(metrics, world_stats);
    let segment_distance = world_hit.map(|hit| hit.t).unwrap_or(distance).min(distance);
    let segment_end_x = start_x + projectile.dir_x * segment_distance;
    let segment_end_y = start_y + projectile.dir_y * segment_distance;
    let segment_end_z = start_z + projectile.dir_z * segment_distance;

    let caster_hit_t = if can_hit_caster {
        metrics.collision_candidate_scans = metrics.collision_candidate_scans.saturating_add(1);
        players
            .iter()
            .find(|player| player.player_id == projectile.caster)
            .filter(|player| player.alive)
            .and_then(|caster| {
                raycast_capsule_with_padding(
                    start_x,
                    start_y,
                    start_z,
                    projectile.dir_x,
                    projectile.dir_y,
                    projectile.dir_z,
                    segment_distance,
                    caster,
                    projectile.radius,
                )
            })
    } else {
        None
    };
    let target_limit = caster_hit_t.unwrap_or(segment_distance);

    if resolve_boomerang_enemy_contacts_on_segment(
        ctx,
        now,
        projectile,
        definition,
        actor_snapshots,
        players,
        candidate_indices,
        start_x,
        start_y,
        start_z,
        target_limit,
        metrics,
    ) {
        return true;
    }

    if let Some(t) = caster_hit_t {
        let hit_x = start_x + projectile.dir_x * t;
        let hit_y = start_y + projectile.dir_y * t;
        let hit_z = start_z + projectile.dir_z * t;
        projectile.pos_x = hit_x;
        projectile.pos_y = hit_y;
        projectile.pos_z = hit_z;
        projectile.traveled += t;
        emit_projectile_event(
            ctx,
            projectile,
            COMBAT_EVENT_IMPACT,
            Some(projectile.caster),
            hit_x,
            hit_y,
            hit_z,
            0,
            now,
            metrics,
        );
        finish_projectile_without_event(ctx, projectile);
        return true;
    }

    if let Some(hit) = world_hit {
        projectile.pos_x = hit.x;
        projectile.pos_y = hit.y;
        projectile.pos_z = hit.z;
        projectile.traveled += hit.t;
        emit_projectile_event(
            ctx,
            projectile,
            COMBAT_EVENT_IMPACT,
            None,
            hit.x,
            hit.y,
            hit.z,
            0,
            now,
            metrics,
        );
        finish_projectile_without_event(ctx, projectile);
        return true;
    }

    projectile.pos_x = segment_end_x;
    projectile.pos_y = segment_end_y;
    projectile.pos_z = segment_end_z;
    projectile.traveled += segment_distance;
    false
}

#[allow(clippy::too_many_arguments)]
fn resolve_boomerang_enemy_contacts_on_segment(
    ctx: &ReducerContext,
    now: Timestamp,
    projectile: &ActiveCombatProjectile,
    definition: &SpellRuntimeDefinition,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    candidate_indices: &mut Vec<usize>,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    max_distance: f32,
    metrics: &mut ProjectileTickMetricsFrame,
) -> bool {
    let end_x = start_x + projectile.dir_x * max_distance;
    let end_z = start_z + projectile.dir_z * max_distance;
    actor_snapshots.query_segment_indices(
        start_x,
        start_z,
        end_x,
        end_z,
        projectile.radius,
        candidate_indices,
    );

    let mut contacts = Vec::new();
    for target in candidate_indices
        .iter()
        .filter_map(|index| players.get(*index))
    {
        metrics.collision_candidate_scans = metrics.collision_candidate_scans.saturating_add(1);
        if target.player_id == projectile.caster || !target.alive {
            continue;
        }
        if !players_share_world_context(ctx, projectile.caster, target.player_id) {
            continue;
        }
        if !can_harm(ctx, projectile.caster, target.player_id) {
            continue;
        }
        if let Some(t) = raycast_capsule_with_padding(
            start_x,
            start_y,
            start_z,
            projectile.dir_x,
            projectile.dir_y,
            projectile.dir_z,
            max_distance,
            target,
            projectile.radius,
        ) {
            contacts.push((t, *target));
        }
    }
    contacts.sort_by(|(a, _), (b, _)| a.total_cmp(b));

    for (t, target) in contacts {
        let key = boomerang_projectile_target_state_key(
            projectile.projectile_instance_id.as_str(),
            target.player_id,
            projectile.boomerang_returning,
        );
        let mut state = ctx
            .db
            .active_combat_projectile_target_state()
            .key()
            .find(key.clone())
            .unwrap_or(ActiveCombatProjectileTargetState {
                key,
                projectile_instance_id: projectile.projectile_instance_id.clone(),
                target: target.player_id,
                hit_count: 0,
                next_allowed_at: Timestamp::UNIX_EPOCH,
                is_overlapping: false,
            });

        if state.hit_count >= projectile.boomerang_max_hits_per_target
            || now < state.next_allowed_at
        {
            continue;
        }

        let hit_x = start_x + projectile.dir_x * t;
        let hit_y = start_y + projectile.dir_y * t;
        let hit_z = start_z + projectile.dir_z * t;
        if projectile_targeted_hit_misses(ctx, projectile, target.player_id, now) {
            emit_projectile_event_with_metadata(
                ctx,
                projectile,
                COMBAT_EVENT_MISS,
                Some(target.player_id),
                hit_x,
                hit_y,
                hit_z,
                0,
                now,
                PROJECTILE_CONTACT_METADATA_KIND,
                PROJECTILE_CONTACT_TERMINAL_KEY,
                PROJECTILE_CONTACT_NON_TERMINAL_VALUE,
                metrics,
            );
            finish_projectile_without_event(ctx, projectile);
            return true;
        }
        if resolve_projectile_defense_with_metadata(
            ctx,
            projectile,
            &target,
            hit_x,
            hit_y,
            hit_z,
            now,
            PROJECTILE_CONTACT_METADATA_KIND,
            PROJECTILE_CONTACT_TERMINAL_KEY,
            PROJECTILE_CONTACT_NON_TERMINAL_VALUE,
            metrics,
        ) {
            metrics.contacts_resolved = metrics.contacts_resolved.saturating_add(1);
            state.hit_count = state.hit_count.saturating_add(1);
            state.next_allowed_at =
                now + Duration::from_secs_f32(projectile.boomerang_hit_cooldown_seconds.max(0.0));
            state.is_overlapping = true;
            upsert_projectile_target_state(ctx, state);
            continue;
        }

        metrics.contacts_resolved = metrics.contacts_resolved.saturating_add(1);
        let impact_damage = projectile_damage_at_current_lifetime(projectile, definition);
        emit_projectile_event_with_metadata(
            ctx,
            projectile,
            COMBAT_EVENT_CONTACT,
            Some(target.player_id),
            hit_x,
            hit_y,
            hit_z,
            impact_damage,
            now,
            PROJECTILE_CONTACT_METADATA_KIND,
            PROJECTILE_CONTACT_TERMINAL_KEY,
            PROJECTILE_CONTACT_NON_TERMINAL_VALUE,
            metrics,
        );
        queue_spell_projectile_hit_effects(ctx, projectile, definition, target.player_id);

        state.hit_count = state.hit_count.saturating_add(1);
        state.next_allowed_at =
            now + Duration::from_secs_f32(projectile.boomerang_hit_cooldown_seconds.max(0.0));
        state.is_overlapping = true;
        upsert_projectile_target_state(ctx, state);
    }
    false
}

fn projectile_target_state_key(projectile_instance_id: &str, target: Identity) -> String {
    format!("{}:{}", projectile_instance_id, target.to_hex())
}

fn boomerang_projectile_target_state_key(
    projectile_instance_id: &str,
    target: Identity,
    returning: bool,
) -> String {
    let phase = if returning { "return" } else { "outbound" };
    format!("{}:{}:{}", projectile_instance_id, target.to_hex(), phase)
}

fn upsert_projectile_target_state(ctx: &ReducerContext, state: ActiveCombatProjectileTargetState) {
    if ctx
        .db
        .active_combat_projectile_target_state()
        .key()
        .find(state.key.clone())
        .is_some()
    {
        ctx.db
            .active_combat_projectile_target_state()
            .key()
            .update(state);
    } else {
        ctx.db.active_combat_projectile_target_state().insert(state);
    }
}

fn retarget_projectile_towards_live_target(
    ctx: &ReducerContext,
    projectile: &mut ActiveCombatProjectile,
    players: &[CombatActorSnapshot],
    player_index_by_id: &HashMap<Identity, usize>,
    spawn_height: f32,
    turn_rate: f32,
    dt: f32,
) {
    if projectile.intended_target == Identity::ZERO {
        return;
    }

    if let Some(target) = player_index_by_id
        .get(&projectile.intended_target)
        .and_then(|idx| players.get(*idx))
        .filter(|player| player.alive)
    {
        if players_share_world_context(ctx, projectile.caster, target.player_id)
            && can_harm(ctx, projectile.caster, target.player_id)
        {
            let desired_x = target.pos_x - projectile.pos_x;
            let desired_y = target.pos_y + spawn_height - projectile.pos_y;
            let desired_z = target.pos_z - projectile.pos_z;
            if let Some(desired_dir) = normalize_vec3(desired_x, desired_y, desired_z) {
                let current_dir =
                    normalize_vec3(projectile.dir_x, projectile.dir_y, projectile.dir_z)
                        .unwrap_or(desired_dir);
                let (dir_x, dir_y, dir_z) =
                    rotate_towards(current_dir, desired_dir, turn_rate * dt);
                projectile.dir_x = dir_x;
                projectile.dir_y = dir_y;
                projectile.dir_z = dir_z;
            }
            return;
        }
    }

    projectile.intended_target = Identity::ZERO;
}

fn advance_projectile_with_collision(
    ctx: &ReducerContext,
    projectile: &mut ActiveCombatProjectile,
    spell_definition: Option<&SpellRuntimeDefinition>,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    candidate_indices: &mut Vec<usize>,
    dt: f32,
    metrics: &mut ProjectileTickMetricsFrame,
) -> ProjectileAdvance {
    let step = projectile.speed * dt;
    projectile.traveled += step;

    let start_x = projectile.pos_x;
    let start_y = projectile.pos_y;
    let start_z = projectile.pos_z;
    let end_x = start_x + projectile.dir_x * step;
    let mut end_y = start_y + projectile.dir_y * step;
    let end_z = start_z + projectile.dir_z * step;
    let terrain_conforming = projectile_uses_terrain_conforming_collision(spell_definition);
    if terrain_conforming {
        let spawn_height = projectile_spell_spawn_height(spell_definition);
        end_y = terrain_surface_y_for_caster(ctx, projectile.caster, end_x, end_z, end_y)
            + spawn_height;
    }

    advance_projectile_segment_with_collision(
        ctx,
        projectile,
        spell_definition,
        actor_snapshots,
        players,
        candidate_indices,
        start_x,
        start_y,
        start_z,
        end_x,
        end_y,
        end_z,
        metrics,
    )
}

fn advance_curved_target_projectile_with_collision(
    ctx: &ReducerContext,
    projectile: &mut ActiveCombatProjectile,
    spell_definition: Option<&SpellRuntimeDefinition>,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    candidate_indices: &mut Vec<usize>,
    dt: f32,
    metrics: &mut ProjectileTickMetricsFrame,
) -> ProjectileAdvance {
    let max_distance = projectile.max_distance.max(0.001);
    let step = projectile.speed.max(0.0) * dt;
    let next_traveled = (projectile.traveled + step).min(max_distance);
    let progress = (next_traveled / max_distance).clamp(0.0, 1.0);
    let start_x = projectile.pos_x;
    let start_y = projectile.pos_y;
    let start_z = projectile.pos_z;
    let (end_x, end_y, end_z) = curved_target_position(projectile, progress);
    if let Some((dir_x, dir_y, dir_z)) =
        normalize_vec3(end_x - start_x, end_y - start_y, end_z - start_z)
    {
        projectile.dir_x = dir_x;
        projectile.dir_y = dir_y;
        projectile.dir_z = dir_z;
    }
    projectile.traveled = next_traveled;

    advance_projectile_segment_with_collision(
        ctx,
        projectile,
        spell_definition,
        actor_snapshots,
        players,
        candidate_indices,
        start_x,
        start_y,
        start_z,
        end_x,
        end_y,
        end_z,
        metrics,
    )
}

#[allow(clippy::too_many_arguments)]
fn advance_projectile_segment_with_collision(
    ctx: &ReducerContext,
    projectile: &mut ActiveCombatProjectile,
    spell_definition: Option<&SpellRuntimeDefinition>,
    actor_snapshots: &CombatActorSnapshotSet,
    players: &[CombatActorSnapshot],
    candidate_indices: &mut Vec<usize>,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    metrics: &mut ProjectileTickMetricsFrame,
) -> ProjectileAdvance {
    let terrain_conforming = projectile_uses_terrain_conforming_collision(spell_definition);
    actor_snapshots.query_segment_indices(
        start_x,
        start_z,
        end_x,
        end_z,
        projectile.radius,
        candidate_indices,
    );

    metrics.collision_candidate_scans = metrics
        .collision_candidate_scans
        .saturating_add(candidate_indices.len().min(u32::MAX as usize) as u32);

    let hit = if terrain_conforming {
        first_player_hit_on_segment_candidates(
            ctx,
            projectile.caster,
            start_x,
            start_y,
            start_z,
            end_x,
            end_y,
            end_z,
            projectile.radius,
            players,
            candidate_indices,
        )
    } else {
        let mut world_stats = WorldRaycastStats::default();
        let hit = first_hit_on_segment_candidates_with_world_stats(
            ctx,
            projectile.caster,
            start_x,
            start_y,
            start_z,
            end_x,
            end_y,
            end_z,
            projectile.radius,
            players,
            candidate_indices,
            &mut world_stats,
        );
        record_world_raycast_stats(metrics, world_stats);
        hit
    };

    if let Some(hit) = hit {
        let impact_target = match hit.kind {
            SceneHitKind::Player(target_id) => Some(target_id),
            SceneHitKind::World => None,
        };
        return ProjectileAdvance {
            impacted: true,
            end_x: hit.x,
            end_y: hit.y,
            end_z: hit.z,
            impact_target,
        };
    }

    projectile.pos_x = end_x;
    projectile.pos_y = end_y;
    projectile.pos_z = end_z;
    ProjectileAdvance {
        impacted: false,
        end_x,
        end_y,
        end_z,
        impact_target: None,
    }
}

fn curved_target_position(projectile: &ActiveCombatProjectile, progress: f32) -> (f32, f32, f32) {
    let t = progress.clamp(0.0, 1.0);
    let one_minus = 1.0 - t;
    (
        one_minus * one_minus * projectile.origin_x
            + 2.0 * one_minus * t * projectile.curve_control_x
            + t * t * projectile.curve_end_x,
        one_minus * one_minus * projectile.origin_y
            + 2.0 * one_minus * t * projectile.curve_control_y
            + t * t * projectile.curve_end_y,
        one_minus * one_minus * projectile.origin_z
            + 2.0 * one_minus * t * projectile.curve_control_z
            + t * t * projectile.curve_end_z,
    )
}

fn projectile_uses_terrain_conforming_collision(
    spell_definition: Option<&SpellRuntimeDefinition>,
) -> bool {
    spell_definition
        .and_then(|definition| definition.secondary.projectile.as_ref())
        .is_some_and(|projectile| projectile.terrain_conforming)
}

fn projectile_spell_spawn_height(spell_definition: Option<&SpellRuntimeDefinition>) -> f32 {
    spell_definition
        .map(|definition| definition.spawn_height)
        .unwrap_or(0.0)
}

fn resolve_projectile_defense(
    ctx: &ReducerContext,
    projectile: &ActiveCombatProjectile,
    target: &CombatActorSnapshot,
    point_x: f32,
    point_y: f32,
    point_z: f32,
    now: Timestamp,
    metrics: &mut ProjectileTickMetricsFrame,
) -> bool {
    resolve_projectile_defense_with_metadata(
        ctx,
        projectile,
        target,
        point_x,
        point_y,
        point_z,
        now,
        COMBAT_METADATA_NONE,
        "",
        "",
        metrics,
    )
}

#[allow(clippy::too_many_arguments)]
fn resolve_projectile_defense_with_metadata(
    ctx: &ReducerContext,
    projectile: &ActiveCombatProjectile,
    target: &CombatActorSnapshot,
    point_x: f32,
    point_y: f32,
    point_z: f32,
    now: Timestamp,
    metadata_kind: &str,
    metadata_key: &str,
    metadata_value: &str,
    metrics: &mut ProjectileTickMetricsFrame,
) -> bool {
    match resolve_defensible_combat_hit(
        ctx,
        DefensibleCombatHit {
            delivery_kind: CombatHitDeliveryKind::Projectile,
            defender: target.player_id,
            active_from: now,
            active_until: now + Duration::from_millis(1),
            parry_behavior: projectile.parry_behavior.as_str(),
            block_behavior: projectile.block_behavior.as_str(),
            source_x: projectile.pos_x,
            source_y: projectile.pos_y,
            source_z: projectile.pos_z,
            impact_x: point_x,
            impact_y: point_y,
            impact_z: point_z,
            dir_x: projectile.dir_x,
            dir_y: projectile.dir_y,
            dir_z: projectile.dir_z,
            speed: projectile.speed,
        },
    ) {
        DefenseResolution::Blocked => {
            emit_projectile_event_with_metadata(
                ctx,
                projectile,
                COMBAT_EVENT_BLOCK,
                Some(target.player_id),
                point_x,
                point_y,
                point_z,
                0,
                now,
                metadata_kind,
                metadata_key,
                metadata_value,
                metrics,
            );
            true
        }
        DefenseResolution::Parried => {
            emit_projectile_event_with_metadata(
                ctx,
                projectile,
                COMBAT_EVENT_PARRY,
                Some(target.player_id),
                point_x,
                point_y,
                point_z,
                0,
                now,
                metadata_kind,
                metadata_key,
                metadata_value,
                metrics,
            );
            true
        }
        DefenseResolution::None => false,
    }
}

fn projectile_targeted_hit_misses(
    ctx: &ReducerContext,
    projectile: &ActiveCombatProjectile,
    target: Identity,
    now: Timestamp,
) -> bool {
    projectile.intended_target == target
        && hostile_targeted_ability_misses(ctx, projectile.caster, target, now)
}

fn queue_spell_projectile_hit_effects(
    ctx: &ReducerContext,
    projectile: &ActiveCombatProjectile,
    definition: &SpellRuntimeDefinition,
    target: Identity,
) {
    let impact_damage = projectile_damage_at_current_lifetime(projectile, definition);
    let mut effects = vec![EffectPacket::Damage {
        amount: impact_damage,
        damage_type: crate::combat::DamageType::from_wire(projectile.damage_type.as_str()),
        source: projectile.caster,
        target,
        spell_id: projectile.projectile_instance_id.clone(),
        delivery: DamageDelivery::Direct,
        source_kind: DAMAGE_SOURCE_KIND_PROJECTILE.to_string(),
        direct_action_key: projectile.projectile_instance_id.clone(),
    }];
    if let Some(projectile_tunables) = definition.secondary.projectile.as_ref() {
        push_impact_effect_packets(
            &mut effects,
            projectile_tunables.impact_effects.as_slice(),
            projectile.caster,
            target,
            projectile.projectile_instance_id.as_str(),
            definition.kind.as_str(),
            impact_damage > 0,
        );
    }
    queue_effects(ctx, effects);
}

fn projectile_damage_at_current_lifetime(
    projectile: &ActiveCombatProjectile,
    definition: &SpellRuntimeDefinition,
) -> i32 {
    if projectile.damage <= 0 {
        return projectile.damage;
    }

    let end_multiplier = definition
        .secondary
        .projectile
        .as_ref()
        .or(definition.secondary.channel_projectile.as_ref())
        .map(|tunables| tunables.damage_multiplier_at_lifetime_end)
        .unwrap_or(1.0)
        .clamp(0.0, 1.0);
    let lifetime_progress = if projectile.lifetime > 0.0 {
        (projectile.age / projectile.lifetime).clamp(0.0, 1.0)
    } else {
        0.0
    };
    let multiplier = 1.0 + (end_multiplier - 1.0) * lifetime_progress;
    ((projectile.damage as f32) * multiplier).round().max(1.0) as i32
}

fn queue_weapon_projectile_hit_effects(
    ctx: &ReducerContext,
    projectile: &ActiveCombatProjectile,
    target: Identity,
    now: Timestamp,
) {
    if projectile.damage > 0 {
        queue_effects(
            ctx,
            vec![EffectPacket::Damage {
                amount: projectile.damage,
                damage_type: crate::combat::DamageType::from_wire(projectile.damage_type.as_str()),
                source: projectile.caster,
                target,
                spell_id: projectile.projectile_instance_id.clone(),
                delivery: DamageDelivery::Direct,
                source_kind: DAMAGE_SOURCE_KIND_PROJECTILE.to_string(),
                direct_action_key: projectile.projectile_instance_id.clone(),
            }],
        );
    }

    if projectile.grants_primary_resource_on_hit {
        grant_primary_resource_for_melee_hit(ctx, projectile.caster, now);
    }
}

fn maybe_emit_projectile_update(
    ctx: &ReducerContext,
    projectile: &mut ActiveCombatProjectile,
    update_interval: f32,
    now: Timestamp,
    dt: f32,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    if update_interval <= 0.0 {
        return;
    }

    projectile.update_accum += dt;
    if projectile.update_accum < update_interval {
        return;
    }

    projectile.update_accum -= update_interval;
    emit_projectile_event(
        ctx,
        projectile,
        COMBAT_EVENT_UPDATE,
        None,
        projectile.pos_x,
        projectile.pos_y,
        projectile.pos_z,
        0,
        now,
        metrics,
    );
}

#[allow(clippy::too_many_arguments)]
fn emit_projectile_event(
    ctx: &ReducerContext,
    projectile: &ActiveCombatProjectile,
    event_type: &str,
    hit: Option<Identity>,
    point_x: f32,
    point_y: f32,
    point_z: f32,
    damage: i32,
    now: Timestamp,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    emit_projectile_event_with_metadata(
        ctx,
        projectile,
        event_type,
        hit,
        point_x,
        point_y,
        point_z,
        damage,
        now,
        COMBAT_METADATA_NONE,
        "",
        "",
        metrics,
    );
}

#[allow(clippy::too_many_arguments)]
fn emit_projectile_event_with_metadata(
    ctx: &ReducerContext,
    projectile: &ActiveCombatProjectile,
    event_type: &str,
    hit: Option<Identity>,
    point_x: f32,
    point_y: f32,
    point_z: f32,
    damage: i32,
    now: Timestamp,
    metadata_kind: &str,
    metadata_key: &str,
    metadata_value: &str,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    let hit = hit.unwrap_or(Identity::ZERO);
    let projectile_trail_vfx_id = if projectile.source_kind == "SPELL" {
        crate::progression::projectile_trail_vfx_id_for_spell(
            projectile.ability_id.as_str(),
            projectile.action_kind.as_str(),
            projectile.projectile_sequence_index,
        )
        .or_else(|| {
            crate::progression::projectile_trail_vfx_id_for_spell(
                projectile.ability_id.as_str(),
                projectile.action_kind.as_str(),
                0,
            )
        })
    } else {
        None
    };
    let terminal = is_projectile_presentation_terminal(
        event_type,
        metadata_kind,
        metadata_key,
        metadata_value,
    );

    ctx.db
        .projectile_presentation_event()
        .insert(ProjectilePresentationEvent {
            event_id: 0,
            action_instance_id: projectile.action_instance_id.clone(),
            action_kind: projectile.action_kind.clone(),
            ability_id: projectile.ability_id.clone(),
            source_kind: projectile.source_kind.clone(),
            projectile_id: projectile.projectile_id.clone(),
            projectile_trail_vfx_id,
            projectile_instance_id: projectile.projectile_instance_id.clone(),
            hit_index: projectile.hit_index as i32,
            event_type: event_type.to_string(),
            caster: projectile.caster,
            hit,
            intended_target: projectile.intended_target,
            origin_x: projectile.origin_x,
            origin_y: projectile.origin_y,
            origin_z: projectile.origin_z,
            dir_x: projectile.dir_x,
            dir_y: projectile.dir_y,
            dir_z: projectile.dir_z,
            point_x,
            point_y,
            point_z,
            speed: projectile.speed,
            max_distance: projectile.max_distance,
            radius: projectile.radius,
            motion_kind: projectile.motion_kind.clone(),
            update_interval_seconds: projectile.update_interval_seconds,
            orbit_initial_yaw: projectile.orbit_initial_yaw,
            orbit_radius: projectile.orbit_radius,
            orbit_height: projectile.orbit_height,
            orbit_angular_speed_deg_per_sec: projectile.orbit_angular_speed_deg_per_sec,
            orbit_phase_offset_deg: projectile.orbit_phase_offset_deg,
            boomerang_returning: projectile.boomerang_returning,
            boomerang_outbound_distance: projectile.boomerang_outbound_distance,
            boomerang_return_speed: projectile.boomerang_return_speed,
            curve_control_x: projectile.curve_control_x,
            curve_control_y: projectile.curve_control_y,
            curve_control_z: projectile.curve_control_z,
            curve_end_x: projectile.curve_end_x,
            curve_end_y: projectile.curve_end_y,
            curve_end_z: projectile.curve_end_z,
            curve_progress: projectile_curve_progress(projectile),
            sequence_index: projectile.projectile_sequence_index,
            sequence_count: 1,
            terminal,
            created_at: now,
            created_at_micros: timestamp_to_micros(now),
        });

    if should_emit_projectile_combat_event(event_type, terminal) {
        ctx.db.combat_event().insert(CombatEvent {
            event_id: 0,
            action_instance_id: projectile.action_instance_id.clone(),
            action_kind: projectile.action_kind.clone(),
            ability_id: projectile.ability_id.clone(),
            hit_index: projectile.hit_index as i32,
            event_type: event_type.to_string(),
            source_kind: projectile.source_kind.clone(),
            caster: projectile.caster,
            hit,
            origin_x: projectile.origin_x,
            origin_y: projectile.origin_y,
            origin_z: projectile.origin_z,
            dir_x: projectile.dir_x,
            dir_y: projectile.dir_y,
            dir_z: projectile.dir_z,
            speed: projectile.speed,
            max_distance: projectile.max_distance,
            scalar_kind: COMBAT_SCALAR_NONE.to_string(),
            scalar_value: 0.0,
            sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
            sequence_index: 0,
            sequence_count: 1,
            point_x,
            point_y,
            point_z,
            created_at: now,
            created_at_micros: timestamp_to_micros(now),
            damage,
            metadata_kind: metadata_kind.to_string(),
            metadata_key: metadata_key.to_string(),
            metadata_value: metadata_value.to_string(),
        });
    }
    record_emitted_event(event_type, metrics);
}

fn is_projectile_presentation_terminal(
    event_type: &str,
    metadata_kind: &str,
    metadata_key: &str,
    metadata_value: &str,
) -> bool {
    if event_type == COMBAT_EVENT_CONTACT
        && metadata_kind == PROJECTILE_CONTACT_METADATA_KIND
        && metadata_key == PROJECTILE_CONTACT_TERMINAL_KEY
        && metadata_value == PROJECTILE_CONTACT_NON_TERMINAL_VALUE
    {
        return false;
    }

    matches!(
        event_type,
        COMBAT_EVENT_IMPACT | COMBAT_EVENT_BLOCK | COMBAT_EVENT_PARRY | COMBAT_EVENT_FIZZLE
    )
}

fn should_emit_projectile_combat_event(event_type: &str, terminal: bool) -> bool {
    terminal || event_type == COMBAT_EVENT_IMPACT || event_type == COMBAT_EVENT_FIZZLE
}

fn projectile_curve_progress(projectile: &ActiveCombatProjectile) -> f32 {
    if projectile.motion_kind != PROJECTILE_MOTION_CURVED_TARGET {
        return 0.0;
    }
    (projectile.traveled / projectile.max_distance.max(0.001)).clamp(0.0, 1.0)
}

#[allow(clippy::too_many_arguments)]
fn emit_projectile_event_untracked(
    ctx: &ReducerContext,
    projectile: &ActiveCombatProjectile,
    event_type: &str,
    hit: Option<Identity>,
    point_x: f32,
    point_y: f32,
    point_z: f32,
    damage: i32,
    now: Timestamp,
) {
    let mut ignored_metrics = ProjectileTickMetricsFrame::default();
    emit_projectile_event(
        ctx,
        projectile,
        event_type,
        hit,
        point_x,
        point_y,
        point_z,
        damage,
        now,
        &mut ignored_metrics,
    );
}

pub(crate) fn finish_combat_projectile_with_event(
    ctx: &ReducerContext,
    now: Timestamp,
    projectile: &ActiveCombatProjectile,
    event_type: &str,
    point_x: f32,
    point_y: f32,
    point_z: f32,
) {
    if ctx
        .db
        .active_combat_projectile()
        .projectile_instance_id()
        .find(projectile.projectile_instance_id.clone())
        .is_none()
    {
        return;
    }

    emit_projectile_event_untracked(
        ctx, projectile, event_type, None, point_x, point_y, point_z, 0, now,
    );
    finish_projectile_without_event(ctx, projectile);
}

fn fizzle_projectile_and_finish(
    ctx: &ReducerContext,
    now: Timestamp,
    projectile: &ActiveCombatProjectile,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    emit_projectile_event(
        ctx,
        projectile,
        COMBAT_EVENT_FIZZLE,
        None,
        projectile.pos_x,
        projectile.pos_y,
        projectile.pos_z,
        0,
        now,
        metrics,
    );
    finish_projectile_without_event(ctx, projectile);
}

fn finish_projectile_without_event(ctx: &ReducerContext, projectile: &ActiveCombatProjectile) {
    ctx.db
        .active_combat_projectile_target_state()
        .projectile_instance_id()
        .delete(&projectile.projectile_instance_id);
    ctx.db
        .active_combat_projectile()
        .projectile_instance_id()
        .delete(projectile.projectile_instance_id.clone());
}

fn update_projectile_row(
    ctx: &ReducerContext,
    projectile: ActiveCombatProjectile,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    metrics.rows_updated = metrics.rows_updated.saturating_add(1);
    ctx.db
        .active_combat_projectile()
        .projectile_instance_id()
        .update(projectile);
}

fn record_motion_kind(
    projectile: &ActiveCombatProjectile,
    spell_definition: Option<&SpellRuntimeDefinition>,
    metrics: &mut ProjectileTickMetricsFrame,
) {
    if projectile.motion_kind == PROJECTILE_MOTION_ORBIT_CASTER {
        metrics.orbit_projectile_count = metrics.orbit_projectile_count.saturating_add(1);
        return;
    }
    if projectile.motion_kind == PROJECTILE_MOTION_BOOMERANG_CASTER {
        metrics.boomerang_projectile_count = metrics.boomerang_projectile_count.saturating_add(1);
        return;
    }
    if projectile.motion_kind == PROJECTILE_MOTION_CURVED_TARGET {
        metrics.curved_projectile_count = metrics.curved_projectile_count.saturating_add(1);
        return;
    }
    if spell_definition
        .map(|definition| projectile_homing_window_seconds(definition) > 0.0)
        .unwrap_or(false)
    {
        metrics.homing_projectile_count = metrics.homing_projectile_count.saturating_add(1);
    } else {
        metrics.linear_projectile_count = metrics.linear_projectile_count.saturating_add(1);
    }
}

fn record_world_raycast_stats(
    metrics: &mut ProjectileTickMetricsFrame,
    world_stats: WorldRaycastStats,
) {
    metrics.world_collision_queries = metrics
        .world_collision_queries
        .saturating_add(world_stats.raycast_queries);
    metrics.world_gameplay_broadphase_candidates = metrics
        .world_gameplay_broadphase_candidates
        .saturating_add(world_stats.world_gameplay_broadphase_candidates);
    metrics.world_gameplay_narrowphase_tests = metrics
        .world_gameplay_narrowphase_tests
        .saturating_add(world_stats.world_gameplay_narrowphase_tests);
    metrics.world_gameplay_full_scan_fallbacks = metrics
        .world_gameplay_full_scan_fallbacks
        .saturating_add(world_stats.world_gameplay_full_scan_fallbacks);
    metrics.world_query_mesh_broadphase_candidates = metrics
        .world_query_mesh_broadphase_candidates
        .saturating_add(world_stats.world_query_mesh_broadphase_candidates);
    metrics.world_query_mesh_bvh_node_tests = metrics
        .world_query_mesh_bvh_node_tests
        .saturating_add(world_stats.world_query_mesh_bvh_node_tests);
    metrics.world_query_mesh_triangles_tested = metrics
        .world_query_mesh_triangles_tested
        .saturating_add(world_stats.world_query_mesh_triangles_tested);
    metrics.world_query_mesh_full_scan_fallbacks = metrics
        .world_query_mesh_full_scan_fallbacks
        .saturating_add(world_stats.world_query_mesh_full_scan_fallbacks);
    metrics.open_world_geometry_point_checks = metrics
        .open_world_geometry_point_checks
        .saturating_add(world_stats.open_world_geometry_point_checks);
}

fn record_emitted_event(event_type: &str, metrics: &mut ProjectileTickMetricsFrame) {
    match event_type {
        COMBAT_EVENT_UPDATE => {
            metrics.update_events_emitted = metrics.update_events_emitted.saturating_add(1);
        }
        COMBAT_EVENT_CONTACT => {
            metrics.contact_events_emitted = metrics.contact_events_emitted.saturating_add(1);
        }
        COMBAT_EVENT_BLOCK | COMBAT_EVENT_PARRY => {
            metrics.block_parry_events_emitted =
                metrics.block_parry_events_emitted.saturating_add(1);
            metrics.terminal_events_emitted = metrics.terminal_events_emitted.saturating_add(1);
        }
        COMBAT_EVENT_IMPACT | COMBAT_EVENT_FIZZLE => {
            metrics.terminal_events_emitted = metrics.terminal_events_emitted.saturating_add(1);
        }
        _ => {}
    }
}

fn maybe_record_empty_projectile_tick_metrics(ctx: &ReducerContext, now: Timestamp) {
    record_empty_combat_projectile_tick_metrics(ctx, now);
}

pub(crate) fn record_empty_combat_projectile_tick_metrics(ctx: &ReducerContext, now: Timestamp) {
    if ctx
        .db
        .combat_projectile_tick_metrics()
        .key()
        .find(METRICS_ROW_KEY.to_string())
        .is_none()
    {
        return;
    }

    record_projectile_tick_metrics(
        ctx,
        now,
        ProjectileTickMetricsFrame {
            ..Default::default()
        },
    );
}

fn record_projectile_tick_metrics(
    ctx: &ReducerContext,
    now: Timestamp,
    metrics: ProjectileTickMetricsFrame,
) {
    let key = METRICS_ROW_KEY.to_string();
    let previous = ctx
        .db
        .combat_projectile_tick_metrics()
        .key()
        .find(key.clone());
    let previous_worst = previous
        .as_ref()
        .map(|row| row.worst_tick_micros)
        .unwrap_or(0);
    let worst_tick_micros = if metrics.active_projectile_count == 0 {
        0
    } else {
        previous_worst.max(metrics.tick_micros)
    };
    let sample_sequence = previous
        .as_ref()
        .map(|row| row.sample_sequence.saturating_add(1))
        .unwrap_or(1);

    let row = CombatProjectileTickMetrics {
        key: key.clone(),
        sampled_at: now,
        sample_sequence,
        active_projectile_count: metrics.active_projectile_count,
        rows_updated: metrics.rows_updated,
        collision_candidate_scans: metrics.collision_candidate_scans,
        world_collision_queries: metrics.world_collision_queries,
        world_gameplay_broadphase_candidates: metrics.world_gameplay_broadphase_candidates,
        world_gameplay_narrowphase_tests: metrics.world_gameplay_narrowphase_tests,
        world_gameplay_full_scan_fallbacks: metrics.world_gameplay_full_scan_fallbacks,
        world_query_mesh_broadphase_candidates: metrics.world_query_mesh_broadphase_candidates,
        world_query_mesh_bvh_node_tests: metrics.world_query_mesh_bvh_node_tests,
        world_query_mesh_triangles_tested: metrics.world_query_mesh_triangles_tested,
        world_query_mesh_full_scan_fallbacks: metrics.world_query_mesh_full_scan_fallbacks,
        open_world_geometry_point_checks: metrics.open_world_geometry_point_checks,
        contacts_resolved: metrics.contacts_resolved,
        update_events_emitted: metrics.update_events_emitted,
        contact_events_emitted: metrics.contact_events_emitted,
        terminal_events_emitted: metrics.terminal_events_emitted,
        block_parry_events_emitted: metrics.block_parry_events_emitted,
        linear_projectile_count: metrics.linear_projectile_count,
        homing_projectile_count: metrics.homing_projectile_count,
        orbit_projectile_count: metrics.orbit_projectile_count,
        boomerang_projectile_count: metrics.boomerang_projectile_count,
        curved_projectile_count: metrics.curved_projectile_count,
        tick_micros: metrics.tick_micros,
        worst_tick_micros,
        peak_active_projectile_count: previous
            .as_ref()
            .map(|row| row.peak_active_projectile_count)
            .unwrap_or(0)
            .max(metrics.active_projectile_count),
        peak_rows_updated: previous
            .as_ref()
            .map(|row| row.peak_rows_updated)
            .unwrap_or(0)
            .max(metrics.rows_updated),
        peak_collision_candidate_scans: previous
            .as_ref()
            .map(|row| row.peak_collision_candidate_scans)
            .unwrap_or(0)
            .max(metrics.collision_candidate_scans),
        peak_world_collision_queries: previous
            .as_ref()
            .map(|row| row.peak_world_collision_queries)
            .unwrap_or(0)
            .max(metrics.world_collision_queries),
        peak_world_gameplay_broadphase_candidates: previous
            .as_ref()
            .map(|row| row.peak_world_gameplay_broadphase_candidates)
            .unwrap_or(0)
            .max(metrics.world_gameplay_broadphase_candidates),
        peak_world_gameplay_narrowphase_tests: previous
            .as_ref()
            .map(|row| row.peak_world_gameplay_narrowphase_tests)
            .unwrap_or(0)
            .max(metrics.world_gameplay_narrowphase_tests),
        peak_world_gameplay_full_scan_fallbacks: previous
            .as_ref()
            .map(|row| row.peak_world_gameplay_full_scan_fallbacks)
            .unwrap_or(0)
            .max(metrics.world_gameplay_full_scan_fallbacks),
        peak_world_query_mesh_broadphase_candidates: previous
            .as_ref()
            .map(|row| row.peak_world_query_mesh_broadphase_candidates)
            .unwrap_or(0)
            .max(metrics.world_query_mesh_broadphase_candidates),
        peak_world_query_mesh_bvh_node_tests: previous
            .as_ref()
            .map(|row| row.peak_world_query_mesh_bvh_node_tests)
            .unwrap_or(0)
            .max(metrics.world_query_mesh_bvh_node_tests),
        peak_world_query_mesh_triangles_tested: previous
            .as_ref()
            .map(|row| row.peak_world_query_mesh_triangles_tested)
            .unwrap_or(0)
            .max(metrics.world_query_mesh_triangles_tested),
        peak_world_query_mesh_full_scan_fallbacks: previous
            .as_ref()
            .map(|row| row.peak_world_query_mesh_full_scan_fallbacks)
            .unwrap_or(0)
            .max(metrics.world_query_mesh_full_scan_fallbacks),
        peak_open_world_geometry_point_checks: previous
            .as_ref()
            .map(|row| row.peak_open_world_geometry_point_checks)
            .unwrap_or(0)
            .max(metrics.open_world_geometry_point_checks),
        peak_contacts_resolved: previous
            .as_ref()
            .map(|row| row.peak_contacts_resolved)
            .unwrap_or(0)
            .max(metrics.contacts_resolved),
        total_rows_updated: previous
            .as_ref()
            .map(|row| row.total_rows_updated)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.rows_updated)),
        total_collision_candidate_scans: previous
            .as_ref()
            .map(|row| row.total_collision_candidate_scans)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.collision_candidate_scans)),
        total_world_collision_queries: previous
            .as_ref()
            .map(|row| row.total_world_collision_queries)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.world_collision_queries)),
        total_world_gameplay_broadphase_candidates: previous
            .as_ref()
            .map(|row| row.total_world_gameplay_broadphase_candidates)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.world_gameplay_broadphase_candidates)),
        total_world_gameplay_narrowphase_tests: previous
            .as_ref()
            .map(|row| row.total_world_gameplay_narrowphase_tests)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.world_gameplay_narrowphase_tests)),
        total_world_gameplay_full_scan_fallbacks: previous
            .as_ref()
            .map(|row| row.total_world_gameplay_full_scan_fallbacks)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.world_gameplay_full_scan_fallbacks)),
        total_world_query_mesh_broadphase_candidates: previous
            .as_ref()
            .map(|row| row.total_world_query_mesh_broadphase_candidates)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.world_query_mesh_broadphase_candidates)),
        total_world_query_mesh_bvh_node_tests: previous
            .as_ref()
            .map(|row| row.total_world_query_mesh_bvh_node_tests)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.world_query_mesh_bvh_node_tests)),
        total_world_query_mesh_triangles_tested: previous
            .as_ref()
            .map(|row| row.total_world_query_mesh_triangles_tested)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.world_query_mesh_triangles_tested)),
        total_world_query_mesh_full_scan_fallbacks: previous
            .as_ref()
            .map(|row| row.total_world_query_mesh_full_scan_fallbacks)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.world_query_mesh_full_scan_fallbacks)),
        total_open_world_geometry_point_checks: previous
            .as_ref()
            .map(|row| row.total_open_world_geometry_point_checks)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.open_world_geometry_point_checks)),
        total_contacts_resolved: previous
            .as_ref()
            .map(|row| row.total_contacts_resolved)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.contacts_resolved)),
        total_update_events_emitted: previous
            .as_ref()
            .map(|row| row.total_update_events_emitted)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.update_events_emitted)),
        total_contact_events_emitted: previous
            .as_ref()
            .map(|row| row.total_contact_events_emitted)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.contact_events_emitted)),
        total_terminal_events_emitted: previous
            .as_ref()
            .map(|row| row.total_terminal_events_emitted)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.terminal_events_emitted)),
        total_block_parry_events_emitted: previous
            .as_ref()
            .map(|row| row.total_block_parry_events_emitted)
            .unwrap_or(0)
            .saturating_add(u64::from(metrics.block_parry_events_emitted)),
    };

    warn_on_high_world_collision_fallback_ratio(&row);
    upsert_projectile_tick_metrics_row(ctx, row);
}

fn warn_on_high_world_collision_fallback_ratio(row: &CombatProjectileTickMetrics) {
    if row.world_collision_queries < WORLD_COLLISION_FALLBACK_WARN_MIN_QUERIES {
        return;
    }

    let fallback_per_mille = row.world_gameplay_full_scan_fallbacks.saturating_mul(1000)
        / row.world_collision_queries.max(1);
    if fallback_per_mille >= WORLD_COLLISION_FALLBACK_WARN_RATIO_PER_MILLE {
        log::warn!(
            "[PROJECTILES] High world gameplay broadphase fallback ratio: fallbacks={} queries={} ratio={:.1}% narrowphase_tests={} candidates={}",
            row.world_gameplay_full_scan_fallbacks,
            row.world_collision_queries,
            fallback_per_mille as f32 / 10.0,
            row.world_gameplay_narrowphase_tests,
            row.world_gameplay_broadphase_candidates
        );
    }

    let mesh_fallback_per_mille = row
        .world_query_mesh_full_scan_fallbacks
        .saturating_mul(1000)
        / row.world_collision_queries.max(1);
    if mesh_fallback_per_mille >= WORLD_COLLISION_FALLBACK_WARN_RATIO_PER_MILLE {
        log::warn!(
            "[PROJECTILES] High world query mesh broadphase fallback ratio: fallbacks={} queries={} ratio={:.1}% bvh_node_tests={} triangle_tests={} candidates={}",
            row.world_query_mesh_full_scan_fallbacks,
            row.world_collision_queries,
            mesh_fallback_per_mille as f32 / 10.0,
            row.world_query_mesh_bvh_node_tests,
            row.world_query_mesh_triangles_tested,
            row.world_query_mesh_broadphase_candidates
        );
    }
}

fn upsert_projectile_tick_metrics_row(ctx: &ReducerContext, row: CombatProjectileTickMetrics) {
    if ctx
        .db
        .combat_projectile_tick_metrics()
        .key()
        .find(row.key.clone())
        .is_some()
    {
        ctx.db.combat_projectile_tick_metrics().key().update(row);
    } else {
        ctx.db.combat_projectile_tick_metrics().insert(row);
    }
}

fn normalize_vec3(x: f32, y: f32, z: f32) -> Option<(f32, f32, f32)> {
    let len_sq = x * x + y * y + z * z;
    if len_sq <= 0.000001 {
        return None;
    }
    let inv_len = 1.0 / len_sq.sqrt();
    Some((x * inv_len, y * inv_len, z * inv_len))
}

fn rotate_towards(
    current: (f32, f32, f32),
    desired: (f32, f32, f32),
    max_angle: f32,
) -> (f32, f32, f32) {
    let (cx, cy, cz) = current;
    let (dx, dy, dz) = desired;
    let dot = (cx * dx + cy * dy + cz * dz).clamp(-1.0, 1.0);
    let angle = dot.acos();
    if angle <= max_angle || angle <= 0.0001 {
        return desired;
    }
    let sin_total = angle.sin();
    if sin_total.abs() <= 0.0001 {
        return desired;
    }
    let t = max_angle / angle;
    let scale_current = ((1.0 - t) * angle).sin() / sin_total;
    let scale_desired = (t * angle).sin() / sin_total;
    let x = cx * scale_current + dx * scale_desired;
    let y = cy * scale_current + dy * scale_desired;
    let z = cz * scale_current + dz * scale_desired;
    normalize_vec3(x, y, z).unwrap_or(desired)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_identity(byte: u8) -> Identity {
        Identity::from_hex(format!("{byte:064x}").as_str()).expect("test identity should parse")
    }

    fn test_snapshot(player_id: Identity) -> CombatActorSnapshot {
        CombatActorSnapshot {
            player_id,
            alive: true,
            pos_x: 0.0,
            pos_y: 0.0,
            pos_z: 0.0,
            facing_yaw: 0.0,
            grounded: true,
            hit_radius: 0.5,
            hit_height: 2.0,
            last_processed_tick: 0,
        }
    }

    fn test_projectile(caster: Identity) -> ActiveCombatProjectile {
        ActiveCombatProjectile {
            projectile_instance_id: "test:p0".to_string(),
            action_instance_id: "test".to_string(),
            projectile_sequence_index: 0,
            projectile_id: "VFX_TEST_PROJECTILE".to_string(),
            source_kind: "SPELL".to_string(),
            action_kind: "ORBITING_BLADES".to_string(),
            ability_id: "SPELL_ORBITING_BLADES".to_string(),
            motion_kind: PROJECTILE_MOTION_ORBIT_CASTER.to_string(),
            caster,
            intended_target: Identity::ZERO,
            origin_x: 0.0,
            origin_y: 0.0,
            origin_z: 0.0,
            pos_x: 0.0,
            pos_y: 0.0,
            pos_z: 0.0,
            dir_x: 0.0,
            dir_y: 0.0,
            dir_z: 1.0,
            speed: 0.0,
            max_distance: 0.0,
            radius: 0.45,
            orbit_initial_yaw: 0.0,
            orbit_radius: 1.5,
            orbit_height: 1.0,
            orbit_angular_speed_deg_per_sec: 90.0,
            orbit_phase_offset_deg: 0.0,
            orbit_hit_cooldown_seconds: 0.35,
            orbit_max_hits_per_target: 1,
            boomerang_returning: false,
            boomerang_outbound_distance: 0.0,
            boomerang_return_speed: 0.0,
            boomerang_hit_cooldown_seconds: 0.0,
            boomerang_max_hits_per_target: 0,
            curve_control_x: 0.0,
            curve_control_y: 0.0,
            curve_control_z: 0.0,
            curve_end_x: 0.0,
            curve_end_y: 0.0,
            curve_end_z: 0.0,
            traveled: 0.0,
            age: 0.0,
            lifetime: 1.0,
            update_accum: 0.0,
            update_interval_seconds: 0.05,
            damage: 18,
            damage_type: crate::combat::DamageType::Physical.as_str().to_string(),
            parry_behavior: "PARRYABLE".to_string(),
            block_behavior: "BLOCKABLE".to_string(),
            grants_primary_resource_on_hit: false,
            hit_index: 0,
            created_at: Timestamp::UNIX_EPOCH,
        }
    }

    #[test]
    fn projectile_backed_channel_spells_drive_active_projectiles() {
        let frozen_splinters =
            spell_definition_by_str("FROZEN_SPLINTERS").expect("Frozen Splinters should exist");
        assert_eq!(frozen_splinters.behavior, SpellBehavior::Channel);
        assert!(spell_definition_drives_active_projectile(frozen_splinters));

        let magic_missile =
            spell_definition_by_str("MAGIC_MISSILE").expect("Magic Missile should exist");
        assert_eq!(magic_missile.behavior, SpellBehavior::Channel);
        assert!(spell_definition_drives_active_projectile(magic_missile));

        let electrocute = spell_definition_by_str("ELECTROCUTE").expect("Electrocute should exist");
        assert_eq!(electrocute.behavior, SpellBehavior::Channel);
        assert!(!spell_definition_drives_active_projectile(electrocute));
    }

    #[test]
    fn projectile_damage_falls_linearly_over_authoritative_lifetime() {
        let caster_id = test_identity(1);
        let mut projectile = test_projectile(caster_id);
        projectile.damage = 30;
        projectile.lifetime = 2.0;
        projectile.age = 1.0;

        let mut definition = spell_definition_by_str("FIREBALL")
            .expect("FIREBALL should exist")
            .clone();
        definition
            .secondary
            .projectile
            .as_mut()
            .expect("FIREBALL should have projectile tunables")
            .damage_multiplier_at_lifetime_end = 0.25;

        assert_eq!(
            projectile_damage_at_current_lifetime(&projectile, &definition),
            19
        );
        projectile.age = 2.0;
        assert_eq!(
            projectile_damage_at_current_lifetime(&projectile, &definition),
            8
        );
    }

    #[test]
    fn projectile_presentation_terminal_policy_keeps_updates_out_of_combat_event_stream() {
        assert!(!is_projectile_presentation_terminal(
            COMBAT_EVENT_UPDATE,
            COMBAT_METADATA_NONE,
            "",
            ""
        ));
        assert!(!should_emit_projectile_combat_event(
            COMBAT_EVENT_UPDATE,
            false
        ));

        assert!(is_projectile_presentation_terminal(
            COMBAT_EVENT_IMPACT,
            COMBAT_METADATA_NONE,
            "",
            ""
        ));
        assert!(should_emit_projectile_combat_event(
            COMBAT_EVENT_IMPACT,
            true
        ));
    }

    #[test]
    fn non_terminal_projectile_contacts_are_presentation_only() {
        let terminal = is_projectile_presentation_terminal(
            COMBAT_EVENT_CONTACT,
            PROJECTILE_CONTACT_METADATA_KIND,
            PROJECTILE_CONTACT_TERMINAL_KEY,
            PROJECTILE_CONTACT_NON_TERMINAL_VALUE,
        );

        assert!(!terminal);
        assert!(!should_emit_projectile_combat_event(
            COMBAT_EVENT_CONTACT,
            terminal
        ));
    }

    #[test]
    fn orbit_projectile_position_tracks_caster_radius_height_and_angular_speed() {
        let caster_id = test_identity(1);
        let mut caster = test_snapshot(caster_id);
        caster.pos_x = 10.0;
        caster.pos_y = 2.0;
        caster.pos_z = -4.0;

        let mut projectile = test_projectile(caster_id);
        projectile.age = 1.0;
        update_orbit_projectile_position(&mut projectile, &caster);

        assert!((projectile.pos_x - 11.5).abs() < 0.0001);
        assert!((projectile.pos_y - 3.0).abs() < 0.0001);
        assert!((projectile.pos_z - -4.0).abs() < 0.0001);
        assert!(projectile.dir_x.abs() < 0.0001);
        assert_eq!(projectile.dir_y, 0.0);
        assert!((projectile.dir_z - -1.0).abs() < 0.0001);
        assert!((projectile.traveled - std::f32::consts::FRAC_PI_2 * 1.5).abs() < 0.0001);
    }

    #[test]
    fn curved_target_projectile_position_uses_authored_control_point() {
        let caster_id = test_identity(1);
        let mut projectile = test_projectile(caster_id);
        projectile.motion_kind = PROJECTILE_MOTION_CURVED_TARGET.to_string();
        projectile.origin_x = 0.0;
        projectile.origin_y = 0.0;
        projectile.origin_z = 0.0;
        projectile.curve_control_x = 5.0;
        projectile.curve_control_y = 5.0;
        projectile.curve_control_z = 0.0;
        projectile.curve_end_x = 10.0;
        projectile.curve_end_y = 0.0;
        projectile.curve_end_z = 0.0;

        let (x, y, z) = curved_target_position(&projectile, 0.5);

        assert!((x - 5.0).abs() < 0.0001);
        assert!((y - 2.5).abs() < 0.0001);
        assert!(z.abs() < 0.0001);
    }

    #[test]
    fn orbit_projectile_contact_uses_projectile_and_target_capsules() {
        let caster_id = test_identity(1);
        let mut projectile = test_projectile(caster_id);
        projectile.pos_x = 0.0;
        projectile.pos_y = 1.0;
        projectile.pos_z = 0.0;

        let mut target = test_snapshot(test_identity(2));
        target.pos_x = 0.94;
        assert!(orbit_projectile_hits_target(&projectile, &target));

        target.pos_x = 0.96;
        assert!(!orbit_projectile_hits_target(&projectile, &target));

        target.pos_x = 0.0;
        target.pos_y = 2.0;
        projectile.pos_y = 4.46;
        assert!(!orbit_projectile_hits_target(&projectile, &target));
    }

    #[test]
    fn terrain_conforming_collision_is_authored_on_ground_slash_projectiles() {
        let ground_slash = spell_definition_by_str("GROUND_SLASH")
            .expect("GROUND_SLASH should be present in the runtime spell catalog");
        let fireball = spell_definition_by_str("FIREBALL")
            .expect("FIREBALL should be present in the runtime spell catalog");

        let ground_slash_uses_terrain =
            projectile_uses_terrain_conforming_collision(Some(ground_slash));
        let fireball_uses_terrain = projectile_uses_terrain_conforming_collision(Some(fireball));

        assert!(ground_slash_uses_terrain);
        assert!(!fireball_uses_terrain);
        assert!(!projectile_uses_terrain_conforming_collision(None));
    }

    #[test]
    fn boomerang_projectile_target_state_is_phase_scoped() {
        let projectile_instance_id = "test:p0";
        let target = test_identity(2);

        let outbound_key =
            boomerang_projectile_target_state_key(projectile_instance_id, target, false);
        let return_key =
            boomerang_projectile_target_state_key(projectile_instance_id, target, true);

        assert_ne!(outbound_key, return_key);
        assert_ne!(
            outbound_key,
            projectile_target_state_key(projectile_instance_id, target)
        );
        assert_ne!(
            return_key,
            projectile_target_state_key(projectile_instance_id, target)
        );
    }
}

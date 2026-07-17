use spacetimedb::{Identity, ReducerContext, Table};

use crate::arena::players_share_world_context;
use crate::combat::actor_snapshot::{CombatActorSnapshot, CombatActorSnapshotSet};
use crate::combat::scene_query::{aoe_hits_player, first_hit_on_segment, SceneHitKind};
use crate::combat::{
    finish_combat_projectile_with_event, queue_effects, ActiveCombatProjectile, DamageDelivery,
    EffectPacket, StatusPolarity, DAMAGE_SOURCE_KIND_SPELL,
};
use crate::relations::target_audience_allows;

use super::casting::resolve_blockable_spell_hit;
use super::events::{
    emit_spell_combat_event, emit_spell_combat_event_with_damage, SpellCombatEventPayload,
    SpellCombatEventScalar, Vec3,
};
use super::manifest::{BespokeRuntimeSpell, ImpactEffect, SpellDefinition};
use super::{ActiveBespokeSpell, SpellId, EVENT_FIZZLE, EVENT_IMPACT};

#[allow(unused_imports)]
use crate::combat::active_combat_projectile as _;
#[allow(unused_imports)]
use crate::spells::active_bespoke_spell as _;

struct NegateSpellCollision {
    point_x: f32,
    point_y: f32,
    point_z: f32,
    event_type: &'static str,
}

#[allow(dead_code)]
pub(crate) fn tick_bespoke_spells(ctx: &ReducerContext, dt: f32) -> Result<(), String> {
    let actor_snapshots = CombatActorSnapshotSet::collect(ctx);
    tick_bespoke_spells_with_snapshots(ctx, dt, &actor_snapshots)
}

pub(crate) fn tick_bespoke_spells_with_snapshots(
    ctx: &ReducerContext,
    dt: f32,
    actor_snapshots: &CombatActorSnapshotSet,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let spells: Vec<ActiveBespokeSpell> = ctx.db.active_bespoke_spell().iter().collect();
    if spells.is_empty() {
        return Ok(());
    }

    let players = actor_snapshots.as_slice();
    for mut spell in spells {
        if ctx
            .db
            .active_bespoke_spell()
            .spell_id()
            .find(spell.spell_id.clone())
            .is_none()
        {
            continue;
        }
        let Ok(spell_id) = SpellId::new(spell.kind.as_str()) else {
            // Unknown spell ids are removed to avoid dangling active rows.
            ctx.db
                .active_bespoke_spell()
                .spell_id()
                .delete(spell.spell_id.clone());
            continue;
        };
        if super::catalog::spell_definition(&spell_id).is_none() {
            ctx.db
                .active_bespoke_spell()
                .spell_id()
                .delete(spell.spell_id.clone());
            continue;
        };

        spell.age += dt;

        tick_bespoke_spell_instance(ctx, now, dt, spell, &spell_id, players);
    }

    Ok(())
}

fn tick_bespoke_spell_instance(
    ctx: &ReducerContext,
    now: spacetimedb::Timestamp,
    dt: f32,
    spell: ActiveBespokeSpell,
    kind: &SpellId,
    players: &[CombatActorSnapshot],
) {
    match BespokeRuntimeSpell::from_spell_id(kind) {
        Some(BespokeRuntimeSpell::Meteor) => tick_meteor_spell(ctx, now, dt, spell, players),
        Some(BespokeRuntimeSpell::Negate) => tick_negate_spell(ctx, now, dt, spell),
        _ => {
            ctx.db
                .active_bespoke_spell()
                .spell_id()
                .delete(spell.spell_id);
        }
    }
}

fn bespoke_spell_definition(
    spell: BespokeRuntimeSpell,
) -> Result<&'static SpellDefinition, String> {
    super::catalog::require_spell_definition_by_str(spell.as_str())
}

fn push_impact_effect_packets(
    effects: &mut Vec<EffectPacket>,
    impact_effects: &[ImpactEffect],
    source: Identity,
    target: Identity,
    spell_id: &str,
    action_key: &str,
    positive_damage: bool,
    dir_x: f32,
    dir_z: f32,
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
            dir_x,
            dir_z,
        ));
    }
}

fn tick_negate_spell(
    ctx: &ReducerContext,
    now: spacetimedb::Timestamp,
    dt: f32,
    mut spell: ActiveBespokeSpell,
) {
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Negate)
        .expect("validated spell catalog must define NEGATE");
    spell.traveled = (spell.traveled + spell.speed * dt)
        .max(spell.traveled)
        .min(definition.max_distance);
    spell.pos_x = spell.origin_x;
    spell.pos_y = spell.origin_y;
    spell.pos_z = spell.origin_z;

    let active_bespoke_spells: Vec<ActiveBespokeSpell> =
        ctx.db.active_bespoke_spell().iter().collect();
    for other_spell in active_bespoke_spells {
        if other_spell.spell_id == spell.spell_id {
            continue;
        }
        if !players_share_world_context(ctx, spell.caster, other_spell.caster) {
            continue;
        }
        let Some(other_definition) =
            super::catalog::spell_definition_by_str(other_spell.kind.as_str())
        else {
            continue;
        };
        let other_kind = &other_definition.kind;
        if let Some(collision) = negate_hits_active_bespoke_spell(
            spell.origin_x,
            spell.origin_y,
            spell.origin_z,
            spell.traveled,
            &other_spell,
            other_kind,
            dt,
        ) {
            finish_active_bespoke_spell_with_event(
                ctx,
                now,
                &other_spell,
                other_kind,
                collision.event_type,
                collision.point_x,
                collision.point_y,
                collision.point_z,
            );
        }
    }

    let active_projectiles: Vec<ActiveCombatProjectile> =
        ctx.db.active_combat_projectile().iter().collect();
    for projectile in active_projectiles {
        if !players_share_world_context(ctx, spell.caster, projectile.caster) {
            continue;
        }
        if let Some(collision) = negate_hits_active_combat_projectile(
            spell.origin_x,
            spell.origin_y,
            spell.origin_z,
            spell.traveled,
            &projectile,
            dt,
        ) {
            finish_combat_projectile_with_event(
                ctx,
                now,
                &projectile,
                collision.event_type,
                collision.point_x,
                collision.point_y,
                collision.point_z,
            );
        }
    }

    if spell.age >= spell.lifetime || spell.traveled >= spell.max_distance {
        emit_spell_combat_event(
            ctx,
            SpellCombatEventPayload {
                action_instance_id: spell.spell_id.as_str(),
                ability_id: spell.ability_id.as_str(),
                kind: &definition.kind,
                event_type: EVENT_FIZZLE,
                caster: spell.caster,
                hit: Identity::ZERO,
                origin: Vec3::new(spell.origin_x, spell.origin_y, spell.origin_z),
                direction: Vec3::new(0.0, 1.0, 0.0),
                speed: spell.speed,
                max_distance: spell.max_distance,
                scalar: SpellCombatEventScalar::None,
                sequence_index: 0,
                sequence_count: 1,
                point: Vec3::new(spell.origin_x, spell.origin_y, spell.origin_z),
                now,
            },
        );
        ctx.db
            .active_bespoke_spell()
            .spell_id()
            .delete(spell.spell_id.clone());
        return;
    }

    ctx.db.active_bespoke_spell().spell_id().update(spell);
}

fn tick_meteor_spell(
    ctx: &ReducerContext,
    now: spacetimedb::Timestamp,
    dt: f32,
    mut spell: ActiveBespokeSpell,
    players: &[CombatActorSnapshot],
) {
    let block_source = players
        .iter()
        .find(|player| player.player_id == spell.caster)
        .copied();
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Meteor)
        .expect("validated spell catalog must define METEOR");
    let flight_duration = spell.lifetime.max(0.01);
    let prev_traveled = spell.traveled.max(0.0);
    spell.traveled = (spell.traveled + dt).max(0.0);

    let prev_t = (prev_traveled / flight_duration).clamp(0.0, 1.0);
    let next_t = (spell.traveled / flight_duration).clamp(0.0, 1.0);

    let start_x = spell.origin_x + spell.dir_x * spell.max_distance * prev_t;
    let start_y = spell.origin_y + spell.dir_y * spell.max_distance * prev_t;
    let start_z = spell.origin_z + spell.dir_z * spell.max_distance * prev_t;
    let end_x = spell.origin_x + spell.dir_x * spell.max_distance * next_t;
    let end_y = spell.origin_y + spell.dir_y * spell.max_distance * next_t;
    let end_z = spell.origin_z + spell.dir_z * spell.max_distance * next_t;

    let mut impacted = next_t >= 1.0;
    let mut impact_x = end_x;
    let mut impact_y = end_y;
    let mut impact_z = end_z;
    let mut impact_target: Option<Identity> = None;

    let valid_players: Vec<_> = players
        .iter()
        .copied()
        .filter(|player| {
            target_audience_allows(
                ctx,
                spell.caster,
                player.player_id,
                definition.target_audience,
            )
        })
        .collect();

    if let Some(hit) = first_hit_on_segment(
        ctx,
        spell.caster,
        start_x,
        start_y,
        start_z,
        end_x,
        end_y,
        end_z,
        definition.projectile_radius,
        &valid_players,
    ) {
        impacted = true;
        impact_x = hit.x;
        impact_y = hit.y;
        impact_z = hit.z;
        if let SceneHitKind::Player(target_id) = hit.kind {
            impact_target = Some(target_id);
        }
    }

    if impacted {
        emit_spell_combat_event_with_damage(
            ctx,
            SpellCombatEventPayload {
                action_instance_id: spell.spell_id.as_str(),
                ability_id: spell.ability_id.as_str(),
                kind: &definition.kind,
                event_type: EVENT_IMPACT,
                caster: spell.caster,
                hit: impact_target.unwrap_or(Identity::ZERO),
                origin: Vec3::new(spell.origin_x, spell.origin_y, spell.origin_z),
                direction: Vec3::new(spell.dir_x, spell.dir_y, spell.dir_z),
                speed: spell.speed,
                max_distance: spell.max_distance,
                scalar: SpellCombatEventScalar::TravelDurationSeconds(spell.lifetime),
                sequence_index: 0,
                sequence_count: 1,
                point: Vec3::new(impact_x, impact_y, impact_z),
                now,
            },
            definition.damage,
        );
        let spell_id = spell.spell_id.clone();
        ctx.db
            .active_bespoke_spell()
            .spell_id()
            .delete(spell_id.clone());

        let mut effects = Vec::new();
        for player in players {
            if !player.alive || player.player_id == spell.caster {
                continue;
            }
            if !players_share_world_context(ctx, spell.caster, player.player_id) {
                continue;
            }
            if !target_audience_allows(
                ctx,
                spell.caster,
                player.player_id,
                definition.target_audience,
            ) {
                continue;
            }
            if aoe_hits_player(impact_x, impact_y, impact_z, definition.radius, player) {
                if resolve_blockable_spell_hit(
                    ctx,
                    spell_id.as_str(),
                    spell.ability_id.as_str(),
                    &definition.kind,
                    spell.caster,
                    player,
                    block_source
                        .map(|entry| entry.pos_x)
                        .unwrap_or(spell.origin_x),
                    block_source
                        .map(|entry| entry.pos_y + entry.hit_height)
                        .unwrap_or(spell.origin_y),
                    block_source
                        .map(|entry| entry.pos_z)
                        .unwrap_or(spell.origin_z),
                    spell.dir_x,
                    spell.dir_y,
                    spell.dir_z,
                    spell.speed,
                    spell.max_distance,
                    impact_x,
                    impact_y,
                    impact_z,
                    definition.damage,
                    definition.block_behavior.as_str(),
                    now,
                ) {
                    continue;
                }
                effects.push(EffectPacket::Damage {
                    amount: definition.damage,
                    damage_type: definition.damage_type,
                    source: spell.caster,
                    target: player.player_id,
                    spell_id: spell_id.clone(),
                    delivery: DamageDelivery::Direct,
                    direct_action_key: spell.spell_id.clone(),
                    source_kind: DAMAGE_SOURCE_KIND_SPELL.to_string(),
                });
                if let Some(area) = definition.secondary.area.as_ref() {
                    let (dir_x, dir_z) = radial_knockback_direction(
                        impact_x,
                        impact_z,
                        spell.origin_x,
                        spell.origin_z,
                        player,
                    );
                    push_impact_effect_packets(
                        &mut effects,
                        area.impact_effects.as_slice(),
                        spell.caster,
                        player.player_id,
                        spell_id.as_str(),
                        definition.kind.as_str(),
                        definition.damage > 0,
                        dir_x,
                        dir_z,
                    );
                }
            }
        }
        if !effects.is_empty() {
            queue_effects(ctx, effects);
        }
        return;
    }

    spell.pos_x = end_x;
    spell.pos_y = end_y;
    spell.pos_z = end_z;
    ctx.db.active_bespoke_spell().spell_id().update(spell);
}

fn radial_knockback_direction(
    center_x: f32,
    center_z: f32,
    origin_x: f32,
    origin_z: f32,
    target: &CombatActorSnapshot,
) -> (f32, f32) {
    for (dx, dz) in [
        (target.pos_x - center_x, target.pos_z - center_z),
        (target.pos_x - origin_x, target.pos_z - origin_z),
    ] {
        let len_sq = dx * dx + dz * dz;
        if len_sq > 0.0001 {
            let inv_len = 1.0 / len_sq.sqrt();
            return (dx * inv_len, dz * inv_len);
        }
    }
    (target.facing_yaw.sin(), target.facing_yaw.cos())
}

fn finish_active_bespoke_spell_with_event(
    ctx: &ReducerContext,
    now: spacetimedb::Timestamp,
    spell: &ActiveBespokeSpell,
    kind: &SpellId,
    event_type: &'static str,
    point_x: f32,
    point_y: f32,
    point_z: f32,
) {
    if ctx
        .db
        .active_bespoke_spell()
        .spell_id()
        .find(spell.spell_id.clone())
        .is_none()
    {
        return;
    }

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell.spell_id.as_str(),
            ability_id: spell.ability_id.as_str(),
            kind,
            event_type,
            caster: spell.caster,
            hit: Identity::ZERO,
            origin: Vec3::new(spell.origin_x, spell.origin_y, spell.origin_z),
            direction: Vec3::new(spell.dir_x, spell.dir_y, spell.dir_z),
            speed: spell.speed,
            max_distance: spell.max_distance,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: Vec3::new(point_x, point_y, point_z),
            now,
        },
    );
    ctx.db
        .active_bespoke_spell()
        .spell_id()
        .delete(spell.spell_id.clone());
}

fn negate_hits_active_bespoke_spell(
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    radius: f32,
    other_spell: &ActiveBespokeSpell,
    other_kind: &SpellId,
    dt: f32,
) -> Option<NegateSpellCollision> {
    let collision_radius = radius + active_bespoke_spell_collision_radius(other_kind);
    if collision_radius <= 0.0 {
        return None;
    }

    let start = (other_spell.pos_x, other_spell.pos_y, other_spell.pos_z);
    let end = estimate_spell_position(other_spell, other_kind, dt);
    let impact_point =
        segment_sphere_entry_point(start, end, (origin_x, origin_y, origin_z), collision_radius)?;
    Some(NegateSpellCollision {
        point_x: impact_point.0,
        point_y: impact_point.1,
        point_z: impact_point.2,
        event_type: negate_collision_event_type(other_kind),
    })
}

fn negate_hits_active_combat_projectile(
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    radius: f32,
    projectile: &ActiveCombatProjectile,
    dt: f32,
) -> Option<NegateSpellCollision> {
    let collision_radius = radius + projectile.radius;
    if collision_radius <= 0.0 {
        return None;
    }

    let start = (projectile.pos_x, projectile.pos_y, projectile.pos_z);
    let end = (
        projectile.pos_x + projectile.dir_x * projectile.speed * dt,
        projectile.pos_y + projectile.dir_y * projectile.speed * dt,
        projectile.pos_z + projectile.dir_z * projectile.speed * dt,
    );
    let impact_point =
        segment_sphere_entry_point(start, end, (origin_x, origin_y, origin_z), collision_radius)?;
    Some(NegateSpellCollision {
        point_x: impact_point.0,
        point_y: impact_point.1,
        point_z: impact_point.2,
        event_type: EVENT_IMPACT,
    })
}

fn estimate_spell_position(spell: &ActiveBespokeSpell, kind: &SpellId, dt: f32) -> (f32, f32, f32) {
    match BespokeRuntimeSpell::from_spell_id(kind) {
        Some(BespokeRuntimeSpell::Meteor) => {
            let flight_duration = spell.lifetime.max(0.01);
            let next_traveled = (spell.traveled + dt).clamp(0.0, flight_duration);
            let next_t = (next_traveled / flight_duration).clamp(0.0, 1.0);
            (
                spell.origin_x + spell.dir_x * spell.max_distance * next_t,
                spell.origin_y + spell.dir_y * spell.max_distance * next_t,
                spell.origin_z + spell.dir_z * spell.max_distance * next_t,
            )
        }
        Some(BespokeRuntimeSpell::Negate) => (spell.origin_x, spell.origin_y, spell.origin_z),
        _ => (spell.pos_x, spell.pos_y, spell.pos_z),
    }
}

fn active_bespoke_spell_collision_radius(kind: &SpellId) -> f32 {
    let Some(definition) = super::catalog::spell_definition(kind) else {
        return 0.0;
    };
    if BespokeRuntimeSpell::from_spell_id(kind) == Some(BespokeRuntimeSpell::Meteor) {
        return definition.projectile_radius;
    }
    0.0
}

fn negate_collision_event_type(kind: &SpellId) -> &'static str {
    if BespokeRuntimeSpell::from_spell_id(kind) == Some(BespokeRuntimeSpell::Meteor) {
        EVENT_IMPACT
    } else {
        EVENT_FIZZLE
    }
}

fn segment_sphere_entry_point(
    start: (f32, f32, f32),
    end: (f32, f32, f32),
    center: (f32, f32, f32),
    radius: f32,
) -> Option<(f32, f32, f32)> {
    let (sx, sy, sz) = start;
    let (ex, ey, ez) = end;
    let (cx, cy, cz) = center;
    let seg_x = ex - sx;
    let seg_y = ey - sy;
    let seg_z = ez - sz;
    let seg_len_sq = seg_x * seg_x + seg_y * seg_y + seg_z * seg_z;

    if seg_len_sq <= 0.000001 {
        let dx = sx - cx;
        let dy = sy - cy;
        let dz = sz - cz;
        if dx * dx + dy * dy + dz * dz <= radius * radius {
            return Some(start);
        }
        return None;
    }

    let a = seg_len_sq;
    let oc_x = sx - cx;
    let oc_y = sy - cy;
    let oc_z = sz - cz;
    let b = 2.0 * (oc_x * seg_x + oc_y * seg_y + oc_z * seg_z);
    let c = oc_x * oc_x + oc_y * oc_y + oc_z * oc_z - radius * radius;
    let disc = b * b - 4.0 * a * c;
    if disc >= 0.0 {
        let sqrt_disc = disc.sqrt();
        let inv_denom = 0.5 / a;
        let t1 = (-b - sqrt_disc) * inv_denom;
        let t2 = (-b + sqrt_disc) * inv_denom;
        let t = if (0.0..=1.0).contains(&t1) {
            Some(t1)
        } else if (0.0..=1.0).contains(&t2) {
            Some(t2)
        } else {
            None
        };
        if let Some(t) = t {
            return Some((sx + seg_x * t, sy + seg_y * t, sz + seg_z * t));
        }
    }

    let t =
        (((cx - sx) * seg_x + (cy - sy) * seg_y + (cz - sz) * seg_z) / seg_len_sq).clamp(0.0, 1.0);
    let closest_x = sx + seg_x * t;
    let closest_y = sy + seg_y * t;
    let closest_z = sz + seg_z * t;
    let dx = closest_x - cx;
    let dy = closest_y - cy;
    let dz = closest_z - cz;
    if dx * dx + dy * dy + dz * dz > radius * radius {
        return None;
    }

    Some((closest_x, closest_y, closest_z))
}

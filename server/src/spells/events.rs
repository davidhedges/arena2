use spacetimedb::{Identity, ReducerContext, Table, Timestamp};

use crate::combat::{
    timestamp_to_micros, CombatEvent, COMBAT_METADATA_NONE, COMBAT_SCALAR_BEAM_CHARGE_PCT,
    COMBAT_SCALAR_NONE, COMBAT_SCALAR_TRAVEL_DURATION_SECONDS, COMBAT_SEQUENCE_BEAM,
    COMBAT_SEQUENCE_NONE,
};

use super::{SpellCounter, SpellId};

#[allow(unused_imports)]
use crate::combat::combat_event as _;
#[allow(unused_imports)]
use crate::spells::spell_counter as _;

#[derive(Clone, Copy, Debug)]
pub(crate) struct Vec3 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

impl Vec3 {
    pub const fn new(x: f32, y: f32, z: f32) -> Self {
        Self { x, y, z }
    }
}

#[derive(Clone, Copy, Debug)]
pub(super) struct SpellCombatEventPayload<'a> {
    pub action_instance_id: &'a str,
    pub ability_id: &'a str,
    pub kind: &'a SpellId,
    pub event_type: &'a str,
    pub caster: Identity,
    pub hit: Identity,
    pub origin: Vec3,
    pub direction: Vec3,
    pub speed: f32,
    pub max_distance: f32,
    pub scalar: SpellCombatEventScalar,
    pub sequence_index: u32,
    pub sequence_count: u32,
    pub point: Vec3,
    pub now: Timestamp,
}

#[derive(Clone, Copy, Debug)]
pub(super) enum SpellCombatEventScalar {
    None,
    TravelDurationSeconds(f32),
    BeamChargePct(f32),
}

impl SpellCombatEventScalar {
    fn encode(self) -> (&'static str, f32) {
        match self {
            SpellCombatEventScalar::None => (COMBAT_SCALAR_NONE, 0.0),
            SpellCombatEventScalar::TravelDurationSeconds(seconds) => {
                (COMBAT_SCALAR_TRAVEL_DURATION_SECONDS, seconds.max(0.0))
            }
            SpellCombatEventScalar::BeamChargePct(charge) => {
                (COMBAT_SCALAR_BEAM_CHARGE_PCT, charge.clamp(0.0, 1.0))
            }
        }
    }
}

pub(super) fn next_spell_instance_id(ctx: &ReducerContext, caster: Identity) -> String {
    let now = ctx.timestamp;
    let local_counter = if let Some(mut counter) = ctx.db.spell_counter().caster().find(caster) {
        if counter.last_cast_at == now {
            counter.counter = counter.counter.saturating_add(1);
        } else {
            counter.last_cast_at = now;
            counter.counter = 0;
        }
        let value = counter.counter;
        ctx.db.spell_counter().caster().update(counter);
        value
    } else {
        ctx.db.spell_counter().insert(SpellCounter {
            caster,
            last_cast_at: now,
            counter: 0,
        });
        0
    };

    let time_key = format!("{:?}", now);
    format!("{}:{}:{}", time_key, caster.to_hex(), local_counter)
}

pub(super) fn emit_spell_combat_event(ctx: &ReducerContext, payload: SpellCombatEventPayload<'_>) {
    emit_spell_combat_event_with_damage(ctx, payload, 0);
}

pub(super) fn emit_spell_combat_event_with_damage(
    ctx: &ReducerContext,
    payload: SpellCombatEventPayload<'_>,
    damage: i32,
) {
    let (scalar_kind, scalar_value) = payload.scalar.encode();
    let sequence_kind = if payload.sequence_count > 1 {
        COMBAT_SEQUENCE_BEAM
    } else {
        COMBAT_SEQUENCE_NONE
    };
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: payload.action_instance_id.to_string(),
        action_kind: payload.kind.as_str().to_string(),
        ability_id: payload.ability_id.to_string(),
        hit_index: -1,
        event_type: payload.event_type.to_string(),
        source_kind: "SPELL".to_string(),
        caster: payload.caster,
        hit: payload.hit,
        origin_x: payload.origin.x,
        origin_y: payload.origin.y,
        origin_z: payload.origin.z,
        dir_x: payload.direction.x,
        dir_y: payload.direction.y,
        dir_z: payload.direction.z,
        speed: payload.speed,
        max_distance: payload.max_distance,
        scalar_kind: scalar_kind.to_string(),
        scalar_value,
        sequence_kind: sequence_kind.to_string(),
        sequence_index: payload.sequence_index,
        sequence_count: payload.sequence_count,
        point_x: payload.point.x,
        point_y: payload.point.y,
        point_z: payload.point.z,
        created_at: payload.now,
        created_at_micros: timestamp_to_micros(payload.now),
        damage,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

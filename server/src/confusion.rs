use spacetimedb::{table, Identity, ReducerContext, Table, Timestamp};

#[allow(unused_imports)]
use crate::confusion::confusion_wander_runtime as _;

pub(crate) const CONFUSION_WANDER_RADIUS_METERS: f32 = 2.25;
const CONFUSION_STEP_METERS: f32 = 0.7;
const CONFUSION_ARRIVAL_EPSILON_METERS: f32 = 0.08;
const CONFUSION_STAND_CHANCE_DENOMINATOR: u64 = 4;
const CONFUSION_STAND_MIN_MS: u64 = 250;
const CONFUSION_STAND_VARIATION_MS: u64 = 501;
const CONFUSION_MOVE_BASE_MS: u64 = 350;
const CONFUSION_MOVE_PER_EXTRA_STEP_MS: u64 = 250;

/// Private decision state for server-authoritative confused wandering.
///
/// The status row remains the replicated source of truth for whether an actor
/// is confused. This row only remembers the cast-time leash center and the
/// current short movement/pause decision.
#[table(accessor = confusion_wander_runtime)]
#[derive(Clone)]
pub struct ConfusionWanderRuntime {
    #[primary_key]
    pub target: Identity,
    pub status_id: u64,
    pub anchor_x: f32,
    pub anchor_z: f32,
    pub target_x: f32,
    pub target_z: f32,
    pub decision_sequence: u64,
    pub decision_ends_at_micros: i64,
    pub standing: bool,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct ConfusionWanderDirective {
    pub target_x: f32,
    pub target_z: f32,
    pub yaw: f32,
    pub standing: bool,
}

pub(crate) fn begin_confusion_wander(
    ctx: &ReducerContext,
    target: Identity,
    status_id: u64,
    anchor_x: f32,
    anchor_z: f32,
    now: Timestamp,
) {
    let runtime = ConfusionWanderRuntime {
        target,
        status_id,
        anchor_x,
        anchor_z,
        target_x: anchor_x,
        target_z: anchor_z,
        decision_sequence: 0,
        decision_ends_at_micros: timestamp_micros(now),
        standing: true,
    };
    if ctx
        .db
        .confusion_wander_runtime()
        .target()
        .find(target)
        .is_some()
    {
        ctx.db.confusion_wander_runtime().target().update(runtime);
    } else {
        ctx.db.confusion_wander_runtime().insert(runtime);
    }
}

pub(crate) fn clear_confusion_wander(ctx: &ReducerContext, target: Identity) {
    ctx.db.confusion_wander_runtime().target().delete(target);
}

pub(crate) fn confusion_wander_directive(
    ctx: &ReducerContext,
    target: Identity,
    current_x: f32,
    current_z: f32,
    now: Timestamp,
) -> Option<ConfusionWanderDirective> {
    let mut runtime = ctx.db.confusion_wander_runtime().target().find(target)?;
    let now_micros = timestamp_micros(now);
    let reached_target =
        horizontal_distance_squared(current_x, current_z, runtime.target_x, runtime.target_z)
            <= CONFUSION_ARRIVAL_EPSILON_METERS * CONFUSION_ARRIVAL_EPSILON_METERS;
    if now_micros >= runtime.decision_ends_at_micros || (!runtime.standing && reached_target) {
        choose_next_decision(&mut runtime, current_x, current_z, now_micros);
        ctx.db
            .confusion_wander_runtime()
            .target()
            .update(runtime.clone());
    }

    Some(directive_from_runtime(&runtime, current_x, current_z))
}

fn choose_next_decision(
    runtime: &mut ConfusionWanderRuntime,
    current_x: f32,
    current_z: f32,
    now_micros: i64,
) {
    runtime.decision_sequence = runtime.decision_sequence.saturating_add(1);
    let random =
        stable_decision_random(runtime.target, runtime.status_id, runtime.decision_sequence);
    if random % CONFUSION_STAND_CHANCE_DENOMINATOR == 0 {
        runtime.target_x = current_x;
        runtime.target_z = current_z;
        runtime.standing = true;
        let stand_ms = CONFUSION_STAND_MIN_MS
            + splitmix64(random ^ 0x6a09_e667_f3bc_c909) % CONFUSION_STAND_VARIATION_MS;
        runtime.decision_ends_at_micros =
            now_micros.saturating_add((stand_ms as i64).saturating_mul(1_000));
        return;
    }

    let steps = 1 + (splitmix64(random ^ 0xbb67_ae85_84ca_a73b) & 1) as u32;
    let distance = CONFUSION_STEP_METERS * steps as f32;
    let angle_unit =
        (splitmix64(random ^ 0x3c6e_f372_fe94_f82b) & 0x00ff_ffff) as f32 / 0x0100_0000_u32 as f32;
    let angle = angle_unit * std::f32::consts::TAU;
    let candidate_x = current_x + angle.sin() * distance;
    let candidate_z = current_z + angle.cos() * distance;
    let (target_x, target_z) = clamp_to_leash(
        runtime.anchor_x,
        runtime.anchor_z,
        candidate_x,
        candidate_z,
        CONFUSION_WANDER_RADIUS_METERS,
    );
    runtime.target_x = target_x;
    runtime.target_z = target_z;
    runtime.standing = false;
    let move_ms = CONFUSION_MOVE_BASE_MS
        .saturating_add(CONFUSION_MOVE_PER_EXTRA_STEP_MS.saturating_mul(u64::from(steps - 1)));
    runtime.decision_ends_at_micros =
        now_micros.saturating_add((move_ms as i64).saturating_mul(1_000));
}

fn directive_from_runtime(
    runtime: &ConfusionWanderRuntime,
    current_x: f32,
    current_z: f32,
) -> ConfusionWanderDirective {
    let dx = runtime.target_x - current_x;
    let dz = runtime.target_z - current_z;
    let standing = runtime.standing
        || dx * dx + dz * dz <= CONFUSION_ARRIVAL_EPSILON_METERS * CONFUSION_ARRIVAL_EPSILON_METERS;
    ConfusionWanderDirective {
        target_x: runtime.target_x,
        target_z: runtime.target_z,
        yaw: if standing { 0.0 } else { dx.atan2(dz) },
        standing,
    }
}

pub(crate) fn clamp_completed_wander_step(
    start_x: f32,
    start_z: f32,
    proposed_x: f32,
    proposed_z: f32,
    directive: ConfusionWanderDirective,
) -> (f32, f32) {
    if directive.standing {
        return (start_x, start_z);
    }
    let target_dx = directive.target_x - start_x;
    let target_dz = directive.target_z - start_z;
    let target_distance_squared = target_dx * target_dx + target_dz * target_dz;
    if target_distance_squared
        <= CONFUSION_ARRIVAL_EPSILON_METERS * CONFUSION_ARRIVAL_EPSILON_METERS
    {
        return (directive.target_x, directive.target_z);
    }
    let proposed_dx = proposed_x - start_x;
    let proposed_dz = proposed_z - start_z;
    let progress = proposed_dx * target_dx + proposed_dz * target_dz;
    if progress >= target_distance_squared {
        (directive.target_x, directive.target_z)
    } else {
        (proposed_x, proposed_z)
    }
}

fn clamp_to_leash(
    anchor_x: f32,
    anchor_z: f32,
    candidate_x: f32,
    candidate_z: f32,
    radius: f32,
) -> (f32, f32) {
    let dx = candidate_x - anchor_x;
    let dz = candidate_z - anchor_z;
    let distance_squared = dx * dx + dz * dz;
    let radius = radius.max(0.0);
    if distance_squared <= radius * radius || distance_squared <= f32::EPSILON {
        return (candidate_x, candidate_z);
    }
    let scalar = radius / distance_squared.sqrt();
    (anchor_x + dx * scalar, anchor_z + dz * scalar)
}

fn horizontal_distance_squared(ax: f32, az: f32, bx: f32, bz: f32) -> f32 {
    let dx = bx - ax;
    let dz = bz - az;
    dx * dx + dz * dz
}

fn stable_decision_random(target: Identity, status_id: u64, decision_sequence: u64) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in target.to_byte_array() {
        hash ^= u64::from(byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    splitmix64(hash ^ status_id.rotate_left(17) ^ decision_sequence.rotate_left(37))
}

fn splitmix64(mut value: u64) -> u64 {
    value = value.wrapping_add(0x9e37_79b9_7f4a_7c15);
    value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    value ^ (value >> 31)
}

fn timestamp_micros(timestamp: Timestamp) -> i64 {
    timestamp.to_micros_since_unix_epoch()
}

#[cfg(test)]
mod tests {
    use super::{
        clamp_completed_wander_step, clamp_to_leash, directive_from_runtime,
        stable_decision_random, ConfusionWanderDirective, ConfusionWanderRuntime,
        CONFUSION_WANDER_RADIUS_METERS,
    };
    use spacetimedb::Identity;

    #[test]
    fn leash_clamps_every_wander_target_to_the_small_confusion_radius() {
        let (x, z) = clamp_to_leash(4.0, -3.0, 20.0, 9.0, CONFUSION_WANDER_RADIUS_METERS);
        let distance = ((x - 4.0).powi(2) + (z + 3.0).powi(2)).sqrt();
        assert!((distance - CONFUSION_WANDER_RADIUS_METERS).abs() < 0.0001);
    }

    #[test]
    fn completed_step_stops_at_its_one_or_two_step_destination() {
        let directive = ConfusionWanderDirective {
            target_x: 0.7,
            target_z: 0.0,
            yaw: std::f32::consts::FRAC_PI_2,
            standing: false,
        };
        assert_eq!(
            clamp_completed_wander_step(0.0, 0.0, 0.9, 0.0, directive),
            (0.7, 0.0)
        );
    }

    #[test]
    fn standing_decision_holds_the_current_position() {
        let directive = ConfusionWanderDirective {
            target_x: 2.0,
            target_z: 3.0,
            yaw: 0.0,
            standing: true,
        };
        assert_eq!(
            clamp_completed_wander_step(2.0, 3.0, 2.5, 3.5, directive),
            (2.0, 3.0)
        );
    }

    #[test]
    fn moving_directive_faces_its_destination() {
        let runtime = ConfusionWanderRuntime {
            target: Identity::ZERO,
            status_id: 7,
            anchor_x: 0.0,
            anchor_z: 0.0,
            target_x: 1.0,
            target_z: 0.0,
            decision_sequence: 1,
            decision_ends_at_micros: 1,
            standing: false,
        };
        let directive = directive_from_runtime(&runtime, 0.0, 0.0);
        assert!(!directive.standing);
        assert!((directive.yaw - std::f32::consts::FRAC_PI_2).abs() < 0.0001);
    }

    #[test]
    fn decisions_are_stable_but_change_with_sequence() {
        let first = stable_decision_random(Identity::ZERO, 9, 1);
        assert_eq!(first, stable_decision_random(Identity::ZERO, 9, 1));
        assert_ne!(first, stable_decision_random(Identity::ZERO, 9, 2));
    }
}

use spacetimedb::{Identity, ReducerContext};

#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

// Tick-count drift budget. Retune this alongside FIXED_TICK_MILLIS so the
// allowed wall-clock action window remains stable.
pub(crate) const MAX_ACTION_INPUT_TICK_DRIFT: u32 = 12;
pub(crate) const ACTION_POSITION_TOLERANCE_BASE: f32 = 0.75;
pub(crate) const ACTION_POSITION_TOLERANCE_PER_TICK: f32 =
    crate::movement::MOVE_SPEED * crate::movement::FIXED_TICK_SECONDS * 1.25;

#[derive(Clone, Copy, Debug)]
pub(crate) struct ActionSnapshotRequest {
    pub input_tick: u32,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub yaw: f32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) enum ActionSnapshotFallback {
    None,
    NonFinitePosition,
    InputTickOutOfRange {
        tick_gap: u32,
    },
    PositionDeltaExceeded {
        tick_gap: u32,
        position_delta: f32,
        allowed_delta: f32,
    },
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct ValidatedActionSnapshot {
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub facing_yaw: f32,
    pub last_processed_tick: u32,
    pub fallback: ActionSnapshotFallback,
}

#[derive(Clone, Copy, Debug)]
struct AuthoritativeActionSnapshot {
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    facing_yaw: f32,
    last_processed_tick: u32,
}

pub(crate) fn validate_authoritative_action_snapshot(
    ctx: &ReducerContext,
    owner: Identity,
    request: ActionSnapshotRequest,
) -> Option<ValidatedActionSnapshot> {
    let player_state = ctx.db.player_state().player_id().find(owner)?;
    if !player_state.alive {
        return None;
    }

    let physics = ctx.db.player_physics().identity().find(owner)?;
    Some(sanitize_authoritative_action_snapshot(
        AuthoritativeActionSnapshot {
            pos_x: physics.pos_x,
            pos_y: physics.pos_y,
            pos_z: physics.pos_z,
            facing_yaw: physics.yaw,
            last_processed_tick: physics.last_processed_tick,
        },
        request,
    ))
}

fn sanitize_authoritative_action_snapshot(
    authoritative: AuthoritativeActionSnapshot,
    request: ActionSnapshotRequest,
) -> ValidatedActionSnapshot {
    if !request.pos_x.is_finite() || !request.pos_y.is_finite() || !request.pos_z.is_finite() {
        return authoritative_snapshot(authoritative, ActionSnapshotFallback::NonFinitePosition);
    }

    let tick_gap = authoritative
        .last_processed_tick
        .abs_diff(request.input_tick);
    if request.input_tick == 0 || tick_gap > MAX_ACTION_INPUT_TICK_DRIFT {
        return authoritative_snapshot(
            authoritative,
            ActionSnapshotFallback::InputTickOutOfRange { tick_gap },
        );
    }

    let dx = request.pos_x - authoritative.pos_x;
    let dy = request.pos_y - authoritative.pos_y;
    let dz = request.pos_z - authoritative.pos_z;
    let position_delta = (dx * dx + dy * dy + dz * dz).sqrt();
    let allowed_delta =
        ACTION_POSITION_TOLERANCE_BASE + tick_gap as f32 * ACTION_POSITION_TOLERANCE_PER_TICK;
    if position_delta > allowed_delta {
        return authoritative_snapshot(
            authoritative,
            ActionSnapshotFallback::PositionDeltaExceeded {
                tick_gap,
                position_delta,
                allowed_delta,
            },
        );
    }

    let facing_yaw = if request.yaw.is_finite() {
        request.yaw.rem_euclid(std::f32::consts::TAU)
    } else {
        authoritative.facing_yaw
    };

    ValidatedActionSnapshot {
        pos_x: request.pos_x,
        pos_y: request.pos_y,
        pos_z: request.pos_z,
        facing_yaw,
        last_processed_tick: authoritative.last_processed_tick,
        fallback: ActionSnapshotFallback::None,
    }
}

fn authoritative_snapshot(
    authoritative: AuthoritativeActionSnapshot,
    fallback: ActionSnapshotFallback,
) -> ValidatedActionSnapshot {
    ValidatedActionSnapshot {
        pos_x: authoritative.pos_x,
        pos_y: authoritative.pos_y,
        pos_z: authoritative.pos_z,
        facing_yaw: authoritative.facing_yaw,
        last_processed_tick: authoritative.last_processed_tick,
        fallback,
    }
}

#[cfg(test)]
mod tests {
    use super::{
        sanitize_authoritative_action_snapshot, ActionSnapshotFallback, ActionSnapshotRequest,
        AuthoritativeActionSnapshot, ACTION_POSITION_TOLERANCE_BASE,
        ACTION_POSITION_TOLERANCE_PER_TICK, MAX_ACTION_INPUT_TICK_DRIFT,
    };

    fn authoritative() -> AuthoritativeActionSnapshot {
        AuthoritativeActionSnapshot {
            pos_x: 10.0,
            pos_y: 2.0,
            pos_z: -4.0,
            facing_yaw: 1.5,
            last_processed_tick: 100,
        }
    }

    #[test]
    fn accepts_in_tolerance_snapshot() {
        let validated = sanitize_authoritative_action_snapshot(
            authoritative(),
            ActionSnapshotRequest {
                input_tick: 98,
                pos_x: 10.2,
                pos_y: 2.0,
                pos_z: -3.9,
                yaw: 7.0,
            },
        );

        assert_eq!(validated.fallback, ActionSnapshotFallback::None);
        assert!((validated.pos_x - 10.2).abs() < 0.0001);
        assert!((validated.facing_yaw - 7.0_f32.rem_euclid(std::f32::consts::TAU)).abs() < 0.0001);
    }

    #[test]
    fn rejects_stale_tick_snapshot() {
        let validated = sanitize_authoritative_action_snapshot(
            authoritative(),
            ActionSnapshotRequest {
                input_tick: 100 + MAX_ACTION_INPUT_TICK_DRIFT + 1,
                pos_x: 10.0,
                pos_y: 2.0,
                pos_z: -4.0,
                yaw: 0.0,
            },
        );

        assert!(matches!(
            validated.fallback,
            ActionSnapshotFallback::InputTickOutOfRange { .. }
        ));
        assert!((validated.pos_x - 10.0).abs() < 0.0001);
    }

    #[test]
    fn rejects_excessive_position_drift() {
        let validated = sanitize_authoritative_action_snapshot(
            authoritative(),
            ActionSnapshotRequest {
                input_tick: 99,
                pos_x: 30.0,
                pos_y: 2.0,
                pos_z: -4.0,
                yaw: 0.0,
            },
        );

        assert!(matches!(
            validated.fallback,
            ActionSnapshotFallback::PositionDeltaExceeded { .. }
        ));
        assert!((validated.pos_x - 10.0).abs() < 0.0001);
    }

    #[test]
    fn tolerance_scales_with_tick_gap() {
        let tick_gap = 3;
        let allowed_delta =
            ACTION_POSITION_TOLERANCE_BASE + tick_gap as f32 * ACTION_POSITION_TOLERANCE_PER_TICK;
        let validated = sanitize_authoritative_action_snapshot(
            authoritative(),
            ActionSnapshotRequest {
                input_tick: 100 - tick_gap,
                pos_x: 10.0 + allowed_delta * 0.99,
                pos_y: 2.0,
                pos_z: -4.0,
                yaw: 0.0,
            },
        );

        assert_eq!(validated.fallback, ActionSnapshotFallback::None);
    }
}

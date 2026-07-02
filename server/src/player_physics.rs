//! Player Physics Table
//!
//! OWNERSHIP RULE: This table is written ONLY by `game_tick`.
//! No other reducer may write to position, velocity, or grounded.
//!
//! This is the authoritative physics state. Clients render from this.
//! The `send_movement_intent` reducer NEVER touches this table.

use spacetimedb::{table, Identity, ReducerContext, Timestamp};

use crate::spells::special_movement_runtime as _;

const POSITION_WRITE_EPSILON: f32 = 0.0001;

/// Authoritative player physics state.
///
/// ALL fields are owned exclusively by `game_tick`. No exceptions.
///
/// Movement model (WoW-style kinematic):
/// - position += velocity * dt
/// - No physics engine, no continuous collision
/// - Two states: grounded or airborne
/// - `grounded` is a STATE, not a query from position
///
/// INVARIANT: Only `game_tick` writes to this table.
/// INVARIANT: `grounded` changes only via explicit jump/land transitions in `game_tick`.
/// INVARIANT: Velocity changes only in `game_tick`.
#[table(accessor = player_physics, public)]
pub struct PlayerPhysics {
    #[primary_key]
    pub identity: Identity,

    // === Position (world space) ===
    pub pos_x: f32,
    pub pos_y: f32, // Vertical axis. Ground is at y=0.
    pub pos_z: f32,

    // === Velocity (units per second) ===
    // When grounded: computed from intent each tick
    // When airborne: horizontal (x,z) is LOCKED (not overwritten), vertical (y) has gravity
    pub vel_x: f32,
    pub vel_y: f32,
    pub vel_z: f32,

    // === Rotation ===
    // Yaw is copied from intent each tick (player can always rotate)
    pub yaw: f32,

    // === Movement State ===
    // This is a STATE, not a query. It changes ONLY via explicit transitions:
    // - grounded -> airborne: when jump is processed (in game_tick)
    // - airborne -> grounded: when landing condition met (in game_tick)
    //
    // LANDING CONDITION: pos_y <= GROUND_Y AND vel_y <= 0
    // Both conditions must be true. This prevents landing while ascending.
    pub grounded: bool,

    /// Latest authoritative input tick incorporated into this physics state.
    /// This is the ack boundary the local client uses for rewind/replay.
    pub last_processed_tick: u32,

    /// Last update timestamp (for client interpolation timing)
    pub updated_at: Timestamp,
}

// Note: There is NO `preserved_vel_x` or `preserved_vel_z` field.
// Horizontal velocity is "preserved" during airtime by simply not overwriting it.
// This is simpler and eliminates a class of bugs from the original design.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum PhysicsWriteMode {
    Normal,
    SpecialMovementTick,
    Force,
}

struct PhysicsCommitDecision {
    row: PlayerPhysics,
    rejected_position: bool,
}

pub(crate) fn commit_player_physics(
    ctx: &ReducerContext,
    next: PlayerPhysics,
    mode: PhysicsWriteMode,
    reason: &'static str,
) {
    let identity = next.identity;
    let Some(current) = ctx.db.player_physics().identity().find(identity) else {
        log::warn!(
            "[PLAYER_PHYSICS_COMMIT_MISSING] owner={} reason={} mode={:?}",
            identity.to_hex(),
            reason,
            mode
        );
        return;
    };

    let special_movement = ctx.db.special_movement_runtime().owner().find(identity);
    if clears_special_movement_runtime_before_commit(mode, special_movement.is_some()) {
        ctx.db.special_movement_runtime().owner().delete(identity);
    }

    let special_movement_kind = special_movement
        .as_ref()
        .map(|runtime| runtime.kind.as_str());
    let requested_x = next.pos_x;
    let requested_y = next.pos_y;
    let requested_z = next.pos_z;
    let decision = resolve_player_physics_commit(&current, next, special_movement_kind, mode);
    if decision.rejected_position {
        log::warn!(
            "[PLAYER_PHYSICS_POSITION_REJECTED] owner={} reason={} smr_kind={} current=({:.4},{:.4},{:.4}) requested=({:.4},{:.4},{:.4}) preserved=({:.4},{:.4},{:.4})",
            identity.to_hex(),
            reason,
            special_movement_kind.unwrap_or(""),
            current.pos_x,
            current.pos_y,
            current.pos_z,
            requested_x,
            requested_y,
            requested_z,
            decision.row.pos_x,
            decision.row.pos_y,
            decision.row.pos_z
        );
    }

    crate::tick_metrics::record_table_write(crate::tick_metrics::TableWriteKind::PlayerPhysics);
    ctx.db.player_physics().identity().update(decision.row);
}

fn resolve_player_physics_commit(
    current: &PlayerPhysics,
    mut next: PlayerPhysics,
    active_special_movement_kind: Option<&str>,
    mode: PhysicsWriteMode,
) -> PhysicsCommitDecision {
    let position_changed = position_component_changed(current.pos_x, next.pos_x)
        || position_component_changed(current.pos_y, next.pos_y)
        || position_component_changed(current.pos_z, next.pos_z);
    let rejected_position = mode == PhysicsWriteMode::Normal
        && active_special_movement_kind.is_some()
        && position_changed;

    if rejected_position {
        next.pos_x = current.pos_x;
        next.pos_y = current.pos_y;
        next.pos_z = current.pos_z;
    }

    PhysicsCommitDecision {
        row: next,
        rejected_position,
    }
}

fn position_component_changed(current: f32, requested: f32) -> bool {
    (requested - current).abs() > POSITION_WRITE_EPSILON
}

fn clears_special_movement_runtime_before_commit(
    mode: PhysicsWriteMode,
    has_runtime: bool,
) -> bool {
    mode == PhysicsWriteMode::Force && has_runtime
}

#[cfg(test)]
mod tests {
    use super::{
        clears_special_movement_runtime_before_commit, position_component_changed,
        resolve_player_physics_commit, PhysicsWriteMode, PlayerPhysics,
    };
    use spacetimedb::{Identity, Timestamp};
    use std::fs;
    use std::path::Path;

    const TEST_IDENTITY_HEX: &str =
        "00000000000000000000000000000000000000000000000000000000000000c1";

    fn test_identity() -> Identity {
        Identity::from_hex(TEST_IDENTITY_HEX).expect("test identity hex should be valid")
    }

    fn test_timestamp(micros: i64) -> Timestamp {
        Timestamp::from_micros_since_unix_epoch(micros)
    }

    fn physics_at(x: f32, y: f32, z: f32) -> PlayerPhysics {
        PlayerPhysics {
            identity: test_identity(),
            pos_x: x,
            pos_y: y,
            pos_z: z,
            vel_x: 0.0,
            vel_y: 0.0,
            vel_z: 0.0,
            yaw: 0.0,
            grounded: true,
            last_processed_tick: 0,
            updated_at: test_timestamp(1),
        }
    }

    #[test]
    fn normal_commit_rejects_position_change_during_special_movement() {
        let current = physics_at(1.0, 2.0, 3.0);
        let mut next = physics_at(4.0, 5.0, 6.0);
        next.vel_x = 7.0;
        next.vel_y = 8.0;
        next.vel_z = 9.0;
        next.yaw = 1.25;
        next.grounded = false;
        next.last_processed_tick = 42;
        next.updated_at = test_timestamp(2);

        let decision = resolve_player_physics_commit(
            &current,
            next,
            Some("STAGGER_SHOVE"),
            PhysicsWriteMode::Normal,
        );

        assert!(decision.rejected_position);
        assert_eq!(decision.row.pos_x, current.pos_x);
        assert_eq!(decision.row.pos_y, current.pos_y);
        assert_eq!(decision.row.pos_z, current.pos_z);
        assert_eq!(decision.row.vel_x, 7.0);
        assert_eq!(decision.row.vel_y, 8.0);
        assert_eq!(decision.row.vel_z, 9.0);
        assert_eq!(decision.row.yaw, 1.25);
        assert!(!decision.row.grounded);
        assert_eq!(decision.row.last_processed_tick, 42);
        assert_eq!(decision.row.updated_at, test_timestamp(2));
    }

    #[test]
    fn position_gate_compares_each_component_independently() {
        assert!(!position_component_changed(1.0, 1.00005));
        assert!(position_component_changed(1.0, 1.0002));

        let current = physics_at(1.0, 2.0, 3.0);
        for next in [
            physics_at(1.00011, 2.0, 3.0),
            physics_at(1.0, 2.00011, 3.0),
            physics_at(1.0, 2.0, 3.00011),
        ] {
            let decision = resolve_player_physics_commit(
                &current,
                next,
                Some("STAGGER_SHOVE"),
                PhysicsWriteMode::Normal,
            );
            assert!(decision.rejected_position);
        }
    }

    #[test]
    fn special_movement_tick_and_force_can_move_with_active_special_movement() {
        let current = physics_at(1.0, 2.0, 3.0);
        let next = physics_at(4.0, 5.0, 6.0);

        let special_decision = resolve_player_physics_commit(
            &current,
            physics_at(4.0, 5.0, 6.0),
            Some("STAGGER_SHOVE"),
            PhysicsWriteMode::SpecialMovementTick,
        );
        assert!(!special_decision.rejected_position);
        assert_eq!(special_decision.row.pos_x, 4.0);
        assert_eq!(special_decision.row.pos_y, 5.0);
        assert_eq!(special_decision.row.pos_z, 6.0);

        let force_decision = resolve_player_physics_commit(
            &current,
            next,
            Some("STAGGER_SHOVE"),
            PhysicsWriteMode::Force,
        );
        assert!(!force_decision.rejected_position);
        assert_eq!(force_decision.row.pos_x, 4.0);
        assert_eq!(force_decision.row.pos_y, 5.0);
        assert_eq!(force_decision.row.pos_z, 6.0);
    }

    #[test]
    fn normal_commit_can_move_without_active_special_movement() {
        let current = physics_at(1.0, 2.0, 3.0);
        let next = physics_at(4.0, 5.0, 6.0);

        let decision =
            resolve_player_physics_commit(&current, next, None, PhysicsWriteMode::Normal);

        assert!(!decision.rejected_position);
        assert_eq!(decision.row.pos_x, 4.0);
        assert_eq!(decision.row.pos_y, 5.0);
        assert_eq!(decision.row.pos_z, 6.0);
    }

    #[test]
    fn force_commit_clears_stale_special_movement_runtime() {
        assert!(clears_special_movement_runtime_before_commit(
            PhysicsWriteMode::Force,
            true
        ));
        assert!(!clears_special_movement_runtime_before_commit(
            PhysicsWriteMode::Force,
            false
        ));
        assert!(!clears_special_movement_runtime_before_commit(
            PhysicsWriteMode::Normal,
            true
        ));
        assert!(!clears_special_movement_runtime_before_commit(
            PhysicsWriteMode::SpecialMovementTick,
            true
        ));
    }

    #[test]
    fn player_physics_updates_go_through_commit_helper() {
        let src_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut offenders = Vec::new();
        scan_for_direct_physics_updates(&src_dir, &mut offenders);

        assert!(
            offenders.is_empty(),
            "direct player_physics identity updates must go through commit_player_physics: {offenders:?}"
        );
    }

    fn scan_for_direct_physics_updates(dir: &Path, offenders: &mut Vec<String>) {
        for entry in fs::read_dir(dir).expect("source directory should be readable") {
            let entry = entry.expect("source entry should be readable");
            let path = entry.path();
            if path.is_dir() {
                scan_for_direct_physics_updates(&path, offenders);
                continue;
            }
            if path.extension().and_then(|ext| ext.to_str()) != Some("rs") {
                continue;
            }
            if path.file_name().and_then(|name| name.to_str()) == Some("player_physics.rs") {
                continue;
            }

            let contents = fs::read_to_string(&path).expect("source file should be readable");
            if contents.contains("player_physics().identity().update") {
                offenders.push(path.display().to_string());
            }
        }
    }
}

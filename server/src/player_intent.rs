//! Player Intent Table
//!
//! OWNERSHIP RULE: `game_tick` owns hot-path updates to the currently applied
//! fallback input state. Lifecycle, teleport, and harness paths may perform
//! explicit resets.
//!
//! This table represents "what the player wants to do" - not "what the player is doing".
//! Intent is data, not action.

use spacetimedb::{table, Identity, Timestamp};

/// Retained player movement fallback - the latest applied axis/facing state.
///
/// Fields:
/// - `forward`: -1 (backward) to 1 (forward), from W/S keys
/// - `strafe`: -1 (left) to 1 (right), from A/D keys (in strafe mode)
/// - `yaw`: absolute facing direction in radians
/// - `jump`: always false here; edge-triggered jumps live in the command queue
/// - `input_tick`: authoritative input tick that last changed the retained fallback state
///
/// INVARIANT: This row represents the currently applied fallback intent used if
/// the next input tick has not arrived yet.
#[table(accessor = player_intent, public)]
pub struct PlayerIntent {
    #[primary_key]
    pub identity: Identity,

    /// Forward/backward input: -1.0 (S key) to 1.0 (W key)
    /// Clamped on receipt.
    pub forward: f32,

    /// Strafe input: -1.0 (A key) to 1.0 (D key)
    /// Only active when right mouse button held (WoW style).
    pub strafe: f32,

    /// Facing direction in radians (0 = +Z, increases counter-clockwise)
    pub yaw: f32,

    /// Last applied jump edge. This remains false in the persistent fallback
    /// row; jump edges live in the queued command stream.
    pub jump: bool,

    /// Authoritative input tick that last changed this retained fallback state.
    /// The every-tick simulation acknowledgement lives on `PlayerPhysics`.
    pub input_tick: u32,

    /// When this retained fallback state last changed (for debugging)
    pub updated_at: Timestamp,
}

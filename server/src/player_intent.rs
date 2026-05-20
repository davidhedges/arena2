//! Player Intent Table
//!
//! OWNERSHIP RULE: This table is written by `game_tick` to reflect the
//! currently applied fallback input state for each player.
//!
//! This table represents "what the player wants to do" - not "what the player is doing".
//! Intent is data, not action.

use spacetimedb::{table, Identity, Timestamp};

/// Player movement intent - the raw input from the client.
///
/// Fields:
/// - `forward`: -1 (backward) to 1 (forward), from W/S keys
/// - `strafe`: -1 (left) to 1 (right), from A/D keys (in strafe mode)
/// - `yaw`: absolute facing direction in radians
/// - `jump`: true if jump was requested THIS FRAME (edge-triggered, not held)
/// - `input_tick`: latest authoritative input tick applied to the movement loop
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

    /// Latest authoritative input tick applied to this fallback state.
    pub input_tick: u32,

    /// When this intent was last updated (for debugging)
    pub updated_at: Timestamp,
}

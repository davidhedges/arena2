use spacetimedb::{reducer, ReducerContext};

use crate::combat::position_history::record_standing_view_delay;

/// RTT probe (feel audit F2b) + standing view-delay report (S9). The client
/// calls this on a slow cadence (~2 s), echoes its send time through
/// `client_send_ms`, and reads the authoritative timestamp off the reducer
/// event to feed `ArenaServerClock.RecordReducerSampleMs`.
///
/// `view_server_time_ms` (S9, E1): while an auto-attack target is armed the
/// client reports the server-time it is rendering that target at; 0 means
/// "no report" — byte-for-byte the pre-S9 behavior, and the value every
/// non-reporting caller passes. A nonzero report writes one small private row
/// per reporting player per ~2 s: private rows never reach subscribers, so
/// pings still cause no replication fan-out to other clients, and the write
/// volume is negligible against 30 Hz physics commits.
#[reducer]
pub fn ping_clock(ctx: &ReducerContext, _client_send_ms: u64, view_server_time_ms: u64) {
    record_standing_view_delay(ctx, ctx.sender(), view_server_time_ms);
}

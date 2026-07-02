use spacetimedb::{reducer, ReducerContext};

/// No-op RTT probe (feel audit F2b). The client calls this on a slow cadence
/// (~2 s), echoes its send time through `client_send_ms`, and reads the
/// authoritative timestamp off the reducer event to feed
/// `ArenaServerClock.RecordReducerSampleMs`. Writes no rows on purpose: only
/// the caller receives the transaction update, so pings cause no replication
/// fan-out to other clients.
#[reducer]
pub fn ping_clock(_ctx: &ReducerContext, _client_send_ms: u64) {}

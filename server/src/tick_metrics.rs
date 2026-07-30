//! Log-only tick instrumentation counters and the `ARENA_PROFILE_TICKS` gate.
//!
//! Everything here is reported once per 5s profile window in
//! `[TICK_PROFILE_SCAN]` / `[TICK_PROFILE]` log lines. Nothing is replicated
//! and nothing changes behavior — this is the shared measurement foundation
//! for the netcode-R3 / server-tick §2 audit recommendations.
//!
//! WASM constraints (the module targets `wasm32-unknown-unknown`):
//! - Process env vars do not exist at module runtime (`std::env::var_os` is
//!   always `None`), so the gate is baked at **compile time**:
//!   `ARENA_PROFILE_TICKS=1 spacetime build` / `ops/republish-local-clear.sh`.
//!   The runtime env check remains as a fallback for native builds (tests).
//! - `std::time::Instant` panics on this target, so in-module wall-clock
//!   timing goes through `ScopeTimer` (zero on wasm) plus host-side
//!   `LogStopwatch` console timers sampled on one tick per profile window.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::OnceLock;

pub(crate) const TICK_PROFILE_ENV_VAR: &str = "ARENA_PROFILE_TICKS";

/// True when `std::time::Instant` works, i.e. native builds (unit tests).
/// In the deployed wasm module, percentile timings are zero and wall-clock
/// comes from the sampled `LogStopwatch` console timers instead.
pub(crate) const WALL_CLOCK_AVAILABLE: bool = cfg!(not(target_arch = "wasm32"));

fn env_value_enables(raw: &str) -> Option<bool> {
    match raw.trim().to_ascii_lowercase().as_str() {
        "0" | "false" | "off" | "no" => Some(false),
        "1" | "true" | "on" | "yes" => Some(true),
        _ => None,
    }
}

pub(crate) fn profiling_enabled() -> bool {
    static ENABLED: OnceLock<bool> = OnceLock::new();
    *ENABLED.get_or_init(|| {
        // Compile-time bake: the only path that works inside the wasm module.
        if let Some(raw) = option_env!("ARENA_PROFILE_TICKS") {
            if let Some(enabled) = env_value_enables(raw) {
                return enabled;
            }
            log::warn!(
                "[INIT] Invalid compile-time {} value {:?}; defaulting to disabled",
                TICK_PROFILE_ENV_VAR,
                raw
            );
            return false;
        }

        // Native fallback (tests, tooling) — never reachable in the module.
        #[cfg(not(target_arch = "wasm32"))]
        if let Some(raw) = std::env::var_os(TICK_PROFILE_ENV_VAR) {
            return env_value_enables(&raw.to_string_lossy()).unwrap_or(false);
        }

        false
    })
}

/// Micro-timer that is a no-op on wasm (where `Instant::now` panics).
pub(crate) struct ScopeTimer {
    #[cfg(not(target_arch = "wasm32"))]
    started_at: Option<std::time::Instant>,
}

impl ScopeTimer {
    pub(crate) fn start(enabled: bool) -> Self {
        #[cfg(not(target_arch = "wasm32"))]
        {
            Self {
                started_at: enabled.then(std::time::Instant::now),
            }
        }
        #[cfg(target_arch = "wasm32")]
        {
            let _ = enabled;
            Self {}
        }
    }

    pub(crate) fn elapsed_micros(&self) -> u32 {
        #[cfg(not(target_arch = "wasm32"))]
        {
            self.started_at
                .map(|started_at| started_at.elapsed().as_micros().min(u32::MAX as u128) as u32)
                .unwrap_or(0)
        }
        #[cfg(target_arch = "wasm32")]
        {
            0
        }
    }
}

// One tick per profile window is timed with host-side console timers so the
// wasm module still produces real wall-clock samples in `spacetime logs`.
static STOPWATCH_SAMPLE_REQUESTED: AtomicBool = AtomicBool::new(false);

pub(crate) fn request_stopwatch_sample() {
    STOPWATCH_SAMPLE_REQUESTED.store(true, Ordering::Relaxed);
}

pub(crate) fn take_stopwatch_sample_request() -> bool {
    STOPWATCH_SAMPLE_REQUESTED.swap(false, Ordering::Relaxed)
}

/// Host-logged wall-clock timer (`console_timer_start`/`end`); logs elapsed on
/// drop. Only exists in the wasm module — a no-op in native builds, where
/// `ScopeTimer` provides real percentile timings instead.
pub(crate) struct PhaseStopwatch {
    #[cfg(target_arch = "wasm32")]
    _inner: Option<spacetimedb::log_stopwatch::LogStopwatch>,
}

impl PhaseStopwatch {
    pub(crate) fn start(active: bool, name: &str) -> Self {
        #[cfg(target_arch = "wasm32")]
        {
            Self {
                _inner: active.then(|| spacetimedb::log_stopwatch::LogStopwatch::new(name)),
            }
        }
        #[cfg(not(target_arch = "wasm32"))]
        {
            let _ = (active, name);
            Self {}
        }
    }
}

pub(crate) const TABLE_WRITE_KIND_COUNT: usize = 8;

/// Hot-tick tables whose write sites are individually instrumented.
#[derive(Clone, Copy, Debug)]
pub(crate) enum TableWriteKind {
    PlayerPhysics = 0,
    PlayerIntent = 1,
    PlayerResource = 2,
    FixedActionChargeState = 3,
    FixedActionChargeRecovery = 4,
    NpcCombatRuntime = 5,
    CombatStackingPassiveRuntime = 6,
    CombatPositionHistory = 7,
}

pub(crate) const TABLE_WRITE_KIND_NAMES: [&str; TABLE_WRITE_KIND_COUNT] = [
    "player_physics",
    "player_intent",
    "player_resource",
    "fixed_action_charge_state",
    "fixed_action_charge_recovery",
    "npc_combat_runtime",
    "combat_stacking_passive_runtime",
    "combat_position_history",
];

static STATUS_COLLECT_COUNT: AtomicU64 = AtomicU64::new(0);
static STATUS_COLLECT_MICROS: AtomicU64 = AtomicU64::new(0);
static STATUS_COLLECT_ROWS: AtomicU64 = AtomicU64::new(0);
static EQUIPMENT_SCAN_COUNT: AtomicU64 = AtomicU64::new(0);
static MOVE_FALLBACK_COUNT: AtomicU64 = AtomicU64::new(0);
static NPC_TARGET_PAIRS_SCANNED: AtomicU64 = AtomicU64::new(0);
static TABLE_WRITE_COUNTS: [AtomicU64; TABLE_WRITE_KIND_COUNT] = [
    AtomicU64::new(0),
    AtomicU64::new(0),
    AtomicU64::new(0),
    AtomicU64::new(0),
    AtomicU64::new(0),
    AtomicU64::new(0),
    AtomicU64::new(0),
    AtomicU64::new(0),
];

pub(crate) fn record_status_collect(micros: u64, rows_scanned: u64) {
    STATUS_COLLECT_COUNT.fetch_add(1, Ordering::Relaxed);
    STATUS_COLLECT_MICROS.fetch_add(micros, Ordering::Relaxed);
    STATUS_COLLECT_ROWS.fetch_add(rows_scanned, Ordering::Relaxed);
}

pub(crate) fn record_equipment_modifier_scan() {
    if !profiling_enabled() {
        return;
    }
    EQUIPMENT_SCAN_COUNT.fetch_add(1, Ordering::Relaxed);
}

pub(crate) fn record_move_fallback() {
    if !profiling_enabled() {
        return;
    }
    MOVE_FALLBACK_COUNT.fetch_add(1, Ordering::Relaxed);
}

pub(crate) fn record_npc_target_pairs_scanned(pairs: u64) {
    if !profiling_enabled() {
        return;
    }
    NPC_TARGET_PAIRS_SCANNED.fetch_add(pairs, Ordering::Relaxed);
}

pub(crate) fn record_table_write(kind: TableWriteKind) {
    if !profiling_enabled() {
        return;
    }
    TABLE_WRITE_COUNTS[kind as usize].fetch_add(1, Ordering::Relaxed);
}

pub(crate) struct TickScanWindowSnapshot {
    pub status_collect_count: u64,
    pub status_collect_micros: u64,
    pub status_collect_rows: u64,
    pub equipment_scan_count: u64,
    pub move_fallback_count: u64,
    pub npc_target_pairs_scanned: u64,
    pub table_writes: [u64; TABLE_WRITE_KIND_COUNT],
}

pub(crate) fn snapshot_and_reset() -> TickScanWindowSnapshot {
    let mut table_writes = [0u64; TABLE_WRITE_KIND_COUNT];
    for (slot, counter) in table_writes.iter_mut().zip(TABLE_WRITE_COUNTS.iter()) {
        *slot = counter.swap(0, Ordering::Relaxed);
    }

    TickScanWindowSnapshot {
        status_collect_count: STATUS_COLLECT_COUNT.swap(0, Ordering::Relaxed),
        status_collect_micros: STATUS_COLLECT_MICROS.swap(0, Ordering::Relaxed),
        status_collect_rows: STATUS_COLLECT_ROWS.swap(0, Ordering::Relaxed),
        equipment_scan_count: EQUIPMENT_SCAN_COUNT.swap(0, Ordering::Relaxed),
        move_fallback_count: MOVE_FALLBACK_COUNT.swap(0, Ordering::Relaxed),
        npc_target_pairs_scanned: NPC_TARGET_PAIRS_SCANNED.swap(0, Ordering::Relaxed),
        table_writes,
    }
}

//! S8 — bounded attacker-view rewind (docs/lag-compensation-design-2026-07-04.md).
//!
//! A short positional history ring per combat-relevant entity lets player
//! attacks validate reach/facing/LOS against the target pose the attacker was
//! actually rendering. The rewind decides whether an attack connects; the
//! present decides everything about how it resolves — health, status, defense
//! state, damage, and emitted event positions never rewind.
//!
//! OWNERSHIP RULE: history samples are written only from the authoritative
//! physics write sites (`commit_player_physics`, the `NpcPhysics` commit
//! sites); barriers are stamped only by discontinuity writes (non-Normal
//! physics commits). Validation code only reads.

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::combat::actor_snapshot::CombatActorSnapshot;
use crate::combat::timestamp_to_micros;
use crate::relations::can_harm;

#[allow(unused_imports)]
use crate::spells::special_movement_runtime as _;

/// D3 (owner-signed): worst accepted claim is "validate against a 250 ms-old
/// world" — the S7 delay-budget cap (200 ms) plus uplink and clock error.
pub(crate) const DEFAULT_MAX_REWIND_MS: u64 = 250;
/// 16 slots at 33 ms ≈ 528 ms of history — the 250 ms cap plus margin.
const HISTORY_SLOTS_PER_ENTITY: usize = 16;
/// Matches POSITION_WRITE_EPSILON in player_physics.rs: samples below this
/// motion are skipped and the newest-≤ lookup treats the gap as "at rest".
const HISTORY_POSITION_EPSILON: f32 = 0.0001;
const HISTORY_YAW_EPSILON: f32 = 0.0001;

/// One authored pose sample. Private: history is server-validation state.
#[table(accessor = combat_position_history)]
#[derive(Clone)]
pub struct CombatPositionHistory {
    #[primary_key]
    #[auto_inc]
    pub sample_id: u64,

    #[index(btree)]
    pub identity: Identity,

    pub stamped_at_micros: i64,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub yaw: f32,
}

/// Rewind barrier: the time of the entity's last positional discontinuity
/// (special-movement tick, teleport, respawn, resync). Rewinds never cross it.
#[table(accessor = combat_rewind_barrier)]
pub struct CombatRewindBarrier {
    #[primary_key]
    pub identity: Identity,
    pub barrier_micros: i64,
}

/// Runtime kill switch + tunable (S7 lesson: A/B legs must flip without a
/// republish). History writing and audit logging stay on regardless — the
/// switch gates only whether rewound verdicts are *used*. `auto_swing_enabled`
/// (S9) additionally gates the auto-attack tick rewind under the master
/// switch, so the S8 kill switch stays one command and the A/B can flip S9
/// alone.
#[table(accessor = combat_lag_comp_config)]
pub struct CombatLagCompConfig {
    #[primary_key]
    pub config_id: u8,
    pub enabled: bool,
    pub max_rewind_ms: u64,
    pub auto_swing_enabled: bool,
    /// S10 (docs/sweep-projectile-rewind-design-2026-07-05.md): additionally
    /// gates per-victim rewind of cone/radius sweep membership, under the
    /// master `enabled` flag — so the S8 kill switch stays one command and the
    /// A/B can flip S10 alone. Projectile impacts stay present-time (G1).
    pub sweep_rewind_enabled: bool,
}

/// The press's clamped view delay, stamped with the press transaction's
/// timestamp. Validation sites apply the rewind only when the stamp matches
/// `ctx.timestamp`, so scheduled completions, queued combo releases, and
/// channel sustains stay present-time without threading a parameter through
/// every signature. S9: the auto-attack tick stamps this row itself (from the
/// standing signal, `signal = "standing"`), which makes a server-initiated
/// due swing indistinguishable from a pressed one to every downstream
/// consumer — one stamp, one pose, one timeline.
#[table(accessor = combat_press_view_delay)]
pub struct CombatPressViewDelay {
    #[primary_key]
    pub identity: Identity,
    pub stamped_at_micros: i64,
    pub view_delay_micros: i64,
    pub reported_view_ms: u64,
    pub clamped_to_max: bool,
    pub signal: String,
}

/// S9 standing view-delay signal: refreshed by `ping_clock` (~2 s cadence)
/// while the client has an auto-attack target armed, using that target's
/// presentation-buffer delay (E1). Substrate for server-initiated swings,
/// which have no press to carry a view time. Private: one small row per
/// reporting player, no replication fan-out.
#[table(accessor = combat_standing_view_delay)]
pub struct CombatStandingViewDelay {
    #[primary_key]
    pub identity: Identity,
    pub updated_at_micros: i64,
    pub view_delay_micros: i64,
    pub reported_view_ms: u64,
    pub clamped_to_max: bool,
}

/// E2: a standing report older than three missed pings is ignored — the
/// client stopped reporting, degraded, or disconnected. Stale → present-time,
/// never a reject.
const STANDING_VIEW_DELAY_TTL_MICROS: i64 = 6_000_000;

pub(crate) const VIEW_DELAY_SIGNAL_PRESS: &str = "press";
pub(crate) const VIEW_DELAY_SIGNAL_STANDING: &str = "standing";

#[reducer]
pub fn set_lag_comp_config(
    ctx: &ReducerContext,
    enabled: bool,
    max_rewind_ms: u64,
    auto_swing_enabled: bool,
    sweep_rewind_enabled: bool,
) -> Result<(), String> {
    let row = CombatLagCompConfig {
        config_id: 0,
        enabled,
        max_rewind_ms: max_rewind_ms.min(1_000),
        auto_swing_enabled,
        sweep_rewind_enabled,
    };
    if ctx
        .db
        .combat_lag_comp_config()
        .config_id()
        .find(0)
        .is_some()
    {
        ctx.db.combat_lag_comp_config().config_id().update(row);
    } else {
        ctx.db.combat_lag_comp_config().insert(row);
    }
    log::info!(
        "[LAG_COMP] config enabled={} max_rewind_ms={} auto_swing_enabled={} sweep_rewind_enabled={}",
        enabled,
        max_rewind_ms.min(1_000),
        auto_swing_enabled,
        sweep_rewind_enabled
    );
    Ok(())
}

pub(crate) fn lag_comp_config(ctx: &ReducerContext) -> (bool, u64) {
    match ctx.db.combat_lag_comp_config().config_id().find(0) {
        Some(row) => (row.enabled, row.max_rewind_ms),
        // Default ON since the 2026-07-04 acceptance (S7-precedent gate:
        // owner shaped leg PASS — no feel regression, wiring verified live).
        // set_lag_comp_config(false, 250, false) is the kill switch.
        None => (true, DEFAULT_MAX_REWIND_MS),
    }
}

/// S9 gate (E4): the auto-attack tick rewind activates only when this AND the
/// master `enabled` flag are on. Default ON since the 2026-07-05 acceptance
/// (owner shaped A/B PASS — S9 controlled 78.5% of edge-swing reach verdicts
/// with no feel regression). `set_lag_comp_config(true, 250, false)` disables
/// S9 alone; `set_lag_comp_config(false, 250, false)` is the S8 master kill.
pub(crate) fn lag_comp_auto_swing_enabled(ctx: &ReducerContext) -> bool {
    ctx.db
        .combat_lag_comp_config()
        .config_id()
        .find(0)
        .map(|row| row.auto_swing_enabled)
        .unwrap_or(true)
}

/// S10 gate (G5): per-victim sweep-membership rewind activates only when this
/// AND the master `enabled` flag are on. **Default ON since 2026-07-05** — owner
/// call to ship it live now on the strength of the historical server-half live
/// probe PASS; that pre-cutover harness was retired during the combat-build
/// cutover. The shaped +40/+40 A/B is deferred as an
/// optional spot-check rather than a gate (see `docs/netcode-open-items.md`).
/// `set_lag_comp_config(true, 250, true, false)` disables S10 alone; the S8
/// master kill switch (`false, …`) is unchanged.
pub(crate) fn lag_comp_sweep_rewind_enabled(ctx: &ReducerContext) -> bool {
    ctx.db
        .combat_lag_comp_config()
        .config_id()
        .find(0)
        .map(|row| row.sweep_rewind_enabled)
        .unwrap_or(true)
}

/// Record an authoritative pose sample. Skips unchanged poses, so idle
/// entities cost nothing and lookup gaps mean "at rest at the last sample".
pub(crate) fn record_position_sample(
    ctx: &ReducerContext,
    identity: Identity,
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    yaw: f32,
    now: Timestamp,
) {
    let mut count = 0usize;
    let mut newest: Option<CombatPositionHistory> = None;
    let mut oldest: Option<CombatPositionHistory> = None;
    for sample in ctx.db.combat_position_history().identity().filter(identity) {
        count += 1;
        if newest
            .as_ref()
            .map(|row| sample.stamped_at_micros > row.stamped_at_micros)
            .unwrap_or(true)
        {
            newest = Some(sample.clone());
        }
        if oldest
            .as_ref()
            .map(|row| sample.stamped_at_micros < row.stamped_at_micros)
            .unwrap_or(true)
        {
            oldest = Some(sample);
        }
    }

    if let Some(newest) = newest.as_ref() {
        let unchanged = (newest.pos_x - pos_x).abs() <= HISTORY_POSITION_EPSILON
            && (newest.pos_y - pos_y).abs() <= HISTORY_POSITION_EPSILON
            && (newest.pos_z - pos_z).abs() <= HISTORY_POSITION_EPSILON
            && (newest.yaw - yaw).abs() <= HISTORY_YAW_EPSILON;
        if unchanged {
            return;
        }
    }

    crate::tick_metrics::record_table_write(
        crate::tick_metrics::TableWriteKind::CombatPositionHistory,
    );
    let now_micros = timestamp_to_micros(now);
    if count < HISTORY_SLOTS_PER_ENTITY {
        ctx.db
            .combat_position_history()
            .insert(CombatPositionHistory {
                sample_id: 0,
                identity,
                stamped_at_micros: now_micros,
                pos_x,
                pos_y,
                pos_z,
                yaw,
            });
        return;
    }

    let Some(mut slot) = oldest else {
        return;
    };
    slot.stamped_at_micros = now_micros;
    slot.pos_x = pos_x;
    slot.pos_y = pos_y;
    slot.pos_z = pos_z;
    slot.yaw = yaw;
    ctx.db.combat_position_history().sample_id().update(slot);
}

/// Stamp a positional discontinuity (§2.4 rule 3). Rewinds clamp to this.
pub(crate) fn stamp_rewind_barrier(ctx: &ReducerContext, identity: Identity, now: Timestamp) {
    let row = CombatRewindBarrier {
        identity,
        barrier_micros: timestamp_to_micros(now),
    };
    if ctx
        .db
        .combat_rewind_barrier()
        .identity()
        .find(identity)
        .is_some()
    {
        ctx.db.combat_rewind_barrier().identity().update(row);
    } else {
        ctx.db.combat_rewind_barrier().insert(row);
    }
}

/// Drop everything the ring knows about a despawned/removed entity.
pub(crate) fn clear_position_history(ctx: &ReducerContext, identity: Identity) {
    ctx.db.combat_position_history().identity().delete(identity);
    ctx.db.combat_rewind_barrier().identity().delete(identity);
    ctx.db.combat_press_view_delay().identity().delete(identity);
    ctx.db
        .combat_standing_view_delay()
        .identity()
        .delete(identity);
}

/// Record the press's view-time report for this transaction. Called at the
/// top of press reducers (melee_attack, cast_request); `view_server_time_ms`
/// of 0 means "no report" and clears any stale context.
pub(crate) fn record_press_view_delay(
    ctx: &ReducerContext,
    caster: Identity,
    view_server_time_ms: u64,
) {
    if view_server_time_ms == 0 {
        ctx.db.combat_press_view_delay().identity().delete(caster);
        return;
    }

    let now_micros = timestamp_to_micros(ctx.timestamp);
    let now_ms = now_micros / 1_000;
    let (_, max_rewind_ms) = lag_comp_config(ctx);
    let raw_delay_ms = now_ms - view_server_time_ms.min(i64::MAX as u64) as i64;
    let clamped_to_max = raw_delay_ms > max_rewind_ms as i64;
    let delay_ms = raw_delay_ms.clamp(0, max_rewind_ms as i64);

    let row = CombatPressViewDelay {
        identity: caster,
        stamped_at_micros: now_micros,
        view_delay_micros: delay_ms * 1_000,
        reported_view_ms: view_server_time_ms,
        clamped_to_max,
        signal: VIEW_DELAY_SIGNAL_PRESS.to_string(),
    };
    if ctx
        .db
        .combat_press_view_delay()
        .identity()
        .find(caster)
        .is_some()
    {
        ctx.db.combat_press_view_delay().identity().update(row);
    } else {
        ctx.db.combat_press_view_delay().insert(row);
    }
}

/// Record the standing view-delay report carried on `ping_clock` (S9 §2.1).
/// 0 means "no report" and deletes any standing row — byte-for-byte the
/// pre-S9 behavior for non-reporting callers. Nonzero claims are sanity- and
/// max-rewind-clamped exactly like press claims.
pub(crate) fn record_standing_view_delay(
    ctx: &ReducerContext,
    identity: Identity,
    view_server_time_ms: u64,
) {
    if view_server_time_ms == 0 {
        ctx.db
            .combat_standing_view_delay()
            .identity()
            .delete(identity);
        return;
    }

    let now_micros = timestamp_to_micros(ctx.timestamp);
    let now_ms = now_micros / 1_000;
    let (_, max_rewind_ms) = lag_comp_config(ctx);
    let raw_delay_ms = now_ms - view_server_time_ms.min(i64::MAX as u64) as i64;
    let clamped_to_max = raw_delay_ms > max_rewind_ms as i64;
    let delay_ms = raw_delay_ms.clamp(0, max_rewind_ms as i64);

    let row = CombatStandingViewDelay {
        identity,
        updated_at_micros: now_micros,
        view_delay_micros: delay_ms * 1_000,
        reported_view_ms: view_server_time_ms,
        clamped_to_max,
    };
    if ctx
        .db
        .combat_standing_view_delay()
        .identity()
        .find(identity)
        .is_some()
    {
        ctx.db.combat_standing_view_delay().identity().update(row);
    } else {
        ctx.db.combat_standing_view_delay().insert(row);
    }
}

/// The owner's standing view-delay row, if fresh (E2). Stale or absent →
/// `None`, and the caller degrades to present-time — never a reject.
pub(crate) fn fresh_standing_view_delay(
    ctx: &ReducerContext,
    identity: Identity,
) -> Option<CombatStandingViewDelay> {
    let row = ctx
        .db
        .combat_standing_view_delay()
        .identity()
        .find(identity)?;
    let now_micros = timestamp_to_micros(ctx.timestamp);
    if now_micros - row.updated_at_micros > STANDING_VIEW_DELAY_TTL_MICROS {
        return None;
    }
    if row.view_delay_micros <= 0 {
        return None;
    }
    Some(row)
}

/// S9 §2.2 step 1: stamp the press context from the standing signal, in the
/// tick's transaction. Downstream (chain gate, impact freeze, D2 re-check)
/// cannot distinguish this from a pressed claim — the one-timeline rule (E3)
/// holds by construction.
pub(crate) fn stamp_standing_press_context(
    ctx: &ReducerContext,
    identity: Identity,
    standing: &CombatStandingViewDelay,
) {
    let row = CombatPressViewDelay {
        identity,
        stamped_at_micros: timestamp_to_micros(ctx.timestamp),
        view_delay_micros: standing.view_delay_micros,
        reported_view_ms: standing.reported_view_ms,
        clamped_to_max: standing.clamped_to_max,
        signal: VIEW_DELAY_SIGNAL_STANDING.to_string(),
    };
    if ctx
        .db
        .combat_press_view_delay()
        .identity()
        .find(identity)
        .is_some()
    {
        ctx.db.combat_press_view_delay().identity().update(row);
    } else {
        ctx.db.combat_press_view_delay().insert(row);
    }
}

/// Audit label for [LAG_COMP] lines: the signal kind of the caster's last
/// stamped view-delay context ("press" when none exists). Exact inside the
/// stamping transaction; at the impact re-check it is the last stamp, which
/// can only mislabel if a different-signal claim landed between dispatch and
/// impact — acceptable for an audit-only field.
pub(crate) fn view_delay_signal_label(ctx: &ReducerContext, caster: Identity) -> String {
    ctx.db
        .combat_press_view_delay()
        .identity()
        .find(caster)
        .map(|row| row.signal)
        .unwrap_or_else(|| VIEW_DELAY_SIGNAL_PRESS.to_string())
}

/// The view delay this press claimed, if the claim was made in the current
/// transaction. 0 outside the press transaction — completions, sustains,
/// queued releases, and server-initiated swings validate present-time.
pub(crate) fn press_view_delay_micros(ctx: &ReducerContext, caster: Identity) -> i64 {
    let Some(row) = ctx.db.combat_press_view_delay().identity().find(caster) else {
        return 0;
    };
    if row.stamped_at_micros != timestamp_to_micros(ctx.timestamp) {
        return 0;
    }
    row.view_delay_micros
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum RewoundPoseSource {
    Present,
    History,
    HistoryOldestClamp,
    BarrierClamp,
    ActiveSpecialMovement,
}

impl RewoundPoseSource {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::Present => "present",
            Self::History => "history",
            Self::HistoryOldestClamp => "oldest_clamp",
            Self::BarrierClamp => "barrier_clamp",
            Self::ActiveSpecialMovement => "active_sm",
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct RewoundPose {
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub yaw: f32,
    pub rewound_by_micros: i64,
    pub source: RewoundPoseSource,
}

/// Resolve the target pose at `now − view_delay_micros`, honoring the §2.4
/// never-rewind rules. Degrades to the present pose, never errors.
pub(crate) fn rewound_pose_for(
    ctx: &ReducerContext,
    target: Identity,
    view_delay_micros: i64,
    now: Timestamp,
    present: &CombatActorSnapshot,
) -> RewoundPose {
    let present_pose = |source: RewoundPoseSource| RewoundPose {
        pos_x: present.pos_x,
        pos_y: present.pos_y,
        pos_z: present.pos_z,
        yaw: present.facing_yaw,
        rewound_by_micros: 0,
        source,
    };

    if view_delay_micros <= 0 {
        return present_pose(RewoundPoseSource::Present);
    }

    // Rule 3: a discontinuity in progress means there is no honest past pose.
    if ctx
        .db
        .special_movement_runtime()
        .owner()
        .find(target)
        .is_some()
    {
        return present_pose(RewoundPoseSource::ActiveSpecialMovement);
    }

    let now_micros = timestamp_to_micros(now);
    let mut effective_micros = now_micros - view_delay_micros;
    let mut source = RewoundPoseSource::History;
    if let Some(barrier) = ctx.db.combat_rewind_barrier().identity().find(target) {
        if barrier.barrier_micros > effective_micros {
            effective_micros = barrier.barrier_micros;
            source = RewoundPoseSource::BarrierClamp;
        }
    }

    let mut best: Option<CombatPositionHistory> = None;
    let mut oldest: Option<CombatPositionHistory> = None;
    for sample in ctx.db.combat_position_history().identity().filter(target) {
        if oldest
            .as_ref()
            .map(|row| sample.stamped_at_micros < row.stamped_at_micros)
            .unwrap_or(true)
        {
            oldest = Some(sample.clone());
        }
        if sample.stamped_at_micros <= effective_micros
            && best
                .as_ref()
                .map(|row| sample.stamped_at_micros > row.stamped_at_micros)
                .unwrap_or(true)
        {
            best = Some(sample);
        }
    }

    let (sample, source) = match best {
        Some(sample) => (sample, source),
        // Deep rewind into a shallow ring: the oldest sample is the closest
        // honest pose. No samples at all degrades to present.
        None => match oldest {
            Some(sample) => (sample, RewoundPoseSource::HistoryOldestClamp),
            None => return present_pose(RewoundPoseSource::Present),
        },
    };

    RewoundPose {
        pos_x: sample.pos_x,
        pos_y: sample.pos_y,
        pos_z: sample.pos_z,
        yaw: sample.yaw,
        rewound_by_micros: now_micros - sample.stamped_at_micros,
        source,
    }
}

/// S10 §2.3 candidate-disc widening: sized on a generous strafe speed so a
/// victim who left the sweep shape during the (≤ max_rewind_ms) view delay is
/// still rewound-tested. MOVE_SPEED is 7 m/s; 12 leaves haste headroom. Dodges
/// move faster but stamp a rewind barrier and validate present-time anyway.
/// Shared by the spell-area and melee caster-cone/radius sweep loops.
pub(crate) const SWEEP_REWIND_MARGIN_SPEED_MPS: f32 = 12.0;

/// S10 per-victim sweep rewind (docs/sweep-projectile-rewind-design-2026-07-05.md).
/// Given a candidate victim's present snapshot, the press's frozen view delay,
/// and an area-membership predicate, returns whether the victim is a member —
/// the rewound verdict when `sweep_rewind_on`, the present verdict otherwise —
/// and logs the `[LAG_COMP] sweep_hit` dual-verdict line whenever a report is
/// present (both switch states, so the A/B flip rate works with the switch
/// OFF). Only the membership *position* is rewound; vitality, audience,
/// defense, damage, and emitted event positions all stay present. Shared by the
/// spell-area (`resolve_area_impact`) and melee caster-cone/radius
/// (`resolve_pending_melee_hit_volume`) loops so both cone kinds rewind
/// identically. `strike` labels the ability in the audit line.
pub(crate) fn sweep_rewind_membership(
    ctx: &ReducerContext,
    caster: Identity,
    player: &CombatActorSnapshot,
    view_delay_micros: i64,
    now: Timestamp,
    sweep_rewind_on: bool,
    strike: &str,
    mut is_member: impl FnMut(&CombatActorSnapshot) -> bool,
) -> bool {
    let present_in = is_member(player);
    if view_delay_micros <= 0 {
        return present_in;
    }
    let pose = rewound_pose_for(ctx, player.player_id, view_delay_micros, now, player);
    let rewound_in = if pose.rewound_by_micros > 0 {
        let mut overlaid = *player;
        overlaid.pos_x = pose.pos_x;
        overlaid.pos_y = pose.pos_y;
        overlaid.pos_z = pose.pos_z;
        overlaid.facing_yaw = pose.yaw;
        is_member(&overlaid)
    } else {
        present_in
    };
    let flip = rewound_in != present_in;
    let used = if sweep_rewind_on {
        rewound_in
    } else {
        present_in
    };
    let line = format!(
        "[LAG_COMP] sweep_hit caster={} target={} strike={} rewound_ms={} source={} enabled={} present={} rewound={} flip={} signal={}",
        &caster.to_hex()[..8],
        &player.player_id.to_hex()[..8],
        strike,
        pose.rewound_by_micros / 1_000,
        pose.source.as_str(),
        sweep_rewind_on,
        if present_in { "in" } else { "out" },
        if rewound_in { "in" } else { "out" },
        flip,
        VIEW_DELAY_SIGNAL_PRESS,
    );
    // S9 log-volume rule: info when the line carries audit signal (a flip or a
    // used inclusion); debug when present and rewound agree and nothing is hit.
    if flip || used {
        log::info!("{}", line);
    } else {
        log::debug!("{}", line);
    }
    used
}

/// Press-transaction overlay for targeted spell validation: returns the
/// target snapshot with its pose swapped for the attacker-view pose when the
/// switch is ON, the press claimed a view time in this transaction, and the
/// target is hostile. Vitality/status fields always stay present.
pub(crate) fn overlay_press_rewound_target_pose(
    ctx: &ReducerContext,
    caster: Identity,
    target: CombatActorSnapshot,
) -> CombatActorSnapshot {
    let (enabled, _) = lag_comp_config(ctx);
    if !enabled {
        return target;
    }
    let view_delay_micros = press_view_delay_micros(ctx, caster);
    if view_delay_micros <= 0 {
        return target;
    }
    if !can_harm(ctx, caster, target.player_id) {
        return target;
    }

    let pose = rewound_pose_for(
        ctx,
        target.player_id,
        view_delay_micros,
        ctx.timestamp,
        &target,
    );
    if pose.rewound_by_micros > 0 {
        log::info!(
            "[LAG_COMP] overlay caster={} target={} rewound_ms={} source={} delta={:.3}",
            &caster.to_hex()[..8],
            &target.player_id.to_hex()[..8],
            pose.rewound_by_micros / 1_000,
            pose.source.as_str(),
            (((pose.pos_x - target.pos_x).powi(2) + (pose.pos_z - target.pos_z).powi(2)) as f64)
                .sqrt(),
        );
    }

    let mut overlaid = target;
    overlaid.pos_x = pose.pos_x;
    overlaid.pos_y = pose.pos_y;
    overlaid.pos_z = pose.pos_z;
    overlaid.facing_yaw = pose.yaw;
    overlaid
}

//! One-database-per-match bootstrap and admission contract.
//!
//! A fresh database is inert until exactly one of two paths wins:
//! - the module owner bootstraps a provisioned 2v2 bot match; or
//! - the module owner explicitly enables the temporary local-direct
//!   compatibility mode used by the current Hub button.
//!
//! Provisioned tables are private. Gameplay clients learn match phase from the
//! existing public arena runtime rows, not from orchestration credentials or
//! reservations.

use std::time::Duration;

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

#[allow(unused_imports)]
use crate::arena::arena_instance as _;
use crate::arena_maps::require_arena_map_id;
#[allow(unused_imports)]
use crate::bot_matches::arena_match as _;
#[allow(unused_imports)]
use crate::match_contract::match_bootstrap_config as _;
#[allow(unused_imports)]
use crate::match_contract::match_module_owner as _;
#[allow(unused_imports)]
use crate::match_contract::match_reservation as _;
#[allow(unused_imports)]
use crate::player::player as _;

const SINGLETON_ID: u8 = 0;
const MAX_ALLOCATION_DURATION: Duration = Duration::from_secs(15 * 60);

const MODE_UNCONFIGURED: &str = "UNCONFIGURED";
const MODE_LOCAL_DIRECT: &str = "LOCAL_DIRECT";
const MODE_PROVISIONED: &str = "PROVISIONED";

pub(crate) const PHASE_BOOTSTRAPPING: &str = "BOOTSTRAPPING";
pub(crate) const PHASE_WAITING: &str = "WAITING";
pub(crate) const PHASE_COUNTDOWN: &str = "COUNTDOWN";
pub(crate) const PHASE_IN_PROGRESS: &str = "IN_PROGRESS";
pub(crate) const PHASE_ENDED: &str = "ENDED";
pub(crate) const PHASE_ABORTED: &str = "ABORTED";

const QUEUE_UNRANKED: &str = "UNRANKED";
const FORMAT_2V2: &str = "2V2";
const RULESET_TEAM_ELIMINATION: &str = "TEAM_ELIMINATION";

/// Private authority and deployment-mode latch for this physical database.
#[table(accessor = match_module_owner)]
pub struct MatchModuleOwner {
    #[primary_key]
    pub singleton_id: u8,
    pub identity: Identity,
    pub deployment_mode: String,
    pub updated_at: Timestamp,
}

/// Private, one-shot configuration for a provisioned physical match database.
#[table(accessor = match_bootstrap_config)]
pub struct MatchBootstrapConfig {
    #[primary_key]
    pub singleton_id: u8,
    pub match_id: String,
    pub match_build_id: String,
    pub map_id: String,
    pub queue_kind: String,
    pub format: String,
    pub ruleset: String,
    pub seed: u64,
    pub phase: String,
    pub allocation_expires_at: Timestamp,
    pub bootstrapped_at: Timestamp,
    pub ended_at: Option<Timestamp>,
    pub terminal_reason: Option<String>,
}

/// Private frozen admission record. The first slice permits one human only.
#[table(accessor = match_reservation)]
#[derive(Clone)]
pub struct MatchReservation {
    #[primary_key]
    pub player_identity: Identity,
    pub team_id: u8,
    pub team_slot: u8,
    pub display_name: String,
    pub reserved_at: Timestamp,
}

pub(crate) enum ConnectionAdmission {
    LocalDirect,
    Service,
    Reserved(MatchReservation),
}

pub(crate) fn initialize_match_module(ctx: &ReducerContext) {
    ctx.db.match_module_owner().insert(MatchModuleOwner {
        singleton_id: SINGLETON_ID,
        identity: ctx.sender(),
        deployment_mode: MODE_UNCONFIGURED.to_string(),
        updated_at: ctx.timestamp,
    });
    log::info!(
        "[MATCH_CONTRACT] Captured module owner {}; database is idle and unconfigured",
        &ctx.sender().to_hex()[..8]
    );
}

/// Resolves a connection before any player-facing row can be created.
pub(crate) fn admit_connection(
    ctx: &ReducerContext,
    identity: Identity,
) -> Result<ConnectionAdmission, String> {
    let Some(owner) = ctx
        .db
        .match_module_owner()
        .singleton_id()
        .find(SINGLETON_ID)
    else {
        // Data-preserving publishes of databases created before this contract
        // remain on the temporary direct path.
        return Ok(ConnectionAdmission::LocalDirect);
    };

    match owner.deployment_mode.as_str() {
        MODE_UNCONFIGURED => {
            if identity == owner.identity {
                Ok(ConnectionAdmission::Service)
            } else {
                Err("Match database is waiting for owner bootstrap".to_string())
            }
        }
        MODE_LOCAL_DIRECT => Ok(ConnectionAdmission::LocalDirect),
        MODE_PROVISIONED => {
            if identity == owner.identity {
                return Ok(ConnectionAdmission::Service);
            }
            let config = require_bootstrap_config(ctx)?;
            let reservation = ctx.db.match_reservation().player_identity().find(identity);
            validate_provisioned_gameplay_admission(
                reservation.is_some(),
                config.phase.as_str(),
                config.allocation_expires_at <= ctx.timestamp,
            )?;
            Ok(ConnectionAdmission::Reserved(
                reservation.expect("validated reservation must exist"),
            ))
        }
        unknown => Err(format!("Unknown match deployment mode {unknown}")),
    }
}

pub(crate) fn simulation_should_run(ctx: &ReducerContext) -> bool {
    let Some(owner) = ctx
        .db
        .match_module_owner()
        .singleton_id()
        .find(SINGLETON_ID)
    else {
        return true;
    };

    match owner.deployment_mode.as_str() {
        MODE_LOCAL_DIRECT => true,
        MODE_PROVISIONED => ctx
            .db
            .match_bootstrap_config()
            .singleton_id()
            .find(SINGLETON_ID)
            .is_some_and(|config| provisioned_phase_runs_simulation(config.phase.as_str())),
        _ => false,
    }
}

pub(crate) fn is_provisioned(ctx: &ReducerContext) -> bool {
    ctx.db
        .match_module_owner()
        .singleton_id()
        .find(SINGLETON_ID)
        .is_some_and(|owner| owner.deployment_mode == MODE_PROVISIONED)
}

pub(crate) fn start_reserved_player_match(
    ctx: &ReducerContext,
    identity: Identity,
) -> Result<(), String> {
    let reservation = ctx
        .db
        .match_reservation()
        .player_identity()
        .find(identity)
        .ok_or_else(|| "Match reservation disappeared during admission".to_string())?;
    crate::bot_matches::join_provisioned_human(ctx, &reservation)?;
    set_provisioned_phase(ctx, PHASE_COUNTDOWN, None)?;
    crate::game_loop::ensure_game_loop_schedule(ctx);
    crate::game_loop::ensure_game_loop_watchdog_schedule(ctx);
    Ok(())
}

pub(crate) fn mark_in_progress(ctx: &ReducerContext) {
    if let Err(error) = set_provisioned_phase(ctx, PHASE_IN_PROGRESS, None) {
        log::error!("[MATCH_CONTRACT] Could not record IN_PROGRESS phase: {error}");
    }
}

pub(crate) fn mark_ended(ctx: &ReducerContext) {
    if let Err(error) = set_provisioned_phase(ctx, PHASE_ENDED, Some(ctx.timestamp)) {
        log::error!("[MATCH_CONTRACT] Could not record ENDED phase: {error}");
    }
}

/// Handles provisioned/service disconnects. `true` means the legacy player
/// teardown path must not run.
pub(crate) fn handle_provisioned_disconnect(
    ctx: &ReducerContext,
    identity: Identity,
) -> Result<bool, String> {
    if !is_provisioned(ctx) {
        return Ok(false);
    }

    let owner = require_module_owner(ctx)?;
    if identity == owner.identity {
        return Ok(true);
    }
    if ctx
        .db
        .match_reservation()
        .player_identity()
        .find(identity)
        .is_none()
    {
        return Ok(true);
    }

    let config = require_bootstrap_config(ctx)?;
    if !is_terminal_phase(config.phase.as_str()) {
        set_provisioned_phase(ctx, PHASE_ABORTED, Some(ctx.timestamp))?;
        let mut config = require_bootstrap_config(ctx)?;
        config.terminal_reason = Some("PLAYER_DISCONNECTED".to_string());
        ctx.db
            .match_bootstrap_config()
            .singleton_id()
            .update(config);
    }
    crate::bot_matches::teardown_provisioned_match_runtime(ctx)?;
    crate::game_loop::stop_game_loop_schedule(ctx);
    Ok(true)
}

/// Temporary compatibility switch for the current direct-connect Unity path.
/// It is deliberately owner-only and cannot replace a bootstrap decision.
#[reducer]
pub fn enable_local_direct_mode(ctx: &ReducerContext) -> Result<(), String> {
    let Some(mut owner) = ctx
        .db
        .match_module_owner()
        .singleton_id()
        .find(SINGLETON_ID)
    else {
        // Data-preserving upgrade of a database that predates owner capture.
        ctx.db.match_module_owner().insert(MatchModuleOwner {
            singleton_id: SINGLETON_ID,
            identity: ctx.sender(),
            deployment_mode: MODE_LOCAL_DIRECT.to_string(),
            updated_at: ctx.timestamp,
        });
        return Ok(());
    };
    require_identity(
        ctx.sender(),
        owner.identity,
        "Only the match database owner may enable local-direct mode",
    )?;
    if owner.deployment_mode == MODE_LOCAL_DIRECT {
        return Ok(());
    }
    if owner.deployment_mode != MODE_UNCONFIGURED
        || ctx
            .db
            .match_bootstrap_config()
            .singleton_id()
            .find(SINGLETON_ID)
            .is_some()
    {
        return Err("A provisioned match database cannot become local-direct".to_string());
    }
    if ctx.db.player().iter().next().is_some()
        || ctx.db.arena_instance().iter().next().is_some()
        || ctx.db.arena_match().iter().next().is_some()
    {
        return Err("Cannot enable local-direct mode after gameplay rows exist".to_string());
    }

    owner.deployment_mode = MODE_LOCAL_DIRECT.to_string();
    owner.updated_at = ctx.timestamp;
    ctx.db.match_module_owner().singleton_id().update(owner);
    log::warn!("[MATCH_CONTRACT] Temporary local-direct compatibility mode enabled by owner");
    Ok(())
}

#[allow(clippy::too_many_arguments)]
#[reducer]
pub fn bootstrap_unranked_2v2_bot_match(
    ctx: &ReducerContext,
    match_id: String,
    match_build_id: String,
    map_id: String,
    seed: u64,
    allocation_expires_at: Timestamp,
    reserved_player_identity: Identity,
    reserved_display_name: String,
) -> Result<(), String> {
    let mut owner = require_module_owner(ctx)?;
    require_identity(
        ctx.sender(),
        owner.identity,
        "Only the match database owner may bootstrap it",
    )?;
    let has_bootstrap_config = ctx
        .db
        .match_bootstrap_config()
        .singleton_id()
        .find(SINGLETON_ID)
        .is_some();
    if !bootstrap_mode_is_available(owner.deployment_mode.as_str(), has_bootstrap_config) {
        return Err("This match database has already selected a runtime mode".to_string());
    }
    if reserved_player_identity == Identity::ZERO || reserved_player_identity == owner.identity {
        return Err(
            "Reserved gameplay identity must be nonzero and distinct from the module owner"
                .to_string(),
        );
    }
    if ctx.db.player().iter().next().is_some()
        || ctx.db.arena_instance().iter().next().is_some()
        || ctx.db.arena_match().iter().next().is_some()
    {
        return Err(
            "Cannot bootstrap a database that already has gameplay runtime rows".to_string(),
        );
    }

    let match_id = validate_identifier("match id", match_id, 8, 96)?;
    let match_build_id = validate_identifier("match build id", match_build_id, 1, 96)?;
    let map_id = require_arena_map_id(map_id.trim())?.as_str().to_string();
    let reserved_display_name = validate_display_name(reserved_display_name)?;
    validate_future_deadline(ctx.timestamp, allocation_expires_at)?;

    owner.deployment_mode = MODE_PROVISIONED.to_string();
    owner.updated_at = ctx.timestamp;
    ctx.db.match_module_owner().singleton_id().update(owner);
    ctx.db
        .match_bootstrap_config()
        .insert(MatchBootstrapConfig {
            singleton_id: SINGLETON_ID,
            match_id,
            match_build_id,
            map_id: map_id.clone(),
            queue_kind: QUEUE_UNRANKED.to_string(),
            format: FORMAT_2V2.to_string(),
            ruleset: RULESET_TEAM_ELIMINATION.to_string(),
            seed,
            phase: PHASE_BOOTSTRAPPING.to_string(),
            allocation_expires_at,
            bootstrapped_at: ctx.timestamp,
            ended_at: None,
            terminal_reason: None,
        });
    ctx.db.match_reservation().insert(MatchReservation {
        player_identity: reserved_player_identity,
        team_id: 0,
        team_slot: 0,
        display_name: reserved_display_name,
        reserved_at: ctx.timestamp,
    });

    crate::bot_matches::bootstrap_provisioned_2v2(
        ctx,
        reserved_player_identity,
        seed,
        map_id.as_str(),
    )?;
    set_provisioned_phase(ctx, PHASE_WAITING, None)?;
    log::info!(
        "[MATCH_CONTRACT] Bootstrapped match for reserved player {}; waiting without a game tick",
        &reserved_player_identity.to_hex()[..8]
    );
    Ok(())
}

#[reducer]
pub fn abort_match(ctx: &ReducerContext, reason: String) -> Result<(), String> {
    let owner = require_module_owner(ctx)?;
    require_identity(
        ctx.sender(),
        owner.identity,
        "Only the match database owner may abort it",
    )?;
    if owner.deployment_mode != MODE_PROVISIONED {
        return Err("Only a provisioned match database can be aborted".to_string());
    }
    let reason = validate_identifier("abort reason", reason, 1, 64)?.to_ascii_uppercase();
    let config = require_bootstrap_config(ctx)?;
    if config.phase == PHASE_ABORTED {
        return Ok(());
    }
    if config.phase == PHASE_ENDED {
        return Err("An ended match cannot be aborted".to_string());
    }

    set_provisioned_phase(ctx, PHASE_ABORTED, Some(ctx.timestamp))?;
    let mut config = require_bootstrap_config(ctx)?;
    config.terminal_reason = Some(reason);
    ctx.db
        .match_bootstrap_config()
        .singleton_id()
        .update(config);
    crate::bot_matches::teardown_provisioned_match_runtime(ctx)?;
    crate::game_loop::stop_game_loop_schedule(ctx);
    Ok(())
}

fn require_module_owner(ctx: &ReducerContext) -> Result<MatchModuleOwner, String> {
    ctx.db
        .match_module_owner()
        .singleton_id()
        .find(SINGLETON_ID)
        .ok_or_else(|| "Match module owner configuration is missing".to_string())
}

fn require_bootstrap_config(ctx: &ReducerContext) -> Result<MatchBootstrapConfig, String> {
    ctx.db
        .match_bootstrap_config()
        .singleton_id()
        .find(SINGLETON_ID)
        .ok_or_else(|| "Match bootstrap configuration is missing".to_string())
}

fn set_provisioned_phase(
    ctx: &ReducerContext,
    phase: &str,
    ended_at: Option<Timestamp>,
) -> Result<(), String> {
    if !is_provisioned(ctx) {
        return Ok(());
    }
    let mut config = require_bootstrap_config(ctx)?;
    config.phase = phase.to_string();
    if ended_at.is_some() {
        config.ended_at = ended_at;
    }
    ctx.db
        .match_bootstrap_config()
        .singleton_id()
        .update(config);
    Ok(())
}

fn require_identity(actual: Identity, expected: Identity, message: &str) -> Result<(), String> {
    (actual == expected)
        .then_some(())
        .ok_or_else(|| message.to_string())
}

fn validate_identifier(
    label: &str,
    value: String,
    min_len: usize,
    max_len: usize,
) -> Result<String, String> {
    let value = value.trim().to_string();
    if value.len() < min_len || value.len() > max_len {
        return Err(format!(
            "{label} must be between {min_len} and {max_len} characters"
        ));
    }
    if !value
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        return Err(format!(
            "{label} may contain only ASCII letters, numbers, '-' and '_'"
        ));
    }
    Ok(value)
}

fn validate_display_name(value: String) -> Result<String, String> {
    let value = value.trim().to_string();
    if value.is_empty() || value.len() > 32 || value.chars().any(char::is_control) {
        return Err("reserved display name must be 1-32 visible characters".to_string());
    }
    Ok(value)
}

fn validate_future_deadline(now: Timestamp, deadline: Timestamp) -> Result<(), String> {
    if deadline <= now {
        return Err("allocation deadline must be in the future".to_string());
    }
    if deadline > now + MAX_ALLOCATION_DURATION {
        return Err("allocation deadline exceeds the 15-minute maximum".to_string());
    }
    Ok(())
}

fn is_terminal_phase(phase: &str) -> bool {
    matches!(phase, PHASE_ENDED | PHASE_ABORTED)
}

fn provisioned_phase_runs_simulation(phase: &str) -> bool {
    matches!(phase, PHASE_COUNTDOWN | PHASE_IN_PROGRESS)
}

fn bootstrap_mode_is_available(mode: &str, has_bootstrap_config: bool) -> bool {
    mode == MODE_UNCONFIGURED && !has_bootstrap_config
}

fn validate_provisioned_gameplay_admission(
    has_reservation: bool,
    phase: &str,
    allocation_expired: bool,
) -> Result<(), String> {
    if !has_reservation {
        return Err("This identity is not reserved for the provisioned match".to_string());
    }
    if is_terminal_phase(phase) {
        return Err(format!("Provisioned match is no longer joinable ({phase})"));
    }
    if allocation_expired {
        return Err("Provisioned match allocation has expired".to_string());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_live_provisioned_phases_run_simulation() {
        for phase in [
            PHASE_BOOTSTRAPPING,
            PHASE_WAITING,
            PHASE_ENDED,
            PHASE_ABORTED,
        ] {
            assert!(!provisioned_phase_runs_simulation(phase));
        }
        for phase in [PHASE_COUNTDOWN, PHASE_IN_PROGRESS] {
            assert!(provisioned_phase_runs_simulation(phase));
        }
    }

    #[test]
    fn bootstrap_is_one_shot_and_cannot_replace_direct_mode() {
        assert!(bootstrap_mode_is_available(MODE_UNCONFIGURED, false));
        assert!(!bootstrap_mode_is_available(MODE_UNCONFIGURED, true));
        assert!(!bootstrap_mode_is_available(MODE_LOCAL_DIRECT, false));
        assert!(!bootstrap_mode_is_available(MODE_PROVISIONED, false));
    }

    #[test]
    fn provisioned_admission_requires_the_reserved_identity_and_live_allocation() {
        assert!(validate_provisioned_gameplay_admission(true, PHASE_WAITING, false).is_ok());
        assert!(validate_provisioned_gameplay_admission(false, PHASE_WAITING, false).is_err());
        assert!(validate_provisioned_gameplay_admission(true, PHASE_ENDED, false).is_err());
        assert!(validate_provisioned_gameplay_admission(true, PHASE_WAITING, true).is_err());
    }

    #[test]
    fn bootstrap_identifiers_are_bounded_and_path_safe() {
        assert!(validate_identifier("match", "match-0001".to_string(), 8, 96).is_ok());
        assert!(validate_identifier("match", "short".to_string(), 8, 96).is_err());
        assert!(validate_identifier("match", "../../other-db".to_string(), 8, 96).is_err());
    }

    #[test]
    fn allocation_deadline_is_future_and_bounded() {
        let now = Timestamp::UNIX_EPOCH + Duration::from_secs(10);
        assert!(validate_future_deadline(now, now + Duration::from_secs(30)).is_ok());
        assert!(validate_future_deadline(now, now).is_err());
        assert!(validate_future_deadline(now, now + Duration::from_secs(901)).is_err());
    }

    #[test]
    fn terminal_phase_vocabulary_is_closed() {
        assert!(is_terminal_phase(PHASE_ENDED));
        assert!(is_terminal_phase(PHASE_ABORTED));
        assert!(!is_terminal_phase(PHASE_WAITING));
        assert!(!is_terminal_phase(PHASE_IN_PROGRESS));
    }
}

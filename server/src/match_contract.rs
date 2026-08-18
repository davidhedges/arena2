//! One-database-per-session bootstrap and admission contract.
//!
//! A fresh database is inert until exactly one of three paths wins:
//! - the module owner bootstraps a provisioned 2v2 bot match;
//! - the module owner bootstraps a provisioned open-world instance; or
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

/// A disposable open world rides the same bootstrap singleton as a match so it
/// inherits allocation expiry, phase-driven teardown, and database disposal
/// unchanged. The destination scene occupies `map_id` (which the provisioner
/// already carries end to end) and `format` (which mirrors the Hub ticket).
pub(crate) const QUEUE_OPEN_WORLD: &str = "OPEN_WORLD";
const RULESET_OPEN_WORLD: &str = "OPEN_WORLD_SANDBOX";

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
    pub primary_discipline_id: String,
    pub secondary_discipline_id_1: String,
    pub secondary_discipline_id_2: String,
    pub selected_ability_ids: Vec<String>,
    pub armor_set_id: String,
    pub main_hand_item_def_id: String,
    pub main_hand_color_id: String,
    pub off_hand_item_def_id: String,
    pub off_hand_color_id: String,
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

/// True only for a disposable open world. Rules written for the elimination
/// match ("a reserved player may not leave") do not apply to a sandbox whose
/// whole point is wandering in and out of private instances.
pub(crate) fn is_provisioned_open_world(ctx: &ReducerContext) -> bool {
    is_provisioned(ctx)
        && ctx
            .db
            .match_bootstrap_config()
            .singleton_id()
            .find(SINGLETON_ID)
            .is_some_and(|config| config.queue_kind == QUEUE_OPEN_WORLD)
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
    let config = require_bootstrap_config(ctx)?;
    if config.queue_kind == QUEUE_OPEN_WORLD {
        return start_reserved_open_world_session(ctx, identity, config.format.as_str());
    }
    crate::bot_matches::join_provisioned_human(ctx, &reservation)?;
    set_provisioned_phase(ctx, PHASE_COUNTDOWN, None)?;
    crate::game_loop::ensure_game_loop_schedule(ctx);
    crate::game_loop::ensure_game_loop_watchdog_schedule(ctx);
    Ok(())
}

/// A disposable open world has no match runtime to join. The reserved player
/// enters the ordinary open world at the frozen destination, so spawn, scene,
/// and subscription scope stay owned by the normal open-world lifecycle.
/// There is no countdown to wait through: the world is live on arrival.
fn start_reserved_open_world_session(
    ctx: &ReducerContext,
    identity: Identity,
    destination: &str,
) -> Result<(), String> {
    crate::arena::upsert_player_open_world_scene(ctx, identity, destination);
    crate::arena::set_player_open_world(ctx, identity)?;
    set_provisioned_phase(ctx, PHASE_IN_PROGRESS, None)?;
    crate::game_loop::ensure_game_loop_schedule(ctx);
    crate::game_loop::ensure_game_loop_watchdog_schedule(ctx);
    log::info!(
        "[MATCH_CONTRACT] Seated reserved player {} in disposable open world {}",
        &identity.to_hex()[..8],
        destination
    );
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
        // Leaving a disposable open world *is* its ending; only an elimination
        // match can be cut short, so only that one reports an abort.
        let (phase, reason) = if config.queue_kind == QUEUE_OPEN_WORLD {
            (PHASE_ENDED, "PLAYER_LEFT")
        } else {
            (PHASE_ABORTED, "PLAYER_DISCONNECTED")
        };
        set_provisioned_phase(ctx, phase, Some(ctx.timestamp))?;
        let mut config = require_bootstrap_config(ctx)?;
        config.terminal_reason = Some(reason.to_string());
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

/// Everything a provisioned bootstrap records, whatever the queue kind. The
/// caller validates `map_id` against its own vocabulary — authored arena maps
/// for a match, authored open-world scenes for a disposable world — because
/// that is the only field whose meaning differs between them.
struct ProvisionedBootstrap {
    match_id: String,
    match_build_id: String,
    map_id: String,
    queue_kind: &'static str,
    format: String,
    ruleset: &'static str,
    seed: u64,
    allocation_expires_at: Timestamp,
    reserved_player_identity: Identity,
    reserved_display_name: String,
    primary_discipline_id: String,
    secondary_discipline_id_1: String,
    secondary_discipline_id_2: String,
    selected_ability_ids: Vec<String>,
    armor_set_id: String,
    main_hand_item_def_id: String,
    main_hand_color_id: String,
    off_hand_item_def_id: String,
    off_hand_color_id: String,
}

/// Latches this database into PROVISIONED mode and freezes the caller's Hub
/// build. Every queue kind shares it so a second kind cannot quietly acquire a
/// weaker admission, expiry, or one-shot guarantee than the first.
fn claim_provisioned_database(
    ctx: &ReducerContext,
    request: ProvisionedBootstrap,
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
    if request.reserved_player_identity == Identity::ZERO
        || request.reserved_player_identity == owner.identity
    {
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

    let match_id = validate_identifier("match id", request.match_id, 8, 96)?;
    let match_build_id = validate_identifier("match build id", request.match_build_id, 1, 96)?;
    let reserved_display_name = validate_display_name(request.reserved_display_name)?;
    let primary_discipline_id = validate_optional_loadout_id(request.primary_discipline_id)?;
    let secondary_discipline_id_1 =
        validate_optional_loadout_id(request.secondary_discipline_id_1)?;
    let secondary_discipline_id_2 =
        validate_optional_loadout_id(request.secondary_discipline_id_2)?;
    let selected_ability_ids = validate_loadout_ability_ids(request.selected_ability_ids)?;
    let armor_set_id = validate_optional_loadout_id(request.armor_set_id)?;
    let main_hand_item_def_id = validate_optional_loadout_id(request.main_hand_item_def_id)?;
    let main_hand_color_id = validate_optional_loadout_id(request.main_hand_color_id)?;
    let off_hand_item_def_id = validate_optional_loadout_id(request.off_hand_item_def_id)?;
    let off_hand_color_id = validate_optional_loadout_id(request.off_hand_color_id)?;
    validate_future_deadline(ctx.timestamp, request.allocation_expires_at)?;

    owner.deployment_mode = MODE_PROVISIONED.to_string();
    owner.updated_at = ctx.timestamp;
    ctx.db.match_module_owner().singleton_id().update(owner);
    ctx.db
        .match_bootstrap_config()
        .insert(MatchBootstrapConfig {
            singleton_id: SINGLETON_ID,
            match_id,
            match_build_id,
            map_id: request.map_id,
            queue_kind: request.queue_kind.to_string(),
            format: request.format,
            ruleset: request.ruleset.to_string(),
            seed: request.seed,
            phase: PHASE_BOOTSTRAPPING.to_string(),
            allocation_expires_at: request.allocation_expires_at,
            bootstrapped_at: ctx.timestamp,
            ended_at: None,
            terminal_reason: None,
        });
    ctx.db.match_reservation().insert(MatchReservation {
        player_identity: request.reserved_player_identity,
        team_id: 0,
        team_slot: 0,
        display_name: reserved_display_name,
        primary_discipline_id,
        secondary_discipline_id_1,
        secondary_discipline_id_2,
        selected_ability_ids,
        armor_set_id,
        main_hand_item_def_id,
        main_hand_color_id,
        off_hand_item_def_id,
        off_hand_color_id,
        reserved_at: ctx.timestamp,
    });
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
    primary_discipline_id: String,
    secondary_discipline_id_1: String,
    secondary_discipline_id_2: String,
    selected_ability_ids: Vec<String>,
    armor_set_id: String,
    main_hand_item_def_id: String,
    main_hand_color_id: String,
    off_hand_item_def_id: String,
    off_hand_color_id: String,
) -> Result<(), String> {
    let map_id = require_arena_map_id(map_id.trim())?.as_str().to_string();
    claim_provisioned_database(
        ctx,
        ProvisionedBootstrap {
            match_id,
            match_build_id,
            map_id: map_id.clone(),
            queue_kind: QUEUE_UNRANKED,
            format: FORMAT_2V2.to_string(),
            ruleset: RULESET_TEAM_ELIMINATION,
            seed,
            allocation_expires_at,
            reserved_player_identity,
            reserved_display_name,
            primary_discipline_id,
            secondary_discipline_id_1,
            secondary_discipline_id_2,
            selected_ability_ids,
            armor_set_id,
            main_hand_item_def_id,
            main_hand_color_id,
            off_hand_item_def_id,
            off_hand_color_id,
        },
    )?;

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

/// Bootstraps a disposable open-world instance.
///
/// Unlike a match, nothing is built here: an authored open-world scene has no
/// arena instance, no roster, and no countdown. The database only records the
/// destination and the caller's frozen Hub build, then waits for that one
/// reserved identity to connect. Progress made inside is destroyed with the
/// database (docs/open-world-disposable-instances-2026-08-18.md).
///
/// Absent from `match-server` for the same reason `set_open_world_scene` is:
/// the PvP flavor compiles out every open-world path.
#[cfg(not(feature = "pvp_match"))]
#[allow(clippy::too_many_arguments)]
#[reducer]
pub fn bootstrap_open_world_instance(
    ctx: &ReducerContext,
    match_id: String,
    match_build_id: String,
    destination: String,
    seed: u64,
    allocation_expires_at: Timestamp,
    reserved_player_identity: Identity,
    reserved_display_name: String,
    primary_discipline_id: String,
    secondary_discipline_id_1: String,
    secondary_discipline_id_2: String,
    selected_ability_ids: Vec<String>,
    armor_set_id: String,
    main_hand_item_def_id: String,
    main_hand_color_id: String,
    off_hand_item_def_id: String,
    off_hand_color_id: String,
) -> Result<(), String> {
    let destination = destination.trim().to_string();
    if !crate::open_world_scene::is_known_open_world_scene(destination.as_str()) {
        return Err(format!("Unknown open-world destination '{destination}'"));
    }
    claim_provisioned_database(
        ctx,
        ProvisionedBootstrap {
            match_id,
            match_build_id,
            map_id: destination.clone(),
            queue_kind: QUEUE_OPEN_WORLD,
            format: destination.clone(),
            ruleset: RULESET_OPEN_WORLD,
            seed,
            allocation_expires_at,
            reserved_player_identity,
            reserved_display_name,
            primary_discipline_id,
            secondary_discipline_id_1,
            secondary_discipline_id_2,
            selected_ability_ids,
            armor_set_id,
            main_hand_item_def_id,
            main_hand_color_id,
            off_hand_item_def_id,
            off_hand_color_id,
        },
    )?;
    set_provisioned_phase(ctx, PHASE_WAITING, None)?;
    log::info!(
        "[MATCH_CONTRACT] Bootstrapped disposable open world {} for reserved player {}",
        destination,
        &reserved_player_identity.to_hex()[..8]
    );
    Ok(())
}

/// Applies the Hub-frozen build only after the ordinary player lifecycle has
/// seeded progression, inventory, and authored catalogs in this disposable DB.
pub(crate) fn apply_reserved_player_loadout(
    ctx: &ReducerContext,
    reservation: &MatchReservation,
) -> Result<(), String> {
    if !reservation.armor_set_id.is_empty() {
        crate::inventory::equip_armor_set_for_owner(
            ctx,
            reservation.player_identity,
            reservation.armor_set_id.clone(),
        )?;
    }
    if !reservation.primary_discipline_id.is_empty() {
        crate::progression::save_character_discipline_loadout_for_owner(
            ctx,
            reservation.player_identity,
            reservation.primary_discipline_id.clone(),
            reservation.secondary_discipline_id_1.clone(),
            reservation.secondary_discipline_id_2.clone(),
            reservation.selected_ability_ids.clone(),
        )?;
        if reservation.main_hand_item_def_id.is_empty() {
            crate::inventory::equip_starter_weapon_for_discipline(
                ctx,
                reservation.player_identity,
                reservation.primary_discipline_id.as_str(),
            )?;
        } else {
            crate::inventory::equip_weapon_definitions_for_discipline(
                ctx,
                reservation.player_identity,
                reservation.primary_discipline_id.as_str(),
                reservation.main_hand_item_def_id.as_str(),
                reservation.main_hand_color_id.as_str(),
                reservation.off_hand_item_def_id.as_str(),
                reservation.off_hand_color_id.as_str(),
            )?;
        }
    }
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

fn validate_optional_loadout_id(value: String) -> Result<String, String> {
    let normalized = value.trim().to_ascii_uppercase();
    if normalized.is_empty() {
        return Ok(normalized);
    }
    validate_identifier("loadout id", normalized, 1, 96)
}

fn validate_loadout_ability_ids(values: Vec<String>) -> Result<Vec<String>, String> {
    if values.len() > 128 {
        return Err("loadout may contain at most 128 selected abilities".to_string());
    }
    let mut normalized = Vec::with_capacity(values.len());
    for value in values {
        let value = validate_optional_loadout_id(value)?;
        if !value.is_empty() && !normalized.contains(&value) {
            normalized.push(value);
        }
    }
    Ok(normalized)
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
    fn frozen_loadout_ability_ids_are_bounded_unique_and_path_safe() {
        assert_eq!(
            validate_loadout_ability_ids(vec!["WARRIOR_HEW".to_string()]),
            Ok(vec!["WARRIOR_HEW".to_string()])
        );
        assert_eq!(
            validate_loadout_ability_ids(vec![
                "WARRIOR_HEW".to_string(),
                "WARRIOR_HEW".to_string(),
            ]),
            Ok(vec!["WARRIOR_HEW".to_string()])
        );
        assert!(validate_loadout_ability_ids(vec!["../../ability".to_string()]).is_err());
        assert!(validate_loadout_ability_ids(vec!["ABILITY".to_string(); 129]).is_err());
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

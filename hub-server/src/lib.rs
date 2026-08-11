//! Persistent Arena Hub control plane.
//!
//! This module deliberately contains no gameplay simulation. It owns durable
//! player identity plus the request/assignment state consumed by the future
//! match provisioner and Unity handoff.

use std::time::Duration;

use spacetimedb::{
    reducer, table, view, Identity, ReducerContext, ScheduleAt, SpacetimeType, Table, Timestamp,
    ViewContext,
};

const SERVICE_CONFIG_ID: u8 = 0;
const MAINTENANCE_INTERVAL: Duration = Duration::from_secs(60);
const PENDING_TICKET_TTL: Duration = Duration::from_secs(2 * 60);
const TERMINAL_TICKET_RETENTION: Duration = Duration::from_secs(5 * 60);
const MAX_LEASE_DURATION: Duration = Duration::from_secs(2 * 60);
const MAX_ASSIGNMENT_DURATION: Duration = Duration::from_secs(4 * 60 * 60);

const QUEUE_UNRANKED: &str = "UNRANKED";
const FORMAT_2V2: &str = "2V2";

const STATUS_PENDING: &str = "PENDING";
const STATUS_CLAIMED: &str = "CLAIMED";
const STATUS_PROVISIONING: &str = "PROVISIONING";
const STATUS_READY: &str = "READY";
const STATUS_FAILED: &str = "FAILED";
const STATUS_CLOSED: &str = "CLOSED";

const FAILURE_TICKET_EXPIRED: &str = "TICKET_EXPIRED";

#[table(accessor = hub_player)]
pub struct HubPlayer {
    #[primary_key]
    pub identity: Identity,
    pub display_name: String,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
}

#[table(accessor = hub_service_config)]
pub struct HubServiceConfig {
    #[primary_key]
    pub singleton_id: u8,
    pub module_owner: Identity,
    pub provisioner_identity: Identity,
    pub updated_at: Timestamp,
}

#[table(accessor = match_ticket)]
pub struct MatchTicket {
    #[primary_key]
    pub ticket_id: String,
    #[unique]
    pub player_identity: Identity,
    pub client_request_id: String,
    pub queue_kind: String,
    pub format: String,
    pub status: String,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
    pub expires_at: Timestamp,
    pub lease_owner: Option<String>,
    pub lease_until: Option<Timestamp>,
    pub failure_code: Option<String>,
}

#[table(accessor = match_assignment)]
pub struct MatchAssignment {
    #[primary_key]
    pub ticket_id: String,
    #[unique]
    pub player_identity: Identity,
    pub match_id: String,
    pub server_uri: String,
    pub database_identity: String,
    pub match_build_id: String,
    pub ready_at: Timestamp,
    pub expires_at: Timestamp,
}

/// One low-frequency housekeeping schedule. This is not a simulation tick.
#[table(accessor = hub_maintenance_timer, scheduled(hub_maintenance_tick))]
pub struct HubMaintenanceTimer {
    #[primary_key]
    #[auto_inc]
    pub scheduled_id: u64,
    pub scheduled_at: ScheduleAt,
}

/// Public projection of the caller's persistent Hub profile.
#[derive(SpacetimeType)]
pub struct MyHubPlayer {
    pub identity: Identity,
    pub display_name: String,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
}

/// Public projection of only the calling player's current match control state.
#[derive(SpacetimeType)]
pub struct MyMatchStatus {
    pub ticket_id: String,
    pub queue_kind: String,
    pub format: String,
    pub status: String,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
    pub expires_at: Timestamp,
    pub failure_code: Option<String>,
    pub match_id: Option<String>,
    pub server_uri: Option<String>,
    pub database_identity: Option<String>,
    pub match_build_id: Option<String>,
    pub ready_at: Option<Timestamp>,
    pub assignment_expires_at: Option<Timestamp>,
}

#[view(accessor = my_hub_player, public)]
pub fn my_hub_player(ctx: &ViewContext) -> Option<MyHubPlayer> {
    ctx.db
        .hub_player()
        .identity()
        .find(ctx.sender())
        .map(|player| MyHubPlayer {
            identity: player.identity,
            display_name: player.display_name,
            created_at: player.created_at,
            updated_at: player.updated_at,
        })
}

#[view(accessor = my_match_status, public)]
pub fn my_match_status(ctx: &ViewContext) -> Option<MyMatchStatus> {
    let ticket = ctx.db.match_ticket().player_identity().find(ctx.sender())?;
    let assignment = ctx
        .db
        .match_assignment()
        .player_identity()
        .find(ctx.sender());

    Some(MyMatchStatus {
        ticket_id: ticket.ticket_id,
        queue_kind: ticket.queue_kind,
        format: ticket.format,
        status: ticket.status,
        created_at: ticket.created_at,
        updated_at: ticket.updated_at,
        expires_at: ticket.expires_at,
        failure_code: ticket.failure_code,
        match_id: assignment.as_ref().map(|row| row.match_id.clone()),
        server_uri: assignment.as_ref().map(|row| row.server_uri.clone()),
        database_identity: assignment.as_ref().map(|row| row.database_identity.clone()),
        match_build_id: assignment.as_ref().map(|row| row.match_build_id.clone()),
        ready_at: assignment.as_ref().map(|row| row.ready_at),
        assignment_expires_at: assignment.map(|row| row.expires_at),
    })
}

#[reducer(init)]
pub fn init(ctx: &ReducerContext) -> Result<(), String> {
    ctx.db.hub_service_config().insert(HubServiceConfig {
        singleton_id: SERVICE_CONFIG_ID,
        module_owner: ctx.sender(),
        provisioner_identity: ctx.sender(),
        updated_at: ctx.timestamp,
    });
    ctx.db.hub_maintenance_timer().insert(HubMaintenanceTimer {
        scheduled_id: 0,
        scheduled_at: ScheduleAt::Interval(MAINTENANCE_INTERVAL.into()),
    });
    log::info!(
        "[HUB_INIT] Persistent Hub initialized; maintenance interval={}s; no gameplay tick",
        MAINTENANCE_INTERVAL.as_secs()
    );
    Ok(())
}

#[reducer(client_connected)]
pub fn client_connected(ctx: &ReducerContext) -> Result<(), String> {
    ensure_hub_player(ctx, ctx.sender());
    Ok(())
}

#[reducer]
pub fn request_unranked_2v2_bot_match(
    ctx: &ReducerContext,
    client_request_id: String,
) -> Result<(), String> {
    let client_request_id = validate_identifier("client request id", client_request_id, 8, 64)?;
    let player_identity = ctx.sender();
    ensure_hub_player(ctx, player_identity);

    if let Some(existing) = ctx
        .db
        .match_ticket()
        .player_identity()
        .find(player_identity)
    {
        match request_decision(
            existing.status.as_str(),
            existing.client_request_id.as_str(),
            client_request_id.as_str(),
        ) {
            RequestDecision::Idempotent => return Ok(()),
            RequestDecision::RejectActive => {
                return Err("A match request is already active for this player".to_string())
            }
            RequestDecision::ReplaceTerminal => {
                delete_assignment_for_player(ctx, player_identity);
                ctx.db.match_ticket().ticket_id().delete(existing.ticket_id);
            }
        }
    }

    let ticket_id = ticket_id_for(player_identity, client_request_id.as_str());
    ctx.db.match_ticket().insert(MatchTicket {
        ticket_id,
        player_identity,
        client_request_id,
        queue_kind: QUEUE_UNRANKED.to_string(),
        format: FORMAT_2V2.to_string(),
        status: STATUS_PENDING.to_string(),
        created_at: ctx.timestamp,
        updated_at: ctx.timestamp,
        expires_at: ctx.timestamp + PENDING_TICKET_TTL,
        lease_owner: None,
        lease_until: None,
        failure_code: None,
    });
    Ok(())
}

#[reducer]
pub fn cancel_match_ticket(ctx: &ReducerContext, ticket_id: String) -> Result<(), String> {
    let Some(mut ticket) = ctx.db.match_ticket().ticket_id().find(ticket_id) else {
        return Err("Match ticket not found".to_string());
    };
    if ticket.player_identity != ctx.sender() {
        return Err("Cannot cancel another player's match ticket".to_string());
    }
    if ticket.status == STATUS_CLOSED {
        return Ok(());
    }

    ticket.status = STATUS_CLOSED.to_string();
    ticket.updated_at = ctx.timestamp;
    ticket.expires_at = ctx.timestamp + TERMINAL_TICKET_RETENTION;
    ticket.lease_owner = None;
    ticket.lease_until = None;
    ticket.failure_code = None;
    ctx.db.match_ticket().ticket_id().update(ticket);
    Ok(())
}

#[reducer]
pub fn set_provisioner_identity(
    ctx: &ReducerContext,
    provisioner_identity: Identity,
) -> Result<(), String> {
    let mut config = require_service_config(ctx)?;
    require_identity(
        ctx.sender(),
        config.module_owner,
        "Only the Hub module owner may change the provisioner identity",
    )?;
    config.provisioner_identity = provisioner_identity;
    config.updated_at = ctx.timestamp;
    ctx.db.hub_service_config().singleton_id().update(config);
    Ok(())
}

#[reducer]
pub fn service_claim_ticket(
    ctx: &ReducerContext,
    ticket_id: String,
    lease_id: String,
    lease_until: Timestamp,
) -> Result<(), String> {
    require_provisioner(ctx)?;
    let lease_id = validate_identifier("lease id", lease_id, 8, 96)?;
    validate_future_deadline(
        "lease deadline",
        ctx.timestamp,
        lease_until,
        MAX_LEASE_DURATION,
    )?;

    let mut ticket = ctx
        .db
        .match_ticket()
        .ticket_id()
        .find(ticket_id)
        .ok_or_else(|| "Match ticket not found".to_string())?;

    if ticket.expires_at <= ctx.timestamp {
        return Err("Match ticket has expired".to_string());
    }

    let lease_expired = ticket
        .lease_until
        .is_none_or(|deadline| deadline <= ctx.timestamp);
    match claim_decision(
        ticket.status.as_str(),
        ticket.lease_owner.as_deref(),
        lease_expired,
        lease_id.as_str(),
    ) {
        ClaimDecision::Renew => {
            ticket.updated_at = ctx.timestamp;
            ticket.lease_until = Some(lease_until);
            ctx.db.match_ticket().ticket_id().update(ticket);
            return Ok(());
        }
        ClaimDecision::StartOrResume => {}
        ClaimDecision::Reject => {
            return Err(format!(
                "Match ticket cannot be claimed from status {}",
                ticket.status
            ))
        }
    }
    ticket.status = STATUS_CLAIMED.to_string();
    ticket.updated_at = ctx.timestamp;
    ticket.lease_owner = Some(lease_id);
    ticket.lease_until = Some(lease_until);
    ticket.failure_code = None;
    ctx.db.match_ticket().ticket_id().update(ticket);
    Ok(())
}

#[reducer]
pub fn service_mark_provisioning(
    ctx: &ReducerContext,
    ticket_id: String,
    lease_id: String,
) -> Result<(), String> {
    require_provisioner(ctx)?;
    let mut ticket = require_leased_ticket(ctx, ticket_id, lease_id.as_str())?;
    if ticket.status == STATUS_PROVISIONING {
        return Ok(());
    }
    if ticket.status != STATUS_CLAIMED {
        return Err(format!(
            "Match ticket cannot start provisioning from status {}",
            ticket.status
        ));
    }

    ticket.status = STATUS_PROVISIONING.to_string();
    ticket.updated_at = ctx.timestamp;
    ctx.db.match_ticket().ticket_id().update(ticket);
    Ok(())
}

#[allow(clippy::too_many_arguments)]
#[reducer]
pub fn service_mark_ready(
    ctx: &ReducerContext,
    ticket_id: String,
    lease_id: String,
    match_id: String,
    server_uri: String,
    database_identity: String,
    match_build_id: String,
    assignment_expires_at: Timestamp,
) -> Result<(), String> {
    require_provisioner(ctx)?;
    let match_id = validate_identifier("match id", match_id, 8, 96)?;
    let database_identity = validate_identifier("database identity", database_identity, 8, 128)?;
    let match_build_id = validate_identifier("match build id", match_build_id, 1, 96)?;
    let server_uri = validate_server_uri(server_uri)?;
    validate_future_deadline(
        "assignment deadline",
        ctx.timestamp,
        assignment_expires_at,
        MAX_ASSIGNMENT_DURATION,
    )?;

    let mut ticket = ctx
        .db
        .match_ticket()
        .ticket_id()
        .find(ticket_id.clone())
        .ok_or_else(|| "Match ticket not found".to_string())?;

    if ticket.status == STATUS_READY {
        let same_assignment = ctx
            .db
            .match_assignment()
            .ticket_id()
            .find(ticket_id)
            .is_some_and(|assignment| {
                assignment.match_id == match_id
                    && assignment.server_uri == server_uri
                    && assignment.database_identity == database_identity
                    && assignment.match_build_id == match_build_id
                    && assignment.expires_at == assignment_expires_at
            });
        return same_assignment
            .then_some(())
            .ok_or_else(|| "Ready ticket already has a different assignment".to_string());
    }

    validate_matching_lease(&ticket, lease_id.as_str(), ctx.timestamp)?;
    if ticket.status != STATUS_CLAIMED && ticket.status != STATUS_PROVISIONING {
        return Err(format!(
            "Match ticket cannot become ready from status {}",
            ticket.status
        ));
    }

    delete_assignment_for_player(ctx, ticket.player_identity);
    ctx.db.match_assignment().insert(MatchAssignment {
        ticket_id: ticket.ticket_id.clone(),
        player_identity: ticket.player_identity,
        match_id,
        server_uri,
        database_identity,
        match_build_id,
        ready_at: ctx.timestamp,
        expires_at: assignment_expires_at,
    });

    ticket.status = STATUS_READY.to_string();
    ticket.updated_at = ctx.timestamp;
    ticket.expires_at = assignment_expires_at;
    ticket.lease_owner = None;
    ticket.lease_until = None;
    ticket.failure_code = None;
    ctx.db.match_ticket().ticket_id().update(ticket);
    Ok(())
}

#[reducer]
pub fn service_mark_failed(
    ctx: &ReducerContext,
    ticket_id: String,
    lease_id: String,
    failure_code: String,
) -> Result<(), String> {
    require_provisioner(ctx)?;
    let failure_code = validate_failure_code(failure_code)?;
    let mut ticket = ctx
        .db
        .match_ticket()
        .ticket_id()
        .find(ticket_id)
        .ok_or_else(|| "Match ticket not found".to_string())?;

    if ticket.status == STATUS_FAILED
        && ticket.failure_code.as_deref() == Some(failure_code.as_str())
    {
        return Ok(());
    }
    validate_matching_lease(&ticket, lease_id.as_str(), ctx.timestamp)?;
    if ticket.status != STATUS_CLAIMED && ticket.status != STATUS_PROVISIONING {
        return Err(format!(
            "Match ticket cannot fail from status {}",
            ticket.status
        ));
    }

    delete_assignment_for_player(ctx, ticket.player_identity);
    ticket.status = STATUS_FAILED.to_string();
    ticket.updated_at = ctx.timestamp;
    ticket.expires_at = ctx.timestamp + TERMINAL_TICKET_RETENTION;
    ticket.lease_owner = None;
    ticket.lease_until = None;
    ticket.failure_code = Some(failure_code);
    ctx.db.match_ticket().ticket_id().update(ticket);
    Ok(())
}

#[reducer]
pub fn service_close_ticket(ctx: &ReducerContext, ticket_id: String) -> Result<(), String> {
    require_provisioner(ctx)?;
    let Some(mut ticket) = ctx.db.match_ticket().ticket_id().find(ticket_id) else {
        return Ok(());
    };

    delete_assignment_for_player(ctx, ticket.player_identity);
    ticket.status = STATUS_CLOSED.to_string();
    ticket.updated_at = ctx.timestamp;
    ticket.expires_at = ctx.timestamp + TERMINAL_TICKET_RETENTION;
    ticket.lease_owner = None;
    ticket.lease_until = None;
    ticket.failure_code = None;
    ctx.db.match_ticket().ticket_id().update(ticket);
    Ok(())
}

#[reducer]
pub fn hub_maintenance_tick(
    ctx: &ReducerContext,
    _timer: HubMaintenanceTimer,
) -> Result<(), String> {
    let expired_ticket_ids: Vec<String> = ctx
        .db
        .match_ticket()
        .iter()
        .filter(|ticket| ticket.expires_at <= ctx.timestamp)
        .map(|ticket| ticket.ticket_id)
        .collect();

    for ticket_id in expired_ticket_ids {
        let Some(mut ticket) = ctx.db.match_ticket().ticket_id().find(ticket_id) else {
            continue;
        };
        match expiry_action(ticket.status.as_str()) {
            ExpiryAction::MarkFailed => {
                ticket.status = STATUS_FAILED.to_string();
                ticket.updated_at = ctx.timestamp;
                ticket.expires_at = ctx.timestamp + TERMINAL_TICKET_RETENTION;
                ticket.lease_owner = None;
                ticket.lease_until = None;
                ticket.failure_code = Some(FAILURE_TICKET_EXPIRED.to_string());
                ctx.db.match_ticket().ticket_id().update(ticket);
            }
            ExpiryAction::MarkClosed => {
                ticket.status = STATUS_CLOSED.to_string();
                ticket.updated_at = ctx.timestamp;
                ticket.expires_at = ctx.timestamp + TERMINAL_TICKET_RETENTION;
                ticket.lease_owner = None;
                ticket.lease_until = None;
                ticket.failure_code = None;
                ctx.db.match_ticket().ticket_id().update(ticket);
            }
            ExpiryAction::Delete => {
                delete_assignment_for_player(ctx, ticket.player_identity);
                ctx.db.match_ticket().ticket_id().delete(ticket.ticket_id);
            }
        }
    }
    Ok(())
}

fn ensure_hub_player(ctx: &ReducerContext, identity: Identity) {
    if ctx.db.hub_player().identity().find(identity).is_some() {
        return;
    }
    let identity_hex = identity.to_hex();
    ctx.db.hub_player().insert(HubPlayer {
        identity,
        display_name: format!("Player_{}", &identity_hex[..8]),
        created_at: ctx.timestamp,
        updated_at: ctx.timestamp,
    });
}

fn require_service_config(ctx: &ReducerContext) -> Result<HubServiceConfig, String> {
    ctx.db
        .hub_service_config()
        .singleton_id()
        .find(SERVICE_CONFIG_ID)
        .ok_or_else(|| "Hub service configuration is missing".to_string())
}

fn require_provisioner(ctx: &ReducerContext) -> Result<(), String> {
    let config = require_service_config(ctx)?;
    require_identity(
        ctx.sender(),
        config.provisioner_identity,
        "Only the configured match provisioner may perform this operation",
    )
}

fn require_identity(actual: Identity, expected: Identity, message: &str) -> Result<(), String> {
    (actual == expected)
        .then_some(())
        .ok_or_else(|| message.to_string())
}

fn require_leased_ticket(
    ctx: &ReducerContext,
    ticket_id: String,
    lease_id: &str,
) -> Result<MatchTicket, String> {
    let ticket = ctx
        .db
        .match_ticket()
        .ticket_id()
        .find(ticket_id)
        .ok_or_else(|| "Match ticket not found".to_string())?;
    validate_matching_lease(&ticket, lease_id, ctx.timestamp)?;
    Ok(ticket)
}

fn validate_matching_lease(
    ticket: &MatchTicket,
    lease_id: &str,
    now: Timestamp,
) -> Result<(), String> {
    if ticket.lease_owner.as_deref() != Some(lease_id) {
        return Err("Match ticket lease does not belong to this work attempt".to_string());
    }
    if ticket.lease_until.is_none_or(|deadline| deadline <= now) {
        return Err("Match ticket lease has expired".to_string());
    }
    Ok(())
}

fn delete_assignment_for_player(ctx: &ReducerContext, player_identity: Identity) {
    if let Some(assignment) = ctx
        .db
        .match_assignment()
        .player_identity()
        .find(player_identity)
    {
        ctx.db
            .match_assignment()
            .ticket_id()
            .delete(assignment.ticket_id);
    }
}

fn ticket_id_for(player_identity: Identity, client_request_id: &str) -> String {
    format!("{}:{}", player_identity.to_hex(), client_request_id)
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
            "{} must be between {} and {} characters",
            label, min_len, max_len
        ));
    }
    if !value
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        return Err(format!(
            "{} may contain only ASCII letters, numbers, '-' and '_'",
            label
        ));
    }
    Ok(value)
}

fn validate_failure_code(value: String) -> Result<String, String> {
    let value = validate_identifier("failure code", value, 1, 64)?;
    Ok(value.to_ascii_uppercase())
}

fn validate_server_uri(value: String) -> Result<String, String> {
    let value = value.trim().to_string();
    if value.len() > 256 || !(value.starts_with("ws://") || value.starts_with("wss://")) {
        return Err(
            "server URI must be a ws:// or wss:// endpoint of at most 256 characters".to_string(),
        );
    }
    Ok(value)
}

fn validate_future_deadline(
    label: &str,
    now: Timestamp,
    deadline: Timestamp,
    maximum_duration: Duration,
) -> Result<(), String> {
    if deadline <= now {
        return Err(format!("{} must be in the future", label));
    }
    if deadline > now + maximum_duration {
        return Err(format!("{} exceeds its maximum duration", label));
    }
    Ok(())
}

fn is_active_status(status: &str) -> bool {
    matches!(
        status,
        STATUS_PENDING | STATUS_CLAIMED | STATUS_PROVISIONING | STATUS_READY
    )
}

#[derive(Debug, PartialEq, Eq)]
enum ClaimDecision {
    StartOrResume,
    Renew,
    Reject,
}

fn claim_decision(
    status: &str,
    current_lease: Option<&str>,
    lease_expired: bool,
    requested_lease: &str,
) -> ClaimDecision {
    if matches!(status, STATUS_CLAIMED | STATUS_PROVISIONING)
        && current_lease == Some(requested_lease)
    {
        return ClaimDecision::Renew;
    }
    if status == STATUS_PENDING
        || (matches!(status, STATUS_CLAIMED | STATUS_PROVISIONING) && lease_expired)
    {
        return ClaimDecision::StartOrResume;
    }
    ClaimDecision::Reject
}

#[derive(Debug, PartialEq, Eq)]
enum RequestDecision {
    Idempotent,
    RejectActive,
    ReplaceTerminal,
}

fn request_decision(
    existing_status: &str,
    existing_request_id: &str,
    requested_id: &str,
) -> RequestDecision {
    if existing_request_id == requested_id {
        RequestDecision::Idempotent
    } else if is_active_status(existing_status) {
        RequestDecision::RejectActive
    } else {
        RequestDecision::ReplaceTerminal
    }
}

#[derive(Debug, PartialEq, Eq)]
enum ExpiryAction {
    MarkFailed,
    MarkClosed,
    Delete,
}

fn expiry_action(status: &str) -> ExpiryAction {
    match status {
        STATUS_PENDING | STATUS_CLAIMED | STATUS_PROVISIONING => ExpiryAction::MarkFailed,
        STATUS_READY => ExpiryAction::MarkClosed,
        _ => ExpiryAction::Delete,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn repeated_request_id_is_idempotent_in_every_state() {
        for status in [
            STATUS_PENDING,
            STATUS_CLAIMED,
            STATUS_PROVISIONING,
            STATUS_READY,
            STATUS_FAILED,
            STATUS_CLOSED,
        ] {
            assert_eq!(
                request_decision(status, "same-request", "same-request"),
                RequestDecision::Idempotent
            );
        }
    }

    #[test]
    fn a_different_request_cannot_replace_active_work() {
        for status in [
            STATUS_PENDING,
            STATUS_CLAIMED,
            STATUS_PROVISIONING,
            STATUS_READY,
        ] {
            assert_eq!(
                request_decision(status, "old-request", "new-request"),
                RequestDecision::RejectActive
            );
        }
    }

    #[test]
    fn a_different_request_replaces_only_terminal_work() {
        for status in [STATUS_FAILED, STATUS_CLOSED] {
            assert_eq!(
                request_decision(status, "old-request", "new-request"),
                RequestDecision::ReplaceTerminal
            );
        }
    }

    #[test]
    fn expiry_preserves_a_terminal_observation_window() {
        assert_eq!(expiry_action(STATUS_PENDING), ExpiryAction::MarkFailed);
        assert_eq!(expiry_action(STATUS_CLAIMED), ExpiryAction::MarkFailed);
        assert_eq!(expiry_action(STATUS_PROVISIONING), ExpiryAction::MarkFailed);
        assert_eq!(expiry_action(STATUS_READY), ExpiryAction::MarkClosed);
        assert_eq!(expiry_action(STATUS_FAILED), ExpiryAction::Delete);
        assert_eq!(expiry_action(STATUS_CLOSED), ExpiryAction::Delete);
    }

    #[test]
    fn service_authorization_requires_the_exact_configured_identity() {
        let owner = Identity::ZERO;
        let other = Identity::from_byte_array([1; 32]);
        assert!(require_identity(owner, owner, "denied").is_ok());
        assert_eq!(
            require_identity(other, owner, "denied"),
            Err("denied".to_string())
        );
    }

    #[test]
    fn claim_retries_renew_and_expired_provisioning_can_be_recovered() {
        assert_eq!(
            claim_decision(STATUS_PENDING, None, true, "lease-new"),
            ClaimDecision::StartOrResume
        );
        assert_eq!(
            claim_decision(STATUS_CLAIMED, Some("lease-a"), false, "lease-a"),
            ClaimDecision::Renew
        );
        assert_eq!(
            claim_decision(STATUS_PROVISIONING, Some("lease-old"), true, "lease-new"),
            ClaimDecision::StartOrResume
        );
        assert_eq!(
            claim_decision(STATUS_PROVISIONING, Some("lease-old"), false, "lease-new"),
            ClaimDecision::Reject
        );
        assert_eq!(
            claim_decision(STATUS_READY, None, true, "lease-new"),
            ClaimDecision::Reject
        );
    }

    #[test]
    fn client_request_ids_are_bounded_and_path_safe() {
        assert_eq!(
            validate_identifier("request", "abc12345-def".to_string(), 8, 64),
            Ok("abc12345-def".to_string())
        );
        assert!(validate_identifier("request", "short".to_string(), 8, 64).is_err());
        assert!(validate_identifier("request", "../../admin".to_string(), 8, 64).is_err());
        assert!(validate_identifier("request", "contains space".to_string(), 8, 64).is_err());
    }

    #[test]
    fn deadlines_must_be_future_and_bounded() {
        let now = Timestamp::UNIX_EPOCH + Duration::from_secs(10);
        assert!(validate_future_deadline(
            "deadline",
            now,
            now + Duration::from_secs(30),
            Duration::from_secs(60),
        )
        .is_ok());
        assert!(validate_future_deadline("deadline", now, now, Duration::from_secs(60)).is_err());
        assert!(validate_future_deadline(
            "deadline",
            now,
            now + Duration::from_secs(61),
            Duration::from_secs(60),
        )
        .is_err());
    }
}

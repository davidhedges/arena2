//! Persistent Arena Hub control plane.
//!
//! This module deliberately contains no gameplay simulation. It owns durable
//! player identity plus the request/assignment state consumed by the future
//! match provisioner and Unity handoff.

use std::collections::{HashMap, HashSet};
use std::time::Duration;

use serde::Deserialize;
use spacetimedb::{
    reducer, table, view, Identity, ReducerContext, ScheduleAt, SpacetimeType, Table, Timestamp,
    ViewContext,
};

const SERVICE_CONFIG_ID: u8 = 0;
const PROVISIONER_WAKEUP_ID: u8 = 0;
const MAINTENANCE_INTERVAL: Duration = Duration::from_secs(60);
const PENDING_TICKET_TTL: Duration = Duration::from_secs(2 * 60);
const TERMINAL_TICKET_RETENTION: Duration = Duration::from_secs(5 * 60);
const MAX_LEASE_DURATION: Duration = Duration::from_secs(2 * 60);
const MAX_ASSIGNMENT_DURATION: Duration = Duration::from_secs(4 * 60 * 60);

const QUEUE_UNRANKED: &str = "UNRANKED";
const FORMAT_2V2: &str = "2V2";
const ARENA_MAP_01_ID: &str = "ARENA_MAP_01";

const STATUS_PENDING: &str = "PENDING";
const STATUS_CLAIMED: &str = "CLAIMED";
const STATUS_PROVISIONING: &str = "PROVISIONING";
const STATUS_READY: &str = "READY";
const STATUS_FAILED: &str = "FAILED";
const STATUS_CLOSED: &str = "CLOSED";

const FAILURE_TICKET_EXPIRED: &str = "TICKET_EXPIRED";
const PRIMARY_DISCIPLINE_ABILITY_MINIMUM: usize = 8;
const SECONDARY_DISCIPLINE_ABILITY_MINIMUM: usize = 1;
const MAX_DISCIPLINE_LOADOUT_ABILITIES: usize = 128;

const DISCIPLINE_SUBTLETY: &str = "SUBTLETY";
const DISCIPLINE_WAR: &str = "WAR";
const DISCIPLINE_ZEAL: &str = "ZEAL";
const DISCIPLINE_PRECISION: &str = "PRECISION";
const DISCIPLINE_RUIN: &str = "RUIN";

const DEFAULT_HUB_PRIMARY_DISCIPLINE: &str = DISCIPLINE_WAR;
const DEFAULT_HUB_SECONDARY_DISCIPLINE_1: &str = DISCIPLINE_SUBTLETY;
const DEFAULT_HUB_SECONDARY_DISCIPLINE_2: &str = DISCIPLINE_RUIN;
const DEFAULT_HUB_ARMOR_SET: &str = "PEASANT";

const EQUIP_SLOT_MAIN_HAND: &str = "MAIN_HAND";
const EQUIP_SLOT_OFF_HAND: &str = "OFF_HAND";
const WEAPON_KIND_TWO_HAND_SWORD: &str = "TWO_HAND_SWORD";
const WEAPON_KIND_ONE_HAND_SWORD: &str = "ONE_HAND_SWORD";
const WEAPON_KIND_TWO_HAND_AXE: &str = "TWO_HAND_AXE";
const WEAPON_KIND_ONE_HAND_AXE: &str = "ONE_HAND_AXE";
const WEAPON_KIND_TWO_HAND_HAMMER: &str = "TWO_HAND_HAMMER";
const WEAPON_KIND_ONE_HAND_HAMMER: &str = "ONE_HAND_HAMMER";
const WEAPON_KIND_ONE_HAND_FIST: &str = "ONE_HAND_FIST";
const WEAPON_KIND_POLEARM: &str = "POLEARM";
const WEAPON_KIND_SHIELD: &str = "SHIELD";
const WEAPON_KIND_DAGGER_PAIR: &str = "DAGGER_PAIR";
const WEAPON_KIND_BOW: &str = "BOW";
const HAND_REQUIREMENT_ONE_HAND: &str = "ONE_HAND";
const HAND_REQUIREMENT_TWO_HAND: &str = "TWO_HAND";
const HAND_REQUIREMENT_OFF_HAND: &str = "OFF_HAND";

const PROGRESSION_CATALOG_JSON: &str =
    include_str!("../../server/src/progression_catalog.shared.json");
const WEAPON_APPEARANCE_CATALOG_JSON: &str =
    include_str!("../../Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json");
const HUB_CATALOG_HASH_OFFSET: u64 = 0xcbf29ce484222325;
const HUB_CATALOG_HASH_PRIME: u64 = 0x100000001b3;
const PROGRESSION_CATALOG_HASH: u64 =
    extend_catalog_hash(HUB_CATALOG_HASH_OFFSET, PROGRESSION_CATALOG_JSON.as_bytes());
const WEAPON_APPEARANCE_CATALOG_HASH: u64 = extend_catalog_hash(
    PROGRESSION_CATALOG_HASH,
    WEAPON_APPEARANCE_CATALOG_JSON.as_bytes(),
);

#[table(accessor = hub_player)]
pub struct HubPlayer {
    #[primary_key]
    pub identity: Identity,
    pub display_name: String,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
}

/// Durable build selection. Item instances and simulation state deliberately
/// stay out of the Hub; equipment choices are stable authored definition ids
/// that the disposable match resolves to fresh item instances.
#[table(accessor = hub_player_loadout)]
#[derive(Clone)]
pub struct HubPlayerLoadout {
    #[primary_key]
    pub owner: Identity,
    pub primary_discipline_id: String,
    pub secondary_discipline_id_1: String,
    pub secondary_discipline_id_2: String,
    pub selected_ability_ids: Vec<String>,
    pub armor_set_id: String,
    pub revision: u64,
    pub updated_at: Timestamp,
    #[default(None::<String>)]
    pub main_hand_item_def_id: Option<String>,
    #[default(None::<String>)]
    pub off_hand_item_def_id: Option<String>,
    #[default(None::<String>)]
    pub main_hand_color_id: Option<String>,
    #[default(None::<String>)]
    pub off_hand_color_id: Option<String>,
}

/// Prevents every Hub connection from reparsing and rescanning the authored
/// catalogs. The revision is derived from the embedded JSON and armor specs.
#[table(accessor = hub_catalog_state)]
struct HubCatalogState {
    #[primary_key]
    singleton_id: u8,
    revision: u64,
}

/// Frozen at ticket creation so later Hub edits cannot alter a match that is
/// already being provisioned.
#[table(accessor = match_player_loadout_snapshot)]
pub struct MatchPlayerLoadoutSnapshot {
    #[primary_key]
    pub ticket_id: String,
    #[index(btree)]
    pub player_identity: Identity,
    pub primary_discipline_id: String,
    pub secondary_discipline_id_1: String,
    pub secondary_discipline_id_2: String,
    pub selected_ability_ids: Vec<String>,
    pub armor_set_id: String,
    pub loadout_revision: u64,
    pub captured_at: Timestamp,
    #[default(None::<String>)]
    pub main_hand_item_def_id: Option<String>,
    #[default(None::<String>)]
    pub off_hand_item_def_id: Option<String>,
    #[default(None::<String>)]
    pub main_hand_color_id: Option<String>,
    #[default(None::<String>)]
    pub off_hand_color_id: Option<String>,
}

/// Display-only Hub copy of the source-controlled combat catalog. Match
/// databases remain authoritative for combat behavior and tuning.
#[table(accessor = hub_combat_discipline_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubCombatDisciplineDefinition {
    #[primary_key]
    pub discipline_id: String,
    pub discipline_kind: String,
    pub combat_profile_id: String,
    pub display_name: String,
    pub sort_order: u32,
}

#[table(accessor = hub_ability_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubAbilityDefinition {
    #[primary_key]
    pub ability_id: String,
    #[index(btree)]
    pub discipline_id: String,
    pub display_name: String,
    pub resource_kind: String,
    pub resource_cost: f32,
    pub ability_tags: String,
    pub description: String,
    pub sort_order: u32,
}

#[table(accessor = hub_armor_set_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubArmorSetDefinition {
    #[primary_key]
    pub armor_set_id: String,
    pub display_name: String,
    pub armor_tier: String,
    pub physical_resistance: f32,
    pub magical_resistance: f32,
    pub move_speed_modifier: f32,
    pub cast_speed_modifier: f32,
    pub piece_count: u32,
    pub sort_order: u32,
}

/// Curated projection of the N-Hance weapon models that have complete Arena
/// item, icon, animation-role, and attachment support.
#[table(accessor = hub_weapon_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubWeaponDefinition {
    #[primary_key]
    pub item_def_id: String,
    pub display_name: String,
    pub icon_id: String,
    pub weapon_kind: String,
    pub hand_requirement: String,
    pub equip_slot: String,
    #[index(btree)]
    pub primary_discipline_id: String,
    pub sort_order: u32,
}

/// Appearance palettes are separate from gameplay item definitions: one row
/// per authored model family/color pair, keyed independently for subscription.
#[table(accessor = hub_weapon_color_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubWeaponColorDefinition {
    #[primary_key]
    pub weapon_color_key: String,
    #[index(btree)]
    pub item_def_id: String,
    pub color_id: String,
    pub display_name: String,
    pub color_hex: String,
    pub sort_order: u32,
}

#[table(accessor = hub_service_config)]
pub struct HubServiceConfig {
    #[primary_key]
    pub singleton_id: u8,
    pub module_owner: Identity,
    pub provisioner_identity: Identity,
    pub updated_at: Timestamp,
}

/// Private state behind the provisioner-only wakeup view.
#[table(accessor = provisioner_wakeup_state)]
pub struct ProvisionerWakeupState {
    #[primary_key]
    pub singleton_id: u8,
    pub sequence: u64,
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
    pub map_id: String,
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

/// Public projection of only the caller's durable build selection.
#[derive(SpacetimeType)]
pub struct MyHubLoadout {
    pub owner: Identity,
    pub primary_discipline_id: String,
    pub secondary_discipline_id_1: String,
    pub secondary_discipline_id_2: String,
    pub selected_ability_ids: Vec<String>,
    pub armor_set_id: String,
    pub main_hand_item_def_id: String,
    pub off_hand_item_def_id: String,
    pub main_hand_color_id: String,
    pub off_hand_color_id: String,
    pub revision: u64,
    pub updated_at: Timestamp,
}

/// Carries no ticket/player data; changing `sequence` only wakes the service.
#[derive(SpacetimeType)]
pub struct ProvisionerWakeup {
    pub sequence: u64,
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
    pub map_id: Option<String>,
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

#[view(accessor = my_hub_loadout, public)]
pub fn my_hub_loadout(ctx: &ViewContext) -> Option<MyHubLoadout> {
    ctx.db
        .hub_player_loadout()
        .owner()
        .find(ctx.sender())
        .map(|loadout| MyHubLoadout {
            owner: loadout.owner,
            primary_discipline_id: loadout.primary_discipline_id,
            secondary_discipline_id_1: loadout.secondary_discipline_id_1,
            secondary_discipline_id_2: loadout.secondary_discipline_id_2,
            selected_ability_ids: loadout.selected_ability_ids,
            armor_set_id: loadout.armor_set_id,
            main_hand_item_def_id: loadout.main_hand_item_def_id.unwrap_or_default(),
            off_hand_item_def_id: loadout.off_hand_item_def_id.unwrap_or_default(),
            main_hand_color_id: loadout.main_hand_color_id.unwrap_or_default(),
            off_hand_color_id: loadout.off_hand_color_id.unwrap_or_default(),
            revision: loadout.revision,
            updated_at: loadout.updated_at,
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
        map_id: assignment.as_ref().map(|row| row.map_id.clone()),
        ready_at: assignment.as_ref().map(|row| row.ready_at),
        assignment_expires_at: assignment.map(|row| row.expires_at),
    })
}

/// Only the configured provisioner can observe match-work wakeups.
#[view(accessor = provisioner_wakeup, public)]
pub fn provisioner_wakeup(ctx: &ViewContext) -> Option<ProvisionerWakeup> {
    let config = ctx
        .db
        .hub_service_config()
        .singleton_id()
        .find(SERVICE_CONFIG_ID)?;
    if ctx.sender() != config.provisioner_identity {
        return None;
    }
    ctx.db
        .provisioner_wakeup_state()
        .singleton_id()
        .find(PROVISIONER_WAKEUP_ID)
        .map(|state| ProvisionerWakeup {
            sequence: state.sequence,
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
    ctx.db
        .provisioner_wakeup_state()
        .insert(ProvisionerWakeupState {
            singleton_id: PROVISIONER_WAKEUP_ID,
            sequence: 0,
        });
    ctx.db.hub_maintenance_timer().insert(HubMaintenanceTimer {
        scheduled_id: 0,
        scheduled_at: ScheduleAt::Interval(MAINTENANCE_INTERVAL.into()),
    });
    ensure_hub_loadout_catalogs(ctx)?;
    log::info!(
        "[HUB_INIT] Persistent Hub initialized; maintenance interval={}s; no gameplay tick",
        MAINTENANCE_INTERVAL.as_secs()
    );
    Ok(())
}

#[reducer(client_connected)]
pub fn client_connected(ctx: &ReducerContext) -> Result<(), String> {
    ensure_hub_player(ctx, ctx.sender());
    // Reconcile display-only authored data after data-preserving publishes,
    // whose existing databases do not rerun init.
    ensure_hub_loadout_catalogs(ctx)?;
    ensure_default_hub_player_loadout(ctx, ctx.sender())?;
    Ok(())
}

#[reducer]
pub fn save_hub_discipline_loadout(
    ctx: &ReducerContext,
    primary_discipline_id: String,
    secondary_discipline_id_1: String,
    secondary_discipline_id_2: String,
    selected_ability_ids: Vec<String>,
) -> Result<(), String> {
    ensure_hub_player(ctx, ctx.sender());
    let (primary, secondary_1, secondary_2, abilities) = validate_hub_discipline_loadout(
        ctx,
        primary_discipline_id,
        secondary_discipline_id_1,
        secondary_discipline_id_2,
        selected_ability_ids,
    )?;
    let existing = ctx.db.hub_player_loadout().owner().find(ctx.sender());
    let (main_hand_item_def_id, main_hand_color_id, off_hand_item_def_id, off_hand_color_id) =
        preserve_or_default_weapon_loadout(primary.as_str(), existing.as_ref());
    let row = HubPlayerLoadout {
        owner: ctx.sender(),
        primary_discipline_id: primary,
        secondary_discipline_id_1: secondary_1,
        secondary_discipline_id_2: secondary_2,
        selected_ability_ids: abilities,
        armor_set_id: existing
            .as_ref()
            .map(|loadout| loadout.armor_set_id.clone())
            .unwrap_or_default(),
        main_hand_item_def_id: Some(main_hand_item_def_id),
        off_hand_item_def_id: Some(off_hand_item_def_id),
        main_hand_color_id: Some(main_hand_color_id),
        off_hand_color_id: Some(off_hand_color_id),
        revision: next_loadout_revision(existing.as_ref().map(|loadout| loadout.revision)),
        updated_at: ctx.timestamp,
    };
    if existing.is_some() {
        ctx.db.hub_player_loadout().owner().update(row);
    } else {
        ctx.db.hub_player_loadout().insert(row);
    }
    Ok(())
}

#[reducer]
pub fn save_hub_armor_set(ctx: &ReducerContext, armor_set_id: String) -> Result<(), String> {
    ensure_hub_player(ctx, ctx.sender());
    let armor_set_id = normalize_authored_id(armor_set_id.as_str());
    if ctx
        .db
        .hub_armor_set_definition()
        .armor_set_id()
        .find(armor_set_id.clone())
        .is_none()
    {
        return Err(format!("unknown armor set '{armor_set_id}'"));
    }
    let existing = ctx.db.hub_player_loadout().owner().find(ctx.sender());
    let row = HubPlayerLoadout {
        owner: ctx.sender(),
        primary_discipline_id: existing
            .as_ref()
            .map(|loadout| loadout.primary_discipline_id.clone())
            .unwrap_or_default(),
        secondary_discipline_id_1: existing
            .as_ref()
            .map(|loadout| loadout.secondary_discipline_id_1.clone())
            .unwrap_or_default(),
        secondary_discipline_id_2: existing
            .as_ref()
            .map(|loadout| loadout.secondary_discipline_id_2.clone())
            .unwrap_or_default(),
        selected_ability_ids: existing
            .as_ref()
            .map(|loadout| loadout.selected_ability_ids.clone())
            .unwrap_or_default(),
        armor_set_id,
        main_hand_item_def_id: existing
            .as_ref()
            .and_then(|loadout| loadout.main_hand_item_def_id.clone()),
        off_hand_item_def_id: existing
            .as_ref()
            .and_then(|loadout| loadout.off_hand_item_def_id.clone()),
        main_hand_color_id: existing
            .as_ref()
            .and_then(|loadout| loadout.main_hand_color_id.clone()),
        off_hand_color_id: existing
            .as_ref()
            .and_then(|loadout| loadout.off_hand_color_id.clone()),
        revision: next_loadout_revision(existing.as_ref().map(|loadout| loadout.revision)),
        updated_at: ctx.timestamp,
    };
    if existing.is_some() {
        ctx.db.hub_player_loadout().owner().update(row);
    } else {
        ctx.db.hub_player_loadout().insert(row);
    }
    Ok(())
}

#[reducer]
pub fn save_hub_weapon_loadout(
    ctx: &ReducerContext,
    main_hand_item_def_id: String,
    main_hand_color_id: String,
    off_hand_item_def_id: String,
    off_hand_color_id: String,
) -> Result<(), String> {
    ensure_hub_player(ctx, ctx.sender());
    let existing = ctx
        .db
        .hub_player_loadout()
        .owner()
        .find(ctx.sender())
        .ok_or_else(|| "save a primary discipline before equipping weapons".to_string())?;
    let (main_hand_item_def_id, main_hand_color_id, off_hand_item_def_id, off_hand_color_id) =
        validate_hub_weapon_loadout(
            existing.primary_discipline_id.as_str(),
            main_hand_item_def_id.as_str(),
            main_hand_color_id.as_str(),
            off_hand_item_def_id.as_str(),
            off_hand_color_id.as_str(),
        )?;
    if existing.main_hand_item_def_id.as_deref() == Some(main_hand_item_def_id.as_str())
        && existing.off_hand_item_def_id.as_deref() == Some(off_hand_item_def_id.as_str())
        && existing.main_hand_color_id.as_deref() == Some(main_hand_color_id.as_str())
        && existing.off_hand_color_id.as_deref() == Some(off_hand_color_id.as_str())
    {
        return Ok(());
    }

    let row = HubPlayerLoadout {
        owner: existing.owner,
        primary_discipline_id: existing.primary_discipline_id,
        secondary_discipline_id_1: existing.secondary_discipline_id_1,
        secondary_discipline_id_2: existing.secondary_discipline_id_2,
        selected_ability_ids: existing.selected_ability_ids,
        armor_set_id: existing.armor_set_id,
        main_hand_item_def_id: Some(main_hand_item_def_id),
        off_hand_item_def_id: Some(off_hand_item_def_id),
        main_hand_color_id: Some(main_hand_color_id),
        off_hand_color_id: Some(off_hand_color_id),
        revision: next_loadout_revision(Some(existing.revision)),
        updated_at: ctx.timestamp,
    };
    ctx.db.hub_player_loadout().owner().update(row);
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
    ensure_hub_loadout_catalogs(ctx)?;
    ensure_default_hub_player_loadout(ctx, player_identity)?;

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
                delete_loadout_snapshot_for_ticket(ctx, existing.ticket_id.as_str());
                ctx.db.match_ticket().ticket_id().delete(existing.ticket_id);
            }
        }
    }

    let ticket_id = ticket_id_for(player_identity, client_request_id.as_str());
    ctx.db.match_ticket().insert(MatchTicket {
        ticket_id: ticket_id.clone(),
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
    freeze_player_loadout_for_ticket(ctx, ticket_id, player_identity);
    bump_provisioner_wakeup(ctx);
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
    map_id: String,
    assignment_expires_at: Timestamp,
) -> Result<(), String> {
    require_provisioner(ctx)?;
    let match_id = validate_identifier("match id", match_id, 8, 96)?;
    let database_identity = validate_identifier("database identity", database_identity, 8, 128)?;
    let match_build_id = validate_identifier("match build id", match_build_id, 1, 96)?;
    let map_id = validate_identifier("map id", map_id, 1, 64)?;
    if map_id != ARENA_MAP_01_ID {
        return Err(format!("Unsupported authored arena map {map_id}"));
    }
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
                    && assignment.map_id == map_id
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
        map_id,
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
                delete_loadout_snapshot_for_ticket(ctx, ticket.ticket_id.as_str());
                ctx.db.match_ticket().ticket_id().delete(ticket.ticket_id);
            }
        }
    }
    Ok(())
}

#[derive(Deserialize)]
struct HubProgressionCatalogFile {
    combat_disciplines: Vec<HubDisciplineAuthoring>,
    abilities: Vec<HubAbilityAuthoring>,
    #[serde(default)]
    action_presentations: Vec<HubActionPresentationAuthoring>,
}

#[derive(Deserialize)]
struct HubDisciplineAuthoring {
    discipline_id: String,
    discipline_kind: String,
    combat_profile_id: String,
    display_name: String,
    sort_order: u32,
}

#[derive(Deserialize)]
struct HubAbilityAuthoring {
    ability_id: String,
    actor_scope: String,
    #[serde(default)]
    discipline_id: String,
    display_name: String,
    resource_kind: String,
    #[serde(default)]
    resource_cost: f32,
    #[serde(default)]
    ability_tags: Vec<String>,
    sort_order: u32,
}

#[derive(Deserialize)]
struct HubActionPresentationAuthoring {
    presentation_kind: String,
    presentation_id: String,
    #[serde(default)]
    description: String,
}

#[derive(Deserialize)]
struct HubWeaponAppearanceCatalogFile {
    schema_version: u32,
    colors: Vec<HubWeaponColorAuthoring>,
    families: Vec<HubWeaponFamilyAuthoring>,
}

#[derive(Deserialize)]
struct HubWeaponColorAuthoring {
    color_id: String,
    display_name: String,
    hex: String,
}

#[derive(Clone, Deserialize)]
struct HubWeaponFamilyAuthoring {
    item_def_id: String,
    display_name: String,
    icon_id: String,
    weapon_kind: String,
    hand_requirement: String,
    equip_slot: String,
    primary_discipline_id: String,
    sort_order: u32,
    default_color_id: String,
    variants: Vec<HubWeaponVariantAuthoring>,
}

#[derive(Clone, Deserialize)]
struct HubWeaponVariantAuthoring {
    color_id: String,
}

#[derive(Clone, Copy)]
struct HubArmorSetSpec {
    armor_set_id: &'static str,
    display_name: &'static str,
    armor_tier: &'static str,
    piece_count: u32,
    sort_order: u32,
}

const fn hub_armor_set(
    armor_set_id: &'static str,
    display_name: &'static str,
    armor_tier: &'static str,
    piece_count: u32,
    sort_order: u32,
) -> HubArmorSetSpec {
    HubArmorSetSpec {
        armor_set_id,
        display_name,
        armor_tier,
        piece_count,
        sort_order,
    }
}

const HUB_ARMOR_SET_SPECS: &[HubArmorSetSpec] = &[
    hub_armor_set("PEASANT", "Peasant Attire", "LIGHT", 4, 10),
    hub_armor_set("APPRENTICE", "Apprentice Vestments", "LIGHT", 7, 20),
    hub_armor_set("LEATHER", "Ranger Leathers", "MEDIUM", 7, 30),
    hub_armor_set("IRON", "Iron Warplate", "HEAVY", 7, 40),
    hub_armor_set("GILDED", "Gilded Warplate", "HEAVY", 7, 50),
    hub_armor_set("FMAGE_BL", "Blue Mage Vestments", "LIGHT", 7, 70),
    hub_armor_set("FMAGE_GN", "Green Mage Vestments", "LIGHT", 7, 71),
    hub_armor_set("FMAGE_RD", "Red Mage Vestments", "LIGHT", 7, 72),
    hub_armor_set("WARLOCK_GN", "Green Warlock Vestments", "LIGHT", 7, 80),
    hub_armor_set("WARLOCK_PE", "Purple Warlock Vestments", "LIGHT", 7, 81),
    hub_armor_set("WARLOCK_VT", "Violet Warlock Vestments", "LIGHT", 7, 82),
    hub_armor_set("WIZARD_PE", "Purple Wizard Vestments", "LIGHT", 7, 90),
    hub_armor_set("WIZARD_VT", "Violet Wizard Vestments", "LIGHT", 7, 91),
    hub_armor_set("BARBARIAN_BL", "Blue Barbarian Leathers", "MEDIUM", 7, 200),
    hub_armor_set("BARBARIAN_GN", "Green Barbarian Leathers", "MEDIUM", 7, 201),
    hub_armor_set("BARBARIAN_RD", "Red Barbarian Leathers", "MEDIUM", 7, 202),
    hub_armor_set("HUNTER_BL", "Blue Hunter Leathers", "MEDIUM", 7, 210),
    hub_armor_set("HUNTER_GN", "Green Hunter Leathers", "MEDIUM", 7, 211),
    hub_armor_set("HUNTER_PE", "Purple Hunter Leathers", "MEDIUM", 7, 212),
    hub_armor_set("HUNTER_RD", "Red Hunter Leathers", "MEDIUM", 7, 213),
    hub_armor_set(
        "NRANGER_BL",
        "Blue Northern Ranger Leathers",
        "MEDIUM",
        7,
        220,
    ),
    hub_armor_set(
        "NRANGER_RD",
        "Red Northern Ranger Leathers",
        "MEDIUM",
        7,
        221,
    ),
    hub_armor_set("RANGER_GN", "Green Ranger Leathers", "MEDIUM", 7, 230),
    hub_armor_set("RANGER_PE", "Purple Ranger Leathers", "MEDIUM", 7, 231),
    hub_armor_set("RANGER_RD", "Red Ranger Leathers", "MEDIUM", 7, 232),
    hub_armor_set("REAPER_BL", "Blue Reaper Leathers", "MEDIUM", 7, 240),
    hub_armor_set("REAPER_CN", "Cyan Reaper Leathers", "MEDIUM", 7, 241),
    hub_armor_set("REAPER_GN", "Green Reaper Leathers", "MEDIUM", 7, 242),
    hub_armor_set("ROGUE_BL", "Blue Rogue Leathers", "MEDIUM", 7, 250),
    hub_armor_set("ROGUE_GN", "Green Rogue Leathers", "MEDIUM", 7, 251),
    hub_armor_set("ROGUE_RD", "Red Rogue Leathers", "MEDIUM", 7, 252),
    hub_armor_set("DK_BL", "Blue Death Knight Plate", "HEAVY", 7, 400),
    hub_armor_set("DK_GN", "Green Death Knight Plate", "HEAVY", 7, 401),
    hub_armor_set("DK_RD", "Red Death Knight Plate", "HEAVY", 7, 402),
    hub_armor_set("DUNGPLATE_BL", "Blue Dungeon Plate", "HEAVY", 7, 410),
    hub_armor_set("DUNGPLATE_PE", "Purple Dungeon Plate", "HEAVY", 7, 411),
    hub_armor_set("DUNGPLATE_RD", "Red Dungeon Plate", "HEAVY", 7, 412),
    hub_armor_set("NWARRIOR_RD", "Red Northern Warplate", "HEAVY", 7, 420),
    hub_armor_set("PALADIN_BL", "Blue Paladin Plate", "HEAVY", 7, 430),
    hub_armor_set("PALADIN_GN", "Green Paladin Plate", "HEAVY", 7, 431),
    hub_armor_set("PALADIN_GR", "Gray Paladin Plate", "HEAVY", 7, 432),
    hub_armor_set("PALADIN_RD", "Red Paladin Plate", "HEAVY", 7, 433),
    hub_armor_set("WARRIOR_GN", "Green Warrior Plate", "HEAVY", 7, 440),
    hub_armor_set("WARRIOR_PE", "Purple Warrior Plate", "HEAVY", 7, 441),
    hub_armor_set("WARRIOR_RD", "Red Warrior Plate", "HEAVY", 7, 442),
];

fn ensure_hub_loadout_catalogs(ctx: &ReducerContext) -> Result<(), String> {
    let revision = hub_catalog_revision();
    if ctx
        .db
        .hub_catalog_state()
        .singleton_id()
        .find(0)
        .is_some_and(|state| state.revision == revision)
    {
        return Ok(());
    }

    sync_hub_loadout_catalogs(ctx)?;
    let state = HubCatalogState {
        singleton_id: 0,
        revision,
    };
    if ctx.db.hub_catalog_state().singleton_id().find(0).is_some() {
        ctx.db.hub_catalog_state().singleton_id().update(state);
    } else {
        ctx.db.hub_catalog_state().insert(state);
    }
    Ok(())
}

fn sync_hub_loadout_catalogs(ctx: &ReducerContext) -> Result<(), String> {
    let authored: HubProgressionCatalogFile = serde_json::from_str(PROGRESSION_CATALOG_JSON)
        .map_err(|error| format!("Hub progression catalog is invalid: {error}"))?;
    let weapon_catalog = parse_weapon_appearance_catalog()?;
    let descriptions: HashMap<String, String> = authored
        .action_presentations
        .into_iter()
        .filter(|presentation| {
            normalize_authored_id(presentation.presentation_kind.as_str()) == "ABILITY"
        })
        .map(|presentation| {
            (
                normalize_authored_id(presentation.presentation_id.as_str()),
                presentation.description.trim().to_string(),
            )
        })
        .collect();

    let discipline_rows: Vec<HubCombatDisciplineDefinition> = authored
        .combat_disciplines
        .into_iter()
        .map(|discipline| HubCombatDisciplineDefinition {
            discipline_id: normalize_authored_id(discipline.discipline_id.as_str()),
            discipline_kind: normalize_authored_id(discipline.discipline_kind.as_str()),
            combat_profile_id: normalize_authored_id(discipline.combat_profile_id.as_str()),
            display_name: discipline.display_name.trim().to_string(),
            sort_order: discipline.sort_order,
        })
        .collect();
    let discipline_ids: HashSet<String> = discipline_rows
        .iter()
        .map(|row| row.discipline_id.clone())
        .collect();
    for row in discipline_rows {
        match ctx
            .db
            .hub_combat_discipline_definition()
            .discipline_id()
            .find(row.discipline_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db
                    .hub_combat_discipline_definition()
                    .discipline_id()
                    .update(row);
            }
            None => {
                ctx.db.hub_combat_discipline_definition().insert(row);
            }
        }
    }
    let stale_discipline_ids: Vec<String> = ctx
        .db
        .hub_combat_discipline_definition()
        .iter()
        .map(|row| row.discipline_id)
        .filter(|id| !discipline_ids.contains(id))
        .collect();
    for id in stale_discipline_ids {
        ctx.db
            .hub_combat_discipline_definition()
            .discipline_id()
            .delete(id);
    }

    let ability_rows: Vec<HubAbilityDefinition> = authored
        .abilities
        .into_iter()
        .filter(|ability| normalize_authored_id(ability.actor_scope.as_str()) == "PLAYER")
        .filter(|ability| {
            ability.ability_tags.iter().any(|tag| {
                matches!(
                    normalize_authored_id(tag.as_str()).as_str(),
                    "ACTION_BAR_ACTION" | "PASSIVE"
                )
            })
        })
        .map(|ability| {
            let ability_id = normalize_authored_id(ability.ability_id.as_str());
            HubAbilityDefinition {
                description: descriptions.get(&ability_id).cloned().unwrap_or_default(),
                ability_id,
                discipline_id: normalize_authored_id(ability.discipline_id.as_str()),
                display_name: ability.display_name.trim().to_string(),
                resource_kind: normalize_authored_id(ability.resource_kind.as_str()),
                resource_cost: ability.resource_cost,
                ability_tags: ability
                    .ability_tags
                    .into_iter()
                    .map(|tag| normalize_authored_id(tag.as_str()))
                    .collect::<Vec<_>>()
                    .join(","),
                sort_order: ability.sort_order,
            }
        })
        .collect();
    let ability_ids: HashSet<String> = ability_rows
        .iter()
        .map(|row| row.ability_id.clone())
        .collect();
    for row in ability_rows {
        match ctx
            .db
            .hub_ability_definition()
            .ability_id()
            .find(row.ability_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db.hub_ability_definition().ability_id().update(row);
            }
            None => {
                ctx.db.hub_ability_definition().insert(row);
            }
        }
    }
    let stale_ability_ids: Vec<String> = ctx
        .db
        .hub_ability_definition()
        .iter()
        .map(|row| row.ability_id)
        .filter(|id| !ability_ids.contains(id))
        .collect();
    for id in stale_ability_ids {
        ctx.db.hub_ability_definition().ability_id().delete(id);
    }

    let armor_rows: Vec<HubArmorSetDefinition> = HUB_ARMOR_SET_SPECS
        .iter()
        .map(|spec| {
            let (resistance, move_speed_modifier, cast_speed_modifier) = match spec.armor_tier {
                "MEDIUM" => (0.20, 0.0, 0.0),
                "HEAVY" => (0.40, -0.10, -0.20),
                _ => (0.0, 0.0, 0.0),
            };
            HubArmorSetDefinition {
                armor_set_id: spec.armor_set_id.to_string(),
                display_name: spec.display_name.to_string(),
                armor_tier: spec.armor_tier.to_string(),
                physical_resistance: resistance,
                magical_resistance: resistance,
                move_speed_modifier,
                cast_speed_modifier,
                piece_count: spec.piece_count,
                sort_order: spec.sort_order,
            }
        })
        .collect();
    let armor_ids: HashSet<String> = armor_rows
        .iter()
        .map(|row| row.armor_set_id.clone())
        .collect();
    for row in armor_rows {
        match ctx
            .db
            .hub_armor_set_definition()
            .armor_set_id()
            .find(row.armor_set_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db.hub_armor_set_definition().armor_set_id().update(row);
            }
            None => {
                ctx.db.hub_armor_set_definition().insert(row);
            }
        }
    }
    let stale_armor_ids: Vec<String> = ctx
        .db
        .hub_armor_set_definition()
        .iter()
        .map(|row| row.armor_set_id)
        .filter(|id| !armor_ids.contains(id))
        .collect();
    for id in stale_armor_ids {
        ctx.db.hub_armor_set_definition().armor_set_id().delete(id);
    }

    let weapon_rows: Vec<HubWeaponDefinition> = weapon_catalog
        .families
        .iter()
        .map(|spec| HubWeaponDefinition {
            item_def_id: normalize_authored_id(spec.item_def_id.as_str()),
            display_name: spec.display_name.trim().to_string(),
            icon_id: spec.icon_id.trim().to_string(),
            weapon_kind: normalize_authored_id(spec.weapon_kind.as_str()),
            hand_requirement: normalize_authored_id(spec.hand_requirement.as_str()),
            equip_slot: normalize_authored_id(spec.equip_slot.as_str()),
            primary_discipline_id: normalize_authored_id(spec.primary_discipline_id.as_str()),
            sort_order: spec.sort_order,
        })
        .collect();
    let weapon_ids: HashSet<String> = weapon_rows
        .iter()
        .map(|row| row.item_def_id.clone())
        .collect();
    for row in weapon_rows {
        match ctx
            .db
            .hub_weapon_definition()
            .item_def_id()
            .find(row.item_def_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db.hub_weapon_definition().item_def_id().update(row);
            }
            None => {
                ctx.db.hub_weapon_definition().insert(row);
            }
        }
    }
    let stale_weapon_ids: Vec<String> = ctx
        .db
        .hub_weapon_definition()
        .iter()
        .map(|row| row.item_def_id)
        .filter(|id| !weapon_ids.contains(id))
        .collect();
    for id in stale_weapon_ids {
        ctx.db.hub_weapon_definition().item_def_id().delete(id);
    }

    let colors_by_id: HashMap<String, &HubWeaponColorAuthoring> = weapon_catalog
        .colors
        .iter()
        .map(|color| (normalize_authored_id(color.color_id.as_str()), color))
        .collect();
    let mut color_rows = Vec::new();
    for family in &weapon_catalog.families {
        let item_def_id = normalize_authored_id(family.item_def_id.as_str());
        for (sort_order, variant) in family.variants.iter().enumerate() {
            let color_id = normalize_authored_id(variant.color_id.as_str());
            let color = colors_by_id.get(&color_id).ok_or_else(|| {
                format!("weapon family '{item_def_id}' references unknown color '{color_id}'")
            })?;
            color_rows.push(HubWeaponColorDefinition {
                weapon_color_key: weapon_color_key(item_def_id.as_str(), color_id.as_str()),
                item_def_id: item_def_id.clone(),
                color_id,
                display_name: color.display_name.trim().to_string(),
                color_hex: color.hex.trim().to_string(),
                sort_order: sort_order as u32,
            });
        }
    }
    let color_keys: HashSet<String> = color_rows
        .iter()
        .map(|row| row.weapon_color_key.clone())
        .collect();
    for row in color_rows {
        match ctx
            .db
            .hub_weapon_color_definition()
            .weapon_color_key()
            .find(row.weapon_color_key.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db
                    .hub_weapon_color_definition()
                    .weapon_color_key()
                    .update(row);
            }
            None => {
                ctx.db.hub_weapon_color_definition().insert(row);
            }
        }
    }
    let stale_color_keys: Vec<String> = ctx
        .db
        .hub_weapon_color_definition()
        .iter()
        .map(|row| row.weapon_color_key)
        .filter(|key| !color_keys.contains(key))
        .collect();
    for key in stale_color_keys {
        ctx.db
            .hub_weapon_color_definition()
            .weapon_color_key()
            .delete(key);
    }
    Ok(())
}

const fn extend_catalog_hash(mut hash: u64, bytes: &[u8]) -> u64 {
    let mut index = 0;
    while index < bytes.len() {
        hash ^= bytes[index] as u64;
        hash = hash.wrapping_mul(HUB_CATALOG_HASH_PRIME);
        index += 1;
    }
    hash
}

fn hub_catalog_revision() -> u64 {
    let armor_hash =
        HUB_ARMOR_SET_SPECS
            .iter()
            .fold(WEAPON_APPEARANCE_CATALOG_HASH, |hash, spec| {
                let hash = extend_catalog_hash(hash, spec.armor_set_id.as_bytes());
                let hash = extend_catalog_hash(hash, spec.display_name.as_bytes());
                let hash = extend_catalog_hash(hash, spec.armor_tier.as_bytes());
                hash ^ ((spec.piece_count as u64) << 32) ^ spec.sort_order as u64
            });
    armor_hash
}

fn parse_weapon_appearance_catalog() -> Result<HubWeaponAppearanceCatalogFile, String> {
    let catalog: HubWeaponAppearanceCatalogFile =
        serde_json::from_str(WEAPON_APPEARANCE_CATALOG_JSON)
            .map_err(|error| format!("weapon appearance catalog is invalid: {error}"))?;
    if catalog.schema_version != 1 {
        return Err(format!(
            "unsupported weapon appearance schema version {}",
            catalog.schema_version
        ));
    }
    Ok(catalog)
}

fn weapon_color_key(item_def_id: &str, color_id: &str) -> String {
    format!(
        "{}:{}",
        normalize_authored_id(item_def_id),
        normalize_authored_id(color_id)
    )
}

fn weapon_spec_contract_is_valid(spec: &HubWeaponFamilyAuthoring) -> bool {
    match normalize_authored_id(spec.primary_discipline_id.as_str()).as_str() {
        DISCIPLINE_SUBTLETY => {
            normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_DAGGER_PAIR
                && normalize_authored_id(spec.hand_requirement.as_str())
                    == HAND_REQUIREMENT_TWO_HAND
        }
        DISCIPLINE_WAR => {
            normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && matches!(
                    normalize_authored_id(spec.weapon_kind.as_str()).as_str(),
                    WEAPON_KIND_TWO_HAND_SWORD
                        | WEAPON_KIND_TWO_HAND_AXE
                        | WEAPON_KIND_TWO_HAND_HAMMER
                        | WEAPON_KIND_POLEARM
                )
                && normalize_authored_id(spec.hand_requirement.as_str())
                    == HAND_REQUIREMENT_TWO_HAND
        }
        DISCIPLINE_ZEAL => match normalize_authored_id(spec.equip_slot.as_str()).as_str() {
            EQUIP_SLOT_MAIN_HAND => {
                matches!(
                    normalize_authored_id(spec.weapon_kind.as_str()).as_str(),
                    WEAPON_KIND_ONE_HAND_SWORD
                        | WEAPON_KIND_ONE_HAND_AXE
                        | WEAPON_KIND_ONE_HAND_HAMMER
                        | WEAPON_KIND_ONE_HAND_FIST
                ) && normalize_authored_id(spec.hand_requirement.as_str())
                    == HAND_REQUIREMENT_ONE_HAND
            }
            EQUIP_SLOT_OFF_HAND => {
                normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_SHIELD
                    && normalize_authored_id(spec.hand_requirement.as_str())
                        == HAND_REQUIREMENT_OFF_HAND
            }
            _ => false,
        },
        DISCIPLINE_PRECISION => {
            normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_BOW
                && normalize_authored_id(spec.hand_requirement.as_str())
                    == HAND_REQUIREMENT_TWO_HAND
        }
        _ => false,
    }
}

fn weapon_spec(item_def_id: &str) -> Option<HubWeaponFamilyAuthoring> {
    let normalized = normalize_authored_id(item_def_id);
    parse_weapon_appearance_catalog()
        .ok()?
        .families
        .into_iter()
        .find(|spec| normalize_authored_id(spec.item_def_id.as_str()) == normalized)
}

fn validated_color_id(spec: &HubWeaponFamilyAuthoring, color_id: &str) -> Result<String, String> {
    let normalized = normalize_authored_id(color_id);
    if spec
        .variants
        .iter()
        .any(|variant| normalize_authored_id(variant.color_id.as_str()) == normalized)
    {
        Ok(normalized)
    } else {
        Err(format!(
            "weapon '{}' does not have color '{}'",
            spec.item_def_id, normalized
        ))
    }
}

fn default_weapon_loadout(primary_discipline_id: &str) -> (String, String, String, String) {
    let primary = normalize_authored_id(primary_discipline_id);
    let catalog = parse_weapon_appearance_catalog().ok();
    let main_hand = catalog
        .as_ref()
        .into_iter()
        .flat_map(|catalog| catalog.families.iter())
        .find(|spec| {
            normalize_authored_id(spec.primary_discipline_id.as_str()) == primary
                && normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && weapon_spec_contract_is_valid(spec)
        });
    let off_hand = catalog
        .as_ref()
        .into_iter()
        .flat_map(|catalog| catalog.families.iter())
        .find(|spec| {
            normalize_authored_id(spec.primary_discipline_id.as_str()) == primary
                && normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_OFF_HAND
                && weapon_spec_contract_is_valid(spec)
        });
    (
        main_hand
            .map(|spec| normalize_authored_id(spec.item_def_id.as_str()))
            .unwrap_or_default(),
        main_hand
            .map(|spec| normalize_authored_id(spec.default_color_id.as_str()))
            .unwrap_or_default(),
        off_hand
            .map(|spec| normalize_authored_id(spec.item_def_id.as_str()))
            .unwrap_or_default(),
        off_hand
            .map(|spec| normalize_authored_id(spec.default_color_id.as_str()))
            .unwrap_or_default(),
    )
}

fn validate_hub_weapon_loadout(
    primary_discipline_id: &str,
    main_hand_item_def_id: &str,
    main_hand_color_id: &str,
    off_hand_item_def_id: &str,
    off_hand_color_id: &str,
) -> Result<(String, String, String, String), String> {
    let primary = normalize_authored_id(primary_discipline_id);
    let main_hand = normalize_authored_id(main_hand_item_def_id);
    let off_hand = normalize_authored_id(off_hand_item_def_id);
    if !matches!(
        primary.as_str(),
        DISCIPLINE_SUBTLETY | DISCIPLINE_WAR | DISCIPLINE_ZEAL | DISCIPLINE_PRECISION
    ) {
        return Err(format!(
            "primary discipline '{primary}' does not support a weapon loadout"
        ));
    }

    let main_spec = weapon_spec(main_hand.as_str())
        .ok_or_else(|| format!("unknown selectable weapon '{main_hand}'"))?;
    if normalize_authored_id(main_spec.primary_discipline_id.as_str()) != primary
        || normalize_authored_id(main_spec.equip_slot.as_str()) != EQUIP_SLOT_MAIN_HAND
        || !weapon_spec_contract_is_valid(&main_spec)
    {
        return Err(format!(
            "weapon '{}' is not allowed for primary discipline '{}'",
            main_spec.item_def_id, primary
        ));
    }
    let main_color = validated_color_id(&main_spec, main_hand_color_id)?;

    if primary == DISCIPLINE_ZEAL {
        let off_spec = weapon_spec(off_hand.as_str())
            .ok_or_else(|| "Zeal requires an authored shield".to_string())?;
        if normalize_authored_id(off_spec.primary_discipline_id.as_str()) != primary
            || normalize_authored_id(off_spec.equip_slot.as_str()) != EQUIP_SLOT_OFF_HAND
            || !weapon_spec_contract_is_valid(&off_spec)
        {
            return Err(format!(
                "off-hand weapon '{}' is not an allowed Zeal shield",
                off_spec.item_def_id
            ));
        }
        let off_color = validated_color_id(&off_spec, off_hand_color_id)?;
        return Ok((main_hand, main_color, off_hand, off_color));
    } else if !off_hand.is_empty() {
        return Err(format!(
            "primary discipline '{primary}' cannot equip an off-hand weapon"
        ));
    } else if !normalize_authored_id(off_hand_color_id).is_empty() {
        return Err(format!(
            "primary discipline '{primary}' cannot select an off-hand color"
        ));
    }

    Ok((main_hand, main_color, String::new(), String::new()))
}

fn preserve_or_default_weapon_loadout(
    primary_discipline_id: &str,
    existing: Option<&HubPlayerLoadout>,
) -> (String, String, String, String) {
    if let Some(existing) = existing {
        if let Ok(valid) = validate_hub_weapon_loadout(
            primary_discipline_id,
            existing
                .main_hand_item_def_id
                .as_deref()
                .unwrap_or_default(),
            existing.main_hand_color_id.as_deref().unwrap_or_default(),
            existing.off_hand_item_def_id.as_deref().unwrap_or_default(),
            existing.off_hand_color_id.as_deref().unwrap_or_default(),
        ) {
            return valid;
        }
    }
    default_weapon_loadout(primary_discipline_id)
}

fn validate_hub_discipline_loadout(
    ctx: &ReducerContext,
    primary_discipline_id: String,
    secondary_discipline_id_1: String,
    secondary_discipline_id_2: String,
    selected_ability_ids: Vec<String>,
) -> Result<(String, String, String, Vec<String>), String> {
    let primary = normalize_authored_id(primary_discipline_id.as_str());
    let secondary_1 = normalize_authored_id(secondary_discipline_id_1.as_str());
    let secondary_2 = normalize_authored_id(secondary_discipline_id_2.as_str());
    if primary.is_empty()
        || ctx
            .db
            .hub_combat_discipline_definition()
            .discipline_id()
            .find(primary.clone())
            .is_none()
    {
        return Err("a known primary combat discipline is required".to_string());
    }
    for secondary in [&secondary_1, &secondary_2] {
        if !secondary.is_empty()
            && ctx
                .db
                .hub_combat_discipline_definition()
                .discipline_id()
                .find(secondary.clone())
                .is_none()
        {
            return Err(format!("unknown secondary combat discipline '{secondary}'"));
        }
    }
    if secondary_1 == primary || secondary_2 == primary {
        return Err("the primary combat discipline cannot also be secondary".to_string());
    }
    if !secondary_1.is_empty() && secondary_1 == secondary_2 {
        return Err("secondary combat disciplines must be unique".to_string());
    }
    if selected_ability_ids.len() > MAX_DISCIPLINE_LOADOUT_ABILITIES {
        return Err(format!(
            "discipline loadout may contain at most {MAX_DISCIPLINE_LOADOUT_ABILITIES} abilities"
        ));
    }

    let selected_disciplines: HashSet<&str> =
        [primary.as_str(), secondary_1.as_str(), secondary_2.as_str()]
            .into_iter()
            .filter(|id| !id.is_empty())
            .collect();
    let mut seen = HashSet::new();
    let mut abilities = Vec::new();
    let mut counts: HashMap<String, usize> = HashMap::new();
    for ability_id in selected_ability_ids {
        let ability_id = normalize_authored_id(ability_id.as_str());
        if ability_id.is_empty() || !seen.insert(ability_id.clone()) {
            continue;
        }
        let ability = ctx
            .db
            .hub_ability_definition()
            .ability_id()
            .find(ability_id.clone())
            .ok_or_else(|| format!("unknown ability '{ability_id}'"))?;
        if !selected_disciplines.contains(ability.discipline_id.as_str()) {
            return Err(format!(
                "ability '{}' does not belong to a selected discipline",
                ability.ability_id
            ));
        }
        if !ability_tags_allow_discipline_selection(ability.ability_tags.as_str()) {
            return Err(format!(
                "ability '{}' cannot be selected for a discipline loadout",
                ability.ability_id
            ));
        }
        *counts.entry(ability.discipline_id).or_default() += 1;
        abilities.push(ability_id);
    }
    if counts.get(&primary).copied().unwrap_or_default() < PRIMARY_DISCIPLINE_ABILITY_MINIMUM {
        return Err(format!(
            "primary discipline requires at least {PRIMARY_DISCIPLINE_ABILITY_MINIMUM} selected abilities"
        ));
    }
    for secondary in [&secondary_1, &secondary_2] {
        if !secondary.is_empty()
            && counts.get(secondary).copied().unwrap_or_default()
                < SECONDARY_DISCIPLINE_ABILITY_MINIMUM
        {
            return Err(format!(
                "secondary discipline '{secondary}' requires at least {SECONDARY_DISCIPLINE_ABILITY_MINIMUM} selected ability"
            ));
        }
    }
    Ok((primary, secondary_1, secondary_2, abilities))
}

fn ability_tags_allow_discipline_selection(ability_tags: &str) -> bool {
    ability_tags.split(',').any(|tag| {
        matches!(
            normalize_authored_id(tag).as_str(),
            "ACTION_BAR_ACTION" | "PASSIVE"
        )
    })
}

fn next_loadout_revision(current: Option<u64>) -> u64 {
    current.unwrap_or(0).saturating_add(1)
}

fn normalize_authored_id(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

fn freeze_player_loadout_for_ticket(
    ctx: &ReducerContext,
    ticket_id: String,
    player_identity: Identity,
) {
    let loadout = ctx.db.hub_player_loadout().owner().find(player_identity);
    ctx.db
        .match_player_loadout_snapshot()
        .insert(MatchPlayerLoadoutSnapshot {
            ticket_id,
            player_identity,
            primary_discipline_id: loadout
                .as_ref()
                .map(|row| row.primary_discipline_id.clone())
                .unwrap_or_default(),
            secondary_discipline_id_1: loadout
                .as_ref()
                .map(|row| row.secondary_discipline_id_1.clone())
                .unwrap_or_default(),
            secondary_discipline_id_2: loadout
                .as_ref()
                .map(|row| row.secondary_discipline_id_2.clone())
                .unwrap_or_default(),
            selected_ability_ids: loadout
                .as_ref()
                .map(|row| row.selected_ability_ids.clone())
                .unwrap_or_default(),
            armor_set_id: loadout
                .as_ref()
                .map(|row| row.armor_set_id.clone())
                .unwrap_or_default(),
            main_hand_item_def_id: loadout
                .as_ref()
                .and_then(|row| row.main_hand_item_def_id.clone()),
            off_hand_item_def_id: loadout
                .as_ref()
                .and_then(|row| row.off_hand_item_def_id.clone()),
            main_hand_color_id: loadout
                .as_ref()
                .and_then(|row| row.main_hand_color_id.clone()),
            off_hand_color_id: loadout
                .as_ref()
                .and_then(|row| row.off_hand_color_id.clone()),
            loadout_revision: loadout.as_ref().map(|row| row.revision).unwrap_or_default(),
            captured_at: ctx.timestamp,
        });
}

fn delete_loadout_snapshot_for_ticket(ctx: &ReducerContext, ticket_id: &str) {
    ctx.db
        .match_player_loadout_snapshot()
        .ticket_id()
        .delete(ticket_id.to_string());
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

fn ensure_default_hub_player_loadout(
    ctx: &ReducerContext,
    identity: Identity,
) -> Result<(), String> {
    if ctx.db.hub_player_loadout().owner().find(identity).is_some() {
        return Ok(());
    }

    let selected_ability_ids = default_hub_selected_ability_ids()?;
    let (primary, secondary_1, secondary_2, selected_ability_ids) =
        validate_hub_discipline_loadout(
            ctx,
            DEFAULT_HUB_PRIMARY_DISCIPLINE.to_string(),
            DEFAULT_HUB_SECONDARY_DISCIPLINE_1.to_string(),
            DEFAULT_HUB_SECONDARY_DISCIPLINE_2.to_string(),
            selected_ability_ids,
        )?;
    if ctx
        .db
        .hub_armor_set_definition()
        .armor_set_id()
        .find(DEFAULT_HUB_ARMOR_SET.to_string())
        .is_none()
    {
        return Err(format!(
            "default Hub armor set '{DEFAULT_HUB_ARMOR_SET}' is not authored"
        ));
    }

    let (main_hand, main_color, off_hand, off_hand_color) =
        default_weapon_loadout(primary.as_str());
    let (main_hand, main_color, off_hand, off_hand_color) = validate_hub_weapon_loadout(
        primary.as_str(),
        main_hand.as_str(),
        main_color.as_str(),
        off_hand.as_str(),
        off_hand_color.as_str(),
    )?;
    ctx.db.hub_player_loadout().insert(HubPlayerLoadout {
        owner: identity,
        primary_discipline_id: primary,
        secondary_discipline_id_1: secondary_1,
        secondary_discipline_id_2: secondary_2,
        selected_ability_ids,
        armor_set_id: DEFAULT_HUB_ARMOR_SET.to_string(),
        main_hand_item_def_id: Some(main_hand),
        off_hand_item_def_id: Some(off_hand),
        main_hand_color_id: Some(main_color),
        off_hand_color_id: Some(off_hand_color),
        revision: next_loadout_revision(None),
        updated_at: ctx.timestamp,
    });
    Ok(())
}

fn default_hub_selected_ability_ids() -> Result<Vec<String>, String> {
    let authored: HubProgressionCatalogFile = serde_json::from_str(PROGRESSION_CATALOG_JSON)
        .map_err(|error| format!("Hub progression catalog is invalid: {error}"))?;
    let mut abilities = authored.abilities;
    abilities.sort_by_key(|ability| {
        (
            ability.sort_order,
            normalize_authored_id(ability.ability_id.as_str()),
        )
    });

    let requirements = [
        (
            DEFAULT_HUB_PRIMARY_DISCIPLINE,
            PRIMARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
        (
            DEFAULT_HUB_SECONDARY_DISCIPLINE_1,
            SECONDARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
        (
            DEFAULT_HUB_SECONDARY_DISCIPLINE_2,
            SECONDARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
    ];
    let mut selected = Vec::new();
    for (discipline_id, minimum) in requirements {
        let available: Vec<String> = abilities
            .iter()
            .filter(|ability| {
                normalize_authored_id(ability.actor_scope.as_str()) == "PLAYER"
                    && normalize_authored_id(ability.discipline_id.as_str()) == discipline_id
                    && authored_ability_has_tag(ability, "ACTION_BAR_ACTION")
                    && !authored_ability_has_tag(ability, "PASSIVE")
            })
            .map(|ability| normalize_authored_id(ability.ability_id.as_str()))
            .collect();
        if available.len() < minimum {
            return Err(format!(
                "default Hub discipline '{discipline_id}' requires {minimum} active abilities but only {} are authored",
                available.len()
            ));
        }
        selected.extend(available.into_iter().take(minimum));
    }
    Ok(selected)
}

fn authored_ability_has_tag(ability: &HubAbilityAuthoring, expected_tag: &str) -> bool {
    ability
        .ability_tags
        .iter()
        .any(|tag| normalize_authored_id(tag.as_str()) == expected_tag)
}

fn bump_provisioner_wakeup(ctx: &ReducerContext) {
    let table = ctx.db.provisioner_wakeup_state();
    if let Some(mut state) = table.singleton_id().find(PROVISIONER_WAKEUP_ID) {
        state.sequence = next_wakeup_sequence(Some(state.sequence));
        table.singleton_id().update(state);
    } else {
        table.insert(ProvisionerWakeupState {
            singleton_id: PROVISIONER_WAKEUP_ID,
            sequence: next_wakeup_sequence(None),
        });
    }
}

fn next_wakeup_sequence(current: Option<u64>) -> u64 {
    current.unwrap_or(0).saturating_add(1)
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
    fn wakeup_sequence_initializes_and_advances_monotonically() {
        assert_eq!(next_wakeup_sequence(None), 1);
        assert_eq!(next_wakeup_sequence(Some(1)), 2);
        assert_eq!(next_wakeup_sequence(Some(u64::MAX)), u64::MAX);
    }

    #[test]
    fn loadout_revision_initializes_and_advances_monotonically() {
        assert_eq!(next_loadout_revision(None), 1);
        assert_eq!(next_loadout_revision(Some(1)), 2);
        assert_eq!(next_loadout_revision(Some(u64::MAX)), u64::MAX);
    }

    #[test]
    fn default_hub_loadout_matches_the_existing_ui_starter_selection() {
        assert_eq!(
            default_hub_selected_ability_ids().expect("canonical Hub starter abilities"),
            [
                "WARRIOR_GROUND_TO_AIR_PLACEHOLDER",
                "WARRIOR_HEW",
                "WARRIOR_MAIM",
                "WARRIOR_CRUSHING_BLOW",
                "WARRIOR_CATACLYSM",
                "WARRIOR_BUZZSAW",
                "WARRIOR_WHIRLWIND",
                "WARRIOR_SUNDER",
                "DAGGER_QUICK_CUT",
                "SPELL_FIREBALL",
            ]
        );
        assert!(HUB_ARMOR_SET_SPECS
            .iter()
            .any(|spec| spec.armor_set_id == DEFAULT_HUB_ARMOR_SET));
        assert_eq!(
            default_weapon_loadout(DEFAULT_HUB_PRIMARY_DISCIPLINE),
            (
                "TRAINING_TWO_HAND_SWORD".to_string(),
                "DEFAULT".to_string(),
                String::new(),
                String::new(),
            )
        );
    }

    #[test]
    fn hub_armor_catalog_ids_are_unique_and_have_supported_tiers() {
        let ids: HashSet<&str> = HUB_ARMOR_SET_SPECS
            .iter()
            .map(|spec| spec.armor_set_id)
            .collect();
        assert_eq!(ids.len(), HUB_ARMOR_SET_SPECS.len());
        assert!(HUB_ARMOR_SET_SPECS.iter().all(|spec| {
            matches!(spec.armor_tier, "LIGHT" | "MEDIUM" | "HEAVY") && spec.piece_count > 0
        }));
    }

    #[test]
    fn hub_weapon_catalog_is_unique_and_enforces_primary_discipline_rules() {
        let catalog = parse_weapon_appearance_catalog().expect("shared weapon appearance catalog");
        let ids: HashSet<&str> = catalog
            .families
            .iter()
            .map(|spec| spec.item_def_id.as_str())
            .collect();
        assert_eq!(ids.len(), 126);
        assert_eq!(
            catalog
                .families
                .iter()
                .map(|family| family.variants.len())
                .sum::<usize>(),
            387
        );
        assert!(ids.contains("NH_FIST_1H_DOUBLECLAW"));
        assert!(ids.contains("NH_FIST_1H_METALPUNCH"));
        for legacy_id in [
            "TRAINING_DAGGER_PAIR",
            "TRAINING_TWO_HAND_SWORD",
            "TRAINING_ONE_HAND_SWORD",
            "TRAINING_SHIELD",
            "TRAINING_BOW",
            "NEWBIE_DAGGER_PAIR_01",
            "NEWBIE_TWO_HAND_SWORD_01",
            "NEWBIE_ONE_HAND_SWORD_01",
            "NEWBIE_SHIELD_01",
            "NEWBIE_BOW_01",
        ] {
            assert!(ids.contains(legacy_id), "missing legacy weapon {legacy_id}");
        }
        assert!(catalog.families.iter().all(weapon_spec_contract_is_valid));

        for primary in [
            DISCIPLINE_SUBTLETY,
            DISCIPLINE_WAR,
            DISCIPLINE_ZEAL,
            DISCIPLINE_PRECISION,
        ] {
            let (main_hand, main_color, off_hand, off_color) = default_weapon_loadout(primary);
            assert!(validate_hub_weapon_loadout(
                primary,
                &main_hand,
                &main_color,
                &off_hand,
                &off_color
            )
            .is_ok());
        }

        assert!(
            validate_hub_weapon_loadout(DISCIPLINE_WAR, "NH_BOW_FANTASY_01", "BL", "", "").is_err()
        );
        assert!(validate_hub_weapon_loadout(
            DISCIPLINE_WAR,
            "NH_SWORD_2H_FANTASY_01",
            "NOT_A_COLOR",
            "",
            ""
        )
        .is_err());
        assert!(validate_hub_weapon_loadout(
            DISCIPLINE_SUBTLETY,
            "NH_DAGGER_1H_FANTASY_01",
            "BL",
            "NH_SHIELD_FANTASY_01",
            "BL"
        )
        .is_err());
    }

    #[test]
    fn canonical_progression_catalog_parses_for_hub_projection() {
        let authored: HubProgressionCatalogFile =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("canonical progression JSON");
        assert!(!authored.combat_disciplines.is_empty());
        assert!(authored.abilities.iter().any(|ability| {
            ability.actor_scope == "PLAYER"
                && ability
                    .ability_tags
                    .iter()
                    .any(|tag| tag == "ACTION_BAR_ACTION")
        }));
    }

    #[test]
    fn discipline_loadout_selection_accepts_active_and_passive_abilities() {
        assert!(ability_tags_allow_discipline_selection("ACTION_BAR_ACTION"));
        assert!(ability_tags_allow_discipline_selection("PASSIVE"));
        assert!(ability_tags_allow_discipline_selection(
            "ACTION_BAR_ACTION,PASSIVE"
        ));
        assert!(!ability_tags_allow_discipline_selection("INTERNAL_ONLY"));
    }

    #[test]
    fn hub_catalog_revision_is_stable_and_nonzero() {
        assert_ne!(hub_catalog_revision(), 0);
        assert_eq!(hub_catalog_revision(), hub_catalog_revision());
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

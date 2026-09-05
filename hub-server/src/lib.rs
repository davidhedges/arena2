//! Persistent Arena Hub control plane.
//!
//! This module deliberately contains no gameplay simulation. It owns durable
//! player identity plus the request/assignment state consumed by the future
//! match provisioner and Unity handoff.

use std::collections::{HashMap, HashSet};
use std::time::Duration;

use spacetimedb::{
    reducer, table, view, Identity, ReducerContext, ScheduleAt, SpacetimeType, Table, Timestamp,
    ViewContext,
};

#[path = "../../server/src/ability_cost.rs"]
mod ability_cost;
#[allow(dead_code)]
#[path = "../../server/src/armor_catalog.rs"]
mod armor_catalog;
use armor_catalog::armor_set_catalog;
#[path = "../../server/src/weapon_catalog.rs"]
mod weapon_catalog;
use weapon_catalog::{
    WeaponAppearanceCatalog as HubWeaponAppearanceCatalogFile,
    WeaponColor as HubWeaponColorAuthoring, WeaponFamily as HubWeaponFamilyAuthoring,
};

#[path = "../../server/src/combat_build_v2.rs"]
mod combat_build_v2_contract;

use combat_build_v2_contract::{
    CombatBuildV2Catalog, CombatBuildV2DisciplineConfiguration as ContractV2Configuration,
    CombatBuildV2Draft as ContractV2Draft, CombatFeatureSelection as ContractV2Feature,
    CombatSpecializationKind, SelectedCombatSpecialization as ContractV2Specialization,
    ValidatedCombatBuildV2, COMBAT_BUILD_V2_SCHEMA_VERSION,
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

/// Open-world sessions ride the same ticket/assignment pipeline as PvP so they
/// inherit its disposal behavior; the destination scene travels in the ticket's
/// `format` column rather than a new column, keeping `MatchTicket` unchanged.
const QUEUE_OPEN_WORLD: &str = "OPEN_WORLD";

/// The control plane refuses to provision a destination it does not recognize,
/// so a typo cannot leave an orphaned database behind. Authoritative scene
/// behavior still lives in the gameplay module's `is_known_open_world_scene`;
/// this list mirrors it and `OpenWorldTravelCatalog.Destinations` on the client.
const OPEN_WORLD_DESTINATIONS: &[&str] = &[
    "Adventure_Island",
    "Desert_Day",
    "Docks_Day",
    "Giant_Skeleton",
    "Golden_Valley_Overcast",
    "Golden_Valley_Sunny",
    "Great_Hall_Day",
    "Idol_Day",
    "Oasis_Day",
    "RandomDungeon",
    "Temple_Gardens",
];

const STATUS_PENDING: &str = "PENDING";
const STATUS_CLAIMED: &str = "CLAIMED";
const STATUS_PROVISIONING: &str = "PROVISIONING";
const STATUS_READY: &str = "READY";
const STATUS_FAILED: &str = "FAILED";
const STATUS_CLOSED: &str = "CLOSED";

const FAILURE_TICKET_EXPIRED: &str = "TICKET_EXPIRED";
const DEFAULT_HUB_ARMOR_SET: &str = "PEASANT";
#[cfg(test)]
const EQUIP_SLOT_MAIN_HAND: &str = "MAIN_HAND";
#[cfg(test)]
const EQUIP_SLOT_OFF_HAND: &str = "OFF_HAND";
#[cfg(test)]
const WEAPON_KIND_TWO_HAND_SWORD: &str = "TWO_HAND_SWORD";
#[cfg(test)]
const WEAPON_KIND_ONE_HAND_SWORD: &str = "ONE_HAND_SWORD";
#[cfg(test)]
const WEAPON_KIND_TWO_HAND_AXE: &str = "TWO_HAND_AXE";
#[cfg(test)]
const WEAPON_KIND_ONE_HAND_AXE: &str = "ONE_HAND_AXE";
#[cfg(test)]
const WEAPON_KIND_TWO_HAND_HAMMER: &str = "TWO_HAND_HAMMER";
#[cfg(test)]
const WEAPON_KIND_ONE_HAND_HAMMER: &str = "ONE_HAND_HAMMER";
#[cfg(test)]
const WEAPON_KIND_ONE_HAND_FIST: &str = "ONE_HAND_FIST";
#[cfg(test)]
const WEAPON_KIND_POLEARM: &str = "POLEARM";
#[cfg(test)]
const WEAPON_KIND_SHIELD: &str = "SHIELD";
#[cfg(test)]
const WEAPON_KIND_DAGGER_PAIR: &str = "DAGGER_PAIR";
#[cfg(test)]
const WEAPON_KIND_BOW: &str = "BOW";
#[cfg(test)]
const WEAPON_KIND_STAFF: &str = "STAFF";
#[cfg(test)]
const HAND_REQUIREMENT_ONE_HAND: &str = "ONE_HAND";
#[cfg(test)]
const HAND_REQUIREMENT_TWO_HAND: &str = "TWO_HAND";
#[cfg(test)]
const HAND_REQUIREMENT_OFF_HAND: &str = "OFF_HAND";

const PROGRESSION_CATALOG_JSON: &str =
    include_str!("../../server/src/progression_catalog.shared.json");
const COMBAT_BUILD_V2_CATALOG_JSON: &str =
    include_str!("../../server/src/combat_build_v2_catalog.shared.json");
const WEAPON_APPEARANCE_CATALOG_JSON: &str =
    include_str!("../../Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json");
const HUB_CATALOG_HASH_OFFSET: u64 = 0xcbf29ce484222325;
const HUB_CATALOG_HASH_PRIME: u64 = 0x100000001b3;
#[allow(long_running_const_eval)]
const PROGRESSION_CATALOG_HASH: u64 =
    extend_catalog_hash(HUB_CATALOG_HASH_OFFSET, PROGRESSION_CATALOG_JSON.as_bytes());
const WEAPON_APPEARANCE_CATALOG_HASH: u64 = extend_catalog_hash(
    PROGRESSION_CATALOG_HASH,
    WEAPON_APPEARANCE_CATALOG_JSON.as_bytes(),
);
#[allow(long_running_const_eval)]
const COMBAT_BUILD_V2_CATALOG_HASH: u64 = extend_catalog_hash(
    WEAPON_APPEARANCE_CATALOG_HASH,
    COMBAT_BUILD_V2_CATALOG_JSON.as_bytes(),
);
const HUB_CATALOG_PROJECTION_HASH: u64 = extend_catalog_hash(
    COMBAT_BUILD_V2_CATALOG_HASH,
    // Refresh existing Hub rows when projection logic changes without JSON edits.
    b"combat-build-editor-projection-v5-shared-weapons",
);
#[table(accessor = hub_player)]
pub struct HubPlayer {
    #[primary_key]
    pub identity: Identity,
    pub display_name: String,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
}

/// Durable armor selection, intentionally independent from the canonical
/// combat-build aggregate.
#[table(accessor = hub_player_armor_selection)]
#[derive(Clone)]
pub struct HubPlayerArmorSelection {
    #[primary_key]
    pub owner: Identity,
    pub armor_set_id: String,
    pub revision: u64,
    pub updated_at: Timestamp,
}

/// Canonical durable combat-build aggregate.
#[table(accessor = combat_build_v2)]
#[derive(Clone, PartialEq)]
pub struct CombatBuildV2 {
    #[primary_key]
    pub owner: Identity,
    pub starting_discipline_id: Option<String>,
    pub revision: u64,
    pub updated_at: Timestamp,
}

#[table(accessor = selected_specialization_v2)]
#[derive(Clone, PartialEq)]
pub struct SelectedSpecializationV2 {
    #[primary_key]
    pub owner_slot_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub slot_index: u8,
    pub specialization_id: String,
}

#[table(accessor = dormant_specialization_v2)]
#[derive(Clone, PartialEq)]
pub struct DormantSpecializationV2 {
    #[primary_key]
    pub owner_specialization_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub specialization_id: String,
}

#[table(accessor = discipline_configuration_v2)]
#[derive(Clone, PartialEq)]
pub struct DisciplineConfigurationV2 {
    #[primary_key]
    pub owner_discipline_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub combat_discipline_id: String,
    pub main_hand_item_def_id: String,
    pub main_hand_color_id: String,
    pub off_hand_item_def_id: String,
    pub off_hand_color_id: String,
}

#[table(accessor = specialization_feature_selection_v2)]
#[derive(Clone, PartialEq)]
pub struct SpecializationFeatureSelectionV2 {
    #[primary_key]
    pub owner_ability_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub specialization_id: String,
    pub ability_id: String,
    pub preferred_bar_order: Option<u8>,
}

#[table(accessor = trait_selection_v2)]
#[derive(Clone, PartialEq)]
pub struct TraitSelectionV2 {
    #[primary_key]
    pub owner_trait_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub ability_id: String,
}

/// Durable proof that the one approved combat-build-only reset ran against
/// the exact Phase 0 snapshot/count contract.
#[table(accessor = combat_build_v2_cutover_audit)]
pub struct CombatBuildV2CutoverAudit {
    #[primary_key]
    pub singleton_id: u8,
    pub snapshot_sha256: String,
    pub v1_root_rows_before: u32,
    pub v1_child_rows_before: u32,
    pub hub_player_rows_preserved: u32,
    pub armor_rows_preserved: u32,
    pub v2_root_rows_after: u32,
    pub executed_at: Timestamp,
}

/// Prevents every Hub connection from reparsing and rescanning the authored
/// catalogs. The revision is derived from the embedded JSON and armor specs.
#[table(accessor = hub_catalog_state)]
struct HubCatalogState {
    #[primary_key]
    singleton_id: u8,
    revision: u64,
}

/// One exact canonical combat-build revision frozen at ticket creation. The
/// provisioner transports `combat_build_snapshot_json` without interpreting
/// it; the disposable module parses and revalidates the shared typed contract.
#[table(accessor = match_player_combat_build_snapshot)]
pub struct MatchPlayerCombatBuildSnapshot {
    #[primary_key]
    pub ticket_id: String,
    #[index(btree)]
    pub player_identity: Identity,
    pub contract_schema_version: u32,
    pub combat_build_revision: u64,
    pub combat_build_snapshot_json: String,
    pub armor_set_id: String,
    pub captured_at: Timestamp,
}

#[table(accessor = combat_build_v2_contract_definition, public)]
#[derive(Clone, PartialEq)]
pub struct CombatBuildV2ContractDefinition {
    #[primary_key]
    pub singleton_id: u8,
    pub schema_version: u32,
    pub minimum_selected_specializations: u32,
    pub maximum_selected_specializations: u32,
    pub global_feature_capacity: u32,
    pub trait_capacity: u32,
    pub direct_action_input_ids: Vec<String>,
}

#[table(accessor = combat_specialization_definition_v2, public)]
#[derive(Clone, PartialEq)]
pub struct CombatSpecializationDefinitionV2 {
    #[primary_key]
    pub specialization_id: String,
    #[index(btree)]
    pub combat_discipline_id: String,
    pub specialization_kind: String,
    pub display_name: String,
    pub sort_order: u32,
}

#[table(accessor = combat_feature_definition_v2, public)]
#[derive(Clone, PartialEq)]
pub struct CombatFeatureDefinitionV2 {
    #[primary_key]
    pub ability_id: String,
    #[index(btree)]
    pub specialization_id: String,
    pub combat_discipline_id: String,
    pub loadout_kind: String,
    pub display_name: String,
    pub resource_kind: String,
    pub resource_cost: f32,
    pub sort_order: u32,
}

#[table(accessor = combat_trait_definition_v2, public)]
#[derive(Clone, PartialEq)]
pub struct CombatTraitDefinitionV2 {
    #[primary_key]
    pub ability_id: String,
    pub loadout_kind: String,
    pub display_name: String,
    pub modifier_scalar: f32,
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
    pub combat_discipline_id: String,
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

/// Caller-only projection of the independently durable armor selection.
#[derive(SpacetimeType)]
pub struct MyHubArmorSelection {
    pub owner: Identity,
    pub armor_set_id: String,
    pub revision: u64,
    pub updated_at: Timestamp,
}

#[derive(Clone, SpacetimeType)]
pub struct SelectedSpecializationV2Input {
    pub slot_index: u8,
    pub specialization_id: String,
}

#[derive(Clone, SpacetimeType)]
pub struct DisciplineConfigurationV2Input {
    pub combat_discipline_id: String,
    pub main_hand_item_def_id: String,
    pub main_hand_color_id: String,
    pub off_hand_item_def_id: String,
    pub off_hand_color_id: String,
}

#[derive(Clone, SpacetimeType)]
pub struct CombatFeatureSelectionV2Input {
    pub specialization_id: String,
    pub ability_id: String,
    pub preferred_bar_order: Option<u8>,
}

#[derive(Clone, SpacetimeType)]
pub struct CombatBuildV2DraftInput {
    pub schema_version: u32,
    pub revision: u64,
    pub starting_discipline_id: Option<String>,
    pub selected_specializations: Vec<SelectedSpecializationV2Input>,
    pub dormant_specializations: Vec<String>,
    pub discipline_configurations: Vec<DisciplineConfigurationV2Input>,
    pub selected_features: Vec<CombatFeatureSelectionV2Input>,
    pub selected_traits: Vec<String>,
}

#[derive(SpacetimeType)]
pub struct MyCombatBuildV2 {
    pub owner: Identity,
    pub schema_version: u32,
    pub revision: u64,
    pub starting_discipline_id: Option<String>,
    pub selected_specializations: Vec<SelectedSpecializationV2Input>,
    pub dormant_specializations: Vec<String>,
    pub discipline_configurations: Vec<DisciplineConfigurationV2Input>,
    pub selected_features: Vec<CombatFeatureSelectionV2Input>,
    pub selected_traits: Vec<String>,
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

#[view(accessor = my_hub_armor_selection, public)]
pub fn my_hub_armor_selection(ctx: &ViewContext) -> Option<MyHubArmorSelection> {
    ctx.db
        .hub_player_armor_selection()
        .owner()
        .find(ctx.sender())
        .map(|selection| MyHubArmorSelection {
            owner: selection.owner,
            armor_set_id: selection.armor_set_id,
            revision: selection.revision,
            updated_at: selection.updated_at,
        })
}

#[view(accessor = my_combat_build_v2, public)]
pub fn my_combat_build_v2(ctx: &ViewContext) -> Option<MyCombatBuildV2> {
    read_my_combat_build_v2(ctx, ctx.sender())
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
    ensure_default_hub_player_armor_selection(ctx, ctx.sender())?;
    ensure_default_combat_build_v2(ctx, ctx.sender())?;
    Ok(())
}

#[reducer]
pub fn save_combat_build_v2(
    ctx: &ReducerContext,
    draft: CombatBuildV2DraftInput,
) -> Result<(), String> {
    ensure_hub_player(ctx, ctx.sender());
    ensure_default_combat_build_v2(ctx, ctx.sender())?;
    let expected_revision = ctx
        .db
        .combat_build_v2()
        .owner()
        .find(ctx.sender())
        .ok_or_else(|| {
            "COMBAT_BUILD_V2_NOT_INITIALIZED: caller has no canonical v2 build".to_string()
        })?
        .revision;
    let starting_discipline_id = draft.starting_discipline_id.clone();
    let contract_draft = contract_v2_draft_from_input(draft);
    let catalog = CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?;
    let validated = catalog
        .validate_draft(&contract_draft, expected_revision)
        .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))?;
    replace_combat_build_v2(ctx, ctx.sender(), starting_discipline_id, validated);
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
    let existing = ctx
        .db
        .hub_player_armor_selection()
        .owner()
        .find(ctx.sender());
    let row = HubPlayerArmorSelection {
        owner: ctx.sender(),
        armor_set_id,
        revision: next_loadout_revision(existing.as_ref().map(|loadout| loadout.revision)),
        updated_at: ctx.timestamp,
    };
    if existing.is_some() {
        ctx.db.hub_player_armor_selection().owner().update(row);
    } else {
        ctx.db.hub_player_armor_selection().insert(row);
    }
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
    ensure_default_hub_player_armor_selection(ctx, player_identity)?;
    ensure_default_combat_build_v2(ctx, player_identity)?;

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
    freeze_player_combat_build_for_ticket(ctx, ticket_id, player_identity)?;
    bump_provisioner_wakeup(ctx);
    Ok(())
}

/// Requests a disposable open-world instance for the caller.
///
/// Open worlds are ephemeral: the player enters with their Hub loadout and
/// nothing is written back when the instance is torn down. Reusing the match
/// ticket pipeline is deliberate — it is what makes the instance disposable.
#[reducer]
pub fn request_open_world_instance(
    ctx: &ReducerContext,
    client_request_id: String,
    destination: String,
) -> Result<(), String> {
    let client_request_id = validate_identifier("client request id", client_request_id, 8, 64)?;
    let destination = validate_identifier("open-world destination", destination, 1, 64)?;
    if !OPEN_WORLD_DESTINATIONS.contains(&destination.as_str()) {
        return Err(format!("Unknown open-world destination '{destination}'"));
    }

    let player_identity = ctx.sender();
    ensure_hub_player(ctx, player_identity);
    ensure_hub_loadout_catalogs(ctx)?;
    ensure_default_hub_player_armor_selection(ctx, player_identity)?;
    ensure_default_combat_build_v2(ctx, player_identity)?;

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
        queue_kind: QUEUE_OPEN_WORLD.to_string(),
        format: destination,
        status: STATUS_PENDING.to_string(),
        created_at: ctx.timestamp,
        updated_at: ctx.timestamp,
        expires_at: ctx.timestamp + PENDING_TICKET_TTL,
        lease_owner: None,
        lease_until: None,
        failure_code: None,
    });
    freeze_player_combat_build_for_ticket(ctx, ticket_id, player_identity)?;
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

    // The destination vocabulary is per queue kind: a match is assigned an
    // authored arena map, an open world an authored scene. Both travel in the
    // assignment's `map_id`, so the ticket decides which list to check.
    if ticket.queue_kind == QUEUE_OPEN_WORLD {
        if !OPEN_WORLD_DESTINATIONS.contains(&map_id.as_str()) {
            return Err(format!("Unsupported open-world destination {map_id}"));
        }
        if map_id != ticket.format {
            return Err(
                "Open-world assignment does not target the destination the ticket requested"
                    .to_string(),
            );
        }
    } else if map_id != ARENA_MAP_01_ID {
        return Err(format!("Unsupported authored arena map {map_id}"));
    }

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
    let weapon_catalog = parse_weapon_appearance_catalog()?;
    sync_combat_build_v2_catalogs(ctx)?;

    let armor_rows: Vec<HubArmorSetDefinition> = armor_set_catalog()
        .map(|spec| HubArmorSetDefinition {
            armor_set_id: spec.armor_set_id().to_string(),
            display_name: spec.display_name().to_string(),
            armor_tier: spec.armor_tier().to_string(),
            physical_resistance: spec.physical_resistance(),
            magical_resistance: spec.magical_resistance(),
            move_speed_modifier: spec.move_speed_modifier(),
            cast_speed_modifier: spec.cast_speed_modifier(),
            piece_count: spec.piece_count() as u32,
            sort_order: spec.sort_order(),
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
            combat_discipline_id: normalize_authored_id(spec.combat_discipline_id.as_str()),
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

fn sync_combat_build_v2_catalogs(ctx: &ReducerContext) -> Result<(), String> {
    let catalog = CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?;
    let rules = catalog.rules();
    let contract = CombatBuildV2ContractDefinition {
        singleton_id: 0,
        schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
        minimum_selected_specializations: rules.minimum_selected_specializations as u32,
        maximum_selected_specializations: rules.maximum_selected_specializations as u32,
        global_feature_capacity: rules.global_feature_capacity as u32,
        trait_capacity: rules.trait_capacity as u32,
        direct_action_input_ids: rules.direct_action_input_ids.clone(),
    };
    match ctx
        .db
        .combat_build_v2_contract_definition()
        .singleton_id()
        .find(0)
    {
        Some(existing) if existing == contract => {}
        Some(_) => {
            ctx.db
                .combat_build_v2_contract_definition()
                .singleton_id()
                .update(contract);
        }
        None => {
            ctx.db
                .combat_build_v2_contract_definition()
                .insert(contract);
        }
    }

    let specialization_rows: Vec<_> = catalog
        .specialization_definitions()
        .into_iter()
        .map(|row| CombatSpecializationDefinitionV2 {
            specialization_id: row.specialization_id,
            combat_discipline_id: row.combat_discipline_id,
            specialization_kind: match row.specialization_kind {
                CombatSpecializationKind::Form => "FORM",
                CombatSpecializationKind::School => "SCHOOL",
            }
            .to_string(),
            display_name: row.display_name,
            sort_order: row.sort_order,
        })
        .collect();
    let specialization_ids: HashSet<_> = specialization_rows
        .iter()
        .map(|row| row.specialization_id.clone())
        .collect();
    for row in specialization_rows {
        match ctx
            .db
            .combat_specialization_definition_v2()
            .specialization_id()
            .find(row.specialization_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db
                    .combat_specialization_definition_v2()
                    .specialization_id()
                    .update(row);
            }
            None => {
                ctx.db.combat_specialization_definition_v2().insert(row);
            }
        }
    }
    let stale_specialization_ids: Vec<_> = ctx
        .db
        .combat_specialization_definition_v2()
        .iter()
        .map(|row| row.specialization_id)
        .filter(|id| !specialization_ids.contains(id))
        .collect();
    for id in stale_specialization_ids {
        ctx.db
            .combat_specialization_definition_v2()
            .specialization_id()
            .delete(id);
    }

    let feature_rows: Vec<_> = catalog
        .feature_definitions()
        .into_iter()
        .map(|row| CombatFeatureDefinitionV2 {
            ability_id: row.ability_id,
            specialization_id: row.specialization_id,
            combat_discipline_id: row.combat_discipline_id,
            loadout_kind: row.loadout_kind.as_str().to_string(),
            display_name: row.display_name,
            resource_kind: row.resource_kind,
            resource_cost: row.resource_cost,
            sort_order: row.sort_order,
        })
        .collect();
    let feature_ids: HashSet<_> = feature_rows
        .iter()
        .map(|row| row.ability_id.clone())
        .collect();
    for row in feature_rows {
        match ctx
            .db
            .combat_feature_definition_v2()
            .ability_id()
            .find(row.ability_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db
                    .combat_feature_definition_v2()
                    .ability_id()
                    .update(row);
            }
            None => {
                ctx.db.combat_feature_definition_v2().insert(row);
            }
        }
    }
    let stale_feature_ids: Vec<_> = ctx
        .db
        .combat_feature_definition_v2()
        .iter()
        .map(|row| row.ability_id)
        .filter(|id| !feature_ids.contains(id))
        .collect();
    for id in stale_feature_ids {
        ctx.db
            .combat_feature_definition_v2()
            .ability_id()
            .delete(id);
    }

    let trait_rows: Vec<_> = catalog
        .trait_definitions()
        .into_iter()
        .map(|row| CombatTraitDefinitionV2 {
            ability_id: row.ability_id,
            loadout_kind: row.loadout_kind.as_str().to_string(),
            display_name: row.display_name,
            modifier_scalar: row.modifier_scalar,
            sort_order: row.sort_order,
        })
        .collect();
    let trait_ids: HashSet<_> = trait_rows
        .iter()
        .map(|row| row.ability_id.clone())
        .collect();
    for row in trait_rows {
        match ctx
            .db
            .combat_trait_definition_v2()
            .ability_id()
            .find(row.ability_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db.combat_trait_definition_v2().ability_id().update(row);
            }
            None => {
                ctx.db.combat_trait_definition_v2().insert(row);
            }
        }
    }
    let stale_trait_ids: Vec<_> = ctx
        .db
        .combat_trait_definition_v2()
        .iter()
        .map(|row| row.ability_id)
        .filter(|id| !trait_ids.contains(id))
        .collect();
    for id in stale_trait_ids {
        ctx.db.combat_trait_definition_v2().ability_id().delete(id);
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
    armor_set_catalog().fold(HUB_CATALOG_PROJECTION_HASH, |hash, spec| {
        let hash = extend_catalog_hash(hash, spec.armor_set_id().as_bytes());
        let hash = extend_catalog_hash(hash, spec.display_name().as_bytes());
        let mut hash = extend_catalog_hash(hash, spec.armor_tier().as_bytes());
        for value in [
            spec.physical_resistance(),
            spec.magical_resistance(),
            spec.move_speed_modifier(),
            spec.cast_speed_modifier(),
        ] {
            hash = extend_catalog_hash(hash, &value.to_bits().to_le_bytes());
        }
        hash ^ ((spec.piece_count() as u64) << 32) ^ spec.sort_order() as u64
    })
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

#[cfg(test)]
fn weapon_spec_contract_is_valid(spec: &HubWeaponFamilyAuthoring) -> bool {
    match normalize_authored_id(spec.combat_discipline_id.as_str()).as_str() {
        "DAGGERS" => {
            normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_DAGGER_PAIR
                && normalize_authored_id(spec.hand_requirement.as_str())
                    == HAND_REQUIREMENT_TWO_HAND
        }
        "TWO_HANDED_SWORD" => {
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
        "SWORD_AND_SHIELD" => match normalize_authored_id(spec.equip_slot.as_str()).as_str() {
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
        "ARCHER_BOW" => {
            normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_BOW
                && normalize_authored_id(spec.hand_requirement.as_str())
                    == HAND_REQUIREMENT_TWO_HAND
        }
        "STAFF" => {
            normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_STAFF
                && normalize_authored_id(spec.hand_requirement.as_str())
                    == HAND_REQUIREMENT_TWO_HAND
        }
        _ => false,
    }
}

fn next_loadout_revision(current: Option<u64>) -> u64 {
    current.unwrap_or(0).saturating_add(1)
}

fn normalize_authored_id(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

fn freeze_player_combat_build_for_ticket(
    ctx: &ReducerContext,
    ticket_id: String,
    player_identity: Identity,
) -> Result<(), String> {
    let validated = validated_combat_build_v2_for_owner(ctx, player_identity)?;
    let catalog = CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?;
    let combat_build_snapshot_json = catalog.serialize_canonical_snapshot(&validated.snapshot)?;
    let armor_set_id = ctx
        .db
        .hub_player_armor_selection()
        .owner()
        .find(player_identity)
        .map(|row| row.armor_set_id)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| "MATCH_ARMOR_NOT_INITIALIZED: caller has no armor selection".to_string())?;

    ctx.db
        .match_player_combat_build_snapshot()
        .insert(MatchPlayerCombatBuildSnapshot {
            ticket_id,
            player_identity,
            contract_schema_version: validated.snapshot.schema_version,
            combat_build_revision: validated.snapshot.revision,
            combat_build_snapshot_json,
            armor_set_id,
            captured_at: ctx.timestamp,
        });
    Ok(())
}

fn delete_loadout_snapshot_for_ticket(ctx: &ReducerContext, ticket_id: &str) {
    ctx.db
        .match_player_combat_build_snapshot()
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

fn combat_build_key(owner: Identity, parts: &[&str]) -> String {
    let mut key = owner.to_hex().to_string();
    for part in parts {
        key.push(':');
        key.push_str(part);
    }
    key
}

fn contract_v2_draft_from_input(draft: CombatBuildV2DraftInput) -> ContractV2Draft {
    ContractV2Draft {
        schema_version: draft.schema_version,
        revision: draft.revision,
        starting_discipline_id: draft.starting_discipline_id,
        selected_specializations: draft
            .selected_specializations
            .into_iter()
            .map(|row| ContractV2Specialization {
                slot_index: row.slot_index,
                specialization_id: row.specialization_id,
            })
            .collect(),
        dormant_specializations: draft.dormant_specializations,
        discipline_configurations: draft
            .discipline_configurations
            .into_iter()
            .map(|row| ContractV2Configuration {
                combat_discipline_id: row.combat_discipline_id,
                main_hand_item_def_id: row.main_hand_item_def_id,
                main_hand_color_id: row.main_hand_color_id,
                off_hand_item_def_id: row.off_hand_item_def_id,
                off_hand_color_id: row.off_hand_color_id,
            })
            .collect(),
        selected_features: draft
            .selected_features
            .into_iter()
            .map(|row| ContractV2Feature {
                specialization_id: row.specialization_id,
                ability_id: row.ability_id,
                preferred_bar_order: row.preferred_bar_order,
            })
            .collect(),
        selected_traits: draft.selected_traits,
    }
}

fn ensure_default_combat_build_v2(ctx: &ReducerContext, owner: Identity) -> Result<(), String> {
    if ctx.db.combat_build_v2().owner().find(owner).is_some() {
        return Ok(());
    }
    let catalog = CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?;
    let draft = catalog.default_draft();
    let starting_discipline_id = draft.starting_discipline_id.clone();
    let validated = catalog
        .validate_draft(&draft, 0)
        .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))?;
    replace_combat_build_v2(ctx, owner, starting_discipline_id, validated);
    Ok(())
}

fn validated_combat_build_v2_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<ValidatedCombatBuildV2, String> {
    let draft = combat_build_v2_draft_for_owner(ctx, owner)?;
    CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?
        .validate_draft(&draft, draft.revision)
        .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))
}

fn combat_build_v2_draft_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<ContractV2Draft, String> {
    let root = ctx
        .db
        .combat_build_v2()
        .owner()
        .find(owner)
        .ok_or_else(|| {
            "COMBAT_BUILD_V2_NOT_INITIALIZED: owner has no canonical v2 build".to_string()
        })?;
    let mut selected_specializations: Vec<_> = ctx
        .db
        .selected_specialization_v2()
        .owner()
        .filter(owner)
        .map(|row| ContractV2Specialization {
            slot_index: row.slot_index,
            specialization_id: row.specialization_id,
        })
        .collect();
    selected_specializations.sort_by_key(|row| row.slot_index);
    let mut dormant_specializations: Vec<_> = ctx
        .db
        .dormant_specialization_v2()
        .owner()
        .filter(owner)
        .map(|row| row.specialization_id)
        .collect();
    dormant_specializations.sort();
    let mut discipline_configurations: Vec<_> = ctx
        .db
        .discipline_configuration_v2()
        .owner()
        .filter(owner)
        .map(|row| ContractV2Configuration {
            combat_discipline_id: row.combat_discipline_id,
            main_hand_item_def_id: row.main_hand_item_def_id,
            main_hand_color_id: row.main_hand_color_id,
            off_hand_item_def_id: row.off_hand_item_def_id,
            off_hand_color_id: row.off_hand_color_id,
        })
        .collect();
    discipline_configurations
        .sort_by(|left, right| left.combat_discipline_id.cmp(&right.combat_discipline_id));
    let mut selected_features: Vec<_> = ctx
        .db
        .specialization_feature_selection_v2()
        .owner()
        .filter(owner)
        .map(|row| ContractV2Feature {
            specialization_id: row.specialization_id,
            ability_id: row.ability_id,
            preferred_bar_order: row.preferred_bar_order,
        })
        .collect();
    selected_features.sort_by(|left, right| {
        (left.specialization_id.as_str(), left.ability_id.as_str())
            .cmp(&(right.specialization_id.as_str(), right.ability_id.as_str()))
    });
    let mut selected_traits: Vec<_> = ctx
        .db
        .trait_selection_v2()
        .owner()
        .filter(owner)
        .map(|row| row.ability_id)
        .collect();
    selected_traits.sort();
    Ok(ContractV2Draft {
        schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
        revision: root.revision,
        starting_discipline_id: root.starting_discipline_id,
        selected_specializations,
        dormant_specializations,
        discipline_configurations,
        selected_features,
        selected_traits,
    })
}

fn read_my_combat_build_v2(ctx: &ViewContext, owner: Identity) -> Option<MyCombatBuildV2> {
    let root = ctx.db.combat_build_v2().owner().find(owner)?;
    let mut selected_specializations: Vec<_> = ctx
        .db
        .selected_specialization_v2()
        .owner()
        .filter(owner)
        .map(|row| SelectedSpecializationV2Input {
            slot_index: row.slot_index,
            specialization_id: row.specialization_id,
        })
        .collect();
    selected_specializations.sort_by_key(|row| row.slot_index);
    let mut dormant_specializations: Vec<_> = ctx
        .db
        .dormant_specialization_v2()
        .owner()
        .filter(owner)
        .map(|row| row.specialization_id)
        .collect();
    dormant_specializations.sort();
    let mut discipline_configurations: Vec<_> = ctx
        .db
        .discipline_configuration_v2()
        .owner()
        .filter(owner)
        .map(|row| DisciplineConfigurationV2Input {
            combat_discipline_id: row.combat_discipline_id,
            main_hand_item_def_id: row.main_hand_item_def_id,
            main_hand_color_id: row.main_hand_color_id,
            off_hand_item_def_id: row.off_hand_item_def_id,
            off_hand_color_id: row.off_hand_color_id,
        })
        .collect();
    discipline_configurations
        .sort_by(|left, right| left.combat_discipline_id.cmp(&right.combat_discipline_id));
    let mut selected_features: Vec<_> = ctx
        .db
        .specialization_feature_selection_v2()
        .owner()
        .filter(owner)
        .map(|row| CombatFeatureSelectionV2Input {
            specialization_id: row.specialization_id,
            ability_id: row.ability_id,
            preferred_bar_order: row.preferred_bar_order,
        })
        .collect();
    selected_features.sort_by(|left, right| {
        (left.specialization_id.as_str(), left.ability_id.as_str())
            .cmp(&(right.specialization_id.as_str(), right.ability_id.as_str()))
    });
    let mut selected_traits: Vec<_> = ctx
        .db
        .trait_selection_v2()
        .owner()
        .filter(owner)
        .map(|row| row.ability_id)
        .collect();
    selected_traits.sort();
    Some(MyCombatBuildV2 {
        owner,
        schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
        revision: root.revision,
        starting_discipline_id: root.starting_discipline_id,
        selected_specializations,
        dormant_specializations,
        discipline_configurations,
        selected_features,
        selected_traits,
        updated_at: root.updated_at,
    })
}

fn replace_combat_build_v2(
    ctx: &ReducerContext,
    owner: Identity,
    starting_discipline_id: Option<String>,
    validated: ValidatedCombatBuildV2,
) {
    delete_combat_build_v2_children(ctx, owner);
    let revision = validated.snapshot.revision.saturating_add(1);
    let root = CombatBuildV2 {
        owner,
        starting_discipline_id,
        revision,
        updated_at: ctx.timestamp,
    };
    if ctx.db.combat_build_v2().owner().find(owner).is_some() {
        ctx.db.combat_build_v2().owner().update(root);
    } else {
        ctx.db.combat_build_v2().insert(root);
    }

    for selected in validated.snapshot.selected_specializations {
        ctx.db
            .selected_specialization_v2()
            .insert(SelectedSpecializationV2 {
                owner_slot_key: combat_build_key(
                    owner,
                    &[selected.slot_index.to_string().as_str()],
                ),
                owner,
                slot_index: selected.slot_index,
                specialization_id: selected.specialization_id,
            });
    }
    for specialization_id in validated.snapshot.dormant_specializations {
        ctx.db
            .dormant_specialization_v2()
            .insert(DormantSpecializationV2 {
                owner_specialization_key: combat_build_key(owner, &[specialization_id.as_str()]),
                owner,
                specialization_id,
            });
    }
    for row in validated.snapshot.discipline_configurations {
        ctx.db
            .discipline_configuration_v2()
            .insert(DisciplineConfigurationV2 {
                owner_discipline_key: combat_build_key(owner, &[row.combat_discipline_id.as_str()]),
                owner,
                combat_discipline_id: row.combat_discipline_id,
                main_hand_item_def_id: row.main_hand_item_def_id,
                main_hand_color_id: row.main_hand_color_id,
                off_hand_item_def_id: row.off_hand_item_def_id,
                off_hand_color_id: row.off_hand_color_id,
            });
    }
    for row in validated.snapshot.selected_features {
        ctx.db
            .specialization_feature_selection_v2()
            .insert(SpecializationFeatureSelectionV2 {
                owner_ability_key: combat_build_key(owner, &[row.ability_id.as_str()]),
                owner,
                specialization_id: row.specialization_id,
                ability_id: row.ability_id,
                preferred_bar_order: row.preferred_bar_order,
            });
    }
    for ability_id in validated.snapshot.selected_traits {
        ctx.db.trait_selection_v2().insert(TraitSelectionV2 {
            owner_trait_key: combat_build_key(owner, &[ability_id.as_str()]),
            owner,
            ability_id,
        });
    }
}

fn delete_combat_build_v2_children(ctx: &ReducerContext, owner: Identity) {
    let selected_keys: Vec<_> = ctx
        .db
        .selected_specialization_v2()
        .owner()
        .filter(owner)
        .map(|row| row.owner_slot_key)
        .collect();
    for key in selected_keys {
        ctx.db
            .selected_specialization_v2()
            .owner_slot_key()
            .delete(key);
    }
    let dormant_keys: Vec<_> = ctx
        .db
        .dormant_specialization_v2()
        .owner()
        .filter(owner)
        .map(|row| row.owner_specialization_key)
        .collect();
    for key in dormant_keys {
        ctx.db
            .dormant_specialization_v2()
            .owner_specialization_key()
            .delete(key);
    }
    let configuration_keys: Vec<_> = ctx
        .db
        .discipline_configuration_v2()
        .owner()
        .filter(owner)
        .map(|row| row.owner_discipline_key)
        .collect();
    for key in configuration_keys {
        ctx.db
            .discipline_configuration_v2()
            .owner_discipline_key()
            .delete(key);
    }
    let feature_keys: Vec<_> = ctx
        .db
        .specialization_feature_selection_v2()
        .owner()
        .filter(owner)
        .map(|row| row.owner_ability_key)
        .collect();
    for key in feature_keys {
        ctx.db
            .specialization_feature_selection_v2()
            .owner_ability_key()
            .delete(key);
    }
    let trait_keys: Vec<_> = ctx
        .db
        .trait_selection_v2()
        .owner()
        .filter(owner)
        .map(|row| row.owner_trait_key)
        .collect();
    for key in trait_keys {
        ctx.db.trait_selection_v2().owner_trait_key().delete(key);
    }
}

fn ensure_default_hub_player_armor_selection(
    ctx: &ReducerContext,
    identity: Identity,
) -> Result<(), String> {
    if ctx
        .db
        .hub_player_armor_selection()
        .owner()
        .find(identity)
        .is_some()
    {
        return Ok(());
    }
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

    ctx.db
        .hub_player_armor_selection()
        .insert(HubPlayerArmorSelection {
            owner: identity,
            armor_set_id: DEFAULT_HUB_ARMOR_SET.to_string(),
            revision: next_loadout_revision(None),
            updated_at: ctx.timestamp,
        });
    Ok(())
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
    fn default_hub_armor_selection_is_authored() {
        assert!(armor_set_catalog().any(|spec| spec.armor_set_id() == DEFAULT_HUB_ARMOR_SET));
    }

    #[test]
    fn hub_armor_catalog_ids_are_unique_and_have_supported_tiers() {
        let ids: HashSet<&str> = armor_set_catalog()
            .map(|spec| spec.armor_set_id())
            .collect();
        assert_eq!(armor_set_catalog().count(), 89);
        assert_eq!(ids.len(), armor_set_catalog().count());
        for armor_set_id in ["DBRINGER_BK", "DBRINGER_BL", "DBRINGER_GN", "DBRINGER_RD"] {
            assert_eq!(
                armor_set_catalog()
                    .find(|spec| spec.armor_set_id() == armor_set_id)
                    .map(|spec| spec.armor_tier()),
                Some("MEDIUM"),
                "{armor_set_id} must remain Medium armor"
            );
        }
        assert!(armor_set_catalog().all(|spec| {
            matches!(spec.armor_tier(), "LIGHT" | "MEDIUM" | "HEAVY")
                && (4..=7).contains(&spec.piece_count())
        }));
        assert_eq!(
            armor_set_catalog()
                .filter(|spec| spec.armor_tier() == "LIGHT")
                .count(),
            29
        );
        assert_eq!(
            armor_set_catalog()
                .filter(|spec| spec.armor_tier() == "MEDIUM")
                .count(),
            41
        );
        assert_eq!(
            armor_set_catalog()
                .filter(|spec| spec.armor_tier() == "HEAVY")
                .count(),
            19
        );
    }

    #[test]
    fn hub_weapon_catalog_is_unique_and_enforces_canonical_discipline_rules() {
        let catalog = parse_weapon_appearance_catalog().expect("shared weapon appearance catalog");
        let ids: HashSet<&str> = catalog
            .families
            .iter()
            .map(|spec| spec.item_def_id.as_str())
            .collect();
        assert_eq!(ids.len(), 138);
        assert_eq!(
            catalog
                .families
                .iter()
                .map(|family| family.variants.len())
                .sum::<usize>(),
            425
        );
        assert!(ids.contains("NH_FIST_1H_DOUBLECLAW"));
        assert!(ids.contains("NH_FIST_1H_METALPUNCH"));
        for legacy_id in [
            "TRAINING_DAGGER_PAIR",
            "TRAINING_TWO_HAND_SWORD",
            "TRAINING_ONE_HAND_SWORD",
            "TRAINING_SHIELD",
            "TRAINING_BOW",
            "NEWBIE_STAFF_01",
            "NEWBIE_STAFF_02",
            "NEWBIE_STAFF_03",
            "NEWBIE_STAFF_04",
            "NEWBIE_DAGGER_PAIR_01",
            "NEWBIE_TWO_HAND_SWORD_01",
            "NEWBIE_ONE_HAND_SWORD_01",
            "NEWBIE_SHIELD_01",
            "NEWBIE_BOW_01",
        ] {
            assert!(ids.contains(legacy_id), "missing legacy weapon {legacy_id}");
        }
        assert!(catalog.families.iter().all(weapon_spec_contract_is_valid));
        let canonical_ids: HashSet<_> = catalog
            .families
            .iter()
            .map(|family| family.combat_discipline_id.as_str())
            .collect();
        assert_eq!(
            canonical_ids,
            HashSet::from([
                "DAGGERS",
                "TWO_HANDED_SWORD",
                "SWORD_AND_SHIELD",
                "ARCHER_BOW",
                "STAFF",
            ])
        );

        let staff_specs: Vec<_> = catalog
            .families
            .iter()
            .filter(|spec| normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_STAFF)
            .collect();
        assert_eq!(staff_specs.len(), 12);
        assert_eq!(
            staff_specs
                .iter()
                .map(|spec| spec.variants.len())
                .sum::<usize>(),
            38
        );
        assert!(staff_specs
            .iter()
            .all(|staff| staff.combat_discipline_id == "STAFF"));
    }

    #[test]
    fn hub_catalog_revision_is_stable_nonzero_and_covers_combat_build_catalog() {
        assert_ne!(hub_catalog_revision(), 0);
        assert_eq!(hub_catalog_revision(), hub_catalog_revision());
        assert_eq!(
            COMBAT_BUILD_V2_CATALOG_HASH,
            extend_catalog_hash(
                WEAPON_APPEARANCE_CATALOG_HASH,
                COMBAT_BUILD_V2_CATALOG_JSON.as_bytes()
            )
        );
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

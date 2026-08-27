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

#[path = "../../server/src/combat_build.rs"]
#[allow(dead_code)]
mod combat_build_contract;

use combat_build_contract::{
    CombatBuildCatalog, CombatBuildDraft,
    DisciplineActionBarAssignment as ContractActionAssignment,
    DisciplineConfiguration as ContractDisciplineConfiguration,
    DisciplineWeaponConfiguration as ContractWeaponConfiguration,
    SelectedCombatDiscipline as ContractSelectedDiscipline, ValidatedCombatBuild,
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
const PRIMARY_DISCIPLINE_ABILITY_MINIMUM: usize = 8;
const SECONDARY_DISCIPLINE_ABILITY_MINIMUM: usize = 1;
const MAX_DISCIPLINE_LOADOUT_ABILITIES: usize = 128;

const DISCIPLINE_SUBTLETY: &str = "SUBTLETY";
const DISCIPLINE_WAR: &str = "WAR";
const DISCIPLINE_ZEAL: &str = "ZEAL";
const DISCIPLINE_PRECISION: &str = "PRECISION";
const DISCIPLINE_BLIGHT: &str = "BLIGHT";
const DISCIPLINE_MORTALITY: &str = "MORTALITY";
const DISCIPLINE_RUIN: &str = "RUIN";
#[cfg(test)]
const DISCIPLINE_DIVINITY: &str = "DIVINITY";
const DISCIPLINE_ARCANA: &str = "ARCANA";
#[cfg(test)]
const DISCIPLINE_PRIMAL: &str = "PRIMAL";
const COMBAT_PROFILE_STAFF: &str = "STAFF";

const DEFAULT_HUB_PRIMARY_DISCIPLINE: &str = DISCIPLINE_WAR;
const DEFAULT_HUB_SECONDARY_DISCIPLINE_1: &str = DISCIPLINE_SUBTLETY;
const DEFAULT_HUB_SECONDARY_DISCIPLINE_2: &str = DISCIPLINE_RUIN;
const DEFAULT_HUB_ARMOR_SET: &str = "PEASANT";
const DEFAULT_STAFF_ITEM_DEF_ID: &str = "NEWBIE_STAFF_01";

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
const WEAPON_KIND_STAFF: &str = "STAFF";
const HAND_REQUIREMENT_ONE_HAND: &str = "ONE_HAND";
const HAND_REQUIREMENT_TWO_HAND: &str = "TWO_HAND";
const HAND_REQUIREMENT_OFF_HAND: &str = "OFF_HAND";

const PROGRESSION_CATALOG_JSON: &str =
    include_str!("../../server/src/progression_catalog.shared.json");
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
const HUB_CATALOG_PROJECTION_HASH: u64 = extend_catalog_hash(
    WEAPON_APPEARANCE_CATALOG_HASH,
    b"combat-build-editor-projection-v1",
);

#[table(accessor = hub_player)]
pub struct HubPlayer {
    #[primary_key]
    pub identity: Identity,
    pub display_name: String,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
}

/// Legacy UI/armor compatibility state. The canonical combat-build writer and
/// ticket handoff do not read or write its discipline, ability, or weapon
/// fields. Armor remains separately scoped here until the later client/final
/// cutover, and new callers receive a row so the current armor UI still works.
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

/// Canonical durable combat-build root. Child rows hold every selected and
/// dormant discipline configuration; only `save_combat_build` may replace
/// this aggregate.
#[table(accessor = combat_build)]
#[derive(Clone, PartialEq)]
pub struct CombatBuild {
    #[primary_key]
    pub owner: Identity,
    pub starting_discipline_id: Option<String>,
    pub revision: u64,
    pub updated_at: Timestamp,
}

#[table(accessor = combat_build_discipline)]
#[derive(Clone, PartialEq)]
pub struct CombatBuildDiscipline {
    #[primary_key]
    pub build_slot_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub slot_index: u8,
    pub combat_discipline_id: String,
}

#[table(accessor = discipline_configuration)]
#[derive(Clone, PartialEq)]
pub struct DisciplineConfiguration {
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

#[table(accessor = staff_school_selection)]
#[derive(Clone, PartialEq)]
pub struct StaffSchoolSelection {
    #[primary_key]
    pub owner_school_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub spell_school_id: String,
}

#[table(accessor = discipline_action_bar_assignment)]
#[derive(Clone, PartialEq)]
pub struct DisciplineActionBarAssignment {
    #[primary_key]
    pub owner_discipline_slot_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub combat_discipline_id: String,
    pub action_slot: String,
    pub ability_id: String,
}

#[table(accessor = discipline_passive_selection)]
#[derive(Clone, PartialEq)]
pub struct DisciplinePassiveSelection {
    #[primary_key]
    pub owner_discipline_ability_key: String,
    #[index(btree)]
    pub owner: Identity,
    pub combat_discipline_id: String,
    pub ability_id: String,
}

/// Prevents every Hub connection from reparsing and rescanning the authored
/// catalogs. The revision is derived from the embedded JSON and armor specs.
#[table(accessor = hub_catalog_state)]
struct HubCatalogState {
    #[primary_key]
    singleton_id: u8,
    revision: u64,
}

/// Data-preserving schema tombstone for Hub rows created before the canonical
/// combat-build handoff. No reducer inserts or reads this table. Phase 7 removes
/// it with the explicitly destructive final schema cutover.
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

/// Canonical, display-only combat-build catalog consumed by the Hub editor.
/// These additive tables deliberately coexist with the legacy display catalog
/// until the separately approved destructive cleanup phase.
#[table(accessor = hub_combat_build_contract_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubCombatBuildContractDefinition {
    #[primary_key]
    pub singleton_id: u8,
    pub schema_version: u32,
    pub minimum_selected_disciplines: u32,
    pub maximum_selected_disciplines: u32,
    pub minimum_staff_schools_when_selected: u32,
    pub maximum_staff_schools_when_selected: u32,
    pub combined_ability_budget: u32,
    pub maximum_active_abilities: u32,
    pub minimum_counted_abilities_per_selected_discipline: u32,
    pub action_slot_ids: Vec<String>,
}

#[table(accessor = hub_combat_build_discipline_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubCombatBuildDisciplineDefinition {
    #[primary_key]
    pub combat_discipline_id: String,
    pub display_name: String,
    pub sort_order: u32,
    pub starter_main_hand_item_def_id: String,
    pub starter_main_hand_color_id: String,
    pub starter_off_hand_item_def_id: String,
    pub starter_off_hand_color_id: String,
}

#[table(accessor = hub_spell_school_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubSpellSchoolDefinition {
    #[primary_key]
    pub spell_school_id: String,
    pub display_name: String,
    pub sort_order: u32,
}

#[table(accessor = hub_combat_build_ability_definition, public)]
#[derive(Clone, PartialEq)]
pub struct HubCombatBuildAbilityDefinition {
    #[primary_key]
    pub ability_id: String,
    #[index(btree)]
    pub combat_discipline_id: String,
    pub spell_school_id: Option<String>,
    pub selection_kind: String,
    pub display_name: String,
    pub resource_kind: String,
    pub resource_cost: f32,
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

/// Legacy caller-only projection retained for the current UI and separately
/// scoped armor selection. It is not a match-handoff or canonical combat-build
/// reader.
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

/// Typed wire draft for the single canonical combat-build save reducer. These
/// DTOs contain no validation rules; they adapt generated clients to the one
/// pure contract validator shared from `server/src/combat_build.rs`.
#[derive(Clone, SpacetimeType)]
pub struct CombatBuildSelectedDisciplineInput {
    pub slot_index: u8,
    pub combat_discipline_id: String,
}

#[derive(Clone, SpacetimeType)]
pub struct CombatBuildWeaponInput {
    pub main_hand_item_def_id: String,
    pub main_hand_color_id: String,
    pub off_hand_item_def_id: String,
    pub off_hand_color_id: String,
}

#[derive(Clone, SpacetimeType)]
pub struct CombatBuildActionAssignmentInput {
    pub action_slot: String,
    pub ability_id: String,
}

#[derive(Clone, SpacetimeType)]
pub struct CombatBuildDisciplineConfigurationInput {
    pub combat_discipline_id: String,
    pub weapon: CombatBuildWeaponInput,
    pub staff_school_ids: Vec<String>,
    pub active_assignments: Vec<CombatBuildActionAssignmentInput>,
    pub passive_ability_ids: Vec<String>,
}

#[derive(Clone, SpacetimeType)]
pub struct CombatBuildDraftInput {
    pub revision: u64,
    pub starting_discipline_id: Option<String>,
    pub selected_disciplines: Vec<CombatBuildSelectedDisciplineInput>,
    pub discipline_configurations: Vec<CombatBuildDisciplineConfigurationInput>,
}

#[derive(SpacetimeType)]
pub struct MyCombatBuild {
    pub owner: Identity,
    pub starting_discipline_id: Option<String>,
    pub revision: u64,
    pub selected_disciplines: Vec<CombatBuildSelectedDisciplineInput>,
    pub discipline_configurations: Vec<CombatBuildDisciplineConfigurationInput>,
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

#[view(accessor = my_combat_build, public)]
pub fn my_combat_build(ctx: &ViewContext) -> Option<MyCombatBuild> {
    let build = ctx.db.combat_build().owner().find(ctx.sender())?;

    let mut selected_disciplines: Vec<_> = ctx
        .db
        .combat_build_discipline()
        .owner()
        .filter(ctx.sender())
        .map(|row| CombatBuildSelectedDisciplineInput {
            slot_index: row.slot_index,
            combat_discipline_id: row.combat_discipline_id,
        })
        .collect();
    selected_disciplines.sort_by_key(|row| row.slot_index);

    let mut staff_school_ids: Vec<_> = ctx
        .db
        .staff_school_selection()
        .owner()
        .filter(ctx.sender())
        .map(|row| row.spell_school_id)
        .collect();
    staff_school_ids.sort();

    let mut assignments_by_discipline: HashMap<String, Vec<_>> = HashMap::new();
    for row in ctx
        .db
        .discipline_action_bar_assignment()
        .owner()
        .filter(ctx.sender())
    {
        assignments_by_discipline
            .entry(row.combat_discipline_id)
            .or_default()
            .push(CombatBuildActionAssignmentInput {
                action_slot: row.action_slot,
                ability_id: row.ability_id,
            });
    }
    for assignments in assignments_by_discipline.values_mut() {
        assignments.sort_by(|left, right| left.action_slot.cmp(&right.action_slot));
    }

    let mut passives_by_discipline: HashMap<String, Vec<_>> = HashMap::new();
    for row in ctx
        .db
        .discipline_passive_selection()
        .owner()
        .filter(ctx.sender())
    {
        passives_by_discipline
            .entry(row.combat_discipline_id)
            .or_default()
            .push(row.ability_id);
    }
    for passives in passives_by_discipline.values_mut() {
        passives.sort();
    }

    let mut discipline_configurations: Vec<_> = ctx
        .db
        .discipline_configuration()
        .owner()
        .filter(ctx.sender())
        .map(|row| {
            let combat_discipline_id = row.combat_discipline_id;
            CombatBuildDisciplineConfigurationInput {
                staff_school_ids: if combat_discipline_id == "STAFF" {
                    staff_school_ids.clone()
                } else {
                    Vec::new()
                },
                active_assignments: assignments_by_discipline
                    .remove(combat_discipline_id.as_str())
                    .unwrap_or_default(),
                passive_ability_ids: passives_by_discipline
                    .remove(combat_discipline_id.as_str())
                    .unwrap_or_default(),
                combat_discipline_id,
                weapon: CombatBuildWeaponInput {
                    main_hand_item_def_id: row.main_hand_item_def_id,
                    main_hand_color_id: row.main_hand_color_id,
                    off_hand_item_def_id: row.off_hand_item_def_id,
                    off_hand_color_id: row.off_hand_color_id,
                },
            }
        })
        .collect();
    discipline_configurations
        .sort_by(|left, right| left.combat_discipline_id.cmp(&right.combat_discipline_id));

    Some(MyCombatBuild {
        owner: build.owner,
        starting_discipline_id: build.starting_discipline_id,
        revision: build.revision,
        selected_disciplines,
        discipline_configurations,
        updated_at: build.updated_at,
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
    ensure_default_combat_build(ctx, ctx.sender())?;
    Ok(())
}

#[reducer]
pub fn save_combat_build(ctx: &ReducerContext, draft: CombatBuildDraftInput) -> Result<(), String> {
    ensure_hub_player(ctx, ctx.sender());
    let expected_revision = ctx
        .db
        .combat_build()
        .owner()
        .find(ctx.sender())
        .ok_or_else(|| {
            "COMBAT_BUILD_NOT_INITIALIZED: caller has no canonical combat build".to_string()
        })?
        .revision;
    let starting_discipline_id = draft.starting_discipline_id.clone();
    let contract_draft = contract_draft_from_input(draft);
    let catalog = CombatBuildCatalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_CATALOG_INVALID: {error}"))?;
    let validated = catalog
        .validate_draft(&contract_draft, expected_revision)
        .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))?;
    replace_combat_build(ctx, ctx.sender(), starting_discipline_id, validated);
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
pub fn request_unranked_2v2_bot_match(
    ctx: &ReducerContext,
    client_request_id: String,
) -> Result<(), String> {
    let client_request_id = validate_identifier("client request id", client_request_id, 8, 64)?;
    let player_identity = ctx.sender();
    ensure_hub_player(ctx, player_identity);
    ensure_hub_loadout_catalogs(ctx)?;
    ensure_default_hub_player_loadout(ctx, player_identity)?;
    ensure_default_combat_build(ctx, player_identity)?;

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
    ensure_default_hub_player_loadout(ctx, player_identity)?;
    ensure_default_combat_build(ctx, player_identity)?;

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

#[derive(Deserialize)]
struct HubProgressionCatalogFile {
    combat_build_contract: HubCombatBuildContractAuthoring,
    combat_disciplines: Vec<HubDisciplineAuthoring>,
    abilities: Vec<HubAbilityAuthoring>,
    #[serde(default)]
    action_presentations: Vec<HubActionPresentationAuthoring>,
}

#[derive(Deserialize)]
struct HubCombatBuildContractAuthoring {
    schema_version: u32,
    combat_disciplines: Vec<HubCombatBuildDisciplineAuthoring>,
    spell_schools: Vec<HubSpellSchoolAuthoring>,
    rules: HubCombatBuildRulesAuthoring,
}

#[derive(Deserialize)]
struct HubCombatBuildDisciplineAuthoring {
    combat_discipline_id: String,
    display_name: String,
    sort_order: u32,
}

#[derive(Deserialize)]
struct HubSpellSchoolAuthoring {
    spell_school_id: String,
    display_name: String,
    sort_order: u32,
}

#[derive(Deserialize)]
struct HubCombatBuildRulesAuthoring {
    minimum_selected_disciplines: u32,
    maximum_selected_disciplines: u32,
    minimum_staff_schools_when_selected: u32,
    maximum_staff_schools_when_selected: u32,
    combined_ability_budget: u32,
    maximum_active_abilities: u32,
    minimum_counted_abilities_per_selected_discipline: u32,
    action_slot_ids: Vec<String>,
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
    selection_kind: String,
    combat_discipline_id: Option<String>,
    spell_school_id: Option<String>,
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
    combat_discipline_id: String,
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
    hub_armor_set("PEASANT_BL", "Blue Peasant Attire", "LIGHT", 6, 60),
    hub_armor_set("PEASANT_RD", "Red Peasant Attire", "LIGHT", 6, 61),
    hub_armor_set("FMAGE_BL", "Blue Mage Vestments", "LIGHT", 7, 70),
    hub_armor_set("FMAGE_GN", "Green Mage Vestments", "LIGHT", 7, 71),
    hub_armor_set("FMAGE_RD", "Red Mage Vestments", "LIGHT", 7, 72),
    hub_armor_set("WARLOCK_GN", "Green Warlock Vestments", "LIGHT", 7, 80),
    hub_armor_set("WARLOCK_PE", "Purple Warlock Vestments", "LIGHT", 7, 81),
    hub_armor_set("WARLOCK_VT", "Violet Warlock Vestments", "LIGHT", 7, 82),
    hub_armor_set("WIZARD_BL", "Blue Wizard Vestments", "LIGHT", 7, 90),
    hub_armor_set("WIZARD_PE", "Purple Wizard Vestments", "LIGHT", 7, 91),
    hub_armor_set("WIZARD_VT", "Violet Wizard Vestments", "LIGHT", 7, 92),
    hub_armor_set("CLERIC_BL", "Blue Cleric Vestments", "LIGHT", 7, 100),
    hub_armor_set("CLERIC_GO", "Gold Cleric Vestments", "LIGHT", 7, 101),
    hub_armor_set("CLERIC_WH", "White Cleric Vestments", "LIGHT", 7, 102),
    hub_armor_set("NMAGE_BL", "Blue Northern Mage Vestments", "LIGHT", 7, 110),
    hub_armor_set("NMAGE_GN", "Green Northern Mage Vestments", "LIGHT", 7, 111),
    hub_armor_set("NMAGE_RD", "Red Northern Mage Vestments", "LIGHT", 7, 112),
    hub_armor_set("NECR_BL", "Blue Necromancer Vestments", "LIGHT", 7, 120),
    hub_armor_set("NECR_GR", "Gray Necromancer Vestments", "LIGHT", 7, 121),
    hub_armor_set("NECR_PE", "Purple Necromancer Vestments", "LIGHT", 7, 122),
    hub_armor_set("SKEEPER_BK", "Black Soul Keeper Vestments", "LIGHT", 7, 130),
    hub_armor_set("SKEEPER_GN", "Green Soul Keeper Vestments", "LIGHT", 7, 131),
    hub_armor_set(
        "SKEEPER_PE",
        "Purple Soul Keeper Vestments",
        "LIGHT",
        7,
        132,
    ),
    hub_armor_set("SKEEPER_RD", "Red Soul Keeper Vestments", "LIGHT", 7, 133),
    hub_armor_set("SMAGE_BL", "Blue Storm Mage Vestments", "LIGHT", 6, 140),
    hub_armor_set("SMAGE_CN", "Cyan Storm Mage Vestments", "LIGHT", 6, 141),
    hub_armor_set("SMAGE_RD", "Red Storm Mage Vestments", "LIGHT", 6, 142),
    hub_armor_set("NARCHER_BL", "Blue Archer Leathers", "MEDIUM", 5, 180),
    hub_armor_set("NARCHER_GN", "Green Archer Leathers", "MEDIUM", 5, 181),
    hub_armor_set("NARCHER_RD", "Red Archer Leathers", "MEDIUM", 5, 182),
    hub_armor_set(
        "NARCHER_OLD_BL",
        "Weathered Blue Archer Leathers",
        "MEDIUM",
        5,
        183,
    ),
    hub_armor_set(
        "NARCHER_OLD_GN",
        "Weathered Green Archer Leathers",
        "MEDIUM",
        5,
        184,
    ),
    hub_armor_set(
        "NARCHER_OLD_PE",
        "Weathered Purple Archer Leathers",
        "MEDIUM",
        5,
        185,
    ),
    hub_armor_set(
        "NARCHER_OLD_WH",
        "Weathered White Archer Leathers",
        "MEDIUM",
        5,
        186,
    ),
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
    hub_armor_set("DRUID_BL", "Blue Druid Leathers", "MEDIUM", 7, 260),
    hub_armor_set("DRUID_RD", "Red Druid Leathers", "MEDIUM", 7, 261),
    hub_armor_set("DRUID_YE", "Yellow Druid Leathers", "MEDIUM", 7, 262),
    hub_armor_set("THIEF_BK", "Black Thief Leathers", "MEDIUM", 7, 270),
    hub_armor_set("THIEF_BR", "Brown Thief Leathers", "MEDIUM", 7, 271),
    hub_armor_set("THIEF_GN", "Green Thief Leathers", "MEDIUM", 7, 272),
    hub_armor_set("THIEF_RD", "Red Thief Leathers", "MEDIUM", 7, 273),
    hub_armor_set(
        "TOMBSEEKER_GN",
        "Green Tomb Seeker Leathers",
        "MEDIUM",
        7,
        280,
    ),
    hub_armor_set(
        "TOMBSEEKER_PE",
        "Purple Tomb Seeker Leathers",
        "MEDIUM",
        7,
        281,
    ),
    hub_armor_set(
        "TOMBSEEKER_RD",
        "Red Tomb Seeker Leathers",
        "MEDIUM",
        7,
        282,
    ),
    hub_armor_set(
        "TOMBSEEKER_WH",
        "White Tomb Seeker Leathers",
        "MEDIUM",
        7,
        283,
    ),
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
    hub_armor_set("DBRINGER_BK", "Black Deathbringer Plate", "MEDIUM", 7, 450),
    hub_armor_set("DBRINGER_BL", "Blue Deathbringer Plate", "MEDIUM", 7, 451),
    hub_armor_set("DBRINGER_GN", "Green Deathbringer Plate", "MEDIUM", 7, 452),
    hub_armor_set("DBRINGER_RD", "Red Deathbringer Plate", "MEDIUM", 7, 453),
    hub_armor_set("FOOTMAN_BL", "Blue Footman Plate", "HEAVY", 7, 460),
    hub_armor_set("FOOTMAN_GO", "Gold Footman Plate", "HEAVY", 7, 461),
    hub_armor_set("FOOTMAN_GR", "Gray Footman Plate", "HEAVY", 7, 462),
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
        .iter()
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

    sync_canonical_combat_build_catalogs(ctx, &authored, &weapon_catalog, &descriptions)?;

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
    reconcile_hub_player_loadouts_for_catalog(ctx);

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

fn sync_canonical_combat_build_catalogs(
    ctx: &ReducerContext,
    authored: &HubProgressionCatalogFile,
    weapon_catalog: &HubWeaponAppearanceCatalogFile,
    descriptions: &HashMap<String, String>,
) -> Result<(), String> {
    let contract = &authored.combat_build_contract;
    let rules = &contract.rules;
    let rule_row = HubCombatBuildContractDefinition {
        singleton_id: 0,
        schema_version: contract.schema_version,
        minimum_selected_disciplines: rules.minimum_selected_disciplines,
        maximum_selected_disciplines: rules.maximum_selected_disciplines,
        minimum_staff_schools_when_selected: rules.minimum_staff_schools_when_selected,
        maximum_staff_schools_when_selected: rules.maximum_staff_schools_when_selected,
        combined_ability_budget: rules.combined_ability_budget,
        maximum_active_abilities: rules.maximum_active_abilities,
        minimum_counted_abilities_per_selected_discipline: rules
            .minimum_counted_abilities_per_selected_discipline,
        action_slot_ids: rules.action_slot_ids.clone(),
    };
    match ctx
        .db
        .hub_combat_build_contract_definition()
        .singleton_id()
        .find(0)
    {
        Some(existing) if existing == rule_row => {}
        Some(_) => {
            ctx.db
                .hub_combat_build_contract_definition()
                .singleton_id()
                .update(rule_row);
        }
        None => {
            ctx.db
                .hub_combat_build_contract_definition()
                .insert(rule_row);
        }
    }

    let mut discipline_rows = Vec::with_capacity(contract.combat_disciplines.len());
    for discipline in &contract.combat_disciplines {
        let combat_discipline_id = normalize_authored_id(discipline.combat_discipline_id.as_str());
        let (main_hand, main_color, off_hand, off_color) =
            starter_weapon_projection(weapon_catalog, combat_discipline_id.as_str())?;
        discipline_rows.push(HubCombatBuildDisciplineDefinition {
            combat_discipline_id,
            display_name: discipline.display_name.trim().to_string(),
            sort_order: discipline.sort_order,
            starter_main_hand_item_def_id: main_hand,
            starter_main_hand_color_id: main_color,
            starter_off_hand_item_def_id: off_hand,
            starter_off_hand_color_id: off_color,
        });
    }
    let discipline_ids: HashSet<String> = discipline_rows
        .iter()
        .map(|row| row.combat_discipline_id.clone())
        .collect();
    for row in discipline_rows {
        match ctx
            .db
            .hub_combat_build_discipline_definition()
            .combat_discipline_id()
            .find(row.combat_discipline_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db
                    .hub_combat_build_discipline_definition()
                    .combat_discipline_id()
                    .update(row);
            }
            None => {
                ctx.db.hub_combat_build_discipline_definition().insert(row);
            }
        }
    }
    let stale_discipline_ids: Vec<String> = ctx
        .db
        .hub_combat_build_discipline_definition()
        .iter()
        .map(|row| row.combat_discipline_id)
        .filter(|id| !discipline_ids.contains(id))
        .collect();
    for id in stale_discipline_ids {
        ctx.db
            .hub_combat_build_discipline_definition()
            .combat_discipline_id()
            .delete(id);
    }

    let school_rows: Vec<HubSpellSchoolDefinition> = contract
        .spell_schools
        .iter()
        .map(|school| HubSpellSchoolDefinition {
            spell_school_id: normalize_authored_id(school.spell_school_id.as_str()),
            display_name: school.display_name.trim().to_string(),
            sort_order: school.sort_order,
        })
        .collect();
    let school_ids: HashSet<String> = school_rows
        .iter()
        .map(|row| row.spell_school_id.clone())
        .collect();
    for row in school_rows {
        match ctx
            .db
            .hub_spell_school_definition()
            .spell_school_id()
            .find(row.spell_school_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db
                    .hub_spell_school_definition()
                    .spell_school_id()
                    .update(row);
            }
            None => {
                ctx.db.hub_spell_school_definition().insert(row);
            }
        }
    }
    let stale_school_ids: Vec<String> = ctx
        .db
        .hub_spell_school_definition()
        .iter()
        .map(|row| row.spell_school_id)
        .filter(|id| !school_ids.contains(id))
        .collect();
    for id in stale_school_ids {
        ctx.db
            .hub_spell_school_definition()
            .spell_school_id()
            .delete(id);
    }

    let ability_rows: Vec<HubCombatBuildAbilityDefinition> = authored
        .abilities
        .iter()
        .filter(|ability| normalize_authored_id(ability.actor_scope.as_str()) == "PLAYER")
        .filter(|ability| {
            matches!(
                normalize_authored_id(ability.selection_kind.as_str()).as_str(),
                "ACTIVE" | "PASSIVE"
            )
        })
        .filter_map(|ability| {
            let combat_discipline_id = ability.combat_discipline_id.as_ref()?;
            let ability_id = normalize_authored_id(ability.ability_id.as_str());
            Some(HubCombatBuildAbilityDefinition {
                description: descriptions.get(&ability_id).cloned().unwrap_or_default(),
                ability_id,
                combat_discipline_id: normalize_authored_id(combat_discipline_id.as_str()),
                spell_school_id: ability
                    .spell_school_id
                    .as_ref()
                    .map(|id| normalize_authored_id(id.as_str())),
                selection_kind: normalize_authored_id(ability.selection_kind.as_str()),
                display_name: ability.display_name.trim().to_string(),
                resource_kind: normalize_authored_id(ability.resource_kind.as_str()),
                resource_cost: ability.resource_cost,
                sort_order: ability.sort_order,
            })
        })
        .collect();
    let ability_ids: HashSet<String> = ability_rows
        .iter()
        .map(|row| row.ability_id.clone())
        .collect();
    for row in ability_rows {
        match ctx
            .db
            .hub_combat_build_ability_definition()
            .ability_id()
            .find(row.ability_id.clone())
        {
            Some(existing) if existing == row => {}
            Some(_) => {
                ctx.db
                    .hub_combat_build_ability_definition()
                    .ability_id()
                    .update(row);
            }
            None => {
                ctx.db.hub_combat_build_ability_definition().insert(row);
            }
        }
    }
    let stale_ability_ids: Vec<String> = ctx
        .db
        .hub_combat_build_ability_definition()
        .iter()
        .map(|row| row.ability_id)
        .filter(|id| !ability_ids.contains(id))
        .collect();
    for id in stale_ability_ids {
        ctx.db
            .hub_combat_build_ability_definition()
            .ability_id()
            .delete(id);
    }

    Ok(())
}

fn starter_weapon_projection(
    weapon_catalog: &HubWeaponAppearanceCatalogFile,
    combat_discipline_id: &str,
) -> Result<(String, String, String, String), String> {
    let mut main_hands: Vec<&HubWeaponFamilyAuthoring> = weapon_catalog
        .families
        .iter()
        .filter(|weapon| {
            normalize_authored_id(weapon.combat_discipline_id.as_str()) == combat_discipline_id
                && normalize_authored_id(weapon.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
        })
        .collect();
    main_hands.sort_by(|left, right| {
        (left.sort_order, left.item_def_id.as_str())
            .cmp(&(right.sort_order, right.item_def_id.as_str()))
    });
    let main_hand = if combat_discipline_id == COMBAT_PROFILE_STAFF {
        main_hands
            .iter()
            .copied()
            .find(|weapon| {
                normalize_authored_id(weapon.item_def_id.as_str()) == DEFAULT_STAFF_ITEM_DEF_ID
            })
            .or_else(|| main_hands.first().copied())
    } else {
        main_hands.first().copied()
    }
    .ok_or_else(|| {
        format!("canonical discipline '{combat_discipline_id}' has no starter main-hand weapon")
    })?;

    let mut off_hands: Vec<&HubWeaponFamilyAuthoring> = weapon_catalog
        .families
        .iter()
        .filter(|weapon| {
            normalize_authored_id(weapon.combat_discipline_id.as_str()) == combat_discipline_id
                && normalize_authored_id(weapon.equip_slot.as_str()) == EQUIP_SLOT_OFF_HAND
        })
        .collect();
    off_hands.sort_by(|left, right| {
        (left.sort_order, left.item_def_id.as_str())
            .cmp(&(right.sort_order, right.item_def_id.as_str()))
    });
    let off_hand = if combat_discipline_id == "SWORD_AND_SHIELD" {
        Some(off_hands.first().copied().ok_or_else(|| {
            "canonical Sword & Shield discipline has no starter off-hand weapon".to_string()
        })?)
    } else {
        None
    };

    Ok((
        normalize_authored_id(main_hand.item_def_id.as_str()),
        normalize_authored_id(main_hand.default_color_id.as_str()),
        off_hand
            .map(|weapon| normalize_authored_id(weapon.item_def_id.as_str()))
            .unwrap_or_default(),
        off_hand
            .map(|weapon| normalize_authored_id(weapon.default_color_id.as_str()))
            .unwrap_or_default(),
    ))
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
    let armor_hash = HUB_ARMOR_SET_SPECS
        .iter()
        .fold(HUB_CATALOG_PROJECTION_HASH, |hash, spec| {
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

fn discipline_uses_staff(discipline_id: &str) -> bool {
    let normalized = normalize_authored_id(discipline_id);
    serde_json::from_str::<HubProgressionCatalogFile>(PROGRESSION_CATALOG_JSON)
        .map(|catalog| {
            catalog.combat_disciplines.iter().any(|discipline| {
                normalize_authored_id(discipline.discipline_id.as_str()) == normalized
                    && normalize_authored_id(discipline.combat_profile_id.as_str())
                        == COMBAT_PROFILE_STAFF
            })
        })
        .unwrap_or(false)
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
        DISCIPLINE_ARCANA => {
            normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_STAFF
                && normalize_authored_id(spec.hand_requirement.as_str())
                    == HAND_REQUIREMENT_TWO_HAND
        }
        _ => false,
    }
}

fn weapon_spec_supports_primary(
    spec: &HubWeaponFamilyAuthoring,
    primary_discipline_id: &str,
    primary_uses_staff: bool,
) -> bool {
    let authored_primary = normalize_authored_id(spec.primary_discipline_id.as_str());
    authored_primary == primary_discipline_id
        || (primary_uses_staff
            && authored_primary == DISCIPLINE_ARCANA
            && normalize_authored_id(spec.weapon_kind.as_str()) == WEAPON_KIND_STAFF)
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
    let primary_uses_staff = discipline_uses_staff(primary.as_str());
    let catalog = parse_weapon_appearance_catalog().ok();
    let main_hand = catalog.as_ref().and_then(|catalog| {
        let eligible = |spec: &&HubWeaponFamilyAuthoring| {
            weapon_spec_supports_primary(spec, primary.as_str(), primary_uses_staff)
                && normalize_authored_id(spec.equip_slot.as_str()) == EQUIP_SLOT_MAIN_HAND
                && weapon_spec_contract_is_valid(spec)
        };
        if primary_uses_staff {
            catalog
                .families
                .iter()
                .find(|spec| {
                    eligible(spec)
                        && normalize_authored_id(spec.item_def_id.as_str())
                            == DEFAULT_STAFF_ITEM_DEF_ID
                })
                .or_else(|| catalog.families.iter().find(eligible))
        } else {
            catalog.families.iter().find(eligible)
        }
    });
    let off_hand = catalog
        .as_ref()
        .into_iter()
        .flat_map(|catalog| catalog.families.iter())
        .find(|spec| {
            weapon_spec_supports_primary(spec, primary.as_str(), primary_uses_staff)
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
    let primary_uses_staff = discipline_uses_staff(primary.as_str());
    let main_hand = normalize_authored_id(main_hand_item_def_id);
    let off_hand = normalize_authored_id(off_hand_item_def_id);
    if !primary_uses_staff
        && !matches!(
            primary.as_str(),
            DISCIPLINE_SUBTLETY | DISCIPLINE_WAR | DISCIPLINE_ZEAL | DISCIPLINE_PRECISION
        )
    {
        return Err(format!(
            "primary discipline '{primary}' does not support a weapon loadout"
        ));
    }

    let main_spec = weapon_spec(main_hand.as_str())
        .ok_or_else(|| format!("unknown selectable weapon '{main_hand}'"))?;
    if !weapon_spec_supports_primary(&main_spec, primary.as_str(), primary_uses_staff)
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
    ability_tags_contain(ability_tags, "ACTION_BAR_ACTION")
        || ability_tags_contain(ability_tags, "PASSIVE")
}

fn ability_tags_contain(ability_tags: &str, expected_tag: &str) -> bool {
    let expected_tag = normalize_authored_id(expected_tag);
    ability_tags
        .split(',')
        .any(|tag| normalize_authored_id(tag) == expected_tag)
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
    let validated = validated_combat_build_for_owner(ctx, player_identity)?;
    let combat_build_snapshot_json = serde_json::to_string(&validated.snapshot)
        .map_err(|error| format!("COMBAT_BUILD_SNAPSHOT_SERIALIZATION_FAILED: {error}"))?;
    let armor_set_id = ctx
        .db
        .hub_player_loadout()
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
            contract_schema_version: validated.snapshot.contract_schema_version,
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
    // Old terminal tickets may still own a pre-cutover tombstone row. Deleting
    // it is cleanup only; no request or provisioner path can create/read one.
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

fn validated_combat_build_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<ValidatedCombatBuild, String> {
    let build = ctx.db.combat_build().owner().find(owner).ok_or_else(|| {
        "COMBAT_BUILD_NOT_INITIALIZED: caller has no canonical combat build".to_string()
    })?;

    let mut selected_disciplines: Vec<_> = ctx
        .db
        .combat_build_discipline()
        .owner()
        .filter(owner)
        .map(|row| ContractSelectedDiscipline {
            slot_index: row.slot_index,
            combat_discipline_id: row.combat_discipline_id,
        })
        .collect();
    selected_disciplines.sort_by_key(|row| row.slot_index);

    let mut staff_school_ids: Vec<_> = ctx
        .db
        .staff_school_selection()
        .owner()
        .filter(owner)
        .map(|row| row.spell_school_id)
        .collect();
    staff_school_ids.sort();

    let mut assignments_by_discipline: HashMap<String, Vec<_>> = HashMap::new();
    for row in ctx
        .db
        .discipline_action_bar_assignment()
        .owner()
        .filter(owner)
    {
        assignments_by_discipline
            .entry(row.combat_discipline_id)
            .or_default()
            .push(ContractActionAssignment {
                action_slot: row.action_slot,
                ability_id: row.ability_id,
            });
    }
    for assignments in assignments_by_discipline.values_mut() {
        assignments.sort_by(|left, right| left.action_slot.cmp(&right.action_slot));
    }

    let mut passives_by_discipline: HashMap<String, Vec<_>> = HashMap::new();
    for row in ctx.db.discipline_passive_selection().owner().filter(owner) {
        passives_by_discipline
            .entry(row.combat_discipline_id)
            .or_default()
            .push(row.ability_id);
    }
    for passives in passives_by_discipline.values_mut() {
        passives.sort();
    }

    let mut found_staff_configuration = false;
    let mut discipline_configurations: Vec<_> = ctx
        .db
        .discipline_configuration()
        .owner()
        .filter(owner)
        .map(|row| {
            let combat_discipline_id = row.combat_discipline_id;
            let is_staff = combat_discipline_id == "STAFF";
            found_staff_configuration |= is_staff;
            ContractDisciplineConfiguration {
                staff_school_ids: if is_staff {
                    staff_school_ids.clone()
                } else {
                    Vec::new()
                },
                active_assignments: assignments_by_discipline
                    .remove(combat_discipline_id.as_str())
                    .unwrap_or_default(),
                passive_ability_ids: passives_by_discipline
                    .remove(combat_discipline_id.as_str())
                    .unwrap_or_default(),
                combat_discipline_id,
                weapon: ContractWeaponConfiguration {
                    main_hand_item_def_id: row.main_hand_item_def_id,
                    main_hand_color_id: row.main_hand_color_id,
                    off_hand_item_def_id: row.off_hand_item_def_id,
                    off_hand_color_id: row.off_hand_color_id,
                },
            }
        })
        .collect();
    discipline_configurations
        .sort_by(|left, right| left.combat_discipline_id.cmp(&right.combat_discipline_id));

    if !assignments_by_discipline.is_empty()
        || !passives_by_discipline.is_empty()
        || (!staff_school_ids.is_empty() && !found_staff_configuration)
    {
        return Err(
            "COMBAT_BUILD_STORAGE_INCONSISTENT: child rows have no discipline configuration"
                .to_string(),
        );
    }

    let draft = CombatBuildDraft {
        revision: build.revision,
        starting_discipline_id: build.starting_discipline_id,
        selected_disciplines,
        discipline_configurations,
    };
    CombatBuildCatalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_CATALOG_INVALID: {error}"))?
        .validate_draft(&draft, build.revision)
        .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))
}

fn contract_draft_from_input(draft: CombatBuildDraftInput) -> CombatBuildDraft {
    CombatBuildDraft {
        revision: draft.revision,
        starting_discipline_id: draft.starting_discipline_id,
        selected_disciplines: draft
            .selected_disciplines
            .into_iter()
            .map(|selected| ContractSelectedDiscipline {
                slot_index: selected.slot_index,
                combat_discipline_id: selected.combat_discipline_id,
            })
            .collect(),
        discipline_configurations: draft
            .discipline_configurations
            .into_iter()
            .map(|configuration| ContractDisciplineConfiguration {
                combat_discipline_id: configuration.combat_discipline_id,
                weapon: ContractWeaponConfiguration {
                    main_hand_item_def_id: configuration.weapon.main_hand_item_def_id,
                    main_hand_color_id: configuration.weapon.main_hand_color_id,
                    off_hand_item_def_id: configuration.weapon.off_hand_item_def_id,
                    off_hand_color_id: configuration.weapon.off_hand_color_id,
                },
                staff_school_ids: configuration.staff_school_ids,
                active_assignments: configuration
                    .active_assignments
                    .into_iter()
                    .map(|assignment| ContractActionAssignment {
                        action_slot: assignment.action_slot,
                        ability_id: assignment.ability_id,
                    })
                    .collect(),
                passive_ability_ids: configuration.passive_ability_ids,
            })
            .collect(),
    }
}

fn default_combat_build_draft() -> CombatBuildDraft {
    CombatBuildDraft {
        revision: 0,
        starting_discipline_id: None,
        selected_disciplines: vec![ContractSelectedDiscipline {
            slot_index: 0,
            combat_discipline_id: "DAGGERS".to_string(),
        }],
        discipline_configurations: vec![ContractDisciplineConfiguration {
            combat_discipline_id: "DAGGERS".to_string(),
            weapon: ContractWeaponConfiguration {
                main_hand_item_def_id: "TRAINING_DAGGER_PAIR".to_string(),
                main_hand_color_id: String::new(),
                off_hand_item_def_id: String::new(),
                off_hand_color_id: String::new(),
            },
            staff_school_ids: Vec::new(),
            active_assignments: vec![ContractActionAssignment {
                action_slot: "slot_0_0".to_string(),
                ability_id: "DAGGER_QUICK_CUT".to_string(),
            }],
            passive_ability_ids: Vec::new(),
        }],
    }
}

fn ensure_default_combat_build(ctx: &ReducerContext, identity: Identity) -> Result<(), String> {
    if ctx.db.combat_build().owner().find(identity).is_some() {
        return Ok(());
    }

    let catalog = CombatBuildCatalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_CATALOG_INVALID: {error}"))?;
    let default_draft = default_combat_build_draft();
    let validated = catalog
        .validate_draft(&default_draft, 0)
        .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))?;
    replace_combat_build(ctx, identity, None, validated);
    Ok(())
}

fn replace_combat_build(
    ctx: &ReducerContext,
    owner: Identity,
    starting_discipline_id: Option<String>,
    validated: ValidatedCombatBuild,
) {
    delete_combat_build_children(ctx, owner);

    let revision = validated.snapshot.revision.saturating_add(1);
    let build = CombatBuild {
        owner,
        starting_discipline_id,
        revision,
        updated_at: ctx.timestamp,
    };
    if ctx.db.combat_build().owner().find(owner).is_some() {
        ctx.db.combat_build().owner().update(build);
    } else {
        ctx.db.combat_build().insert(build);
    }

    for selected in validated.snapshot.selected_disciplines {
        ctx.db
            .combat_build_discipline()
            .insert(CombatBuildDiscipline {
                build_slot_key: combat_build_key(
                    owner,
                    &[selected.slot_index.to_string().as_str()],
                ),
                owner,
                slot_index: selected.slot_index,
                combat_discipline_id: selected.combat_discipline_id,
            });
    }

    for configuration in validated.snapshot.discipline_configurations {
        let discipline_id = configuration.combat_discipline_id;
        ctx.db
            .discipline_configuration()
            .insert(DisciplineConfiguration {
                owner_discipline_key: combat_build_key(owner, &[discipline_id.as_str()]),
                owner,
                combat_discipline_id: discipline_id.clone(),
                main_hand_item_def_id: configuration.weapon.main_hand_item_def_id,
                main_hand_color_id: configuration.weapon.main_hand_color_id,
                off_hand_item_def_id: configuration.weapon.off_hand_item_def_id,
                off_hand_color_id: configuration.weapon.off_hand_color_id,
            });

        for school_id in configuration.staff_school_ids {
            ctx.db
                .staff_school_selection()
                .insert(StaffSchoolSelection {
                    owner_school_key: combat_build_key(owner, &[school_id.as_str()]),
                    owner,
                    spell_school_id: school_id,
                });
        }
        for assignment in configuration.active_assignments {
            ctx.db
                .discipline_action_bar_assignment()
                .insert(DisciplineActionBarAssignment {
                    owner_discipline_slot_key: combat_build_key(
                        owner,
                        &[discipline_id.as_str(), assignment.action_slot.as_str()],
                    ),
                    owner,
                    combat_discipline_id: discipline_id.clone(),
                    action_slot: assignment.action_slot,
                    ability_id: assignment.ability_id,
                });
        }
        for ability_id in configuration.passive_ability_ids {
            ctx.db
                .discipline_passive_selection()
                .insert(DisciplinePassiveSelection {
                    owner_discipline_ability_key: combat_build_key(
                        owner,
                        &[discipline_id.as_str(), ability_id.as_str()],
                    ),
                    owner,
                    combat_discipline_id: discipline_id.clone(),
                    ability_id,
                });
        }
    }
}

fn delete_combat_build_children(ctx: &ReducerContext, owner: Identity) {
    let build_slot_keys: Vec<_> = ctx
        .db
        .combat_build_discipline()
        .owner()
        .filter(owner)
        .map(|row| row.build_slot_key)
        .collect();
    for key in build_slot_keys {
        ctx.db
            .combat_build_discipline()
            .build_slot_key()
            .delete(key);
    }

    let configuration_keys: Vec<_> = ctx
        .db
        .discipline_configuration()
        .owner()
        .filter(owner)
        .map(|row| row.owner_discipline_key)
        .collect();
    for key in configuration_keys {
        ctx.db
            .discipline_configuration()
            .owner_discipline_key()
            .delete(key);
    }

    let school_keys: Vec<_> = ctx
        .db
        .staff_school_selection()
        .owner()
        .filter(owner)
        .map(|row| row.owner_school_key)
        .collect();
    for key in school_keys {
        ctx.db
            .staff_school_selection()
            .owner_school_key()
            .delete(key);
    }

    let assignment_keys: Vec<_> = ctx
        .db
        .discipline_action_bar_assignment()
        .owner()
        .filter(owner)
        .map(|row| row.owner_discipline_slot_key)
        .collect();
    for key in assignment_keys {
        ctx.db
            .discipline_action_bar_assignment()
            .owner_discipline_slot_key()
            .delete(key);
    }

    let passive_keys: Vec<_> = ctx
        .db
        .discipline_passive_selection()
        .owner()
        .filter(owner)
        .map(|row| row.owner_discipline_ability_key)
        .collect();
    for key in passive_keys {
        ctx.db
            .discipline_passive_selection()
            .owner_discipline_ability_key()
            .delete(key);
    }
}

fn combat_build_key(owner: Identity, parts: &[&str]) -> String {
    let mut key = owner.to_hex().to_string();
    for part in parts {
        key.push(':');
        key.push_str(part);
    }
    key
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

fn reconcile_restructured_spell_school_disciplines(
    selected_ability_ids: &[String],
    primary_discipline_id: &str,
    secondary_discipline_id_1: &str,
    secondary_discipline_id_2: &str,
    ability_rows: &[HubAbilityDefinition],
) -> (String, String, String) {
    let ability_by_id: HashMap<&str, &HubAbilityDefinition> = ability_rows
        .iter()
        .map(|ability| (ability.ability_id.as_str(), ability))
        .collect();
    let mut selected_counts: HashMap<String, usize> = HashMap::new();
    for selected_id in selected_ability_ids {
        let selected_id = normalize_authored_id(selected_id);
        let Some(ability) = ability_by_id.get(selected_id.as_str()) else {
            continue;
        };
        if ability_tags_allow_discipline_selection(ability.ability_tags.as_str()) {
            *selected_counts
                .entry(ability.discipline_id.clone())
                .or_default() += 1;
        }
    }

    let mut disciplines = [
        normalize_authored_id(primary_discipline_id),
        normalize_authored_id(secondary_discipline_id_1),
        normalize_authored_id(secondary_discipline_id_2),
    ];

    let mortality_count = selected_counts
        .get(DISCIPLINE_MORTALITY)
        .copied()
        .unwrap_or_default();
    if mortality_count > 0 && !disciplines.iter().any(|id| id == DISCIPLINE_MORTALITY) {
        if let Some(index) = disciplines.iter().position(|id| id == DISCIPLINE_BLIGHT) {
            disciplines[index] = DISCIPLINE_MORTALITY.to_string();
        }
    }

    let blight_count = selected_counts
        .get(DISCIPLINE_BLIGHT)
        .copied()
        .unwrap_or_default();
    let ruin_count = selected_counts
        .get(DISCIPLINE_RUIN)
        .copied()
        .unwrap_or_default();
    if blight_count > 0
        && !disciplines.iter().any(|id| id == DISCIPLINE_BLIGHT)
        && disciplines.iter().any(|id| id == DISCIPLINE_RUIN)
    {
        if ruin_count == 0 {
            if let Some(index) = disciplines.iter().position(|id| id == DISCIPLINE_RUIN) {
                disciplines[index] = DISCIPLINE_BLIGHT.to_string();
            }
        } else if let Some(index) =
            (1..disciplines.len()).find(|index| disciplines[*index].is_empty())
        {
            disciplines[index] = DISCIPLINE_BLIGHT.to_string();
        } else if blight_count > ruin_count {
            if let Some(index) = disciplines.iter().position(|id| id == DISCIPLINE_RUIN) {
                disciplines[index] = DISCIPLINE_BLIGHT.to_string();
            }
        }
    }

    (
        disciplines[0].clone(),
        disciplines[1].clone(),
        disciplines[2].clone(),
    )
}

fn reconcile_selected_ability_ids(
    selected_ability_ids: &[String],
    primary_discipline_id: &str,
    secondary_discipline_id_1: &str,
    secondary_discipline_id_2: &str,
    ability_rows: &[HubAbilityDefinition],
) -> Vec<String> {
    let selected_disciplines: HashSet<String> = [
        primary_discipline_id,
        secondary_discipline_id_1,
        secondary_discipline_id_2,
    ]
    .into_iter()
    .map(normalize_authored_id)
    .filter(|discipline_id| !discipline_id.is_empty())
    .collect();
    let ability_by_id: HashMap<&str, &HubAbilityDefinition> = ability_rows
        .iter()
        .map(|ability| (ability.ability_id.as_str(), ability))
        .collect();
    let mut reconciled = Vec::new();
    let mut counts: HashMap<String, usize> = HashMap::new();

    for selected_id in selected_ability_ids {
        let selected_id = normalize_authored_id(selected_id.as_str());
        let Some(ability) = ability_by_id.get(selected_id.as_str()).copied() else {
            continue;
        };
        if !selected_disciplines.contains(ability.discipline_id.as_str())
            || !ability_tags_allow_discipline_selection(ability.ability_tags.as_str())
            || reconciled.contains(&ability.ability_id)
        {
            continue;
        }
        *counts.entry(ability.discipline_id.clone()).or_default() += 1;
        reconciled.push(ability.ability_id.clone());
    }

    let mut ordered_active_abilities: Vec<&HubAbilityDefinition> = ability_rows
        .iter()
        .filter(|ability| ability_tags_contain(ability.ability_tags.as_str(), "ACTION_BAR_ACTION"))
        .collect();
    ordered_active_abilities
        .sort_by_key(|ability| (ability.sort_order, ability.ability_id.as_str()));

    for (discipline_id, minimum) in [
        (
            normalize_authored_id(primary_discipline_id),
            PRIMARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
        (
            normalize_authored_id(secondary_discipline_id_1),
            SECONDARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
        (
            normalize_authored_id(secondary_discipline_id_2),
            SECONDARY_DISCIPLINE_ABILITY_MINIMUM,
        ),
    ] {
        if discipline_id.is_empty() {
            continue;
        }
        for ability in ordered_active_abilities
            .iter()
            .copied()
            .filter(|ability| ability.discipline_id == discipline_id)
        {
            if counts.get(&discipline_id).copied().unwrap_or_default() >= minimum
                || reconciled.len() >= MAX_DISCIPLINE_LOADOUT_ABILITIES
            {
                break;
            }
            if reconciled.contains(&ability.ability_id) {
                continue;
            }
            reconciled.push(ability.ability_id.clone());
            *counts.entry(discipline_id.clone()).or_default() += 1;
        }
    }

    reconciled
}

fn reconcile_hub_player_loadouts_for_catalog(ctx: &ReducerContext) {
    let ability_rows: Vec<HubAbilityDefinition> = ctx.db.hub_ability_definition().iter().collect();
    let loadouts: Vec<HubPlayerLoadout> = ctx.db.hub_player_loadout().iter().collect();
    for mut loadout in loadouts {
        let (primary, secondary_1, secondary_2) = reconcile_restructured_spell_school_disciplines(
            loadout.selected_ability_ids.as_slice(),
            loadout.primary_discipline_id.as_str(),
            loadout.secondary_discipline_id_1.as_str(),
            loadout.secondary_discipline_id_2.as_str(),
            ability_rows.as_slice(),
        );
        let reconciled = reconcile_selected_ability_ids(
            loadout.selected_ability_ids.as_slice(),
            primary.as_str(),
            secondary_1.as_str(),
            secondary_2.as_str(),
            ability_rows.as_slice(),
        );
        if primary == loadout.primary_discipline_id
            && secondary_1 == loadout.secondary_discipline_id_1
            && secondary_2 == loadout.secondary_discipline_id_2
            && reconciled == loadout.selected_ability_ids
        {
            continue;
        }
        log::info!(
            "[HUB_CATALOG] Reconciled saved disciplines and abilities for {} after catalog revision",
            &loadout.owner.to_hex()[..8]
        );
        loadout.primary_discipline_id = primary;
        loadout.secondary_discipline_id_1 = secondary_1;
        loadout.secondary_discipline_id_2 = secondary_2;
        loadout.selected_ability_ids = reconciled;
        loadout.revision = next_loadout_revision(Some(loadout.revision));
        loadout.updated_at = ctx.timestamp;
        ctx.db.hub_player_loadout().owner().update(loadout);
    }
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
    fn canonical_combat_build_default_is_deterministic_and_validator_owned() {
        let draft = default_combat_build_draft();
        let catalog = CombatBuildCatalog::from_shared_catalogs().expect("canonical catalogs");
        let validated = catalog
            .validate_draft(&draft, 0)
            .expect("canonical default must pass the production validator");

        assert_eq!(validated.active_count, 1);
        assert_eq!(validated.passive_count, 0);
        assert_eq!(validated.snapshot.starting_discipline_id, "DAGGERS");
        assert_eq!(validated.snapshot.selected_disciplines.len(), 1);
        assert_eq!(
            validated.snapshot.selected_disciplines[0].combat_discipline_id,
            "DAGGERS"
        );
        assert_eq!(
            validated.snapshot.discipline_configurations[0]
                .weapon
                .main_hand_item_def_id,
            "TRAINING_DAGGER_PAIR"
        );
        assert_eq!(
            validated.snapshot.discipline_configurations[0].active_assignments[0].ability_id,
            "DAGGER_QUICK_CUT"
        );
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
        assert_eq!(HUB_ARMOR_SET_SPECS.len(), 89);
        assert_eq!(ids.len(), HUB_ARMOR_SET_SPECS.len());
        for armor_set_id in ["DBRINGER_BK", "DBRINGER_BL", "DBRINGER_GN", "DBRINGER_RD"] {
            assert_eq!(
                HUB_ARMOR_SET_SPECS
                    .iter()
                    .find(|spec| spec.armor_set_id == armor_set_id)
                    .map(|spec| spec.armor_tier),
                Some("MEDIUM"),
                "{armor_set_id} must remain Medium armor"
            );
        }
        assert!(HUB_ARMOR_SET_SPECS.iter().all(|spec| {
            matches!(spec.armor_tier, "LIGHT" | "MEDIUM" | "HEAVY")
                && (4..=7).contains(&spec.piece_count)
        }));
        assert_eq!(
            HUB_ARMOR_SET_SPECS
                .iter()
                .filter(|spec| spec.armor_tier == "LIGHT")
                .count(),
            29
        );
        assert_eq!(
            HUB_ARMOR_SET_SPECS
                .iter()
                .filter(|spec| spec.armor_tier == "MEDIUM")
                .count(),
            41
        );
        assert_eq!(
            HUB_ARMOR_SET_SPECS
                .iter()
                .filter(|spec| spec.armor_tier == "HEAVY")
                .count(),
            19
        );
    }

    #[test]
    fn hub_weapon_catalog_is_unique_and_enforces_primary_discipline_rules() {
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

        for primary in [
            DISCIPLINE_SUBTLETY,
            DISCIPLINE_WAR,
            DISCIPLINE_ZEAL,
            DISCIPLINE_PRECISION,
            DISCIPLINE_BLIGHT,
            DISCIPLINE_MORTALITY,
            DISCIPLINE_RUIN,
            DISCIPLINE_DIVINITY,
            DISCIPLINE_ARCANA,
            DISCIPLINE_PRIMAL,
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

        for primary in [
            DISCIPLINE_BLIGHT,
            DISCIPLINE_MORTALITY,
            DISCIPLINE_RUIN,
            DISCIPLINE_DIVINITY,
            DISCIPLINE_ARCANA,
            DISCIPLINE_PRIMAL,
        ] {
            assert_eq!(
                default_weapon_loadout(primary),
                (
                    "NEWBIE_STAFF_01".to_string(),
                    "DEFAULT".to_string(),
                    String::new(),
                    String::new(),
                )
            );
        }

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
        for staff in staff_specs {
            for variant in &staff.variants {
                assert!(validate_hub_weapon_loadout(
                    DISCIPLINE_BLIGHT,
                    staff.item_def_id.as_str(),
                    variant.color_id.as_str(),
                    "",
                    ""
                )
                .is_ok());
            }
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
        assert_eq!(authored.combat_build_contract.schema_version, 1);
        assert_eq!(authored.combat_build_contract.combat_disciplines.len(), 5);
        assert_eq!(authored.combat_build_contract.spell_schools.len(), 6);
        assert_eq!(
            authored.combat_build_contract.rules.combined_ability_budget,
            20
        );
        assert_eq!(
            authored
                .combat_build_contract
                .rules
                .maximum_active_abilities,
            16
        );
        assert_eq!(
            authored.combat_build_contract.rules.action_slot_ids.len(),
            27
        );
        let weapon_catalog =
            parse_weapon_appearance_catalog().expect("canonical weapon appearance JSON");
        for discipline in &authored.combat_build_contract.combat_disciplines {
            let (main_hand, main_color, off_hand, off_color) = starter_weapon_projection(
                &weapon_catalog,
                discipline.combat_discipline_id.as_str(),
            )
            .expect("every canonical discipline needs an editor starter weapon");
            assert!(!main_hand.is_empty());
            assert!(!main_color.is_empty());
            if discipline.combat_discipline_id == "SWORD_AND_SHIELD" {
                assert!(!off_hand.is_empty());
                assert!(!off_color.is_empty());
            } else {
                assert!(off_hand.is_empty());
                assert!(off_color.is_empty());
            }
        }
        assert!(authored.abilities.iter().any(|ability| {
            ability.selection_kind == "ACTIVE"
                && ability.combat_discipline_id.as_deref() == Some("STAFF")
                && ability.spell_school_id.as_deref() == Some("RUIN")
        }));
        assert!(authored.abilities.iter().any(|ability| {
            ability.selection_kind == "PASSIVE"
                && ability.combat_discipline_id.as_deref() == Some("DAGGERS")
                && ability.spell_school_id.is_none()
        }));
        assert!(!authored.combat_disciplines.is_empty());
        assert!(authored
            .combat_disciplines
            .iter()
            .any(|discipline| discipline.discipline_id == DISCIPLINE_MORTALITY));
        for discipline_id in [
            DISCIPLINE_BLIGHT,
            DISCIPLINE_MORTALITY,
            DISCIPLINE_RUIN,
            DISCIPLINE_DIVINITY,
            DISCIPLINE_ARCANA,
            DISCIPLINE_PRIMAL,
        ] {
            let discipline = authored
                .combat_disciplines
                .iter()
                .find(|discipline| discipline.discipline_id == discipline_id)
                .expect("magical discipline must be authored");
            assert_eq!(discipline.combat_profile_id, COMBAT_PROFILE_STAFF);
        }
        assert!(authored.abilities.iter().any(|ability| {
            ability.ability_id == "SPELL_NECROTIC_AURA"
                && ability.discipline_id == DISCIPLINE_MORTALITY
        }));
        assert!(authored.abilities.iter().any(|ability| {
            ability.ability_id == "SPELL_ICICLE" && ability.discipline_id == DISCIPLINE_BLIGHT
        }));
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
    fn catalog_reconciliation_drops_stale_or_misowned_abilities_and_restores_minimums() {
        let mut abilities: Vec<HubAbilityDefinition> = (1..=8)
            .map(|index| HubAbilityDefinition {
                ability_id: format!("SUBTLETY_{index}"),
                discipline_id: DISCIPLINE_SUBTLETY.to_string(),
                display_name: format!("Subtlety {index}"),
                resource_kind: "STAMINA".to_string(),
                resource_cost: 0.0,
                ability_tags: "ACTION_BAR_ACTION".to_string(),
                description: String::new(),
                sort_order: index,
            })
            .collect();
        abilities.push(HubAbilityDefinition {
            ability_id: "BLIGHT_ACTION".to_string(),
            discipline_id: "BLIGHT".to_string(),
            display_name: "Blight Action".to_string(),
            resource_kind: "MANA".to_string(),
            resource_cost: 0.0,
            ability_tags: "ACTION_BAR_ACTION".to_string(),
            description: String::new(),
            sort_order: 1,
        });
        let selected = [
            "SUBTLETY_1",
            "SUBTLETY_2",
            "SUBTLETY_3",
            "SUBTLETY_4",
            "SUBTLETY_5",
            "SUBTLETY_6",
            "SUBTLETY_7",
            "REMOVED_ACTION",
            "BLIGHT_ACTION",
        ]
        .into_iter()
        .map(str::to_string)
        .collect::<Vec<_>>();

        assert_eq!(
            reconcile_selected_ability_ids(
                selected.as_slice(),
                DISCIPLINE_SUBTLETY,
                "BLIGHT",
                "",
                abilities.as_slice(),
            ),
            [
                "SUBTLETY_1",
                "SUBTLETY_2",
                "SUBTLETY_3",
                "SUBTLETY_4",
                "SUBTLETY_5",
                "SUBTLETY_6",
                "SUBTLETY_7",
                "BLIGHT_ACTION",
                "SUBTLETY_8",
            ]
        );
        assert_eq!(
            reconcile_selected_ability_ids(
                selected.as_slice(),
                DISCIPLINE_SUBTLETY,
                "",
                "",
                abilities.as_slice(),
            ),
            [
                "SUBTLETY_1",
                "SUBTLETY_2",
                "SUBTLETY_3",
                "SUBTLETY_4",
                "SUBTLETY_5",
                "SUBTLETY_6",
                "SUBTLETY_7",
                "SUBTLETY_8",
            ]
        );
    }

    #[test]
    fn catalog_reconciliation_migrates_restructured_spell_school_slots() {
        let ability = |ability_id: &str, discipline_id: &str| HubAbilityDefinition {
            ability_id: ability_id.to_string(),
            discipline_id: discipline_id.to_string(),
            display_name: ability_id.to_string(),
            resource_kind: "MANA".to_string(),
            resource_cost: 0.0,
            ability_tags: "ACTION_BAR_ACTION".to_string(),
            description: String::new(),
            sort_order: 1,
        };
        let abilities = [
            ability("NECROTIC_AURA", DISCIPLINE_MORTALITY),
            ability("ICICLE", DISCIPLINE_BLIGHT),
            ability("FIREBALL", DISCIPLINE_RUIN),
            ability("FROST_NOVA", DISCIPLINE_BLIGHT),
        ];

        assert_eq!(
            reconcile_restructured_spell_school_disciplines(
                &["NECROTIC_AURA".to_string(), "ICICLE".to_string()],
                DISCIPLINE_BLIGHT,
                DISCIPLINE_RUIN,
                "",
                &abilities,
            ),
            (
                DISCIPLINE_MORTALITY.to_string(),
                DISCIPLINE_BLIGHT.to_string(),
                String::new(),
            )
        );
        assert_eq!(
            reconcile_restructured_spell_school_disciplines(
                &["ICICLE".to_string()],
                DISCIPLINE_RUIN,
                "",
                "",
                &abilities,
            ),
            (DISCIPLINE_BLIGHT.to_string(), String::new(), String::new(),)
        );
        assert_eq!(
            reconcile_restructured_spell_school_disciplines(
                &[
                    "ICICLE".to_string(),
                    "FROST_NOVA".to_string(),
                    "FIREBALL".to_string(),
                ],
                DISCIPLINE_RUIN,
                DISCIPLINE_SUBTLETY,
                DISCIPLINE_WAR,
                &abilities,
            ),
            (
                DISCIPLINE_BLIGHT.to_string(),
                DISCIPLINE_SUBTLETY.to_string(),
                DISCIPLINE_WAR.to_string(),
            )
        );
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

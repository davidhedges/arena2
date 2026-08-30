//! Isolated Combat Build v2 Hub persistence rehearsal.
//!
//! This crate is intentionally separate from `hub-server`. It exercises the
//! future durable aggregate and reducer surface without changing the canonical
//! v1 Hub schema, subscriptions, or player data.

use spacetimedb::{
    reducer, table, view, Identity, ReducerContext, SpacetimeType, Table, Timestamp, ViewContext,
};

#[path = "../../server/src/combat_build_v2.rs"]
#[allow(dead_code)]
mod combat_build_v2_contract;

use combat_build_v2_contract::{
    CombatBuildV2Catalog, CombatBuildV2DisciplineConfiguration, CombatBuildV2Draft,
    CombatFeatureSelection, CombatSpecializationKind, SelectedCombatSpecialization,
    ValidatedCombatBuildV2, COMBAT_BUILD_V2_SCHEMA_VERSION, MASTERY_TRAIT_ID,
};
#[cfg(test)]
use combat_build_v2_contract::{CombatBuildV2Snapshot, CombatFeatureLoadoutKind};

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

#[table(accessor = phase2_probe_result, public)]
#[derive(Clone)]
pub struct Phase2ProbeResult {
    #[primary_key]
    pub owner: Identity,
    pub final_revision: u64,
    pub three_school_reload_passed: bool,
    pub same_parent_reload_passed: bool,
    pub dormant_restore_passed: bool,
    pub stale_rejection_passed: bool,
    pub invalid_rollback_passed: bool,
    pub mastery_predicate_passed: bool,
    pub completed_at: Timestamp,
}

/// Phase-owned equivalent of the canonical v1 frozen-ticket row. The JSON is
/// represented as hex only so the shell rehearsal can transport exact bytes
/// between disposable identities without terminal quoting changing them.
#[table(accessor = match_player_combat_build_snapshot_v2, public)]
#[derive(Clone)]
pub struct MatchPlayerCombatBuildSnapshotV2 {
    #[primary_key]
    pub ticket_id: String,
    #[index(btree)]
    pub player_identity: Identity,
    pub contract_schema_version: u32,
    pub combat_build_revision: u64,
    pub combat_build_snapshot_json_hex: String,
    pub armor_set_id: String,
    pub captured_at: Timestamp,
}

#[table(accessor = phase3_handoff_result, public)]
#[derive(Clone)]
pub struct Phase3HandoffResult {
    #[primary_key]
    pub owner: Identity,
    pub ticket_id: String,
    pub contract_schema_version: u32,
    pub combat_build_revision: u64,
    pub snapshot_json_hex: String,
    pub selected_specialization_count: u32,
    pub parent_discipline_count: u32,
    pub technique_count: u32,
    pub spell_count: u32,
    pub perk_count: u32,
    pub trait_count: u32,
    pub mastery_active: bool,
    pub completed_at: Timestamp,
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

#[view(accessor = my_combat_build_v2, public)]
pub fn my_combat_build_v2(ctx: &ViewContext) -> Option<MyCombatBuildV2> {
    read_my_combat_build_v2(ctx, ctx.sender())
}

#[reducer(init)]
pub fn init(ctx: &ReducerContext) -> Result<(), String> {
    sync_catalog_definitions(ctx)
}

#[reducer(client_connected)]
pub fn client_connected(ctx: &ReducerContext) -> Result<(), String> {
    ensure_default_combat_build_v2(ctx, ctx.sender())
}

#[reducer]
pub fn save_combat_build_v2(
    ctx: &ReducerContext,
    draft: CombatBuildV2DraftInput,
) -> Result<(), String> {
    ensure_default_combat_build_v2(ctx, ctx.sender())?;
    save_for_owner(ctx, ctx.sender(), input_to_contract(draft))
}

/// One anonymous call exercises the live reducer/storage surface. The entire
/// module is disposable and this reducer is intentionally not copied into the
/// canonical Hub.
#[reducer]
pub fn run_phase2_live_probe(ctx: &ReducerContext) -> Result<(), String> {
    let owner = ctx.sender();
    ensure_default_combat_build_v2(ctx, owner)?;

    let three_schools = CombatBuildV2Draft {
        schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
        revision: current_revision(ctx, owner)?,
        starting_discipline_id: Some("STAFF".to_string()),
        selected_specializations: selected(&["BLIGHT", "MORTALITY", "RUIN"]),
        dormant_specializations: Vec::new(),
        discipline_configurations: vec![staff_configuration()],
        selected_features: vec![
            feature("BLIGHT", "SPELL_ICICLE", 0),
            feature("MORTALITY", "SPELL_VAMPIRIC_ORB", 1),
            feature("RUIN", "SPELL_FIREBALL", 2),
        ],
        selected_traits: vec![MASTERY_TRAIT_ID.to_string()],
    };
    save_for_owner(ctx, owner, three_schools)?;
    let three_school_validated = validated_for_owner(ctx, owner)?;
    let three_school_reload_passed = three_school_validated.projection.parent_discipline_ids
        == ["STAFF"]
        && three_school_validated.projection.technique_bars.is_empty()
        && three_school_validated.projection.spell_ability_ids.len() == 3;
    let three_school_mastery = three_school_validated.projection.mastery_active;

    let dormant = CombatBuildV2Draft {
        schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
        revision: current_revision(ctx, owner)?,
        starting_discipline_id: Some("DAGGERS".to_string()),
        selected_specializations: selected(&["DAGGERS_BLADEDANCER"]),
        dormant_specializations: vec!["DAGGERS_EXECUTIONER".to_string()],
        discipline_configurations: vec![dagger_configuration()],
        selected_features: vec![
            feature("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", 0),
            feature("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", 0),
        ],
        selected_traits: vec![MASTERY_TRAIT_ID.to_string()],
    };
    save_for_owner(ctx, owner, dormant)?;
    let dormant_reload = validated_for_owner(ctx, owner)?;
    let dormant_restore_passed = dormant_reload
        .snapshot
        .selected_features
        .iter()
        .find(|row| row.ability_id == "DAGGER_GUT_RIPPER")
        .is_some_and(|row| row.preferred_bar_order == Some(0))
        && dormant_reload.selected_feature_count() == 1;

    let returning = CombatBuildV2Draft {
        schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
        revision: current_revision(ctx, owner)?,
        starting_discipline_id: Some("DAGGERS".to_string()),
        selected_specializations: selected(&["DAGGERS_BLADEDANCER", "DAGGERS_EXECUTIONER"]),
        dormant_specializations: Vec::new(),
        discipline_configurations: vec![dagger_configuration()],
        selected_features: vec![
            feature("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", 0),
            feature("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", 0),
        ],
        selected_traits: vec![MASTERY_TRAIT_ID.to_string()],
    };
    save_for_owner(ctx, owner, returning)?;
    let returned = validated_for_owner(ctx, owner)?;
    let same_parent_reload_passed = returned.projection.parent_discipline_ids == ["DAGGERS"]
        && returned.projection.technique_bars.len() == 1
        && returned.projection.technique_bars[0].ability_ids
            == ["DAGGER_QUICK_CUT", "DAGGER_GUT_RIPPER"];
    let mastery_predicate_passed = three_school_mastery && returned.projection.mastery_active;

    let before_rejections = draft_for_owner(ctx, owner)?;
    let mut stale = before_rejections.clone();
    stale.revision = stale.revision.saturating_sub(1);
    let stale_rejection_passed = save_for_owner(ctx, owner, stale)
        .is_err_and(|error| error.starts_with("COMBAT_BUILD_V2_STALE_REVISION"));
    let mut invalid = before_rejections.clone();
    invalid.selected_features.push(CombatFeatureSelection {
        specialization_id: "DAGGERS_BLADEDANCER".to_string(),
        ability_id: "STAFF_STRIKE".to_string(),
        preferred_bar_order: Some(2),
    });
    let invalid_rejected = save_for_owner(ctx, owner, invalid)
        .is_err_and(|error| error.starts_with("COMBAT_BUILD_V2_UNKNOWN_FEATURE"));
    let after_rejections = draft_for_owner(ctx, owner)?;
    let invalid_rollback_passed = invalid_rejected && before_rejections == after_rejections;

    if !(three_school_reload_passed
        && same_parent_reload_passed
        && dormant_restore_passed
        && stale_rejection_passed
        && invalid_rollback_passed
        && mastery_predicate_passed)
    {
        return Err("PHASE2_LIVE_PROBE_FAILED: one or more checks did not pass".to_string());
    }

    let row = Phase2ProbeResult {
        owner,
        final_revision: current_revision(ctx, owner)?,
        three_school_reload_passed,
        same_parent_reload_passed,
        dormant_restore_passed,
        stale_rejection_passed,
        invalid_rollback_passed,
        mastery_predicate_passed,
        completed_at: ctx.timestamp,
    };
    if ctx.db.phase2_probe_result().owner().find(owner).is_some() {
        ctx.db.phase2_probe_result().owner().update(row);
    } else {
        ctx.db.phase2_probe_result().insert(row);
    }
    Ok(())
}

/// Freezes a three-School aggregate into exact canonical v2 snapshot bytes.
/// This is a rehearsal-only ticket seam and is never copied into the
/// canonical Hub before the coordinated cutover.
#[reducer]
pub fn prepare_phase3_three_school_handoff(ctx: &ReducerContext) -> Result<(), String> {
    let owner = ctx.sender();
    ensure_default_combat_build_v2(ctx, owner)?;
    let draft = CombatBuildV2Draft {
        schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
        revision: current_revision(ctx, owner)?,
        starting_discipline_id: Some("STAFF".to_string()),
        selected_specializations: selected(&["BLIGHT", "MORTALITY", "RUIN"]),
        dormant_specializations: Vec::new(),
        discipline_configurations: vec![staff_configuration()],
        selected_features: vec![
            feature("BLIGHT", "SPELL_ICICLE", 2),
            CombatFeatureSelection {
                specialization_id: "BLIGHT".to_string(),
                ability_id: "BLIGHT_TOXIC_WEAPON".to_string(),
                preferred_bar_order: None,
            },
            feature("MORTALITY", "SPELL_VAMPIRIC_ORB", 1),
            feature("RUIN", "SPELL_FIREBALL", 0),
        ],
        selected_traits: vec![MASTERY_TRAIT_ID.to_string()],
    };
    save_for_owner(ctx, owner, draft)?;

    let catalog = CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?;
    let validated = validated_for_owner(ctx, owner)?;
    let snapshot_json = catalog.serialize_canonical_snapshot(&validated.snapshot)?;
    let plan = catalog.materialization_plan(&validated)?;
    if plan.parent_discipline_ids != ["STAFF"]
        || !plan.techniques.is_empty()
        || plan.spells.len() != 3
        || plan.perks.len() != 1
        || plan.traits != [MASTERY_TRAIT_ID]
        || !plan.mastery_active
    {
        return Err("PHASE3_HUB_HANDOFF_INVALID: materialization plan diverged".to_string());
    }

    let ticket_id = format!("phase3-{}", &owner.to_hex()[..16]);
    let snapshot_json_hex = hex_encode(snapshot_json.as_bytes());
    let frozen = MatchPlayerCombatBuildSnapshotV2 {
        ticket_id: ticket_id.clone(),
        player_identity: owner,
        contract_schema_version: validated.snapshot.schema_version,
        combat_build_revision: validated.snapshot.revision,
        combat_build_snapshot_json_hex: snapshot_json_hex.clone(),
        armor_set_id: "IRON".to_string(),
        captured_at: ctx.timestamp,
    };
    if ctx
        .db
        .match_player_combat_build_snapshot_v2()
        .ticket_id()
        .find(ticket_id.clone())
        .is_some()
    {
        ctx.db
            .match_player_combat_build_snapshot_v2()
            .ticket_id()
            .update(frozen);
    } else {
        ctx.db
            .match_player_combat_build_snapshot_v2()
            .insert(frozen);
    }

    let result = Phase3HandoffResult {
        owner,
        ticket_id,
        contract_schema_version: validated.snapshot.schema_version,
        combat_build_revision: validated.snapshot.revision,
        snapshot_json_hex,
        selected_specialization_count: plan.selected_specializations.len() as u32,
        parent_discipline_count: plan.parent_discipline_ids.len() as u32,
        technique_count: plan.techniques.len() as u32,
        spell_count: plan.spells.len() as u32,
        perk_count: plan.perks.len() as u32,
        trait_count: plan.traits.len() as u32,
        mastery_active: plan.mastery_active,
        completed_at: ctx.timestamp,
    };
    if ctx.db.phase3_handoff_result().owner().find(owner).is_some() {
        ctx.db.phase3_handoff_result().owner().update(result);
    } else {
        ctx.db.phase3_handoff_result().insert(result);
    }
    Ok(())
}

fn selected(ids: &[&str]) -> Vec<SelectedCombatSpecialization> {
    ids.iter()
        .enumerate()
        .map(|(slot_index, id)| SelectedCombatSpecialization {
            slot_index: slot_index as u8,
            specialization_id: (*id).to_string(),
        })
        .collect()
}

fn feature(specialization_id: &str, ability_id: &str, order: u8) -> CombatFeatureSelection {
    CombatFeatureSelection {
        specialization_id: specialization_id.to_string(),
        ability_id: ability_id.to_string(),
        preferred_bar_order: Some(order),
    }
}

fn dagger_configuration() -> CombatBuildV2DisciplineConfiguration {
    CombatBuildV2DisciplineConfiguration {
        combat_discipline_id: "DAGGERS".to_string(),
        main_hand_item_def_id: "TRAINING_DAGGER_PAIR".to_string(),
        main_hand_color_id: String::new(),
        off_hand_item_def_id: String::new(),
        off_hand_color_id: String::new(),
    }
}

fn staff_configuration() -> CombatBuildV2DisciplineConfiguration {
    CombatBuildV2DisciplineConfiguration {
        combat_discipline_id: "STAFF".to_string(),
        main_hand_item_def_id: "NEWBIE_STAFF_01".to_string(),
        main_hand_color_id: String::new(),
        off_hand_item_def_id: String::new(),
        off_hand_color_id: String::new(),
    }
}

fn input_to_contract(input: CombatBuildV2DraftInput) -> CombatBuildV2Draft {
    CombatBuildV2Draft {
        schema_version: input.schema_version,
        revision: input.revision,
        starting_discipline_id: input.starting_discipline_id,
        selected_specializations: input
            .selected_specializations
            .into_iter()
            .map(|row| SelectedCombatSpecialization {
                slot_index: row.slot_index,
                specialization_id: row.specialization_id,
            })
            .collect(),
        dormant_specializations: input.dormant_specializations,
        discipline_configurations: input
            .discipline_configurations
            .into_iter()
            .map(|row| CombatBuildV2DisciplineConfiguration {
                combat_discipline_id: row.combat_discipline_id,
                main_hand_item_def_id: row.main_hand_item_def_id,
                main_hand_color_id: row.main_hand_color_id,
                off_hand_item_def_id: row.off_hand_item_def_id,
                off_hand_color_id: row.off_hand_color_id,
            })
            .collect(),
        selected_features: input
            .selected_features
            .into_iter()
            .map(|row| CombatFeatureSelection {
                specialization_id: row.specialization_id,
                ability_id: row.ability_id,
                preferred_bar_order: row.preferred_bar_order,
            })
            .collect(),
        selected_traits: input.selected_traits,
    }
}

#[cfg(test)]
fn contract_to_input(draft: CombatBuildV2Draft) -> CombatBuildV2DraftInput {
    CombatBuildV2DraftInput {
        schema_version: draft.schema_version,
        revision: draft.revision,
        starting_discipline_id: draft.starting_discipline_id,
        selected_specializations: draft
            .selected_specializations
            .into_iter()
            .map(|row| SelectedSpecializationV2Input {
                slot_index: row.slot_index,
                specialization_id: row.specialization_id,
            })
            .collect(),
        dormant_specializations: draft.dormant_specializations,
        discipline_configurations: draft
            .discipline_configurations
            .into_iter()
            .map(|row| DisciplineConfigurationV2Input {
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
            .map(|row| CombatFeatureSelectionV2Input {
                specialization_id: row.specialization_id,
                ability_id: row.ability_id,
                preferred_bar_order: row.preferred_bar_order,
            })
            .collect(),
        selected_traits: draft.selected_traits,
    }
}

fn save_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    draft: CombatBuildV2Draft,
) -> Result<(), String> {
    let expected_revision = current_revision(ctx, owner)?;
    let starting_discipline_id = draft.starting_discipline_id.clone();
    let catalog = CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?;
    let validated = catalog
        .validate_draft(&draft, expected_revision)
        .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))?;
    replace_combat_build_v2(ctx, owner, starting_discipline_id, validated);
    Ok(())
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

fn current_revision(ctx: &ReducerContext, owner: Identity) -> Result<u64, String> {
    ctx.db
        .combat_build_v2()
        .owner()
        .find(owner)
        .map(|row| row.revision)
        .ok_or_else(|| "COMBAT_BUILD_V2_NOT_INITIALIZED: caller has no v2 build".to_string())
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
                owner_slot_key: aggregate_key(owner, &[selected.slot_index.to_string().as_str()]),
                owner,
                slot_index: selected.slot_index,
                specialization_id: selected.specialization_id,
            });
    }
    for specialization_id in validated.snapshot.dormant_specializations {
        ctx.db
            .dormant_specialization_v2()
            .insert(DormantSpecializationV2 {
                owner_specialization_key: aggregate_key(owner, &[specialization_id.as_str()]),
                owner,
                specialization_id,
            });
    }
    for row in validated.snapshot.discipline_configurations {
        ctx.db
            .discipline_configuration_v2()
            .insert(DisciplineConfigurationV2 {
                owner_discipline_key: aggregate_key(owner, &[row.combat_discipline_id.as_str()]),
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
                owner_ability_key: aggregate_key(owner, &[row.ability_id.as_str()]),
                owner,
                specialization_id: row.specialization_id,
                ability_id: row.ability_id,
                preferred_bar_order: row.preferred_bar_order,
            });
    }
    for ability_id in validated.snapshot.selected_traits {
        ctx.db.trait_selection_v2().insert(TraitSelectionV2 {
            owner_trait_key: aggregate_key(owner, &[ability_id.as_str()]),
            owner,
            ability_id,
        });
    }
}

fn draft_for_owner(ctx: &ReducerContext, owner: Identity) -> Result<CombatBuildV2Draft, String> {
    let root = ctx
        .db
        .combat_build_v2()
        .owner()
        .find(owner)
        .ok_or_else(|| "COMBAT_BUILD_V2_NOT_INITIALIZED: caller has no v2 build".to_string())?;
    let mut selected_specializations: Vec<_> = ctx
        .db
        .selected_specialization_v2()
        .owner()
        .filter(owner)
        .map(|row| SelectedCombatSpecialization {
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
        .map(|row| CombatBuildV2DisciplineConfiguration {
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
        .map(|row| CombatFeatureSelection {
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
    Ok(CombatBuildV2Draft {
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

fn validated_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<ValidatedCombatBuildV2, String> {
    let draft = draft_for_owner(ctx, owner)?;
    CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?
        .validate_draft(&draft, draft.revision)
        .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))
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

fn aggregate_key(owner: Identity, parts: &[&str]) -> String {
    let mut key = owner.to_hex().to_string();
    for part in parts {
        key.push(':');
        key.push_str(part);
    }
    key
}

fn hex_encode(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut encoded = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        encoded.push(HEX[(byte >> 4) as usize] as char);
        encoded.push(HEX[(byte & 0x0f) as usize] as char);
    }
    encoded
}

fn sync_catalog_definitions(ctx: &ReducerContext) -> Result<(), String> {
    let catalog = CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?;
    let rules = catalog.rules();
    ctx.db
        .combat_build_v2_contract_definition()
        .insert(CombatBuildV2ContractDefinition {
            singleton_id: 0,
            schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
            minimum_selected_specializations: rules.minimum_selected_specializations as u32,
            maximum_selected_specializations: rules.maximum_selected_specializations as u32,
            global_feature_capacity: rules.global_feature_capacity as u32,
            trait_capacity: rules.trait_capacity as u32,
            direct_action_input_ids: rules.direct_action_input_ids.clone(),
        });
    for row in catalog.specialization_definitions() {
        ctx.db
            .combat_specialization_definition_v2()
            .insert(CombatSpecializationDefinitionV2 {
                specialization_id: row.specialization_id,
                combat_discipline_id: row.combat_discipline_id,
                specialization_kind: match row.specialization_kind {
                    CombatSpecializationKind::Form => "FORM",
                    CombatSpecializationKind::School => "SCHOOL",
                }
                .to_string(),
                display_name: row.display_name,
                sort_order: row.sort_order,
            });
    }
    for row in catalog.feature_definitions() {
        ctx.db
            .combat_feature_definition_v2()
            .insert(CombatFeatureDefinitionV2 {
                ability_id: row.ability_id,
                specialization_id: row.specialization_id,
                combat_discipline_id: row.combat_discipline_id,
                loadout_kind: row.loadout_kind.as_str().to_string(),
                display_name: row.display_name,
                resource_kind: row.resource_kind,
                resource_cost: row.resource_cost,
                sort_order: row.sort_order,
            });
    }
    for row in catalog.trait_definitions() {
        ctx.db
            .combat_trait_definition_v2()
            .insert(CombatTraitDefinitionV2 {
                ability_id: row.ability_id,
                loadout_kind: row.loadout_kind.as_str().to_string(),
                display_name: row.display_name,
                modifier_scalar: row.modifier_scalar,
                sort_order: row.sort_order,
            });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Clone, Debug, PartialEq, Eq)]
    struct InMemoryHubBuild {
        draft: CombatBuildV2Draft,
    }

    impl InMemoryHubBuild {
        fn new(catalog: &CombatBuildV2Catalog) -> Self {
            let default = catalog.default_draft();
            let validated = catalog.validate_draft(&default, 0).unwrap();
            Self {
                draft: snapshot_to_persisted_draft(validated.snapshot, Some("DAGGERS".to_string())),
            }
        }

        fn save(
            &mut self,
            catalog: &CombatBuildV2Catalog,
            candidate: CombatBuildV2Draft,
        ) -> Result<(), String> {
            let starting = candidate.starting_discipline_id.clone();
            let validated = catalog
                .validate_draft(&candidate, self.draft.revision)
                .map_err(|error| format!("{}: {}", error.code.as_str(), error.detail))?;
            self.draft = snapshot_to_persisted_draft(validated.snapshot, starting);
            Ok(())
        }

        fn reload(&self, catalog: &CombatBuildV2Catalog) -> ValidatedCombatBuildV2 {
            catalog
                .validate_draft(&self.draft, self.draft.revision)
                .expect("persisted draft")
        }
    }

    fn snapshot_to_persisted_draft(
        snapshot: CombatBuildV2Snapshot,
        starting_discipline_id: Option<String>,
    ) -> CombatBuildV2Draft {
        CombatBuildV2Draft {
            schema_version: snapshot.schema_version,
            revision: snapshot.revision.saturating_add(1),
            starting_discipline_id,
            selected_specializations: snapshot.selected_specializations,
            dormant_specializations: snapshot.dormant_specializations,
            discipline_configurations: snapshot.discipline_configurations,
            selected_features: snapshot.selected_features,
            selected_traits: snapshot.selected_traits,
        }
    }

    fn catalog() -> CombatBuildV2Catalog {
        CombatBuildV2Catalog::from_shared_catalogs().expect("v2 catalogs")
    }

    #[test]
    fn default_and_wire_conversion_round_trip_through_the_validator() {
        let catalog = catalog();
        let store = InMemoryHubBuild::new(&catalog);
        assert_eq!(store.draft.revision, 1);
        let input = contract_to_input(store.draft.clone());
        let decoded = input_to_contract(input);
        assert_eq!(decoded, store.draft);
        let validated = store.reload(&catalog);
        assert_eq!(validated.snapshot.starting_discipline_id, "DAGGERS");
        assert_eq!(validated.selected_feature_count(), 1);
    }

    #[test]
    fn revision_checked_rejection_leaves_the_accepted_aggregate_unchanged() {
        let catalog = catalog();
        let mut store = InMemoryHubBuild::new(&catalog);
        let accepted = store.draft.clone();

        let mut stale = accepted.clone();
        stale.revision = 0;
        assert!(store
            .save(&catalog, stale)
            .unwrap_err()
            .starts_with("COMBAT_BUILD_V2_STALE_REVISION"));
        assert_eq!(store.draft, accepted);

        let mut invalid = accepted.clone();
        invalid.selected_features[0].ability_id = "STAFF_STRIKE".to_string();
        assert!(store
            .save(&catalog, invalid)
            .unwrap_err()
            .starts_with("COMBAT_BUILD_V2_UNKNOWN_FEATURE"));
        assert_eq!(store.draft, accepted);
    }

    #[test]
    fn three_school_save_reload_has_one_staff_parent_and_no_technique_bar() {
        let catalog = catalog();
        let mut store = InMemoryHubBuild::new(&catalog);
        let draft = CombatBuildV2Draft {
            schema_version: 2,
            revision: 1,
            starting_discipline_id: Some("STAFF".to_string()),
            selected_specializations: selected(&["BLIGHT", "MORTALITY", "RUIN"]),
            dormant_specializations: Vec::new(),
            discipline_configurations: vec![staff_configuration()],
            selected_features: vec![
                feature("BLIGHT", "SPELL_ICICLE", 0),
                feature("MORTALITY", "SPELL_VAMPIRIC_ORB", 1),
                feature("RUIN", "SPELL_FIREBALL", 2),
            ],
            selected_traits: vec![MASTERY_TRAIT_ID.to_string()],
        };
        store.save(&catalog, draft).unwrap();
        let reloaded = store.reload(&catalog);
        assert_eq!(reloaded.projection.parent_discipline_ids, ["STAFF"]);
        assert!(reloaded.projection.technique_bars.is_empty());
        assert!(reloaded.projection.mastery_active);
    }

    #[test]
    fn dormant_restore_reflows_collisions_and_preserves_one_parent_configuration() {
        let catalog = catalog();
        let mut store = InMemoryHubBuild::new(&catalog);
        let dormant = CombatBuildV2Draft {
            schema_version: 2,
            revision: 1,
            starting_discipline_id: Some("DAGGERS".to_string()),
            selected_specializations: selected(&["DAGGERS_BLADEDANCER"]),
            dormant_specializations: vec!["DAGGERS_EXECUTIONER".to_string()],
            discipline_configurations: vec![dagger_configuration()],
            selected_features: vec![
                feature("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", 0),
                feature("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", 0),
            ],
            selected_traits: Vec::new(),
        };
        store.save(&catalog, dormant).unwrap();
        assert_eq!(store.draft.dormant_specializations, ["DAGGERS_EXECUTIONER"]);
        assert_eq!(store.reload(&catalog).selected_feature_count(), 1);

        let mut returning = store.draft.clone();
        returning.selected_specializations =
            selected(&["DAGGERS_BLADEDANCER", "DAGGERS_EXECUTIONER"]);
        returning.dormant_specializations.clear();
        store.save(&catalog, returning).unwrap();
        let reloaded = store.reload(&catalog);
        assert_eq!(store.draft.discipline_configurations.len(), 1);
        assert_eq!(
            reloaded.projection.technique_bars[0].ability_ids,
            ["DAGGER_QUICK_CUT", "DAGGER_GUT_RIPPER"]
        );
    }

    #[test]
    fn public_catalog_projection_has_all_v2_rows_and_no_staff_technique() {
        let catalog = catalog();
        assert_eq!(catalog.specialization_definitions().len(), 18);
        assert_eq!(catalog.feature_definitions().len(), 208);
        assert_eq!(catalog.trait_definitions().len(), 1);
        assert!(catalog.feature_definitions().iter().all(|row| {
            row.combat_discipline_id != "STAFF"
                || row.loadout_kind != CombatFeatureLoadoutKind::Technique
        }));
    }
}

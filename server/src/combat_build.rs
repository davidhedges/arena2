//! Pure combat-build contract and catalog validation.
//!
//! The persistent Hub save path and later match/runtime phases reuse this same
//! source so catalog legality and stable error codes cannot drift across
//! environment boundaries.

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

const CONTRACT_SCHEMA_VERSION: u32 = 1;
const STAFF_DISCIPLINE_ID: &str = "STAFF";
const ACTION_SLOT_GROUP: &str = "ACTION_BAR_ACTION";

#[cfg(not(feature = "pvp_match"))]
const PROGRESSION_CATALOG_JSON: &str = include_str!("progression_catalog.shared.json");
#[cfg(feature = "pvp_match")]
const PROGRESSION_CATALOG_JSON: &str = crate::progression::PROGRESSION_CATALOG_JSON;

#[cfg(not(feature = "pvp_match"))]
const WEAPON_APPEARANCE_CATALOG_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json"
));
#[cfg(feature = "pvp_match")]
const WEAPON_APPEARANCE_CATALOG_JSON: &str = crate::inventory::WEAPON_APPEARANCE_CATALOG_JSON;

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct CombatBuildDraft {
    pub revision: u64,
    pub starting_discipline_id: Option<String>,
    pub selected_disciplines: Vec<SelectedCombatDiscipline>,
    pub discipline_configurations: Vec<DisciplineConfiguration>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct SelectedCombatDiscipline {
    pub slot_index: u8,
    pub combat_discipline_id: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct DisciplineConfiguration {
    pub combat_discipline_id: String,
    pub weapon: DisciplineWeaponConfiguration,
    pub staff_school_ids: Vec<String>,
    pub active_assignments: Vec<DisciplineActionBarAssignment>,
    pub passive_ability_ids: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct DisciplineWeaponConfiguration {
    pub main_hand_item_def_id: String,
    pub main_hand_color_id: String,
    pub off_hand_item_def_id: String,
    pub off_hand_color_id: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct DisciplineActionBarAssignment {
    pub action_slot: String,
    pub ability_id: String,
}

/// Versioned, fully validated structure suitable for a later frozen handoff.
/// Dormant configurations remain present, but only selected configurations
/// contribute to the returned counts or runtime build.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct CombatBuildSnapshot {
    pub contract_schema_version: u32,
    pub revision: u64,
    pub starting_discipline_id: String,
    pub selected_disciplines: Vec<SelectedCombatDiscipline>,
    pub discipline_configurations: Vec<DisciplineConfiguration>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct ValidatedCombatBuild {
    pub snapshot: CombatBuildSnapshot,
    pub active_count: usize,
    pub passive_count: usize,
}

/// Canonical first-use build shared by the persistent Hub and the explicit
/// local-direct compatibility path. Both environments must pass this draft
/// through `CombatBuildCatalog`; the default is data, not a validation bypass.
pub(crate) fn default_combat_build_draft() -> CombatBuildDraft {
    CombatBuildDraft {
        revision: 0,
        starting_discipline_id: None,
        selected_disciplines: vec![SelectedCombatDiscipline {
            slot_index: 0,
            combat_discipline_id: "DAGGERS".to_string(),
        }],
        discipline_configurations: vec![DisciplineConfiguration {
            combat_discipline_id: "DAGGERS".to_string(),
            weapon: DisciplineWeaponConfiguration {
                main_hand_item_def_id: "TRAINING_DAGGER_PAIR".to_string(),
                main_hand_color_id: String::new(),
                off_hand_item_def_id: String::new(),
                off_hand_color_id: String::new(),
            },
            staff_school_ids: Vec::new(),
            active_assignments: vec![DisciplineActionBarAssignment {
                action_slot: "slot_0_0".to_string(),
                ability_id: "DAGGER_QUICK_CUT".to_string(),
            }],
            passive_ability_ids: Vec::new(),
        }],
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum CombatBuildErrorCode {
    UnsupportedSchemaVersion,
    StaleRevision,
    DisciplineCount,
    UnknownDiscipline,
    DuplicateDiscipline,
    DisciplineSlotOrder,
    StartingDisciplineNotSelected,
    DuplicateConfiguration,
    MissingDisciplineConfiguration,
    DisciplineAbilityMinimum,
    ActiveBudgetExceeded,
    CombinedBudgetExceeded,
    StaffSchoolCount,
    DuplicateStaffSchool,
    UnknownStaffSchool,
    NonStaffSchoolSelection,
    DuplicateAbility,
    DuplicateActionSlot,
    InvalidActionSlot,
    UnknownAbility,
    AbilityKind,
    AbilityDisciplineMismatch,
    StaffSchoolNotSelected,
    InvalidWeaponLoadout,
}

impl CombatBuildErrorCode {
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::UnsupportedSchemaVersion => "COMBAT_BUILD_UNSUPPORTED_SCHEMA_VERSION",
            Self::StaleRevision => "COMBAT_BUILD_STALE_REVISION",
            Self::DisciplineCount => "COMBAT_BUILD_DISCIPLINE_COUNT",
            Self::UnknownDiscipline => "COMBAT_BUILD_UNKNOWN_DISCIPLINE",
            Self::DuplicateDiscipline => "COMBAT_BUILD_DUPLICATE_DISCIPLINE",
            Self::DisciplineSlotOrder => "COMBAT_BUILD_DISCIPLINE_SLOT_ORDER",
            Self::StartingDisciplineNotSelected => "COMBAT_BUILD_STARTING_DISCIPLINE_NOT_SELECTED",
            Self::DuplicateConfiguration => "COMBAT_BUILD_DUPLICATE_CONFIGURATION",
            Self::MissingDisciplineConfiguration => "COMBAT_BUILD_MISSING_DISCIPLINE_CONFIGURATION",
            Self::DisciplineAbilityMinimum => "COMBAT_BUILD_DISCIPLINE_ABILITY_MINIMUM",
            Self::ActiveBudgetExceeded => "COMBAT_BUILD_ACTIVE_BUDGET_EXCEEDED",
            Self::CombinedBudgetExceeded => "COMBAT_BUILD_COMBINED_BUDGET_EXCEEDED",
            Self::StaffSchoolCount => "COMBAT_BUILD_STAFF_SCHOOL_COUNT",
            Self::DuplicateStaffSchool => "COMBAT_BUILD_DUPLICATE_STAFF_SCHOOL",
            Self::UnknownStaffSchool => "COMBAT_BUILD_UNKNOWN_STAFF_SCHOOL",
            Self::NonStaffSchoolSelection => "COMBAT_BUILD_NON_STAFF_SCHOOL_SELECTION",
            Self::DuplicateAbility => "COMBAT_BUILD_DUPLICATE_ABILITY",
            Self::DuplicateActionSlot => "COMBAT_BUILD_DUPLICATE_ACTION_SLOT",
            Self::InvalidActionSlot => "COMBAT_BUILD_INVALID_ACTION_SLOT",
            Self::UnknownAbility => "COMBAT_BUILD_UNKNOWN_ABILITY",
            Self::AbilityKind => "COMBAT_BUILD_ABILITY_KIND",
            Self::AbilityDisciplineMismatch => "COMBAT_BUILD_ABILITY_DISCIPLINE_MISMATCH",
            Self::StaffSchoolNotSelected => "COMBAT_BUILD_STAFF_SCHOOL_NOT_SELECTED",
            Self::InvalidWeaponLoadout => "COMBAT_BUILD_INVALID_WEAPON_LOADOUT",
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct CombatBuildValidationError {
    pub code: CombatBuildErrorCode,
    pub detail: String,
}

impl CombatBuildValidationError {
    fn new(code: CombatBuildErrorCode, detail: impl Into<String>) -> Self {
        Self {
            code,
            detail: detail.into(),
        }
    }
}

#[derive(Clone, Debug, Deserialize, PartialEq, Eq)]
pub(crate) struct CombatBuildRules {
    pub combat_discipline_ids: Vec<String>,
    pub staff_school_ids: Vec<String>,
    pub minimum_selected_disciplines: usize,
    pub maximum_selected_disciplines: usize,
    pub minimum_staff_schools_when_selected: usize,
    pub maximum_staff_schools_when_selected: usize,
    pub combined_ability_budget: usize,
    pub maximum_active_abilities: usize,
    pub minimum_counted_abilities_per_selected_discipline: usize,
    pub default_starting_discipline: String,
    pub action_slot_ids: Vec<String>,
}

#[derive(Clone, Debug)]
pub(crate) struct CombatBuildCatalog {
    rules: CombatBuildRules,
    discipline_ids: HashSet<String>,
    staff_school_ids: HashSet<String>,
    action_slot_ids: HashSet<String>,
    abilities: HashMap<String, CatalogAbility>,
    weapons: HashMap<String, CatalogWeapon>,
}

#[derive(Clone, Debug)]
struct CatalogAbility {
    selection_kind: AbilitySelectionKind,
    combat_discipline_id: Option<String>,
    spell_school_id: Option<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum AbilitySelectionKind {
    Active,
    Passive,
    Intrinsic,
}

impl AbilitySelectionKind {
    fn parse(value: &str) -> Result<Self, String> {
        match normalized(value).as_str() {
            "ACTIVE" => Ok(Self::Active),
            "PASSIVE" => Ok(Self::Passive),
            "INTRINSIC" => Ok(Self::Intrinsic),
            _ => Err(format!("unknown ability selection_kind '{value}'")),
        }
    }
}

#[derive(Clone, Debug)]
struct CatalogWeapon {
    combat_discipline_id: String,
    hand_requirement: String,
    equip_slot: String,
    weapon_kind: String,
    color_ids: HashSet<String>,
}

#[derive(Deserialize)]
struct ProgressionCatalogSource {
    combat_build_contract: CombatBuildContractSource,
    combat_modes: Vec<CombatModeSource>,
    abilities: Vec<AbilitySource>,
    slots: Vec<ActionSlotSource>,
}

#[derive(Deserialize)]
struct CombatBuildContractSource {
    schema_version: u32,
    combat_disciplines: Vec<CombatBuildDisciplineSource>,
    spell_schools: Vec<SpellSchoolSource>,
    rules: CombatBuildRules,
}

#[derive(Deserialize)]
struct CombatBuildDisciplineSource {
    combat_discipline_id: String,
    display_name: String,
    sort_order: u32,
}

#[derive(Deserialize)]
struct SpellSchoolSource {
    spell_school_id: String,
    display_name: String,
    sort_order: u32,
}

#[derive(Deserialize)]
struct CombatModeSource {
    combat_discipline_id: String,
    mode_id: String,
}

#[derive(Deserialize)]
struct ActionSlotSource {
    slot_id: String,
    slot_group: String,
}

#[derive(Deserialize)]
struct AbilitySource {
    ability_id: String,
    actor_scope: String,
    selection_kind: String,
    combat_discipline_id: Option<String>,
    spell_school_id: Option<String>,
    gameplay: AbilityGameplaySource,
    #[serde(default)]
    ability_tags: Vec<String>,
}

#[derive(Deserialize)]
struct AbilityGameplaySource {
    kind: String,
    damage_type: Option<String>,
}

#[derive(Deserialize)]
struct WeaponCatalogSource {
    schema_version: u32,
    families: Vec<WeaponSource>,
}

#[derive(Deserialize)]
struct WeaponSource {
    item_def_id: String,
    weapon_kind: String,
    hand_requirement: String,
    equip_slot: String,
    combat_discipline_id: String,
    variants: Vec<WeaponVariantSource>,
}

#[derive(Deserialize)]
struct WeaponVariantSource {
    color_id: String,
}

impl CombatBuildCatalog {
    pub(crate) fn from_shared_catalogs() -> Result<Self, String> {
        Self::from_json(PROGRESSION_CATALOG_JSON, WEAPON_APPEARANCE_CATALOG_JSON)
    }

    fn from_json(progression_json: &str, weapon_json: &str) -> Result<Self, String> {
        let source: ProgressionCatalogSource = serde_json::from_str(progression_json)
            .map_err(|error| format!("progression catalog schema error: {error}"))?;
        let weapon_source: WeaponCatalogSource = serde_json::from_str(weapon_json)
            .map_err(|error| format!("weapon catalog schema error: {error}"))?;
        validate_contract_catalog(&source, &weapon_source)?;

        let discipline_ids = source
            .combat_build_contract
            .combat_disciplines
            .iter()
            .map(|row| normalized(row.combat_discipline_id.as_str()))
            .collect();
        let staff_school_ids = source
            .combat_build_contract
            .spell_schools
            .iter()
            .map(|row| normalized(row.spell_school_id.as_str()))
            .collect();
        let action_slot_ids = source
            .combat_build_contract
            .rules
            .action_slot_ids
            .iter()
            .cloned()
            .collect();
        let abilities = source
            .abilities
            .into_iter()
            .map(|row| {
                let ability_id = normalized(row.ability_id.as_str());
                let selection_kind = AbilitySelectionKind::parse(row.selection_kind.as_str())
                    .expect("catalog validation checked selection kind");
                (
                    ability_id,
                    CatalogAbility {
                        selection_kind,
                        combat_discipline_id: normalized_option(row.combat_discipline_id),
                        spell_school_id: normalized_option(row.spell_school_id),
                    },
                )
            })
            .collect();
        let weapons = weapon_source
            .families
            .into_iter()
            .map(|row| {
                let item_def_id = normalized(row.item_def_id.as_str());
                (
                    item_def_id,
                    CatalogWeapon {
                        combat_discipline_id: normalized(row.combat_discipline_id.as_str()),
                        hand_requirement: normalized(row.hand_requirement.as_str()),
                        equip_slot: normalized(row.equip_slot.as_str()),
                        weapon_kind: normalized(row.weapon_kind.as_str()),
                        color_ids: row
                            .variants
                            .into_iter()
                            .map(|variant| normalized(variant.color_id.as_str()))
                            .collect(),
                    },
                )
            })
            .collect();

        Ok(Self {
            rules: source.combat_build_contract.rules,
            discipline_ids,
            staff_school_ids,
            action_slot_ids,
            abilities,
            weapons,
        })
    }

    pub(crate) fn rules(&self) -> &CombatBuildRules {
        &self.rules
    }

    pub(crate) fn validate_draft(
        &self,
        draft: &CombatBuildDraft,
        expected_revision: u64,
    ) -> Result<ValidatedCombatBuild, CombatBuildValidationError> {
        if draft.revision != expected_revision {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::StaleRevision,
                format!(
                    "draft revision {} does not match current revision {expected_revision}",
                    draft.revision
                ),
            ));
        }

        if !(self.rules.minimum_selected_disciplines..=self.rules.maximum_selected_disciplines)
            .contains(&draft.selected_disciplines.len())
        {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::DisciplineCount,
                "selected discipline count is outside the authored range",
            ));
        }

        let mut selected_ids = HashSet::new();
        for selected in &draft.selected_disciplines {
            let discipline_id = selected.combat_discipline_id.as_str();
            if !self.discipline_ids.contains(discipline_id) {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::UnknownDiscipline,
                    format!("unknown combat discipline '{discipline_id}'"),
                ));
            }
            if !selected_ids.insert(discipline_id.to_string()) {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::DuplicateDiscipline,
                    format!("combat discipline '{discipline_id}' is selected more than once"),
                ));
            }
        }
        for (expected_slot, selected) in draft.selected_disciplines.iter().enumerate() {
            if usize::from(selected.slot_index) != expected_slot {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::DisciplineSlotOrder,
                    "selected discipline slots must be contiguous and ordered from zero",
                ));
            }
        }

        if let Some(starting_discipline_id) = draft.starting_discipline_id.as_deref() {
            if !selected_ids.contains(starting_discipline_id) {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::StartingDisciplineNotSelected,
                    format!("starting discipline '{starting_discipline_id}' is not selected"),
                ));
            }
        }

        let mut configurations = HashMap::new();
        for configuration in &draft.discipline_configurations {
            let discipline_id = configuration.combat_discipline_id.as_str();
            if !self.discipline_ids.contains(discipline_id) {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::UnknownDiscipline,
                    format!("configuration references unknown discipline '{discipline_id}'"),
                ));
            }
            if configurations
                .insert(discipline_id.to_string(), configuration)
                .is_some()
            {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::DuplicateConfiguration,
                    format!("discipline '{discipline_id}' has multiple configurations"),
                ));
            }
        }

        for selected in &draft.selected_disciplines {
            let discipline_id = selected.combat_discipline_id.as_str();
            let Some(configuration) = configurations.get(discipline_id) else {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::MissingDisciplineConfiguration,
                    format!("selected discipline '{discipline_id}' has no configuration"),
                ));
            };
            if configuration.active_assignments.len() + configuration.passive_ability_ids.len()
                < self.rules.minimum_counted_abilities_per_selected_discipline
            {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::DisciplineAbilityMinimum,
                    format!("selected discipline '{discipline_id}' has no counted ability"),
                ));
            }
        }

        let (active_count, passive_count) = draft.selected_disciplines.iter().fold(
            (0usize, 0usize),
            |(active_count, passive_count), selected| {
                let configuration = configurations
                    .get(selected.combat_discipline_id.as_str())
                    .expect("selected configurations checked above");
                (
                    active_count + configuration.active_assignments.len(),
                    passive_count + configuration.passive_ability_ids.len(),
                )
            },
        );
        if active_count > self.rules.maximum_active_abilities {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::ActiveBudgetExceeded,
                format!(
                    "active count {active_count} exceeds {}",
                    self.rules.maximum_active_abilities
                ),
            ));
        }
        if active_count + passive_count > self.rules.combined_ability_budget {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::CombinedBudgetExceeded,
                format!(
                    "combined count {} exceeds {}",
                    active_count + passive_count,
                    self.rules.combined_ability_budget
                ),
            ));
        }

        for configuration in &draft.discipline_configurations {
            let discipline_id = configuration.combat_discipline_id.as_str();
            self.validate_school_selection(
                discipline_id,
                configuration,
                selected_ids.contains(discipline_id),
            )?;
            self.validate_weapon_configuration(discipline_id, &configuration.weapon)?;
        }

        let mut selected_ids = HashSet::new();
        for configuration in &draft.discipline_configurations {
            for ability_id in configuration
                .active_assignments
                .iter()
                .map(|assignment| assignment.ability_id.as_str())
                .chain(configuration.passive_ability_ids.iter().map(String::as_str))
            {
                if !selected_ids.insert(ability_id.to_string()) {
                    return Err(CombatBuildValidationError::new(
                        CombatBuildErrorCode::DuplicateAbility,
                        format!("ability '{ability_id}' is selected more than once"),
                    ));
                }
            }
        }

        for configuration in &draft.discipline_configurations {
            let discipline_id = configuration.combat_discipline_id.as_str();
            let selected_schools: HashSet<_> =
                configuration.staff_school_ids.iter().cloned().collect();
            let mut action_slots = HashSet::new();
            for assignment in &configuration.active_assignments {
                let action_slot = assignment.action_slot.as_str();
                if !action_slots.insert(action_slot.to_string()) {
                    return Err(CombatBuildValidationError::new(
                        CombatBuildErrorCode::DuplicateActionSlot,
                        format!(
                            "discipline '{discipline_id}' assigns action slot '{action_slot}' more than once"
                        ),
                    ));
                }
                if !self.action_slot_ids.contains(action_slot) {
                    return Err(CombatBuildValidationError::new(
                        CombatBuildErrorCode::InvalidActionSlot,
                        format!("unknown action slot '{action_slot}'"),
                    ));
                }
                self.validate_ability(
                    assignment.ability_id.as_str(),
                    AbilitySelectionKind::Active,
                    discipline_id,
                    &selected_schools,
                )?;
            }
            for ability_id in &configuration.passive_ability_ids {
                self.validate_ability(
                    ability_id.as_str(),
                    AbilitySelectionKind::Passive,
                    discipline_id,
                    &selected_schools,
                )?;
            }
        }

        let starting_discipline_id = draft
            .starting_discipline_id
            .clone()
            .unwrap_or_else(|| draft.selected_disciplines[0].combat_discipline_id.clone());
        Ok(ValidatedCombatBuild {
            snapshot: CombatBuildSnapshot {
                contract_schema_version: CONTRACT_SCHEMA_VERSION,
                revision: draft.revision,
                starting_discipline_id,
                selected_disciplines: draft.selected_disciplines.clone(),
                discipline_configurations: draft.discipline_configurations.clone(),
            },
            active_count,
            passive_count,
        })
    }

    /// Revalidates a frozen cross-environment snapshot without inventing a
    /// second bootstrap policy. A snapshot always carries its effective start
    /// discipline, so converting it back to a draft is lossless.
    pub(crate) fn validate_snapshot(
        &self,
        snapshot: &CombatBuildSnapshot,
    ) -> Result<ValidatedCombatBuild, CombatBuildValidationError> {
        if snapshot.contract_schema_version != CONTRACT_SCHEMA_VERSION {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::UnsupportedSchemaVersion,
                format!(
                    "snapshot schema version {} is unsupported; expected {CONTRACT_SCHEMA_VERSION}",
                    snapshot.contract_schema_version
                ),
            ));
        }

        let draft = CombatBuildDraft {
            revision: snapshot.revision,
            starting_discipline_id: Some(snapshot.starting_discipline_id.clone()),
            selected_disciplines: snapshot.selected_disciplines.clone(),
            discipline_configurations: snapshot.discipline_configurations.clone(),
        };
        self.validate_draft(&draft, snapshot.revision)
    }

    fn validate_school_selection(
        &self,
        discipline_id: &str,
        configuration: &DisciplineConfiguration,
        is_selected: bool,
    ) -> Result<(), CombatBuildValidationError> {
        if discipline_id != STAFF_DISCIPLINE_ID {
            if !configuration.staff_school_ids.is_empty() {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::NonStaffSchoolSelection,
                    format!("non-Staff discipline '{discipline_id}' selects Staff schools"),
                ));
            }
            return Ok(());
        }

        if is_selected
            && !(self.rules.minimum_staff_schools_when_selected
                ..=self.rules.maximum_staff_schools_when_selected)
                .contains(&configuration.staff_school_ids.len())
        {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::StaffSchoolCount,
                "selected Staff school count is outside the authored range",
            ));
        }
        if !is_selected
            && configuration.staff_school_ids.len() > self.rules.maximum_staff_schools_when_selected
        {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::StaffSchoolCount,
                "dormant Staff configuration exceeds the authored school maximum",
            ));
        }

        let mut school_ids = HashSet::new();
        for school_id in &configuration.staff_school_ids {
            let school_id = school_id.as_str();
            if !school_ids.insert(school_id.to_string()) {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::DuplicateStaffSchool,
                    format!("Staff school '{school_id}' is selected more than once"),
                ));
            }
            if !self.staff_school_ids.contains(school_id) {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::UnknownStaffSchool,
                    format!("unknown Staff school '{school_id}'"),
                ));
            }
        }
        Ok(())
    }

    fn validate_ability(
        &self,
        ability_id: &str,
        expected_kind: AbilitySelectionKind,
        discipline_id: &str,
        selected_schools: &HashSet<String>,
    ) -> Result<(), CombatBuildValidationError> {
        let Some(ability) = self.abilities.get(ability_id) else {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::UnknownAbility,
                format!("unknown ability '{ability_id}'"),
            ));
        };
        if ability.selection_kind != expected_kind {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::AbilityKind,
                format!("ability '{ability_id}' has the wrong selection kind"),
            ));
        }
        if ability.combat_discipline_id.as_deref() != Some(discipline_id) {
            return Err(CombatBuildValidationError::new(
                CombatBuildErrorCode::AbilityDisciplineMismatch,
                format!("ability '{ability_id}' is not owned by discipline '{discipline_id}'"),
            ));
        }
        if discipline_id == STAFF_DISCIPLINE_ID {
            let school_id = ability
                .spell_school_id
                .as_deref()
                .expect("catalog validation requires Staff ability schools");
            if !selected_schools.contains(school_id) {
                return Err(CombatBuildValidationError::new(
                    CombatBuildErrorCode::StaffSchoolNotSelected,
                    format!("ability '{ability_id}' requires unselected school '{school_id}'"),
                ));
            }
        }
        Ok(())
    }

    fn validate_weapon_configuration(
        &self,
        discipline_id: &str,
        weapon: &DisciplineWeaponConfiguration,
    ) -> Result<(), CombatBuildValidationError> {
        let main_id = weapon.main_hand_item_def_id.as_str();
        let off_id = weapon.off_hand_item_def_id.as_str();
        let Some(main) = self.weapons.get(main_id) else {
            return Err(invalid_weapon(format!(
                "unknown main-hand weapon '{main_id}'"
            )));
        };
        if main.combat_discipline_id != discipline_id || main.equip_slot != "MAIN_HAND" {
            return Err(invalid_weapon(format!(
                "main-hand weapon '{main_id}' is illegal for '{discipline_id}'"
            )));
        }
        if !valid_color(main, weapon.main_hand_color_id.as_str()) {
            return Err(invalid_weapon(format!(
                "main-hand color is illegal for '{main_id}'"
            )));
        }

        match main.hand_requirement.as_str() {
            "TWO_HAND" => {
                if !off_id.is_empty() || !weapon.off_hand_color_id.trim().is_empty() {
                    return Err(invalid_weapon(format!(
                        "two-handed weapon '{main_id}' cannot have an off hand"
                    )));
                }
            }
            "ONE_HAND" => {
                let Some(off) = self.weapons.get(off_id) else {
                    return Err(invalid_weapon(format!(
                        "one-handed weapon '{main_id}' requires a legal off hand"
                    )));
                };
                if off.combat_discipline_id != discipline_id
                    || off.equip_slot != "OFF_HAND"
                    || off.hand_requirement != "OFF_HAND"
                    || off.weapon_kind != "SHIELD"
                    || !valid_color(off, weapon.off_hand_color_id.as_str())
                {
                    return Err(invalid_weapon(format!(
                        "off-hand weapon '{off_id}' is illegal for '{discipline_id}'"
                    )));
                }
            }
            _ => {
                return Err(invalid_weapon(format!(
                    "main-hand weapon '{main_id}' has an unsupported hand requirement"
                )));
            }
        }
        Ok(())
    }
}

fn invalid_weapon(detail: impl Into<String>) -> CombatBuildValidationError {
    CombatBuildValidationError::new(CombatBuildErrorCode::InvalidWeaponLoadout, detail)
}

fn valid_color(weapon: &CatalogWeapon, color_id: &str) -> bool {
    color_id.is_empty() || weapon.color_ids.contains(color_id)
}

fn normalized(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

fn normalized_option(value: Option<String>) -> Option<String> {
    value
        .map(|value| normalized(value.as_str()))
        .filter(|value| !value.is_empty())
}

fn validate_contract_catalog(
    source: &ProgressionCatalogSource,
    weapon_source: &WeaponCatalogSource,
) -> Result<(), String> {
    let contract = &source.combat_build_contract;
    if contract.schema_version != CONTRACT_SCHEMA_VERSION {
        return Err(format!(
            "unsupported combat-build schema version {}",
            contract.schema_version
        ));
    }
    if weapon_source.schema_version != 1 {
        return Err(format!(
            "unsupported weapon catalog schema version {}",
            weapon_source.schema_version
        ));
    }

    let discipline_ids = unique_nonempty_rows(
        "combat discipline",
        contract
            .combat_disciplines
            .iter()
            .map(|row| row.combat_discipline_id.as_str()),
    )?;
    for row in &contract.combat_disciplines {
        if row.combat_discipline_id != normalized(row.combat_discipline_id.as_str()) {
            return Err(format!(
                "combat discipline id '{}' is not the exact canonical wire id",
                row.combat_discipline_id
            ));
        }
    }
    let expected_disciplines: HashSet<_> = [
        "DAGGERS",
        "TWO_HANDED_SWORD",
        "SWORD_AND_SHIELD",
        "ARCHER_BOW",
        STAFF_DISCIPLINE_ID,
    ]
    .into_iter()
    .map(str::to_string)
    .collect();
    if discipline_ids != expected_disciplines {
        return Err(
            "combat-build catalog must contain exactly the five canonical disciplines".to_string(),
        );
    }
    validate_display_rows(
        "combat discipline",
        contract.combat_disciplines.iter().map(|row| {
            (
                row.combat_discipline_id.as_str(),
                row.display_name.as_str(),
                row.sort_order,
            )
        }),
    )?;

    let school_ids = unique_nonempty_rows(
        "spell school",
        contract
            .spell_schools
            .iter()
            .map(|row| row.spell_school_id.as_str()),
    )?;
    for row in &contract.spell_schools {
        if row.spell_school_id != normalized(row.spell_school_id.as_str()) {
            return Err(format!(
                "spell school id '{}' is not the exact canonical wire id",
                row.spell_school_id
            ));
        }
    }
    let expected_schools: HashSet<_> = [
        "BLIGHT",
        "MORTALITY",
        "RUIN",
        "DIVINITY",
        "ARCANA",
        "PRIMAL",
    ]
    .into_iter()
    .map(str::to_string)
    .collect();
    if school_ids != expected_schools {
        return Err(
            "spell-school catalog must contain exactly the six consolidated schools".to_string(),
        );
    }
    validate_display_rows(
        "spell school",
        contract.spell_schools.iter().map(|row| {
            (
                row.spell_school_id.as_str(),
                row.display_name.as_str(),
                row.sort_order,
            )
        }),
    )?;

    let projected_disciplines: HashSet<_> = contract
        .rules
        .combat_discipline_ids
        .iter()
        .cloned()
        .collect();
    let projected_schools: HashSet<_> = contract.rules.staff_school_ids.iter().cloned().collect();
    if projected_disciplines != discipline_ids
        || contract.rules.combat_discipline_ids.len() != discipline_ids.len()
    {
        return Err("rules discipline projection must match the canonical catalog".to_string());
    }
    if projected_schools != school_ids || contract.rules.staff_school_ids.len() != school_ids.len()
    {
        return Err("rules school projection must match the spell-school catalog".to_string());
    }
    validate_rule_ranges(&contract.rules)?;

    let mut mode_keys = HashSet::new();
    for mode in &source.combat_modes {
        let profile_id = normalized(mode.combat_discipline_id.as_str());
        let mode_id = normalized(mode.mode_id.as_str());
        if mode.combat_discipline_id != profile_id || mode.mode_id != mode_id {
            return Err("combat-mode IDs must use exact authored wire IDs".to_string());
        }
        if !discipline_ids.contains(profile_id.as_str()) {
            return Err(format!(
                "combat mode '{mode_id}' does not map to a canonical discipline"
            ));
        }
        if mode_id.is_empty() || !mode_keys.insert((profile_id, mode_id)) {
            return Err("combat modes must have unique non-empty discipline/mode keys".to_string());
        }
    }

    let projected_slots = unique_exact_nonempty_rows(
        "projected action slot",
        contract.rules.action_slot_ids.iter().map(String::as_str),
    )?;
    let authored_slots = unique_exact_nonempty_rows(
        "authored action slot",
        source
            .slots
            .iter()
            .filter(|row| normalized(row.slot_group.as_str()) == ACTION_SLOT_GROUP)
            .map(|row| row.slot_id.as_str()),
    )?;
    if projected_slots != authored_slots {
        return Err(
            "rules action-slot projection must exactly match player action-bar slots".to_string(),
        );
    }

    validate_abilities(source, &discipline_ids, &school_ids)?;
    validate_weapons(weapon_source, &discipline_ids)?;
    Ok(())
}

fn validate_rule_ranges(rules: &CombatBuildRules) -> Result<(), String> {
    if rules.minimum_selected_disciplines == 0
        || rules.minimum_selected_disciplines > rules.maximum_selected_disciplines
        || rules.maximum_selected_disciplines > rules.combat_discipline_ids.len()
    {
        return Err("invalid selected-discipline rule range".to_string());
    }
    if rules.minimum_staff_schools_when_selected == 0
        || rules.minimum_staff_schools_when_selected > rules.maximum_staff_schools_when_selected
        || rules.maximum_staff_schools_when_selected > rules.staff_school_ids.len()
    {
        return Err("invalid Staff-school rule range".to_string());
    }
    if rules.maximum_active_abilities > rules.combined_ability_budget
        || rules.minimum_counted_abilities_per_selected_discipline == 0
    {
        return Err("invalid combat-build ability budget rules".to_string());
    }
    if rules.default_starting_discipline != "selected_disciplines[0]" {
        return Err("unsupported default starting-discipline rule".to_string());
    }
    Ok(())
}

fn validate_abilities(
    source: &ProgressionCatalogSource,
    discipline_ids: &HashSet<String>,
    school_ids: &HashSet<String>,
) -> Result<(), String> {
    let damage_types: HashSet<_> = source
        .abilities
        .iter()
        .filter_map(|ability| ability.gameplay.damage_type.as_deref())
        .map(normalized)
        .collect();
    if !damage_types.is_disjoint(school_ids) {
        return Err("damage types and spell-school IDs must remain separate domains".to_string());
    }

    let mut ability_ids = HashSet::new();
    for ability in &source.abilities {
        let ability_id = normalized(ability.ability_id.as_str());
        if ability.ability_id != ability_id
            || ability_id.is_empty()
            || !ability_ids.insert(ability_id.clone())
        {
            return Err(format!("duplicate or empty ability id '{ability_id}'"));
        }
        let selection_kind = AbilitySelectionKind::parse(ability.selection_kind.as_str())?;
        if ability.selection_kind != normalized(ability.selection_kind.as_str()) {
            return Err(format!(
                "ability '{ability_id}' selection_kind must use its exact wire value"
            ));
        }
        let actor_scope = normalized(ability.actor_scope.as_str());
        if ability.actor_scope != actor_scope {
            return Err(format!(
                "ability '{ability_id}' actor_scope must use its exact wire value"
            ));
        }
        let canonical_id = normalized_option(ability.combat_discipline_id.clone());
        let school_id = normalized_option(ability.spell_school_id.clone());
        if ability
            .combat_discipline_id
            .as_deref()
            .is_some_and(|value| value != normalized(value))
            || ability
                .spell_school_id
                .as_deref()
                .is_some_and(|value| value != normalized(value))
        {
            return Err(format!(
                "ability '{ability_id}' ownership IDs must use exact canonical wire values"
            ));
        }

        if actor_scope == "NPC" {
            if selection_kind != AbilitySelectionKind::Intrinsic
                || canonical_id.is_some()
                || school_id.is_some()
            {
                return Err(format!(
                    "NPC ability '{ability_id}' must stay intrinsic and outside the player build"
                ));
            }
            continue;
        }
        if actor_scope != "PLAYER" {
            return Err(format!("ability '{ability_id}' has unknown actor scope"));
        }
        let expected_kind = if ability
            .ability_tags
            .iter()
            .any(|tag| normalized(tag.as_str()) == "ACTION_BAR_ACTION")
        {
            AbilitySelectionKind::Active
        } else if ability
            .ability_tags
            .iter()
            .any(|tag| normalized(tag.as_str()) == "PASSIVE")
        {
            AbilitySelectionKind::Passive
        } else {
            AbilitySelectionKind::Intrinsic
        };
        if selection_kind != expected_kind {
            return Err(format!(
                "player ability '{ability_id}' selection kind does not match its authored tags"
            ));
        }
        if selection_kind == AbilitySelectionKind::Passive
            && normalized(ability.gameplay.kind.as_str()) != "PASSIVE"
        {
            return Err(format!(
                "player passive '{ability_id}' must use PASSIVE gameplay kind"
            ));
        }

        let canonical_id = canonical_id
            .ok_or_else(|| format!("player ability '{ability_id}' has no canonical discipline"))?;
        if !discipline_ids.contains(canonical_id.as_str()) {
            return Err(format!(
                "player ability '{ability_id}' references unknown canonical discipline"
            ));
        }
        if canonical_id == STAFF_DISCIPLINE_ID {
            let school_id = school_id.ok_or_else(|| {
                format!("Staff ability '{ability_id}' must have one spell_school_id")
            })?;
            if !school_ids.contains(school_id.as_str()) {
                return Err(format!(
                    "Staff ability '{ability_id}' has invalid spell-school ownership"
                ));
            }
        } else if school_id.is_some() {
            return Err(format!(
                "non-Staff ability '{ability_id}' must not have a spell_school_id"
            ));
        }
    }
    Ok(())
}

fn validate_weapons(
    weapon_source: &WeaponCatalogSource,
    discipline_ids: &HashSet<String>,
) -> Result<(), String> {
    let mut item_ids = HashSet::new();
    for weapon in &weapon_source.families {
        let item_id = normalized(weapon.item_def_id.as_str());
        if weapon.item_def_id != item_id || item_id.is_empty() || !item_ids.insert(item_id.clone())
        {
            return Err(format!("duplicate or empty weapon item id '{item_id}'"));
        }
        let canonical_id = normalized(weapon.combat_discipline_id.as_str());
        if weapon.combat_discipline_id != canonical_id
            || weapon.weapon_kind != normalized(weapon.weapon_kind.as_str())
            || weapon.hand_requirement != normalized(weapon.hand_requirement.as_str())
            || weapon.equip_slot != normalized(weapon.equip_slot.as_str())
        {
            return Err(format!(
                "weapon '{item_id}' metadata must use exact authored wire values"
            ));
        }
        if !discipline_ids.contains(canonical_id.as_str()) {
            return Err(format!(
                "weapon '{item_id}' references unknown canonical discipline '{canonical_id}'"
            ));
        }
        validate_weapon_shape(weapon, canonical_id.as_str())?;
        let mut color_ids = HashSet::new();
        for variant in &weapon.variants {
            let color_id = normalized(variant.color_id.as_str());
            if variant.color_id != color_id
                || color_id.is_empty()
                || !color_ids.insert(color_id.clone())
            {
                return Err(format!(
                    "weapon '{item_id}' color IDs must be exact, unique, and non-empty"
                ));
            }
        }
    }
    Ok(())
}

fn validate_weapon_shape(weapon: &WeaponSource, canonical_id: &str) -> Result<(), String> {
    let kind = normalized(weapon.weapon_kind.as_str());
    let hand = normalized(weapon.hand_requirement.as_str());
    let slot = normalized(weapon.equip_slot.as_str());
    let valid = match canonical_id {
        "DAGGERS" => kind == "DAGGER_PAIR" && hand == "TWO_HAND" && slot == "MAIN_HAND",
        "TWO_HANDED_SWORD" => {
            matches!(
                kind.as_str(),
                "TWO_HAND_SWORD" | "TWO_HAND_AXE" | "TWO_HAND_HAMMER" | "POLEARM"
            ) && hand == "TWO_HAND"
                && slot == "MAIN_HAND"
        }
        "SWORD_AND_SHIELD" => match slot.as_str() {
            "MAIN_HAND" => {
                matches!(
                    kind.as_str(),
                    "ONE_HAND_SWORD" | "ONE_HAND_AXE" | "ONE_HAND_HAMMER" | "ONE_HAND_FIST"
                ) && hand == "ONE_HAND"
            }
            "OFF_HAND" => kind == "SHIELD" && hand == "OFF_HAND",
            _ => false,
        },
        "ARCHER_BOW" => kind == "BOW" && hand == "TWO_HAND" && slot == "MAIN_HAND",
        STAFF_DISCIPLINE_ID => kind == "STAFF" && hand == "TWO_HAND" && slot == "MAIN_HAND",
        _ => false,
    };
    if valid {
        Ok(())
    } else {
        Err(format!(
            "weapon '{}' has an illegal shape for '{canonical_id}'",
            weapon.item_def_id
        ))
    }
}

fn unique_nonempty_rows<'a>(
    label: &str,
    rows: impl IntoIterator<Item = &'a str>,
) -> Result<HashSet<String>, String> {
    let mut ids = HashSet::new();
    for row in rows {
        let id = normalized(row);
        if id.is_empty() || !ids.insert(id.clone()) {
            return Err(format!("{label} ids must be unique and non-empty: '{id}'"));
        }
    }
    Ok(ids)
}

fn unique_exact_nonempty_rows<'a>(
    label: &str,
    rows: impl IntoIterator<Item = &'a str>,
) -> Result<HashSet<String>, String> {
    let mut ids = HashSet::new();
    for row in rows {
        if row.trim().is_empty() || row != row.trim() || !ids.insert(row.to_string()) {
            return Err(format!(
                "{label} ids must be exact, unique, and non-empty: '{row}'"
            ));
        }
    }
    Ok(ids)
}

fn validate_display_rows<'a>(
    label: &str,
    rows: impl IntoIterator<Item = (&'a str, &'a str, u32)>,
) -> Result<(), String> {
    let mut sort_orders = HashSet::new();
    for (id, display_name, sort_order) in rows {
        if display_name.trim().is_empty() || !sort_orders.insert(sort_order) {
            return Err(format!(
                "{label} '{}' must have a display name and unique sort order",
                normalized(id)
            ));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const FIXTURES_JSON: &str =
        include_str!("../../docs/fixtures/combat-build-contract-v1/cases.json");
    type CatalogMutation = Box<dyn Fn(&mut serde_json::Value, &mut serde_json::Value)>;

    #[derive(Deserialize)]
    struct FixtureFile {
        fixture_version: u32,
        rules: FixtureRules,
        cases: Vec<FixtureCase>,
    }

    #[derive(Deserialize)]
    struct FixtureRules {
        combat_discipline_ids: Vec<String>,
        staff_school_ids: Vec<String>,
        minimum_selected_disciplines: usize,
        maximum_selected_disciplines: usize,
        minimum_staff_schools_when_selected: usize,
        maximum_staff_schools_when_selected: usize,
        combined_ability_budget: usize,
        maximum_active_abilities: usize,
        minimum_counted_abilities_per_selected_discipline: usize,
        default_starting_discipline: String,
    }

    #[derive(Deserialize)]
    struct FixtureCase {
        id: String,
        build: CombatBuildDraft,
        expected: FixtureExpected,
    }

    #[derive(Deserialize)]
    struct FixtureExpected {
        valid: bool,
        error_code: Option<String>,
        active_count: Option<usize>,
        passive_count: Option<usize>,
        combined_count: Option<usize>,
        effective_starting_discipline_id: Option<String>,
    }

    fn catalog() -> CombatBuildCatalog {
        CombatBuildCatalog::from_shared_catalogs().expect("canonical combat-build catalog")
    }

    #[test]
    fn canonical_default_is_validator_owned_and_runtime_ready() {
        let draft = default_combat_build_draft();
        let validated = catalog()
            .validate_draft(&draft, 0)
            .expect("canonical default must pass the production validator");

        assert_eq!(validated.active_count, 1);
        assert_eq!(validated.passive_count, 0);
        assert_eq!(validated.snapshot.starting_discipline_id, "DAGGERS");
        assert_eq!(validated.snapshot.selected_disciplines.len(), 1);
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

    fn fixtures() -> FixtureFile {
        serde_json::from_str(FIXTURES_JSON).expect("combat-build contract fixtures")
    }

    #[test]
    fn shared_catalogs_form_one_exhaustive_combat_build_projection() {
        let catalog = catalog();
        let source: ProgressionCatalogSource =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("progression catalog source");
        assert_eq!(catalog.discipline_ids.len(), 5);
        assert_eq!(catalog.staff_school_ids.len(), 6);
        assert_eq!(catalog.weapons.len(), 138);
        assert_eq!(catalog.abilities.len(), 414);
        assert_eq!(catalog.rules.action_slot_ids.len(), 27);
        assert_eq!(
            source
                .abilities
                .iter()
                .filter(|ability| ability.actor_scope == "PLAYER")
                .count(),
            217
        );
        assert_eq!(
            source
                .abilities
                .iter()
                .filter(|ability| ability.actor_scope == "NPC")
                .count(),
            197
        );
        assert_eq!(source.combat_build_contract.combat_disciplines.len(), 5);
        assert!(source.abilities.iter().all(|ability| {
            if ability.actor_scope == "PLAYER" {
                ability.combat_discipline_id.is_some()
            } else {
                ability.selection_kind == "INTRINSIC"
                    && ability.combat_discipline_id.is_none()
                    && ability.spell_school_id.is_none()
            }
        }));
    }

    #[test]
    fn authored_rules_match_the_frozen_owner_approved_fixture_rules() {
        let catalog = catalog();
        let fixture = fixtures();
        let rules = catalog.rules();
        assert_eq!(fixture.fixture_version, CONTRACT_SCHEMA_VERSION);
        assert_eq!(
            fixture.rules.combat_discipline_ids,
            rules.combat_discipline_ids
        );
        assert_eq!(fixture.rules.staff_school_ids, rules.staff_school_ids);
        assert_eq!(
            fixture.rules.minimum_selected_disciplines,
            rules.minimum_selected_disciplines
        );
        assert_eq!(
            fixture.rules.maximum_selected_disciplines,
            rules.maximum_selected_disciplines
        );
        assert_eq!(
            fixture.rules.minimum_staff_schools_when_selected,
            rules.minimum_staff_schools_when_selected
        );
        assert_eq!(
            fixture.rules.maximum_staff_schools_when_selected,
            rules.maximum_staff_schools_when_selected
        );
        assert_eq!(
            fixture.rules.combined_ability_budget,
            rules.combined_ability_budget
        );
        assert_eq!(
            fixture.rules.maximum_active_abilities,
            rules.maximum_active_abilities
        );
        assert_eq!(
            fixture
                .rules
                .minimum_counted_abilities_per_selected_discipline,
            rules.minimum_counted_abilities_per_selected_discipline
        );
        assert_eq!(
            fixture.rules.default_starting_discipline,
            rules.default_starting_discipline
        );
    }

    #[test]
    fn every_frozen_contract_fixture_uses_the_production_validator() {
        let catalog = catalog();
        let fixture = fixtures();
        assert_eq!(fixture.cases.len(), 29);
        for case in fixture.cases {
            let result = catalog.validate_draft(&case.build, case.build.revision);
            match (case.expected.valid, result) {
                (true, Ok(validated)) => {
                    if let Some(active_count) = case.expected.active_count {
                        assert_eq!(validated.active_count, active_count, "{}", case.id);
                    }
                    if let Some(passive_count) = case.expected.passive_count {
                        assert_eq!(validated.passive_count, passive_count, "{}", case.id);
                    }
                    if let Some(combined_count) = case.expected.combined_count {
                        assert_eq!(
                            validated.active_count + validated.passive_count,
                            combined_count,
                            "{}",
                            case.id
                        );
                    }
                    if let Some(starting_id) = case.expected.effective_starting_discipline_id {
                        assert_eq!(
                            validated.snapshot.starting_discipline_id, starting_id,
                            "{}",
                            case.id
                        );
                    }
                }
                (false, Err(error)) => assert_eq!(
                    Some(error.code.as_str()),
                    case.expected.error_code.as_deref(),
                    "{}: {}",
                    case.id,
                    error.detail
                ),
                (true, Err(error)) => panic!(
                    "fixture '{}' should be valid but returned {}: {}",
                    case.id,
                    error.code.as_str(),
                    error.detail
                ),
                (false, Ok(_)) => panic!("fixture '{}' should be invalid", case.id),
            }
        }
    }

    #[test]
    fn stale_revision_is_rejected_before_build_validation() {
        let catalog = catalog();
        let draft = fixtures().cases.remove(0).build;
        let error = catalog
            .validate_draft(&draft, draft.revision + 1)
            .expect_err("stale revision must fail");
        assert_eq!(error.code, CombatBuildErrorCode::StaleRevision);
    }

    #[test]
    fn dormant_configuration_is_validated_but_excluded_from_counts() {
        let catalog = catalog();
        let draft = fixtures()
            .cases
            .into_iter()
            .find(|case| case.id == "valid_dormant_configuration_does_not_count")
            .expect("dormant fixture")
            .build;
        let validated = catalog
            .validate_draft(&draft, draft.revision)
            .expect("dormant configuration remains valid");
        assert_eq!(validated.active_count, 1);
        assert_eq!(validated.passive_count, 0);
        assert_eq!(validated.snapshot.discipline_configurations.len(), 2);
    }

    #[test]
    fn structural_and_dormant_mutations_return_stable_codes() {
        fn assert_code(
            catalog: &CombatBuildCatalog,
            draft: &CombatBuildDraft,
            expected: CombatBuildErrorCode,
        ) {
            let error = catalog
                .validate_draft(draft, draft.revision)
                .expect_err("mutated draft must fail");
            assert_eq!(error.code, expected, "{}", error.detail);
        }

        let catalog = catalog();
        let fixture = fixtures();
        let base = fixture
            .cases
            .iter()
            .find(|case| case.id == "valid_one_discipline_one_active")
            .expect("single discipline fixture")
            .build
            .clone();

        let mut missing_configuration = base.clone();
        missing_configuration.discipline_configurations.clear();
        assert_code(
            &catalog,
            &missing_configuration,
            CombatBuildErrorCode::MissingDisciplineConfiguration,
        );

        let mut duplicate_configuration = base.clone();
        duplicate_configuration
            .discipline_configurations
            .push(duplicate_configuration.discipline_configurations[0].clone());
        assert_code(
            &catalog,
            &duplicate_configuration,
            CombatBuildErrorCode::DuplicateConfiguration,
        );

        let mut duplicate_slot = base.clone();
        duplicate_slot.discipline_configurations[0]
            .active_assignments
            .push(DisciplineActionBarAssignment {
                action_slot: "slot_0_0".to_string(),
                ability_id: "DAGGER_SLICE".to_string(),
            });
        assert_code(
            &catalog,
            &duplicate_slot,
            CombatBuildErrorCode::DuplicateActionSlot,
        );

        let mut invalid_color = base.clone();
        invalid_color.discipline_configurations[0]
            .weapon
            .main_hand_color_id = "NOT_A_COLOR".to_string();
        assert_code(
            &catalog,
            &invalid_color,
            CombatBuildErrorCode::InvalidWeaponLoadout,
        );

        let mut alias_id = base.clone();
        alias_id.selected_disciplines[0].combat_discipline_id = "daggers".to_string();
        assert_code(&catalog, &alias_id, CombatBuildErrorCode::UnknownDiscipline);

        let mut dormant_unknown_reference = fixture
            .cases
            .iter()
            .find(|case| case.id == "valid_dormant_configuration_does_not_count")
            .expect("dormant fixture")
            .build
            .clone();
        dormant_unknown_reference.discipline_configurations[1].active_assignments[0].ability_id =
            "DELETED_DORMANT_ABILITY".to_string();
        assert_code(
            &catalog,
            &dormant_unknown_reference,
            CombatBuildErrorCode::UnknownAbility,
        );
    }

    #[test]
    fn sword_and_shield_requires_a_legal_paired_off_hand() {
        let catalog = catalog();
        let mut draft = CombatBuildDraft {
            revision: 1,
            starting_discipline_id: None,
            selected_disciplines: vec![SelectedCombatDiscipline {
                slot_index: 0,
                combat_discipline_id: "SWORD_AND_SHIELD".to_string(),
            }],
            discipline_configurations: vec![DisciplineConfiguration {
                combat_discipline_id: "SWORD_AND_SHIELD".to_string(),
                weapon: DisciplineWeaponConfiguration {
                    main_hand_item_def_id: "TRAINING_ONE_HAND_SWORD".to_string(),
                    main_hand_color_id: String::new(),
                    off_hand_item_def_id: "TRAINING_SHIELD".to_string(),
                    off_hand_color_id: String::new(),
                },
                staff_school_ids: Vec::new(),
                active_assignments: vec![DisciplineActionBarAssignment {
                    action_slot: "slot_0_0".to_string(),
                    ability_id: "PALADIN_SHIELD_PUMMEL".to_string(),
                }],
                passive_ability_ids: Vec::new(),
            }],
        };
        catalog
            .validate_draft(&draft, 1)
            .expect("legal sword and shield pair");
        draft.discipline_configurations[0]
            .weapon
            .off_hand_item_def_id
            .clear();
        assert_eq!(
            catalog.validate_draft(&draft, 1).unwrap_err().code,
            CombatBuildErrorCode::InvalidWeaponLoadout
        );
    }

    #[test]
    fn mutation_checks_cover_catalog_failure_domains() {
        let progression: serde_json::Value =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("progression json");
        let weapons: serde_json::Value =
            serde_json::from_str(WEAPON_APPEARANCE_CATALOG_JSON).expect("weapon json");

        let mutations: Vec<(&str, CatalogMutation)> = vec![
            (
                "duplicate discipline",
                Box::new(|p, _| {
                    let rows = p["combat_build_contract"]["combat_disciplines"]
                        .as_array_mut()
                        .unwrap();
                    rows[1]["combat_discipline_id"] = rows[0]["combat_discipline_id"].clone();
                }),
            ),
            (
                "unknown Staff school",
                Box::new(|p, _| {
                    p["abilities"]
                        .as_array_mut()
                        .unwrap()
                        .iter_mut()
                        .find(|row| row["ability_id"] == "SPELL_FIREBALL")
                        .unwrap()["spell_school_id"] =
                        serde_json::Value::String("FIRE".to_string());
                }),
            ),
            (
                "non-Staff school",
                Box::new(|p, _| {
                    p["abilities"]
                        .as_array_mut()
                        .unwrap()
                        .iter_mut()
                        .find(|row| row["ability_id"] == "DAGGER_QUICK_CUT")
                        .unwrap()["spell_school_id"] =
                        serde_json::Value::String("RUIN".to_string());
                }),
            ),
            (
                "ability kind",
                Box::new(|p, _| {
                    p["abilities"]
                        .as_array_mut()
                        .unwrap()
                        .iter_mut()
                        .find(|row| row["ability_id"] == "DAGGER_QUICK_CUT")
                        .unwrap()["selection_kind"] =
                        serde_json::Value::String("PASSIVE".to_string());
                }),
            ),
            (
                "damage type as school",
                Box::new(|p, _| {
                    p["combat_build_contract"]["spell_schools"][0]["spell_school_id"] =
                        serde_json::Value::String("FIRE".to_string());
                    p["combat_build_contract"]["rules"]["staff_school_ids"][0] =
                        serde_json::Value::String("FIRE".to_string());
                }),
            ),
            (
                "weapon ownership",
                Box::new(|_, w| {
                    w["families"][0]["combat_discipline_id"] =
                        serde_json::Value::String("ARCHER_BOW".to_string());
                }),
            ),
            (
                "mode ownership",
                Box::new(|p, _| {
                    p["combat_modes"][0]["combat_discipline_id"] =
                        serde_json::Value::String("RUIN".to_string());
                }),
            ),
            (
                "action slot domain",
                Box::new(|p, _| {
                    p["combat_build_contract"]["rules"]["action_slot_ids"]
                        .as_array_mut()
                        .unwrap()
                        .pop();
                }),
            ),
        ];

        for (label, mutate) in mutations {
            let mut progression = progression.clone();
            let mut weapons = weapons.clone();
            mutate(&mut progression, &mut weapons);
            let progression = serde_json::to_string(&progression).unwrap();
            let weapons = serde_json::to_string(&weapons).unwrap();
            assert!(
                CombatBuildCatalog::from_json(&progression, &weapons).is_err(),
                "catalog mutation '{label}' must fail"
            );
        }
    }
}

//! Pure Combat Build v2 catalog, normalization, and validation.
//!
//! Phase 1 intentionally leaves every v1 persistence and runtime consumer on
//! `combat_build.rs`. Hub, ticket, and match phases adopt this module only at
//! their coordinated boundaries.

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

pub(crate) const COMBAT_BUILD_V2_SCHEMA_VERSION: u32 = 2;
pub(crate) const STAFF_DISCIPLINE_ID: &str = "STAFF";
pub(crate) const MASTERY_TRAIT_ID: &str = "MASTERY";

#[cfg(not(feature = "pvp_match"))]
const COMBAT_BUILD_V2_CATALOG_JSON: &str = include_str!("combat_build_v2_catalog.shared.json");
#[cfg(feature = "pvp_match")]
const COMBAT_BUILD_V2_CATALOG_JSON: &str = include_str!(concat!(
    env!("OUT_DIR"),
    "/combat_build_v2_catalog.shared.json"
));

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

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum CombatSpecializationKind {
    Form,
    School,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum CombatFeatureLoadoutKind {
    Technique,
    Spell,
    Perk,
    Trait,
    Intrinsic,
}

impl CombatFeatureLoadoutKind {
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::Technique => "TECHNIQUE",
            Self::Spell => "SPELL",
            Self::Perk => "PERK",
            Self::Trait => "TRAIT",
            Self::Intrinsic => "INTRINSIC",
        }
    }

    fn is_active(self) -> bool {
        matches!(self, Self::Technique | Self::Spell)
    }
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct CombatBuildV2Draft {
    pub schema_version: u32,
    pub revision: u64,
    pub starting_discipline_id: Option<String>,
    pub selected_specializations: Vec<SelectedCombatSpecialization>,
    #[serde(default)]
    pub dormant_specializations: Vec<String>,
    pub discipline_configurations: Vec<CombatBuildV2DisciplineConfiguration>,
    pub selected_features: Vec<CombatFeatureSelection>,
    pub selected_traits: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct SelectedCombatSpecialization {
    pub slot_index: u8,
    pub specialization_id: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct CombatBuildV2DisciplineConfiguration {
    pub combat_discipline_id: String,
    pub main_hand_item_def_id: String,
    pub main_hand_color_id: String,
    pub off_hand_item_def_id: String,
    pub off_hand_color_id: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct CombatFeatureSelection {
    pub specialization_id: String,
    pub ability_id: String,
    pub preferred_bar_order: Option<u8>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct CombatBuildV2Snapshot {
    pub schema_version: u32,
    pub revision: u64,
    pub starting_discipline_id: String,
    pub selected_specializations: Vec<SelectedCombatSpecialization>,
    pub dormant_specializations: Vec<String>,
    pub discipline_configurations: Vec<CombatBuildV2DisciplineConfiguration>,
    pub selected_features: Vec<CombatFeatureSelection>,
    pub selected_traits: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct DerivedTechniqueBar {
    pub combat_discipline_id: String,
    pub ability_ids: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct CombatBuildV2Projection {
    pub parent_discipline_ids: Vec<String>,
    pub technique_bars: Vec<DerivedTechniqueBar>,
    pub spell_ability_ids: Vec<String>,
    pub perk_ability_ids: Vec<String>,
    pub trait_ability_ids: Vec<String>,
    pub mastery_active: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct ValidatedCombatBuildV2 {
    pub snapshot: CombatBuildV2Snapshot,
    pub projection: CombatBuildV2Projection,
    pub technique_count: usize,
    pub spell_count: usize,
    pub perk_count: usize,
    pub trait_count: usize,
}

impl ValidatedCombatBuildV2 {
    pub(crate) fn selected_feature_count(&self) -> usize {
        self.technique_count + self.spell_count + self.perk_count
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum CombatBuildV2ErrorCode {
    UnsupportedSchemaVersion,
    StaleRevision,
    SpecializationCount,
    SpecializationSlotOrder,
    DuplicateSpecialization,
    UnknownSpecialization,
    DormantSpecializationConflict,
    StartingDisciplineNotSelected,
    DuplicateConfiguration,
    MissingDisciplineConfiguration,
    UnreferencedDisciplineConfiguration,
    InvalidWeaponLoadout,
    UnknownFeature,
    FeatureOwner,
    DuplicateFeature,
    EmptySpecialization,
    FeatureCapacity,
    PassiveBarOrder,
    TraitCapacity,
    DuplicateTrait,
    UnknownTrait,
}

impl CombatBuildV2ErrorCode {
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::UnsupportedSchemaVersion => "COMBAT_BUILD_V2_UNSUPPORTED_SCHEMA_VERSION",
            Self::StaleRevision => "COMBAT_BUILD_V2_STALE_REVISION",
            Self::SpecializationCount => "COMBAT_BUILD_V2_SPECIALIZATION_COUNT",
            Self::SpecializationSlotOrder => "COMBAT_BUILD_V2_SPECIALIZATION_SLOT_ORDER",
            Self::DuplicateSpecialization => "COMBAT_BUILD_V2_DUPLICATE_SPECIALIZATION",
            Self::UnknownSpecialization => "COMBAT_BUILD_V2_UNKNOWN_SPECIALIZATION",
            Self::DormantSpecializationConflict => {
                "COMBAT_BUILD_V2_DORMANT_SPECIALIZATION_CONFLICT"
            }
            Self::StartingDisciplineNotSelected => {
                "COMBAT_BUILD_V2_STARTING_DISCIPLINE_NOT_SELECTED"
            }
            Self::DuplicateConfiguration => "COMBAT_BUILD_V2_DUPLICATE_CONFIGURATION",
            Self::MissingDisciplineConfiguration => {
                "COMBAT_BUILD_V2_MISSING_DISCIPLINE_CONFIGURATION"
            }
            Self::UnreferencedDisciplineConfiguration => {
                "COMBAT_BUILD_V2_UNREFERENCED_DISCIPLINE_CONFIGURATION"
            }
            Self::InvalidWeaponLoadout => "COMBAT_BUILD_V2_INVALID_WEAPON_LOADOUT",
            Self::UnknownFeature => "COMBAT_BUILD_V2_UNKNOWN_FEATURE",
            Self::FeatureOwner => "COMBAT_BUILD_V2_FEATURE_OWNER",
            Self::DuplicateFeature => "COMBAT_BUILD_V2_DUPLICATE_FEATURE",
            Self::EmptySpecialization => "COMBAT_BUILD_V2_EMPTY_SPECIALIZATION",
            Self::FeatureCapacity => "COMBAT_BUILD_V2_FEATURE_CAPACITY",
            Self::PassiveBarOrder => "COMBAT_BUILD_V2_PASSIVE_BAR_ORDER",
            Self::TraitCapacity => "COMBAT_BUILD_V2_TRAIT_CAPACITY",
            Self::DuplicateTrait => "COMBAT_BUILD_V2_DUPLICATE_TRAIT",
            Self::UnknownTrait => "COMBAT_BUILD_V2_UNKNOWN_TRAIT",
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct CombatBuildV2ValidationError {
    pub code: CombatBuildV2ErrorCode,
    pub detail: String,
}

impl CombatBuildV2ValidationError {
    fn new(code: CombatBuildV2ErrorCode, detail: impl Into<String>) -> Self {
        Self {
            code,
            detail: detail.into(),
        }
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct CombatBuildV2Rules {
    pub minimum_selected_specializations: usize,
    pub maximum_selected_specializations: usize,
    pub global_feature_capacity: usize,
    pub trait_capacity: usize,
    pub default_starting_discipline: String,
    pub direct_action_input_ids: Vec<String>,
}

#[derive(Clone, Debug)]
pub(crate) struct CombatBuildV2Catalog {
    rules: CombatBuildV2Rules,
    specializations: HashMap<String, CatalogSpecialization>,
    features: HashMap<String, CatalogFeature>,
    traits: HashMap<String, CatalogTrait>,
    intrinsic_ability_ids: HashSet<String>,
    removed_player_ability_ids: HashSet<String>,
    weapons: HashMap<String, CatalogWeapon>,
    default_draft: CombatBuildV2Draft,
}

#[derive(Clone, Debug)]
struct CatalogSpecialization {
    specialization_id: String,
    combat_discipline_id: String,
    specialization_kind: CombatSpecializationKind,
    display_name: String,
    sort_order: u32,
}

#[derive(Clone, Debug)]
struct CatalogFeature {
    ability_id: String,
    specialization_id: String,
    loadout_kind: CombatFeatureLoadoutKind,
    sort_order: u32,
    display_name: String,
    resource_kind: String,
    resource_cost: f32,
}

#[derive(Clone, Debug)]
struct CatalogTrait {
    ability_id: String,
    display_name: String,
    sort_order: u32,
    modifier_scalar: f32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct CombatSpecializationCatalogEntry {
    pub specialization_id: String,
    pub combat_discipline_id: String,
    pub specialization_kind: CombatSpecializationKind,
    pub display_name: String,
    pub sort_order: u32,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct CombatFeatureCatalogEntry {
    pub ability_id: String,
    pub specialization_id: String,
    pub combat_discipline_id: String,
    pub loadout_kind: CombatFeatureLoadoutKind,
    pub display_name: String,
    pub resource_kind: String,
    pub resource_cost: f32,
    pub sort_order: u32,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct CombatTraitCatalogEntry {
    pub ability_id: String,
    pub display_name: String,
    pub loadout_kind: CombatFeatureLoadoutKind,
    pub sort_order: u32,
    pub modifier_scalar: f32,
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
#[serde(deny_unknown_fields)]
struct CombatBuildV2CatalogSource {
    schema_version: u32,
    source_contract: String,
    source_contract_sha256: String,
    rules: CombatBuildV2Rules,
    specializations: Vec<SpecializationSource>,
    traits: Vec<TraitSource>,
    intrinsic_abilities: Vec<IntrinsicAbilitySource>,
    removed_player_abilities: Vec<RemovedAbilitySource>,
    default_build: CombatBuildV2Draft,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SpecializationSource {
    specialization_id: String,
    combat_discipline_id: String,
    specialization_kind: CombatSpecializationKind,
    display_name: String,
    sort_order: u32,
    technique_ability_ids: Vec<String>,
    spell_ability_ids: Vec<String>,
    perk_ability_ids: Vec<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct TraitSource {
    ability_id: String,
    display_name: String,
    loadout_kind: CombatFeatureLoadoutKind,
    sort_order: u32,
    effect_kind: String,
    modifier_scalar: f32,
    condition: String,
    damage_scope: String,
    excludes: Vec<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct IntrinsicAbilitySource {
    ability_id: String,
    loadout_kind: CombatFeatureLoadoutKind,
    selectable: bool,
    counts_toward_capacity: bool,
    disposition: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RemovedAbilitySource {
    ability_id: String,
    current_selection_kind: String,
    current_gameplay_kind: String,
    disposition: String,
    may_retain_private_presentation_data: bool,
}

#[derive(Deserialize)]
struct ProgressionCatalogSource {
    combat_build_contract: LegacyCombatBuildContractSource,
    abilities: Vec<ProgressionAbilitySource>,
}

#[derive(Deserialize)]
struct LegacyCombatBuildContractSource {
    combat_disciplines: Vec<LegacyDisciplineSource>,
    spell_schools: Vec<LegacySchoolSource>,
}

#[derive(Deserialize)]
struct LegacyDisciplineSource {
    combat_discipline_id: String,
}

#[derive(Deserialize)]
struct LegacySchoolSource {
    spell_school_id: String,
}

#[derive(Deserialize)]
struct ProgressionAbilitySource {
    ability_id: String,
    actor_scope: String,
    selection_kind: String,
    #[serde(default)]
    display_name: String,
    #[serde(default)]
    resource_kind: String,
    #[serde(default)]
    resource_cost: f32,
    #[serde(default)]
    sort_order: u32,
    gameplay: ProgressionGameplaySource,
}

#[derive(Deserialize)]
struct ProgressionGameplaySource {
    kind: String,
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

impl CombatBuildV2Catalog {
    pub(crate) fn from_shared_catalogs() -> Result<Self, String> {
        Self::from_json(
            COMBAT_BUILD_V2_CATALOG_JSON,
            PROGRESSION_CATALOG_JSON,
            WEAPON_APPEARANCE_CATALOG_JSON,
        )
    }

    fn from_json(v2_json: &str, progression_json: &str, weapon_json: &str) -> Result<Self, String> {
        let source: CombatBuildV2CatalogSource = serde_json::from_str(v2_json)
            .map_err(|error| format!("Combat Build v2 catalog schema error: {error}"))?;
        let progression: ProgressionCatalogSource = serde_json::from_str(progression_json)
            .map_err(|error| format!("progression catalog schema error: {error}"))?;
        let weapons: WeaponCatalogSource = serde_json::from_str(weapon_json)
            .map_err(|error| format!("weapon catalog schema error: {error}"))?;

        validate_catalog_source(&source, &progression, &weapons)?;

        let mut specializations = HashMap::new();
        let mut features = HashMap::new();
        for row in &source.specializations {
            specializations.insert(
                row.specialization_id.clone(),
                CatalogSpecialization {
                    specialization_id: row.specialization_id.clone(),
                    combat_discipline_id: row.combat_discipline_id.clone(),
                    specialization_kind: row.specialization_kind,
                    display_name: row.display_name.clone(),
                    sort_order: row.sort_order,
                },
            );
            for (loadout_kind, ability_ids) in [
                (
                    CombatFeatureLoadoutKind::Technique,
                    &row.technique_ability_ids,
                ),
                (CombatFeatureLoadoutKind::Spell, &row.spell_ability_ids),
                (CombatFeatureLoadoutKind::Perk, &row.perk_ability_ids),
            ] {
                for ability_id in ability_ids {
                    let ability = progression
                        .abilities
                        .iter()
                        .find(|row| row.ability_id == *ability_id)
                        .expect("catalog validation checked feature mechanics");
                    features.insert(
                        ability_id.clone(),
                        CatalogFeature {
                            ability_id: ability_id.clone(),
                            specialization_id: row.specialization_id.clone(),
                            loadout_kind,
                            sort_order: ability.sort_order,
                            display_name: ability.display_name.clone(),
                            resource_kind: ability.resource_kind.clone(),
                            resource_cost: ability.resource_cost,
                        },
                    );
                }
            }
        }
        let traits = source
            .traits
            .iter()
            .map(|row| {
                (
                    row.ability_id.clone(),
                    CatalogTrait {
                        ability_id: row.ability_id.clone(),
                        display_name: row.display_name.clone(),
                        sort_order: row.sort_order,
                        modifier_scalar: row.modifier_scalar,
                    },
                )
            })
            .collect();
        let weapon_rows = weapons
            .families
            .iter()
            .map(|row| {
                (
                    row.item_def_id.clone(),
                    CatalogWeapon {
                        combat_discipline_id: row.combat_discipline_id.clone(),
                        hand_requirement: row.hand_requirement.clone(),
                        equip_slot: row.equip_slot.clone(),
                        weapon_kind: row.weapon_kind.clone(),
                        color_ids: row
                            .variants
                            .iter()
                            .map(|variant| variant.color_id.clone())
                            .collect(),
                    },
                )
            })
            .collect();

        let catalog = Self {
            rules: source.rules,
            specializations,
            features,
            traits,
            intrinsic_ability_ids: source
                .intrinsic_abilities
                .iter()
                .map(|row| row.ability_id.clone())
                .collect(),
            removed_player_ability_ids: source
                .removed_player_abilities
                .iter()
                .map(|row| row.ability_id.clone())
                .collect(),
            weapons: weapon_rows,
            default_draft: source.default_build,
        };
        catalog
            .validate_draft(&catalog.default_draft, catalog.default_draft.revision)
            .map_err(|error| {
                format!(
                    "Combat Build v2 default failed {}: {}",
                    error.code.as_str(),
                    error.detail
                )
            })?;
        Ok(catalog)
    }

    pub(crate) fn rules(&self) -> &CombatBuildV2Rules {
        &self.rules
    }

    pub(crate) fn specialization_definitions(&self) -> Vec<CombatSpecializationCatalogEntry> {
        let mut rows: Vec<_> = self
            .specializations
            .values()
            .map(|row| CombatSpecializationCatalogEntry {
                specialization_id: row.specialization_id.clone(),
                combat_discipline_id: row.combat_discipline_id.clone(),
                specialization_kind: row.specialization_kind,
                display_name: row.display_name.clone(),
                sort_order: row.sort_order,
            })
            .collect();
        rows.sort_by(|left, right| {
            (
                left.combat_discipline_id.as_str(),
                left.sort_order,
                left.specialization_id.as_str(),
            )
                .cmp(&(
                    right.combat_discipline_id.as_str(),
                    right.sort_order,
                    right.specialization_id.as_str(),
                ))
        });
        rows
    }

    pub(crate) fn feature_definitions(&self) -> Vec<CombatFeatureCatalogEntry> {
        let mut rows: Vec<_> = self
            .features
            .values()
            .map(|row| CombatFeatureCatalogEntry {
                ability_id: row.ability_id.clone(),
                specialization_id: row.specialization_id.clone(),
                combat_discipline_id: self.specializations[&row.specialization_id]
                    .combat_discipline_id
                    .clone(),
                loadout_kind: row.loadout_kind,
                display_name: row.display_name.clone(),
                resource_kind: row.resource_kind.clone(),
                resource_cost: row.resource_cost,
                sort_order: row.sort_order,
            })
            .collect();
        rows.sort_by(|left, right| {
            (
                left.specialization_id.as_str(),
                left.sort_order,
                left.ability_id.as_str(),
            )
                .cmp(&(
                    right.specialization_id.as_str(),
                    right.sort_order,
                    right.ability_id.as_str(),
                ))
        });
        rows
    }

    pub(crate) fn trait_definitions(&self) -> Vec<CombatTraitCatalogEntry> {
        let mut rows: Vec<_> = self
            .traits
            .values()
            .map(|row| CombatTraitCatalogEntry {
                ability_id: row.ability_id.clone(),
                display_name: row.display_name.clone(),
                loadout_kind: CombatFeatureLoadoutKind::Trait,
                sort_order: row.sort_order,
                modifier_scalar: row.modifier_scalar,
            })
            .collect();
        rows.sort_by(|left, right| {
            (left.sort_order, left.ability_id.as_str())
                .cmp(&(right.sort_order, right.ability_id.as_str()))
        });
        rows
    }

    pub(crate) fn default_draft(&self) -> CombatBuildV2Draft {
        self.default_draft.clone()
    }

    pub(crate) fn specialization_parent(&self, specialization_id: &str) -> Option<&str> {
        self.specializations
            .get(specialization_id)
            .map(|row| row.combat_discipline_id.as_str())
    }

    pub(crate) fn specialization_kind(
        &self,
        specialization_id: &str,
    ) -> Option<CombatSpecializationKind> {
        self.specializations
            .get(specialization_id)
            .map(|row| row.specialization_kind)
    }

    pub(crate) fn feature_loadout_kind(
        &self,
        ability_id: &str,
    ) -> Option<CombatFeatureLoadoutKind> {
        self.features.get(ability_id).map(|row| row.loadout_kind)
    }

    pub(crate) fn feature_specialization(&self, ability_id: &str) -> Option<&str> {
        self.features
            .get(ability_id)
            .map(|row| row.specialization_id.as_str())
    }

    pub(crate) fn is_private_intrinsic(&self, ability_id: &str) -> bool {
        self.intrinsic_ability_ids.contains(ability_id)
    }

    pub(crate) fn is_removed_player_ability(&self, ability_id: &str) -> bool {
        self.removed_player_ability_ids.contains(ability_id)
    }

    pub(crate) fn mastery_modifier_scalar(&self) -> f32 {
        self.traits
            .get(MASTERY_TRAIT_ID)
            .map(|row| row.modifier_scalar)
            .unwrap_or(0.0)
    }
}

fn exact_id(value: &str) -> bool {
    !value.is_empty() && value == value.trim() && value == value.to_ascii_uppercase()
}

fn validate_catalog_source(
    source: &CombatBuildV2CatalogSource,
    progression: &ProgressionCatalogSource,
    weapons: &WeaponCatalogSource,
) -> Result<(), String> {
    if source.schema_version != COMBAT_BUILD_V2_SCHEMA_VERSION {
        return Err(format!(
            "unsupported Combat Build v2 schema version {}",
            source.schema_version
        ));
    }
    if source.source_contract.trim().is_empty()
        || source.source_contract_sha256.len() != 64
        || !source
            .source_contract_sha256
            .chars()
            .all(|character| character.is_ascii_hexdigit())
    {
        return Err("Combat Build v2 source-contract provenance is invalid".to_string());
    }
    validate_v2_rules(&source.rules)?;

    let discipline_ids: HashSet<_> = progression
        .combat_build_contract
        .combat_disciplines
        .iter()
        .map(|row| row.combat_discipline_id.as_str())
        .collect();
    let legacy_school_ids: HashSet<_> = progression
        .combat_build_contract
        .spell_schools
        .iter()
        .map(|row| row.spell_school_id.as_str())
        .collect();
    let progression_abilities: HashMap<_, _> = progression
        .abilities
        .iter()
        .map(|row| (row.ability_id.as_str(), row))
        .collect();

    let mut specialization_ids = HashSet::new();
    let mut specialization_sort_keys = HashSet::new();
    let mut school_ids = HashSet::new();
    let mut parent_ids_with_specializations = HashSet::new();
    let mut projected_features = HashMap::new();
    for specialization in &source.specializations {
        let id = specialization.specialization_id.as_str();
        let parent = specialization.combat_discipline_id.as_str();
        if !exact_id(id) || !specialization_ids.insert(id) {
            return Err(format!("specialization id '{id}' must be exact and unique"));
        }
        if !discipline_ids.contains(parent) {
            return Err(format!(
                "specialization '{id}' has unknown parent Discipline '{parent}'"
            ));
        }
        if specialization.display_name.trim().is_empty()
            || !specialization_sort_keys.insert((parent, specialization.sort_order))
        {
            return Err(format!(
                "specialization '{id}' needs a display name and parent-unique sort order"
            ));
        }
        match specialization.specialization_kind {
            CombatSpecializationKind::School if parent == STAFF_DISCIPLINE_ID => {
                school_ids.insert(id);
                if !specialization.technique_ability_ids.is_empty() {
                    return Err(format!("School '{id}' may not own Techniques"));
                }
            }
            CombatSpecializationKind::Form if parent != STAFF_DISCIPLINE_ID => {}
            CombatSpecializationKind::School => {
                return Err(format!("School '{id}' must have parent STAFF"));
            }
            CombatSpecializationKind::Form => {
                return Err(format!("Form '{id}' may not have parent STAFF"));
            }
        }
        parent_ids_with_specializations.insert(parent);
        if specialization.technique_ability_ids.is_empty()
            && specialization.spell_ability_ids.is_empty()
            && specialization.perk_ability_ids.is_empty()
        {
            return Err(format!("specialization '{id}' may not be empty"));
        }

        for (kind, ids) in [
            (
                CombatFeatureLoadoutKind::Technique,
                &specialization.technique_ability_ids,
            ),
            (
                CombatFeatureLoadoutKind::Spell,
                &specialization.spell_ability_ids,
            ),
            (
                CombatFeatureLoadoutKind::Perk,
                &specialization.perk_ability_ids,
            ),
        ] {
            for ability_id in ids {
                if !exact_id(ability_id)
                    || projected_features
                        .insert(ability_id.as_str(), (id, kind))
                        .is_some()
                {
                    return Err(format!(
                        "feature '{ability_id}' must be exact and mapped exactly once"
                    ));
                }
            }
        }
    }
    if parent_ids_with_specializations != discipline_ids {
        return Err("every Discipline must own at least one v2 Specialization".to_string());
    }
    if school_ids != legacy_school_ids {
        return Err("v2 Schools must exactly project the six existing School IDs".to_string());
    }

    let mut removed_ids = HashSet::new();
    for row in &source.removed_player_abilities {
        if !exact_id(row.ability_id.as_str()) || !removed_ids.insert(row.ability_id.as_str()) {
            return Err("removed player ability IDs must be exact and unique".to_string());
        }
        if row.disposition.trim().is_empty()
            || row.current_selection_kind.trim().is_empty()
            || row.current_gameplay_kind.trim().is_empty()
        {
            return Err(format!(
                "removed ability '{}' lacks its disposition",
                row.ability_id
            ));
        }
    }
    let expected_removed: HashSet<_> = [
        "STAFF_STRIKE",
        "STAFF_STRIKE_2",
        "STAFF_SWEEP",
        "STAFF_THRUST",
    ]
    .into_iter()
    .collect();
    if removed_ids != expected_removed {
        return Err("the v2 Staff-melee removal ledger must contain exactly four IDs".to_string());
    }
    let staff_strike_2 = source
        .removed_player_abilities
        .iter()
        .find(|row| row.ability_id == "STAFF_STRIKE_2")
        .expect("exact removal set checked");
    if !staff_strike_2.may_retain_private_presentation_data
        || source.removed_player_abilities.iter().any(|row| {
            row.ability_id != "STAFF_STRIKE_2" && row.may_retain_private_presentation_data
        })
    {
        return Err("only STAFF_STRIKE_2 may retain private presentation data".to_string());
    }

    let expected_selectable: HashSet<_> = progression
        .abilities
        .iter()
        .filter(|row| {
            row.actor_scope == "PLAYER"
                && matches!(row.selection_kind.as_str(), "ACTIVE" | "PASSIVE")
                && !removed_ids.contains(row.ability_id.as_str())
        })
        .map(|row| row.ability_id.as_str())
        .collect();
    if projected_features.keys().copied().collect::<HashSet<_>>() != expected_selectable {
        return Err("v2 feature projection must classify every retained selectable player ability exactly once".to_string());
    }

    for (ability_id, (specialization_id, kind)) in &projected_features {
        let ability = progression_abilities
            .get(ability_id)
            .expect("exhaustive projection checked");
        match kind {
            CombatFeatureLoadoutKind::Technique => {
                if ability.selection_kind != "ACTIVE" {
                    return Err(format!(
                        "Technique '{ability_id}' must be structurally ACTIVE"
                    ));
                }
                let specialization = source
                    .specializations
                    .iter()
                    .find(|row| row.specialization_id == *specialization_id)
                    .expect("specialization exists");
                if specialization.specialization_kind != CombatSpecializationKind::Form
                    || specialization.combat_discipline_id == STAFF_DISCIPLINE_ID
                {
                    return Err(format!(
                        "Technique '{ability_id}' must belong to a non-Staff Form"
                    ));
                }
            }
            CombatFeatureLoadoutKind::Spell => {
                if ability.selection_kind != "ACTIVE" || ability.gameplay.kind != "SPELL" {
                    return Err(format!(
                        "Spell '{ability_id}' must be ACTIVE and use the weapon-independent SPELL executor"
                    ));
                }
            }
            CombatFeatureLoadoutKind::Perk => {
                if ability.selection_kind != "PASSIVE" || ability.gameplay.kind != "PASSIVE" {
                    return Err(format!("Perk '{ability_id}' must be structurally PASSIVE"));
                }
            }
            _ => unreachable!("only selectable kinds are projected here"),
        }
    }

    let mut intrinsic_ids = HashSet::new();
    for row in &source.intrinsic_abilities {
        if !exact_id(row.ability_id.as_str()) || !intrinsic_ids.insert(row.ability_id.as_str()) {
            return Err("intrinsic ability IDs must be exact and unique".to_string());
        }
        if row.loadout_kind != CombatFeatureLoadoutKind::Intrinsic
            || row.selectable
            || row.counts_toward_capacity
            || row.disposition.trim().is_empty()
        {
            return Err(format!(
                "intrinsic '{}' has invalid v2 metadata",
                row.ability_id
            ));
        }
    }
    let expected_intrinsics: HashSet<_> = progression
        .abilities
        .iter()
        .filter(|row| {
            row.actor_scope == "PLAYER"
                && row.selection_kind == "INTRINSIC"
                && !removed_ids.contains(row.ability_id.as_str())
        })
        .map(|row| row.ability_id.as_str())
        .collect();
    if intrinsic_ids != expected_intrinsics {
        return Err("v2 intrinsic projection must classify every retained private player action exactly once".to_string());
    }

    for removed_id in &removed_ids {
        let Some(ability) = progression_abilities.get(removed_id) else {
            return Err(format!(
                "removed player ability '{removed_id}' is absent from v1"
            ));
        };
        if ability.actor_scope != "PLAYER"
            || projected_features.contains_key(removed_id)
            || intrinsic_ids.contains(removed_id)
        {
            return Err(format!(
                "removed player ability '{removed_id}' leaked into v2"
            ));
        }
    }

    validate_traits(&source.traits)?;
    validate_weapon_catalog(weapons, &discipline_ids)?;
    Ok(())
}

fn validate_v2_rules(rules: &CombatBuildV2Rules) -> Result<(), String> {
    if rules.minimum_selected_specializations != 1
        || rules.maximum_selected_specializations != 3
        || rules.global_feature_capacity != 18
        || rules.trait_capacity != 3
        || rules.default_starting_discipline != "selected_specializations[0].parent_discipline"
    {
        return Err("Combat Build v2 rules do not match the locked Phase 0 contract".to_string());
    }
    let input_ids: HashSet<_> = rules
        .direct_action_input_ids
        .iter()
        .map(String::as_str)
        .collect();
    if rules.direct_action_input_ids.len() != 18
        || input_ids.len() != 18
        || rules
            .direct_action_input_ids
            .iter()
            .enumerate()
            .any(|(index, id)| id != &format!("COMBAT_ACTION_{index:02}"))
    {
        return Err("v2 must expose exactly COMBAT_ACTION_00 through COMBAT_ACTION_17".to_string());
    }
    Ok(())
}

fn validate_traits(traits: &[TraitSource]) -> Result<(), String> {
    if traits.len() != 1 {
        return Err("v2 initially requires exactly the MASTERY Trait".to_string());
    }
    let trait_row = &traits[0];
    if trait_row.ability_id != MASTERY_TRAIT_ID
        || trait_row.display_name.trim().is_empty()
        || trait_row.loadout_kind != CombatFeatureLoadoutKind::Trait
        || trait_row.sort_order == 0
        || trait_row.effect_kind != "SINGLE_PARENT_OUTGOING_DAMAGE_MULTIPLIER"
        || !trait_row.modifier_scalar.is_finite()
        || (trait_row.modifier_scalar - 0.10).abs() > f32::EPSILON
        || trait_row.condition != "EXACTLY_ONE_DISTINCT_PARENT_DISCIPLINE"
        || trait_row.damage_scope != "NORMAL_PLAYER_AUTHORED_OUTGOING_DAMAGE"
        || trait_row.excludes != ["SYSTEM", "SELF_INFLICTED_FINAL", "COPIED_FINAL"]
    {
        return Err("MASTERY does not match its locked Phase 0 definition".to_string());
    }
    Ok(())
}

fn validate_weapon_catalog(
    source: &WeaponCatalogSource,
    discipline_ids: &HashSet<&str>,
) -> Result<(), String> {
    if source.schema_version != 1 {
        return Err(format!(
            "unsupported weapon catalog schema version {}",
            source.schema_version
        ));
    }
    let mut item_ids = HashSet::new();
    for weapon in &source.families {
        if !exact_id(weapon.item_def_id.as_str()) || !item_ids.insert(weapon.item_def_id.as_str()) {
            return Err(format!(
                "weapon '{}' must have an exact unique ID",
                weapon.item_def_id
            ));
        }
        if !discipline_ids.contains(weapon.combat_discipline_id.as_str()) {
            return Err(format!(
                "weapon '{}' has unknown Discipline '{}'",
                weapon.item_def_id, weapon.combat_discipline_id
            ));
        }
        validate_weapon_shape(weapon)?;
        let mut colors = HashSet::new();
        for variant in &weapon.variants {
            if !exact_id(variant.color_id.as_str()) || !colors.insert(variant.color_id.as_str()) {
                return Err(format!(
                    "weapon '{}' color IDs must be exact and unique",
                    weapon.item_def_id
                ));
            }
        }
    }
    Ok(())
}

fn validate_weapon_shape(weapon: &WeaponSource) -> Result<(), String> {
    let valid = match weapon.combat_discipline_id.as_str() {
        "DAGGERS" => {
            weapon.weapon_kind == "DAGGER_PAIR"
                && weapon.hand_requirement == "TWO_HAND"
                && weapon.equip_slot == "MAIN_HAND"
        }
        "TWO_HANDED_SWORD" => {
            matches!(
                weapon.weapon_kind.as_str(),
                "TWO_HAND_SWORD" | "TWO_HAND_AXE" | "TWO_HAND_HAMMER" | "POLEARM"
            ) && weapon.hand_requirement == "TWO_HAND"
                && weapon.equip_slot == "MAIN_HAND"
        }
        "SWORD_AND_SHIELD" => match weapon.equip_slot.as_str() {
            "MAIN_HAND" => {
                matches!(
                    weapon.weapon_kind.as_str(),
                    "ONE_HAND_SWORD" | "ONE_HAND_AXE" | "ONE_HAND_HAMMER" | "ONE_HAND_FIST"
                ) && weapon.hand_requirement == "ONE_HAND"
            }
            "OFF_HAND" => weapon.weapon_kind == "SHIELD" && weapon.hand_requirement == "OFF_HAND",
            _ => false,
        },
        "ARCHER_BOW" => {
            weapon.weapon_kind == "BOW"
                && weapon.hand_requirement == "TWO_HAND"
                && weapon.equip_slot == "MAIN_HAND"
        }
        STAFF_DISCIPLINE_ID => {
            weapon.weapon_kind == "STAFF"
                && weapon.hand_requirement == "TWO_HAND"
                && weapon.equip_slot == "MAIN_HAND"
        }
        _ => false,
    };
    if valid {
        Ok(())
    } else {
        Err(format!(
            "weapon '{}' has an illegal shape for '{}'",
            weapon.item_def_id, weapon.combat_discipline_id
        ))
    }
}

impl CombatBuildV2Catalog {
    pub(crate) fn validate_draft(
        &self,
        draft: &CombatBuildV2Draft,
        expected_revision: u64,
    ) -> Result<ValidatedCombatBuildV2, CombatBuildV2ValidationError> {
        if draft.schema_version != COMBAT_BUILD_V2_SCHEMA_VERSION {
            return Err(CombatBuildV2ValidationError::new(
                CombatBuildV2ErrorCode::UnsupportedSchemaVersion,
                format!(
                    "draft schema version {} is unsupported; expected {}",
                    draft.schema_version, COMBAT_BUILD_V2_SCHEMA_VERSION
                ),
            ));
        }
        if draft.revision != expected_revision {
            return Err(CombatBuildV2ValidationError::new(
                CombatBuildV2ErrorCode::StaleRevision,
                format!(
                    "draft revision {} does not match current revision {expected_revision}",
                    draft.revision
                ),
            ));
        }
        if !(self.rules.minimum_selected_specializations
            ..=self.rules.maximum_selected_specializations)
            .contains(&draft.selected_specializations.len())
        {
            return Err(CombatBuildV2ValidationError::new(
                CombatBuildV2ErrorCode::SpecializationCount,
                "selected Specialization count is outside the authored range",
            ));
        }

        let mut selected_ids = HashSet::new();
        let mut selected_slots = HashMap::new();
        let mut parent_discipline_ids = Vec::new();
        let mut selected_parent_ids = HashSet::new();
        for (expected_slot, selected) in draft.selected_specializations.iter().enumerate() {
            if usize::from(selected.slot_index) != expected_slot {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::SpecializationSlotOrder,
                    "selected Specialization slots must be contiguous and ordered from zero",
                ));
            }
            let Some(specialization) = self.specializations.get(&selected.specialization_id) else {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::UnknownSpecialization,
                    format!(
                        "unknown selected Specialization '{}'",
                        selected.specialization_id
                    ),
                ));
            };
            if !selected_ids.insert(selected.specialization_id.as_str()) {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::DuplicateSpecialization,
                    format!(
                        "Specialization '{}' is selected more than once",
                        selected.specialization_id
                    ),
                ));
            }
            selected_slots.insert(selected.specialization_id.as_str(), expected_slot);
            if selected_parent_ids.insert(specialization.combat_discipline_id.as_str()) {
                parent_discipline_ids.push(specialization.combat_discipline_id.clone());
            }
        }

        let mut dormant_ids = HashSet::new();
        for specialization_id in &draft.dormant_specializations {
            if !self.specializations.contains_key(specialization_id) {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::UnknownSpecialization,
                    format!("unknown dormant Specialization '{specialization_id}'"),
                ));
            }
            if selected_ids.contains(specialization_id.as_str())
                || !dormant_ids.insert(specialization_id.as_str())
            {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::DormantSpecializationConflict,
                    format!(
                        "dormant Specialization '{specialization_id}' is selected or duplicated"
                    ),
                ));
            }
        }

        if let Some(starting_id) = draft.starting_discipline_id.as_deref() {
            if !selected_parent_ids.contains(starting_id) {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::StartingDisciplineNotSelected,
                    format!("starting Discipline '{starting_id}' is not derived by the build"),
                ));
            }
        }

        let referenced_parent_ids: HashSet<_> = selected_ids
            .iter()
            .chain(dormant_ids.iter())
            .map(|specialization_id| {
                self.specializations[*specialization_id]
                    .combat_discipline_id
                    .as_str()
            })
            .collect();
        let mut configurations = HashMap::new();
        for configuration in &draft.discipline_configurations {
            let discipline_id = configuration.combat_discipline_id.as_str();
            if !referenced_parent_ids.contains(discipline_id) {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::UnreferencedDisciplineConfiguration,
                    format!(
                        "Discipline configuration '{discipline_id}' has no selected or dormant Specialization"
                    ),
                ));
            }
            if configurations
                .insert(discipline_id, configuration)
                .is_some()
            {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::DuplicateConfiguration,
                    format!("Discipline '{discipline_id}' has multiple configurations"),
                ));
            }
            self.validate_weapon_configuration(configuration)?;
        }
        for parent_id in &referenced_parent_ids {
            if !configurations.contains_key(parent_id) {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::MissingDisciplineConfiguration,
                    format!(
                        "selected or dormant parent Discipline '{parent_id}' has no configuration"
                    ),
                ));
            }
        }

        let mut selected_features = draft.selected_features.clone();
        let mut seen_feature_ids = HashSet::new();
        let mut selected_feature_counts: HashMap<&str, usize> = selected_ids
            .iter()
            .map(|specialization_id| (*specialization_id, 0))
            .collect();
        let mut technique_count = 0usize;
        let mut spell_count = 0usize;
        let mut perk_count = 0usize;
        let mut active_scopes: HashMap<String, Vec<usize>> = HashMap::new();

        for (input_index, selection) in selected_features.iter().enumerate() {
            let Some(feature) = self.features.get(&selection.ability_id) else {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::UnknownFeature,
                    format!("unknown or removed feature '{}'", selection.ability_id),
                ));
            };
            if feature.specialization_id != selection.specialization_id {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::FeatureOwner,
                    format!(
                        "feature '{}' belongs to '{}' rather than '{}'",
                        selection.ability_id,
                        feature.specialization_id,
                        selection.specialization_id
                    ),
                ));
            }
            if !selected_ids.contains(selection.specialization_id.as_str())
                && !dormant_ids.contains(selection.specialization_id.as_str())
            {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::UnknownSpecialization,
                    format!(
                        "feature '{}' references a Specialization absent from selected and dormant state",
                        selection.ability_id
                    ),
                ));
            }
            if !seen_feature_ids.insert(selection.ability_id.as_str()) {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::DuplicateFeature,
                    format!(
                        "feature '{}' is selected more than once",
                        selection.ability_id
                    ),
                ));
            }
            if !feature.loadout_kind.is_active() && selection.preferred_bar_order.is_some() {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::PassiveBarOrder,
                    format!("Perk '{}' may not have bar order", selection.ability_id),
                ));
            }

            if selected_ids.contains(selection.specialization_id.as_str()) {
                *selected_feature_counts
                    .get_mut(selection.specialization_id.as_str())
                    .expect("selected feature count initialized") += 1;
                match feature.loadout_kind {
                    CombatFeatureLoadoutKind::Technique => technique_count += 1,
                    CombatFeatureLoadoutKind::Spell => spell_count += 1,
                    CombatFeatureLoadoutKind::Perk => perk_count += 1,
                    _ => unreachable!("catalog admits only selectable feature kinds"),
                }
                if feature.loadout_kind.is_active() {
                    let specialization = &self.specializations[&feature.specialization_id];
                    let scope = match feature.loadout_kind {
                        CombatFeatureLoadoutKind::Spell => "SPELL".to_string(),
                        CombatFeatureLoadoutKind::Technique => {
                            format!("TECHNIQUE:{}", specialization.combat_discipline_id)
                        }
                        _ => unreachable!(),
                    };
                    active_scopes.entry(scope).or_default().push(input_index);
                }
            }
        }

        for (specialization_id, count) in selected_feature_counts {
            if count == 0 {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::EmptySpecialization,
                    format!("selected Specialization '{specialization_id}' has no feature"),
                ));
            }
        }
        let feature_count = technique_count + spell_count + perk_count;
        if feature_count > self.rules.global_feature_capacity {
            return Err(CombatBuildV2ValidationError::new(
                CombatBuildV2ErrorCode::FeatureCapacity,
                format!(
                    "selected feature count {feature_count} exceeds {}",
                    self.rules.global_feature_capacity
                ),
            ));
        }

        for indices in active_scopes.values_mut() {
            indices.sort_by_key(|index| {
                (
                    selected_features[*index]
                        .preferred_bar_order
                        .map(usize::from)
                        .unwrap_or(usize::MAX),
                    *index,
                )
            });
            for (normalized_order, index) in indices.iter().enumerate() {
                selected_features[*index].preferred_bar_order = Some(normalized_order as u8);
            }
        }

        let mut trait_ids = HashSet::new();
        for trait_id in &draft.selected_traits {
            if !self.traits.contains_key(trait_id) {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::UnknownTrait,
                    format!("unknown Trait '{trait_id}'"),
                ));
            }
            if !trait_ids.insert(trait_id.as_str()) {
                return Err(CombatBuildV2ValidationError::new(
                    CombatBuildV2ErrorCode::DuplicateTrait,
                    format!("Trait '{trait_id}' is selected more than once"),
                ));
            }
        }
        if draft.selected_traits.len() > self.rules.trait_capacity {
            return Err(CombatBuildV2ValidationError::new(
                CombatBuildV2ErrorCode::TraitCapacity,
                format!(
                    "selected Trait count {} exceeds {}",
                    draft.selected_traits.len(),
                    self.rules.trait_capacity
                ),
            ));
        }

        let starting_discipline_id = draft
            .starting_discipline_id
            .clone()
            .unwrap_or_else(|| parent_discipline_ids[0].clone());
        let projection = self.build_projection(
            &selected_features,
            &selected_ids,
            &parent_discipline_ids,
            &draft.selected_traits,
        );

        let mut dormant_specializations = draft.dormant_specializations.clone();
        dormant_specializations.sort();
        let parent_rank: HashMap<_, _> = parent_discipline_ids
            .iter()
            .enumerate()
            .map(|(index, id)| (id.as_str(), index))
            .collect();
        let mut discipline_configurations = draft.discipline_configurations.clone();
        discipline_configurations.sort_by(|left, right| {
            let left_rank = parent_rank
                .get(left.combat_discipline_id.as_str())
                .copied()
                .unwrap_or(usize::MAX);
            let right_rank = parent_rank
                .get(right.combat_discipline_id.as_str())
                .copied()
                .unwrap_or(usize::MAX);
            (left_rank, left.combat_discipline_id.as_str())
                .cmp(&(right_rank, right.combat_discipline_id.as_str()))
        });
        selected_features.sort_by(|left, right| {
            (left.specialization_id.as_str(), left.ability_id.as_str())
                .cmp(&(right.specialization_id.as_str(), right.ability_id.as_str()))
        });
        let mut selected_traits = draft.selected_traits.clone();
        selected_traits.sort_by_key(|trait_id| {
            let row = &self.traits[trait_id];
            (row.sort_order, row.ability_id.as_str())
        });

        Ok(ValidatedCombatBuildV2 {
            snapshot: CombatBuildV2Snapshot {
                schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
                revision: draft.revision,
                starting_discipline_id,
                selected_specializations: draft.selected_specializations.clone(),
                dormant_specializations,
                discipline_configurations,
                selected_features,
                selected_traits,
            },
            projection,
            technique_count,
            spell_count,
            perk_count,
            trait_count: draft.selected_traits.len(),
        })
    }

    pub(crate) fn validate_snapshot(
        &self,
        snapshot: &CombatBuildV2Snapshot,
    ) -> Result<ValidatedCombatBuildV2, CombatBuildV2ValidationError> {
        if snapshot.schema_version != COMBAT_BUILD_V2_SCHEMA_VERSION {
            return Err(CombatBuildV2ValidationError::new(
                CombatBuildV2ErrorCode::UnsupportedSchemaVersion,
                format!(
                    "snapshot schema version {} is unsupported; expected {}",
                    snapshot.schema_version, COMBAT_BUILD_V2_SCHEMA_VERSION
                ),
            ));
        }
        self.validate_draft(
            &CombatBuildV2Draft {
                schema_version: snapshot.schema_version,
                revision: snapshot.revision,
                starting_discipline_id: Some(snapshot.starting_discipline_id.clone()),
                selected_specializations: snapshot.selected_specializations.clone(),
                dormant_specializations: snapshot.dormant_specializations.clone(),
                discipline_configurations: snapshot.discipline_configurations.clone(),
                selected_features: snapshot.selected_features.clone(),
                selected_traits: snapshot.selected_traits.clone(),
            },
            snapshot.revision,
        )
    }

    fn build_projection(
        &self,
        features: &[CombatFeatureSelection],
        selected_specializations: &HashSet<&str>,
        parent_discipline_ids: &[String],
        selected_traits: &[String],
    ) -> CombatBuildV2Projection {
        let mut spells: Vec<_> = features
            .iter()
            .filter_map(|selection| {
                let feature = &self.features[&selection.ability_id];
                (selected_specializations.contains(selection.specialization_id.as_str())
                    && feature.loadout_kind == CombatFeatureLoadoutKind::Spell)
                    .then_some((
                        selection.preferred_bar_order.unwrap_or(u8::MAX),
                        selection.ability_id.clone(),
                    ))
            })
            .collect();
        spells.sort();

        let mut technique_bars = Vec::new();
        for parent_id in parent_discipline_ids {
            if parent_id == STAFF_DISCIPLINE_ID {
                continue;
            }
            let mut techniques: Vec<_> = features
                .iter()
                .filter_map(|selection| {
                    let feature = &self.features[&selection.ability_id];
                    let specialization = &self.specializations[&selection.specialization_id];
                    (selected_specializations.contains(selection.specialization_id.as_str())
                        && feature.loadout_kind == CombatFeatureLoadoutKind::Technique
                        && specialization.combat_discipline_id == *parent_id)
                        .then_some((
                            selection.preferred_bar_order.unwrap_or(u8::MAX),
                            selection.ability_id.clone(),
                        ))
                })
                .collect();
            techniques.sort();
            technique_bars.push(DerivedTechniqueBar {
                combat_discipline_id: parent_id.clone(),
                ability_ids: techniques
                    .into_iter()
                    .map(|(_, ability_id)| ability_id)
                    .collect(),
            });
        }

        let mut perks: Vec<_> = features
            .iter()
            .filter_map(|selection| {
                let feature = &self.features[&selection.ability_id];
                if selected_specializations.contains(selection.specialization_id.as_str())
                    && feature.loadout_kind == CombatFeatureLoadoutKind::Perk
                {
                    let specialization = &self.specializations[&selection.specialization_id];
                    Some((
                        specialization.sort_order,
                        feature.sort_order,
                        feature.ability_id.clone(),
                    ))
                } else {
                    None
                }
            })
            .collect();
        perks.sort();

        let mut traits = selected_traits.to_vec();
        traits.sort_by_key(|trait_id| {
            let row = &self.traits[trait_id];
            (row.sort_order, row.ability_id.as_str())
        });
        CombatBuildV2Projection {
            parent_discipline_ids: parent_discipline_ids.to_vec(),
            technique_bars,
            spell_ability_ids: spells
                .into_iter()
                .map(|(_, ability_id)| ability_id)
                .collect(),
            perk_ability_ids: perks
                .into_iter()
                .map(|(_, _, ability_id)| ability_id)
                .collect(),
            trait_ability_ids: traits,
            mastery_active: selected_traits
                .iter()
                .any(|trait_id| trait_id == MASTERY_TRAIT_ID)
                && parent_discipline_ids.len() == 1,
        }
    }

    fn validate_weapon_configuration(
        &self,
        configuration: &CombatBuildV2DisciplineConfiguration,
    ) -> Result<(), CombatBuildV2ValidationError> {
        let discipline_id = configuration.combat_discipline_id.as_str();
        let main_id = configuration.main_hand_item_def_id.as_str();
        let off_id = configuration.off_hand_item_def_id.as_str();
        let invalid = |detail| {
            CombatBuildV2ValidationError::new(CombatBuildV2ErrorCode::InvalidWeaponLoadout, detail)
        };
        let Some(main) = self.weapons.get(main_id) else {
            return Err(invalid(format!("unknown main-hand weapon '{main_id}'")));
        };
        if main.combat_discipline_id != discipline_id || main.equip_slot != "MAIN_HAND" {
            return Err(invalid(format!(
                "main-hand weapon '{main_id}' is illegal for '{discipline_id}'"
            )));
        }
        if !valid_weapon_color(main, configuration.main_hand_color_id.as_str()) {
            return Err(invalid(format!(
                "main-hand color is illegal for '{main_id}'"
            )));
        }
        match main.hand_requirement.as_str() {
            "TWO_HAND" => {
                if !off_id.is_empty() || !configuration.off_hand_color_id.is_empty() {
                    return Err(invalid(format!(
                        "two-handed weapon '{main_id}' cannot have an off hand"
                    )));
                }
            }
            "ONE_HAND" => {
                let Some(off) = self.weapons.get(off_id) else {
                    return Err(invalid(format!(
                        "one-handed weapon '{main_id}' requires a legal off hand"
                    )));
                };
                if off.combat_discipline_id != discipline_id
                    || off.equip_slot != "OFF_HAND"
                    || off.hand_requirement != "OFF_HAND"
                    || off.weapon_kind != "SHIELD"
                    || !valid_weapon_color(off, configuration.off_hand_color_id.as_str())
                {
                    return Err(invalid(format!(
                        "off-hand weapon '{off_id}' is illegal for '{discipline_id}'"
                    )));
                }
            }
            _ => {
                return Err(invalid(format!(
                    "main-hand weapon '{main_id}' has unsupported hand requirement"
                )));
            }
        }
        Ok(())
    }
}

fn valid_weapon_color(weapon: &CatalogWeapon, color_id: &str) -> bool {
    color_id.is_empty() || weapon.color_ids.contains(color_id)
}

#[cfg(test)]
mod tests {
    use super::*;

    const PHASE_0_CONTRACT_JSON: &str =
        include_str!("../../docs/combat-build-v2-phase-0-contract-2026-08-29.json");

    #[derive(Deserialize)]
    struct FixtureInventory {
        fixtures: Vec<FixtureDefinition>,
    }

    #[derive(Deserialize)]
    struct FixtureDefinition {
        fixture_id: String,
        valid: bool,
        expected_error: Option<String>,
    }

    fn catalog() -> CombatBuildV2Catalog {
        CombatBuildV2Catalog::from_shared_catalogs().expect("canonical v2 catalogs")
    }

    fn config(parent: &str) -> CombatBuildV2DisciplineConfiguration {
        let (main, off) = match parent {
            "DAGGERS" => ("TRAINING_DAGGER_PAIR", ""),
            "TWO_HANDED_SWORD" => ("TRAINING_TWO_HAND_SWORD", ""),
            "SWORD_AND_SHIELD" => ("TRAINING_ONE_HAND_SWORD", "TRAINING_SHIELD"),
            "ARCHER_BOW" => ("TRAINING_BOW", ""),
            STAFF_DISCIPLINE_ID => ("NEWBIE_STAFF_01", ""),
            _ => panic!("unknown fixture parent {parent}"),
        };
        CombatBuildV2DisciplineConfiguration {
            combat_discipline_id: parent.to_string(),
            main_hand_item_def_id: main.to_string(),
            main_hand_color_id: String::new(),
            off_hand_item_def_id: off.to_string(),
            off_hand_color_id: String::new(),
        }
    }

    fn selection(
        specialization_id: &str,
        ability_id: &str,
        order: Option<u8>,
    ) -> CombatFeatureSelection {
        CombatFeatureSelection {
            specialization_id: specialization_id.to_string(),
            ability_id: ability_id.to_string(),
            preferred_bar_order: order,
        }
    }

    fn draft_for(
        selected_specialization_ids: &[&str],
        dormant_specialization_ids: &[&str],
        features: Vec<CombatFeatureSelection>,
        traits: &[&str],
    ) -> CombatBuildV2Draft {
        let catalog = catalog();
        let mut parent_ids = Vec::new();
        for specialization_id in selected_specialization_ids
            .iter()
            .chain(dormant_specialization_ids.iter())
        {
            let parent = catalog
                .specialization_parent(specialization_id)
                .expect("fixture specialization");
            if !parent_ids.contains(&parent) {
                parent_ids.push(parent);
            }
        }
        CombatBuildV2Draft {
            schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
            revision: 7,
            starting_discipline_id: None,
            selected_specializations: selected_specialization_ids
                .iter()
                .enumerate()
                .map(
                    |(slot_index, specialization_id)| SelectedCombatSpecialization {
                        slot_index: slot_index as u8,
                        specialization_id: (*specialization_id).to_string(),
                    },
                )
                .collect(),
            dormant_specializations: dormant_specialization_ids
                .iter()
                .map(|id| (*id).to_string())
                .collect(),
            discipline_configurations: parent_ids.into_iter().map(config).collect(),
            selected_features: features,
            selected_traits: traits.iter().map(|id| (*id).to_string()).collect(),
        }
    }

    fn feature_ids(
        catalog: &CombatBuildV2Catalog,
        specialization_ids: &[&str],
        kind: CombatFeatureLoadoutKind,
        count: usize,
    ) -> Vec<CombatFeatureSelection> {
        let ranks: HashMap<_, _> = specialization_ids
            .iter()
            .enumerate()
            .map(|(index, id)| (*id, index))
            .collect();
        let mut rows: Vec<_> = catalog
            .features
            .values()
            .filter(|row| {
                row.loadout_kind == kind && ranks.contains_key(row.specialization_id.as_str())
            })
            .collect();
        rows.sort_by_key(|row| {
            (
                ranks[row.specialization_id.as_str()],
                row.sort_order,
                row.ability_id.as_str(),
            )
        });
        rows.into_iter()
            .take(count)
            .enumerate()
            .map(|(index, row)| {
                selection(
                    row.specialization_id.as_str(),
                    row.ability_id.as_str(),
                    kind.is_active().then_some(index as u8),
                )
            })
            .collect()
    }

    fn expect_error(
        catalog: &CombatBuildV2Catalog,
        draft: &CombatBuildV2Draft,
        expected: CombatBuildV2ErrorCode,
        fixture_error: &'static str,
    ) -> Result<(), &'static str> {
        let error = catalog
            .validate_draft(draft, draft.revision)
            .expect_err("invalid fixture must fail");
        assert_eq!(error.code, expected, "{}", error.detail);
        Err(fixture_error)
    }

    fn mutated_catalog_rejects(
        fixture_error: &'static str,
        mutate: impl FnOnce(&mut serde_json::Value, &mut serde_json::Value),
    ) -> Result<(), &'static str> {
        let mut v2: serde_json::Value =
            serde_json::from_str(COMBAT_BUILD_V2_CATALOG_JSON).expect("v2 source");
        let mut progression: serde_json::Value =
            serde_json::from_str(PROGRESSION_CATALOG_JSON).expect("progression source");
        mutate(&mut v2, &mut progression);
        let result = CombatBuildV2Catalog::from_json(
            serde_json::to_string(&v2).unwrap().as_str(),
            serde_json::to_string(&progression).unwrap().as_str(),
            WEAPON_APPEARANCE_CATALOG_JSON,
        );
        assert!(result.is_err(), "mutated catalog should fail closed");
        Err(fixture_error)
    }

    fn move_feature_kind(
        v2: &mut serde_json::Value,
        specialization_id: &str,
        ability_id: &str,
        from: &str,
        to: &str,
    ) {
        let specialization = v2["specializations"]
            .as_array_mut()
            .unwrap()
            .iter_mut()
            .find(|row| row["specialization_id"] == specialization_id)
            .unwrap();
        let from_rows = specialization[from].as_array_mut().unwrap();
        let index = from_rows.iter().position(|row| row == ability_id).unwrap();
        let row = from_rows.remove(index);
        specialization[to].as_array_mut().unwrap().push(row);
    }

    fn run_fixture(id: &str) -> Result<(), &'static str> {
        let catalog = catalog();
        match id {
            "VALID_SINGLE_FORM" => {
                catalog
                    .validate_draft(&catalog.default_draft(), 0)
                    .expect("default fixture");
                Ok(())
            }
            "VALID_THREE_SAME_PARENT_FORMS" => {
                let draft = draft_for(
                    &[
                        "DAGGERS_BLADEDANCER",
                        "DAGGERS_EXECUTIONER",
                        "DAGGERS_SHADOW",
                    ],
                    &[],
                    vec![
                        selection("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", Some(0)),
                        selection("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", Some(1)),
                        selection("DAGGERS_SHADOW", "DAGGER_STEALTH", Some(2)),
                    ],
                    &[],
                );
                let validated = catalog
                    .validate_draft(&draft, 7)
                    .expect("three Dagger Forms");
                assert_eq!(validated.projection.parent_discipline_ids, ["DAGGERS"]);
                assert_eq!(validated.projection.technique_bars.len(), 1);
                Ok(())
            }
            "VALID_THREE_SCHOOLS" => {
                let draft = draft_for(
                    &["BLIGHT", "MORTALITY", "RUIN"],
                    &[],
                    vec![
                        selection("BLIGHT", "SPELL_ICICLE", Some(0)),
                        selection("MORTALITY", "SPELL_VAMPIRIC_ORB", Some(1)),
                        selection("RUIN", "SPELL_FIREBALL", Some(2)),
                    ],
                    &[],
                );
                let validated = catalog.validate_draft(&draft, 7).expect("three Schools");
                assert_eq!(validated.projection.parent_discipline_ids, ["STAFF"]);
                assert!(validated.projection.technique_bars.is_empty());
                Ok(())
            }
            "VALID_MIXED_FORM_SCHOOL" => {
                let draft = draft_for(
                    &["DAGGERS_BLADEDANCER", "RUIN"],
                    &[],
                    vec![
                        selection("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", Some(0)),
                        selection("RUIN", "SPELL_FIREBALL", Some(0)),
                    ],
                    &[],
                );
                let validated = catalog.validate_draft(&draft, 7).expect("mixed build");
                assert_eq!(
                    validated.projection.parent_discipline_ids,
                    ["DAGGERS", "STAFF"]
                );
                assert_eq!(validated.projection.spell_ability_ids, ["SPELL_FIREBALL"]);
                Ok(())
            }
            "VALID_EIGHTEEN_TECHNIQUES_ONE_PARENT" => {
                let ids = ["DAGGERS_BLADEDANCER", "DAGGERS_EXECUTIONER"];
                let features = feature_ids(&catalog, &ids, CombatFeatureLoadoutKind::Technique, 18);
                assert_eq!(features.len(), 18);
                let validated = catalog
                    .validate_draft(&draft_for(&ids, &[], features, &[]), 7)
                    .expect("18 Techniques");
                assert_eq!(validated.technique_count, 18);
                assert_eq!(validated.projection.technique_bars[0].ability_ids.len(), 18);
                Ok(())
            }
            "VALID_EIGHTEEN_SPELLS" => {
                let ids = ["BLIGHT", "MORTALITY", "RUIN"];
                let mut features = Vec::new();
                for specialization_id in ids {
                    features.extend(feature_ids(
                        &catalog,
                        &[specialization_id],
                        CombatFeatureLoadoutKind::Spell,
                        1,
                    ));
                }
                for candidate in
                    feature_ids(&catalog, &ids, CombatFeatureLoadoutKind::Spell, usize::MAX)
                {
                    if features
                        .iter()
                        .any(|row| row.ability_id == candidate.ability_id)
                    {
                        continue;
                    }
                    features.push(candidate);
                    if features.len() == 18 {
                        break;
                    }
                }
                for (order, feature) in features.iter_mut().enumerate() {
                    feature.preferred_bar_order = Some(order as u8);
                }
                assert_eq!(features.len(), 18);
                let validated = catalog
                    .validate_draft(&draft_for(&ids, &[], features, &[]), 7)
                    .expect("18 Spells");
                assert_eq!(validated.spell_count, 18);
                assert_eq!(validated.projection.spell_ability_ids.len(), 18);
                Ok(())
            }
            "VALID_DORMANT_ORDER_REFLOW" => {
                let dormant = draft_for(
                    &["DAGGERS_BLADEDANCER"],
                    &["DAGGERS_EXECUTIONER"],
                    vec![
                        selection("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", Some(0)),
                        selection("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", Some(0)),
                    ],
                    &[],
                );
                let dormant_validated = catalog.validate_draft(&dormant, 7).expect("dormant build");
                let dormant_row = dormant_validated
                    .snapshot
                    .selected_features
                    .iter()
                    .find(|row| row.ability_id == "DAGGER_GUT_RIPPER")
                    .unwrap();
                assert_eq!(dormant_row.preferred_bar_order, Some(0));

                let returning = draft_for(
                    &["DAGGERS_BLADEDANCER", "DAGGERS_EXECUTIONER"],
                    &[],
                    dormant.selected_features,
                    &[],
                );
                let returned = catalog
                    .validate_draft(&returning, 7)
                    .expect("returning Form");
                assert_eq!(
                    returned.projection.technique_bars[0].ability_ids,
                    ["DAGGER_QUICK_CUT", "DAGGER_GUT_RIPPER"]
                );
                Ok(())
            }
            "VALID_MASTERY_ONE_PARENT_THREE_FORMS" => {
                let draft = draft_for(
                    &[
                        "DAGGERS_BLADEDANCER",
                        "DAGGERS_EXECUTIONER",
                        "DAGGERS_SHADOW",
                    ],
                    &[],
                    vec![
                        selection("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", Some(0)),
                        selection("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", Some(1)),
                        selection("DAGGERS_SHADOW", "DAGGER_STEALTH", Some(2)),
                    ],
                    &[MASTERY_TRAIT_ID],
                );
                let validated = catalog.validate_draft(&draft, 7).expect("Mastery build");
                assert!(validated.projection.mastery_active);
                assert_eq!(catalog.mastery_modifier_scalar(), 0.10);
                Ok(())
            }
            "INVALID_SCHEMA_VERSION" => {
                let mut draft = catalog.default_draft();
                draft.schema_version = 1;
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::UnsupportedSchemaVersion,
                    "SCHEMA_VERSION",
                )
            }
            "INVALID_ZERO_SPECIALIZATIONS" => {
                let mut draft = catalog.default_draft();
                draft.selected_specializations.clear();
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::SpecializationCount,
                    "SPECIALIZATION_COUNT",
                )
            }
            "INVALID_FOUR_SPECIALIZATIONS" => {
                let mut draft = draft_for(
                    &[
                        "DAGGERS_BLADEDANCER",
                        "DAGGERS_EXECUTIONER",
                        "DAGGERS_SHADOW",
                    ],
                    &[],
                    vec![
                        selection("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", Some(0)),
                        selection("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", Some(1)),
                        selection("DAGGERS_SHADOW", "DAGGER_STEALTH", Some(2)),
                    ],
                    &[],
                );
                draft
                    .selected_specializations
                    .push(SelectedCombatSpecialization {
                        slot_index: 3,
                        specialization_id: "ARCHER_BOW_MARKSMAN".to_string(),
                    });
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::SpecializationCount,
                    "SPECIALIZATION_COUNT",
                )
            }
            "INVALID_NONCONTIGUOUS_SPECIALIZATION_SLOTS" => {
                let mut draft = catalog.default_draft();
                draft.selected_specializations[0].slot_index = 1;
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::SpecializationSlotOrder,
                    "SPECIALIZATION_SLOTS",
                )
            }
            "INVALID_DUPLICATE_SPECIALIZATION" => {
                let mut draft = catalog.default_draft();
                draft
                    .selected_specializations
                    .push(SelectedCombatSpecialization {
                        slot_index: 1,
                        specialization_id: "DAGGERS_BLADEDANCER".to_string(),
                    });
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::DuplicateSpecialization,
                    "DUPLICATE_SPECIALIZATION",
                )
            }
            "INVALID_UNKNOWN_SPECIALIZATION" => {
                let mut draft = catalog.default_draft();
                draft.selected_specializations[0].specialization_id = "UNKNOWN_FORM".to_string();
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::UnknownSpecialization,
                    "UNKNOWN_SPECIALIZATION",
                )
            }
            "INVALID_STARTING_DISCIPLINE" => {
                let mut draft = catalog.default_draft();
                draft.starting_discipline_id = Some("STAFF".to_string());
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::StartingDisciplineNotSelected,
                    "STARTING_DISCIPLINE",
                )
            }
            "INVALID_MISSING_DISCIPLINE_CONFIGURATION" => {
                let mut draft = catalog.default_draft();
                draft.discipline_configurations.clear();
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::MissingDisciplineConfiguration,
                    "MISSING_CONFIGURATION",
                )
            }
            "INVALID_WEAPON_CONFIGURATION" => {
                let mut draft = catalog.default_draft();
                draft.discipline_configurations[0].main_hand_item_def_id =
                    "NEWBIE_STAFF_01".to_string();
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::InvalidWeaponLoadout,
                    "WEAPON_CONFIGURATION",
                )
            }
            "INVALID_EMPTY_SPECIALIZATION" => {
                let mut draft = catalog.default_draft();
                draft.selected_features.clear();
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::EmptySpecialization,
                    "EMPTY_SPECIALIZATION",
                )
            }
            "INVALID_FEATURE_OWNER" => {
                let mut draft = draft_for(
                    &["DAGGERS_BLADEDANCER", "DAGGERS_EXECUTIONER"],
                    &[],
                    vec![
                        selection("DAGGERS_EXECUTIONER", "DAGGER_QUICK_CUT", Some(0)),
                        selection("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", Some(1)),
                    ],
                    &[],
                );
                draft.selected_features[0].specialization_id = "DAGGERS_EXECUTIONER".to_string();
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::FeatureOwner,
                    "FEATURE_OWNER",
                )
            }
            "INVALID_FEATURE_KIND" => mutated_catalog_rejects("FEATURE_KIND", |v2, _| {
                move_feature_kind(
                    v2,
                    "DAGGERS_BLADEDANCER",
                    "DAGGER_QUICK_CUT",
                    "technique_ability_ids",
                    "perk_ability_ids",
                );
            }),
            "INVALID_STAFF_TECHNIQUE" => mutated_catalog_rejects("STAFF_TECHNIQUE", |v2, _| {
                move_feature_kind(
                    v2,
                    "RUIN",
                    "SPELL_FIREBALL",
                    "spell_ability_ids",
                    "technique_ability_ids",
                );
            }),
            "INVALID_NINETEEN_FEATURES" => {
                let ids = ["DAGGERS_BLADEDANCER", "DAGGERS_EXECUTIONER"];
                let features = feature_ids(&catalog, &ids, CombatFeatureLoadoutKind::Technique, 19);
                assert_eq!(features.len(), 19);
                expect_error(
                    &catalog,
                    &draft_for(&ids, &[], features, &[]),
                    CombatBuildV2ErrorCode::FeatureCapacity,
                    "FEATURE_CAPACITY",
                )
            }
            "INVALID_FOUR_TRAITS" => {
                let mut expanded = catalog.clone();
                for index in 1..=3 {
                    let ability_id = format!("TEST_TRAIT_{index}");
                    expanded.traits.insert(
                        ability_id.clone(),
                        CatalogTrait {
                            display_name: ability_id.clone(),
                            ability_id,
                            sort_order: 10 + index,
                            modifier_scalar: 0.0,
                        },
                    );
                }
                let mut draft = expanded.default_draft();
                draft.selected_traits = vec![
                    MASTERY_TRAIT_ID.to_string(),
                    "TEST_TRAIT_1".to_string(),
                    "TEST_TRAIT_2".to_string(),
                    "TEST_TRAIT_3".to_string(),
                ];
                expect_error(
                    &expanded,
                    &draft,
                    CombatBuildV2ErrorCode::TraitCapacity,
                    "TRAIT_CAPACITY",
                )
            }
            "INVALID_TRAIT_SATISFIES_NONEMPTY" => {
                let mut draft = catalog.default_draft();
                draft.selected_features.clear();
                draft.selected_traits.push(MASTERY_TRAIT_ID.to_string());
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::EmptySpecialization,
                    "EMPTY_SPECIALIZATION",
                )
            }
            "INVALID_PERK_BAR_ORDER" => {
                let draft = draft_for(
                    &["DAGGERS_BLADEDANCER"],
                    &[],
                    vec![selection(
                        "DAGGERS_BLADEDANCER",
                        "SUBTLETY_FLEET_FOOTED",
                        Some(0),
                    )],
                    &[],
                );
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::PassiveBarOrder,
                    "PASSIVE_BAR_ORDER",
                )
            }
            "INVALID_TRAIT_BAR_ORDER" => {
                let mut value = serde_json::to_value(catalog.default_draft()).unwrap();
                value["selected_traits"] = serde_json::json!([
                    {"ability_id": "MASTERY", "preferred_bar_order": 0}
                ]);
                assert!(serde_json::from_value::<CombatBuildV2Draft>(value).is_err());
                Err("PASSIVE_BAR_ORDER")
            }
            "INVALID_DUPLICATE_FEATURE" => {
                let mut draft = catalog.default_draft();
                draft
                    .selected_features
                    .push(draft.selected_features[0].clone());
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::DuplicateFeature,
                    "DUPLICATE_FEATURE",
                )
            }
            "INVALID_SCHOOL_PARENT" => mutated_catalog_rejects("SPECIALIZATION_PARENT", |v2, _| {
                let row = v2["specializations"]
                    .as_array_mut()
                    .unwrap()
                    .iter_mut()
                    .find(|row| row["specialization_id"] == "RUIN")
                    .unwrap();
                row["combat_discipline_id"] = "DAGGERS".into();
                row["sort_order"] = 99.into();
            }),
            "INVALID_FORM_PARENT_STAFF" => {
                mutated_catalog_rejects("SPECIALIZATION_PARENT", |v2, _| {
                    let row = v2["specializations"]
                        .as_array_mut()
                        .unwrap()
                        .iter_mut()
                        .find(|row| row["specialization_id"] == "DAGGERS_BLADEDANCER")
                        .unwrap();
                    row["combat_discipline_id"] = "STAFF".into();
                    row["sort_order"] = 99.into();
                })
            }
            "INVALID_WEAPON_BOUND_SPELL_EXECUTOR" => {
                mutated_catalog_rejects("SPELL_EXECUTOR", |_, progression| {
                    let row = progression["abilities"]
                        .as_array_mut()
                        .unwrap()
                        .iter_mut()
                        .find(|row| row["ability_id"] == "SPELL_FIREBALL")
                        .unwrap();
                    row["gameplay"]["kind"] = "MELEE".into();
                })
            }
            "INVALID_DORMANT_UNKNOWN_FEATURE" => {
                let mut draft = draft_for(
                    &["DAGGERS_BLADEDANCER"],
                    &["DAGGERS_EXECUTIONER"],
                    vec![
                        selection("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", Some(0)),
                        selection("DAGGERS_EXECUTIONER", "DELETED_DORMANT_FEATURE", Some(0)),
                    ],
                    &[],
                );
                draft.selected_features[1].ability_id = "DELETED_DORMANT_FEATURE".to_string();
                expect_error(
                    &catalog,
                    &draft,
                    CombatBuildV2ErrorCode::UnknownFeature,
                    "DORMANT_CATALOG",
                )
            }
            "INVALID_ATOMIC_SAVE_DOES_NOT_MUTATE" => {
                let original = catalog.default_draft();
                let mut invalid = original.clone();
                invalid.selected_features.clear();
                let before = original.clone();
                let result = catalog.validate_draft(&invalid, invalid.revision);
                assert!(result.is_err());
                assert_eq!(
                    original, before,
                    "pure validation may not mutate accepted state"
                );
                Err("ATOMIC_REJECT")
            }
            _ => panic!("Phase 0 fixture '{id}' has no executable Phase 1 implementation"),
        }
    }

    #[test]
    fn locked_phase_0_fixture_inventory_is_executable() {
        let inventory: FixtureInventory =
            serde_json::from_str(PHASE_0_CONTRACT_JSON).expect("Phase 0 fixture inventory");
        assert_eq!(inventory.fixtures.len(), 32);
        for fixture in inventory.fixtures {
            let result = run_fixture(fixture.fixture_id.as_str());
            assert_eq!(result.is_ok(), fixture.valid, "{}", fixture.fixture_id);
            if let Err(actual_error) = result {
                assert_eq!(
                    Some(actual_error),
                    fixture.expected_error.as_deref(),
                    "{}",
                    fixture.fixture_id
                );
            }
        }
    }

    #[test]
    fn canonical_catalog_is_exhaustive_and_staff_has_no_techniques() {
        let catalog = catalog();
        assert_eq!(catalog.specializations.len(), 18);
        assert_eq!(catalog.features.len(), 208);
        assert_eq!(catalog.intrinsic_ability_ids.len(), 5);
        assert_eq!(catalog.removed_player_ability_ids.len(), 4);
        assert_eq!(catalog.traits.len(), 1);
        assert_eq!(
            catalog
                .features
                .values()
                .filter(|row| row.loadout_kind == CombatFeatureLoadoutKind::Technique)
                .count(),
            80
        );
        assert!(catalog.features.values().all(|feature| {
            if feature.loadout_kind != CombatFeatureLoadoutKind::Technique {
                return true;
            }
            catalog.specializations[&feature.specialization_id].combat_discipline_id
                != STAFF_DISCIPLINE_ID
        }));
        for removed in [
            "STAFF_STRIKE",
            "STAFF_STRIKE_2",
            "STAFF_SWEEP",
            "STAFF_THRUST",
        ] {
            assert!(catalog.is_removed_player_ability(removed));
            assert!(catalog.feature_loadout_kind(removed).is_none());
            assert!(!catalog.is_private_intrinsic(removed));
        }
    }

    #[test]
    fn snapshot_round_trip_is_versioned_and_idempotently_normalized() {
        let catalog = catalog();
        let draft = draft_for(
            &["DAGGERS_BLADEDANCER", "DAGGERS_EXECUTIONER"],
            &[],
            vec![
                selection("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", Some(8)),
                selection("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", Some(8)),
                selection("DAGGERS_BLADEDANCER", "DAGGER_SLICE", None),
            ],
            &[MASTERY_TRAIT_ID],
        );
        let first = catalog
            .validate_draft(&draft, 7)
            .expect("sparse/collision draft");
        assert_eq!(
            first.projection.technique_bars[0].ability_ids,
            ["DAGGER_QUICK_CUT", "DAGGER_GUT_RIPPER", "DAGGER_SLICE"]
        );
        let encoded = serde_json::to_string(&first.snapshot).expect("snapshot serialization");
        let decoded: CombatBuildV2Snapshot =
            serde_json::from_str(encoded.as_str()).expect("snapshot deserialization");
        let second = catalog
            .validate_snapshot(&decoded)
            .expect("snapshot validation");
        assert_eq!(first, second);

        let mut old = decoded;
        old.schema_version = 1;
        let error = catalog
            .validate_snapshot(&old)
            .expect_err("old snapshot must fail");
        assert_eq!(error.code, CombatBuildV2ErrorCode::UnsupportedSchemaVersion);
    }

    #[test]
    fn mastery_uses_distinct_parent_count_not_specialization_count() {
        let same_parent = draft_for(
            &["BLIGHT", "MORTALITY", "RUIN"],
            &[],
            vec![
                selection("BLIGHT", "SPELL_ICICLE", Some(0)),
                selection("MORTALITY", "SPELL_VAMPIRIC_ORB", Some(1)),
                selection("RUIN", "SPELL_FIREBALL", Some(2)),
            ],
            &[MASTERY_TRAIT_ID],
        );
        assert!(
            catalog()
                .validate_draft(&same_parent, 7)
                .unwrap()
                .projection
                .mastery_active
        );

        let mixed = draft_for(
            &["DAGGERS_BLADEDANCER", "RUIN"],
            &[],
            vec![
                selection("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", Some(0)),
                selection("RUIN", "SPELL_FIREBALL", Some(0)),
            ],
            &[MASTERY_TRAIT_ID],
        );
        assert!(
            !catalog()
                .validate_draft(&mixed, 7)
                .unwrap()
                .projection
                .mastery_active
        );
    }
}

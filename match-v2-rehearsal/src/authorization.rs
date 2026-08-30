use std::collections::{HashMap, HashSet};

use crate::combat_build_v2_contract::{
    CombatBuildV2MaterializationPlan, CombatFeatureLoadoutKind, STAFF_DISCIPLINE_ID,
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct NormalizedFeatureGrantV2 {
    pub specialization_id: String,
    pub combat_discipline_id: String,
    pub ability_id: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum V2AuthorizationDenial {
    NoActiveDiscipline,
    ActiveDisciplineNotSelected,
    UnselectedFeature,
    WrongWeapon,
}

impl V2AuthorizationDenial {
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::NoActiveDiscipline => "NO_ACTIVE_DISCIPLINE",
            Self::ActiveDisciplineNotSelected => "ACTIVE_DISCIPLINE_NOT_SELECTED",
            Self::UnselectedFeature => "UNSELECTED_FEATURE",
            Self::WrongWeapon => "WRONG_WEAPON",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum NormalOutgoingDamagePath {
    AutoAttack,
    Technique,
    Spell,
    OwnedPeriodic,
}

pub(crate) const REVIEWED_NORMAL_OUTGOING_DAMAGE_PATHS: [NormalOutgoingDamagePath; 4] = [
    NormalOutgoingDamagePath::AutoAttack,
    NormalOutgoingDamagePath::Technique,
    NormalOutgoingDamagePath::Spell,
    NormalOutgoingDamagePath::OwnedPeriodic,
];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum OutgoingDamageScope {
    PlayerAuthored(NormalOutgoingDamagePath),
    System,
    SelfInflictedFinal,
    CopiedFinal,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct NormalizedMatchBuildV2 {
    active_discipline_id: Option<String>,
    selected_specialization_ids: HashSet<String>,
    parent_discipline_ids: HashSet<String>,
    techniques: HashMap<String, NormalizedFeatureGrantV2>,
    spells: HashMap<String, NormalizedFeatureGrantV2>,
    perks: HashMap<String, NormalizedFeatureGrantV2>,
    traits: HashSet<String>,
    mastery_active: bool,
}

impl NormalizedMatchBuildV2 {
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn new(
        active_discipline_id: Option<String>,
        selected_specializations: Vec<(String, String)>,
        parent_discipline_ids: Vec<String>,
        techniques: Vec<NormalizedFeatureGrantV2>,
        spells: Vec<NormalizedFeatureGrantV2>,
        perks: Vec<NormalizedFeatureGrantV2>,
        traits: Vec<String>,
        stored_mastery_active: bool,
    ) -> Result<Self, String> {
        let selected_specialization_parents: HashMap<_, _> =
            selected_specializations.iter().cloned().collect();
        if selected_specialization_parents.len() != selected_specializations.len() {
            return Err(
                "COMBAT_BUILD_V2_RUNTIME_INVALID: duplicate selected Specialization".to_string(),
            );
        }
        let parent_discipline_ids: HashSet<_> = parent_discipline_ids.into_iter().collect();
        if parent_discipline_ids.is_empty()
            || selected_specializations
                .iter()
                .any(|(_, parent_id)| !parent_discipline_ids.contains(parent_id))
        {
            return Err(
                "COMBAT_BUILD_V2_RUNTIME_INVALID: selected Specialization parent mismatch"
                    .to_string(),
            );
        }

        let techniques = validate_feature_grants(
            techniques,
            &selected_specialization_parents,
            &parent_discipline_ids,
            CombatFeatureLoadoutKind::Technique,
        )?;
        if techniques
            .values()
            .any(|row| row.combat_discipline_id == STAFF_DISCIPLINE_ID)
        {
            return Err(
                "COMBAT_BUILD_V2_RUNTIME_INVALID: Staff cannot materialize a Technique".to_string(),
            );
        }
        let spells = validate_feature_grants(
            spells,
            &selected_specialization_parents,
            &parent_discipline_ids,
            CombatFeatureLoadoutKind::Spell,
        )?;
        let perks = validate_feature_grants(
            perks,
            &selected_specialization_parents,
            &parent_discipline_ids,
            CombatFeatureLoadoutKind::Perk,
        )?;
        let traits: HashSet<_> = traits.into_iter().collect();
        let computed_mastery_active =
            traits.contains("MASTERY") && parent_discipline_ids.len() == 1;
        if computed_mastery_active != stored_mastery_active {
            return Err(
                "COMBAT_BUILD_V2_RUNTIME_INVALID: stored Mastery predicate diverged".to_string(),
            );
        }

        Ok(Self {
            active_discipline_id,
            selected_specialization_ids: selected_specialization_parents.into_keys().collect(),
            parent_discipline_ids,
            techniques,
            spells,
            perks,
            traits,
            mastery_active: computed_mastery_active,
        })
    }

    pub(crate) fn from_plan(
        plan: &CombatBuildV2MaterializationPlan,
        active_discipline_id: Option<String>,
    ) -> Result<Self, String> {
        Self::new(
            active_discipline_id,
            plan.selected_specializations
                .iter()
                .map(|row| {
                    (
                        row.specialization_id.clone(),
                        row.combat_discipline_id.clone(),
                    )
                })
                .collect(),
            plan.parent_discipline_ids.clone(),
            plan.techniques.iter().map(feature_grant).collect(),
            plan.spells.iter().map(feature_grant).collect(),
            plan.perks.iter().map(feature_grant).collect(),
            plan.traits.clone(),
            plan.mastery_active,
        )
    }

    #[cfg(test)]
    pub(crate) fn with_active_discipline(
        &self,
        combat_discipline_id: &str,
    ) -> Result<Self, V2AuthorizationDenial> {
        if !self.parent_discipline_ids.contains(combat_discipline_id) {
            return Err(V2AuthorizationDenial::ActiveDisciplineNotSelected);
        }
        let mut next = self.clone();
        next.active_discipline_id = Some(combat_discipline_id.to_string());
        Ok(next)
    }

    pub(crate) fn authorize_technique(
        &self,
        ability_id: &str,
    ) -> Result<(), V2AuthorizationDenial> {
        let active_discipline_id = self.require_selected_active_discipline()?;
        let grant = self
            .techniques
            .get(ability_id)
            .ok_or(V2AuthorizationDenial::UnselectedFeature)?;
        if grant.combat_discipline_id != active_discipline_id {
            return Err(V2AuthorizationDenial::WrongWeapon);
        }
        Ok(())
    }

    pub(crate) fn authorize_spell(&self, ability_id: &str) -> Result<(), V2AuthorizationDenial> {
        self.require_selected_active_discipline()?;
        self.spells
            .contains_key(ability_id)
            .then_some(())
            .ok_or(V2AuthorizationDenial::UnselectedFeature)
    }

    pub(crate) fn build_contains_selected_active(&self, ability_id: &str) -> bool {
        self.techniques.contains_key(ability_id) || self.spells.contains_key(ability_id)
    }

    pub(crate) fn perk_is_active(&self, ability_id: &str) -> bool {
        self.perks.get(ability_id).is_some_and(|grant| {
            self.selected_specialization_ids
                .contains(grant.specialization_id.as_str())
                && self
                    .parent_discipline_ids
                    .contains(grant.combat_discipline_id.as_str())
        })
    }

    pub(crate) fn trait_is_selected(&self, ability_id: &str) -> bool {
        self.traits.contains(ability_id)
    }

    pub(crate) fn mastery_is_active(&self) -> bool {
        self.mastery_active && self.trait_is_selected("MASTERY")
    }

    pub(crate) fn mastery_outgoing_damage_multiplier(
        &self,
        scope: OutgoingDamageScope,
        modifier_scalar: f32,
    ) -> f32 {
        if self.mastery_is_active()
            && matches!(scope, OutgoingDamageScope::PlayerAuthored(_))
            && modifier_scalar.is_finite()
            && modifier_scalar > 0.0
        {
            1.0 + modifier_scalar
        } else {
            1.0
        }
    }

    pub(crate) fn apply_mastery_outgoing_damage(
        &self,
        base_damage: i32,
        scope: OutgoingDamageScope,
        modifier_scalar: f32,
    ) -> i32 {
        if base_damage <= 0 {
            return base_damage.max(0);
        }
        ((base_damage as f32) * self.mastery_outgoing_damage_multiplier(scope, modifier_scalar))
            .round()
            .max(0.0) as i32
    }

    fn require_selected_active_discipline(&self) -> Result<&str, V2AuthorizationDenial> {
        let active = self
            .active_discipline_id
            .as_deref()
            .ok_or(V2AuthorizationDenial::NoActiveDiscipline)?;
        if !self.parent_discipline_ids.contains(active) {
            return Err(V2AuthorizationDenial::ActiveDisciplineNotSelected);
        }
        Ok(active)
    }
}

fn feature_grant(
    row: &crate::combat_build_v2_contract::MaterializedCombatFeatureV2,
) -> NormalizedFeatureGrantV2 {
    NormalizedFeatureGrantV2 {
        specialization_id: row.specialization_id.clone(),
        combat_discipline_id: row.combat_discipline_id.clone(),
        ability_id: row.ability_id.clone(),
    }
}

fn validate_feature_grants(
    rows: Vec<NormalizedFeatureGrantV2>,
    selected_specialization_parents: &HashMap<String, String>,
    parent_discipline_ids: &HashSet<String>,
    expected_kind: CombatFeatureLoadoutKind,
) -> Result<HashMap<String, NormalizedFeatureGrantV2>, String> {
    let mut grants = HashMap::new();
    for row in rows {
        if selected_specialization_parents
            .get(row.specialization_id.as_str())
            .is_none_or(|parent_id| parent_id != &row.combat_discipline_id)
            || !parent_discipline_ids.contains(row.combat_discipline_id.as_str())
        {
            return Err(format!(
                "COMBAT_BUILD_V2_RUNTIME_INVALID: {} '{}' has an unselected source",
                expected_kind.as_str(),
                row.ability_id
            ));
        }
        let ability_id = row.ability_id.clone();
        if grants.insert(ability_id.clone(), row).is_some() {
            return Err(format!(
                "COMBAT_BUILD_V2_RUNTIME_INVALID: duplicate {} '{ability_id}'",
                expected_kind.as_str()
            ));
        }
    }
    Ok(grants)
}

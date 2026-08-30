use std::collections::{HashMap, HashSet};

use crate::combat_build_v2_contract::{CombatBuildV2MaterializationPlan, STAFF_DISCIPLINE_ID};

/// Every accepted action family that must interrupt an active cast in Combat
/// Build v2. Rejected input never reaches this policy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum AcceptedInterruptV2 {
    Movement,
    Jump,
    DisciplineSwitch,
    Technique,
    Spell,
    Dodge,
    Block,
    Parry,
    Interact,
    FixedCombatAction,
    AutoAttackStart,
    Stagger,
    Knockback,
    Death,
}

impl AcceptedInterruptV2 {
    pub(crate) const ALL: [Self; 14] = [
        Self::Movement,
        Self::Jump,
        Self::DisciplineSwitch,
        Self::Technique,
        Self::Spell,
        Self::Dodge,
        Self::Block,
        Self::Parry,
        Self::Interact,
        Self::FixedCombatAction,
        Self::AutoAttackStart,
        Self::Stagger,
        Self::Knockback,
        Self::Death,
    ];

    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::Movement => "MOVEMENT",
            Self::Jump => "JUMP",
            Self::DisciplineSwitch => "DISCIPLINE_SWITCH",
            Self::Technique => "TECHNIQUE",
            Self::Spell => "SPELL",
            Self::Dodge => "DODGE",
            Self::Block => "BLOCK",
            Self::Parry => "PARRY",
            Self::Interact => "INTERACT",
            Self::FixedCombatAction => "FIXED_COMBAT_ACTION",
            Self::AutoAttackStart => "AUTO_ATTACK_START",
            Self::Stagger => "STAGGER",
            Self::Knockback => "KNOCKBACK",
            Self::Death => "DEATH",
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct ActiveCastPresentationV2 {
    action_id: String,
    hold_active: bool,
    temporary_prop_active: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct CastCancelOutcomeV2 {
    pub canceled_action_id: Option<String>,
    pub authoritative_fizzle_emitted: bool,
    pub client_cancel_phase_emitted: bool,
    pub hold_cleared: bool,
    pub temporary_prop_cleared: bool,
}

impl CastCancelOutcomeV2 {
    fn no_active_cast() -> Self {
        Self {
            canceled_action_id: None,
            authoritative_fizzle_emitted: false,
            client_cancel_phase_emitted: false,
            hold_cleared: true,
            temporary_prop_cleared: true,
        }
    }

    pub(crate) fn fully_canceled(&self) -> bool {
        self.canceled_action_id.is_some()
            && self.authoritative_fizzle_emitted
            && self.client_cancel_phase_emitted
            && self.hold_cleared
            && self.temporary_prop_cleared
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct DisciplineSwitchOutcomeV2 {
    pub switched: bool,
    pub cast_cancel: CastCancelOutcomeV2,
}

/// Transport-neutral rehearsal of the state reset order required at cutover.
/// It intentionally models contracts, not canonical v1 gameplay tables.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct SwitchRuntimeV2 {
    switch_targets: Vec<String>,
    active_discipline_id: String,
    techniques_by_discipline: HashMap<String, Vec<String>>,
    spell_bar: Vec<String>,
    active_cast: Option<ActiveCastPresentationV2>,
    auto_attack_armed: bool,
    auto_attack_timing_epoch: u64,
    combo_sequence_index: u32,
    potential_state_active: bool,
    weapon_transient_active: bool,
}

impl SwitchRuntimeV2 {
    pub(crate) fn from_plan(plan: &CombatBuildV2MaterializationPlan) -> Result<Self, String> {
        let mut seen = HashSet::new();
        let switch_targets: Vec<_> = plan
            .selected_specializations
            .iter()
            .filter_map(|selected| {
                seen.insert(selected.combat_discipline_id.as_str())
                    .then(|| selected.combat_discipline_id.clone())
            })
            .collect();
        if switch_targets.is_empty() {
            return Err(
                "COMBAT_BUILD_V2_SWITCH_INVALID: no selected parent Discipline".to_string(),
            );
        }
        if !switch_targets
            .iter()
            .any(|id| id == &plan.starting_discipline_id)
        {
            return Err(
                "COMBAT_BUILD_V2_SWITCH_INVALID: starting Discipline is not selected".to_string(),
            );
        }

        let mut techniques_by_discipline: HashMap<String, Vec<(u8, String)>> = HashMap::new();
        for feature in &plan.techniques {
            if feature.combat_discipline_id == STAFF_DISCIPLINE_ID {
                return Err(
                    "COMBAT_BUILD_V2_SWITCH_INVALID: Staff cannot expose a Technique".to_string(),
                );
            }
            techniques_by_discipline
                .entry(feature.combat_discipline_id.clone())
                .or_default()
                .push((
                    feature
                        .bar_order
                        .expect("materialized Technique must have a bar order"),
                    feature.ability_id.clone(),
                ));
        }
        let techniques_by_discipline = techniques_by_discipline
            .into_iter()
            .map(|(discipline_id, mut rows)| {
                rows.sort_by(|left, right| left.cmp(right));
                (
                    discipline_id,
                    rows.into_iter().map(|(_, ability_id)| ability_id).collect(),
                )
            })
            .collect();

        let mut spells: Vec<_> = plan
            .spells
            .iter()
            .map(|feature| {
                (
                    feature
                        .bar_order
                        .expect("materialized Spell must have a bar order"),
                    feature.ability_id.clone(),
                )
            })
            .collect();
        spells.sort_by(|left, right| left.cmp(right));

        Ok(Self {
            switch_targets,
            active_discipline_id: plan.starting_discipline_id.clone(),
            techniques_by_discipline,
            spell_bar: spells
                .into_iter()
                .map(|(_, ability_id)| ability_id)
                .collect(),
            active_cast: None,
            auto_attack_armed: false,
            auto_attack_timing_epoch: 0,
            combo_sequence_index: 0,
            potential_state_active: false,
            weapon_transient_active: false,
        })
    }

    pub(crate) fn switch_targets(&self) -> &[String] {
        self.switch_targets.as_slice()
    }

    pub(crate) fn active_discipline_id(&self) -> &str {
        self.active_discipline_id.as_str()
    }

    pub(crate) fn spell_bar(&self) -> &[String] {
        self.spell_bar.as_slice()
    }

    pub(crate) fn visible_technique_bar(&self) -> &[String] {
        if self.active_discipline_id == STAFF_DISCIPLINE_ID {
            return &[];
        }
        self.techniques_by_discipline
            .get(self.active_discipline_id.as_str())
            .map(Vec::as_slice)
            .unwrap_or(&[])
    }

    pub(crate) fn ordinary_auto_attack_available(&self) -> bool {
        self.switch_targets
            .iter()
            .any(|id| id == &self.active_discipline_id)
    }

    pub(crate) fn arm_transient_weapon_state_for_probe(&mut self) {
        self.auto_attack_armed = true;
        self.combo_sequence_index = 2;
        self.potential_state_active = true;
        self.weapon_transient_active = true;
    }

    pub(crate) fn begin_cast_for_probe(&mut self, action_id: &str, temporary_prop: bool) {
        self.active_cast = Some(ActiveCastPresentationV2 {
            action_id: action_id.to_string(),
            hold_active: true,
            temporary_prop_active: temporary_prop,
        });
    }

    pub(crate) fn switch_discipline(
        &mut self,
        target_discipline_id: &str,
    ) -> Result<DisciplineSwitchOutcomeV2, String> {
        if !self
            .switch_targets
            .iter()
            .any(|id| id == target_discipline_id)
        {
            return Err(format!(
                "COMBAT_BUILD_V2_SWITCH_DENIED: Discipline '{target_discipline_id}' is not selected"
            ));
        }
        if self.active_discipline_id == target_discipline_id {
            return Ok(DisciplineSwitchOutcomeV2 {
                switched: false,
                cast_cancel: CastCancelOutcomeV2::no_active_cast(),
            });
        }

        // Preserve the required order: cancel/fizzle first, then replace the
        // equipped configuration and clear weapon-owned transient state.
        let cast_cancel = self.interrupt_active_cast(AcceptedInterruptV2::DisciplineSwitch);
        self.active_discipline_id = target_discipline_id.to_string();
        self.auto_attack_armed = false;
        self.auto_attack_timing_epoch = self.auto_attack_timing_epoch.saturating_add(1);
        self.combo_sequence_index = 0;
        self.potential_state_active = false;
        self.weapon_transient_active = false;

        Ok(DisciplineSwitchOutcomeV2 {
            switched: true,
            cast_cancel,
        })
    }

    pub(crate) fn interrupt_active_cast(
        &mut self,
        _source: AcceptedInterruptV2,
    ) -> CastCancelOutcomeV2 {
        let Some(active_cast) = self.active_cast.take() else {
            return CastCancelOutcomeV2::no_active_cast();
        };
        CastCancelOutcomeV2 {
            canceled_action_id: Some(active_cast.action_id),
            authoritative_fizzle_emitted: true,
            client_cancel_phase_emitted: true,
            hold_cleared: active_cast.hold_active,
            temporary_prop_cleared: true,
        }
    }

    pub(crate) fn weapon_transients_are_clear(&self) -> bool {
        !self.auto_attack_armed
            && self.combo_sequence_index == 0
            && !self.potential_state_active
            && !self.weapon_transient_active
    }

    pub(crate) fn auto_attack_timing_epoch(&self) -> u64 {
        self.auto_attack_timing_epoch
    }

    pub(crate) fn has_active_cast_or_presentation(&self) -> bool {
        self.active_cast.is_some()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{mixed_switching_draft, CombatBuildV2Catalog};

    fn runtime() -> SwitchRuntimeV2 {
        let catalog = CombatBuildV2Catalog::from_shared_catalogs().unwrap();
        let draft = mixed_switching_draft();
        let validated = catalog.validate_draft(&draft, draft.revision).unwrap();
        let plan = catalog.materialization_plan(&validated).unwrap();
        SwitchRuntimeV2::from_plan(&plan).unwrap()
    }

    #[test]
    fn repeated_forms_derive_one_switch_target_and_one_merged_bar() {
        let runtime = runtime();
        assert_eq!(runtime.switch_targets(), ["DAGGERS", "STAFF"]);
        assert_eq!(
            runtime.visible_technique_bar(),
            ["DAGGER_QUICK_CUT", "DAGGER_GUT_RIPPER"]
        );
        assert_eq!(runtime.spell_bar(), ["SPELL_FIREBALL"]);
    }

    #[test]
    fn switch_cancels_before_reset_and_staff_retains_only_ordinary_auto_attack() {
        let mut runtime = runtime();
        let spell_bar = runtime.spell_bar().to_vec();
        runtime.arm_transient_weapon_state_for_probe();
        runtime.begin_cast_for_probe("SPELL_FIREBALL", false);
        let outcome = runtime.switch_discipline("STAFF").unwrap();
        assert!(outcome.switched);
        assert!(outcome.cast_cancel.fully_canceled());
        assert!(runtime.weapon_transients_are_clear());
        assert_eq!(runtime.auto_attack_timing_epoch(), 1);
        assert!(runtime.visible_technique_bar().is_empty());
        assert_eq!(runtime.spell_bar(), spell_bar);
        assert!(runtime.ordinary_auto_attack_available());
    }

    #[test]
    fn every_accepted_action_clears_hold_and_temporary_prop() {
        for source in AcceptedInterruptV2::ALL {
            let mut runtime = runtime();
            runtime.begin_cast_for_probe("PALADIN_BLESSED_SHIELD", true);
            let outcome = runtime.interrupt_active_cast(source);
            assert!(outcome.fully_canceled(), "{}", source.as_str());
            assert!(!runtime.has_active_cast_or_presentation());
        }
    }
}

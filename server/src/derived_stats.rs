use std::time::Duration;

use spacetimedb::{Identity, ReducerContext};

use crate::combat::DEFAULT_MAX_HP;
use crate::progression::{
    active_stat_totals_for_owner, combat_rule_value, stat_scaling_value, AllocatedStatTotals,
};

const EFFECT_DAMAGE_MULTIPLIER_PER_POINT: &str = "DAMAGE_MULTIPLIER_PER_POINT";
const EFFECT_CRIT_CHANCE_PER_POINT: &str = "CRIT_CHANCE_PER_POINT";
const EFFECT_MAX_HP_PER_POINT: &str = "MAX_HP_PER_POINT";
const EFFECT_RUN_SPEED_MULTIPLIER_PER_POINT: &str = "RUN_SPEED_MULTIPLIER_PER_POINT";
const EFFECT_CAST_SPEED_MULTIPLIER_PER_POINT: &str = "CAST_SPEED_MULTIPLIER_PER_POINT";
const EFFECT_FORTIFY_TEMP_HP_MULTIPLIER_PER_POINT: &str = "FORTIFY_TEMP_HP_MULTIPLIER_PER_POINT";
const RULE_BASE_CRIT_CHANCE: &str = "BASE_CRIT_CHANCE";
const RULE_MAX_CRIT_CHANCE: &str = "MAX_CRIT_CHANCE";

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub(crate) struct DerivedCombatStats {
    pub allocated: AllocatedStatTotals,
    pub damage_multiplier: f32,
    pub crit_chance: f32,
    pub max_hp: i32,
    pub run_speed_multiplier: f32,
    pub cast_speed_multiplier: f32,
}

pub(crate) fn derived_combat_stats_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> DerivedCombatStats {
    derived_combat_stats_from_allocations(active_stat_totals_for_owner(ctx, owner))
}

pub(crate) fn derived_combat_stats_from_allocations(
    allocated: AllocatedStatTotals,
) -> DerivedCombatStats {
    let might_f32 = allocated.might as f32;
    let finesse_f32 = allocated.finesse as f32;
    let quickness_f32 = allocated.quickness as f32;
    let fortitude_f32 = allocated.fortitude as f32;
    let damage_per_might = stat_scaling_value(EFFECT_DAMAGE_MULTIPLIER_PER_POINT);
    let crit_chance_per_finesse = stat_scaling_value(EFFECT_CRIT_CHANCE_PER_POINT);
    let max_hp_per_fortitude = stat_scaling_value(EFFECT_MAX_HP_PER_POINT);
    let run_speed_per_quickness = stat_scaling_value(EFFECT_RUN_SPEED_MULTIPLIER_PER_POINT);
    let cast_speed_per_quickness = stat_scaling_value(EFFECT_CAST_SPEED_MULTIPLIER_PER_POINT);
    let base_crit_chance = combat_rule_value(RULE_BASE_CRIT_CHANCE);
    let max_crit_chance = combat_rule_value(RULE_MAX_CRIT_CHANCE).clamp(0.0, 1.0);

    DerivedCombatStats {
        allocated,
        damage_multiplier: (1.0 + might_f32 * damage_per_might).max(0.0),
        crit_chance: (base_crit_chance + finesse_f32 * crit_chance_per_finesse)
            .clamp(0.0, max_crit_chance),
        max_hp: (DEFAULT_MAX_HP + (fortitude_f32 * max_hp_per_fortitude).round() as i32).max(1),
        run_speed_multiplier: (1.0 + quickness_f32 * run_speed_per_quickness).max(1.0),
        cast_speed_multiplier: (1.0 + quickness_f32 * cast_speed_per_quickness).max(1.0),
    }
}

pub(crate) fn scale_cast_duration(duration: Duration, cast_speed_multiplier: f32) -> Duration {
    if duration <= Duration::ZERO {
        return duration;
    }

    let scaled_millis =
        (duration.as_secs_f64() * 1000.0 / cast_speed_multiplier.max(0.01) as f64).round();
    Duration::from_millis(scaled_millis.max(1.0) as u64)
}

pub(crate) fn scale_fortify_temporary_hitpoints_from_allocations(
    base_amount: i32,
    allocated: AllocatedStatTotals,
) -> i32 {
    let scalar = stat_scaling_value(EFFECT_FORTIFY_TEMP_HP_MULTIPLIER_PER_POINT);
    let multiplier = (1.0 + allocated.fortitude as f32 * scalar).max(0.0);
    ((base_amount.max(0) as f32) * multiplier).round() as i32
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::{
        derived_combat_stats_from_allocations, scale_fortify_temporary_hitpoints_from_allocations,
        DerivedCombatStats,
    };
    use crate::combat::DEFAULT_MAX_HP;
    use crate::progression::AllocatedStatTotals;

    #[test]
    fn zero_allocations_preserve_existing_baselines() {
        let derived = derived_combat_stats_from_allocations(AllocatedStatTotals::default());

        assert_eq!(
            derived,
            DerivedCombatStats {
                allocated: AllocatedStatTotals::default(),
                damage_multiplier: 1.0,
                crit_chance: 0.05,
                max_hp: DEFAULT_MAX_HP,
                run_speed_multiplier: 1.0,
                cast_speed_multiplier: 1.0,
            }
        );
    }

    #[test]
    fn all_runtime_scalars_follow_allocated_stats() {
        let derived = derived_combat_stats_from_allocations(AllocatedStatTotals {
            might: 4,
            insight: 0,
            finesse: 6,
            quickness: 5,
            fortitude: 10,
        });

        assert!((derived.damage_multiplier - 1.08).abs() < 0.0001);
        assert!((derived.crit_chance - 0.11).abs() < 0.0001);
        assert_eq!(derived.max_hp, DEFAULT_MAX_HP + 120);
        assert!((derived.run_speed_multiplier - (1.0 + 5.0 * 0.015)).abs() < 0.0001);
        assert!((derived.cast_speed_multiplier - (1.0 + 5.0 * 0.015)).abs() < 0.0001);
    }

    #[test]
    fn cast_speed_scales_duration_down_but_keeps_positive_floor() {
        let baseline = Duration::from_millis(1_200);
        let scaled = super::scale_cast_duration(baseline, 1.5);
        let floored = super::scale_cast_duration(Duration::from_millis(1), 99.0);

        assert_eq!(scaled, Duration::from_millis(800));
        assert_eq!(floored, Duration::from_millis(1));
    }

    #[test]
    fn fortify_temporary_hitpoints_scale_with_fortitude() {
        let allocated = AllocatedStatTotals {
            fortitude: 10,
            ..AllocatedStatTotals::default()
        };

        assert_eq!(
            scale_fortify_temporary_hitpoints_from_allocations(30, allocated),
            36
        );
        assert_eq!(
            scale_fortify_temporary_hitpoints_from_allocations(60, allocated),
            72
        );
    }
}

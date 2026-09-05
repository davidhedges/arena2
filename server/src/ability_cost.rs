//! Authored upfront resource-cost policy shared by gameplay and Hub metadata.

/// Spell executors prefer a positive `gameplay.resource_cost`, falling back to
/// the ability's top-level cost. Other executors use the top-level cost.
///
/// This preserves the spell catalog's precedence without applying runtime
/// modifiers or interpreting delivery-specific per-second costs.
pub(crate) fn authored_upfront_resource_cost(
    gameplay_kind: &str,
    ability_resource_cost: f32,
    gameplay_resource_cost: f32,
) -> f32 {
    if gameplay_kind.trim().eq_ignore_ascii_case("SPELL") && gameplay_resource_cost > 0.0 {
        gameplay_resource_cost
    } else {
        ability_resource_cost
    }
}

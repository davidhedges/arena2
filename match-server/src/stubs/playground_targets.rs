//! Playground target authoring is a Hub/training concern, not match runtime.

use spacetimedb::{Identity, ReducerContext};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum PlaygroundTargetKind {
    Hostile,
    Neutral,
    PartyMember,
}

pub(crate) fn despawn_all_playground_targets_for_owner(
    _ctx: &ReducerContext,
    _owner: Identity,
) -> Result<(), String> {
    Ok(())
}

pub(crate) fn playground_target_kind_for_relation(
    _ctx: &ReducerContext,
    _source: Identity,
    _target: Identity,
) -> Option<PlaygroundTargetKind> {
    None
}

pub(crate) fn is_playground_target(_ctx: &ReducerContext, _identity: Identity) -> bool {
    false
}

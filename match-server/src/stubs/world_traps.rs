//! Random-dungeon hazards are not part of arena PvP.

use spacetimedb::{Identity, ReducerContext, Timestamp};

use crate::combat::actor_snapshot::CombatActorSnapshotSet;

pub(crate) fn collect_world_trap_tick_players(_ctx: &ReducerContext) -> Vec<Identity> {
    Vec::new()
}

pub(crate) fn tick_world_traps(
    _ctx: &ReducerContext,
    _now: Timestamp,
    _snapshots: &CombatActorSnapshotSet,
    _players: &[Identity],
) {
}

pub(crate) fn expire_world_traps(_ctx: &ReducerContext, _now: Timestamp) {}

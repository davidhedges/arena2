//! PvP matches derive ally/enemy relationships from `MatchParticipant`.
//! Hub parties and invitations intentionally do not exist in disposable match
//! databases.

use spacetimedb::{Identity, ReducerContext, Timestamp};

pub(crate) fn expire_party_invites(_ctx: &ReducerContext, _now: Timestamp) {}

pub(crate) fn remove_player_from_party_state(_ctx: &ReducerContext, _identity: Identity) {}

pub(crate) fn same_party(_ctx: &ReducerContext, a: Identity, b: Identity) -> bool {
    a == b
}

//! Queued player movement commands and receive cursors.
//!
//! The authoritative movement loop consumes exactly one input tick per server
//! tick. Incoming commands are therefore buffered here instead of collapsing
//! into a latest-intent row.

use spacetimedb::{table, Identity, ReducerContext, Timestamp};

#[allow(unused_imports)]
use crate::player_input::player_command as _;

/// One client-authored movement command for a single fixed input tick.
#[table(accessor = player_command)]
pub struct PlayerCommand {
    #[primary_key]
    #[auto_inc]
    pub command_id: u64,

    #[index(btree)]
    pub identity: Identity,

    #[index(btree)]
    pub input_tick: u32,

    pub forward: f32,
    pub strafe: f32,
    pub yaw: f32,
    pub jump: bool,
    pub received_at: Timestamp,
}

/// Tracks the latest movement input tick received from the client so stale or
/// duplicate commands can be ignored without consulting the queue.
#[table(accessor = player_input_cursor)]
pub struct PlayerInputCursor {
    #[primary_key]
    pub identity: Identity,
    pub latest_received_tick: u32,
}

pub fn clear_pending_player_commands(ctx: &ReducerContext, identity: Identity) {
    ctx.db.player_command().identity().delete(identity);
}

pub fn clear_pending_player_commands_through_tick(
    ctx: &ReducerContext,
    identity: Identity,
    latest_received_tick: u32,
) {
    clear_pending_player_commands(ctx, identity);

    let Some(mut cursor) = ctx.db.player_input_cursor().identity().find(identity) else {
        return;
    };

    if cursor.latest_received_tick >= latest_received_tick {
        return;
    }

    cursor.latest_received_tick = latest_received_tick;
    ctx.db.player_input_cursor().identity().update(cursor);
}

pub fn pop_command_for_tick(
    ctx: &ReducerContext,
    identity: Identity,
    input_tick: u32,
) -> Option<PlayerCommand> {
    let command = ctx
        .db
        .player_command()
        .identity()
        .filter(identity)
        .find(|command| command.input_tick == input_tick)?;

    ctx.db
        .player_command()
        .command_id()
        .delete(command.command_id);
    Some(command)
}

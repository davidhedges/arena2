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
#[derive(Clone)]
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

pub fn find_buffered_command_for_tick(
    ctx: &ReducerContext,
    identity: Identity,
    input_tick: u32,
) -> Option<PlayerCommand> {
    ctx.db
        .player_command()
        .identity()
        .filter(identity)
        .find(|command| command.input_tick == input_tick)
}

/// Deletes buffered commands that cannot legally be consumed from the
/// current authoritative tick and returns the latest tick that remains.
///
/// This is intentionally run on receive as well as on normal actor teardown:
/// a module upgrade may inherit an unbounded queue authored against an older
/// reducer, and merely rejecting new far-future commands would leave that
/// poisoned cursor/queue resident indefinitely.
pub fn prune_player_commands_outside_window(
    ctx: &ReducerContext,
    identity: Identity,
    last_processed_tick: u32,
    max_accepted_tick: u32,
) -> Option<u32> {
    let mut latest_remaining = None;
    let mut retained_command_by_tick = std::collections::BTreeMap::<u32, u64>::new();
    let stale_command_ids: Vec<_> = ctx
        .db
        .player_command()
        .identity()
        .filter(identity)
        .filter_map(|command| {
            if command.input_tick <= last_processed_tick || command.input_tick > max_accepted_tick {
                Some(command.command_id)
            } else if let Some(retained_command_id) =
                retained_command_by_tick.get_mut(&command.input_tick)
            {
                // Reducers are serialized and the current receive path merges
                // by tick, so duplicates cannot be created now. Keep the most
                // recently inserted row if an older module left duplicates.
                if command.command_id > *retained_command_id {
                    let stale_command_id = *retained_command_id;
                    *retained_command_id = command.command_id;
                    Some(stale_command_id)
                } else {
                    Some(command.command_id)
                }
            } else {
                retained_command_by_tick.insert(command.input_tick, command.command_id);
                latest_remaining =
                    Some(latest_remaining.map_or(command.input_tick, |latest: u32| {
                        latest.max(command.input_tick)
                    }));
                None
            }
        })
        .collect();

    for command_id in stale_command_ids {
        ctx.db.player_command().command_id().delete(command_id);
    }

    latest_remaining
}

/// Buffered-command occupancy after a tick's consume (S5): every remaining
/// row targets a future tick, so a plain per-identity count is the number of
/// ticks of input the server is holding for this player.
pub fn count_buffered_commands(ctx: &ReducerContext, identity: Identity) -> u32 {
    ctx.db.player_command().identity().filter(identity).count() as u32
}

use spacetimedb::{Identity, ReducerContext, Table};

use crate::arena::{clear_player_world, upsert_player_world};
use crate::combat::clear_player_combat_state;
use crate::inventory::clear_inventory_for_owner;
use crate::movement_actions::{
    clear_fixed_action_charge_states_for_owner, clear_movement_action_for_owner,
    ensure_dodge_charge_state,
};
use crate::player::Player;
use crate::player_input::{clear_pending_player_commands, PlayerInputCursor};
use crate::player_intent::PlayerIntent;
use crate::player_physics::PlayerPhysics;
use crate::player_state::PlayerState;
use crate::resources::clear_player_resources;

#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::player::player as _;
#[allow(unused_imports)]
use crate::player_input::player_command as _;
#[allow(unused_imports)]
use crate::player_input::player_input_cursor as _;
#[allow(unused_imports)]
use crate::player_intent::player_intent as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

pub(crate) enum ActorWorldAssignment {
    Open,
    Instance(u64),
}

pub(crate) struct ActorSpawnSpec {
    pub identity: Identity,
    pub username: String,
    pub class_id: String,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub yaw: f32,
    pub vel_x: f32,
    pub vel_y: f32,
    pub vel_z: f32,
    pub grounded: bool,
    pub last_processed_tick: u32,
    pub state: PlayerState,
    pub world: Option<ActorWorldAssignment>,
}

pub(crate) enum ActorWorldCleanup {
    DeleteOnly,
    RemoveFromCurrentInstance,
}

pub(crate) struct ActorDespawnOptions {
    pub world_cleanup: ActorWorldCleanup,
}

pub(crate) fn spawn_actor_bundle(ctx: &ReducerContext, spec: ActorSpawnSpec) -> Result<(), String> {
    if ctx.db.player().identity().find(spec.identity).is_some() {
        return Err(format!(
            "actor {} already has a player row",
            spec.identity.to_hex()
        ));
    }

    let now = ctx.timestamp;
    ctx.db.player().insert(Player {
        identity: spec.identity,
        username: spec.username,
        connected_at: now,
        class_id: spec.class_id,
    });
    ctx.db.player_intent().insert(PlayerIntent {
        identity: spec.identity,
        forward: 0.0,
        strafe: 0.0,
        yaw: spec.yaw,
        jump: false,
        input_tick: spec.last_processed_tick,
        updated_at: now,
    });
    ctx.db.player_input_cursor().insert(PlayerInputCursor {
        identity: spec.identity,
        latest_received_tick: spec.last_processed_tick,
    });
    ctx.db.player_physics().insert(PlayerPhysics {
        identity: spec.identity,
        pos_x: spec.pos_x,
        pos_y: spec.pos_y,
        pos_z: spec.pos_z,
        vel_x: spec.vel_x,
        vel_y: spec.vel_y,
        vel_z: spec.vel_z,
        yaw: spec.yaw,
        grounded: spec.grounded,
        last_processed_tick: spec.last_processed_tick,
        updated_at: now,
    });
    ctx.db.player_state().insert(spec.state);
    ensure_dodge_charge_state(ctx, spec.identity, now);
    match spec.world {
        Some(ActorWorldAssignment::Open) => {
            upsert_player_world(ctx, spec.identity, "OPEN", None);
        }
        Some(ActorWorldAssignment::Instance(instance_id)) => {
            upsert_player_world(ctx, spec.identity, "INSTANCE", Some(instance_id));
        }
        None => {}
    }

    Ok(())
}

pub(crate) fn despawn_actor_bundle(
    ctx: &ReducerContext,
    identity: Identity,
    options: ActorDespawnOptions,
) -> Result<(), String> {
    match options.world_cleanup {
        ActorWorldCleanup::DeleteOnly => {
            if ctx.db.player_world().identity().find(identity).is_some() {
                ctx.db.player_world().identity().delete(identity);
            }
        }
        ActorWorldCleanup::RemoveFromCurrentInstance => {
            clear_player_world(ctx, identity)?;
        }
    }

    clear_player_combat_state(ctx, identity);
    clear_inventory_for_owner(ctx, identity);
    clear_movement_action_for_owner(ctx, identity);
    clear_fixed_action_charge_states_for_owner(ctx, identity);
    clear_player_resources(ctx, identity);

    if ctx.db.player_physics().identity().find(identity).is_some() {
        ctx.db.player_physics().identity().delete(identity);
    }
    clear_pending_player_commands(ctx, identity);
    if ctx
        .db
        .player_input_cursor()
        .identity()
        .find(identity)
        .is_some()
    {
        ctx.db.player_input_cursor().identity().delete(identity);
    }
    if ctx.db.player_intent().identity().find(identity).is_some() {
        ctx.db.player_intent().identity().delete(identity);
    }
    if ctx.db.player_state().player_id().find(identity).is_some() {
        ctx.db.player_state().player_id().delete(identity);
    }
    if ctx.db.player().identity().find(identity).is_some() {
        ctx.db.player().identity().delete(identity);
    }

    Ok(())
}

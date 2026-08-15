//! Minimal authoritative team-match slice used by the Hub's unranked 2v2 button.
//!
//! The three bots deliberately reuse the ordinary player actor bundle. Their
//! `is_dummy` state makes the game loop settle them without accepting movement,
//! attack, cast, or respawn input. MatchParticipant is the authoritative source
//! for team relationships and team-elimination victory.

use spacetimedb::{reducer, table, Identity, ReducerContext, Table};

use crate::actor_lifecycle::{
    despawn_actor_bundle, spawn_actor_bundle, ActorDespawnOptions, ActorSpawnSpec,
    ActorWorldAssignment, ActorWorldCleanup,
};
use crate::arena::{
    create_arena_instance, create_arena_instance_with_seed_and_map,
    join_identity_into_instance_at_spawn, ArenaInstance, MATCH_PHASE_COUNTDOWN, MATCH_PHASE_ENDED,
};
use crate::combat::{
    new_dummy_player_state, snapshot_match_hp_remaining, DEFAULT_HIT_HEIGHT, DEFAULT_HIT_RADIUS,
};
use crate::match_contract::MatchReservation;
use crate::world_collision::resolve_world_spawn_position_with_layout;

#[allow(unused_imports)]
use crate::arena::arena_instance as _;
#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::bot_matches::arena_match as _;
#[allow(unused_imports)]
use crate::bot_matches::match_participant as _;
#[allow(unused_imports)]
use crate::combat::match_participant_stats as _;
#[allow(unused_imports)]
use crate::player::player as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

const QUEUE_KIND_UNRANKED: &str = "UNRANKED";
const MATCH_FORMAT_2V2: &str = "2V2";
const RULESET_TEAM_ELIMINATION: &str = "TEAM_ELIMINATION";
const TEAM_SIZE_2V2: u8 = 2;
const MATCH_PLAYER_COUNT: u32 = 4;
const BOT_MAX_HP: i32 = 1_000;

const TEAM_ZERO: u8 = 0;
const TEAM_ONE: u8 = 1;
const LEFT_SPAWN_X: f32 = -5.0;
const RIGHT_SPAWN_X: f32 = 5.0;
const LOWER_SPAWN_Z: f32 = -2.0;
const UPPER_SPAWN_Z: f32 = 2.0;
const FACE_POSITIVE_X: f32 = std::f32::consts::FRAC_PI_2;
const FACE_NEGATIVE_X: f32 = -std::f32::consts::FRAC_PI_2;

// The marker occupies the high half of the encoded u128. The low 64 bits are
// the arena id and the final two bytes are team/slot, producing stable bot ids.
const BOT_ACTOR_MAGIC: u128 = 0xb07a_2a20_0000_0000_0000_0000_0000_0000;
const BOT_ARENA_ID_MASK: u64 = 0x0000_ffff_ffff_ffff;

#[table(accessor = arena_match, public)]
pub struct ArenaMatch {
    #[primary_key]
    pub instance_id: u64,
    pub queue_kind: String,
    pub format: String,
    pub ruleset: String,
    pub team_size: u8,
    #[index(btree)]
    pub human_owner: Identity,
    /// `None` means either pending or draw; ArenaInstance.phase distinguishes it.
    pub winner_team_id: Option<u8>,
}

#[table(accessor = match_participant, public)]
pub struct MatchParticipant {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub instance_id: u64,
    pub team_id: u8,
    pub team_slot: u8,
    pub is_bot: bool,
}

#[derive(Clone, Copy)]
struct BotSlot {
    team_id: u8,
    team_slot: u8,
    username: &'static str,
    spawn_x: f32,
    spawn_z: f32,
    spawn_yaw: f32,
}

const BOT_SLOTS: [BotSlot; 3] = [
    BotSlot {
        team_id: TEAM_ZERO,
        team_slot: 1,
        username: "Ally Dummy",
        spawn_x: LEFT_SPAWN_X,
        spawn_z: UPPER_SPAWN_Z,
        spawn_yaw: FACE_POSITIVE_X,
    },
    BotSlot {
        team_id: TEAM_ONE,
        team_slot: 0,
        username: "Enemy Dummy 1",
        spawn_x: RIGHT_SPAWN_X,
        spawn_z: LOWER_SPAWN_Z,
        spawn_yaw: FACE_NEGATIVE_X,
    },
    BotSlot {
        team_id: TEAM_ONE,
        team_slot: 1,
        username: "Enemy Dummy 2",
        spawn_x: RIGHT_SPAWN_X,
        spawn_z: UPPER_SPAWN_Z,
        spawn_yaw: FACE_NEGATIVE_X,
    },
];

/// Creates the complete four-actor match in one transaction. Any failed row
/// validation or spawn returns an error and SpacetimeDB rolls the reducer back.
#[reducer]
pub fn start_unranked_2v2_bot_match(ctx: &ReducerContext) -> Result<(), String> {
    validate_fixed_roster_slots()?;
    let human = ctx.sender();
    if ctx.db.player().identity().find(human).is_none() {
        return Err("Cannot start a match before the player has spawned".to_string());
    }
    let Some(world) = ctx.db.player_world().identity().find(human) else {
        return Err("Cannot start a match without current world state".to_string());
    };
    if world.instance_id.is_some() || world.world_kind == "INSTANCE" {
        return Err("Leave the current instance before starting a match".to_string());
    }
    if ctx.db.match_participant().identity().find(human).is_some()
        || ctx
            .db
            .arena_match()
            .human_owner()
            .filter(human)
            .next()
            .is_some()
    {
        return Err("A match is already active for this player".to_string());
    }

    create_fixed_2v2_runtime(ctx, human, None, true)?;
    Ok(())
}

/// Builds the fixed roster in an otherwise empty physical match database.
/// The reserved human's actor is intentionally absent until that exact
/// identity establishes its gameplay connection.
pub(crate) fn bootstrap_provisioned_2v2(
    ctx: &ReducerContext,
    human: Identity,
    seed: u64,
    map_id: &str,
) -> Result<u64, String> {
    validate_fixed_roster_slots()?;
    if ctx.db.match_participant().identity().find(human).is_some()
        || ctx.db.arena_match().iter().next().is_some()
    {
        return Err("Provisioned match runtime already exists".to_string());
    }
    create_fixed_2v2_runtime(ctx, human, Some((seed, map_id)), false)
}

fn create_fixed_2v2_runtime(
    ctx: &ReducerContext,
    human: Identity,
    provisioned: Option<(u64, &str)>,
    human_is_present: bool,
) -> Result<u64, String> {
    let instance_id = provisioned.map_or_else(
        || create_arena_instance(ctx, MATCH_PLAYER_COUNT),
        |(seed, map_id)| {
            create_arena_instance_with_seed_and_map(ctx, MATCH_PLAYER_COUNT, seed, map_id)
        },
    );
    let Some(arena) = ctx.db.arena_instance().id().find(instance_id) else {
        return Err("Failed to create the arena instance".to_string());
    };

    ctx.db.arena_match().insert(ArenaMatch {
        instance_id,
        queue_kind: QUEUE_KIND_UNRANKED.to_string(),
        format: MATCH_FORMAT_2V2.to_string(),
        ruleset: RULESET_TEAM_ELIMINATION.to_string(),
        team_size: TEAM_SIZE_2V2,
        human_owner: human,
        winner_team_id: None,
    });
    ctx.db.match_participant().insert(MatchParticipant {
        identity: human,
        instance_id,
        team_id: TEAM_ZERO,
        team_slot: 0,
        is_bot: false,
    });
    if human_is_present {
        join_identity_into_instance_at_spawn(
            ctx,
            human,
            instance_id,
            LEFT_SPAWN_X,
            LOWER_SPAWN_Z,
            FACE_POSITIVE_X,
        )?;
    }

    for slot in BOT_SLOTS {
        spawn_bot(ctx, &arena, slot)?;
    }

    let Some(mut arena) = ctx.db.arena_instance().id().find(instance_id) else {
        return Err("Arena disappeared while its actors were spawning".to_string());
    };
    arena.player_count = if human_is_present {
        MATCH_PLAYER_COUNT
    } else {
        MATCH_PLAYER_COUNT - 1
    };
    arena.phase = if human_is_present {
        MATCH_PHASE_COUNTDOWN.to_string()
    } else {
        crate::arena::MATCH_PHASE_WAITING.to_string()
    };
    arena.winner_id = None;
    arena.ended_at = None;
    arena.countdown_started_at = human_is_present.then_some(ctx.timestamp);
    ctx.db.arena_instance().id().update(arena);

    Ok(instance_id)
}

pub(crate) fn join_provisioned_human(
    ctx: &ReducerContext,
    reservation: &MatchReservation,
) -> Result<(), String> {
    if reservation.team_id != TEAM_ZERO || reservation.team_slot != 0 {
        return Err("The 2v2 bootstrap reservation must occupy team 0 slot 0".to_string());
    }
    let participant = ctx
        .db
        .match_participant()
        .identity()
        .find(reservation.player_identity)
        .ok_or_else(|| "Reserved match participant is missing".to_string())?;
    if participant.is_bot
        || participant.team_id != reservation.team_id
        || participant.team_slot != reservation.team_slot
    {
        return Err("Reserved match participant does not match its frozen slot".to_string());
    }
    if ctx
        .db
        .player_world()
        .identity()
        .find(reservation.player_identity)
        .is_some_and(|world| world.instance_id == Some(participant.instance_id))
    {
        return Ok(());
    }

    join_identity_into_instance_at_spawn(
        ctx,
        reservation.player_identity,
        participant.instance_id,
        LEFT_SPAWN_X,
        LOWER_SPAWN_Z,
        FACE_POSITIVE_X,
    )?;
    let Some(mut arena) = ctx.db.arena_instance().id().find(participant.instance_id) else {
        return Err("Provisioned arena disappeared during player admission".to_string());
    };
    if arena.player_count != MATCH_PLAYER_COUNT {
        return Err(format!(
            "Provisioned 2v2 roster has {} active actors instead of {}",
            arena.player_count, MATCH_PLAYER_COUNT
        ));
    }
    arena.phase = MATCH_PHASE_COUNTDOWN.to_string();
    arena.winner_id = None;
    arena.ended_at = None;
    arena.countdown_started_at = Some(ctx.timestamp);
    ctx.db.arena_instance().id().update(arena);
    Ok(())
}

fn spawn_bot(ctx: &ReducerContext, arena: &ArenaInstance, slot: BotSlot) -> Result<(), String> {
    let identity = bot_identity(arena.id, slot.team_id, slot.team_slot)?;
    if ctx.db.player().identity().find(identity).is_some()
        || ctx
            .db
            .match_participant()
            .identity()
            .find(identity)
            .is_some()
    {
        return Err(format!(
            "Bot identity collision for arena {} team {} slot {}",
            arena.id, slot.team_id, slot.team_slot
        ));
    }

    let (pos_x, pos_y, pos_z) = resolve_world_spawn_position_with_layout(
        Some(arena.seed),
        false,
        slot.spawn_x,
        slot.spawn_z,
        DEFAULT_HIT_RADIUS,
        DEFAULT_HIT_HEIGHT,
    );
    spawn_actor_bundle(
        ctx,
        ActorSpawnSpec {
            identity,
            username: slot.username.to_string(),
            pos_x,
            pos_y,
            pos_z,
            yaw: slot.spawn_yaw,
            vel_x: 0.0,
            vel_y: 0.0,
            vel_z: 0.0,
            grounded: true,
            last_processed_tick: 0,
            state: new_bot_player_state(identity, ctx.timestamp),
            world: Some(ActorWorldAssignment::Instance(arena.id)),
        },
    )?;
    ctx.db.match_participant().insert(MatchParticipant {
        identity,
        instance_id: arena.id,
        team_id: slot.team_id,
        team_slot: slot.team_slot,
        is_bot: true,
    });
    Ok(())
}

fn new_bot_player_state(
    identity: Identity,
    now: spacetimedb::Timestamp,
) -> crate::player_state::PlayerState {
    new_dummy_player_state(
        identity,
        now,
        BOT_MAX_HP,
        DEFAULT_HIT_RADIUS,
        DEFAULT_HIT_HEIGHT,
    )
}

fn validate_fixed_roster_slots() -> Result<(), String> {
    let mut occupied = [[false; TEAM_SIZE_2V2 as usize]; 2];
    occupied[TEAM_ZERO as usize][0] = true;
    for slot in BOT_SLOTS {
        let team = usize::from(slot.team_id);
        let team_slot = usize::from(slot.team_slot);
        if team >= occupied.len() || team_slot >= TEAM_SIZE_2V2 as usize {
            return Err(format!(
                "Invalid fixed roster slot: team {} slot {}",
                slot.team_id, slot.team_slot
            ));
        }
        if occupied[team][team_slot] {
            return Err(format!(
                "Duplicate fixed roster slot: team {} slot {}",
                slot.team_id, slot.team_slot
            ));
        }
        occupied[team][team_slot] = true;
    }
    if occupied.iter().flatten().any(|is_occupied| !is_occupied) {
        return Err("The fixed 2v2 roster does not fill all four slots".to_string());
    }
    Ok(())
}

fn bot_identity(instance_id: u64, team_id: u8, team_slot: u8) -> Result<Identity, String> {
    let encoded = (u128::from(instance_id & BOT_ARENA_ID_MASK) << 16)
        | (u128::from(team_id) << 8)
        | u128::from(team_slot);
    let hex = format!("{:064x}", BOT_ACTOR_MAGIC | encoded);
    Identity::from_hex(hex.as_str()).map_err(|error| {
        format!(
            "invalid bot identity for arena {instance_id} team {team_id} slot {team_slot}: {error}"
        )
    })
}

/// Returns Some(true) for allies, Some(false) for opponents, and None when
/// either actor is outside this match roster.
pub(crate) fn participant_team_pair(
    ctx: &ReducerContext,
    source: Identity,
    target: Identity,
) -> Option<bool> {
    let source_row = ctx.db.match_participant().identity().find(source)?;
    let target_row = ctx.db.match_participant().identity().find(target)?;
    if source_row.instance_id != target_row.instance_id {
        return None;
    }
    Some(source_row.team_id == target_row.team_id)
}

/// Handles team victory for roster-backed matches. `true` means this was a
/// team match and the legacy free-for-all conclusion must not run.
pub(crate) fn conclude_team_match_if_needed(ctx: &ReducerContext, instance_id: u64) -> bool {
    let Some(mut arena_match) = ctx.db.arena_match().instance_id().find(instance_id) else {
        return false;
    };
    let Some(mut arena) = ctx.db.arena_instance().id().find(instance_id) else {
        return true;
    };
    if arena.phase == MATCH_PHASE_ENDED {
        return true;
    }

    let team_zero_alive = team_has_living_participant(ctx, instance_id, TEAM_ZERO);
    let team_one_alive = team_has_living_participant(ctx, instance_id, TEAM_ONE);
    let Some(winner_team_id) = resolve_team_outcome(team_zero_alive, team_one_alive) else {
        return true;
    };

    arena.phase = MATCH_PHASE_ENDED.to_string();
    arena.winner_id = None;
    arena.ended_at = Some(ctx.timestamp);
    ctx.db.arena_instance().id().update(arena);

    arena_match.winner_team_id = winner_team_id;
    ctx.db.arena_match().instance_id().update(arena_match);
    snapshot_match_hp_remaining(ctx, instance_id);
    crate::match_contract::mark_ended(ctx);
    true
}

fn team_has_living_participant(ctx: &ReducerContext, instance_id: u64, team_id: u8) -> bool {
    ctx.db
        .match_participant()
        .instance_id()
        .filter(instance_id)
        .filter(|participant| participant.team_id == team_id)
        .any(|participant| {
            ctx.db
                .player_state()
                .player_id()
                .find(participant.identity)
                .is_some_and(|state| state.alive && !state.eliminated)
        })
}

/// None: match continues. Some(None): draw. Some(Some(team)): winning team.
fn resolve_team_outcome(team_zero_alive: bool, team_one_alive: bool) -> Option<Option<u8>> {
    match (team_zero_alive, team_one_alive) {
        (true, true) => None,
        (true, false) => Some(Some(TEAM_ZERO)),
        (false, true) => Some(Some(TEAM_ONE)),
        (false, false) => Some(None),
    }
}

/// Removes the server-owned actors/config for the human's private bot match.
/// The caller's ordinary arena-leave path then removes its own actor assignment
/// and the now-empty ArenaInstance. Repeated calls are harmless.
pub(crate) fn teardown_owned_bot_match(
    ctx: &ReducerContext,
    human: Identity,
) -> Result<bool, String> {
    let Some(arena_match) = ctx.db.arena_match().human_owner().filter(human).next() else {
        return Ok(false);
    };
    let instance_id = arena_match.instance_id;
    let participants: Vec<MatchParticipant> = ctx
        .db
        .match_participant()
        .instance_id()
        .filter(instance_id)
        .collect();

    for participant in participants.iter().filter(|row| row.is_bot) {
        despawn_actor_bundle(
            ctx,
            participant.identity,
            ActorDespawnOptions {
                world_cleanup: ActorWorldCleanup::DeleteOnly,
            },
        )?;
    }
    for participant in participants {
        ctx.db
            .match_participant()
            .identity()
            .delete(participant.identity);
    }

    let stat_keys: Vec<String> = ctx
        .db
        .match_participant_stats()
        .instance_id()
        .filter(instance_id)
        .map(|row| row.key)
        .collect();
    for key in stat_keys {
        ctx.db.match_participant_stats().key().delete(key);
    }
    ctx.db.arena_match().instance_id().delete(instance_id);

    // Leave the human as the final counted actor so arena::remove_identity...
    // owns the canonical zero-count deletion in both leave and disconnect paths.
    if let Some(mut arena) = ctx.db.arena_instance().id().find(instance_id) {
        arena.player_count = 1;
        ctx.db.arena_instance().id().update(arena);
    }
    Ok(true)
}

/// Deletes all gameplay runtime rows owned by the single provisioned match.
/// Bootstrap configuration and reservation rows remain until the physical
/// database is deleted, allowing the owner to inspect terminal state while
/// preventing a second bootstrap.
pub(crate) fn teardown_provisioned_match_runtime(ctx: &ReducerContext) -> Result<(), String> {
    let participants: Vec<MatchParticipant> = ctx.db.match_participant().iter().collect();
    let instance_ids: Vec<u64> = ctx
        .db
        .arena_match()
        .iter()
        .map(|row| row.instance_id)
        .collect();

    for participant in &participants {
        if ctx
            .db
            .player()
            .identity()
            .find(participant.identity)
            .is_some()
        {
            despawn_actor_bundle(
                ctx,
                participant.identity,
                ActorDespawnOptions {
                    world_cleanup: ActorWorldCleanup::DeleteOnly,
                },
            )?;
        }
    }
    for participant in participants {
        ctx.db
            .match_participant()
            .identity()
            .delete(participant.identity);
    }
    for instance_id in instance_ids {
        let stat_keys: Vec<String> = ctx
            .db
            .match_participant_stats()
            .instance_id()
            .filter(instance_id)
            .map(|row| row.key)
            .collect();
        for key in stat_keys {
            ctx.db.match_participant_stats().key().delete(key);
        }
        ctx.db.arena_match().instance_id().delete(instance_id);
        if ctx.db.arena_instance().id().find(instance_id).is_some() {
            ctx.db.arena_instance().id().delete(instance_id);
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn team_outcome_waits_for_a_team_elimination() {
        assert_eq!(resolve_team_outcome(true, true), None);
        assert_eq!(resolve_team_outcome(true, false), Some(Some(TEAM_ZERO)));
        assert_eq!(resolve_team_outcome(false, true), Some(Some(TEAM_ONE)));
        assert_eq!(resolve_team_outcome(false, false), Some(None));
    }

    #[test]
    fn bot_identities_are_stable_and_unique_per_team_slot() {
        let ally = bot_identity(42, TEAM_ZERO, 1).unwrap();
        let enemy_zero = bot_identity(42, TEAM_ONE, 0).unwrap();
        let enemy_one = bot_identity(42, TEAM_ONE, 1).unwrap();

        assert_eq!(ally, bot_identity(42, TEAM_ZERO, 1).unwrap());
        assert_ne!(ally, enemy_zero);
        assert_ne!(enemy_zero, enemy_one);
        assert_ne!(ally, bot_identity(43, TEAM_ZERO, 1).unwrap());
    }

    #[test]
    fn fixed_roster_fills_each_team_slot_once() {
        assert_eq!(validate_fixed_roster_slots(), Ok(()));
    }

    #[test]
    fn match_bots_spawn_at_full_1000_hp() {
        let identity = bot_identity(42, TEAM_ONE, 0).unwrap();
        let state = new_bot_player_state(identity, spacetimedb::Timestamp::UNIX_EPOCH);

        assert!(state.is_dummy);
        assert_eq!(state.hp, 1_000);
        assert_eq!(state.max_hp, 1_000);
    }
}

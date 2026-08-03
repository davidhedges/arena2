//! Solo round-based survival mode built on the shared arena, NPC, combat and inventory seams.

use std::sync::OnceLock;
use std::time::Duration;

use serde::Deserialize;
use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::{
    create_arena_instance_with_kind, join_identity_into_instance, set_player_open_world,
    INSTANCE_KIND_SURVIVAL,
};
use crate::combat::apply_survival_npc_damage_multiplier;
use crate::inventory::{
    begin_survival_inventory, delete_item_aggregate, purchase_survival_shop_item,
    restore_survival_inventory, roll_survival_shop_item, MODIFIER_ARCANE_RESISTANCE,
    MODIFIER_COLD_RESISTANCE, MODIFIER_CRIT_CHANCE, MODIFIER_FIRE_RESISTANCE, MODIFIER_FORTITUDE,
    MODIFIER_HEALTH_REGEN, MODIFIER_HOLY_RESISTANCE, MODIFIER_LIGHTNING_RESISTANCE,
    MODIFIER_MOVE_SPEED, MODIFIER_PHYSICAL_DAMAGE, MODIFIER_POISON_RESISTANCE,
    MODIFIER_SHADOW_RESISTANCE,
};
use crate::npcs::{
    clear_npc_combat_runtime, clear_npc_spawn_counter_for_owner, clear_npc_target_pin,
    despawn_all_npcs_for_owner, despawn_npc_identity, interrupt_npc_actions_for_crowd_control,
    npc_template, pin_npc_target, spawn_system_npc_in_instance,
};
use crate::progression::{active_combat_mode as _, COMBAT_MODE_STEALTHED};
use crate::resources::reset_player_resources_to_full;

#[allow(unused_imports)]
use crate::arena::arena_instance as _;
#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::npcs::npc_physics as _;
#[allow(unused_imports)]
use crate::npcs::npc_state as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::survival::survival_npc as _;
#[allow(unused_imports)]
use crate::survival::survival_perception_pause as _;
#[allow(unused_imports)]
use crate::survival::survival_result as _;
#[allow(unused_imports)]
use crate::survival::survival_run as _;
#[allow(unused_imports)]
use crate::survival::survival_run_item as _;
#[allow(unused_imports)]
use crate::survival::survival_score as _;
#[allow(unused_imports)]
use crate::survival::survival_shop_offer as _;
#[allow(unused_imports)]
use crate::survival::survival_stash as _;
#[allow(unused_imports)]
use crate::survival::survival_summon_quota as _;
#[allow(unused_imports)]
use crate::survival::survival_upgrade as _;

pub(crate) const SURVIVAL_PHASE_INTERMISSION: &str = "INTERMISSION";
pub(crate) const SURVIVAL_PHASE_ACTIVE: &str = "ACTIVE";
pub(crate) const SURVIVAL_PHASE_BOSS: &str = "BOSS";
pub(crate) const SURVIVAL_ORIGIN_DIRECTOR: &str = "DIRECTOR";
#[allow(dead_code)]
pub(crate) const SURVIVAL_ORIGIN_SUMMON: &str = "SUMMON";
pub(crate) const SURVIVAL_ROLE_ADD: &str = "ADD";
pub(crate) const SURVIVAL_ROLE_BOSS: &str = "BOSS";

const SURVIVAL_ROUND_DURATION: Duration = Duration::from_secs(60);
const SURVIVAL_SPAWN_INTERVAL: Duration = Duration::from_millis(900);
const DIRECTOR_CAP: u32 = 14;
pub(crate) const TOTAL_ALIVE_CEILING: u32 = 40;
#[allow(dead_code)]
const MAX_SUMMON_DEPTH: u32 = 2;
const BOSS_ADD_BUDGET_MULTIPLIER: f32 = 0.30;
const BASE_BUDGET: f32 = 150.0;
const BUDGET_EXPONENT: f32 = 1.15;
const RATING_MIN: f32 = 54.0;
const RATING_MAX: f32 = 316.0;
const SURVIVAL_OWNER_MAGIC: u128 = 0x6172_656e_6132_5f73_7572_7669_7661_6c01;
const SURVIVAL_RATINGS_JSON: &str = include_str!("survival_ratings.shared.json");
const SURVIVAL_LAYOUT_JSON: &str = include_str!("survival_arena_layout.shared.json");
const SURVIVAL_OFFER_KIND_MODIFIER: &str = "MODIFIER";
const SURVIVAL_OFFER_KIND_ITEM: &str = "ITEM";
pub(crate) const SURVIVAL_RUN_ITEM_SOURCE_STARTER: &str = "STARTER";
const SURVIVAL_RUN_ITEM_SOURCE_SHOP: &str = "SHOP";
const SURVIVAL_MODIFIER_OFFER_COUNT: u32 = 4;
const SURVIVAL_ITEM_OFFER_COUNT: u32 = 2;

#[table(accessor = survival_run, public)]
#[derive(Clone)]
pub struct SurvivalRun {
    #[primary_key]
    pub arena_id: u64,
    #[index(btree)]
    pub owner: Identity,
    pub round: u32,
    pub phase: String,
    pub round_started_at: Timestamp,
    pub round_ends_at: Timestamp,
    pub gold: u64,
    pub gold_earned: u64,
    pub kills: u32,
    pub director_alive: u32,
    pub total_alive: u32,
    pub budget_remaining: f32,
    pub spawn_sequence: u32,
    pub next_spawn_at: Timestamp,
    pub boss_identity: Option<Identity>,
    pub seed: u64,
}

#[table(accessor = survival_npc)]
#[derive(Clone)]
pub struct SurvivalNpc {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub arena_id: u64,
    pub origin: String,
    pub role: String,
    pub summoner: Option<Identity>,
    pub summon_depth: u32,
    pub rating: f32,
    pub gold_value: u32,
    pub round: u32,
}

#[table(accessor = survival_summon_quota)]
pub struct SurvivalSummonQuota {
    #[primary_key]
    pub summoner: Identity,
    #[index(btree)]
    pub arena_id: u64,
    pub round: u32,
    pub summoned_this_round: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[allow(dead_code)]
pub(crate) struct SurvivalSummonPolicy {
    pub max_children_per_round: u32,
    pub max_depth: u32,
}

#[table(accessor = survival_perception_pause)]
pub struct SurvivalPerceptionPause {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub arena_id: u64,
    pub last_known_x: f32,
    pub last_known_y: f32,
    pub last_known_z: f32,
    pub paused_at: Timestamp,
}

pub(crate) enum SurvivalNpcPerceptionState {
    Active,
    Paused {
        last_known_x: f32,
        last_known_y: f32,
        last_known_z: f32,
        newly_paused: bool,
    },
}

#[table(accessor = survival_upgrade)]
#[derive(Clone)]
pub struct SurvivalUpgrade {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub arena_id: u64,
    pub modifier_id: String,
    pub stacks: u32,
    pub total_value: f32,
}

#[table(accessor = survival_shop_offer, public)]
#[derive(Clone)]
pub struct SurvivalShopOffer {
    #[primary_key]
    pub offer_id: String,
    #[index(btree)]
    pub arena_id: u64,
    pub round: u32,
    pub kind: String,
    pub modifier_id: String,
    pub item_instance_id: String,
    pub price: u64,
    pub purchased: bool,
}

#[table(accessor = survival_stash)]
pub struct SurvivalStash {
    #[primary_key]
    pub owner: Identity,
    #[index(btree)]
    pub arena_id: u64,
    pub equipment_json: String,
    pub items_json: String,
    pub placements_json: String,
    pub captured_at: Timestamp,
}

#[table(accessor = survival_run_item)]
pub struct SurvivalRunItem {
    #[primary_key]
    pub item_instance_id: String,
    #[index(btree)]
    pub arena_id: u64,
    pub source: String,
}

#[table(accessor = survival_score, public)]
pub struct SurvivalScore {
    #[primary_key]
    pub owner: Identity,
    pub best_round: u32,
    pub best_kills: u32,
    pub best_gold_earned: u64,
    pub runs_played: u32,
}

#[table(accessor = survival_result, public)]
pub struct SurvivalResult {
    #[primary_key]
    pub owner: Identity,
    pub round_reached: u32,
    pub kills: u32,
    pub gold_earned: u64,
    pub ended_at: Timestamp,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SurvivalRatingsDocument {
    schema_version: u32,
    roster: Vec<SurvivalRating>,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct SurvivalRating {
    template_id: String,
    rating: f32,
    gold_value: u32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SurvivalArenaLayout {
    schema_version: u32,
    arena_radius: f32,
    player_spawn: SurvivalSpawnPoint,
    edge_spawn_points: Vec<SurvivalSpawnPoint>,
    #[serde(rename = "obstacles")]
    _obstacles: Vec<serde_json::Value>,
}

#[derive(Clone, Copy, Deserialize)]
#[serde(deny_unknown_fields)]
struct SurvivalSpawnPoint {
    x: f32,
    z: f32,
    #[serde(default)]
    yaw: f32,
}

#[reducer]
pub fn start_survival_run(ctx: &ReducerContext) -> Result<(), String> {
    let owner = ctx.sender();
    if ctx.db.survival_run().owner().filter(owner).next().is_some() {
        return Err("A survival run is already active".to_string());
    }
    if ctx
        .db
        .player_world()
        .identity()
        .find(owner)
        .is_some_and(|world| world.instance_id.is_some())
    {
        return Err("Leave the current instance before starting survival".to_string());
    }
    if !ctx
        .db
        .player_state()
        .player_id()
        .find(owner)
        .is_some_and(|state| state.alive)
    {
        return Err("A living player actor is required to start survival".to_string());
    }

    ctx.db.survival_result().owner().delete(owner);
    let arena_id = create_arena_instance_with_kind(ctx, 1, INSTANCE_KIND_SURVIVAL);
    let Some(arena) = ctx.db.arena_instance().id().find(arena_id) else {
        return Err("Survival arena creation failed".to_string());
    };
    join_identity_into_instance(ctx, owner, arena_id)?;

    let now = ctx.timestamp;
    ctx.db.survival_run().insert(SurvivalRun {
        arena_id,
        owner,
        round: 0,
        phase: SURVIVAL_PHASE_INTERMISSION.to_string(),
        round_started_at: now,
        round_ends_at: now,
        gold: 0,
        gold_earned: 0,
        kills: 0,
        director_alive: 0,
        total_alive: 0,
        budget_remaining: 0.0,
        spawn_sequence: 0,
        next_spawn_at: now,
        boss_identity: None,
        seed: arena.seed,
    });
    begin_survival_inventory(ctx, owner, arena_id)?;
    let run = ctx
        .db
        .survival_run()
        .arena_id()
        .find(arena_id)
        .ok_or_else(|| "Survival run disappeared during initialization".to_string())?;
    create_survival_shop_offers(ctx, &run, 1)?;
    Ok(())
}

#[reducer]
pub fn ready_for_next_survival_round(ctx: &ReducerContext) -> Result<(), String> {
    let owner = ctx.sender();
    let Some(mut run) = ctx.db.survival_run().owner().filter(owner).next() else {
        return Err("No active survival run".to_string());
    };
    if run.phase != SURVIVAL_PHASE_INTERMISSION {
        return Err("Survival round is already active".to_string());
    }
    if run.total_alive != 0 {
        return Err("Cannot start a round while survival NPCs remain".to_string());
    }

    if run.round == 0 {
        run.round = 1;
    }
    let now = ctx.timestamp;
    let boss_round = is_boss_round(run.round);
    run.phase = if boss_round {
        SURVIVAL_PHASE_BOSS.to_string()
    } else {
        SURVIVAL_PHASE_ACTIVE.to_string()
    };
    run.round_started_at = now;
    run.round_ends_at = if boss_round {
        now
    } else {
        now + SURVIVAL_ROUND_DURATION
    };
    run.budget_remaining = if boss_round {
        boss_add_budget(run.round)
    } else {
        director_budget(run.round)
    };
    run.spawn_sequence = 0;
    run.next_spawn_at = now;
    run.boss_identity = None;
    if boss_round {
        spawn_boss_for_round(ctx, &mut run, now)?;
    }
    ctx.db.survival_run().arena_id().update(run);
    Ok(())
}

#[reducer]
pub fn dismiss_survival_result(ctx: &ReducerContext) {
    ctx.db.survival_result().owner().delete(ctx.sender());
}

#[reducer]
pub fn purchase_survival_offer(ctx: &ReducerContext, offer_id: String) -> Result<(), String> {
    let owner = ctx.sender();
    let Some(mut run) = ctx.db.survival_run().owner().filter(owner).next() else {
        return Err("No active survival run".to_string());
    };
    if run.phase != SURVIVAL_PHASE_INTERMISSION {
        return Err("Survival purchases are available only during intermission".to_string());
    }
    let Some(mut offer) = ctx.db.survival_shop_offer().offer_id().find(offer_id) else {
        return Err("Survival offer not found".to_string());
    };
    let shop_round = run.round.max(1);
    if offer.arena_id != run.arena_id || offer.round != shop_round {
        return Err("Survival offer does not belong to the current intermission".to_string());
    }
    if offer.purchased {
        return Err("Survival offer was already purchased".to_string());
    }
    if run.gold < offer.price {
        return Err("Not enough survival gold".to_string());
    }

    match offer.kind.as_str() {
        SURVIVAL_OFFER_KIND_MODIFIER => {
            purchase_survival_modifier(ctx, &run, offer.modifier_id.as_str())?;
        }
        SURVIVAL_OFFER_KIND_ITEM => {
            purchase_survival_shop_item(ctx, owner, offer.item_instance_id.as_str())?;
            ctx.db.survival_run_item().insert(SurvivalRunItem {
                item_instance_id: offer.item_instance_id.clone(),
                arena_id: run.arena_id,
                source: SURVIVAL_RUN_ITEM_SOURCE_SHOP.to_string(),
            });
        }
        _ => return Err("Survival offer has an invalid kind".to_string()),
    }

    run.gold -= offer.price;
    offer.purchased = true;
    ctx.db.survival_run().arena_id().update(run);
    ctx.db.survival_shop_offer().offer_id().update(offer);
    Ok(())
}

pub(crate) fn tick_survival(ctx: &ReducerContext, now: Timestamp) {
    let arena_ids: Vec<u64> = ctx
        .db
        .survival_run()
        .iter()
        .map(|run| run.arena_id)
        .collect();
    for arena_id in arena_ids {
        let Some(mut run) = ctx.db.survival_run().arena_id().find(arena_id) else {
            continue;
        };
        if !owner_is_in_run_arena(ctx, &run) {
            if let Err(error) = teardown_survival_for_owner(ctx, run.owner, "owner_left") {
                log::error!("[SURVIVAL] orphan teardown failed arena={arena_id}: {error}");
            }
            continue;
        }
        let is_active_round = run.phase == SURVIVAL_PHASE_ACTIVE;
        let is_boss_round = run.phase == SURVIVAL_PHASE_BOSS;
        if !is_active_round && !is_boss_round {
            continue;
        }
        if is_active_round && now >= run.round_ends_at {
            finish_active_round(ctx, run, now);
            continue;
        }

        if now >= run.next_spawn_at
            && run.director_alive < DIRECTOR_CAP
            && run.total_alive < TOTAL_ALIVE_CEILING
        {
            match spawn_next_director_npc(ctx, &mut run, now) {
                Ok(true) => {
                    ctx.db.survival_run().arena_id().update(run);
                    continue;
                }
                Ok(false) => {}
                Err(error) => {
                    log::error!(
                        "[SURVIVAL] director spawn failed arena={} round={}: {}",
                        run.arena_id,
                        run.round,
                        error
                    );
                    run.next_spawn_at = now + SURVIVAL_SPAWN_INTERVAL;
                }
            }
        }

        if is_active_round && director_is_exhausted(&run) && run.director_alive == 0 {
            finish_active_round(ctx, run, now);
        } else {
            ctx.db.survival_run().arena_id().update(run);
        }
    }
}

pub(crate) fn is_survival_npc(ctx: &ReducerContext, identity: Identity) -> bool {
    ctx.db.survival_npc().identity().find(identity).is_some()
}

pub(crate) fn on_survival_combat_mode_changed(ctx: &ReducerContext, owner: Identity) {
    let Some(run) = ctx.db.survival_run().owner().filter(owner).next() else {
        return;
    };
    let identities: Vec<Identity> = ctx
        .db
        .survival_npc()
        .arena_id()
        .filter(run.arena_id)
        .map(|row| row.identity)
        .collect();
    for identity in identities {
        let fallback = ctx
            .db
            .npc_physics()
            .identity()
            .find(identity)
            .map(|physics| (physics.pos_x, physics.pos_y, physics.pos_z))
            .unwrap_or((0.0, 0.0, 0.0));
        if matches!(
            update_survival_npc_perception(ctx, identity, fallback),
            SurvivalNpcPerceptionState::Paused { .. }
        ) {
            interrupt_npc_actions_for_crowd_control(ctx, identity, ctx.timestamp);
            clear_npc_combat_runtime(ctx, identity);
        }
    }
}

pub(crate) fn update_survival_npc_perception(
    ctx: &ReducerContext,
    identity: Identity,
    fallback_position: (f32, f32, f32),
) -> SurvivalNpcPerceptionState {
    let Some(membership) = ctx.db.survival_npc().identity().find(identity) else {
        return SurvivalNpcPerceptionState::Active;
    };
    let Some(run) = ctx.db.survival_run().arena_id().find(membership.arena_id) else {
        clear_survival_perception_pause(ctx, identity);
        return SurvivalNpcPerceptionState::Active;
    };

    if survival_player_is_invisible(ctx, run.owner) {
        clear_npc_target_pin(ctx, identity);
        let existing_pause = ctx.db.survival_perception_pause().identity().find(identity);
        let newly_paused = existing_pause.is_none();
        let pause = existing_pause.unwrap_or_else(|| {
            let last_known = ctx
                .db
                .player_physics()
                .identity()
                .find(run.owner)
                .map(|physics| (physics.pos_x, physics.pos_y, physics.pos_z))
                .unwrap_or(fallback_position);
            ctx.db
                .survival_perception_pause()
                .insert(SurvivalPerceptionPause {
                    identity,
                    arena_id: run.arena_id,
                    last_known_x: last_known.0,
                    last_known_y: last_known.1,
                    last_known_z: last_known.2,
                    paused_at: ctx.timestamp,
                })
        });
        return SurvivalNpcPerceptionState::Paused {
            last_known_x: pause.last_known_x,
            last_known_y: pause.last_known_y,
            last_known_z: pause.last_known_z,
            newly_paused,
        };
    }

    if ctx
        .db
        .survival_perception_pause()
        .identity()
        .find(identity)
        .is_some()
    {
        clear_survival_perception_pause(ctx, identity);
        match survival_system_owner(&run) {
            Ok(system_owner) => {
                if let Err(error) = pin_npc_target(ctx, identity, run.owner, system_owner) {
                    log::error!(
                        "[SURVIVAL] failed to resume target pin npc={}: {}",
                        identity.to_hex(),
                        error
                    );
                }
            }
            Err(error) => log::error!(
                "[SURVIVAL] failed to resolve system owner while resuming npc={}: {}",
                identity.to_hex(),
                error
            ),
        }
    }
    SurvivalNpcPerceptionState::Active
}

pub(crate) fn clear_survival_perception_pause(ctx: &ReducerContext, identity: Identity) {
    ctx.db
        .survival_perception_pause()
        .identity()
        .delete(identity);
}

fn survival_player_is_invisible(ctx: &ReducerContext, owner: Identity) -> bool {
    ctx.db
        .active_combat_mode()
        .owner()
        .find(owner)
        .is_some_and(|mode| combat_mode_is_survival_invisible(mode.mode_id.as_str()))
}

fn combat_mode_is_survival_invisible(mode_id: &str) -> bool {
    mode_id == COMBAT_MODE_STEALTHED
}

pub(crate) fn survival_player_is_invulnerable(ctx: &ReducerContext, identity: Identity) -> bool {
    ctx.db
        .survival_run()
        .owner()
        .filter(identity)
        .next()
        .is_some_and(|run| run.phase == SURVIVAL_PHASE_INTERMISSION)
}

pub(crate) fn resolve_survival_spawn_override(
    ctx: &ReducerContext,
    instance_id: u64,
) -> Option<(f32, f32, f32)> {
    let arena = ctx.db.arena_instance().id().find(instance_id)?;
    if arena.instance_kind != INSTANCE_KIND_SURVIVAL {
        return None;
    }
    let spawn = survival_layout().player_spawn;
    Some((spawn.x, spawn.z, spawn.yaw))
}

pub(crate) fn on_survival_npc_defeated(
    ctx: &ReducerContext,
    identity: Identity,
    killer: Identity,
) -> bool {
    let Some(membership) = ctx.db.survival_npc().identity().find(identity) else {
        return false;
    };
    clear_survival_perception_pause(ctx, identity);
    ctx.db.survival_npc().identity().delete(identity);
    if let Some(mut run) = ctx.db.survival_run().arena_id().find(membership.arena_id) {
        if membership.origin == SURVIVAL_ORIGIN_DIRECTOR {
            run.director_alive = run.director_alive.saturating_sub(1);
        }
        run.total_alive = run.total_alive.saturating_sub(1);
        run.kills = run.kills.saturating_add(1);
        let payout = survival_kill_payout(membership.gold_value, killer == run.owner);
        run.gold = run.gold.saturating_add(payout);
        run.gold_earned = run.gold_earned.saturating_add(payout);
        if boss_death_completes_round(run.phase.as_str(), run.boss_identity, identity) {
            finish_active_round(ctx, run, ctx.timestamp);
        } else {
            ctx.db.survival_run().arena_id().update(run);
        }
    }
    true
}

fn survival_kill_payout(gold_value: u32, killed_by_owner: bool) -> u64 {
    if killed_by_owner {
        gold_value as u64
    } else {
        0
    }
}

pub(crate) fn end_survival_run_for_player_death(ctx: &ReducerContext, owner: Identity) {
    match teardown_survival_for_owner(ctx, owner, "death") {
        Ok(true) => {}
        Ok(false) => return,
        Err(error) => {
            log::error!(
                "[SURVIVAL] death teardown failed owner={}: {}",
                owner.to_hex(),
                error
            );
            return;
        }
    }
    if let Err(error) = set_player_open_world(ctx, owner) {
        log::error!(
            "[SURVIVAL] failed to return defeated owner={} to open world: {}",
            owner.to_hex(),
            error
        );
    }
}

pub(crate) fn teardown_survival_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    reason: &str,
) -> Result<bool, String> {
    let Some(run) = ctx.db.survival_run().owner().filter(owner).next() else {
        return Ok(false);
    };

    let round_reached = completed_round_for_result(&run);
    upsert_survival_score(ctx, &run, round_reached);
    upsert_survival_result(ctx, &run, round_reached);

    let system_owner = survival_system_owner(&run)?;
    let memberships: Vec<Identity> = ctx
        .db
        .survival_npc()
        .arena_id()
        .filter(run.arena_id)
        .map(|row| row.identity)
        .collect();
    for identity in memberships {
        clear_survival_perception_pause(ctx, identity);
        ctx.db.survival_npc().identity().delete(identity);
    }
    clear_survival_summon_quotas(ctx, run.arena_id);
    despawn_all_npcs_for_owner(ctx, system_owner);
    clear_npc_spawn_counter_for_owner(ctx, system_owner);

    restore_survival_inventory(ctx, owner, run.arena_id)?;
    clear_survival_shop_offers(ctx, run.arena_id);
    let upgrade_keys: Vec<String> = ctx
        .db
        .survival_upgrade()
        .arena_id()
        .filter(run.arena_id)
        .map(|row| row.key)
        .collect();
    for key in upgrade_keys {
        ctx.db.survival_upgrade().key().delete(key);
    }

    ctx.db.survival_run().arena_id().delete(run.arena_id);
    log::info!(
        "[SURVIVAL] teardown owner={} arena={} reason={} round={} kills={}",
        owner.to_hex(),
        run.arena_id,
        reason,
        round_reached,
        run.kills
    );
    Ok(true)
}

fn spawn_next_director_npc(
    ctx: &ReducerContext,
    run: &mut SurvivalRun,
    now: Timestamp,
) -> Result<bool, String> {
    let candidates = affordable_director_candidates(run);
    if candidates.is_empty() {
        return Ok(false);
    }

    let draw_hash = director_hash(run.seed, run.round, run.spawn_sequence);
    run.spawn_sequence = run.spawn_sequence.saturating_add(1);
    let rating = candidates[(draw_hash as usize) % candidates.len()];
    let spawn_points = &survival_layout().edge_spawn_points;
    let spawn = spawn_points[(mix64(draw_hash) as usize) % spawn_points.len()];
    let yaw = (-spawn.x).atan2(-spawn.z);
    let system_owner = survival_system_owner(run)?;
    let identity = spawn_system_npc_in_instance(
        ctx,
        system_owner,
        rating.template_id.as_str(),
        run.arena_id,
        spawn.x,
        spawn.z,
        yaw,
    )?;

    let (hp_multiplier, damage_multiplier) = round_stat_multipliers(run.round);
    if hp_multiplier > 1.0 {
        if let Some(mut state) = ctx.db.npc_state().identity().find(identity) {
            let scaled_hp = ((state.max_hp as f32) * hp_multiplier).ceil() as i32;
            state.max_hp = scaled_hp.max(1);
            state.hp = state.max_hp;
            ctx.db.npc_state().identity().update(state);
        }
    }
    if damage_multiplier > 1.0 {
        apply_survival_npc_damage_multiplier(ctx, identity, damage_multiplier);
    }
    if let Err(error) = pin_npc_target(ctx, identity, run.owner, system_owner) {
        despawn_npc_identity(ctx, identity);
        return Err(error);
    }

    let scaled_rating = scaled_rating(rating.rating, hp_multiplier, damage_multiplier);
    ctx.db.survival_npc().insert(SurvivalNpc {
        identity,
        arena_id: run.arena_id,
        origin: SURVIVAL_ORIGIN_DIRECTOR.to_string(),
        role: SURVIVAL_ROLE_ADD.to_string(),
        summoner: None,
        summon_depth: 0,
        rating: scaled_rating,
        gold_value: (scaled_rating * 0.35).ceil().max(rating.gold_value as f32) as u32,
        round: run.round,
    });
    run.director_alive = run.director_alive.saturating_add(1);
    run.total_alive = run.total_alive.saturating_add(1);
    run.budget_remaining = (run.budget_remaining - scaled_rating).max(0.0);
    run.next_spawn_at = now + SURVIVAL_SPAWN_INTERVAL;
    Ok(true)
}

fn spawn_boss_for_round(
    ctx: &ReducerContext,
    run: &mut SurvivalRun,
    now: Timestamp,
) -> Result<(), String> {
    let spec = boss_round_spec(run.round)
        .ok_or_else(|| format!("Round {} is not a survival boss round", run.round))?;
    let draw_hash = director_hash(run.seed ^ 0x424f_5353_5f52_4f55, run.round, 0);
    let template_id = boss_template_for_round(run.seed, run.round)
        .ok_or_else(|| format!("Round {} has no survival boss template", run.round))?;
    let rating = survival_ratings()
        .iter()
        .find(|entry| entry.template_id == template_id)
        .ok_or_else(|| format!("Boss template {template_id} is missing a survival rating"))?;
    let spawn_points = &survival_layout().edge_spawn_points;
    let spawn = spawn_points[(mix64(draw_hash) as usize) % spawn_points.len()];
    let yaw = (-spawn.x).atan2(-spawn.z);
    let system_owner = survival_system_owner(run)?;
    let identity = spawn_system_npc_in_instance(
        ctx,
        system_owner,
        template_id,
        run.arena_id,
        spawn.x,
        spawn.z,
        yaw,
    )?;

    let (round_hp_multiplier, damage_multiplier) = round_stat_multipliers(run.round);
    let hp_multiplier = round_hp_multiplier * spec.hp_multiplier;
    let Some(mut state) = ctx.db.npc_state().identity().find(identity) else {
        despawn_npc_identity(ctx, identity);
        return Err("Survival boss spawned without NPC state".to_string());
    };
    let scaled_hp = ((state.max_hp as f32) * hp_multiplier).ceil() as i32;
    state.max_hp = scaled_hp.max(1);
    state.hp = state.max_hp;
    ctx.db.npc_state().identity().update(state);
    if damage_multiplier > 1.0 {
        apply_survival_npc_damage_multiplier(ctx, identity, damage_multiplier);
    }
    if let Err(error) = pin_npc_target(ctx, identity, run.owner, system_owner) {
        despawn_npc_identity(ctx, identity);
        return Err(error);
    }

    let scaled_rating = scaled_rating(rating.rating, hp_multiplier, damage_multiplier);
    ctx.db.survival_npc().insert(SurvivalNpc {
        identity,
        arena_id: run.arena_id,
        origin: SURVIVAL_ORIGIN_DIRECTOR.to_string(),
        role: SURVIVAL_ROLE_BOSS.to_string(),
        summoner: None,
        summon_depth: 0,
        rating: scaled_rating,
        gold_value: (scaled_rating * 0.35).ceil().max(rating.gold_value as f32) as u32,
        round: run.round,
    });
    run.director_alive = run.director_alive.saturating_add(1);
    run.total_alive = run.total_alive.saturating_add(1);
    run.next_spawn_at = now + SURVIVAL_SPAWN_INTERVAL;
    run.boss_identity = Some(identity);
    Ok(())
}

#[allow(clippy::too_many_arguments)]
#[allow(dead_code)]
pub(crate) fn try_spawn_survival_summon(
    ctx: &ReducerContext,
    summoner: Identity,
    template_id: &str,
    spawn_x: f32,
    spawn_z: f32,
    yaw: f32,
    policy: SurvivalSummonPolicy,
) -> Result<Option<Identity>, String> {
    let Some(parent) = ctx.db.survival_npc().identity().find(summoner) else {
        return Ok(None);
    };
    let Some(mut run) = ctx.db.survival_run().arena_id().find(parent.arena_id) else {
        return Ok(None);
    };
    if !matches!(
        run.phase.as_str(),
        SURVIVAL_PHASE_ACTIVE | SURVIVAL_PHASE_BOSS
    ) || parent.round != run.round
        || !ctx
            .db
            .npc_state()
            .identity()
            .find(summoner)
            .is_some_and(|state| state.alive)
    {
        return Ok(None);
    }

    let existing_quota = ctx.db.survival_summon_quota().summoner().find(summoner);
    let summoned_this_round = existing_quota
        .as_ref()
        .filter(|quota| quota.arena_id == run.arena_id && quota.round == run.round)
        .map(|quota| quota.summoned_this_round)
        .unwrap_or(0);
    if !survival_summon_is_admitted(
        run.total_alive,
        parent.summon_depth,
        summoned_this_round,
        policy,
    ) {
        return Ok(None);
    }

    let rating = survival_ratings()
        .iter()
        .find(|entry| entry.template_id == template_id)
        .ok_or_else(|| format!("Summoned template {template_id} is missing a survival rating"))?;
    let system_owner = survival_system_owner(&run)?;
    let identity = spawn_system_npc_in_instance(
        ctx,
        system_owner,
        template_id,
        run.arena_id,
        spawn_x,
        spawn_z,
        yaw,
    )?;
    let (hp_multiplier, damage_multiplier) = round_stat_multipliers(run.round);
    if hp_multiplier > 1.0 {
        let Some(mut state) = ctx.db.npc_state().identity().find(identity) else {
            despawn_npc_identity(ctx, identity);
            return Err("Survival summon spawned without NPC state".to_string());
        };
        let scaled_hp = ((state.max_hp as f32) * hp_multiplier).ceil() as i32;
        state.max_hp = scaled_hp.max(1);
        state.hp = state.max_hp;
        ctx.db.npc_state().identity().update(state);
    }
    if damage_multiplier > 1.0 {
        apply_survival_npc_damage_multiplier(ctx, identity, damage_multiplier);
    }
    if let Err(error) = pin_npc_target(ctx, identity, run.owner, system_owner) {
        despawn_npc_identity(ctx, identity);
        return Err(error);
    }

    let scaled_rating = scaled_rating(rating.rating, hp_multiplier, damage_multiplier);
    ctx.db.survival_npc().insert(SurvivalNpc {
        identity,
        arena_id: run.arena_id,
        origin: SURVIVAL_ORIGIN_SUMMON.to_string(),
        role: SURVIVAL_ROLE_ADD.to_string(),
        summoner: Some(summoner),
        summon_depth: parent.summon_depth.saturating_add(1),
        rating: scaled_rating,
        gold_value: 0,
        round: run.round,
    });
    run.total_alive = run.total_alive.saturating_add(1);
    ctx.db.survival_run().arena_id().update(run.clone());

    let quota = SurvivalSummonQuota {
        summoner,
        arena_id: run.arena_id,
        round: run.round,
        summoned_this_round: summoned_this_round.saturating_add(1),
    };
    if existing_quota.is_some() {
        ctx.db.survival_summon_quota().summoner().update(quota);
    } else {
        ctx.db.survival_summon_quota().insert(quota);
    }
    Ok(Some(identity))
}

fn finish_active_round(ctx: &ReducerContext, mut run: SurvivalRun, now: Timestamp) {
    despawn_survival_npcs_for_arena(ctx, run.arena_id);
    clear_survival_summon_quotas(ctx, run.arena_id);
    run.director_alive = 0;
    run.total_alive = 0;
    run.budget_remaining = 0.0;
    run.spawn_sequence = 0;
    run.phase = SURVIVAL_PHASE_INTERMISSION.to_string();
    run.round = run.round.saturating_add(1);
    run.round_started_at = now;
    run.round_ends_at = now;
    run.next_spawn_at = now;
    run.boss_identity = None;
    ctx.db.survival_run().arena_id().update(run.clone());
    restore_survival_player_for_intermission(ctx, run.owner, now);
    if let Err(error) = create_survival_shop_offers(ctx, &run, run.round.max(1)) {
        log::error!(
            "[SURVIVAL] failed to create shop offers arena={} round={}: {}",
            run.arena_id,
            run.round,
            error
        );
    }
}

fn restore_survival_player_for_intermission(ctx: &ReducerContext, owner: Identity, now: Timestamp) {
    if let Some(mut state) = ctx.db.player_state().player_id().find(owner) {
        if state.alive && state.hp != state.max_hp {
            state.hp = state.max_hp.max(1);
            ctx.db.player_state().player_id().update(state);
        }
    }
    reset_player_resources_to_full(ctx, owner, now);
}

#[derive(Clone, Copy)]
struct SurvivalModifierSpec {
    modifier_id: &'static str,
    value_per_stack: f32,
    cap: u32,
    base_price: u64,
}

const SURVIVAL_MODIFIER_SPECS: &[SurvivalModifierSpec] = &[
    SurvivalModifierSpec {
        modifier_id: MODIFIER_PHYSICAL_DAMAGE,
        value_per_stack: 0.08,
        cap: 8,
        base_price: 110,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_FORTITUDE,
        value_per_stack: 1.0,
        cap: 10,
        base_price: 90,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_MOVE_SPEED,
        value_per_stack: 0.04,
        cap: 5,
        base_price: 130,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_CRIT_CHANCE,
        value_per_stack: 0.03,
        cap: 6,
        base_price: 120,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_HEALTH_REGEN,
        value_per_stack: 1.5,
        cap: 5,
        base_price: 100,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_FIRE_RESISTANCE,
        value_per_stack: 0.06,
        cap: 5,
        base_price: 70,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_COLD_RESISTANCE,
        value_per_stack: 0.06,
        cap: 5,
        base_price: 70,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_LIGHTNING_RESISTANCE,
        value_per_stack: 0.06,
        cap: 5,
        base_price: 70,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_POISON_RESISTANCE,
        value_per_stack: 0.06,
        cap: 5,
        base_price: 70,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_HOLY_RESISTANCE,
        value_per_stack: 0.06,
        cap: 5,
        base_price: 70,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_SHADOW_RESISTANCE,
        value_per_stack: 0.06,
        cap: 5,
        base_price: 70,
    },
    SurvivalModifierSpec {
        modifier_id: MODIFIER_ARCANE_RESISTANCE,
        value_per_stack: 0.06,
        cap: 5,
        base_price: 70,
    },
];

fn create_survival_shop_offers(
    ctx: &ReducerContext,
    run: &SurvivalRun,
    shop_round: u32,
) -> Result<(), String> {
    clear_survival_shop_offers(ctx, run.arena_id);

    let mut eligible: Vec<SurvivalModifierSpec> = SURVIVAL_MODIFIER_SPECS
        .iter()
        .copied()
        .filter(|spec| survival_upgrade_stacks(ctx, run.arena_id, spec.modifier_id) < spec.cap)
        .collect();
    for slot in 0..SURVIVAL_MODIFIER_OFFER_COUNT {
        if eligible.is_empty() {
            break;
        }
        let draw = survival_offer_hash(run.seed, shop_round, slot);
        let spec = eligible.remove((draw as usize) % eligible.len());
        let stacks = survival_upgrade_stacks(ctx, run.arena_id, spec.modifier_id);
        ctx.db.survival_shop_offer().insert(SurvivalShopOffer {
            offer_id: survival_offer_id(run.arena_id, shop_round, slot, draw),
            arena_id: run.arena_id,
            round: shop_round,
            kind: SURVIVAL_OFFER_KIND_MODIFIER.to_string(),
            modifier_id: spec.modifier_id.to_string(),
            item_instance_id: String::new(),
            price: survival_modifier_price(spec.base_price, stacks),
            purchased: false,
        });
    }

    for item_index in 0..SURVIVAL_ITEM_OFFER_COUNT {
        let slot = SURVIVAL_MODIFIER_OFFER_COUNT + item_index;
        let draw = survival_offer_hash(run.seed, shop_round, slot);
        let (item_instance_id, affix_count) =
            roll_survival_shop_item(ctx, run.owner, run.arena_id, shop_round, slot, run.seed)?;
        ctx.db.survival_shop_offer().insert(SurvivalShopOffer {
            offer_id: survival_offer_id(run.arena_id, shop_round, slot, draw),
            arena_id: run.arena_id,
            round: shop_round,
            kind: SURVIVAL_OFFER_KIND_ITEM.to_string(),
            modifier_id: String::new(),
            item_instance_id,
            price: survival_item_price(shop_round, affix_count),
            purchased: false,
        });
    }
    Ok(())
}

fn clear_survival_shop_offers(ctx: &ReducerContext, arena_id: u64) {
    let offers: Vec<SurvivalShopOffer> = ctx
        .db
        .survival_shop_offer()
        .arena_id()
        .filter(arena_id)
        .collect();
    for offer in offers {
        if offer.kind == SURVIVAL_OFFER_KIND_ITEM
            && !offer.purchased
            && !offer.item_instance_id.is_empty()
        {
            delete_item_aggregate(ctx, offer.item_instance_id.as_str());
        }
        ctx.db
            .survival_shop_offer()
            .offer_id()
            .delete(offer.offer_id);
    }
}

fn purchase_survival_modifier(
    ctx: &ReducerContext,
    run: &SurvivalRun,
    modifier_id: &str,
) -> Result<(), String> {
    let spec = SURVIVAL_MODIFIER_SPECS
        .iter()
        .find(|spec| spec.modifier_id == modifier_id)
        .ok_or_else(|| "Survival modifier is not in the approved economy".to_string())?;
    let key = survival_upgrade_key(run.arena_id, modifier_id);
    if let Some(mut upgrade) = ctx.db.survival_upgrade().key().find(key.clone()) {
        if upgrade.stacks >= spec.cap {
            return Err("Survival modifier is already at its stack cap".to_string());
        }
        upgrade.stacks = upgrade.stacks.saturating_add(1);
        upgrade.total_value += spec.value_per_stack;
        ctx.db.survival_upgrade().key().update(upgrade);
    } else {
        ctx.db.survival_upgrade().insert(SurvivalUpgrade {
            key,
            arena_id: run.arena_id,
            modifier_id: modifier_id.to_string(),
            stacks: 1,
            total_value: spec.value_per_stack,
        });
    }
    Ok(())
}

fn survival_upgrade_stacks(ctx: &ReducerContext, arena_id: u64, modifier_id: &str) -> u32 {
    ctx.db
        .survival_upgrade()
        .key()
        .find(survival_upgrade_key(arena_id, modifier_id))
        .map(|row| row.stacks)
        .unwrap_or(0)
}

fn survival_upgrade_key(arena_id: u64, modifier_id: &str) -> String {
    format!("{arena_id}:{modifier_id}")
}

fn survival_offer_hash(seed: u64, round: u32, slot: u32) -> u64 {
    director_hash(seed ^ 0x5348_4f50_5f4f_4646, round, slot)
}

fn survival_offer_id(arena_id: u64, round: u32, slot: u32, hash: u64) -> String {
    format!("survival:{arena_id}:{round}:{slot:02}:{hash:016x}")
}

fn survival_modifier_price(base_price: u64, stacks_owned: u32) -> u64 {
    ((base_price as f32) * (1.0 + 0.35 * stacks_owned as f32)).round() as u64
}

fn survival_item_price(round: u32, affix_count: u32) -> u64 {
    (140.0 * (1.0 + 0.18 * round.saturating_sub(1) as f32) * (1.0 + 0.5 * affix_count as f32))
        .round() as u64
}

fn despawn_survival_npcs_for_arena(ctx: &ReducerContext, arena_id: u64) {
    let identities: Vec<Identity> = ctx
        .db
        .survival_npc()
        .arena_id()
        .filter(arena_id)
        .map(|row| row.identity)
        .collect();
    for identity in identities {
        clear_survival_perception_pause(ctx, identity);
        ctx.db.survival_npc().identity().delete(identity);
        despawn_npc_identity(ctx, identity);
    }
}

fn clear_survival_summon_quotas(ctx: &ReducerContext, arena_id: u64) {
    let summoners: Vec<Identity> = ctx
        .db
        .survival_summon_quota()
        .arena_id()
        .filter(arena_id)
        .map(|row| row.summoner)
        .collect();
    for summoner in summoners {
        ctx.db.survival_summon_quota().summoner().delete(summoner);
    }
}

#[allow(dead_code)]
fn survival_summon_is_admitted(
    total_alive: u32,
    parent_depth: u32,
    summoned_this_round: u32,
    policy: SurvivalSummonPolicy,
) -> bool {
    let max_depth = policy.max_depth.min(MAX_SUMMON_DEPTH);
    let max_children = policy.max_children_per_round.min(TOTAL_ALIVE_CEILING);
    total_alive < TOTAL_ALIVE_CEILING
        && parent_depth < max_depth
        && summoned_this_round < max_children
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct BossRoundSpec {
    hp_multiplier: f32,
    templates: &'static [&'static str],
}

fn is_boss_round(round: u32) -> bool {
    round > 0 && round % 5 == 0
}

fn boss_round_spec(round: u32) -> Option<BossRoundSpec> {
    if !is_boss_round(round) {
        return None;
    }
    let spec = match round {
        5 => BossRoundSpec {
            hp_multiplier: 3.0,
            templates: &["FIRE_REVENANT", "ROCK_GOLEM"],
        },
        10 => BossRoundSpec {
            hp_multiplier: 4.0,
            templates: &["LICH_BOSS", "DEMON_BOSS", "OGRE_KING"],
        },
        15 => BossRoundSpec {
            hp_multiplier: 4.0,
            templates: &["DRAKE", "SKELETAL_DRAGON"],
        },
        _ => BossRoundSpec {
            hp_multiplier: 5.0,
            templates: &["DRAGON", "ELDER_DRAGON"],
        },
    };
    Some(spec)
}

fn boss_add_budget(round: u32) -> f32 {
    director_budget(round) * BOSS_ADD_BUDGET_MULTIPLIER
}

fn boss_template_for_round(seed: u64, round: u32) -> Option<&'static str> {
    let spec = boss_round_spec(round)?;
    let draw_hash = director_hash(seed ^ 0x424f_5353_5f52_4f55, round, 0);
    Some(spec.templates[(draw_hash as usize) % spec.templates.len()])
}

fn boss_death_completes_round(
    phase: &str,
    boss_identity: Option<Identity>,
    death_identity: Identity,
) -> bool {
    phase == SURVIVAL_PHASE_BOSS && boss_identity == Some(death_identity)
}

fn affordable_director_candidates(run: &SurvivalRun) -> Vec<&'static SurvivalRating> {
    let (band_min, band_max) = director_band(run.round);
    let (hp_multiplier, damage_multiplier) = round_stat_multipliers(run.round);
    survival_ratings()
        .iter()
        .filter(|entry| {
            let band_rating = entry.rating.round();
            band_rating >= band_min && band_rating <= band_max
        })
        .filter(|entry| {
            scaled_rating(entry.rating, hp_multiplier, damage_multiplier)
                <= run.budget_remaining + f32::EPSILON
        })
        .collect()
}

fn director_is_exhausted(run: &SurvivalRun) -> bool {
    affordable_director_candidates(run).is_empty()
}

fn director_budget(round: u32) -> f32 {
    BASE_BUDGET * (round.max(1) as f32).powf(BUDGET_EXPONENT)
}

fn director_band(round: u32) -> (f32, f32) {
    let offset = round.saturating_sub(1) as f32;
    (
        (RATING_MIN + 4.0 * offset).clamp(RATING_MIN, RATING_MAX),
        (82.0 + 9.0 * offset).clamp(RATING_MIN, RATING_MAX),
    )
}

fn round_stat_multipliers(round: u32) -> (f32, f32) {
    let late_rounds = round.saturating_sub(11) as f32;
    (1.0 + 0.06 * late_rounds, 1.0 + 0.04 * late_rounds)
}

fn scaled_rating(base_rating: f32, hp_multiplier: f32, damage_multiplier: f32) -> f32 {
    base_rating * hp_multiplier.sqrt() * damage_multiplier.powf(0.65)
}

fn director_hash(seed: u64, round: u32, sequence: u32) -> u64 {
    mix64(
        seed ^ u64::from(round).wrapping_mul(0x9e37_79b9_7f4a_7c15)
            ^ u64::from(sequence).wrapping_mul(0xc2b2_ae3d_27d4_eb4f),
    )
}

fn mix64(mut value: u64) -> u64 {
    value ^= value >> 33;
    value = value.wrapping_mul(0xff51_afd7_ed55_8ccd);
    value ^= value >> 33;
    value = value.wrapping_mul(0xc4ce_b9fe_1a85_ec53);
    value ^ (value >> 33)
}

fn survival_system_owner(run: &SurvivalRun) -> Result<Identity, String> {
    let token = (u128::from(run.seed) << 64) | u128::from(run.arena_id);
    Identity::from_hex(format!("{SURVIVAL_OWNER_MAGIC:032x}{token:032x}").as_str())
        .map_err(|error| format!("Cannot derive survival system owner: {error}"))
}

fn owner_is_in_run_arena(ctx: &ReducerContext, run: &SurvivalRun) -> bool {
    ctx.db
        .player_world()
        .identity()
        .find(run.owner)
        .is_some_and(|world| world.instance_id == Some(run.arena_id))
}

fn completed_round_for_result(run: &SurvivalRun) -> u32 {
    if run.phase == SURVIVAL_PHASE_INTERMISSION {
        run.round.saturating_sub(1)
    } else {
        run.round
    }
}

fn upsert_survival_score(ctx: &ReducerContext, run: &SurvivalRun, round_reached: u32) {
    let row = if let Some(existing) = ctx.db.survival_score().owner().find(run.owner) {
        SurvivalScore {
            owner: run.owner,
            best_round: existing.best_round.max(round_reached),
            best_kills: existing.best_kills.max(run.kills),
            best_gold_earned: existing.best_gold_earned.max(run.gold_earned),
            runs_played: existing.runs_played.saturating_add(1),
        }
    } else {
        SurvivalScore {
            owner: run.owner,
            best_round: round_reached,
            best_kills: run.kills,
            best_gold_earned: run.gold_earned,
            runs_played: 1,
        }
    };
    if ctx.db.survival_score().owner().find(run.owner).is_some() {
        ctx.db.survival_score().owner().update(row);
    } else {
        ctx.db.survival_score().insert(row);
    }
}

fn upsert_survival_result(ctx: &ReducerContext, run: &SurvivalRun, round_reached: u32) {
    let row = SurvivalResult {
        owner: run.owner,
        round_reached,
        kills: run.kills,
        gold_earned: run.gold_earned,
        ended_at: ctx.timestamp,
    };
    if ctx.db.survival_result().owner().find(run.owner).is_some() {
        ctx.db.survival_result().owner().update(row);
    } else {
        ctx.db.survival_result().insert(row);
    }
}

fn survival_ratings() -> &'static [SurvivalRating] {
    static RATINGS: OnceLock<SurvivalRatingsDocument> = OnceLock::new();
    RATINGS
        .get_or_init(|| {
            let document: SurvivalRatingsDocument = serde_json::from_str(SURVIVAL_RATINGS_JSON)
                .expect("survival_ratings.shared.json must remain valid");
            assert_eq!(document.schema_version, 1);
            assert!(!document.roster.is_empty());
            assert!(document.roster.iter().all(|entry| {
                entry.rating.is_finite()
                    && entry.rating > 0.0
                    && npc_template(entry.template_id.as_str()).is_some()
            }));
            document
        })
        .roster
        .as_slice()
}

fn survival_layout() -> &'static SurvivalArenaLayout {
    static LAYOUT: OnceLock<SurvivalArenaLayout> = OnceLock::new();
    LAYOUT.get_or_init(|| {
        let layout: SurvivalArenaLayout = serde_json::from_str(SURVIVAL_LAYOUT_JSON)
            .expect("survival_arena_layout.shared.json must remain valid");
        assert_eq!(layout.schema_version, 1);
        assert!(layout.arena_radius > 0.0);
        assert!(!layout.edge_spawn_points.is_empty());
        assert!(layout.player_spawn.x.is_finite());
        assert!(layout.player_spawn.z.is_finite());
        assert!(layout.player_spawn.yaw.is_finite());
        layout
    })
}

#[cfg(test)]
mod tests {
    use super::{
        boss_add_budget, boss_death_completes_round, boss_round_spec, boss_template_for_round,
        combat_mode_is_survival_invisible, director_band, director_budget, director_hash,
        round_stat_multipliers, scaled_rating, survival_item_price, survival_kill_payout,
        survival_layout, survival_modifier_price, survival_offer_hash, survival_offer_id,
        survival_ratings, survival_summon_is_admitted, SurvivalSummonPolicy,
        SURVIVAL_MODIFIER_SPECS, SURVIVAL_PHASE_ACTIVE, SURVIVAL_PHASE_BOSS,
    };
    use crate::progression::{COMBAT_MODE_READY, COMBAT_MODE_STEALTHED};
    use spacetimedb::Identity;

    #[test]
    fn director_budget_and_band_match_the_approved_curve() {
        assert!((director_budget(1) - 150.0).abs() < 0.001);
        assert_eq!(director_band(1), (54.0, 82.0));
        assert_eq!(director_band(100), (316.0, 316.0));
    }

    #[test]
    fn director_draw_hash_includes_sequence() {
        assert_eq!(director_hash(7, 3, 4), director_hash(7, 3, 4));
        assert_ne!(director_hash(7, 3, 4), director_hash(7, 3, 5));
    }

    #[test]
    fn late_round_scaling_starts_at_round_twelve() {
        assert_eq!(round_stat_multipliers(11), (1.0, 1.0));
        assert_eq!(round_stat_multipliers(12), (1.06, 1.04));
        assert!(scaled_rating(100.0, 1.06, 1.04) > 100.0);
    }

    #[test]
    fn authored_survival_inputs_are_complete_and_flat() {
        assert_eq!(survival_ratings().len(), 92);
        let layout = survival_layout();
        assert_eq!(layout.arena_radius, 30.0);
        assert_eq!(layout.edge_spawn_points.len(), 4);
        let entrance_spawns: Vec<(f32, f32)> = layout
            .edge_spawn_points
            .iter()
            .map(|spawn| (spawn.x, spawn.z))
            .collect();
        assert_eq!(
            entrance_spawns,
            vec![(0.0, 28.0), (28.0, 0.0), (0.0, -28.0), (-28.0, 0.0)]
        );
        assert!(layout._obstacles.is_empty());
    }

    #[test]
    fn gold_pays_only_to_the_run_owner() {
        assert_eq!(survival_kill_payout(19, true), 19);
        assert_eq!(survival_kill_payout(19, false), 0);
        assert_eq!(survival_kill_payout(0, true), 0);
    }

    #[test]
    fn approved_shop_economy_is_encoded_exactly() {
        assert_eq!(survival_modifier_price(110, 0), 110);
        assert_eq!(survival_modifier_price(110, 1), 149);
        assert_eq!(survival_item_price(1, 1), 210);
        assert_eq!(survival_item_price(5, 2), 482);
        assert_eq!(SURVIVAL_MODIFIER_SPECS.len(), 12);
        let fortitude = SURVIVAL_MODIFIER_SPECS
            .iter()
            .find(|spec| spec.modifier_id == "FORTITUDE")
            .expect("fortitude offer");
        assert_eq!(fortitude.value_per_stack, 1.0);
        assert_eq!(fortitude.cap, 10);
        assert_eq!(fortitude.base_price, 90);
    }

    #[test]
    fn shop_offer_ids_are_deterministic_and_slot_distinct() {
        let first = survival_offer_hash(7, 5, 0);
        assert_eq!(first, survival_offer_hash(7, 5, 0));
        assert_ne!(first, survival_offer_hash(7, 5, 1));
        assert_eq!(
            survival_offer_id(11, 5, 0, first),
            format!("survival:11:5:00:{first:016x}")
        );
    }

    #[test]
    fn only_stealthed_combat_mode_hides_the_survival_player() {
        assert!(combat_mode_is_survival_invisible(COMBAT_MODE_STEALTHED));
        assert!(!combat_mode_is_survival_invisible(COMBAT_MODE_READY));
        assert!(!combat_mode_is_survival_invisible(""));
    }

    #[test]
    fn boss_round_table_and_add_budget_match_the_approved_design() {
        assert!(boss_round_spec(4).is_none());
        assert_eq!(boss_round_spec(5).unwrap().hp_multiplier, 3.0);
        assert_eq!(
            boss_round_spec(5).unwrap().templates,
            ["FIRE_REVENANT", "ROCK_GOLEM"]
        );
        assert_eq!(boss_round_spec(10).unwrap().hp_multiplier, 4.0);
        assert_eq!(boss_round_spec(15).unwrap().hp_multiplier, 4.0);
        assert_eq!(boss_round_spec(20).unwrap().hp_multiplier, 5.0);
        assert_eq!(
            boss_round_spec(25).unwrap().templates,
            ["DRAGON", "ELDER_DRAGON"]
        );
        assert!((boss_add_budget(5) - director_budget(5) * 0.30).abs() < 0.001);
        assert_eq!(
            boss_template_for_round(71, 10),
            boss_template_for_round(71, 10)
        );
        assert!(boss_round_spec(10)
            .unwrap()
            .templates
            .contains(&boss_template_for_round(71, 10).unwrap()));
    }

    #[test]
    fn boss_round_completion_requires_the_tracked_boss_identity() {
        let boss = Identity::ZERO;
        let add =
            Identity::from_hex("1111111111111111111111111111111122222222222222222222222222222222")
                .unwrap();
        assert!(boss_death_completes_round(
            SURVIVAL_PHASE_BOSS,
            Some(boss),
            boss
        ));
        assert!(!boss_death_completes_round(
            SURVIVAL_PHASE_BOSS,
            Some(boss),
            add
        ));
        assert!(!boss_death_completes_round(
            SURVIVAL_PHASE_ACTIVE,
            Some(boss),
            boss
        ));
        assert!(!boss_death_completes_round(SURVIVAL_PHASE_BOSS, None, boss));
    }

    #[test]
    fn summon_admission_supports_npc_specific_quotas_and_bounded_chains() {
        let normal = SurvivalSummonPolicy {
            max_children_per_round: 3,
            max_depth: 1,
        };
        let elite = SurvivalSummonPolicy {
            max_children_per_round: 8,
            max_depth: 2,
        };
        assert!(survival_summon_is_admitted(39, 0, 2, normal));
        assert!(!survival_summon_is_admitted(39, 0, 3, normal));
        assert!(!survival_summon_is_admitted(39, 1, 0, normal));

        assert!(survival_summon_is_admitted(39, 1, 7, elite));
        assert!(!survival_summon_is_admitted(39, 2, 0, elite));
        assert!(!survival_summon_is_admitted(40, 0, 0, elite));

        let unbounded_request = SurvivalSummonPolicy {
            max_children_per_round: u32::MAX,
            max_depth: u32::MAX,
        };
        assert!(!survival_summon_is_admitted(1, 2, 0, unbounded_request));
        assert!(!survival_summon_is_admitted(1, 0, 40, unbounded_request));
    }
}

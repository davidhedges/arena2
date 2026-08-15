//! Dedicated disposable PvP match module.
//!
//! This crate owns only the build/schema boundary. Every gameplay module below
//! is compiled from the authoritative server source tree, so movement, combat,
//! and match lifecycle behavior cannot drift into a copied implementation.

#[path = "../../server/src/action_ids.rs"]
mod action_ids;
#[path = "../../server/src/action_prediction.rs"]
mod action_prediction;
#[path = "../../server/src/action_snapshot.rs"]
mod action_snapshot;
#[path = "../../server/src/actor_lifecycle.rs"]
mod actor_lifecycle;
#[cfg(test)]
#[path = "../../server/src/animation_set_test_utils.rs"]
mod animation_set_test_utils;
#[path = "../../server/src/appearance.rs"]
mod appearance;
#[path = "../../server/src/arena.rs"]
mod arena;
#[path = "../../server/src/arena_maps.rs"]
mod arena_maps;
#[path = "../../server/src/auto_attack.rs"]
mod auto_attack;
#[path = "../../server/src/bot_matches.rs"]
mod bot_matches;
#[path = "../../server/src/combat.rs"]
mod combat;
#[path = "../../server/src/contract_version.rs"]
mod contract_version;
#[path = "../../server/src/defense.rs"]
mod defense;
#[path = "../../server/src/derived_stats.rs"]
mod derived_stats;
#[path = "../../server/src/game_loop.rs"]
pub(crate) mod game_loop;
#[path = "../../server/src/inventory.rs"]
mod inventory;
#[path = "../../server/src/lingering_shade.rs"]
mod lingering_shade;
#[path = "../../server/src/match_contract.rs"]
mod match_contract;
#[path = "../../server/src/melee.rs"]
mod melee;
#[path = "../../server/src/movement.rs"]
mod movement;
#[path = "../../server/src/movement_actions.rs"]
mod movement_actions;
#[path = "../../server/src/npcs.rs"]
mod npcs;
#[path = "../../server/src/open_world_scene.rs"]
mod open_world_scene;
#[path = "../../server/src/open_world_terrain.rs"]
mod open_world_terrain;
#[path = "stubs/party.rs"]
mod party;
#[path = "../../server/src/ping.rs"]
mod ping;
#[path = "../../server/src/player.rs"]
mod player;
#[path = "../../server/src/player_input.rs"]
mod player_input;
#[path = "../../server/src/player_intent.rs"]
mod player_intent;
#[path = "../../server/src/player_physics.rs"]
mod player_physics;
#[path = "../../server/src/player_state.rs"]
mod player_state;
#[path = "stubs/playground_targets.rs"]
mod playground_targets;
#[path = "stubs/practice.rs"]
mod practice;
#[path = "../../server/src/progression.rs"]
mod progression;
#[path = "../../server/src/relations.rs"]
mod relations;
#[path = "../../server/src/resources.rs"]
mod resources;
#[path = "../../server/src/spells/mod.rs"]
mod spells;
#[path = "stubs/survival.rs"]
mod survival;
#[path = "../../server/src/tick_metrics.rs"]
mod tick_metrics;
#[path = "../../server/src/verdant_spirits.rs"]
mod verdant_spirits;
#[cfg(test)]
#[path = "../../server/src/vfx_generation.rs"]
mod vfx_generation;
#[path = "../../server/src/world_collision.rs"]
mod world_collision;
#[path = "stubs/world_interactions.rs"]
mod world_interactions;
#[path = "../../server/src/world_obstacles.rs"]
mod world_obstacles;
#[path = "stubs/world_traps.rs"]
mod world_traps;

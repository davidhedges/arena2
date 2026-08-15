//! Arena Server Module
//!
//! Architecture invariants (from audit):
//! 1. `grounded` is written by exactly one reducer: `game_tick`
//! 2. All velocity fields are written by exactly one reducer: `game_tick`
//! 3. Jump and landing transitions occur in the same function (`game_tick`)
//! 4. Intent is consumed, not executed - `send_movement_intent` records intent only
//! 5. No preserved velocity fields - horizontal velocity locks by not overwriting while airborne
//! 6. Position integration immediately follows jump initiation (same tick)
//! 7. Landing check only runs after position integration

// All modules - SpacetimeDB will find tables and reducers via the macros
mod action_ids;
mod action_prediction;
mod action_snapshot;
mod actor_lifecycle;
#[cfg(test)]
mod animation_set_test_utils;
mod appearance;
mod arena;
mod arena_maps;
mod auto_attack;
mod bot_matches;
mod combat;
mod contract_version;
mod defense;
mod derived_stats;
mod dice;
pub(crate) mod game_loop;
mod inventory;
mod lingering_shade;
mod match_contract;
mod melee;
mod movement;
mod movement_actions;
mod npcs;
mod open_world_scene;
mod open_world_terrain;
mod party;
mod ping;
mod player;
mod player_input;
mod player_intent;
mod player_physics;
mod player_state;
mod playground_targets;
mod practice;
mod progression;
mod relations;
mod resources;
mod spells;
mod survival;
mod tick_metrics;
mod verdant_spirits;
#[cfg(test)]
mod vfx_generation;
mod world_collision;
mod world_interactions;
mod world_obstacles;
mod world_traps;

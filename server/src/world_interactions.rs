use std::collections::HashSet;
use std::sync::OnceLock;
use std::time::Duration;

use serde::Deserialize;
use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::{
    resolve_player_world_context, ResolvedWorldContext, WorldRayHit, WorldRaycastRequest,
};
use crate::world_collision::raycast_world_with_layout_for_scene;

#[allow(unused_imports)]
use crate::player_intent::player_intent as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::spells::{active_cast as _, pending_cast_request as _};
#[allow(unused_imports)]
use crate::world_interactions::{active_world_interaction as _, world_door_state as _};

const DOOR_MANIFEST_JSON: &str = include_str!("world_data/random_dungeon.doors.shared.json");
const INTERACTION_PROFILE_MANIFEST_JSON: &str =
    include_str!("world_data/world_interaction_profiles.shared.json");
const RANDOM_DUNGEON_WORLD_KEY: &str = "RANDOM_DUNGEON";
const RANDOM_DUNGEON_SCENE_NAME: &str = "RandomDungeon";
const WORLD_KIND_OPEN: &str = "OPEN";
const COLLISION_EPSILON: f32 = 0.001;
const STATIONARY_POSITION_EPSILON: f32 = 0.02;
const MOVEMENT_INPUT_EPSILON: f32 = 0.001;
const LINE_ACCESS_TARGET_EPSILON: f32 = 0.15;
const LINE_ACCESS_PROBE_RADIUS: f32 = 0.05;

const CANCEL_MOVEMENT: u32 = 1 << 0;
const CANCEL_DISPLACEMENT: u32 = 1 << 1;
const CANCEL_DAMAGE: u32 = 1 << 2;
const CANCEL_DEATH: u32 = 1 << 3;
const CANCEL_WORLD_CHANGE: u32 = 1 << 4;
const CANCEL_RANGE_OR_LINE_ACCESS: u32 = 1 << 5;
const CANCEL_CONFLICTING_COMBAT_ACTION: u32 = 1 << 6;
const CANCEL_TARGET_REVISION_CHANGED: u32 = 1 << 7;
const KNOWN_CANCEL_CONDITIONS: u32 = CANCEL_MOVEMENT
    | CANCEL_DISPLACEMENT
    | CANCEL_DAMAGE
    | CANCEL_DEATH
    | CANCEL_WORLD_CHANGE
    | CANCEL_RANGE_OR_LINE_ACCESS
    | CANCEL_CONFLICTING_COMBAT_ACTION
    | CANCEL_TARGET_REVISION_CHANGED;

#[table(accessor = world_door_state, public)]
#[derive(Clone)]
pub struct WorldDoorState {
    #[primary_key]
    pub door_state_id: String,
    #[index(btree)]
    pub door_definition_id: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    /// Query-safe scalar mirror of `instance_id`; zero means open world.
    #[index(btree)]
    pub instance_scope_id: u64,
    pub open_world_scene_name: String,
    pub is_open: bool,
    pub revision: u64,
    pub updated_at: Timestamp,
}

#[table(accessor = active_world_interaction, public)]
#[derive(Clone)]
pub struct ActiveWorldInteraction {
    #[primary_key]
    pub actor: Identity,
    pub action_instance_id: String,
    pub target_kind: String,
    pub target_definition_id: String,
    pub target_state_id: String,
    pub verb: String,
    pub desired_open: bool,
    pub observed_revision: u64,
    pub interaction_profile_id: String,
    pub animation_profile_id: String,
    pub progress_label_key: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    /// Query-safe scalar mirror of `instance_id`; zero means open world.
    #[index(btree)]
    pub instance_scope_id: u64,
    pub open_world_scene_name: String,
    pub interaction_anchor_x: f32,
    pub interaction_anchor_y: f32,
    pub interaction_anchor_z: f32,
    pub max_interaction_distance: f32,
    pub started_pos_x: f32,
    pub started_pos_y: f32,
    pub started_pos_z: f32,
    pub started_hp: i32,
    pub started_at: Timestamp,
    pub completes_at: Timestamp,
    #[index(btree)]
    pub completes_at_micros: i64,
    pub cancel_conditions: u32,
}

#[derive(Clone, Copy, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct ManifestVector3 {
    x: f32,
    y: f32,
    z: f32,
}

#[derive(Clone, Copy, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct DoorBlockerDefinition {
    center: ManifestVector3,
    size: ManifestVector3,
    yaw_degrees: f32,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct DoorDefinition {
    door_definition_id: String,
    world_definition_key: String,
    interaction_anchor: ManifestVector3,
    max_interaction_distance: f32,
    closed_blocker: DoorBlockerDefinition,
    default_open: bool,
    open_interaction_profile_id: String,
    close_interaction_profile_id: String,
    definition_version: u32,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct DoorManifest {
    schema_version: u32,
    world_definition_key: String,
    doors: Vec<DoorDefinition>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct WorldInteractionProfileDefinition {
    profile_id: String,
    progress_label_key: String,
    duration_ms: u64,
    animation_profile_id: String,
    requires_grounded: bool,
    requires_stationary: bool,
    cancel_conditions: u32,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct WorldInteractionProfileManifest {
    schema_version: u32,
    profiles: Vec<WorldInteractionProfileDefinition>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct DoorScope {
    world_kind: String,
    instance_id: Option<u64>,
    open_world_scene_name: String,
}

#[derive(Clone, Copy)]
struct DoorObb {
    center: [f32; 3],
    half_extents: [f32; 3],
    yaw_radians: f32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum DoorRequestRevisionGate {
    AlreadySatisfied,
    ChangeAllowed,
    Stale,
}

#[reducer]
pub fn begin_world_door_action(
    ctx: &ReducerContext,
    door_definition_id: String,
    desired_open: bool,
    observed_revision: u64,
) -> Result<(), String> {
    let actor = ctx.sender();
    let door = require_door_definition(door_definition_id.as_str())?;
    let scope = resolve_door_scope_for_actor(ctx, actor, door)?;
    let (physics, player_state) = require_live_player_rows(ctx, actor)?;

    validate_interaction_position_and_access(ctx, actor, &physics, &player_state, door, &scope)?;

    let state = materialize_door_state(ctx, door, &scope, ctx.timestamp);
    match classify_door_request_revision(
        state.is_open,
        state.revision,
        desired_open,
        observed_revision,
    ) {
        DoorRequestRevisionGate::AlreadySatisfied => return Ok(()),
        DoorRequestRevisionGate::Stale => {
            return Err(format!(
                "Door state is stale (observed {}, authoritative {})",
                observed_revision, state.revision
            ));
        }
        DoorRequestRevisionGate::ChangeAllowed => {}
    }

    if ctx
        .db
        .active_world_interaction()
        .actor()
        .find(actor)
        .is_some()
    {
        return Err("Another world interaction is already active".to_string());
    }
    if actor_has_conflicting_combat_cast(ctx, actor) {
        return Err("Cannot interact with a door while casting".to_string());
    }

    let profile_id = if desired_open {
        door.open_interaction_profile_id.as_str()
    } else {
        door.close_interaction_profile_id.as_str()
    };
    let profile = require_interaction_profile(profile_id)?;
    validate_profile_start(ctx, actor, &physics, profile)?;

    if interaction_commits_immediately(profile.duration_ms) {
        commit_door_state(ctx, state, desired_open, ctx.timestamp);
        return Ok(());
    }

    let completes_at = ctx.timestamp + Duration::from_millis(profile.duration_ms);
    let action_instance_id = format!(
        "WORLD_DOOR:{}:{}:{}",
        actor.to_hex(),
        ctx.timestamp.to_micros_since_unix_epoch(),
        state.revision
    );
    ctx.db
        .active_world_interaction()
        .insert(ActiveWorldInteraction {
            actor,
            action_instance_id,
            target_kind: "DOOR".to_string(),
            target_definition_id: door.door_definition_id.clone(),
            target_state_id: state.door_state_id,
            verb: if desired_open {
                "OPEN".to_string()
            } else {
                "CLOSE".to_string()
            },
            desired_open,
            observed_revision: state.revision,
            interaction_profile_id: profile.profile_id.clone(),
            animation_profile_id: profile.animation_profile_id.clone(),
            progress_label_key: profile.progress_label_key.clone(),
            world_kind: scope.world_kind,
            instance_id: scope.instance_id,
            instance_scope_id: scope.instance_id.unwrap_or_default(),
            open_world_scene_name: scope.open_world_scene_name,
            interaction_anchor_x: door.interaction_anchor.x,
            interaction_anchor_y: door.interaction_anchor.y,
            interaction_anchor_z: door.interaction_anchor.z,
            max_interaction_distance: door.max_interaction_distance,
            started_pos_x: physics.pos_x,
            started_pos_y: physics.pos_y,
            started_pos_z: physics.pos_z,
            started_hp: player_state.hp,
            started_at: ctx.timestamp,
            completes_at,
            completes_at_micros: completes_at.to_micros_since_unix_epoch(),
            cancel_conditions: profile.cancel_conditions,
        });
    Ok(())
}

#[reducer]
pub fn cancel_world_interaction(
    ctx: &ReducerContext,
    action_instance_id: String,
) -> Result<(), String> {
    let actor = ctx.sender();
    let Some(active) = ctx.db.active_world_interaction().actor().find(actor) else {
        return Ok(());
    };
    if active.action_instance_id != action_instance_id {
        return Err("The active world interaction has changed".to_string());
    }
    ctx.db.active_world_interaction().actor().delete(actor);
    Ok(())
}

pub(crate) fn cancel_active_world_interaction_for_actor(
    ctx: &ReducerContext,
    actor: Identity,
) -> bool {
    if ctx
        .db
        .active_world_interaction()
        .actor()
        .find(actor)
        .is_none()
    {
        return false;
    }
    ctx.db.active_world_interaction().actor().delete(actor);
    true
}

pub(crate) fn cancel_active_world_interaction_for_damage(
    ctx: &ReducerContext,
    actor: Identity,
    lethal: bool,
) -> bool {
    let Some(active) = ctx.db.active_world_interaction().actor().find(actor) else {
        return false;
    };
    if active.cancel_conditions & CANCEL_DAMAGE == 0
        && (!lethal || active.cancel_conditions & CANCEL_DEATH == 0)
    {
        return false;
    }
    ctx.db.active_world_interaction().actor().delete(actor);
    true
}

pub(crate) fn tick_world_interactions(ctx: &ReducerContext, now: Timestamp) {
    let interactions: Vec<ActiveWorldInteraction> =
        ctx.db.active_world_interaction().iter().collect();
    for active in interactions {
        if let Some(reason) = active_interaction_cancel_reason(ctx, &active) {
            log::info!(
                "[WORLD_INTERACTION_CANCEL] actor={} action={} target={} reason={}",
                active.actor.to_hex(),
                active.action_instance_id,
                active.target_definition_id,
                reason
            );
            ctx.db
                .active_world_interaction()
                .actor()
                .delete(active.actor);
            continue;
        }
        if active.completes_at > now {
            continue;
        }

        if door_definition(active.target_definition_id.as_str()).is_none() {
            ctx.db
                .active_world_interaction()
                .actor()
                .delete(active.actor);
            continue;
        }
        let Some(state) = ctx
            .db
            .world_door_state()
            .door_state_id()
            .find(active.target_state_id.clone())
        else {
            ctx.db
                .active_world_interaction()
                .actor()
                .delete(active.actor);
            continue;
        };

        commit_door_state(ctx, state, active.desired_open, now);
        ctx.db
            .active_world_interaction()
            .actor()
            .delete(active.actor);
    }
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn resolve_closed_door_movement(
    ctx: &ReducerContext,
    actor: Identity,
    start_x: f32,
    start_z: f32,
    target_x: f32,
    target_z: f32,
    radius: f32,
    foot_y: f32,
    height: f32,
) -> (f32, f32) {
    let Some(scope) = resolved_scope_for_collision_actor(ctx, actor) else {
        return (target_x, target_z);
    };

    let mut out_x = target_x;
    let mut out_z = target_z;
    for door in door_manifest()
        .doors
        .iter()
        .filter(|door| door_belongs_to_scope(door, &scope))
    {
        let (is_open, _) = effective_door_state(ctx, door, &scope);
        if is_open {
            continue;
        }
        let Some(fraction) = segment_door_movement_hit_fraction(
            door_obb(door),
            start_x,
            start_z,
            out_x,
            out_z,
            radius.max(0.0),
            foot_y,
            height.max(0.0),
        ) else {
            continue;
        };
        let safe_fraction = (fraction - COLLISION_EPSILON).max(0.0);
        out_x = start_x + (out_x - start_x) * safe_fraction;
        out_z = start_z + (out_z - start_z) * safe_fraction;
    }
    (out_x, out_z)
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn first_closed_door_hit(
    ctx: &ReducerContext,
    actor: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
) -> Option<WorldRayHit> {
    first_closed_door_hit_excluding(
        ctx, actor, start_x, start_y, start_z, end_x, end_y, end_z, radius, None,
    )
}

#[allow(clippy::too_many_arguments)]
fn first_closed_door_hit_excluding(
    ctx: &ReducerContext,
    actor: Identity,
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    excluded_definition_id: Option<&str>,
) -> Option<WorldRayHit> {
    let scope = resolved_scope_for_collision_actor(ctx, actor)?;
    let dx = end_x - start_x;
    let dy = end_y - start_y;
    let dz = end_z - start_z;
    let distance = (dx * dx + dy * dy + dz * dz).sqrt();
    if distance <= f32::EPSILON {
        return None;
    }

    let mut best: Option<WorldRayHit> = None;
    for door in door_manifest()
        .doors
        .iter()
        .filter(|door| door_belongs_to_scope(door, &scope))
    {
        if excluded_definition_id == Some(door.door_definition_id.as_str()) {
            continue;
        }
        let (is_open, _) = effective_door_state(ctx, door, &scope);
        if is_open {
            continue;
        }
        let Some(fraction) = segment_door_hit_fraction_3d(
            door_obb(door),
            [start_x, start_y, start_z],
            [end_x, end_y, end_z],
            radius.max(0.0),
        ) else {
            continue;
        };
        let hit = WorldRayHit {
            t: distance * fraction,
            x: start_x + dx * fraction,
            y: start_y + dy * fraction,
            z: start_z + dz * fraction,
        };
        if best.is_none_or(|existing| hit.t < existing.t) {
            best = Some(hit);
        }
    }
    best
}

fn active_interaction_cancel_reason(
    ctx: &ReducerContext,
    active: &ActiveWorldInteraction,
) -> Option<&'static str> {
    let Some(profile) = interaction_profile(active.interaction_profile_id.as_str()) else {
        return Some("profile_missing");
    };
    let Some(player_state) = ctx.db.player_state().player_id().find(active.actor) else {
        return Some("player_state_missing");
    };
    let Some(physics) = ctx.db.player_physics().identity().find(active.actor) else {
        return Some("player_physics_missing");
    };

    if has_cancel(active, CANCEL_DEATH) && !player_state.alive {
        return Some("death");
    }
    if profile.requires_grounded && !physics.grounded {
        return Some("not_grounded");
    }
    if has_cancel(active, CANCEL_MOVEMENT)
        && ctx
            .db
            .player_intent()
            .identity()
            .find(active.actor)
            .is_some_and(|intent| {
                intent.forward.abs() > MOVEMENT_INPUT_EPSILON
                    || intent.strafe.abs() > MOVEMENT_INPUT_EPSILON
            })
    {
        return Some("movement");
    }
    if has_cancel(active, CANCEL_DISPLACEMENT)
        && squared_distance(
            [physics.pos_x, physics.pos_y, physics.pos_z],
            [
                active.started_pos_x,
                active.started_pos_y,
                active.started_pos_z,
            ],
        ) > STATIONARY_POSITION_EPSILON * STATIONARY_POSITION_EPSILON
    {
        return Some("displacement");
    }
    if has_cancel(active, CANCEL_DAMAGE) && player_state.hp < active.started_hp {
        return Some("damage");
    }
    if has_cancel(active, CANCEL_WORLD_CHANGE)
        && !active_scope_matches_actor(ctx, active, active.actor)
    {
        return Some("world_change");
    }
    if has_cancel(active, CANCEL_CONFLICTING_COMBAT_ACTION)
        && actor_has_conflicting_combat_cast(ctx, active.actor)
    {
        return Some("combat_action");
    }

    let Some(door) = door_definition(active.target_definition_id.as_str()) else {
        return Some("target_missing");
    };
    let scope = scope_from_active(active);
    if has_cancel(active, CANCEL_TARGET_REVISION_CHANGED) {
        let (_, revision) = effective_door_state(ctx, door, &scope);
        if revision != active.observed_revision {
            return Some("target_revision");
        }
    }
    if has_cancel(active, CANCEL_RANGE_OR_LINE_ACCESS)
        && validate_interaction_position_and_access(
            ctx,
            active.actor,
            &physics,
            &player_state,
            door,
            &scope,
        )
        .is_err()
    {
        return Some("range_or_line_access");
    }
    None
}

fn validate_profile_start(
    ctx: &ReducerContext,
    actor: Identity,
    physics: &crate::player_physics::PlayerPhysics,
    profile: &WorldInteractionProfileDefinition,
) -> Result<(), String> {
    if profile.requires_grounded && !physics.grounded {
        return Err("This interaction requires the player to be grounded".to_string());
    }
    if profile.requires_stationary
        && ctx
            .db
            .player_intent()
            .identity()
            .find(actor)
            .is_some_and(|intent| {
                intent.forward.abs() > MOVEMENT_INPUT_EPSILON
                    || intent.strafe.abs() > MOVEMENT_INPUT_EPSILON
            })
    {
        return Err("Stop moving before beginning this interaction".to_string());
    }
    Ok(())
}

fn validate_interaction_position_and_access(
    ctx: &ReducerContext,
    actor: Identity,
    physics: &crate::player_physics::PlayerPhysics,
    player_state: &crate::player_state::PlayerState,
    door: &DoorDefinition,
    scope: &DoorScope,
) -> Result<(), String> {
    if !active_scope_values_match_actor(
        ctx,
        scope.world_kind.as_str(),
        scope.instance_id,
        scope.open_world_scene_name.as_str(),
        actor,
    ) {
        return Err("The door is not in the player's current world".to_string());
    }

    let start = [
        physics.pos_x,
        physics.pos_y + player_state.hit_height.max(0.0) * 0.5,
        physics.pos_z,
    ];
    let end = [
        door.interaction_anchor.x,
        door.interaction_anchor.y,
        door.interaction_anchor.z,
    ];
    let max_distance = door.max_interaction_distance.max(0.0);
    if !interaction_is_in_range(
        [physics.pos_x, physics.pos_y, physics.pos_z],
        end,
        max_distance,
    ) {
        return Err("The door is too far away".to_string());
    }

    if !line_of_access_to_door(ctx, actor, scope, door, start, end) {
        return Err("The door is not reachable from here".to_string());
    }
    Ok(())
}

fn line_of_access_to_door(
    ctx: &ReducerContext,
    actor: Identity,
    scope: &DoorScope,
    door: &DoorDefinition,
    start: [f32; 3],
    end: [f32; 3],
) -> bool {
    let delta = [end[0] - start[0], end[1] - start[1], end[2] - start[2]];
    let distance = (delta[0] * delta[0] + delta[1] * delta[1] + delta[2] * delta[2]).sqrt();
    if distance <= f32::EPSILON {
        return true;
    }
    let inverse_distance = distance.recip();
    let static_hit = raycast_world_with_layout_for_scene(
        None,
        false,
        Some(scope.open_world_scene_name.as_str()),
        WorldRaycastRequest {
            origin_x: start[0],
            origin_y: start[1],
            origin_z: start[2],
            dir_x: delta[0] * inverse_distance,
            dir_y: delta[1] * inverse_distance,
            dir_z: delta[2] * inverse_distance,
            max_distance: distance,
            radius: LINE_ACCESS_PROBE_RADIUS,
        },
    );
    if static_hit.is_some_and(|hit| hit.t < distance - LINE_ACCESS_TARGET_EPSILON) {
        return false;
    }

    if crate::world_obstacles::first_active_spell_world_obstacle_hit(
        ctx,
        actor,
        start[0],
        start[1],
        start[2],
        end[0],
        end[1],
        end[2],
        LINE_ACCESS_PROBE_RADIUS,
    )
    .is_some_and(|hit| hit.t < distance - LINE_ACCESS_TARGET_EPSILON)
    {
        return false;
    }

    first_closed_door_hit_excluding(
        ctx,
        actor,
        start[0],
        start[1],
        start[2],
        end[0],
        end[1],
        end[2],
        LINE_ACCESS_PROBE_RADIUS,
        Some(door.door_definition_id.as_str()),
    )
    .is_none()
}

fn require_live_player_rows(
    ctx: &ReducerContext,
    actor: Identity,
) -> Result<
    (
        crate::player_physics::PlayerPhysics,
        crate::player_state::PlayerState,
    ),
    String,
> {
    let physics = ctx
        .db
        .player_physics()
        .identity()
        .find(actor)
        .ok_or_else(|| "Player physics is unavailable".to_string())?;
    let state = ctx
        .db
        .player_state()
        .player_id()
        .find(actor)
        .ok_or_else(|| "Player state is unavailable".to_string())?;
    if !state.alive {
        return Err("A dead player cannot interact with a door".to_string());
    }
    Ok((physics, state))
}

fn actor_has_conflicting_combat_cast(ctx: &ReducerContext, actor: Identity) -> bool {
    ctx.db.active_cast().caster().find(actor).is_some()
        || ctx.db.pending_cast_request().caster().find(actor).is_some()
}

fn resolve_door_scope_for_actor(
    ctx: &ReducerContext,
    actor: Identity,
    door: &DoorDefinition,
) -> Result<DoorScope, String> {
    let Some(context) = resolve_player_world_context(ctx, actor) else {
        return Err("Player has no resolved world".to_string());
    };
    match context {
        ResolvedWorldContext::Open(scene)
            if door.world_definition_key == RANDOM_DUNGEON_WORLD_KEY
                && scene.eq_ignore_ascii_case(RANDOM_DUNGEON_SCENE_NAME) =>
        {
            Ok(DoorScope {
                world_kind: WORLD_KIND_OPEN.to_string(),
                instance_id: None,
                open_world_scene_name: scene,
            })
        }
        _ => Err("The requested door does not belong to the player's world".to_string()),
    }
}

fn resolved_scope_for_collision_actor(ctx: &ReducerContext, actor: Identity) -> Option<DoorScope> {
    match resolve_player_world_context(ctx, actor)? {
        ResolvedWorldContext::Open(scene)
            if scene.eq_ignore_ascii_case(RANDOM_DUNGEON_SCENE_NAME) =>
        {
            Some(DoorScope {
                world_kind: WORLD_KIND_OPEN.to_string(),
                instance_id: None,
                open_world_scene_name: scene,
            })
        }
        _ => None,
    }
}

fn active_scope_matches_actor(
    ctx: &ReducerContext,
    active: &ActiveWorldInteraction,
    actor: Identity,
) -> bool {
    active_scope_values_match_actor(
        ctx,
        active.world_kind.as_str(),
        active.instance_id,
        active.open_world_scene_name.as_str(),
        actor,
    )
}

fn active_scope_values_match_actor(
    ctx: &ReducerContext,
    world_kind: &str,
    instance_id: Option<u64>,
    open_world_scene_name: &str,
    actor: Identity,
) -> bool {
    match resolve_player_world_context(ctx, actor) {
        Some(ResolvedWorldContext::Open(scene)) => {
            world_kind == WORLD_KIND_OPEN && scene == open_world_scene_name
        }
        Some(ResolvedWorldContext::Instance(actor_instance)) => {
            world_kind == "INSTANCE" && instance_id == Some(actor_instance)
        }
        None => false,
    }
}

fn scope_from_active(active: &ActiveWorldInteraction) -> DoorScope {
    DoorScope {
        world_kind: active.world_kind.clone(),
        instance_id: active.instance_id,
        open_world_scene_name: active.open_world_scene_name.clone(),
    }
}

fn door_belongs_to_scope(door: &DoorDefinition, scope: &DoorScope) -> bool {
    door.world_definition_key == RANDOM_DUNGEON_WORLD_KEY
        && scope.world_kind == WORLD_KIND_OPEN
        && scope
            .open_world_scene_name
            .eq_ignore_ascii_case(RANDOM_DUNGEON_SCENE_NAME)
}

fn door_state_id(door: &DoorDefinition, scope: &DoorScope) -> String {
    match scope.instance_id {
        Some(instance_id) => format!("INSTANCE:{}:{}", instance_id, door.door_definition_id),
        None => format!(
            "OPEN:{}:{}",
            scope.open_world_scene_name, door.door_definition_id
        ),
    }
}

fn effective_door_state(
    ctx: &ReducerContext,
    door: &DoorDefinition,
    scope: &DoorScope,
) -> (bool, u64) {
    ctx.db
        .world_door_state()
        .door_state_id()
        .find(door_state_id(door, scope))
        .map(|state| (state.is_open, state.revision))
        .unwrap_or((door.default_open, 0))
}

fn materialize_door_state(
    ctx: &ReducerContext,
    door: &DoorDefinition,
    scope: &DoorScope,
    now: Timestamp,
) -> WorldDoorState {
    let state_id = door_state_id(door, scope);
    if let Some(state) = ctx
        .db
        .world_door_state()
        .door_state_id()
        .find(state_id.clone())
    {
        return state;
    }
    ctx.db.world_door_state().insert(WorldDoorState {
        door_state_id: state_id,
        door_definition_id: door.door_definition_id.clone(),
        world_kind: scope.world_kind.clone(),
        instance_id: scope.instance_id,
        instance_scope_id: scope.instance_id.unwrap_or_default(),
        open_world_scene_name: scope.open_world_scene_name.clone(),
        is_open: door.default_open,
        revision: 0,
        updated_at: now,
    })
}

fn commit_door_state(
    ctx: &ReducerContext,
    mut state: WorldDoorState,
    desired_open: bool,
    now: Timestamp,
) {
    if !apply_desired_door_state(&mut state.is_open, &mut state.revision, desired_open) {
        return;
    }
    state.updated_at = now;
    ctx.db.world_door_state().door_state_id().update(state);
}

fn classify_door_request_revision(
    current_open: bool,
    current_revision: u64,
    desired_open: bool,
    observed_revision: u64,
) -> DoorRequestRevisionGate {
    if current_open == desired_open {
        DoorRequestRevisionGate::AlreadySatisfied
    } else if current_revision == observed_revision {
        DoorRequestRevisionGate::ChangeAllowed
    } else {
        DoorRequestRevisionGate::Stale
    }
}

fn interaction_commits_immediately(duration_ms: u64) -> bool {
    duration_ms == 0
}

fn apply_desired_door_state(is_open: &mut bool, revision: &mut u64, desired_open: bool) -> bool {
    if *is_open == desired_open {
        return false;
    }
    *is_open = desired_open;
    *revision = revision.saturating_add(1);
    true
}

fn door_manifest() -> &'static DoorManifest {
    static MANIFEST: OnceLock<DoorManifest> = OnceLock::new();
    MANIFEST.get_or_init(|| {
        parse_door_manifest(DOOR_MANIFEST_JSON)
            .unwrap_or_else(|error| panic!("Invalid random dungeon door manifest: {error}"))
    })
}

fn interaction_profile_manifest() -> &'static WorldInteractionProfileManifest {
    static MANIFEST: OnceLock<WorldInteractionProfileManifest> = OnceLock::new();
    MANIFEST.get_or_init(|| {
        parse_interaction_profile_manifest(INTERACTION_PROFILE_MANIFEST_JSON)
            .unwrap_or_else(|error| panic!("Invalid world interaction profile manifest: {error}"))
    })
}

fn door_definition(door_definition_id: &str) -> Option<&'static DoorDefinition> {
    let normalized = normalize_id(door_definition_id);
    door_manifest()
        .doors
        .iter()
        .find(|door| door.door_definition_id == normalized)
}

fn require_door_definition(door_definition_id: &str) -> Result<&'static DoorDefinition, String> {
    door_definition(door_definition_id)
        .ok_or_else(|| format!("Unknown door definition '{}'", door_definition_id.trim()))
}

fn interaction_profile(profile_id: &str) -> Option<&'static WorldInteractionProfileDefinition> {
    let normalized = normalize_id(profile_id);
    interaction_profile_manifest()
        .profiles
        .iter()
        .find(|profile| profile.profile_id == normalized)
}

fn require_interaction_profile(
    profile_id: &str,
) -> Result<&'static WorldInteractionProfileDefinition, String> {
    interaction_profile(profile_id)
        .ok_or_else(|| format!("Unknown world interaction profile '{}'", profile_id.trim()))
}

fn parse_door_manifest(json: &str) -> Result<DoorManifest, String> {
    let mut manifest: DoorManifest =
        serde_json::from_str(json).map_err(|error| error.to_string())?;
    manifest.world_definition_key = normalize_id(manifest.world_definition_key.as_str());
    if manifest.schema_version != 1 {
        return Err(format!(
            "unsupported schema_version {}",
            manifest.schema_version
        ));
    }
    if manifest.world_definition_key != RANDOM_DUNGEON_WORLD_KEY {
        return Err(format!(
            "unexpected world_definition_key '{}'",
            manifest.world_definition_key
        ));
    }
    if manifest.doors.is_empty() {
        return Err("doors must not be empty".to_string());
    }

    let mut ids = HashSet::new();
    for door in &mut manifest.doors {
        door.door_definition_id = normalize_id(door.door_definition_id.as_str());
        door.world_definition_key = normalize_id(door.world_definition_key.as_str());
        door.open_interaction_profile_id = normalize_id(door.open_interaction_profile_id.as_str());
        door.close_interaction_profile_id =
            normalize_id(door.close_interaction_profile_id.as_str());
        if door.door_definition_id.is_empty() || !ids.insert(door.door_definition_id.clone()) {
            return Err(format!(
                "door_definition_id '{}' is empty or duplicated",
                door.door_definition_id
            ));
        }
        if door.world_definition_key != manifest.world_definition_key {
            return Err(format!(
                "door '{}' has mismatched world_definition_key '{}'",
                door.door_definition_id, door.world_definition_key
            ));
        }
        if door.definition_version == 0
            || !door.max_interaction_distance.is_finite()
            || door.max_interaction_distance <= 0.0
            || !vector_is_finite(door.interaction_anchor)
            || !vector_is_finite(door.closed_blocker.center)
            || !vector_is_finite(door.closed_blocker.size)
            || !door.closed_blocker.yaw_degrees.is_finite()
            || door.closed_blocker.size.x <= 0.0
            || door.closed_blocker.size.y <= 0.0
            || door.closed_blocker.size.z <= 0.0
        {
            return Err(format!(
                "door '{}' has invalid range, anchor, blocker, or definition version",
                door.door_definition_id
            ));
        }
        if door.open_interaction_profile_id.is_empty()
            || door.close_interaction_profile_id.is_empty()
        {
            return Err(format!(
                "door '{}' requires open and close profiles",
                door.door_definition_id
            ));
        }
    }
    Ok(manifest)
}

fn parse_interaction_profile_manifest(
    json: &str,
) -> Result<WorldInteractionProfileManifest, String> {
    let mut manifest: WorldInteractionProfileManifest =
        serde_json::from_str(json).map_err(|error| error.to_string())?;
    if manifest.schema_version != 1 {
        return Err(format!(
            "unsupported schema_version {}",
            manifest.schema_version
        ));
    }
    if manifest.profiles.is_empty() {
        return Err("profiles must not be empty".to_string());
    }

    let mut ids = HashSet::new();
    for profile in &mut manifest.profiles {
        profile.profile_id = normalize_id(profile.profile_id.as_str());
        profile.progress_label_key = normalize_id(profile.progress_label_key.as_str());
        profile.animation_profile_id = normalize_id(profile.animation_profile_id.as_str());
        if profile.profile_id.is_empty() || !ids.insert(profile.profile_id.clone()) {
            return Err(format!(
                "profile_id '{}' is empty or duplicated",
                profile.profile_id
            ));
        }
        if profile.progress_label_key.is_empty() {
            return Err(format!(
                "profile '{}' requires a progress_label_key",
                profile.profile_id
            ));
        }
        if profile.cancel_conditions & !KNOWN_CANCEL_CONDITIONS != 0 {
            return Err(format!(
                "profile '{}' contains unknown cancel-condition bits",
                profile.profile_id
            ));
        }
    }

    let profile_ids: HashSet<&str> = manifest
        .profiles
        .iter()
        .map(|profile| profile.profile_id.as_str())
        .collect();
    for door in &door_manifest().doors {
        if !profile_ids.contains(door.open_interaction_profile_id.as_str())
            || !profile_ids.contains(door.close_interaction_profile_id.as_str())
        {
            return Err(format!(
                "door '{}' references an unknown interaction profile",
                door.door_definition_id
            ));
        }
    }
    Ok(manifest)
}

fn normalize_id(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

fn vector_is_finite(value: ManifestVector3) -> bool {
    value.x.is_finite() && value.y.is_finite() && value.z.is_finite()
}

fn has_cancel(active: &ActiveWorldInteraction, condition: u32) -> bool {
    active.cancel_conditions & condition != 0
}

fn squared_distance(a: [f32; 3], b: [f32; 3]) -> f32 {
    let dx = a[0] - b[0];
    let dy = a[1] - b[1];
    let dz = a[2] - b[2];
    dx * dx + dy * dy + dz * dz
}

fn interaction_is_in_range(actor: [f32; 3], anchor: [f32; 3], max_distance: f32) -> bool {
    let max_distance = max_distance.max(0.0);
    squared_distance(actor, anchor) <= max_distance * max_distance
}

fn door_obb(door: &DoorDefinition) -> DoorObb {
    DoorObb {
        center: [
            door.closed_blocker.center.x,
            door.closed_blocker.center.y,
            door.closed_blocker.center.z,
        ],
        half_extents: [
            door.closed_blocker.size.x * 0.5,
            door.closed_blocker.size.y * 0.5,
            door.closed_blocker.size.z * 0.5,
        ],
        yaw_radians: door.closed_blocker.yaw_degrees.to_radians(),
    }
}

fn door_local_point(door: DoorObb, world: [f32; 3]) -> [f32; 3] {
    let dx = world[0] - door.center[0];
    let dz = world[2] - door.center[2];
    let cosine = door.yaw_radians.cos();
    let sine = door.yaw_radians.sin();
    [
        dx * cosine - dz * sine,
        world[1] - door.center[1],
        dx * sine + dz * cosine,
    ]
}

#[allow(clippy::too_many_arguments)]
fn segment_door_movement_hit_fraction(
    door: DoorObb,
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    radius: f32,
    foot_y: f32,
    height: f32,
) -> Option<f32> {
    let half_height = height * 0.5;
    let actor_center_y = foot_y + half_height;
    let start = door_local_point(door, [start_x, actor_center_y, start_z]);
    let end = door_local_point(door, [end_x, actor_center_y, end_z]);
    let half_extents = [
        door.half_extents[0] + radius,
        door.half_extents[1] + half_height,
        door.half_extents[2] + radius,
    ];
    if (0..3).all(|axis| start[axis].abs() <= half_extents[axis]) {
        return None;
    }
    segment_aabb_fraction(
        start,
        end,
        [-half_extents[0], -half_extents[1], -half_extents[2]],
        half_extents,
    )
}

fn segment_door_hit_fraction_3d(
    door: DoorObb,
    start: [f32; 3],
    end: [f32; 3],
    radius: f32,
) -> Option<f32> {
    let start = door_local_point(door, start);
    let end = door_local_point(door, end);
    segment_aabb_fraction(
        start,
        end,
        [
            -door.half_extents[0] - radius,
            -door.half_extents[1] - radius,
            -door.half_extents[2] - radius,
        ],
        [
            door.half_extents[0] + radius,
            door.half_extents[1] + radius,
            door.half_extents[2] + radius,
        ],
    )
}

fn segment_aabb_fraction<const N: usize>(
    start: [f32; N],
    end: [f32; N],
    min: [f32; N],
    max: [f32; N],
) -> Option<f32> {
    let mut enter = 0.0_f32;
    let mut exit = 1.0_f32;
    for axis in 0..N {
        let delta = end[axis] - start[axis];
        if delta.abs() <= f32::EPSILON {
            if start[axis] < min[axis] || start[axis] > max[axis] {
                return None;
            }
            continue;
        }
        let inverse = delta.recip();
        let mut near = (min[axis] - start[axis]) * inverse;
        let mut far = (max[axis] - start[axis]) * inverse;
        if near > far {
            std::mem::swap(&mut near, &mut far);
        }
        enter = enter.max(near);
        exit = exit.min(far);
        if enter > exit {
            return None;
        }
    }
    (enter <= 1.0 && exit >= 0.0).then_some(enter.max(0.0))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_door_obb() -> DoorObb {
        DoorObb {
            center: [0.0, 1.75, 0.0],
            half_extents: [1.775, 1.75, 0.175],
            yaw_radians: 0.0,
        }
    }

    #[test]
    fn paired_manifests_parse_and_references_resolve() {
        let doors = parse_door_manifest(DOOR_MANIFEST_JSON).expect("door manifest");
        let profiles = parse_interaction_profile_manifest(INTERACTION_PROFILE_MANIFEST_JSON)
            .expect("interaction profiles");
        // Count, not contract: the doors are regenerated from the dungeon on
        // every rebuild, so pinning the number here only guarantees the test
        // breaks the next time the dungeon is rebuilt. What this test is named
        // for is that the pair parses and every reference resolves.
        assert!(!doors.doors.is_empty());
        assert!(profiles
            .profiles
            .iter()
            .any(|profile| profile.duration_ms == 0));
        assert!(profiles
            .profiles
            .iter()
            .any(|profile| profile.duration_ms > 0));
    }

    #[test]
    fn closed_door_stops_capsule_movement_at_expanded_face() {
        let fraction = segment_door_movement_hit_fraction(
            test_door_obb(),
            0.0,
            -2.0,
            0.0,
            2.0,
            0.25,
            0.0,
            1.8,
        )
        .expect("door hit");
        assert!((fraction - 0.39375).abs() < 0.001);
    }

    #[test]
    fn open_space_parallel_to_door_does_not_hit() {
        assert!(segment_door_movement_hit_fraction(
            test_door_obb(),
            -3.0,
            -2.0,
            -3.0,
            2.0,
            0.25,
            0.0,
            1.8,
        )
        .is_none());
    }

    #[test]
    fn line_query_hits_closed_door_volume() {
        assert!(segment_door_hit_fraction_3d(
            test_door_obb(),
            [0.0, 1.2, -2.0],
            [0.0, 1.2, 2.0],
            0.05,
        )
        .is_some());
    }

    #[test]
    fn movement_starting_inside_closed_door_is_allowed_to_escape() {
        assert!(segment_door_movement_hit_fraction(
            test_door_obb(),
            0.0,
            0.0,
            0.0,
            2.0,
            0.25,
            0.0,
            1.8,
        )
        .is_none());
    }

    #[test]
    fn closed_door_blocks_reentry_after_capsule_is_clear() {
        assert!(segment_door_movement_hit_fraction(
            test_door_obb(),
            0.0,
            0.5,
            0.0,
            0.0,
            0.25,
            0.0,
            1.8,
        )
        .is_some());
    }

    #[test]
    fn desired_state_requests_are_idempotent_and_revision_guarded() {
        assert_eq!(
            classify_door_request_revision(true, 7, true, 2),
            DoorRequestRevisionGate::AlreadySatisfied
        );
        assert_eq!(
            classify_door_request_revision(true, 7, false, 7),
            DoorRequestRevisionGate::ChangeAllowed
        );
        assert_eq!(
            classify_door_request_revision(true, 7, false, 6),
            DoorRequestRevisionGate::Stale
        );
    }

    #[test]
    fn desired_state_commit_increments_revision_exactly_once() {
        let mut is_open = true;
        let mut revision = 4;
        assert!(apply_desired_door_state(&mut is_open, &mut revision, false));
        assert!(!is_open);
        assert_eq!(revision, 5);
        assert!(!apply_desired_door_state(
            &mut is_open,
            &mut revision,
            false
        ));
        assert_eq!(revision, 5);
    }

    #[test]
    fn only_zero_duration_uses_the_instant_commit_path() {
        assert!(interaction_commits_immediately(0));
        assert!(!interaction_commits_immediately(1));
        assert!(!interaction_commits_immediately(1_500));
    }

    #[test]
    fn authored_default_profile_covers_every_v1_cancel_condition() {
        let profile = parse_interaction_profile_manifest(INTERACTION_PROFILE_MANIFEST_JSON)
            .expect("interaction profiles")
            .profiles
            .into_iter()
            .find(|profile| profile.profile_id == "TIMED_HUMANOID_USE")
            .expect("timed profile");
        assert_eq!(profile.cancel_conditions, KNOWN_CANCEL_CONDITIONS);
    }

    #[test]
    fn interaction_range_includes_vertical_distance_and_is_boundary_inclusive() {
        assert!(interaction_is_in_range(
            [0.0, 0.0, 0.0],
            [0.0, 1.5, 2.0],
            2.5
        ));
        assert!(!interaction_is_in_range(
            [0.0, 0.0, 0.0],
            [0.0, 1.5, 2.01],
            2.5
        ));
    }

    #[test]
    fn random_dungeon_definitions_reject_other_world_scopes() {
        let door = &door_manifest().doors[0];
        let random_dungeon = DoorScope {
            world_kind: "OPEN".to_string(),
            instance_id: None,
            open_world_scene_name: "RandomDungeon".to_string(),
        };
        let oasis = DoorScope {
            world_kind: "OPEN".to_string(),
            instance_id: None,
            open_world_scene_name: "Oasis_Day".to_string(),
        };
        let instance = DoorScope {
            world_kind: "INSTANCE".to_string(),
            instance_id: Some(7),
            open_world_scene_name: String::new(),
        };
        assert!(door_belongs_to_scope(door, &random_dungeon));
        assert!(!door_belongs_to_scope(door, &oasis));
        assert!(!door_belongs_to_scope(door, &instance));
    }
}

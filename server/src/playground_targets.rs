use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::actor_lifecycle::{
    despawn_actor_bundle, spawn_actor_bundle, ActorDespawnOptions, ActorSpawnSpec,
    ActorWorldAssignment, ActorWorldCleanup,
};
use crate::arena::{
    open_world_scene_name_for_identity, upsert_player_open_world_scene, upsert_player_world,
};
use crate::combat::{
    new_dummy_player_state, DEFAULT_HIT_HEIGHT, DEFAULT_HIT_RADIUS, DEFAULT_MAX_HP,
};
use crate::party::{
    ensure_playground_party_member, remove_playground_party_member,
    remove_playground_party_member_and_created_party, MAX_PARTY_MEMBERS,
};

#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::player::player as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::playground_targets::playground_target as _;

pub(crate) const PLAYGROUND_TARGET_KIND_HOSTILE: &str = "HOSTILE";
pub(crate) const PLAYGROUND_TARGET_KIND_NEUTRAL: &str = "NEUTRAL";
pub(crate) const PLAYGROUND_TARGET_KIND_PARTY_MEMBER: &str = "PARTY_MEMBER";
pub(crate) const PLAYGROUND_TARGET_KIND_MOB_HOSTILE: &str = "MOB_HOSTILE";
pub(crate) const PLAYGROUND_TARGET_KIND_MOB_NEUTRAL: &str = "MOB_NEUTRAL";
pub(crate) const PLAYGROUND_TARGET_KIND_MOB_FRIENDLY: &str = "MOB_FRIENDLY";

const WORLD_KIND_OPEN: &str = "OPEN";
const WORLD_KIND_INSTANCE: &str = "INSTANCE";
const PLAYGROUND_TARGET_HP: i32 = DEFAULT_MAX_HP * 50;
const PLAYGROUND_TARGET_SPAWN_FORWARD: f32 = 2.5;
const PLAYGROUND_TARGET_MAGIC: u128 = 0x706c_6179_6772_6f75_6e64_5f746172_00;

#[table(accessor = playground_target, public)]
#[derive(Clone)]
pub struct PlaygroundTarget {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub owner: Identity,
    #[index(btree)]
    pub kind: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    pub open_world_scene_name: String,
    pub party_id: Option<u64>,
    pub created_party_for_owner: bool,
    pub spawned_at: Timestamp,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum PlaygroundTargetKind {
    Hostile,
    Neutral,
    PartyMember,
    MobHostile,
    MobNeutral,
    MobFriendly,
}

impl PlaygroundTargetKind {
    pub(crate) const ALL: [Self; 6] = [
        Self::Hostile,
        Self::Neutral,
        Self::PartyMember,
        Self::MobHostile,
        Self::MobNeutral,
        Self::MobFriendly,
    ];

    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::Hostile => PLAYGROUND_TARGET_KIND_HOSTILE,
            Self::Neutral => PLAYGROUND_TARGET_KIND_NEUTRAL,
            Self::PartyMember => PLAYGROUND_TARGET_KIND_PARTY_MEMBER,
            Self::MobHostile => PLAYGROUND_TARGET_KIND_MOB_HOSTILE,
            Self::MobNeutral => PLAYGROUND_TARGET_KIND_MOB_NEUTRAL,
            Self::MobFriendly => PLAYGROUND_TARGET_KIND_MOB_FRIENDLY,
        }
    }

    pub(crate) fn from_wire(value: &str) -> Option<Self> {
        match value.trim().to_ascii_uppercase().as_str() {
            PLAYGROUND_TARGET_KIND_HOSTILE => Some(Self::Hostile),
            PLAYGROUND_TARGET_KIND_NEUTRAL => Some(Self::Neutral),
            PLAYGROUND_TARGET_KIND_PARTY_MEMBER => Some(Self::PartyMember),
            PLAYGROUND_TARGET_KIND_MOB_HOSTILE => Some(Self::MobHostile),
            PLAYGROUND_TARGET_KIND_MOB_NEUTRAL => Some(Self::MobNeutral),
            PLAYGROUND_TARGET_KIND_MOB_FRIENDLY => Some(Self::MobFriendly),
            _ => None,
        }
    }

    fn identity_code(self, slot: u8) -> u8 {
        match self {
            Self::Hostile => 1,
            Self::Neutral => 2,
            Self::PartyMember => 4_u8.saturating_add(slot),
            Self::MobHostile => 16,
            Self::MobNeutral => 17,
            Self::MobFriendly => 18,
        }
    }

    fn label(self) -> &'static str {
        match self {
            Self::Hostile => "Hostile",
            Self::Neutral => "Neutral",
            Self::PartyMember => "Party Member",
            Self::MobHostile => "Hostile Kobold",
            Self::MobNeutral => "Neutral Kobold",
            Self::MobFriendly => "Friendly Kobold",
        }
    }
}

#[reducer]
pub fn spawn_playground_target(ctx: &ReducerContext, kind: String) -> Result<(), String> {
    let owner = ctx.sender();
    let kind = PlaygroundTargetKind::from_wire(kind.as_str())
        .ok_or_else(|| format!("Unknown playground target kind '{kind}'"))?;

    if ctx.db.player_state().player_id().find(owner).is_none() {
        return Err("Cannot spawn playground target without a player_state row".to_string());
    }

    if kind != PlaygroundTargetKind::PartyMember {
        despawn_playground_target_for_kind(ctx, owner, kind)?;
    }

    let Some(owner_physics) = ctx.db.player_physics().identity().find(owner) else {
        return Err("Cannot spawn playground target without owner physics".to_string());
    };
    let Some(owner_world) = ctx.db.player_world().identity().find(owner) else {
        return Err("Cannot spawn playground target without owner world".to_string());
    };

    let (identity, slot) = next_playground_target_identity(ctx, owner, kind)?;
    let target_yaw = wrap_yaw(owner_physics.yaw + std::f32::consts::PI);
    let spawn_x = owner_physics.pos_x + owner_physics.yaw.sin() * PLAYGROUND_TARGET_SPAWN_FORWARD;
    let spawn_z = owner_physics.pos_z + owner_physics.yaw.cos() * PLAYGROUND_TARGET_SPAWN_FORWARD;

    let is_instance = owner_world
        .world_kind
        .eq_ignore_ascii_case(WORLD_KIND_INSTANCE);
    let owner_instance_id = if is_instance {
        Some(owner_world.instance_id.ok_or_else(|| {
            "Cannot spawn playground target in instance without owner instance_id".to_string()
        })?)
    } else {
        None
    };
    let open_world_scene_name = if is_instance {
        String::new()
    } else {
        open_world_scene_name_for_identity(ctx, owner)
    };

    spawn_actor_bundle(
        ctx,
        ActorSpawnSpec {
            identity,
            username: playground_target_username(kind, slot),
            class_id: "WARRIOR".to_string(),
            pos_x: spawn_x,
            pos_y: owner_physics.pos_y,
            pos_z: spawn_z,
            yaw: target_yaw,
            vel_x: 0.0,
            vel_y: 0.0,
            vel_z: 0.0,
            grounded: true,
            last_processed_tick: 0,
            state: new_dummy_player_state(
                identity,
                ctx.timestamp,
                PLAYGROUND_TARGET_HP,
                DEFAULT_HIT_RADIUS,
                DEFAULT_HIT_HEIGHT,
            ),
            world: Some(if is_instance {
                ActorWorldAssignment::Instance(owner_instance_id.unwrap_or_default())
            } else {
                ActorWorldAssignment::Open
            }),
        },
    )?;

    if is_instance {
        upsert_player_world(ctx, identity, WORLD_KIND_INSTANCE, owner_instance_id);
    } else {
        upsert_player_open_world_scene(ctx, identity, open_world_scene_name.as_str());
        upsert_player_world(ctx, identity, WORLD_KIND_OPEN, None);
    }

    let mut party_id = None;
    let mut created_party_for_owner = false;
    if kind == PlaygroundTargetKind::PartyMember {
        match ensure_playground_party_member(ctx, owner, identity) {
            Ok(assignment) => {
                party_id = Some(assignment.party_id);
                created_party_for_owner = assignment.created_party_for_owner;
            }
            Err(error) => {
                let _ = despawn_actor_bundle(
                    ctx,
                    identity,
                    ActorDespawnOptions {
                        world_cleanup: ActorWorldCleanup::DeleteOnly,
                    },
                );
                return Err(error);
            }
        }
    }

    ctx.db.playground_target().insert(PlaygroundTarget {
        identity,
        owner,
        kind: kind.as_str().to_string(),
        world_kind: if is_instance {
            WORLD_KIND_INSTANCE.to_string()
        } else {
            WORLD_KIND_OPEN.to_string()
        },
        instance_id: owner_instance_id,
        open_world_scene_name,
        party_id,
        created_party_for_owner,
        spawned_at: ctx.timestamp,
    });

    Ok(())
}

#[reducer]
pub fn despawn_playground_target(ctx: &ReducerContext, kind: String) -> Result<(), String> {
    let owner = ctx.sender();
    let kind = PlaygroundTargetKind::from_wire(kind.as_str())
        .ok_or_else(|| format!("Unknown playground target kind '{kind}'"))?;
    despawn_playground_target_for_kind(ctx, owner, kind)
}

#[reducer]
pub fn despawn_all_playground_targets(ctx: &ReducerContext) -> Result<(), String> {
    despawn_all_playground_targets_for_owner(ctx, ctx.sender())
}

pub(crate) fn despawn_all_playground_targets_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<(), String> {
    let existing_identities: Vec<_> = ctx
        .db
        .playground_target()
        .owner()
        .filter(owner)
        .map(|row| row.identity)
        .collect();
    for identity in existing_identities {
        despawn_playground_target_identity(ctx, owner, identity)?;
    }

    for kind in PlaygroundTargetKind::ALL {
        despawn_playground_target_for_kind(ctx, owner, kind)?;
    }
    Ok(())
}

fn despawn_playground_target_for_kind(
    ctx: &ReducerContext,
    owner: Identity,
    kind: PlaygroundTargetKind,
) -> Result<(), String> {
    if kind == PlaygroundTargetKind::PartyMember {
        let identities: Vec<_> = ctx
            .db
            .playground_target()
            .owner()
            .filter(owner)
            .filter(|row| row.kind == PLAYGROUND_TARGET_KIND_PARTY_MEMBER)
            .map(|row| row.identity)
            .collect();
        for identity in identities {
            despawn_playground_target_identity(ctx, owner, identity)?;
        }

        for slot in 0..MAX_PARTY_MEMBERS {
            let identity = playground_target_identity(owner, kind, slot as u8)?;
            despawn_playground_target_identity(ctx, owner, identity)?;
        }
        return Ok(());
    }

    let identity = playground_target_identity(owner, kind, 0)?;
    despawn_playground_target_identity(ctx, owner, identity)
}

fn despawn_playground_target_identity(
    ctx: &ReducerContext,
    owner: Identity,
    identity: Identity,
) -> Result<(), String> {
    if let Some(row) = ctx.db.playground_target().identity().find(identity) {
        ctx.db.playground_target().identity().delete(identity);
        if row.kind == PLAYGROUND_TARGET_KIND_PARTY_MEMBER {
            if let Some(party_id) = row.party_id {
                remove_playground_party_member_and_created_party(
                    ctx,
                    owner,
                    identity,
                    party_id,
                    row.created_party_for_owner,
                );
            } else {
                remove_playground_party_member(ctx, identity);
            }
        }
    } else {
        remove_playground_party_member(ctx, identity);
    }
    if ctx.db.player().identity().find(identity).is_some() {
        despawn_actor_bundle(
            ctx,
            identity,
            ActorDespawnOptions {
                world_cleanup: ActorWorldCleanup::DeleteOnly,
            },
        )?;
    }
    Ok(())
}

pub(crate) fn playground_target_kind_for_relation(
    ctx: &ReducerContext,
    source: Identity,
    target: Identity,
) -> Option<PlaygroundTargetKind> {
    let row = ctx.db.playground_target().identity().find(target)?;
    let kind = PlaygroundTargetKind::from_wire(row.kind.as_str())?;
    if source == row.owner {
        return Some(kind);
    }

    if kind == PlaygroundTargetKind::PartyMember {
        return Some(kind);
    }

    Some(PlaygroundTargetKind::Neutral)
}

pub(crate) fn is_playground_target(ctx: &ReducerContext, identity: Identity) -> bool {
    ctx.db
        .playground_target()
        .identity()
        .find(identity)
        .is_some()
}

fn playground_target_identity(
    owner: Identity,
    kind: PlaygroundTargetKind,
    slot: u8,
) -> Result<Identity, String> {
    let owner_hex = owner.to_hex();
    let owner_tail = owner_hex
        .get(32..64)
        .ok_or_else(|| format!("invalid owner identity hex '{}'", owner_hex))?;
    let owner_bits = u128::from_str_radix(owner_tail, 16)
        .map_err(|error| format!("invalid owner identity tail '{}': {}", owner_tail, error))?;
    let encoded = (u128::from(kind.identity_code(slot)) << 120)
        | (owner_bits & 0x00ff_ffff_ffff_ffff_ffff_ffff_ffff_ffff);
    let hex = format!("{PLAYGROUND_TARGET_MAGIC:032x}{encoded:032x}");
    Identity::from_hex(hex.as_str()).map_err(|error| {
        format!(
            "invalid playground target identity owner={} kind={} hex={} error={}",
            owner.to_hex(),
            kind.as_str(),
            hex,
            error
        )
    })
}

fn next_playground_target_identity(
    ctx: &ReducerContext,
    owner: Identity,
    kind: PlaygroundTargetKind,
) -> Result<(Identity, u8), String> {
    if kind != PlaygroundTargetKind::PartyMember {
        return Ok((playground_target_identity(owner, kind, 0)?, 0));
    }

    for slot in 0..MAX_PARTY_MEMBERS {
        let slot = slot as u8;
        let identity = playground_target_identity(owner, kind, slot)?;
        if ctx
            .db
            .playground_target()
            .identity()
            .find(identity)
            .is_none()
            && ctx.db.player().identity().find(identity).is_none()
        {
            return Ok((identity, slot));
        }
    }

    Err("Maximum playground party members are already spawned".to_string())
}

fn playground_target_username(kind: PlaygroundTargetKind, slot: u8) -> String {
    if kind == PlaygroundTargetKind::PartyMember {
        return format!("Playground Party Member {}", u16::from(slot) + 1);
    }

    format!("Playground {}", kind.label())
}

fn wrap_yaw(yaw: f32) -> f32 {
    yaw.rem_euclid(std::f32::consts::TAU)
}

#[cfg(test)]
mod tests {
    use super::{
        playground_target_identity, PlaygroundTargetKind, PLAYGROUND_TARGET_KIND_HOSTILE,
        PLAYGROUND_TARGET_KIND_MOB_FRIENDLY, PLAYGROUND_TARGET_KIND_MOB_HOSTILE,
        PLAYGROUND_TARGET_KIND_MOB_NEUTRAL, PLAYGROUND_TARGET_KIND_NEUTRAL,
        PLAYGROUND_TARGET_KIND_PARTY_MEMBER,
    };
    use spacetimedb::Identity;

    #[test]
    fn playground_target_kind_wire_values_are_stable() {
        assert_eq!(
            PlaygroundTargetKind::Hostile.as_str(),
            PLAYGROUND_TARGET_KIND_HOSTILE
        );
        assert_eq!(
            PlaygroundTargetKind::Neutral.as_str(),
            PLAYGROUND_TARGET_KIND_NEUTRAL
        );
        assert_eq!(
            PlaygroundTargetKind::PartyMember.as_str(),
            PLAYGROUND_TARGET_KIND_PARTY_MEMBER
        );
        assert_eq!(
            PlaygroundTargetKind::MobHostile.as_str(),
            PLAYGROUND_TARGET_KIND_MOB_HOSTILE
        );
        assert_eq!(
            PlaygroundTargetKind::MobNeutral.as_str(),
            PLAYGROUND_TARGET_KIND_MOB_NEUTRAL
        );
        assert_eq!(
            PlaygroundTargetKind::MobFriendly.as_str(),
            PLAYGROUND_TARGET_KIND_MOB_FRIENDLY
        );
        assert_eq!(
            PlaygroundTargetKind::from_wire("party_member"),
            Some(PlaygroundTargetKind::PartyMember)
        );
        assert_eq!(
            PlaygroundTargetKind::from_wire("mob_hostile"),
            Some(PlaygroundTargetKind::MobHostile)
        );
    }

    #[test]
    fn playground_target_identity_is_deterministic_per_owner_and_kind() {
        let owner =
            Identity::from_hex("1111111111111111111111111111111122222222222222222222222222222222")
                .expect("test identity should parse");
        let hostile_a =
            playground_target_identity(owner, PlaygroundTargetKind::Hostile, 0).unwrap();
        let hostile_b =
            playground_target_identity(owner, PlaygroundTargetKind::Hostile, 0).unwrap();
        let neutral = playground_target_identity(owner, PlaygroundTargetKind::Neutral, 0).unwrap();
        let mob_hostile =
            playground_target_identity(owner, PlaygroundTargetKind::MobHostile, 0).unwrap();
        assert_eq!(hostile_a, hostile_b);
        assert_ne!(hostile_a, neutral);
        assert_ne!(hostile_a, mob_hostile);
    }

    #[test]
    fn playground_party_member_identities_are_deterministic_per_slot() {
        let owner =
            Identity::from_hex("1111111111111111111111111111111122222222222222222222222222222222")
                .expect("test identity should parse");
        let first =
            playground_target_identity(owner, PlaygroundTargetKind::PartyMember, 0).unwrap();
        let first_again =
            playground_target_identity(owner, PlaygroundTargetKind::PartyMember, 0).unwrap();
        let second =
            playground_target_identity(owner, PlaygroundTargetKind::PartyMember, 1).unwrap();

        assert_eq!(first, first_again);
        assert_ne!(first, second);
    }
}

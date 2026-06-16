use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::open_world_scene_name_for_identity;

#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::npcs::npc_instance as _;
#[allow(unused_imports)]
use crate::npcs::npc_physics as _;
#[allow(unused_imports)]
use crate::npcs::npc_spawn_counter as _;
#[allow(unused_imports)]
use crate::npcs::npc_state as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

pub(crate) const NPC_FACTION_HOSTILE: &str = "HOSTILE";
pub(crate) const NPC_FACTION_NEUTRAL: &str = "NEUTRAL";
pub(crate) const NPC_FACTION_FRIENDLY: &str = "FRIENDLY";

pub(crate) const NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD: &str =
    "KOBOLD_WARRIOR_RD_SWORD_SHIELD";
pub(crate) const NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR: &str = "KOBOLD_WARRIOR_GN_SPEAR";
pub(crate) const NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD: &str = "KOBOLD_THIEF_BK_DUAL_SWORD";
pub(crate) const NPC_TEMPLATE_KOBOLD_KNIGHT_RD_SWORD_SHIELD: &str =
    "KOBOLD_KNIGHT_RD_SWORD_SHIELD";

const WORLD_KIND_OPEN: &str = "OPEN";
const WORLD_KIND_INSTANCE: &str = "INSTANCE";
const NPC_SPAWN_FORWARD: f32 = 2.5;
const NPC_ID_MAGIC: u128 = 0x6172_656e_6132_5f6e_7063_0000_0000_0001;

#[table(accessor = npc_instance, public)]
#[derive(Clone)]
pub struct NpcInstance {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub spawned_by: Identity,
    #[index(btree)]
    pub template_id: String,
    pub species_id: String,
    #[index(btree)]
    pub faction: String,
    pub display_name: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    pub open_world_scene_name: String,
    pub spawned_at: Timestamp,
}

#[table(accessor = npc_state, public)]
#[derive(Clone)]
pub struct NpcState {
    #[primary_key]
    pub identity: Identity,
    #[index(btree)]
    pub alive: bool,
    pub hp: i32,
    pub max_hp: i32,
    pub hit_radius: f32,
    pub hit_height: f32,
}

#[table(accessor = npc_physics, public)]
#[derive(Clone)]
pub struct NpcPhysics {
    #[primary_key]
    pub identity: Identity,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub yaw: f32,
    pub updated_at: Timestamp,
}

#[table(accessor = npc_spawn_counter)]
pub struct NpcSpawnCounter {
    #[primary_key]
    pub owner: Identity,
    pub next_sequence: u64,
}

#[derive(Clone, Copy)]
pub(crate) struct NpcTemplate {
    pub template_id: &'static str,
    pub species_id: &'static str,
    pub display_name: &'static str,
    pub max_hp: i32,
    pub hit_radius: f32,
    pub hit_height: f32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum NpcFaction {
    Hostile,
    Neutral,
    Friendly,
}

impl NpcFaction {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::Hostile => NPC_FACTION_HOSTILE,
            Self::Neutral => NPC_FACTION_NEUTRAL,
            Self::Friendly => NPC_FACTION_FRIENDLY,
        }
    }

    pub(crate) fn from_wire(value: &str) -> Option<Self> {
        match value.trim().to_ascii_uppercase().as_str() {
            NPC_FACTION_HOSTILE => Some(Self::Hostile),
            NPC_FACTION_NEUTRAL => Some(Self::Neutral),
            NPC_FACTION_FRIENDLY => Some(Self::Friendly),
            _ => None,
        }
    }
}

pub(crate) fn npc_template(template_id: &str) -> Option<NpcTemplate> {
    match normalize_id(template_id).as_str() {
        NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD => Some(NpcTemplate {
            template_id: NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD,
            species_id: "KOBOLD_WARRIOR",
            display_name: "Kobold Warrior",
            max_hp: 500,
            hit_radius: 0.45,
            hit_height: 1.35,
        }),
        NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR => Some(NpcTemplate {
            template_id: NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR,
            species_id: "KOBOLD_WARRIOR",
            display_name: "Kobold Spearman",
            max_hp: 450,
            hit_radius: 0.45,
            hit_height: 1.35,
        }),
        NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD => Some(NpcTemplate {
            template_id: NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD,
            species_id: "KOBOLD_THIEF",
            display_name: "Kobold Thief",
            max_hp: 360,
            hit_radius: 0.4,
            hit_height: 1.25,
        }),
        NPC_TEMPLATE_KOBOLD_KNIGHT_RD_SWORD_SHIELD => Some(NpcTemplate {
            template_id: NPC_TEMPLATE_KOBOLD_KNIGHT_RD_SWORD_SHIELD,
            species_id: "KOBOLD_KNIGHT",
            display_name: "Kobold Knight",
            max_hp: 650,
            hit_radius: 0.5,
            hit_height: 1.45,
        }),
        _ => None,
    }
}

#[reducer]
pub fn spawn_npc(ctx: &ReducerContext, template_id: String, faction: String) -> Result<(), String> {
    let owner = ctx.sender();
    let template = npc_template(template_id.as_str())
        .ok_or_else(|| format!("Unknown NPC template '{template_id}'"))?;
    let faction = NpcFaction::from_wire(faction.as_str())
        .ok_or_else(|| format!("Unknown NPC faction '{faction}'"))?;

    if ctx.db.player_state().player_id().find(owner).is_none() {
        return Err("Cannot spawn NPC without a player_state row".to_string());
    }

    let Some(owner_physics) = ctx.db.player_physics().identity().find(owner) else {
        return Err("Cannot spawn NPC without owner physics".to_string());
    };
    let Some(owner_world) = ctx.db.player_world().identity().find(owner) else {
        return Err("Cannot spawn NPC without owner world".to_string());
    };

    let is_instance = owner_world
        .world_kind
        .eq_ignore_ascii_case(WORLD_KIND_INSTANCE);
    let instance_id = if is_instance {
        Some(owner_world.instance_id.ok_or_else(|| {
            "Cannot spawn NPC in instance without owner instance_id".to_string()
        })?)
    } else {
        None
    };
    let open_world_scene_name = if is_instance {
        String::new()
    } else if owner_world.world_kind.eq_ignore_ascii_case(WORLD_KIND_OPEN) {
        open_world_scene_name_for_identity(ctx, owner)
    } else {
        return Err(format!(
            "Cannot spawn NPC in unsupported owner world kind '{}'",
            owner_world.world_kind
        ));
    };

    let sequence = next_npc_sequence(ctx, owner);
    let identity = npc_identity(owner, sequence)?;
    let target_yaw = wrap_yaw(owner_physics.yaw + std::f32::consts::PI);
    let spawn_x = owner_physics.pos_x + owner_physics.yaw.sin() * NPC_SPAWN_FORWARD;
    let spawn_z = owner_physics.pos_z + owner_physics.yaw.cos() * NPC_SPAWN_FORWARD;

    ctx.db.npc_instance().insert(NpcInstance {
        identity,
        spawned_by: owner,
        template_id: template.template_id.to_string(),
        species_id: template.species_id.to_string(),
        faction: faction.as_str().to_string(),
        display_name: template.display_name.to_string(),
        world_kind: if is_instance {
            WORLD_KIND_INSTANCE.to_string()
        } else {
            WORLD_KIND_OPEN.to_string()
        },
        instance_id,
        open_world_scene_name,
        spawned_at: ctx.timestamp,
    });

    ctx.db.npc_state().insert(NpcState {
        identity,
        alive: true,
        hp: template.max_hp,
        max_hp: template.max_hp,
        hit_radius: template.hit_radius,
        hit_height: template.hit_height,
    });

    ctx.db.npc_physics().insert(NpcPhysics {
        identity,
        pos_x: spawn_x,
        pos_y: owner_physics.pos_y,
        pos_z: spawn_z,
        yaw: target_yaw,
        updated_at: ctx.timestamp,
    });

    Ok(())
}

#[reducer]
pub fn despawn_npc(ctx: &ReducerContext, identity: Identity) -> Result<(), String> {
    let owner = ctx.sender();
    let Some(row) = ctx.db.npc_instance().identity().find(identity) else {
        return Ok(());
    };

    if row.spawned_by != owner {
        return Err("Cannot despawn an NPC spawned by another identity".to_string());
    }

    despawn_npc_identity(ctx, identity);
    Ok(())
}

#[reducer]
pub fn despawn_all_npcs(ctx: &ReducerContext) -> Result<(), String> {
    let owner = ctx.sender();
    let identities: Vec<_> = ctx
        .db
        .npc_instance()
        .spawned_by()
        .filter(owner)
        .map(|row| row.identity)
        .collect();

    for identity in identities {
        despawn_npc_identity(ctx, identity);
    }

    Ok(())
}

pub(crate) fn npc_faction(ctx: &ReducerContext, identity: Identity) -> Option<NpcFaction> {
    let row = ctx.db.npc_instance().identity().find(identity)?;
    NpcFaction::from_wire(row.faction.as_str())
}

fn despawn_npc_identity(ctx: &ReducerContext, identity: Identity) {
    if ctx.db.npc_instance().identity().find(identity).is_some() {
        ctx.db.npc_instance().identity().delete(identity);
    }
    if ctx.db.npc_state().identity().find(identity).is_some() {
        ctx.db.npc_state().identity().delete(identity);
    }
    if ctx.db.npc_physics().identity().find(identity).is_some() {
        ctx.db.npc_physics().identity().delete(identity);
    }
}

fn next_npc_sequence(ctx: &ReducerContext, owner: Identity) -> u64 {
    if let Some(mut counter) = ctx.db.npc_spawn_counter().owner().find(owner) {
        let sequence = counter.next_sequence;
        counter.next_sequence = counter.next_sequence.saturating_add(1);
        ctx.db.npc_spawn_counter().owner().update(counter);
        return sequence;
    }

    ctx.db.npc_spawn_counter().insert(NpcSpawnCounter {
        owner,
        next_sequence: 1,
    });
    0
}

fn npc_identity(owner: Identity, sequence: u64) -> Result<Identity, String> {
    let owner_hex = owner.to_hex();
    let owner_tail = owner_hex
        .get(32..64)
        .ok_or_else(|| format!("invalid owner identity hex '{}'", owner_hex))?;
    let owner_bits = u128::from_str_radix(owner_tail, 16)
        .map_err(|error| format!("invalid owner identity tail '{}': {}", owner_tail, error))?;
    let sequence_bits = u128::from(sequence) & 0x0000_0000_ffff_ffff;
    let encoded = ((owner_bits & 0xffff_ffff_ffff_ffff) << 32) | sequence_bits;
    let hex = format!("{NPC_ID_MAGIC:032x}{encoded:032x}");
    Identity::from_hex(hex.as_str()).map_err(|error| {
        format!(
            "invalid NPC identity owner={} sequence={} hex={} error={}",
            owner.to_hex(),
            sequence,
            hex,
            error
        )
    })
}

fn normalize_id(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

fn wrap_yaw(yaw: f32) -> f32 {
    yaw.rem_euclid(std::f32::consts::TAU)
}

#[cfg(test)]
mod tests {
    use super::{
        npc_identity, npc_template, NpcFaction, NPC_FACTION_FRIENDLY, NPC_FACTION_HOSTILE,
        NPC_FACTION_NEUTRAL, NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD,
    };
    use spacetimedb::Identity;

    #[test]
    fn npc_faction_wire_values_are_stable() {
        assert_eq!(NpcFaction::Hostile.as_str(), NPC_FACTION_HOSTILE);
        assert_eq!(NpcFaction::Neutral.as_str(), NPC_FACTION_NEUTRAL);
        assert_eq!(NpcFaction::Friendly.as_str(), NPC_FACTION_FRIENDLY);
        assert_eq!(NpcFaction::from_wire("hostile"), Some(NpcFaction::Hostile));
        assert_eq!(NpcFaction::from_wire("party_member"), None);
    }

    #[test]
    fn kobold_template_ids_are_stable() {
        let template = npc_template(NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD).unwrap();
        assert_eq!(template.template_id, NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD);
        assert_eq!(template.species_id, "KOBOLD_WARRIOR");
    }

    #[test]
    fn npc_identity_is_deterministic_per_owner_and_sequence() {
        let owner =
            Identity::from_hex("1111111111111111111111111111111122222222222222222222222222222222")
                .expect("test identity should parse");
        let first_a = npc_identity(owner, 0).unwrap();
        let first_b = npc_identity(owner, 0).unwrap();
        let second = npc_identity(owner, 1).unwrap();

        assert_eq!(first_a, first_b);
        assert_ne!(first_a, second);
    }
}

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::{open_world_scene_name_for_identity, player_world as _};
use crate::npcs::{npc_instance as _, npc_physics as _, npc_state as _};
use crate::player::DEFAULT_COMBAT_PROFILE;
use crate::player_physics::player_physics as _;
use crate::progression::{
    runtime_class_id_for_owner, sync_active_combat_mode_for_owner, COMBAT_PROFILE_ARCHER_BOW,
};

#[allow(unused_imports)]
use crate::inventory::equipment_loadout as _;
#[allow(unused_imports)]
use crate::inventory::inventory_container as _;
#[allow(unused_imports)]
use crate::inventory::inventory_counter as _;
#[allow(unused_imports)]
use crate::inventory::inventory_slot as _;
#[allow(unused_imports)]
use crate::inventory::item_definition as _;
#[allow(unused_imports)]
use crate::inventory::item_instance as _;

pub(crate) const CONTAINER_KIND_PLAYER_BAG: &str = "PLAYER_BAG";
pub(crate) const CONTAINER_KIND_CORPSE: &str = "CORPSE";
#[allow(dead_code)]
pub(crate) const CONTAINER_KIND_CHEST: &str = "CHEST";

const CONTAINER_STATE_ACTIVE: &str = "ACTIVE";
const MAX_EQUIPMENT_PHYSICAL_RESISTANCE: f32 = 0.75;
const PLAYER_BAG_WIDTH: u32 = 10;
const PLAYER_BAG_HEIGHT: u32 = 4;
const CORPSE_CONTAINER_WIDTH: u32 = 4;
const CORPSE_CONTAINER_HEIGHT: u32 = 4;
const LOOT_INTERACT_RANGE: f32 = 3.5;

const ITEM_KIND_ARMOR: &str = "ARMOR";
const ITEM_KIND_JEWELRY: &str = "JEWELRY";
const ITEM_KIND_WEAPON: &str = "WEAPON";
const ITEM_KIND_MISC: &str = "MISC";

const EQUIP_SLOT_HEAD: &str = "HEAD";
const EQUIP_SLOT_SHOULDER: &str = "SHOULDER";
const EQUIP_SLOT_CAPE: &str = "CAPE";
const EQUIP_SLOT_CHEST: &str = "CHEST";
const EQUIP_SLOT_LEGS: &str = "LEGS";
const EQUIP_SLOT_BOOTS: &str = "BOOTS";
const EQUIP_SLOT_GLOVES: &str = "GLOVES";
const EQUIP_SLOT_RING: &str = "RING";
const EQUIP_SLOT_AMULET: &str = "AMULET";
const EQUIP_SLOT_MAIN_HAND: &str = "MAIN_HAND";
const EQUIP_SLOT_OFF_HAND: &str = "OFF_HAND";

const WEAPON_KIND_TWO_HAND_SWORD: &str = "TWO_HAND_SWORD";
const WEAPON_KIND_ONE_HAND_SWORD: &str = "ONE_HAND_SWORD";
const WEAPON_KIND_SHIELD: &str = "SHIELD";
const WEAPON_KIND_DAGGER_PAIR: &str = "DAGGER_PAIR";
const WEAPON_KIND_BOW: &str = "BOW";

const HAND_REQUIREMENT_NONE: &str = "NONE";
const HAND_REQUIREMENT_ONE_HAND: &str = "ONE_HAND";
const HAND_REQUIREMENT_TWO_HAND: &str = "TWO_HAND";
const HAND_REQUIREMENT_OFF_HAND: &str = "OFF_HAND";

const COMBAT_PROFILE_TWO_HANDED_SWORD: &str = "TWO_HANDED_SWORD";
const COMBAT_PROFILE_DAGGERS: &str = "DAGGERS";

#[table(accessor = item_definition, public)]
#[derive(Clone)]
pub struct ItemDefinition {
    #[primary_key]
    pub item_def_id: String,
    pub display_name: String,
    pub item_kind: String,
    pub rarity: String,
    pub icon_id: String,
    pub max_stack: u32,
    pub width: u32,
    pub height: u32,
    pub equip_slot: String,
    pub weapon_kind: String,
    pub hand_requirement: String,
    pub unique_equipped: bool,
    pub combat_profile_id: String,
    pub physical_resistance: f32,
}

#[table(accessor = item_instance, public)]
#[derive(Clone)]
pub struct ItemInstance {
    #[primary_key]
    pub item_instance_id: String,
    #[index(btree)]
    pub item_def_id: String,
    #[index(btree)]
    pub current_owner_key: String,
    pub current_owner: Option<Identity>,
    pub quantity: u32,
    pub created_at: Timestamp,
}

#[table(accessor = inventory_container, public)]
#[derive(Clone)]
pub struct InventoryContainer {
    #[primary_key]
    pub container_id: String,
    #[index(btree)]
    pub container_kind: String,
    #[index(btree)]
    pub owner_key: String,
    pub owner: Option<Identity>,
    #[index(btree)]
    pub anchor_key: String,
    pub anchor_identity: Option<Identity>,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    pub open_world_scene_name: String,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub width: u32,
    pub height: u32,
    pub state: String,
    pub expires_at: Option<Timestamp>,
    pub revision: u64,
    pub updated_at: Timestamp,
}

#[table(accessor = inventory_slot, public)]
#[derive(Clone)]
pub struct InventorySlot {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub container_id: String,
    #[index(btree)]
    pub item_instance_id: String,
    pub x: u32,
    pub y: u32,
    pub width: u32,
    pub height: u32,
}

#[table(accessor = equipment_loadout, public)]
#[derive(Clone)]
pub struct EquipmentLoadout {
    #[primary_key]
    pub owner: Identity,
    pub head_item_id: Option<String>,
    pub shoulder_item_id: Option<String>,
    pub cape_item_id: Option<String>,
    pub chest_item_id: Option<String>,
    pub legs_item_id: Option<String>,
    pub boots_item_id: Option<String>,
    pub gloves_item_id: Option<String>,
    pub ring_1_item_id: Option<String>,
    pub ring_2_item_id: Option<String>,
    pub amulet_item_id: Option<String>,
    pub main_hand_item_id: Option<String>,
    pub off_hand_item_id: Option<String>,
    pub revision: u64,
    pub updated_at: Timestamp,
}

#[table(accessor = inventory_counter)]
#[derive(Clone)]
pub struct InventoryCounter {
    #[primary_key]
    pub owner: Identity,
    pub next_item_sequence: u64,
}

#[derive(Clone, Copy)]
struct ItemDefinitionSpec {
    item_def_id: &'static str,
    display_name: &'static str,
    item_kind: &'static str,
    rarity: &'static str,
    icon_id: &'static str,
    max_stack: u32,
    width: u32,
    height: u32,
    equip_slot: &'static str,
    weapon_kind: &'static str,
    hand_requirement: &'static str,
    unique_equipped: bool,
    combat_profile_id: &'static str,
    physical_resistance: f32,
}

#[derive(Clone, Copy)]
struct StarterEquipmentSpec {
    slot_id: &'static str,
    item_def_id: &'static str,
}

const WARRIOR_STARTER_ARMOR: &[StarterEquipmentSpec] = &[
    starter_equipment(EQUIP_SLOT_HEAD, "IRON_HELM"),
    starter_equipment(EQUIP_SLOT_SHOULDER, "IRON_SHOULDERS"),
    starter_equipment(EQUIP_SLOT_CAPE, "TRAVELER_CAPE"),
    starter_equipment(EQUIP_SLOT_CHEST, "IRON_CHESTPLATE"),
    starter_equipment(EQUIP_SLOT_LEGS, "IRON_LEGGINGS"),
    starter_equipment(EQUIP_SLOT_BOOTS, "IRON_BOOTS"),
    starter_equipment(EQUIP_SLOT_GLOVES, "IRON_GLOVES"),
];

const PALADIN_STARTER_ARMOR: &[StarterEquipmentSpec] = &[
    starter_equipment(EQUIP_SLOT_HEAD, "GILDED_HELM"),
    starter_equipment(EQUIP_SLOT_SHOULDER, "GILDED_SHOULDERS"),
    starter_equipment(EQUIP_SLOT_CAPE, "GILDED_CAPE"),
    starter_equipment(EQUIP_SLOT_CHEST, "GILDED_CHESTPLATE"),
    starter_equipment(EQUIP_SLOT_LEGS, "GILDED_LEGGINGS"),
    starter_equipment(EQUIP_SLOT_BOOTS, "GILDED_BOOTS"),
    starter_equipment(EQUIP_SLOT_GLOVES, "GILDED_GLOVES"),
];

const RANGER_STARTER_ARMOR: &[StarterEquipmentSpec] = &[
    starter_equipment(EQUIP_SLOT_HEAD, "LEATHER_HELM"),
    starter_equipment(EQUIP_SLOT_SHOULDER, "LEATHER_SHOULDERS"),
    starter_equipment(EQUIP_SLOT_CAPE, "LEATHER_CAPE"),
    starter_equipment(EQUIP_SLOT_CHEST, "LEATHER_CHESTPIECE"),
    starter_equipment(EQUIP_SLOT_LEGS, "LEATHER_LEGGINGS"),
    starter_equipment(EQUIP_SLOT_BOOTS, "LEATHER_BOOTS"),
    starter_equipment(EQUIP_SLOT_GLOVES, "LEATHER_GLOVES"),
];

const WARRIOR_STARTER_WEAPONS: &[StarterEquipmentSpec] = &[starter_equipment(
    EQUIP_SLOT_MAIN_HAND,
    "TRAINING_TWO_HAND_SWORD",
)];

const PALADIN_STARTER_WEAPONS: &[StarterEquipmentSpec] = &[
    starter_equipment(EQUIP_SLOT_MAIN_HAND, "TRAINING_ONE_HAND_SWORD"),
    starter_equipment(EQUIP_SLOT_OFF_HAND, "TRAINING_SHIELD"),
];

const RANGER_STARTER_WEAPONS: &[StarterEquipmentSpec] =
    &[starter_equipment(EQUIP_SLOT_MAIN_HAND, "TRAINING_BOW")];

const STARTER_ITEM_DEFINITIONS: &[ItemDefinitionSpec] = &[
    armor("IRON_HELM", "Iron Helm", EQUIP_SLOT_HEAD, 0.020),
    armor(
        "IRON_SHOULDERS",
        "Iron Shoulders",
        EQUIP_SLOT_SHOULDER,
        0.030,
    ),
    armor("TRAVELER_CAPE", "Traveler Cape", EQUIP_SLOT_CAPE, 0.005),
    armor(
        "IRON_CHESTPLATE",
        "Iron Chestplate",
        EQUIP_SLOT_CHEST,
        0.060,
    ),
    armor("IRON_LEGGINGS", "Iron Leggings", EQUIP_SLOT_LEGS, 0.040),
    armor("IRON_BOOTS", "Iron Boots", EQUIP_SLOT_BOOTS, 0.020),
    armor("IRON_GLOVES", "Iron Gloves", EQUIP_SLOT_GLOVES, 0.020),
    armor("GILDED_HELM", "Gilded Helm", EQUIP_SLOT_HEAD, 0.025),
    armor(
        "GILDED_SHOULDERS",
        "Gilded Shoulders",
        EQUIP_SLOT_SHOULDER,
        0.035,
    ),
    armor("GILDED_CAPE", "Gilded Cape", EQUIP_SLOT_CAPE, 0.010),
    armor(
        "GILDED_CHESTPLATE",
        "Gilded Chestplate",
        EQUIP_SLOT_CHEST,
        0.070,
    ),
    armor("GILDED_LEGGINGS", "Gilded Leggings", EQUIP_SLOT_LEGS, 0.045),
    armor("GILDED_BOOTS", "Gilded Boots", EQUIP_SLOT_BOOTS, 0.025),
    armor("GILDED_GLOVES", "Gilded Gloves", EQUIP_SLOT_GLOVES, 0.025),
    armor("LEATHER_HELM", "Leather Helm", EQUIP_SLOT_HEAD, 0.010),
    armor(
        "LEATHER_SHOULDERS",
        "Leather Shoulders",
        EQUIP_SLOT_SHOULDER,
        0.015,
    ),
    armor("LEATHER_CAPE", "Leather Cape", EQUIP_SLOT_CAPE, 0.005),
    armor(
        "LEATHER_CHESTPIECE",
        "Leather Chestpiece",
        EQUIP_SLOT_CHEST,
        0.035,
    ),
    armor(
        "LEATHER_LEGGINGS",
        "Leather Leggings",
        EQUIP_SLOT_LEGS,
        0.025,
    ),
    armor("LEATHER_BOOTS", "Leather Boots", EQUIP_SLOT_BOOTS, 0.010),
    armor("LEATHER_GLOVES", "Leather Gloves", EQUIP_SLOT_GLOVES, 0.010),
    jewelry("BRONZE_RING", "Bronze Ring", EQUIP_SLOT_RING, true),
    jewelry("IRON_RING", "Iron Ring", EQUIP_SLOT_RING, true),
    jewelry("BRONZE_AMULET", "Bronze Amulet", EQUIP_SLOT_AMULET, false),
    weapon(
        "TRAINING_TWO_HAND_SWORD",
        "Training Two-Handed Sword",
        WEAPON_KIND_TWO_HAND_SWORD,
        HAND_REQUIREMENT_TWO_HAND,
        COMBAT_PROFILE_TWO_HANDED_SWORD,
    ),
    weapon(
        "TRAINING_ONE_HAND_SWORD",
        "Training One-Handed Sword",
        WEAPON_KIND_ONE_HAND_SWORD,
        HAND_REQUIREMENT_ONE_HAND,
        DEFAULT_COMBAT_PROFILE,
    ),
    weapon(
        "TRAINING_SHIELD",
        "Training Shield",
        WEAPON_KIND_SHIELD,
        HAND_REQUIREMENT_OFF_HAND,
        DEFAULT_COMBAT_PROFILE,
    ),
    weapon(
        "TRAINING_DAGGER_PAIR",
        "Training Daggers",
        WEAPON_KIND_DAGGER_PAIR,
        HAND_REQUIREMENT_TWO_HAND,
        COMBAT_PROFILE_DAGGERS,
    ),
    weapon(
        "TRAINING_BOW",
        "Training Bow",
        WEAPON_KIND_BOW,
        HAND_REQUIREMENT_TWO_HAND,
        COMBAT_PROFILE_ARCHER_BOW,
    ),
    ItemDefinitionSpec {
        item_def_id: "CRACKED_KOBOLD_CHARM",
        display_name: "Cracked Kobold Charm",
        item_kind: ITEM_KIND_MISC,
        rarity: "COMMON",
        icon_id: "misc_charm_cracked",
        max_stack: 10,
        width: 1,
        height: 1,
        equip_slot: "",
        weapon_kind: "",
        hand_requirement: HAND_REQUIREMENT_NONE,
        unique_equipped: false,
        combat_profile_id: "",
        physical_resistance: 0.0,
    },
];

const fn armor(
    item_def_id: &'static str,
    display_name: &'static str,
    equip_slot: &'static str,
    physical_resistance: f32,
) -> ItemDefinitionSpec {
    ItemDefinitionSpec {
        item_def_id,
        display_name,
        item_kind: ITEM_KIND_ARMOR,
        rarity: "COMMON",
        icon_id: "armor_common",
        max_stack: 1,
        width: 1,
        height: 1,
        equip_slot,
        weapon_kind: "",
        hand_requirement: HAND_REQUIREMENT_NONE,
        unique_equipped: false,
        combat_profile_id: "",
        physical_resistance,
    }
}

const fn jewelry(
    item_def_id: &'static str,
    display_name: &'static str,
    equip_slot: &'static str,
    unique_equipped: bool,
) -> ItemDefinitionSpec {
    ItemDefinitionSpec {
        item_def_id,
        display_name,
        item_kind: ITEM_KIND_JEWELRY,
        rarity: "COMMON",
        icon_id: "jewelry_common",
        max_stack: 1,
        width: 1,
        height: 1,
        equip_slot,
        weapon_kind: "",
        hand_requirement: HAND_REQUIREMENT_NONE,
        unique_equipped,
        combat_profile_id: "",
        physical_resistance: 0.0,
    }
}

const fn weapon(
    item_def_id: &'static str,
    display_name: &'static str,
    weapon_kind: &'static str,
    hand_requirement: &'static str,
    combat_profile_id: &'static str,
) -> ItemDefinitionSpec {
    ItemDefinitionSpec {
        item_def_id,
        display_name,
        item_kind: ITEM_KIND_WEAPON,
        rarity: "COMMON",
        icon_id: "weapon_common",
        max_stack: 1,
        width: 1,
        height: 2,
        equip_slot: EQUIP_SLOT_MAIN_HAND,
        weapon_kind,
        hand_requirement,
        unique_equipped: false,
        combat_profile_id,
        physical_resistance: 0.0,
    }
}

const fn starter_equipment(
    slot_id: &'static str,
    item_def_id: &'static str,
) -> StarterEquipmentSpec {
    StarterEquipmentSpec {
        slot_id,
        item_def_id,
    }
}

#[reducer]
pub fn publish_item_definitions(ctx: &ReducerContext) -> Result<(), String> {
    sync_item_definitions(ctx);
    Ok(())
}

#[reducer]
pub fn open_loot_npc(ctx: &ReducerContext, npc_identity: Identity) -> Result<(), String> {
    sync_item_definitions(ctx);

    let state = ctx
        .db
        .npc_state()
        .identity()
        .find(npc_identity)
        .ok_or_else(|| "NPC state row not found".to_string())?;
    if state.alive {
        return Err("NPC is still alive".to_string());
    }

    validate_npc_loot_access(ctx, ctx.sender(), npc_identity)?;
    create_corpse_loot_for_npc(ctx, npc_identity, ctx.sender());
    ctx.db
        .inventory_container()
        .container_id()
        .find(corpse_container_id(npc_identity))
        .ok_or_else(|| "corpse loot container was not created".to_string())?;
    Ok(())
}

fn validate_npc_loot_access(
    ctx: &ReducerContext,
    owner: Identity,
    npc_identity: Identity,
) -> Result<(), String> {
    let npc = ctx
        .db
        .npc_instance()
        .identity()
        .find(npc_identity)
        .ok_or_else(|| "NPC row not found".to_string())?;
    let npc_physics = ctx
        .db
        .npc_physics()
        .identity()
        .find(npc_identity)
        .ok_or_else(|| "NPC physics row not found".to_string())?;
    let player_world = ctx
        .db
        .player_world()
        .identity()
        .find(owner)
        .ok_or_else(|| "player has no world row".to_string())?;
    if !player_world
        .world_kind
        .eq_ignore_ascii_case(npc.world_kind.as_str())
    {
        return Err("player is not in the NPC world kind".to_string());
    }
    if player_world.world_kind.eq_ignore_ascii_case("INSTANCE") {
        if player_world.instance_id != npc.instance_id {
            return Err("player is not in the NPC instance".to_string());
        }
    } else if player_world.world_kind.eq_ignore_ascii_case("OPEN") {
        let scene_name = open_world_scene_name_for_identity(ctx, owner);
        if !scene_name.eq_ignore_ascii_case(npc.open_world_scene_name.as_str()) {
            return Err("player is not in the NPC open-world scene".to_string());
        }
    }

    let player_physics = ctx
        .db
        .player_physics()
        .identity()
        .find(owner)
        .ok_or_else(|| "player has no physics row".to_string())?;
    let dx = player_physics.pos_x - npc_physics.pos_x;
    let dy = player_physics.pos_y - npc_physics.pos_y;
    let dz = player_physics.pos_z - npc_physics.pos_z;
    let distance_sq = dx * dx + dy * dy + dz * dz;
    if distance_sq > LOOT_INTERACT_RANGE * LOOT_INTERACT_RANGE {
        return Err("player is too far from the NPC corpse".to_string());
    }
    Ok(())
}

#[reducer]
pub fn move_item(
    ctx: &ReducerContext,
    source_container_id: String,
    item_instance_id: String,
    destination_container_id: String,
    destination_x: u32,
    destination_y: u32,
    quantity: u32,
) -> Result<(), String> {
    sync_item_definitions(ctx);

    let owner = ctx.sender();
    let source_container = require_accessible_container(ctx, owner, source_container_id.as_str())?;
    let mut destination_container =
        require_accessible_container(ctx, owner, destination_container_id.as_str())?;
    let source_slot = require_container_slot(
        ctx,
        source_container.container_id.as_str(),
        item_instance_id.as_str(),
    )?;
    let mut item = require_item_instance(ctx, item_instance_id.as_str())?;
    let definition = require_item_definition(ctx, item.item_def_id.as_str())?;

    if find_equipped_slot_for_item(ctx, owner, item.item_instance_id.as_str()).is_some() {
        return Err("equipped items must be unequipped before grid movement".to_string());
    }
    if quantity == 0 || quantity > item.quantity {
        return Err(format!(
            "invalid move quantity {} for item quantity {}",
            quantity, item.quantity
        ));
    }

    validate_grid_fit(
        &destination_container,
        destination_x,
        destination_y,
        definition.width,
        definition.height,
    )?;
    validate_grid_space(
        ctx,
        destination_container.container_id.as_str(),
        destination_x,
        destination_y,
        definition.width,
        definition.height,
        Some(item.item_instance_id.as_str()),
    )?;

    if quantity == item.quantity {
        ctx.db.inventory_slot().key().delete(source_slot.key);
        upsert_inventory_slot(
            ctx,
            destination_container.container_id.as_str(),
            item.item_instance_id.as_str(),
            destination_x,
            destination_y,
            definition.width,
            definition.height,
        );
        item.current_owner_key = destination_item_owner_key(owner, &destination_container);
        item.current_owner = destination_item_owner(owner, &destination_container);
        ctx.db.item_instance().item_instance_id().update(item);
    } else {
        item.quantity -= quantity;
        ctx.db
            .item_instance()
            .item_instance_id()
            .update(item.clone());
        let split_item = ItemInstance {
            item_instance_id: next_item_instance_id(ctx, owner),
            item_def_id: item.item_def_id,
            current_owner_key: destination_item_owner_key(owner, &destination_container),
            current_owner: destination_item_owner(owner, &destination_container),
            quantity,
            created_at: ctx.timestamp,
        };
        ctx.db.item_instance().insert(split_item.clone());
        upsert_inventory_slot(
            ctx,
            destination_container.container_id.as_str(),
            split_item.item_instance_id.as_str(),
            destination_x,
            destination_y,
            definition.width,
            definition.height,
        );
    }

    touch_container(ctx, source_container);
    destination_container.revision = destination_container.revision.saturating_add(1);
    destination_container.updated_at = ctx.timestamp;
    ctx.db
        .inventory_container()
        .container_id()
        .update(destination_container);
    Ok(())
}

#[reducer]
pub fn quick_loot(
    ctx: &ReducerContext,
    source_container_id: String,
    item_instance_id: String,
) -> Result<(), String> {
    sync_item_definitions(ctx);

    let owner = ctx.sender();
    let source_container = require_accessible_container(ctx, owner, source_container_id.as_str())?;
    if source_container
        .container_kind
        .eq_ignore_ascii_case(CONTAINER_KIND_PLAYER_BAG)
    {
        return Err("quick loot source must be a loot container".to_string());
    }

    let bag = require_player_bag(ctx, owner)?;
    let item = require_item_instance(ctx, item_instance_id.as_str())?;
    let definition = require_item_definition(ctx, item.item_def_id.as_str())?;
    require_container_slot(
        ctx,
        source_container.container_id.as_str(),
        item.item_instance_id.as_str(),
    )?;
    let Some((x, y)) = first_free_position(
        ctx,
        bag.container_id.as_str(),
        bag.width,
        bag.height,
        definition.width,
        definition.height,
        None,
    ) else {
        return Err("player inventory has no free space for item".to_string());
    };

    move_item(
        ctx,
        source_container.container_id,
        item.item_instance_id,
        bag.container_id,
        x,
        y,
        item.quantity,
    )
}

#[reducer]
pub fn merge_stack(
    ctx: &ReducerContext,
    source_item_instance_id: String,
    target_item_instance_id: String,
) -> Result<(), String> {
    sync_item_definitions(ctx);

    let owner = ctx.sender();
    let mut source = require_item_instance(ctx, source_item_instance_id.as_str())?;
    let mut target = require_item_instance(ctx, target_item_instance_id.as_str())?;
    if source.item_def_id != target.item_def_id {
        return Err("only identical item definitions can be merged".to_string());
    }

    let definition = require_item_definition(ctx, source.item_def_id.as_str())?;
    if definition.max_stack <= 1 {
        return Err("item is not stackable".to_string());
    }
    if target.quantity >= definition.max_stack {
        return Err("target stack is already full".to_string());
    }

    let source_slot =
        require_slot_for_accessible_item(ctx, owner, source.item_instance_id.as_str())?;
    require_slot_for_accessible_item(ctx, owner, target.item_instance_id.as_str())?;
    let movable = source
        .quantity
        .min(definition.max_stack.saturating_sub(target.quantity));
    source.quantity -= movable;
    target.quantity += movable;
    ctx.db.item_instance().item_instance_id().update(target);
    if source.quantity == 0 {
        ctx.db.inventory_slot().key().delete(source_slot.key);
        ctx.db
            .item_instance()
            .item_instance_id()
            .delete(source.item_instance_id);
    } else {
        ctx.db.item_instance().item_instance_id().update(source);
    }
    Ok(())
}

#[reducer]
pub fn equip_item(
    ctx: &ReducerContext,
    item_instance_id: String,
    target_slot: String,
) -> Result<(), String> {
    sync_item_definitions(ctx);

    let owner = ctx.sender();
    let item = require_item_instance(ctx, item_instance_id.as_str())?;
    let definition = require_item_definition(ctx, item.item_def_id.as_str())?;
    if item.quantity != 1 {
        return Err("only single item instances can be equipped".to_string());
    }

    let source_slot = require_slot_for_accessible_item(ctx, owner, item.item_instance_id.as_str())?;
    let source_container =
        require_accessible_container(ctx, owner, source_slot.container_id.as_str())?;
    if !source_container
        .container_kind
        .eq_ignore_ascii_case(CONTAINER_KIND_PLAYER_BAG)
    {
        return Err("items must be moved into player inventory before equipping".to_string());
    }

    let (mut equipment, _) = ensure_equipment_loadout(ctx, owner);
    let normalized_slot = normalize_equipment_slot(target_slot.as_str());
    validate_equip_request(ctx, &equipment, &definition, normalized_slot.as_str())?;
    if let Some(existing_item_id) = equipment_item_at_slot(&equipment, normalized_slot.as_str()) {
        return Err(format!(
            "equipment slot '{}' is already occupied by '{}'",
            normalized_slot, existing_item_id
        ));
    }
    if definition.unique_equipped
        && is_item_definition_equipped(ctx, &equipment, definition.item_def_id.as_str())
    {
        return Err(format!(
            "item definition '{}' is unique-equipped",
            definition.item_def_id
        ));
    }

    set_equipment_slot(
        &mut equipment,
        normalized_slot.as_str(),
        Some(item.item_instance_id.clone()),
    )?;
    apply_hand_locks_for_equipped_item(&mut equipment, &definition);
    equipment.revision = equipment.revision.saturating_add(1);
    equipment.updated_at = ctx.timestamp;

    ctx.db.inventory_slot().key().delete(source_slot.key);
    let mut item = item;
    item.current_owner = Some(owner);
    ctx.db.item_instance().item_instance_id().update(item);
    ctx.db.equipment_loadout().owner().update(equipment);
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
    Ok(())
}

#[reducer]
pub fn unequip_item(
    ctx: &ReducerContext,
    source_slot: String,
    destination_container_id: String,
    destination_x: u32,
    destination_y: u32,
) -> Result<(), String> {
    sync_item_definitions(ctx);

    let owner = ctx.sender();
    let (mut equipment, _) = ensure_equipment_loadout(ctx, owner);
    let normalized_slot = normalize_equipment_slot(source_slot.as_str());
    let Some(item_instance_id) =
        equipment_item_at_slot(&equipment, normalized_slot.as_str()).cloned()
    else {
        return Err(format!("equipment slot '{}' is empty", normalized_slot));
    };
    let item = require_item_instance(ctx, item_instance_id.as_str())?;
    let definition = require_item_definition(ctx, item.item_def_id.as_str())?;
    let destination_container =
        require_accessible_container(ctx, owner, destination_container_id.as_str())?;
    if !destination_container
        .container_kind
        .eq_ignore_ascii_case(CONTAINER_KIND_PLAYER_BAG)
    {
        return Err("equipment can only be unequipped to player inventory".to_string());
    }
    validate_grid_fit(
        &destination_container,
        destination_x,
        destination_y,
        definition.width,
        definition.height,
    )?;
    validate_grid_space(
        ctx,
        destination_container.container_id.as_str(),
        destination_x,
        destination_y,
        definition.width,
        definition.height,
        None,
    )?;

    clear_equipment_references_to_item(&mut equipment, item_instance_id.as_str());
    equipment.revision = equipment.revision.saturating_add(1);
    equipment.updated_at = ctx.timestamp;
    upsert_inventory_slot(
        ctx,
        destination_container.container_id.as_str(),
        item_instance_id.as_str(),
        destination_x,
        destination_y,
        definition.width,
        definition.height,
    );
    ctx.db.equipment_loadout().owner().update(equipment);
    touch_container(ctx, destination_container);
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
    Ok(())
}

pub(crate) fn sync_item_definitions(ctx: &ReducerContext) {
    for spec in STARTER_ITEM_DEFINITIONS {
        let row = ItemDefinition {
            item_def_id: normalize_id(spec.item_def_id),
            display_name: spec.display_name.to_string(),
            item_kind: normalize_id(spec.item_kind),
            rarity: normalize_id(spec.rarity),
            icon_id: spec.icon_id.to_string(),
            max_stack: spec.max_stack.max(1),
            width: spec.width.max(1),
            height: spec.height.max(1),
            equip_slot: normalize_id(spec.equip_slot),
            weapon_kind: normalize_id(spec.weapon_kind),
            hand_requirement: normalize_id(spec.hand_requirement),
            unique_equipped: spec.unique_equipped,
            combat_profile_id: normalize_id(spec.combat_profile_id),
            physical_resistance: spec
                .physical_resistance
                .clamp(0.0, MAX_EQUIPMENT_PHYSICAL_RESISTANCE),
        };
        if ctx
            .db
            .item_definition()
            .item_def_id()
            .find(row.item_def_id.clone())
            .is_some()
        {
            ctx.db.item_definition().item_def_id().update(row);
        } else {
            ctx.db.item_definition().insert(row);
        }
    }
}

pub(crate) fn ensure_player_inventory_for_identity(ctx: &ReducerContext, owner: Identity) {
    sync_item_definitions(ctx);
    ensure_player_bag(ctx, owner);
    let (equipment, created) = ensure_equipment_loadout(ctx, owner);
    if created {
        if let Some(class_id) = runtime_class_id_for_owner(ctx, owner) {
            seed_starter_equipment_for_class(ctx, owner, equipment, class_id.as_str(), true, true);
        }
    }
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
}

pub(crate) fn ensure_starter_equipment_for_class(
    ctx: &ReducerContext,
    owner: Identity,
    class_id: &str,
) {
    sync_item_definitions(ctx);
    ensure_player_bag(ctx, owner);
    let (equipment, _) = ensure_equipment_loadout(ctx, owner);
    seed_starter_equipment_for_class(ctx, owner, equipment, class_id, true, true);
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
}

pub(crate) fn clear_inventory_for_owner(ctx: &ReducerContext, owner: Identity) {
    let container_ids: Vec<_> = ctx
        .db
        .inventory_container()
        .owner_key()
        .filter(identity_key(owner).as_str())
        .map(|row| row.container_id)
        .collect();
    for container_id in container_ids {
        delete_container_and_slots(ctx, container_id.as_str());
    }

    let item_ids: Vec<_> = ctx
        .db
        .item_instance()
        .current_owner_key()
        .filter(identity_key(owner).as_str())
        .map(|row| row.item_instance_id)
        .collect();
    for item_id in item_ids {
        if let Some(slot) = ctx
            .db
            .inventory_slot()
            .item_instance_id()
            .filter(item_id.as_str())
            .next()
        {
            ctx.db.inventory_slot().key().delete(slot.key);
        }
        ctx.db.item_instance().item_instance_id().delete(item_id);
    }

    if ctx.db.equipment_loadout().owner().find(owner).is_some() {
        ctx.db.equipment_loadout().owner().delete(owner);
    }
    if ctx.db.inventory_counter().owner().find(owner).is_some() {
        ctx.db.inventory_counter().owner().delete(owner);
    }
}

pub(crate) fn clear_loot_for_anchor(ctx: &ReducerContext, anchor_identity: Identity) {
    let container_ids: Vec<_> = ctx
        .db
        .inventory_container()
        .anchor_key()
        .filter(identity_key(anchor_identity).as_str())
        .map(|row| row.container_id)
        .collect();
    for container_id in container_ids {
        delete_container_items_and_slots(ctx, container_id.as_str());
    }
}

pub(crate) fn create_corpse_loot_for_npc(
    ctx: &ReducerContext,
    npc_identity: Identity,
    looter_hint: Identity,
) {
    sync_item_definitions(ctx);
    let Some(npc) = ctx.db.npc_instance().identity().find(npc_identity) else {
        return;
    };
    let Some(physics) = ctx.db.npc_physics().identity().find(npc_identity) else {
        return;
    };

    let container_id = corpse_container_id(npc_identity);
    if ctx
        .db
        .inventory_container()
        .container_id()
        .find(container_id.clone())
        .is_none()
    {
        ctx.db.inventory_container().insert(InventoryContainer {
            container_id: container_id.clone(),
            container_kind: CONTAINER_KIND_CORPSE.to_string(),
            owner_key: String::new(),
            owner: None,
            anchor_key: identity_key(npc_identity),
            anchor_identity: Some(npc_identity),
            world_kind: npc.world_kind,
            instance_id: npc.instance_id,
            open_world_scene_name: npc.open_world_scene_name,
            pos_x: physics.pos_x,
            pos_y: physics.pos_y,
            pos_z: physics.pos_z,
            width: CORPSE_CONTAINER_WIDTH,
            height: CORPSE_CONTAINER_HEIGHT,
            state: CONTAINER_STATE_ACTIVE.to_string(),
            expires_at: None,
            revision: 0,
            updated_at: ctx.timestamp,
        });
    }

    if ctx
        .db
        .inventory_slot()
        .container_id()
        .filter(&container_id)
        .next()
        .is_some()
    {
        return;
    }

    let counter_owner = if looter_hint == Identity::ZERO {
        npc.spawned_by
    } else {
        looter_hint
    };
    let item = ItemInstance {
        item_instance_id: next_item_instance_id(ctx, counter_owner),
        item_def_id: "CRACKED_KOBOLD_CHARM".to_string(),
        current_owner_key: String::new(),
        current_owner: None,
        quantity: 1,
        created_at: ctx.timestamp,
    };
    ctx.db.item_instance().insert(item.clone());
    upsert_inventory_slot(
        ctx,
        container_id.as_str(),
        item.item_instance_id.as_str(),
        0,
        0,
        1,
        1,
    );
}

pub(crate) fn equipment_combat_profile_id_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<String> {
    let equipment = ctx.db.equipment_loadout().owner().find(owner)?;
    let main_hand = equipment
        .main_hand_item_id
        .as_deref()
        .and_then(|item_id| item_definition_for_instance(ctx, item_id));
    let off_hand = equipment
        .off_hand_item_id
        .as_deref()
        .and_then(|item_id| item_definition_for_instance(ctx, item_id));

    if let Some(definition) = main_hand {
        let profile = normalize_id(definition.combat_profile_id.as_str());
        if !profile.is_empty() {
            return Some(profile);
        }
    }

    if off_hand
        .as_ref()
        .is_some_and(|definition| definition.weapon_kind == WEAPON_KIND_SHIELD)
    {
        return Some(DEFAULT_COMBAT_PROFILE.to_string());
    }

    None
}

pub(crate) fn physical_resistance_for_owner(ctx: &ReducerContext, owner: Identity) -> f32 {
    let Some(equipment) = ctx.db.equipment_loadout().owner().find(owner) else {
        return 0.0;
    };

    equipment_item_ids(&equipment)
        .filter_map(|item_id| item_definition_for_instance(ctx, item_id))
        .map(|definition| definition.physical_resistance.max(0.0))
        .sum::<f32>()
        .clamp(0.0, MAX_EQUIPMENT_PHYSICAL_RESISTANCE)
}

fn ensure_player_bag(ctx: &ReducerContext, owner: Identity) -> InventoryContainer {
    let container_id = player_bag_container_id(owner);
    if let Some(container) = ctx
        .db
        .inventory_container()
        .container_id()
        .find(container_id.clone())
    {
        return container;
    }

    let container = InventoryContainer {
        container_id,
        container_kind: CONTAINER_KIND_PLAYER_BAG.to_string(),
        owner_key: identity_key(owner),
        owner: Some(owner),
        anchor_key: identity_key(owner),
        anchor_identity: Some(owner),
        world_kind: String::new(),
        instance_id: None,
        open_world_scene_name: String::new(),
        pos_x: 0.0,
        pos_y: 0.0,
        pos_z: 0.0,
        width: PLAYER_BAG_WIDTH,
        height: PLAYER_BAG_HEIGHT,
        state: CONTAINER_STATE_ACTIVE.to_string(),
        expires_at: None,
        revision: 0,
        updated_at: ctx.timestamp,
    };
    ctx.db.inventory_container().insert(container.clone());
    container
}

fn ensure_equipment_loadout(ctx: &ReducerContext, owner: Identity) -> (EquipmentLoadout, bool) {
    if let Some(loadout) = ctx.db.equipment_loadout().owner().find(owner) {
        return (loadout, false);
    }

    let loadout = EquipmentLoadout {
        owner,
        head_item_id: None,
        shoulder_item_id: None,
        cape_item_id: None,
        chest_item_id: None,
        legs_item_id: None,
        boots_item_id: None,
        gloves_item_id: None,
        ring_1_item_id: None,
        ring_2_item_id: None,
        amulet_item_id: None,
        main_hand_item_id: None,
        off_hand_item_id: None,
        revision: 0,
        updated_at: ctx.timestamp,
    };
    ctx.db.equipment_loadout().insert(loadout.clone());
    (loadout, true)
}

fn seed_starter_equipment_for_class(
    ctx: &ReducerContext,
    owner: Identity,
    mut equipment: EquipmentLoadout,
    class_id: &str,
    seed_armor: bool,
    reconcile_starter_weapons: bool,
) {
    if seed_armor {
        clear_equipped_starter_armor(ctx, &mut equipment);
    }
    if reconcile_starter_weapons && equipment_weapon_slots_are_empty_or_starter(ctx, &equipment) {
        clear_equipped_starter_weapons(ctx, &mut equipment);
    }

    let armor_specs: &[StarterEquipmentSpec] = if seed_armor {
        starter_armor_equipment_for_class(class_id)
    } else {
        &[]
    };
    for spec in armor_specs
        .iter()
        .chain(starter_weapon_equipment_for_class(class_id).iter())
    {
        if equipment_item_at_slot(&equipment, spec.slot_id).is_some() {
            continue;
        }
        let item_def_id = normalize_id(spec.item_def_id);
        if ctx
            .db
            .item_definition()
            .item_def_id()
            .find(item_def_id.clone())
            .is_none()
        {
            log::warn!(
                "[INVENTORY] Starter equipment definition '{}' is missing for owner {}",
                item_def_id,
                &owner.to_hex()[..8]
            );
            continue;
        }

        let item_instance_id = next_item_instance_id(ctx, owner);
        ctx.db.item_instance().insert(ItemInstance {
            item_instance_id: item_instance_id.clone(),
            item_def_id,
            current_owner_key: identity_key(owner),
            current_owner: Some(owner),
            quantity: 1,
            created_at: ctx.timestamp,
        });
        if let Err(error) = set_equipment_slot(&mut equipment, spec.slot_id, Some(item_instance_id))
        {
            log::warn!(
                "[INVENTORY] Failed to seed starter equipment slot '{}' for owner {}: {}",
                spec.slot_id,
                &owner.to_hex()[..8],
                error
            );
        }
    }

    equipment.revision = equipment.revision.saturating_add(1);
    equipment.updated_at = ctx.timestamp;
    ctx.db.equipment_loadout().owner().update(equipment);
}

fn equipment_weapon_slots_are_empty_or_starter(
    ctx: &ReducerContext,
    equipment: &EquipmentLoadout,
) -> bool {
    [
        equipment.main_hand_item_id.as_ref(),
        equipment.off_hand_item_id.as_ref(),
    ]
    .iter()
    .all(|item_id| {
        item_id
            .as_deref()
            .and_then(|item_id| item_definition_for_instance(ctx, item_id))
            .map(|definition| is_starter_weapon_definition_id(definition.item_def_id.as_str()))
            .unwrap_or(true)
    })
}

fn clear_equipped_starter_armor(ctx: &ReducerContext, equipment: &mut EquipmentLoadout) {
    for slot_id in [
        EQUIP_SLOT_HEAD,
        EQUIP_SLOT_SHOULDER,
        EQUIP_SLOT_CAPE,
        EQUIP_SLOT_CHEST,
        EQUIP_SLOT_LEGS,
        EQUIP_SLOT_BOOTS,
        EQUIP_SLOT_GLOVES,
    ] {
        let Some(item_instance_id) = equipment_item_at_slot(equipment, slot_id).cloned() else {
            continue;
        };
        let should_clear = item_definition_for_instance(ctx, item_instance_id.as_str())
            .map(|definition| is_starter_armor_definition_id(definition.item_def_id.as_str()))
            .unwrap_or(false);
        if !should_clear {
            continue;
        }

        if let Some(slot) = ctx
            .db
            .inventory_slot()
            .item_instance_id()
            .filter(item_instance_id.as_str())
            .next()
        {
            ctx.db.inventory_slot().key().delete(slot.key);
        }
        ctx.db
            .item_instance()
            .item_instance_id()
            .delete(item_instance_id);
        let _ = set_equipment_slot(equipment, slot_id, None);
    }
}

fn clear_equipped_starter_weapons(ctx: &ReducerContext, equipment: &mut EquipmentLoadout) {
    for slot_id in [EQUIP_SLOT_MAIN_HAND, EQUIP_SLOT_OFF_HAND] {
        let Some(item_instance_id) = equipment_item_at_slot(equipment, slot_id).cloned() else {
            continue;
        };
        let should_clear = item_definition_for_instance(ctx, item_instance_id.as_str())
            .map(|definition| is_starter_weapon_definition_id(definition.item_def_id.as_str()))
            .unwrap_or(false);
        if !should_clear {
            continue;
        }

        if let Some(slot) = ctx
            .db
            .inventory_slot()
            .item_instance_id()
            .filter(item_instance_id.as_str())
            .next()
        {
            ctx.db.inventory_slot().key().delete(slot.key);
        }
        ctx.db
            .item_instance()
            .item_instance_id()
            .delete(item_instance_id);
        let _ = set_equipment_slot(equipment, slot_id, None);
    }
}

fn is_starter_armor_definition_id(item_def_id: &str) -> bool {
    let normalized = normalize_id(item_def_id);
    WARRIOR_STARTER_ARMOR
        .iter()
        .chain(PALADIN_STARTER_ARMOR.iter())
        .chain(RANGER_STARTER_ARMOR.iter())
        .any(|spec| spec.item_def_id == normalized.as_str())
}

fn is_starter_weapon_definition_id(item_def_id: &str) -> bool {
    let normalized = normalize_id(item_def_id);
    WARRIOR_STARTER_WEAPONS
        .iter()
        .chain(PALADIN_STARTER_WEAPONS.iter())
        .chain(RANGER_STARTER_WEAPONS.iter())
        .any(|spec| spec.item_def_id == normalized.as_str())
}

fn starter_armor_equipment_for_class(class_id: &str) -> &'static [StarterEquipmentSpec] {
    match normalize_id(class_id).as_str() {
        "PALADIN" => PALADIN_STARTER_ARMOR,
        "RANGER" | "HUNTER" => RANGER_STARTER_ARMOR,
        _ => WARRIOR_STARTER_ARMOR,
    }
}

fn starter_weapon_equipment_for_class(class_id: &str) -> &'static [StarterEquipmentSpec] {
    match normalize_id(class_id).as_str() {
        "PALADIN" => PALADIN_STARTER_WEAPONS,
        "RANGER" | "HUNTER" => RANGER_STARTER_WEAPONS,
        _ => WARRIOR_STARTER_WEAPONS,
    }
}

fn require_player_bag(ctx: &ReducerContext, owner: Identity) -> Result<InventoryContainer, String> {
    ctx.db
        .inventory_container()
        .container_id()
        .find(player_bag_container_id(owner))
        .ok_or_else(|| "player inventory container not found".to_string())
}

fn require_accessible_container(
    ctx: &ReducerContext,
    owner: Identity,
    container_id: &str,
) -> Result<InventoryContainer, String> {
    let container = ctx
        .db
        .inventory_container()
        .container_id()
        .find(container_id.to_string())
        .ok_or_else(|| format!("inventory container '{}' not found", container_id))?;
    if !container.state.eq_ignore_ascii_case(CONTAINER_STATE_ACTIVE) {
        return Err(format!(
            "inventory container '{}' is not active",
            container_id
        ));
    }
    if let Some(container_owner) = container.owner {
        if container_owner != owner {
            return Err("inventory container belongs to another identity".to_string());
        }
        return Ok(container);
    }

    validate_world_container_access(ctx, owner, &container)?;
    Ok(container)
}

fn validate_world_container_access(
    ctx: &ReducerContext,
    owner: Identity,
    container: &InventoryContainer,
) -> Result<(), String> {
    let player_world = ctx
        .db
        .player_world()
        .identity()
        .find(owner)
        .ok_or_else(|| "player has no world row".to_string())?;
    if !player_world
        .world_kind
        .eq_ignore_ascii_case(container.world_kind.as_str())
    {
        return Err("player is not in the container world kind".to_string());
    }
    if player_world.world_kind.eq_ignore_ascii_case("INSTANCE") {
        if player_world.instance_id != container.instance_id {
            return Err("player is not in the container instance".to_string());
        }
    } else if player_world.world_kind.eq_ignore_ascii_case("OPEN") {
        let scene_name = open_world_scene_name_for_identity(ctx, owner);
        if !scene_name.eq_ignore_ascii_case(container.open_world_scene_name.as_str()) {
            return Err("player is not in the container open-world scene".to_string());
        }
    }

    let physics = ctx
        .db
        .player_physics()
        .identity()
        .find(owner)
        .ok_or_else(|| "player has no physics row".to_string())?;
    let dx = physics.pos_x - container.pos_x;
    let dy = physics.pos_y - container.pos_y;
    let dz = physics.pos_z - container.pos_z;
    let distance_sq = dx * dx + dy * dy + dz * dz;
    if distance_sq > LOOT_INTERACT_RANGE * LOOT_INTERACT_RANGE {
        return Err("player is too far from the inventory container".to_string());
    }
    Ok(())
}

fn require_item_instance(
    ctx: &ReducerContext,
    item_instance_id: &str,
) -> Result<ItemInstance, String> {
    ctx.db
        .item_instance()
        .item_instance_id()
        .find(item_instance_id.to_string())
        .ok_or_else(|| format!("item instance '{}' not found", item_instance_id))
}

fn require_item_definition(
    ctx: &ReducerContext,
    item_def_id: &str,
) -> Result<ItemDefinition, String> {
    ctx.db
        .item_definition()
        .item_def_id()
        .find(normalize_id(item_def_id))
        .ok_or_else(|| format!("item definition '{}' not found", item_def_id))
}

fn item_definition_for_instance(
    ctx: &ReducerContext,
    item_instance_id: &str,
) -> Option<ItemDefinition> {
    let item = ctx
        .db
        .item_instance()
        .item_instance_id()
        .find(item_instance_id.to_string())?;
    ctx.db
        .item_definition()
        .item_def_id()
        .find(item.item_def_id)
}

fn require_container_slot(
    ctx: &ReducerContext,
    container_id: &str,
    item_instance_id: &str,
) -> Result<InventorySlot, String> {
    let slot = ctx
        .db
        .inventory_slot()
        .item_instance_id()
        .filter(item_instance_id)
        .next()
        .ok_or_else(|| format!("item '{}' is not in a container", item_instance_id))?;
    if slot.container_id != container_id {
        return Err(format!(
            "item '{}' is not in container '{}'",
            item_instance_id, container_id
        ));
    }
    Ok(slot)
}

fn require_slot_for_accessible_item(
    ctx: &ReducerContext,
    owner: Identity,
    item_instance_id: &str,
) -> Result<InventorySlot, String> {
    let slot = ctx
        .db
        .inventory_slot()
        .item_instance_id()
        .filter(item_instance_id)
        .next()
        .ok_or_else(|| format!("item '{}' is not in a container", item_instance_id))?;
    require_accessible_container(ctx, owner, slot.container_id.as_str())?;
    Ok(slot)
}

fn validate_grid_fit(
    container: &InventoryContainer,
    x: u32,
    y: u32,
    width: u32,
    height: u32,
) -> Result<(), String> {
    if width == 0 || height == 0 {
        return Err("item dimensions must be positive".to_string());
    }
    if x.checked_add(width)
        .is_none_or(|right| right > container.width)
        || y.checked_add(height)
            .is_none_or(|bottom| bottom > container.height)
    {
        return Err(format!(
            "item footprint {}x{} at {},{} does not fit container {}x{}",
            width, height, x, y, container.width, container.height
        ));
    }
    Ok(())
}

fn validate_grid_space(
    ctx: &ReducerContext,
    container_id: &str,
    x: u32,
    y: u32,
    width: u32,
    height: u32,
    ignore_item_instance_id: Option<&str>,
) -> Result<(), String> {
    for slot in ctx.db.inventory_slot().container_id().filter(container_id) {
        if ignore_item_instance_id.is_some_and(|ignored| ignored == slot.item_instance_id) {
            continue;
        }
        if rectangles_overlap(x, y, width, height, slot.x, slot.y, slot.width, slot.height) {
            return Err(format!(
                "destination overlaps item '{}'",
                slot.item_instance_id
            ));
        }
    }
    Ok(())
}

fn first_free_position(
    ctx: &ReducerContext,
    container_id: &str,
    container_width: u32,
    container_height: u32,
    item_width: u32,
    item_height: u32,
    ignore_item_instance_id: Option<&str>,
) -> Option<(u32, u32)> {
    if item_width > container_width || item_height > container_height {
        return None;
    }
    for y in 0..=(container_height - item_height) {
        for x in 0..=(container_width - item_width) {
            if validate_grid_space(
                ctx,
                container_id,
                x,
                y,
                item_width,
                item_height,
                ignore_item_instance_id,
            )
            .is_ok()
            {
                return Some((x, y));
            }
        }
    }
    None
}

fn rectangles_overlap(
    ax: u32,
    ay: u32,
    aw: u32,
    ah: u32,
    bx: u32,
    by: u32,
    bw: u32,
    bh: u32,
) -> bool {
    ax < bx.saturating_add(bw)
        && ax.saturating_add(aw) > bx
        && ay < by.saturating_add(bh)
        && ay.saturating_add(ah) > by
}

fn validate_equip_request(
    ctx: &ReducerContext,
    equipment: &EquipmentLoadout,
    definition: &ItemDefinition,
    target_slot: &str,
) -> Result<(), String> {
    if definition.equip_slot.is_empty() {
        return Err(format!(
            "item definition '{}' is not equippable",
            definition.item_def_id
        ));
    }

    match definition.item_kind.as_str() {
        ITEM_KIND_ARMOR => validate_slot_matches(definition.equip_slot.as_str(), target_slot),
        ITEM_KIND_JEWELRY => {
            if definition.equip_slot == EQUIP_SLOT_RING {
                if target_slot == "RING_1" || target_slot == "RING_2" {
                    Ok(())
                } else {
                    Err("ring items can only be equipped in ring slots".to_string())
                }
            } else {
                validate_slot_matches(definition.equip_slot.as_str(), target_slot)
            }
        }
        ITEM_KIND_WEAPON => validate_weapon_equip_request(ctx, equipment, definition, target_slot),
        _ => Err(format!(
            "item kind '{}' is not equippable",
            definition.item_kind
        )),
    }
}

fn validate_slot_matches(expected_slot: &str, target_slot: &str) -> Result<(), String> {
    if expected_slot == target_slot {
        Ok(())
    } else {
        Err(format!(
            "item slot '{}' cannot be equipped in '{}'",
            expected_slot, target_slot
        ))
    }
}

fn validate_weapon_equip_request(
    ctx: &ReducerContext,
    equipment: &EquipmentLoadout,
    definition: &ItemDefinition,
    target_slot: &str,
) -> Result<(), String> {
    let main_hand = equipment
        .main_hand_item_id
        .as_deref()
        .and_then(|item_id| item_definition_for_instance(ctx, item_id));
    validate_weapon_equip_request_with_main_hand(
        equipment,
        definition,
        target_slot,
        main_hand.as_ref(),
    )
}

fn validate_weapon_equip_request_with_main_hand(
    equipment: &EquipmentLoadout,
    definition: &ItemDefinition,
    target_slot: &str,
    main_hand: Option<&ItemDefinition>,
) -> Result<(), String> {
    match definition.weapon_kind.as_str() {
        WEAPON_KIND_TWO_HAND_SWORD | WEAPON_KIND_DAGGER_PAIR | WEAPON_KIND_BOW => {
            if target_slot != EQUIP_SLOT_MAIN_HAND {
                return Err("two-hand weapons must be equipped in main hand".to_string());
            }
            if equipment.off_hand_item_id.is_some() {
                return Err(
                    "two-hand weapons cannot be equipped while off hand is occupied".to_string(),
                );
            }
            Ok(())
        }
        WEAPON_KIND_ONE_HAND_SWORD => {
            if target_slot != EQUIP_SLOT_MAIN_HAND {
                return Err("one-hand swords must be equipped in main hand".to_string());
            }
            Ok(())
        }
        WEAPON_KIND_SHIELD => {
            if target_slot != EQUIP_SLOT_OFF_HAND {
                return Err("shields must be equipped in off hand".to_string());
            }
            if main_hand
                .is_some_and(|definition| definition.hand_requirement == HAND_REQUIREMENT_TWO_HAND)
            {
                return Err("shields cannot be equipped with a two-hand weapon".to_string());
            }
            Ok(())
        }
        _ => Err(format!(
            "unsupported weapon kind '{}'",
            definition.weapon_kind
        )),
    }
}

fn apply_hand_locks_for_equipped_item(
    equipment: &mut EquipmentLoadout,
    definition: &ItemDefinition,
) {
    if definition.hand_requirement == HAND_REQUIREMENT_TWO_HAND {
        equipment.off_hand_item_id = None;
    }
}

fn is_item_definition_equipped(
    ctx: &ReducerContext,
    equipment: &EquipmentLoadout,
    item_def_id: &str,
) -> bool {
    equipment_item_ids(equipment).any(|item_id| {
        ctx.db
            .item_instance()
            .item_instance_id()
            .find(item_id.to_string())
            .is_some_and(|item| item.item_def_id == item_def_id)
    })
}

fn equipment_item_at_slot<'a>(equipment: &'a EquipmentLoadout, slot: &str) -> Option<&'a String> {
    match slot {
        EQUIP_SLOT_HEAD => equipment.head_item_id.as_ref(),
        EQUIP_SLOT_SHOULDER => equipment.shoulder_item_id.as_ref(),
        EQUIP_SLOT_CAPE => equipment.cape_item_id.as_ref(),
        EQUIP_SLOT_CHEST => equipment.chest_item_id.as_ref(),
        EQUIP_SLOT_LEGS => equipment.legs_item_id.as_ref(),
        EQUIP_SLOT_BOOTS => equipment.boots_item_id.as_ref(),
        EQUIP_SLOT_GLOVES => equipment.gloves_item_id.as_ref(),
        "RING_1" => equipment.ring_1_item_id.as_ref(),
        "RING_2" => equipment.ring_2_item_id.as_ref(),
        EQUIP_SLOT_AMULET => equipment.amulet_item_id.as_ref(),
        EQUIP_SLOT_MAIN_HAND => equipment.main_hand_item_id.as_ref(),
        EQUIP_SLOT_OFF_HAND => equipment.off_hand_item_id.as_ref(),
        _ => None,
    }
}

fn set_equipment_slot(
    equipment: &mut EquipmentLoadout,
    slot: &str,
    item_instance_id: Option<String>,
) -> Result<(), String> {
    match slot {
        EQUIP_SLOT_HEAD => equipment.head_item_id = item_instance_id,
        EQUIP_SLOT_SHOULDER => equipment.shoulder_item_id = item_instance_id,
        EQUIP_SLOT_CAPE => equipment.cape_item_id = item_instance_id,
        EQUIP_SLOT_CHEST => equipment.chest_item_id = item_instance_id,
        EQUIP_SLOT_LEGS => equipment.legs_item_id = item_instance_id,
        EQUIP_SLOT_BOOTS => equipment.boots_item_id = item_instance_id,
        EQUIP_SLOT_GLOVES => equipment.gloves_item_id = item_instance_id,
        "RING_1" => equipment.ring_1_item_id = item_instance_id,
        "RING_2" => equipment.ring_2_item_id = item_instance_id,
        EQUIP_SLOT_AMULET => equipment.amulet_item_id = item_instance_id,
        EQUIP_SLOT_MAIN_HAND => equipment.main_hand_item_id = item_instance_id,
        EQUIP_SLOT_OFF_HAND => equipment.off_hand_item_id = item_instance_id,
        _ => return Err(format!("unknown equipment slot '{}'", slot)),
    }
    Ok(())
}

fn clear_equipment_references_to_item(equipment: &mut EquipmentLoadout, item_instance_id: &str) {
    for slot in [
        EQUIP_SLOT_HEAD,
        EQUIP_SLOT_SHOULDER,
        EQUIP_SLOT_CAPE,
        EQUIP_SLOT_CHEST,
        EQUIP_SLOT_LEGS,
        EQUIP_SLOT_BOOTS,
        EQUIP_SLOT_GLOVES,
        "RING_1",
        "RING_2",
        EQUIP_SLOT_AMULET,
        EQUIP_SLOT_MAIN_HAND,
        EQUIP_SLOT_OFF_HAND,
    ] {
        if equipment_item_at_slot(equipment, slot)
            .is_some_and(|equipped| equipped == item_instance_id)
        {
            let _ = set_equipment_slot(equipment, slot, None);
        }
    }
}

fn equipment_item_ids(equipment: &EquipmentLoadout) -> impl Iterator<Item = &str> {
    [
        equipment.head_item_id.as_deref(),
        equipment.shoulder_item_id.as_deref(),
        equipment.cape_item_id.as_deref(),
        equipment.chest_item_id.as_deref(),
        equipment.legs_item_id.as_deref(),
        equipment.boots_item_id.as_deref(),
        equipment.gloves_item_id.as_deref(),
        equipment.ring_1_item_id.as_deref(),
        equipment.ring_2_item_id.as_deref(),
        equipment.amulet_item_id.as_deref(),
        equipment.main_hand_item_id.as_deref(),
        equipment.off_hand_item_id.as_deref(),
    ]
    .into_iter()
    .flatten()
}

fn find_equipped_slot_for_item(
    ctx: &ReducerContext,
    owner: Identity,
    item_instance_id: &str,
) -> Option<String> {
    let equipment = ctx.db.equipment_loadout().owner().find(owner)?;
    for slot in [
        EQUIP_SLOT_HEAD,
        EQUIP_SLOT_SHOULDER,
        EQUIP_SLOT_CAPE,
        EQUIP_SLOT_CHEST,
        EQUIP_SLOT_LEGS,
        EQUIP_SLOT_BOOTS,
        EQUIP_SLOT_GLOVES,
        "RING_1",
        "RING_2",
        EQUIP_SLOT_AMULET,
        EQUIP_SLOT_MAIN_HAND,
        EQUIP_SLOT_OFF_HAND,
    ] {
        if equipment_item_at_slot(&equipment, slot)
            .is_some_and(|equipped| equipped == item_instance_id)
        {
            return Some(slot.to_string());
        }
    }
    None
}

fn normalize_equipment_slot(value: &str) -> String {
    let normalized = normalize_id(value);
    match normalized.as_str() {
        "RING1" => "RING_1".to_string(),
        "RING2" => "RING_2".to_string(),
        _ => normalized,
    }
}

fn upsert_inventory_slot(
    ctx: &ReducerContext,
    container_id: &str,
    item_instance_id: &str,
    x: u32,
    y: u32,
    width: u32,
    height: u32,
) {
    let row = InventorySlot {
        key: inventory_slot_key(container_id, item_instance_id),
        container_id: container_id.to_string(),
        item_instance_id: item_instance_id.to_string(),
        x,
        y,
        width,
        height,
    };
    if ctx
        .db
        .inventory_slot()
        .key()
        .find(row.key.clone())
        .is_some()
    {
        ctx.db.inventory_slot().key().update(row);
    } else {
        ctx.db.inventory_slot().insert(row);
    }
}

fn touch_container(ctx: &ReducerContext, mut container: InventoryContainer) {
    container.revision = container.revision.saturating_add(1);
    container.updated_at = ctx.timestamp;
    ctx.db
        .inventory_container()
        .container_id()
        .update(container);
}

fn destination_item_owner(
    owner: Identity,
    destination_container: &InventoryContainer,
) -> Option<Identity> {
    if destination_container
        .container_kind
        .eq_ignore_ascii_case(CONTAINER_KIND_PLAYER_BAG)
    {
        Some(owner)
    } else {
        None
    }
}

fn destination_item_owner_key(
    owner: Identity,
    destination_container: &InventoryContainer,
) -> String {
    destination_item_owner(owner, destination_container)
        .map(identity_key)
        .unwrap_or_default()
}

fn delete_container_and_slots(ctx: &ReducerContext, container_id: &str) {
    let slot_keys: Vec<_> = ctx
        .db
        .inventory_slot()
        .container_id()
        .filter(container_id)
        .map(|slot| slot.key)
        .collect();
    for key in slot_keys {
        ctx.db.inventory_slot().key().delete(key);
    }
    if ctx
        .db
        .inventory_container()
        .container_id()
        .find(container_id.to_string())
        .is_some()
    {
        ctx.db
            .inventory_container()
            .container_id()
            .delete(container_id.to_string());
    }
}

fn delete_container_items_and_slots(ctx: &ReducerContext, container_id: &str) {
    let item_ids: Vec<_> = ctx
        .db
        .inventory_slot()
        .container_id()
        .filter(container_id)
        .map(|slot| item_instance_id_from_slot(slot))
        .collect();
    delete_container_and_slots(ctx, container_id);
    for item_id in item_ids {
        if ctx
            .db
            .item_instance()
            .item_instance_id()
            .find(item_id.clone())
            .is_some()
        {
            ctx.db.item_instance().item_instance_id().delete(item_id);
        }
    }
}

fn item_instance_id_from_slot(slot: InventorySlot) -> String {
    slot.item_instance_id
}

fn next_item_instance_id(ctx: &ReducerContext, owner: Identity) -> String {
    let mut counter = ctx
        .db
        .inventory_counter()
        .owner()
        .find(owner)
        .unwrap_or(InventoryCounter {
            owner,
            next_item_sequence: 1,
        });

    if counter.next_item_sequence == 0 {
        counter.next_item_sequence = 1;
    }

    loop {
        let sequence = counter.next_item_sequence;
        let id = format!("item:{}:{}", owner.to_hex(), sequence);
        counter.next_item_sequence = counter.next_item_sequence.checked_add(1).unwrap_or(1);
        if ctx
            .db
            .item_instance()
            .item_instance_id()
            .find(id.clone())
            .is_some()
        {
            continue;
        }

        if ctx.db.inventory_counter().owner().find(owner).is_some() {
            ctx.db.inventory_counter().owner().update(counter);
        } else {
            ctx.db.inventory_counter().insert(counter);
        }
        return id;
    }
}

fn player_bag_container_id(owner: Identity) -> String {
    format!("player:{}:bag:0", owner.to_hex())
}

fn corpse_container_id(npc_identity: Identity) -> String {
    format!("corpse:{}", npc_identity.to_hex())
}

fn inventory_slot_key(container_id: &str, item_instance_id: &str) -> String {
    format!("{container_id}:{item_instance_id}")
}

fn normalize_id(value: &str) -> String {
    value
        .trim()
        .replace('-', "_")
        .replace(' ', "_")
        .to_ascii_uppercase()
}

fn identity_key(identity: Identity) -> String {
    identity.to_hex().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn empty_equipment() -> EquipmentLoadout {
        EquipmentLoadout {
            owner: Identity::ZERO,
            head_item_id: None,
            shoulder_item_id: None,
            cape_item_id: None,
            chest_item_id: None,
            legs_item_id: None,
            boots_item_id: None,
            gloves_item_id: None,
            ring_1_item_id: None,
            ring_2_item_id: None,
            amulet_item_id: None,
            main_hand_item_id: None,
            off_hand_item_id: None,
            revision: 0,
            updated_at: Timestamp::UNIX_EPOCH,
        }
    }

    fn item_definition(weapon_kind: &str, hand_requirement: &str) -> ItemDefinition {
        ItemDefinition {
            item_def_id: "TEST".to_string(),
            display_name: "Test".to_string(),
            item_kind: ITEM_KIND_WEAPON.to_string(),
            rarity: "COMMON".to_string(),
            icon_id: String::new(),
            max_stack: 1,
            width: 1,
            height: 1,
            equip_slot: EQUIP_SLOT_MAIN_HAND.to_string(),
            weapon_kind: weapon_kind.to_string(),
            hand_requirement: hand_requirement.to_string(),
            unique_equipped: false,
            combat_profile_id: String::new(),
            physical_resistance: 0.0,
        }
    }

    #[test]
    fn grid_rectangles_detect_overlap() {
        assert!(rectangles_overlap(0, 0, 2, 2, 1, 1, 1, 1));
        assert!(!rectangles_overlap(0, 0, 1, 1, 1, 0, 1, 1));
        assert!(!rectangles_overlap(0, 0, 1, 1, 0, 1, 1, 1));
    }

    #[test]
    fn shield_can_be_equipped_alone() {
        let equipment = empty_equipment();
        let shield = item_definition(WEAPON_KIND_SHIELD, HAND_REQUIREMENT_OFF_HAND);
        assert!(validate_weapon_equip_request_with_main_hand(
            &equipment,
            &shield,
            EQUIP_SLOT_OFF_HAND,
            None
        )
        .is_ok());
    }

    #[test]
    fn shield_cannot_pair_with_two_hand_weapon() {
        let mut equipment = empty_equipment();
        equipment.main_hand_item_id = Some("greatsword".to_string());
        let shield = item_definition(WEAPON_KIND_SHIELD, HAND_REQUIREMENT_OFF_HAND);
        let greatsword = item_definition(WEAPON_KIND_TWO_HAND_SWORD, HAND_REQUIREMENT_TWO_HAND);
        assert!(validate_weapon_equip_request_with_main_hand(
            &equipment,
            &shield,
            EQUIP_SLOT_OFF_HAND,
            Some(&greatsword)
        )
        .is_err());
    }

    #[test]
    fn two_hand_weapon_requires_empty_off_hand() {
        let mut equipment = empty_equipment();
        equipment.off_hand_item_id = Some("shield".to_string());
        let sword = item_definition(WEAPON_KIND_TWO_HAND_SWORD, HAND_REQUIREMENT_TWO_HAND);
        assert!(validate_weapon_equip_request_with_main_hand(
            &equipment,
            &sword,
            EQUIP_SLOT_MAIN_HAND,
            None
        )
        .is_err());
    }

    #[test]
    fn dagger_pair_is_two_hand_main_hand_weapon() {
        let equipment = empty_equipment();
        let daggers = item_definition(WEAPON_KIND_DAGGER_PAIR, HAND_REQUIREMENT_TWO_HAND);
        assert!(validate_weapon_equip_request_with_main_hand(
            &equipment,
            &daggers,
            EQUIP_SLOT_MAIN_HAND,
            None
        )
        .is_ok());
        assert!(validate_weapon_equip_request_with_main_hand(
            &equipment,
            &daggers,
            EQUIP_SLOT_OFF_HAND,
            None
        )
        .is_err());
    }

    #[test]
    fn starter_weapon_mapping_matches_class_equipment_contract() {
        assert_eq!(starter_weapon_equipment_for_class("WARRIOR").len(), 1);
        assert_eq!(
            starter_weapon_equipment_for_class("WARRIOR")[0].item_def_id,
            "TRAINING_TWO_HAND_SWORD"
        );
        assert_eq!(
            starter_weapon_equipment_for_class("WARRIOR")[0].slot_id,
            EQUIP_SLOT_MAIN_HAND
        );

        assert_eq!(starter_weapon_equipment_for_class("PALADIN").len(), 2);
        assert_eq!(
            starter_weapon_equipment_for_class("PALADIN")[0].item_def_id,
            "TRAINING_ONE_HAND_SWORD"
        );
        assert_eq!(
            starter_weapon_equipment_for_class("PALADIN")[1].item_def_id,
            "TRAINING_SHIELD"
        );

        assert_eq!(starter_weapon_equipment_for_class("RANGER").len(), 1);
        assert_eq!(
            starter_weapon_equipment_for_class("RANGER")[0].item_def_id,
            "TRAINING_BOW"
        );
    }

    #[test]
    fn starter_armor_mapping_matches_class_equipment_contract() {
        assert_eq!(starter_armor_equipment_for_class("WARRIOR").len(), 7);
        assert_eq!(
            starter_armor_equipment_for_class("WARRIOR")[0].item_def_id,
            "IRON_HELM"
        );

        assert_eq!(starter_armor_equipment_for_class("PALADIN").len(), 7);
        assert_eq!(
            starter_armor_equipment_for_class("PALADIN")[0].item_def_id,
            "GILDED_HELM"
        );

        assert_eq!(starter_armor_equipment_for_class("RANGER").len(), 7);
        assert_eq!(
            starter_armor_equipment_for_class("RANGER")[0].item_def_id,
            "LEATHER_HELM"
        );
        assert_eq!(
            starter_armor_equipment_for_class("HUNTER")[0].item_def_id,
            "LEATHER_HELM"
        );
    }

    #[test]
    fn starter_armor_authors_physical_resistance_by_item() {
        fn starter_set_physical_resistance(class_id: &str) -> f32 {
            starter_armor_equipment_for_class(class_id)
                .iter()
                .map(|equipment| {
                    STARTER_ITEM_DEFINITIONS
                        .iter()
                        .find(|definition| definition.item_def_id == equipment.item_def_id)
                        .expect("starter equipment should have an item definition")
                        .physical_resistance
                })
                .sum()
        }

        let warrior = starter_set_physical_resistance("WARRIOR");
        let paladin = starter_set_physical_resistance("PALADIN");
        let ranger = starter_set_physical_resistance("RANGER");

        assert!((warrior - 0.195).abs() < 0.0001);
        assert!((paladin - 0.235).abs() < 0.0001);
        assert!((ranger - 0.110).abs() < 0.0001);
        assert!(ranger < warrior);
        assert!(warrior < paladin);
    }

    #[test]
    fn starter_item_classifiers_only_match_authored_starters() {
        assert!(is_starter_weapon_definition_id("training_two_hand_sword"));
        assert!(is_starter_weapon_definition_id("TRAINING_SHIELD"));
        assert!(is_starter_weapon_definition_id("training-bow"));
        assert!(!is_starter_weapon_definition_id("EPIC_PLAYER_SWORD"));

        assert!(is_starter_armor_definition_id("iron_helm"));
        assert!(is_starter_armor_definition_id("gilded_helm"));
        assert!(is_starter_armor_definition_id("leather_helm"));
        assert!(is_starter_armor_definition_id("traveler-cape"));
        assert!(!is_starter_armor_definition_id("EPIC_PLAYER_HELM"));
    }
}

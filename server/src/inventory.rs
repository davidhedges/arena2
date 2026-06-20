use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::{open_world_scene_name_for_identity, player_world as _};
use crate::combat::{queue_effects, EffectPacket};
use crate::npcs::{npc_instance as _, npc_physics as _, npc_state as _};
use crate::player::DEFAULT_COMBAT_PROFILE;
use crate::player_physics::player_physics as _;
use crate::player_state::player_state as _;
use crate::progression::{
    runtime_class_id_for_owner, sync_active_combat_mode_for_owner, COMBAT_PROFILE_ARCHER_BOW,
};
use crate::relations::TargetAudience;

#[allow(unused_imports)]
use crate::inventory::equipment_loadout as _;
#[allow(unused_imports)]
use crate::inventory::equipment_periodic_runtime as _;
#[allow(unused_imports)]
use crate::inventory::inventory_container as _;
#[allow(unused_imports)]
use crate::inventory::inventory_counter as _;
#[allow(unused_imports)]
use crate::inventory::inventory_slot as _;
#[allow(unused_imports)]
use crate::inventory::item_affix_definition as _;
#[allow(unused_imports)]
use crate::inventory::item_affix_instance as _;
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
const MAX_EQUIPMENT_MAGIC_RESISTANCE: f32 = 0.75;
const MAX_EQUIPMENT_SPECIFIC_MAGIC_RESISTANCE: f32 = 0.75;
const MAX_EQUIPMENT_DAMAGE_BONUS: f32 = 5.0;
const MAX_EQUIPMENT_CRIT_CHANCE_BONUS: f32 = 1.0;
const MAX_EQUIPMENT_MOVE_SPEED_BONUS: f32 = 3.0;
const MAX_EQUIPMENT_RESOURCE_REGEN: f32 = 100.0;
const MAX_EQUIPMENT_HEALTH_REGEN: f32 = 100.0;
const MAX_EQUIPMENT_STEAL_RATIO: f32 = 1.0;
const MAX_ROLLED_ITEM_AFFIXES: usize = 3;
const BASE_NPC_EQUIPMENT_DROP_CHANCE: f32 = 0.12;
const HIDDEN_LOOT_QUALITY_DROP_SCALAR: f32 = 0.08;
const HIDDEN_LOOT_QUALITY_AFFIX_SCALAR: f32 = 0.16;
const GLOBAL_LOOT_QUANTITY_MODIFIER: f32 = 0.0;
const GLOBAL_LOOT_QUALITY_MODIFIER: f32 = 0.0;
const LOOT_ITEM_KIND_ARMOR_WEIGHT: f32 = 78.0;
const LOOT_ITEM_KIND_JEWELRY_WEIGHT: f32 = 12.0;
const LOOT_ITEM_KIND_WEAPON_WEIGHT: f32 = 10.0;
const CORPSE_LOOT_CLUSTER_RANGE: f32 = LOOT_INTERACT_RANGE;
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

pub(crate) const MODIFIER_PHYSICAL_RESISTANCE: &str = "PHYSICAL_RESISTANCE";
pub(crate) const MODIFIER_MAGIC_RESISTANCE: &str = "MAGIC_RESISTANCE";
pub(crate) const MODIFIER_FIRE_RESISTANCE: &str = "FIRE_RESISTANCE";
pub(crate) const MODIFIER_COLD_RESISTANCE: &str = "COLD_RESISTANCE";
pub(crate) const MODIFIER_LIGHTNING_RESISTANCE: &str = "LIGHTNING_RESISTANCE";
pub(crate) const MODIFIER_POISON_RESISTANCE: &str = "POISON_RESISTANCE";
pub(crate) const MODIFIER_HOLY_RESISTANCE: &str = "HOLY_RESISTANCE";
pub(crate) const MODIFIER_SHADOW_RESISTANCE: &str = "SHADOW_RESISTANCE";
pub(crate) const MODIFIER_ARCANE_RESISTANCE: &str = "ARCANE_RESISTANCE";
pub(crate) const MODIFIER_PHYSICAL_DAMAGE: &str = "PHYSICAL_DAMAGE";
pub(crate) const MODIFIER_CRIT_CHANCE: &str = "CRIT_CHANCE";
pub(crate) const MODIFIER_MOVE_SPEED: &str = "MOVE_SPEED";
pub(crate) const MODIFIER_MANA_REGEN: &str = "MANA_REGEN";
pub(crate) const MODIFIER_HEALTH_REGEN: &str = "HEALTH_REGEN";
pub(crate) const MODIFIER_TRANSFERENCE: &str = "TRANSFERENCE";
pub(crate) const MODIFIER_REAPING: &str = "REAPING";
pub(crate) const MODIFIER_AWARENESS: &str = "AWARENESS";
pub(crate) const MODIFIER_LIGHT: &str = "LIGHT";
pub(crate) const MODIFIER_STEALTH: &str = "STEALTH";

const ALL_EQUIPMENT_SLOTS: &str =
    "HEAD,SHOULDER,CAPE,CHEST,LEGS,BOOTS,GLOVES,RING,AMULET,MAIN_HAND,OFF_HAND";
const JEWELRY_EQUIPMENT_SLOTS: &str = "RING,AMULET";

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

#[table(accessor = item_affix_definition, public)]
#[derive(Clone)]
pub struct ItemAffixDefinition {
    #[primary_key]
    pub affix_id: String,
    pub display_name: String,
    pub modifier_kind: String,
    pub value_min: f32,
    pub value_max: f32,
    pub allowed_item_kinds: String,
    pub allowed_equip_slots: String,
    pub jewelry_only: bool,
    pub sort_order: u32,
}

#[table(accessor = item_affix_instance, public)]
#[derive(Clone)]
pub struct ItemAffixInstance {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub item_instance_id: String,
    pub affix_id: String,
    pub modifier_kind: String,
    pub value: f32,
    pub sort_order: u32,
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

#[table(accessor = equipment_periodic_runtime)]
#[derive(Clone)]
pub struct EquipmentPeriodicRuntime {
    #[primary_key]
    pub owner: Identity,
    pub health_regen_accumulator: f32,
    pub updated_at: Timestamp,
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

#[derive(Clone, Copy, Debug, PartialEq)]
struct ItemAffixDefinitionSpec {
    affix_id: &'static str,
    display_name: &'static str,
    modifier_kind: &'static str,
    value_min: f32,
    value_max: f32,
    allowed_item_kinds: &'static str,
    allowed_equip_slots: &'static str,
    jewelry_only: bool,
    sort_order: u32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct RolledAffixSpec {
    affix: ItemAffixDefinitionSpec,
    value: f32,
}

#[derive(Clone, Debug, PartialEq)]
struct LootRollContext {
    source_kind: &'static str,
    source_id: String,
    template_id: String,
    world_kind: String,
    open_world_scene_name: String,
    hidden_loot_quality: f32,
    drop_chance: f32,
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
    armor(
        "IRON_HELM",
        "Iron Helm",
        "iron_helm",
        EQUIP_SLOT_HEAD,
        0.020,
    ),
    armor(
        "IRON_SHOULDERS",
        "Iron Shoulders",
        "iron_shoulders",
        EQUIP_SLOT_SHOULDER,
        0.030,
    ),
    armor(
        "TRAVELER_CAPE",
        "Traveler Cape",
        "traveler_cape",
        EQUIP_SLOT_CAPE,
        0.005,
    ),
    armor(
        "IRON_CHESTPLATE",
        "Iron Chestplate",
        "iron_chestplate",
        EQUIP_SLOT_CHEST,
        0.060,
    ),
    armor(
        "IRON_LEGGINGS",
        "Iron Leggings",
        "iron_leggings",
        EQUIP_SLOT_LEGS,
        0.040,
    ),
    armor(
        "IRON_BOOTS",
        "Iron Boots",
        "iron_boots",
        EQUIP_SLOT_BOOTS,
        0.020,
    ),
    armor(
        "IRON_GLOVES",
        "Iron Gloves",
        "iron_gloves",
        EQUIP_SLOT_GLOVES,
        0.020,
    ),
    armor(
        "GILDED_HELM",
        "Gilded Helm",
        "gilded_helm",
        EQUIP_SLOT_HEAD,
        0.025,
    ),
    armor(
        "GILDED_SHOULDERS",
        "Gilded Shoulders",
        "gilded_shoulders",
        EQUIP_SLOT_SHOULDER,
        0.035,
    ),
    armor(
        "GILDED_CAPE",
        "Gilded Cape",
        "gilded_cape",
        EQUIP_SLOT_CAPE,
        0.010,
    ),
    armor(
        "GILDED_CHESTPLATE",
        "Gilded Chestplate",
        "gilded_chestplate",
        EQUIP_SLOT_CHEST,
        0.070,
    ),
    armor(
        "GILDED_LEGGINGS",
        "Gilded Leggings",
        "gilded_leggings",
        EQUIP_SLOT_LEGS,
        0.045,
    ),
    armor(
        "GILDED_BOOTS",
        "Gilded Boots",
        "gilded_boots",
        EQUIP_SLOT_BOOTS,
        0.025,
    ),
    armor(
        "GILDED_GLOVES",
        "Gilded Gloves",
        "gilded_gloves",
        EQUIP_SLOT_GLOVES,
        0.025,
    ),
    armor(
        "LEATHER_HELM",
        "Leather Helm",
        "leather_helm",
        EQUIP_SLOT_HEAD,
        0.010,
    ),
    armor(
        "LEATHER_SHOULDERS",
        "Leather Shoulders",
        "leather_shoulders",
        EQUIP_SLOT_SHOULDER,
        0.015,
    ),
    armor(
        "LEATHER_CAPE",
        "Leather Cape",
        "leather_cape",
        EQUIP_SLOT_CAPE,
        0.005,
    ),
    armor(
        "LEATHER_CHESTPIECE",
        "Leather Chestpiece",
        "leather_chestpiece",
        EQUIP_SLOT_CHEST,
        0.035,
    ),
    armor(
        "LEATHER_LEGGINGS",
        "Leather Leggings",
        "leather_leggings",
        EQUIP_SLOT_LEGS,
        0.025,
    ),
    armor(
        "LEATHER_BOOTS",
        "Leather Boots",
        "leather_boots",
        EQUIP_SLOT_BOOTS,
        0.010,
    ),
    armor(
        "LEATHER_GLOVES",
        "Leather Gloves",
        "leather_gloves",
        EQUIP_SLOT_GLOVES,
        0.010,
    ),
    jewelry(
        "BRONZE_RING",
        "Bronze Ring",
        "bronze_ring",
        EQUIP_SLOT_RING,
        true,
    ),
    jewelry("IRON_RING", "Iron Ring", "iron_ring", EQUIP_SLOT_RING, true),
    jewelry(
        "BRONZE_AMULET",
        "Bronze Amulet",
        "bronze_amulet",
        EQUIP_SLOT_AMULET,
        false,
    ),
    weapon(
        "TRAINING_TWO_HAND_SWORD",
        "Training Two-Handed Sword",
        "training_two_hand_sword",
        WEAPON_KIND_TWO_HAND_SWORD,
        HAND_REQUIREMENT_TWO_HAND,
        COMBAT_PROFILE_TWO_HANDED_SWORD,
    ),
    weapon(
        "TRAINING_ONE_HAND_SWORD",
        "Training One-Handed Sword",
        "training_one_hand_sword",
        WEAPON_KIND_ONE_HAND_SWORD,
        HAND_REQUIREMENT_ONE_HAND,
        DEFAULT_COMBAT_PROFILE,
    ),
    weapon(
        "TRAINING_SHIELD",
        "Training Shield",
        "training_shield",
        WEAPON_KIND_SHIELD,
        HAND_REQUIREMENT_OFF_HAND,
        DEFAULT_COMBAT_PROFILE,
    ),
    weapon(
        "TRAINING_DAGGER_PAIR",
        "Training Daggers",
        "training_dagger_pair",
        WEAPON_KIND_DAGGER_PAIR,
        HAND_REQUIREMENT_TWO_HAND,
        COMBAT_PROFILE_DAGGERS,
    ),
    weapon(
        "TRAINING_BOW",
        "Training Bow",
        "training_bow",
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

const ITEM_AFFIX_DEFINITIONS: &[ItemAffixDefinitionSpec] = &[
    affix(
        "AFFIX_PHYSICAL_RESISTANCE_MINOR",
        "Physical Resistance",
        MODIFIER_PHYSICAL_RESISTANCE,
        0.005,
        0.030,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        10,
    ),
    affix(
        "AFFIX_MAGIC_RESISTANCE_MINOR",
        "Magic Resistance",
        MODIFIER_MAGIC_RESISTANCE,
        0.005,
        0.030,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        20,
    ),
    affix(
        "AFFIX_FIRE_RESISTANCE_MINOR",
        "Fire Resistance",
        MODIFIER_FIRE_RESISTANCE,
        0.005,
        0.040,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        30,
    ),
    affix(
        "AFFIX_COLD_RESISTANCE_MINOR",
        "Cold Resistance",
        MODIFIER_COLD_RESISTANCE,
        0.005,
        0.040,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        40,
    ),
    affix(
        "AFFIX_LIGHTNING_RESISTANCE_MINOR",
        "Lightning Resistance",
        MODIFIER_LIGHTNING_RESISTANCE,
        0.005,
        0.040,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        50,
    ),
    affix(
        "AFFIX_POISON_RESISTANCE_MINOR",
        "Poison Resistance",
        MODIFIER_POISON_RESISTANCE,
        0.005,
        0.040,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        60,
    ),
    affix(
        "AFFIX_HOLY_RESISTANCE_MINOR",
        "Holy Resistance",
        MODIFIER_HOLY_RESISTANCE,
        0.005,
        0.040,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        70,
    ),
    affix(
        "AFFIX_SHADOW_RESISTANCE_MINOR",
        "Shadow Resistance",
        MODIFIER_SHADOW_RESISTANCE,
        0.005,
        0.040,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        80,
    ),
    affix(
        "AFFIX_ARCANE_RESISTANCE_MINOR",
        "Arcane Resistance",
        MODIFIER_ARCANE_RESISTANCE,
        0.005,
        0.040,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        90,
    ),
    affix(
        "AFFIX_PHYSICAL_DAMAGE_MINOR",
        "Physical Damage",
        MODIFIER_PHYSICAL_DAMAGE,
        0.010,
        0.060,
        "WEAPON,ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        100,
    ),
    affix(
        "AFFIX_CRIT_CHANCE_MINOR",
        "Critical Chance",
        MODIFIER_CRIT_CHANCE,
        0.005,
        0.030,
        "WEAPON,ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        110,
    ),
    affix(
        "AFFIX_MOVE_SPEED_MINOR",
        "Movement Speed",
        MODIFIER_MOVE_SPEED,
        0.005,
        0.030,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        120,
    ),
    affix(
        "AFFIX_MANA_REGEN_MINOR",
        "Mana Regeneration",
        MODIFIER_MANA_REGEN,
        0.10,
        0.75,
        "ARMOR,JEWELRY",
        ALL_EQUIPMENT_SLOTS,
        false,
        130,
    ),
    affix(
        "AFFIX_HEALTH_REGEN_JEWELRY",
        "Health Regeneration",
        MODIFIER_HEALTH_REGEN,
        0.05,
        0.35,
        "JEWELRY",
        JEWELRY_EQUIPMENT_SLOTS,
        true,
        200,
    ),
    affix(
        "AFFIX_TRANSFERENCE_JEWELRY",
        "Transference",
        MODIFIER_TRANSFERENCE,
        0.010,
        0.050,
        "JEWELRY",
        JEWELRY_EQUIPMENT_SLOTS,
        true,
        210,
    ),
    affix(
        "AFFIX_REAPING_JEWELRY",
        "Reaping",
        MODIFIER_REAPING,
        0.010,
        0.050,
        "JEWELRY",
        JEWELRY_EQUIPMENT_SLOTS,
        true,
        220,
    ),
    affix(
        "AFFIX_AWARENESS_JEWELRY",
        "Awareness",
        MODIFIER_AWARENESS,
        1.0,
        5.0,
        "JEWELRY",
        JEWELRY_EQUIPMENT_SLOTS,
        true,
        230,
    ),
    affix(
        "AFFIX_LIGHT_JEWELRY",
        "Light",
        MODIFIER_LIGHT,
        1.0,
        5.0,
        "JEWELRY",
        JEWELRY_EQUIPMENT_SLOTS,
        true,
        240,
    ),
    affix(
        "AFFIX_STEALTH_JEWELRY",
        "Stealth",
        MODIFIER_STEALTH,
        0.05,
        0.25,
        "JEWELRY",
        JEWELRY_EQUIPMENT_SLOTS,
        true,
        250,
    ),
];

const fn armor(
    item_def_id: &'static str,
    display_name: &'static str,
    icon_id: &'static str,
    equip_slot: &'static str,
    physical_resistance: f32,
) -> ItemDefinitionSpec {
    ItemDefinitionSpec {
        item_def_id,
        display_name,
        item_kind: ITEM_KIND_ARMOR,
        rarity: "COMMON",
        icon_id,
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
    icon_id: &'static str,
    equip_slot: &'static str,
    unique_equipped: bool,
) -> ItemDefinitionSpec {
    ItemDefinitionSpec {
        item_def_id,
        display_name,
        item_kind: ITEM_KIND_JEWELRY,
        rarity: "COMMON",
        icon_id,
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
    icon_id: &'static str,
    weapon_kind: &'static str,
    hand_requirement: &'static str,
    combat_profile_id: &'static str,
) -> ItemDefinitionSpec {
    ItemDefinitionSpec {
        item_def_id,
        display_name,
        item_kind: ITEM_KIND_WEAPON,
        rarity: "COMMON",
        icon_id,
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

const fn affix(
    affix_id: &'static str,
    display_name: &'static str,
    modifier_kind: &'static str,
    value_min: f32,
    value_max: f32,
    allowed_item_kinds: &'static str,
    allowed_equip_slots: &'static str,
    jewelry_only: bool,
    sort_order: u32,
) -> ItemAffixDefinitionSpec {
    ItemAffixDefinitionSpec {
        affix_id,
        display_name,
        modifier_kind,
        value_min,
        value_max,
        allowed_item_kinds,
        allowed_equip_slots,
        jewelry_only,
        sort_order,
    }
}

#[reducer]
pub fn publish_item_definitions(ctx: &ReducerContext) -> Result<(), String> {
    sync_item_definitions(ctx);
    Ok(())
}

#[reducer]
pub fn publish_item_affix_definitions(ctx: &ReducerContext) -> Result<(), String> {
    sync_item_affix_definitions(ctx);
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
    collect_nearby_corpse_loot_into_primary(ctx, ctx.sender(), npc_identity);
    ctx.db
        .inventory_container()
        .container_id()
        .find(corpse_container_id(npc_identity))
        .ok_or_else(|| "corpse loot container was not created".to_string())?;
    Ok(())
}

fn collect_nearby_corpse_loot_into_primary(
    ctx: &ReducerContext,
    owner: Identity,
    primary_npc_identity: Identity,
) {
    let primary_container_id = corpse_container_id(primary_npc_identity);
    let Some(primary_npc) = ctx.db.npc_instance().identity().find(primary_npc_identity) else {
        return;
    };
    let Some(primary_physics) = ctx.db.npc_physics().identity().find(primary_npc_identity) else {
        return;
    };

    let clustered_npc_ids: Vec<_> = ctx
        .db
        .npc_state()
        .alive()
        .filter(false)
        .filter_map(|state| {
            if state.identity == primary_npc_identity {
                return None;
            }
            let npc = ctx.db.npc_instance().identity().find(state.identity)?;
            let physics = ctx.db.npc_physics().identity().find(state.identity)?;
            if corpse_is_in_loot_cluster(&primary_npc, &primary_physics, &npc, &physics) {
                Some(state.identity)
            } else {
                None
            }
        })
        .collect();

    for npc_identity in clustered_npc_ids {
        if validate_npc_loot_access(ctx, owner, npc_identity).is_err() {
            continue;
        }
        create_corpse_loot_for_npc(ctx, npc_identity, owner);
        merge_corpse_container_into_primary(ctx, primary_container_id.as_str(), npc_identity);
    }
}

fn corpse_is_in_loot_cluster(
    primary_npc: &crate::npcs::NpcInstance,
    primary_physics: &crate::npcs::NpcPhysics,
    candidate_npc: &crate::npcs::NpcInstance,
    candidate_physics: &crate::npcs::NpcPhysics,
) -> bool {
    if !primary_npc
        .world_kind
        .eq_ignore_ascii_case(candidate_npc.world_kind.as_str())
    {
        return false;
    }
    if primary_npc.world_kind.eq_ignore_ascii_case("INSTANCE") {
        if primary_npc.instance_id != candidate_npc.instance_id {
            return false;
        }
    } else if primary_npc.world_kind.eq_ignore_ascii_case("OPEN")
        && !primary_npc
            .open_world_scene_name
            .eq_ignore_ascii_case(candidate_npc.open_world_scene_name.as_str())
    {
        return false;
    }

    let dx = primary_physics.pos_x - candidate_physics.pos_x;
    let dy = primary_physics.pos_y - candidate_physics.pos_y;
    let dz = primary_physics.pos_z - candidate_physics.pos_z;
    let distance_sq = dx * dx + dy * dy + dz * dz;
    distance_sq <= CORPSE_LOOT_CLUSTER_RANGE * CORPSE_LOOT_CLUSTER_RANGE
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
    sync_item_affix_definitions(ctx);
}

pub(crate) fn sync_item_affix_definitions(ctx: &ReducerContext) {
    let authored_ids: Vec<String> = ITEM_AFFIX_DEFINITIONS
        .iter()
        .map(|spec| normalize_id(spec.affix_id))
        .collect();
    for spec in ITEM_AFFIX_DEFINITIONS {
        let value_min = spec.value_min.min(spec.value_max).max(0.0);
        let value_max = spec.value_max.max(spec.value_min).max(0.0);
        let row = ItemAffixDefinition {
            affix_id: normalize_id(spec.affix_id),
            display_name: spec.display_name.to_string(),
            modifier_kind: normalize_id(spec.modifier_kind),
            value_min,
            value_max,
            allowed_item_kinds: normalize_csv(spec.allowed_item_kinds),
            allowed_equip_slots: normalize_csv(spec.allowed_equip_slots),
            jewelry_only: spec.jewelry_only,
            sort_order: spec.sort_order,
        };
        if ctx
            .db
            .item_affix_definition()
            .affix_id()
            .find(row.affix_id.clone())
            .is_some()
        {
            ctx.db.item_affix_definition().affix_id().update(row);
        } else {
            ctx.db.item_affix_definition().insert(row);
        }
    }

    let stale_ids: Vec<_> = ctx
        .db
        .item_affix_definition()
        .iter()
        .map(|row| row.affix_id)
        .filter(|id| !authored_ids.contains(id))
        .collect();
    for stale_id in stale_ids {
        ctx.db.item_affix_definition().affix_id().delete(stale_id);
    }
}

pub(crate) fn ensure_player_inventory_for_identity(ctx: &ReducerContext, owner: Identity) {
    sync_item_definitions(ctx);
    ensure_player_bag(ctx, owner);
    let (equipment, created) = ensure_equipment_loadout(ctx, owner);
    if created {
        if let Some(class_id) = runtime_class_id_for_owner(ctx, owner) {
            seed_starter_equipment_for_class(ctx, owner, equipment, class_id.as_str(), false, true);
        }
    }
    sync_active_combat_mode_for_owner(ctx, owner, ctx.timestamp);
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub(crate) struct EquipmentModifierTotals {
    pub physical_resistance: f32,
    pub magic_resistance: f32,
    pub fire_resistance: f32,
    pub cold_resistance: f32,
    pub lightning_resistance: f32,
    pub poison_resistance: f32,
    pub holy_resistance: f32,
    pub shadow_resistance: f32,
    pub arcane_resistance: f32,
    pub physical_damage_bonus: f32,
    pub crit_chance_bonus: f32,
    pub move_speed_bonus: f32,
    pub mana_regen_per_second: f32,
    pub health_regen_per_second: f32,
    pub melee_life_steal: f32,
    pub melee_mana_steal: f32,
    pub trap_awareness: f32,
    pub light: f32,
    pub stealth_aggro_reduction: f32,
}

impl EquipmentModifierTotals {
    pub(crate) fn physical_damage_multiplier(self) -> f32 {
        1.0 + self
            .physical_damage_bonus
            .clamp(0.0, MAX_EQUIPMENT_DAMAGE_BONUS)
    }

    pub(crate) fn move_speed_multiplier(self) -> f32 {
        1.0 + self
            .move_speed_bonus
            .clamp(0.0, MAX_EQUIPMENT_MOVE_SPEED_BONUS)
    }

    pub(crate) fn resistance_for_damage_type(self, damage_type: &str) -> f32 {
        match normalize_id(damage_type).as_str() {
            "PHYSICAL" => {
                return self
                    .physical_resistance
                    .clamp(0.0, MAX_EQUIPMENT_PHYSICAL_RESISTANCE);
            }
            "FIRE" => self.magic_resistance + self.fire_resistance,
            "COLD" => self.magic_resistance + self.cold_resistance,
            "LIGHTNING" => self.magic_resistance + self.lightning_resistance,
            "POISON" => self.magic_resistance + self.poison_resistance,
            "HOLY" => self.magic_resistance + self.holy_resistance,
            "SHADOW" => self.magic_resistance + self.shadow_resistance,
            "ARCANE" => self.magic_resistance + self.arcane_resistance,
            _ => self.physical_resistance,
        }
        .clamp(0.0, MAX_EQUIPMENT_MAGIC_RESISTANCE)
    }
}

pub(crate) fn equipment_modifier_totals_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> EquipmentModifierTotals {
    let Some(equipment) = ctx.db.equipment_loadout().owner().find(owner) else {
        return EquipmentModifierTotals::default();
    };

    let mut totals = EquipmentModifierTotals::default();
    for item_id in equipment_item_ids(&equipment) {
        let Some(definition) = item_definition_for_instance(ctx, item_id) else {
            continue;
        };
        totals.physical_resistance += definition.physical_resistance.max(0.0);

        let mut affixes: Vec<_> = ctx
            .db
            .item_affix_instance()
            .item_instance_id()
            .filter(item_id)
            .collect();
        affixes.sort_by_key(|affix| affix.sort_order);
        for affix in affixes {
            if !affix_is_valid_for_definition(ctx, &affix, &definition) {
                continue;
            }
            apply_modifier_value(&mut totals, affix.modifier_kind.as_str(), affix.value);
        }
    }

    totals.physical_resistance = totals
        .physical_resistance
        .clamp(0.0, MAX_EQUIPMENT_PHYSICAL_RESISTANCE);
    totals.magic_resistance = totals
        .magic_resistance
        .clamp(0.0, MAX_EQUIPMENT_MAGIC_RESISTANCE);
    totals.fire_resistance = totals
        .fire_resistance
        .clamp(0.0, MAX_EQUIPMENT_SPECIFIC_MAGIC_RESISTANCE);
    totals.cold_resistance = totals
        .cold_resistance
        .clamp(0.0, MAX_EQUIPMENT_SPECIFIC_MAGIC_RESISTANCE);
    totals.lightning_resistance = totals
        .lightning_resistance
        .clamp(0.0, MAX_EQUIPMENT_SPECIFIC_MAGIC_RESISTANCE);
    totals.poison_resistance = totals
        .poison_resistance
        .clamp(0.0, MAX_EQUIPMENT_SPECIFIC_MAGIC_RESISTANCE);
    totals.holy_resistance = totals
        .holy_resistance
        .clamp(0.0, MAX_EQUIPMENT_SPECIFIC_MAGIC_RESISTANCE);
    totals.shadow_resistance = totals
        .shadow_resistance
        .clamp(0.0, MAX_EQUIPMENT_SPECIFIC_MAGIC_RESISTANCE);
    totals.arcane_resistance = totals
        .arcane_resistance
        .clamp(0.0, MAX_EQUIPMENT_SPECIFIC_MAGIC_RESISTANCE);
    totals.physical_damage_bonus = totals
        .physical_damage_bonus
        .clamp(0.0, MAX_EQUIPMENT_DAMAGE_BONUS);
    totals.crit_chance_bonus = totals
        .crit_chance_bonus
        .clamp(0.0, MAX_EQUIPMENT_CRIT_CHANCE_BONUS);
    totals.move_speed_bonus = totals
        .move_speed_bonus
        .clamp(0.0, MAX_EQUIPMENT_MOVE_SPEED_BONUS);
    totals.mana_regen_per_second = totals
        .mana_regen_per_second
        .clamp(0.0, MAX_EQUIPMENT_RESOURCE_REGEN);
    totals.health_regen_per_second = totals
        .health_regen_per_second
        .clamp(0.0, MAX_EQUIPMENT_HEALTH_REGEN);
    totals.melee_life_steal = totals
        .melee_life_steal
        .clamp(0.0, MAX_EQUIPMENT_STEAL_RATIO);
    totals.melee_mana_steal = totals
        .melee_mana_steal
        .clamp(0.0, MAX_EQUIPMENT_STEAL_RATIO);
    totals.stealth_aggro_reduction = totals.stealth_aggro_reduction.clamp(0.0, 1.0);
    totals
}

pub(crate) fn tick_equipment_periodic_effects(
    ctx: &ReducerContext,
    now: Timestamp,
    dt_seconds: f32,
) -> usize {
    let owners: Vec<_> = ctx
        .db
        .player_state()
        .iter()
        .filter(|row| row.alive && !row.is_dummy)
        .map(|row| row.player_id)
        .collect();
    let mut queued = 0;
    for owner in owners {
        let health_regen = equipment_modifier_totals_for_owner(ctx, owner).health_regen_per_second;
        if health_regen <= 0.0 {
            if ctx
                .db
                .equipment_periodic_runtime()
                .owner()
                .find(owner)
                .is_some()
            {
                ctx.db.equipment_periodic_runtime().owner().delete(owner);
            }
            continue;
        }

        let mut runtime = ctx
            .db
            .equipment_periodic_runtime()
            .owner()
            .find(owner)
            .unwrap_or(EquipmentPeriodicRuntime {
                owner,
                health_regen_accumulator: 0.0,
                updated_at: now,
            });
        runtime.health_regen_accumulator += health_regen * dt_seconds.max(0.0);
        let heal_amount = runtime.health_regen_accumulator.floor() as i32;
        if heal_amount > 0 {
            runtime.health_regen_accumulator -= heal_amount as f32;
            queue_effects(
                ctx,
                vec![EffectPacket::Heal {
                    amount: heal_amount,
                    source: owner,
                    target: owner,
                    spell_id: "EQUIPMENT_HEALTH_REGEN".to_string(),
                    target_audience: TargetAudience::PartyOrSelf,
                }],
            );
            queued += 1;
        }
        runtime.updated_at = now;
        if ctx
            .db
            .equipment_periodic_runtime()
            .owner()
            .find(owner)
            .is_some()
        {
            ctx.db.equipment_periodic_runtime().owner().update(runtime);
        } else {
            ctx.db.equipment_periodic_runtime().insert(runtime);
        }
    }
    queued
}

pub(crate) fn ensure_starter_equipment_for_class(
    ctx: &ReducerContext,
    owner: Identity,
    class_id: &str,
) {
    sync_item_definitions(ctx);
    ensure_player_bag(ctx, owner);
    let (equipment, _) = ensure_equipment_loadout(ctx, owner);
    seed_starter_equipment_for_class(ctx, owner, equipment, class_id, false, true);
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
            world_kind: npc.world_kind.clone(),
            instance_id: npc.instance_id,
            open_world_scene_name: npc.open_world_scene_name.clone(),
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
    roll_corpse_equipment_loot(ctx, &npc, counter_owner, container_id.as_str());
}

fn roll_corpse_equipment_loot(
    ctx: &ReducerContext,
    npc: &crate::npcs::NpcInstance,
    counter_owner: Identity,
    container_id: &str,
) {
    let loot_context = npc_loot_roll_context(npc);
    if loot_unit(&loot_context, "equipment_drop", 0) > loot_context.drop_chance {
        return;
    }

    let Some(definition) = choose_loot_item_definition(&loot_context) else {
        return;
    };
    let affixes = roll_item_affixes(&loot_context, &definition);
    let item = ItemInstance {
        item_instance_id: next_item_instance_id(ctx, counter_owner),
        item_def_id: definition.item_def_id.to_string(),
        current_owner_key: String::new(),
        current_owner: None,
        quantity: 1,
        created_at: ctx.timestamp,
    };
    ctx.db.item_instance().insert(item.clone());
    for affix in affixes {
        ctx.db.item_affix_instance().insert(ItemAffixInstance {
            key: item_affix_instance_key(item.item_instance_id.as_str(), affix.affix.affix_id),
            item_instance_id: item.item_instance_id.clone(),
            affix_id: normalize_id(affix.affix.affix_id),
            modifier_kind: normalize_id(affix.affix.modifier_kind),
            value: affix.value,
            sort_order: affix.affix.sort_order,
        });
    }
    upsert_inventory_slot(
        ctx,
        container_id,
        item.item_instance_id.as_str(),
        0,
        0,
        definition.width,
        definition.height,
    );
}

fn merge_corpse_container_into_primary(
    ctx: &ReducerContext,
    primary_container_id: &str,
    source_npc_identity: Identity,
) {
    let source_container_id = corpse_container_id(source_npc_identity);
    if source_container_id == primary_container_id {
        return;
    }
    let Some(mut primary_container) = ctx
        .db
        .inventory_container()
        .container_id()
        .find(primary_container_id.to_string())
    else {
        return;
    };
    let Some(mut source_container) = ctx
        .db
        .inventory_container()
        .container_id()
        .find(source_container_id.clone())
    else {
        return;
    };

    let mut moved_any = false;
    let source_slots: Vec<_> = ctx
        .db
        .inventory_slot()
        .container_id()
        .filter(source_container_id.as_str())
        .collect();
    for slot in source_slots {
        let Some(item) = ctx
            .db
            .item_instance()
            .item_instance_id()
            .find(slot.item_instance_id.clone())
        else {
            continue;
        };
        let Some(definition) = ctx
            .db
            .item_definition()
            .item_def_id()
            .find(item.item_def_id)
        else {
            continue;
        };
        let Some((x, y)) = first_free_position(
            ctx,
            primary_container.container_id.as_str(),
            primary_container.width,
            primary_container.height,
            definition.width,
            definition.height,
            None,
        ) else {
            continue;
        };

        ctx.db.inventory_slot().key().delete(slot.key);
        upsert_inventory_slot(
            ctx,
            primary_container.container_id.as_str(),
            item.item_instance_id.as_str(),
            x,
            y,
            definition.width,
            definition.height,
        );
        moved_any = true;
    }

    if moved_any {
        primary_container.revision = primary_container.revision.saturating_add(1);
        primary_container.updated_at = ctx.timestamp;
        ctx.db
            .inventory_container()
            .container_id()
            .update(primary_container);
        source_container.revision = source_container.revision.saturating_add(1);
        source_container.updated_at = ctx.timestamp;
        ctx.db
            .inventory_container()
            .container_id()
            .update(source_container);
    }
}

fn npc_loot_roll_context(npc: &crate::npcs::NpcInstance) -> LootRollContext {
    let hidden_loot_quality =
        (hidden_loot_quality_for_npc(npc) + GLOBAL_LOOT_QUALITY_MODIFIER).max(0.0);
    let quantity_multiplier = (1.0 + GLOBAL_LOOT_QUANTITY_MODIFIER).max(0.0);
    LootRollContext {
        source_kind: "NPC",
        source_id: npc.identity.to_hex().to_string(),
        template_id: normalize_id(npc.template_id.as_str()),
        world_kind: normalize_id(npc.world_kind.as_str()),
        open_world_scene_name: normalize_id(npc.open_world_scene_name.as_str()),
        hidden_loot_quality,
        drop_chance: ((BASE_NPC_EQUIPMENT_DROP_CHANCE
            + hidden_loot_quality * HIDDEN_LOOT_QUALITY_DROP_SCALAR)
            * quantity_multiplier)
            .clamp(0.02, 0.35),
    }
}

fn hidden_loot_quality_for_npc(npc: &crate::npcs::NpcInstance) -> f32 {
    let template_bonus = match normalize_id(npc.template_id.as_str()).as_str() {
        crate::npcs::NPC_TEMPLATE_KOBOLD_KNIGHT_RD_SWORD_SHIELD => 0.85,
        crate::npcs::NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD => 0.45,
        crate::npcs::NPC_TEMPLATE_KOBOLD_WARRIOR_GN_SPEAR => 0.25,
        crate::npcs::NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD => 0.20,
        _ => 0.0,
    };
    template_bonus
}

fn choose_loot_item_definition(context: &LootRollContext) -> Option<ItemDefinitionSpec> {
    let candidates = choose_loot_item_kind_candidates(context)?;
    if candidates.is_empty() {
        return None;
    }
    let index = loot_index(context, "item_definition", 0, candidates.len());
    candidates.get(index).copied()
}

fn choose_loot_item_kind_candidates(context: &LootRollContext) -> Option<Vec<ItemDefinitionSpec>> {
    let armor = lootable_item_definitions_for_kind(ITEM_KIND_ARMOR);
    let jewelry = lootable_item_definitions_for_kind(ITEM_KIND_JEWELRY);
    let weapons = lootable_item_definitions_for_kind(ITEM_KIND_WEAPON);
    let armor_weight = if armor.is_empty() {
        0.0
    } else {
        LOOT_ITEM_KIND_ARMOR_WEIGHT
    };
    let jewelry_weight = if jewelry.is_empty() {
        0.0
    } else {
        LOOT_ITEM_KIND_JEWELRY_WEIGHT
    };
    let weapon_weight = if weapons.is_empty() {
        0.0
    } else {
        LOOT_ITEM_KIND_WEAPON_WEIGHT
    };
    let total = armor_weight + jewelry_weight + weapon_weight;
    if total <= 0.0 {
        return None;
    }

    let roll = loot_unit(context, "item_kind", 0) * total;
    if roll < jewelry_weight {
        Some(jewelry)
    } else if roll < jewelry_weight + weapon_weight {
        Some(weapons)
    } else {
        Some(armor)
    }
}

fn lootable_item_definitions_for_kind(item_kind: &str) -> Vec<ItemDefinitionSpec> {
    lootable_equipment_definitions()
        .into_iter()
        .filter(|definition| definition.item_kind == item_kind)
        .collect()
}

fn lootable_equipment_definitions() -> Vec<ItemDefinitionSpec> {
    STARTER_ITEM_DEFINITIONS
        .iter()
        .copied()
        .filter(|definition| {
            definition.max_stack == 1
                && (definition.item_kind == ITEM_KIND_ARMOR
                    || definition.item_kind == ITEM_KIND_JEWELRY
                    || definition.item_kind == ITEM_KIND_WEAPON)
        })
        .collect()
}

fn roll_item_affixes(
    context: &LootRollContext,
    definition: &ItemDefinitionSpec,
) -> Vec<RolledAffixSpec> {
    let target_count = roll_affix_count(context);
    let mut candidates = eligible_affix_specs_for_item_definition(definition)
        .into_iter()
        .map(|affix| {
            let score = loot_hash(
                context,
                "affix_select",
                stable_affix_index(affix.affix_id) as u32,
            );
            (score, affix)
        })
        .collect::<Vec<_>>();
    candidates.sort_by_key(|(score, affix)| (*score, affix.sort_order, affix.affix_id));

    let mut rolled = Vec::new();
    let mut used_modifiers = Vec::new();
    for (_, affix) in candidates {
        if rolled.len() >= target_count {
            break;
        }
        let modifier = normalize_id(affix.modifier_kind);
        if used_modifiers.iter().any(|used| used == &modifier) {
            continue;
        }
        let value = roll_affix_value(
            context,
            &affix,
            loot_unit(
                context,
                "affix_value",
                stable_affix_index(affix.affix_id) as u32,
            ),
        );
        used_modifiers.push(modifier);
        rolled.push(RolledAffixSpec { affix, value });
    }
    rolled
}

fn eligible_affix_specs_for_item_definition(
    definition: &ItemDefinitionSpec,
) -> Vec<ItemAffixDefinitionSpec> {
    ITEM_AFFIX_DEFINITIONS
        .iter()
        .copied()
        .filter(|affix| affix_spec_applies_to_item(affix, definition))
        .collect()
}

fn affix_spec_applies_to_item(
    affix: &ItemAffixDefinitionSpec,
    definition: &ItemDefinitionSpec,
) -> bool {
    if affix.jewelry_only && definition.item_kind != ITEM_KIND_JEWELRY {
        return false;
    }
    if !csv_contains(affix.allowed_item_kinds, definition.item_kind) {
        return false;
    }
    if definition.equip_slot == EQUIP_SLOT_RING {
        return csv_contains(affix.allowed_equip_slots, EQUIP_SLOT_RING);
    }
    csv_contains(affix.allowed_equip_slots, definition.equip_slot)
}

fn roll_affix_count(context: &LootRollContext) -> usize {
    let one_affix_weight = 100.0;
    let two_affix_weight = 24.0 + context.hidden_loot_quality.max(0.0) * 8.0;
    let three_affix_weight = 4.0 + context.hidden_loot_quality.max(0.0) * 3.0;
    let total = one_affix_weight + two_affix_weight + three_affix_weight;
    let roll = loot_unit(context, "affix_count", 0) * total;
    if roll < three_affix_weight {
        MAX_ROLLED_ITEM_AFFIXES
    } else if roll < three_affix_weight + two_affix_weight {
        2
    } else {
        1
    }
}

fn roll_affix_value(context: &LootRollContext, affix: &ItemAffixDefinitionSpec, roll: f32) -> f32 {
    let quality_shift =
        (context.hidden_loot_quality.max(0.0) * HIDDEN_LOOT_QUALITY_AFFIX_SCALAR).min(0.35);
    let normalized = (roll + quality_shift).clamp(0.0, 1.0);
    affix.value_min + (affix.value_max - affix.value_min).max(0.0) * normalized
}

fn loot_index(context: &LootRollContext, salt: &str, stream: u32, len: usize) -> usize {
    if len <= 1 {
        return 0;
    }
    (loot_hash(context, salt, stream) as usize) % len
}

fn loot_unit(context: &LootRollContext, salt: &str, stream: u32) -> f32 {
    let upper53 = loot_hash(context, salt, stream) >> 11;
    (upper53 as f64 / ((1_u64 << 53) as f64)) as f32
}

fn loot_hash(context: &LootRollContext, salt: &str, stream: u32) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    hash = fnv1a_update(hash, context.source_kind.as_bytes());
    hash = fnv1a_update(hash, context.source_id.as_bytes());
    hash = fnv1a_update(hash, context.template_id.as_bytes());
    hash = fnv1a_update(hash, context.world_kind.as_bytes());
    hash = fnv1a_update(hash, context.open_world_scene_name.as_bytes());
    hash = fnv1a_update(hash, salt.as_bytes());
    fnv1a_update(hash, &stream.to_le_bytes())
}

fn fnv1a_update(mut hash: u64, bytes: &[u8]) -> u64 {
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}

fn stable_affix_index(affix_id: &str) -> usize {
    ITEM_AFFIX_DEFINITIONS
        .iter()
        .position(|affix| affix.affix_id == affix_id)
        .unwrap_or(0)
}

fn item_affix_instance_key(item_instance_id: &str, affix_id: &str) -> String {
    format!("{}:{}", item_instance_id, normalize_id(affix_id))
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
    clear_equipped_starter_armor(ctx, &mut equipment);
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

fn affix_is_valid_for_definition(
    ctx: &ReducerContext,
    affix: &ItemAffixInstance,
    definition: &ItemDefinition,
) -> bool {
    let Some(authored) = ctx
        .db
        .item_affix_definition()
        .affix_id()
        .find(normalize_id(affix.affix_id.as_str()))
    else {
        return false;
    };
    if normalize_id(authored.modifier_kind.as_str()) != normalize_id(affix.modifier_kind.as_str()) {
        return false;
    }
    if !csv_contains(
        authored.allowed_item_kinds.as_str(),
        definition.item_kind.as_str(),
    ) {
        return false;
    }
    if authored.jewelry_only && definition.item_kind != ITEM_KIND_JEWELRY {
        return false;
    }
    let equip_slot = normalize_equipment_slot(definition.equip_slot.as_str());
    if definition.equip_slot == EQUIP_SLOT_RING {
        return csv_contains(authored.allowed_equip_slots.as_str(), EQUIP_SLOT_RING);
    }
    csv_contains(authored.allowed_equip_slots.as_str(), equip_slot.as_str())
}

fn apply_modifier_value(totals: &mut EquipmentModifierTotals, modifier_kind: &str, value: f32) {
    let value = value.max(0.0);
    match normalize_id(modifier_kind).as_str() {
        MODIFIER_PHYSICAL_RESISTANCE => totals.physical_resistance += value,
        MODIFIER_MAGIC_RESISTANCE => totals.magic_resistance += value,
        MODIFIER_FIRE_RESISTANCE => totals.fire_resistance += value,
        MODIFIER_COLD_RESISTANCE => totals.cold_resistance += value,
        MODIFIER_LIGHTNING_RESISTANCE => totals.lightning_resistance += value,
        MODIFIER_POISON_RESISTANCE => totals.poison_resistance += value,
        MODIFIER_HOLY_RESISTANCE => totals.holy_resistance += value,
        MODIFIER_SHADOW_RESISTANCE => totals.shadow_resistance += value,
        MODIFIER_ARCANE_RESISTANCE => totals.arcane_resistance += value,
        MODIFIER_PHYSICAL_DAMAGE => totals.physical_damage_bonus += value,
        MODIFIER_CRIT_CHANCE => totals.crit_chance_bonus += value,
        MODIFIER_MOVE_SPEED => totals.move_speed_bonus += value,
        MODIFIER_MANA_REGEN => totals.mana_regen_per_second += value,
        MODIFIER_HEALTH_REGEN => totals.health_regen_per_second += value,
        MODIFIER_TRANSFERENCE => totals.melee_life_steal += value,
        MODIFIER_REAPING => totals.melee_mana_steal += value,
        MODIFIER_AWARENESS => totals.trap_awareness += value,
        MODIFIER_LIGHT => totals.light += value,
        MODIFIER_STEALTH => totals.stealth_aggro_reduction += value,
        _ => {}
    }
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

fn normalize_csv(value: &str) -> String {
    value
        .split(',')
        .map(normalize_id)
        .filter(|part| !part.is_empty())
        .collect::<Vec<_>>()
        .join(",")
}

fn csv_contains(csv: &str, value: &str) -> bool {
    let needle = normalize_id(value);
    csv.split(',').map(normalize_id).any(|part| part == needle)
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

    fn loot_context_for_test(source_id: String, hidden_loot_quality: f32) -> LootRollContext {
        LootRollContext {
            source_kind: "TEST",
            source_id,
            template_id: crate::npcs::NPC_TEMPLATE_KOBOLD_KNIGHT_RD_SWORD_SHIELD.to_string(),
            world_kind: "INSTANCE".to_string(),
            open_world_scene_name: "TEST_SCENE".to_string(),
            hidden_loot_quality,
            drop_chance: 1.0,
        }
    }

    fn npc_for_cluster_test(
        world_kind: &str,
        instance_id: Option<u64>,
        scene: &str,
    ) -> crate::npcs::NpcInstance {
        crate::npcs::NpcInstance {
            identity: Identity::ZERO,
            spawned_by: Identity::ZERO,
            template_id: crate::npcs::NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD.to_string(),
            species_id: "KOBOLD_WARRIOR".to_string(),
            faction: crate::npcs::NPC_FACTION_HOSTILE.to_string(),
            display_name: "Kobold".to_string(),
            world_kind: world_kind.to_string(),
            instance_id,
            open_world_scene_name: scene.to_string(),
            spawned_at: Timestamp::UNIX_EPOCH,
        }
    }

    fn npc_physics_for_cluster_test(x: f32, z: f32) -> crate::npcs::NpcPhysics {
        crate::npcs::NpcPhysics {
            identity: Identity::ZERO,
            pos_x: x,
            pos_y: 0.0,
            pos_z: z,
            yaw: 0.0,
            updated_at: Timestamp::UNIX_EPOCH,
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
    fn equipment_resistance_totals_stack_global_and_specific_magic_sources() {
        let totals = EquipmentModifierTotals {
            physical_resistance: 0.20,
            magic_resistance: 0.15,
            fire_resistance: 0.10,
            cold_resistance: 0.05,
            ..EquipmentModifierTotals::default()
        };

        assert!((totals.resistance_for_damage_type("PHYSICAL") - 0.20).abs() < 0.0001);
        assert!((totals.resistance_for_damage_type("FIRE") - 0.25).abs() < 0.0001);
        assert!((totals.resistance_for_damage_type("COLD") - 0.20).abs() < 0.0001);
        assert!((totals.resistance_for_damage_type("ARCANE") - 0.15).abs() < 0.0001);
    }

    #[test]
    fn equipment_modifier_totals_clamp_combat_multipliers() {
        let totals = EquipmentModifierTotals {
            physical_damage_bonus: MAX_EQUIPMENT_DAMAGE_BONUS + 1.0,
            move_speed_bonus: MAX_EQUIPMENT_MOVE_SPEED_BONUS + 1.0,
            ..EquipmentModifierTotals::default()
        };

        assert!(
            (totals.physical_damage_multiplier() - (1.0 + MAX_EQUIPMENT_DAMAGE_BONUS)).abs()
                < 0.0001
        );
        assert!(
            (totals.move_speed_multiplier() - (1.0 + MAX_EQUIPMENT_MOVE_SPEED_BONUS)).abs()
                < 0.0001
        );
    }

    #[test]
    fn apply_modifier_value_accumulates_jewelry_only_combat_hooks() {
        let mut totals = EquipmentModifierTotals::default();

        apply_modifier_value(&mut totals, MODIFIER_HEALTH_REGEN, 0.25);
        apply_modifier_value(&mut totals, MODIFIER_TRANSFERENCE, 0.08);
        apply_modifier_value(&mut totals, MODIFIER_REAPING, 0.06);
        apply_modifier_value(&mut totals, MODIFIER_AWARENESS, 3.0);
        apply_modifier_value(&mut totals, MODIFIER_LIGHT, 2.0);
        apply_modifier_value(&mut totals, MODIFIER_STEALTH, 0.12);

        assert!((totals.health_regen_per_second - 0.25).abs() < 0.0001);
        assert!((totals.melee_life_steal - 0.08).abs() < 0.0001);
        assert!((totals.melee_mana_steal - 0.06).abs() < 0.0001);
        assert!((totals.trap_awareness - 3.0).abs() < 0.0001);
        assert!((totals.light - 2.0).abs() < 0.0001);
        assert!((totals.stealth_aggro_reduction - 0.12).abs() < 0.0001);
    }

    #[test]
    fn rolled_affix_count_caps_at_three_and_keeps_three_rarer_than_two() {
        let mut one = 0;
        let mut two = 0;
        let mut three = 0;
        for index in 0..4096 {
            let context = loot_context_for_test(format!("loot-source-{index}"), 1.25);
            match roll_affix_count(&context) {
                1 => one += 1,
                2 => two += 1,
                3 => three += 1,
                other => panic!("unexpected affix count {other}"),
            }
        }

        assert!(one > two, "one-affix rolls should be most common");
        assert!(
            two > three,
            "three-affix rolls should be rarer than two-affix rolls"
        );
    }

    #[test]
    fn rolled_affixes_do_not_duplicate_affix_or_modifier_on_one_item() {
        let definition = STARTER_ITEM_DEFINITIONS
            .iter()
            .find(|definition| definition.item_def_id == "BRONZE_RING")
            .expect("test ring definition should exist");

        for index in 0..128 {
            let context = loot_context_for_test(format!("ring-source-{index}"), 2.0);
            let affixes = roll_item_affixes(&context, definition);
            assert!(affixes.len() <= MAX_ROLLED_ITEM_AFFIXES);
            let mut affix_ids = Vec::new();
            let mut modifiers = Vec::new();
            for affix in affixes {
                assert!(
                    !affix_ids.contains(&affix.affix.affix_id),
                    "duplicate affix id {}",
                    affix.affix.affix_id
                );
                assert!(
                    !modifiers.contains(&affix.affix.modifier_kind),
                    "duplicate modifier {}",
                    affix.affix.modifier_kind
                );
                affix_ids.push(affix.affix.affix_id);
                modifiers.push(affix.affix.modifier_kind);
            }
        }
    }

    #[test]
    fn npc_loot_quality_does_not_depend_on_instance_context() {
        let open_world = npc_for_cluster_test("OPEN", None, "OPEN_WORLD");
        let instance = npc_for_cluster_test("INSTANCE", Some(42), "OPEN_WORLD");

        assert!(
            (hidden_loot_quality_for_npc(&open_world) - hidden_loot_quality_for_npc(&instance))
                .abs()
                < 0.0001
        );
    }

    #[test]
    fn all_kobold_templates_can_roll_jewelry_without_forcing_it() {
        fn jewelry_rolls_for_template(template_id: &str) -> usize {
            (0..2048)
                .filter(|index| {
                    let mut context = loot_context_for_test(format!("{template_id}-{index}"), 0.0);
                    context.template_id = template_id.to_string();
                    choose_loot_item_definition(&context)
                        .is_some_and(|definition| definition.item_kind == ITEM_KIND_JEWELRY)
                })
                .count()
        }

        let warrior_jewelry =
            jewelry_rolls_for_template(crate::npcs::NPC_TEMPLATE_KOBOLD_WARRIOR_RD_SWORD_SHIELD);
        let thief_jewelry =
            jewelry_rolls_for_template(crate::npcs::NPC_TEMPLATE_KOBOLD_THIEF_BK_DUAL_SWORD);

        assert!(
            warrior_jewelry > 0,
            "warriors should be able to drop jewelry"
        );
        assert!(thief_jewelry > 0, "thieves should be able to drop jewelry");
        assert!(
            warrior_jewelry < 2048,
            "warriors should not force jewelry drops"
        );
        assert!(
            thief_jewelry < 2048,
            "thieves should not force jewelry drops"
        );
    }

    #[test]
    fn resistance_affixes_are_not_eligible_for_weapons() {
        let weapon_spec = ItemDefinitionSpec {
            item_def_id: "TEST_WEAPON",
            display_name: "Test Weapon",
            item_kind: ITEM_KIND_WEAPON,
            rarity: "COMMON",
            icon_id: "",
            max_stack: 1,
            width: 1,
            height: 1,
            equip_slot: EQUIP_SLOT_MAIN_HAND,
            weapon_kind: WEAPON_KIND_ONE_HAND_SWORD,
            hand_requirement: HAND_REQUIREMENT_ONE_HAND,
            unique_equipped: false,
            combat_profile_id: "",
            physical_resistance: 0.0,
        };
        let affixes = eligible_affix_specs_for_item_definition(&weapon_spec);

        assert!(affixes
            .iter()
            .all(|affix| !affix.modifier_kind.ends_with("_RESISTANCE")));
        assert!(affixes
            .iter()
            .any(|affix| affix.modifier_kind == MODIFIER_PHYSICAL_DAMAGE));
        assert!(affixes
            .iter()
            .any(|affix| affix.modifier_kind == MODIFIER_CRIT_CHANCE));
    }

    #[test]
    fn corpse_loot_cluster_accepts_same_world_corpses_within_range() {
        let primary = npc_for_cluster_test("OPEN", None, "OPEN_WORLD");
        let candidate = npc_for_cluster_test("OPEN", None, "OPEN_WORLD");
        let primary_physics = npc_physics_for_cluster_test(10.0, 5.0);
        let candidate_physics = npc_physics_for_cluster_test(11.0, 6.0);

        assert!(corpse_is_in_loot_cluster(
            &primary,
            &primary_physics,
            &candidate,
            &candidate_physics
        ));
    }

    #[test]
    fn corpse_loot_cluster_rejects_far_or_different_scene_corpses() {
        let primary = npc_for_cluster_test("OPEN", None, "OPEN_WORLD");
        let far_candidate = npc_for_cluster_test("OPEN", None, "OPEN_WORLD");
        let different_scene_candidate = npc_for_cluster_test("OPEN", None, "OTHER_SCENE");
        let primary_physics = npc_physics_for_cluster_test(0.0, 0.0);
        let far_physics = npc_physics_for_cluster_test(CORPSE_LOOT_CLUSTER_RANGE + 1.0, 0.0);
        let near_physics = npc_physics_for_cluster_test(0.5, 0.5);

        assert!(!corpse_is_in_loot_cluster(
            &primary,
            &primary_physics,
            &far_candidate,
            &far_physics
        ));
        assert!(!corpse_is_in_loot_cluster(
            &primary,
            &primary_physics,
            &different_scene_candidate,
            &near_physics
        ));
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

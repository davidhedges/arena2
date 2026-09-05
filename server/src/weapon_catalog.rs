//! Shared read schema for weapon appearance JSON consumed by Hub and gameplay.
//!
//! Each consumer uses a subset of these fields. Unity-only placement and prefab
//! fields remain in the authored JSON; this module does not write that catalog.
#![allow(dead_code)]

use serde::Deserialize;

#[derive(Deserialize)]
pub(crate) struct WeaponAppearanceCatalog {
    pub schema_version: u32,
    pub colors: Vec<WeaponColor>,
    pub families: Vec<WeaponFamily>,
}

#[derive(Deserialize)]
pub(crate) struct WeaponColor {
    pub color_id: String,
    pub display_name: String,
    pub hex: String,
}

#[derive(Clone, Deserialize)]
pub(crate) struct WeaponFamily {
    pub item_def_id: String,
    pub display_name: String,
    pub icon_id: String,
    pub weapon_kind: String,
    pub hand_requirement: String,
    pub equip_slot: String,
    pub combat_discipline_id: String,
    pub sort_order: u32,
    pub default_color_id: String,
    pub variants: Vec<WeaponVariant>,
}

#[derive(Clone, Deserialize)]
pub(crate) struct WeaponVariant {
    pub color_id: String,
}

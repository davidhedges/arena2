//! Canonical armor roster, equipment pieces, and stat rules for Hub and gameplay.

pub(crate) const ARMOR_TIER_LIGHT: &str = "LIGHT";
pub(crate) const ARMOR_TIER_MEDIUM: &str = "MEDIUM";
pub(crate) const ARMOR_TIER_HEAVY: &str = "HEAVY";

pub(crate) const ARMOR_SET_PEASANT: &str = "PEASANT";
pub(crate) const ARMOR_SET_APPRENTICE: &str = "APPRENTICE";
pub(crate) const ARMOR_SET_LEATHER: &str = "LEATHER";
pub(crate) const ARMOR_SET_IRON: &str = "IRON";
pub(crate) const ARMOR_SET_GILDED: &str = "GILDED";

pub(crate) const LIGHT_ARMOR_RESISTANCE: f32 = 0.0;
pub(crate) const MEDIUM_ARMOR_RESISTANCE: f32 = 0.20;
pub(crate) const HEAVY_ARMOR_RESISTANCE: f32 = 0.40;
pub(crate) const HEAVY_ARMOR_MOVE_SPEED_PENALTY: f32 = 0.10;
pub(crate) const HEAVY_ARMOR_CAST_SPEED_PENALTY: f32 = 0.20;

pub(crate) const EQUIP_SLOT_HEAD: &str = "HEAD";
pub(crate) const EQUIP_SLOT_SHOULDER: &str = "SHOULDER";
pub(crate) const EQUIP_SLOT_CAPE: &str = "CAPE";
pub(crate) const EQUIP_SLOT_CHEST: &str = "CHEST";
pub(crate) const EQUIP_SLOT_LEGS: &str = "LEGS";
pub(crate) const EQUIP_SLOT_BOOTS: &str = "BOOTS";
pub(crate) const EQUIP_SLOT_GLOVES: &str = "GLOVES";

pub(crate) const ARMOR_EQUIPMENT_SLOT_IDS: [&str; 7] = [
    EQUIP_SLOT_HEAD,
    EQUIP_SLOT_SHOULDER,
    EQUIP_SLOT_CAPE,
    EQUIP_SLOT_CHEST,
    EQUIP_SLOT_LEGS,
    EQUIP_SLOT_BOOTS,
    EQUIP_SLOT_GLOVES,
];

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct ArmorSetPieceSpec {
    pub(crate) slot_id: &'static str,
    pub(crate) item_def_id: &'static str,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct ArmorSetSpec {
    pub(crate) armor_set_id: &'static str,
    pub(crate) display_name: &'static str,
    pub(crate) armor_tier: &'static str,
    pub(crate) physical_resistance: f32,
    pub(crate) magical_resistance: f32,
    pub(crate) move_speed_modifier: f32,
    pub(crate) cast_speed_modifier: f32,
    pub(crate) pieces: &'static [ArmorSetPieceSpec],
    pub(crate) sort_order: u32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct CompleteArmorSetSpec {
    pub(crate) armor_set_id: &'static str,
    pub(crate) display_name: &'static str,
    pub(crate) armor_tier: &'static str,
    pub(crate) pieces: &'static [&'static str],
    pub(crate) sort_order: u32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) enum ResolvedArmorSetSpec {
    Core(&'static ArmorSetSpec),
    Complete(&'static CompleteArmorSetSpec),
}

impl ResolvedArmorSetSpec {
    pub(crate) fn armor_set_id(self) -> &'static str {
        match self {
            Self::Core(spec) => spec.armor_set_id,
            Self::Complete(spec) => spec.armor_set_id,
        }
    }

    pub(crate) fn display_name(self) -> &'static str {
        match self {
            Self::Core(spec) => spec.display_name,
            Self::Complete(spec) => spec.display_name,
        }
    }

    pub(crate) fn armor_tier(self) -> &'static str {
        match self {
            Self::Core(spec) => spec.armor_tier,
            Self::Complete(spec) => spec.armor_tier,
        }
    }

    pub(crate) fn physical_resistance(self) -> f32 {
        match self {
            Self::Core(spec) => spec.physical_resistance,
            Self::Complete(_) => armor_tier_resistance(self.armor_tier()),
        }
    }

    pub(crate) fn magical_resistance(self) -> f32 {
        match self {
            Self::Core(spec) => spec.magical_resistance,
            Self::Complete(_) => armor_tier_resistance(self.armor_tier()),
        }
    }

    pub(crate) fn move_speed_modifier(self) -> f32 {
        match self {
            Self::Core(spec) => spec.move_speed_modifier,
            Self::Complete(_) if self.armor_tier() == ARMOR_TIER_HEAVY => {
                -HEAVY_ARMOR_MOVE_SPEED_PENALTY
            }
            Self::Complete(_) => 0.0,
        }
    }

    pub(crate) fn cast_speed_modifier(self) -> f32 {
        match self {
            Self::Core(spec) => spec.cast_speed_modifier,
            Self::Complete(_) if self.armor_tier() == ARMOR_TIER_HEAVY => {
                -HEAVY_ARMOR_CAST_SPEED_PENALTY
            }
            Self::Complete(_) => 0.0,
        }
    }

    pub(crate) fn piece_count(self) -> usize {
        match self {
            Self::Core(spec) => spec.pieces.len(),
            Self::Complete(spec) => spec.pieces.len(),
        }
    }

    pub(crate) fn item_def_id_for_slot(self, slot_id: &str) -> Option<String> {
        match self {
            Self::Core(spec) => {
                armor_set_piece_for_slot(spec, slot_id).map(|piece| piece.item_def_id.to_string())
            }
            Self::Complete(spec) if spec.pieces.contains(&slot_id) => Some(format!(
                "ARMOR_SET_{}_{}",
                self.armor_set_id(),
                normalize_id(slot_id)
            )),
            Self::Complete(_) => None,
        }
    }

    pub(crate) fn sort_order(self) -> u32 {
        match self {
            Self::Core(spec) => spec.sort_order,
            Self::Complete(spec) => spec.sort_order,
        }
    }
}

const PEASANT_ARMOR_PIECES: &[ArmorSetPieceSpec] = &[
    armor_set_piece(EQUIP_SLOT_CHEST, "PEASANT_TUNIC"),
    armor_set_piece(EQUIP_SLOT_LEGS, "PEASANT_TROUSERS"),
    armor_set_piece(EQUIP_SLOT_BOOTS, "PEASANT_BOOTS"),
    armor_set_piece(EQUIP_SLOT_GLOVES, "PEASANT_GLOVES"),
];

const APPRENTICE_ARMOR_PIECES: &[ArmorSetPieceSpec] = &[
    armor_set_piece(EQUIP_SLOT_HEAD, "APPRENTICE_HOOD"),
    armor_set_piece(EQUIP_SLOT_SHOULDER, "APPRENTICE_MANTLE"),
    armor_set_piece(EQUIP_SLOT_CAPE, "APPRENTICE_CLOAK"),
    armor_set_piece(EQUIP_SLOT_CHEST, "APPRENTICE_ROBE"),
    armor_set_piece(EQUIP_SLOT_LEGS, "APPRENTICE_TROUSERS"),
    armor_set_piece(EQUIP_SLOT_BOOTS, "APPRENTICE_BOOTS"),
    armor_set_piece(EQUIP_SLOT_GLOVES, "APPRENTICE_GLOVES"),
];

const LEATHER_ARMOR_PIECES: &[ArmorSetPieceSpec] = &[
    armor_set_piece(EQUIP_SLOT_HEAD, "LEATHER_HELM"),
    armor_set_piece(EQUIP_SLOT_SHOULDER, "LEATHER_SHOULDERS"),
    armor_set_piece(EQUIP_SLOT_CAPE, "LEATHER_CAPE"),
    armor_set_piece(EQUIP_SLOT_CHEST, "LEATHER_CHESTPIECE"),
    armor_set_piece(EQUIP_SLOT_LEGS, "LEATHER_LEGGINGS"),
    armor_set_piece(EQUIP_SLOT_BOOTS, "LEATHER_BOOTS"),
    armor_set_piece(EQUIP_SLOT_GLOVES, "LEATHER_GLOVES"),
];

const IRON_ARMOR_PIECES: &[ArmorSetPieceSpec] = &[
    armor_set_piece(EQUIP_SLOT_HEAD, "IRON_HELM"),
    armor_set_piece(EQUIP_SLOT_SHOULDER, "IRON_SHOULDERS"),
    armor_set_piece(EQUIP_SLOT_CAPE, "TRAVELER_CAPE"),
    armor_set_piece(EQUIP_SLOT_CHEST, "IRON_CHESTPLATE"),
    armor_set_piece(EQUIP_SLOT_LEGS, "IRON_LEGGINGS"),
    armor_set_piece(EQUIP_SLOT_BOOTS, "IRON_BOOTS"),
    armor_set_piece(EQUIP_SLOT_GLOVES, "IRON_GLOVES"),
];

const GILDED_ARMOR_PIECES: &[ArmorSetPieceSpec] = &[
    armor_set_piece(EQUIP_SLOT_HEAD, "GILDED_HELM"),
    armor_set_piece(EQUIP_SLOT_SHOULDER, "GILDED_SHOULDERS"),
    armor_set_piece(EQUIP_SLOT_CAPE, "GILDED_CAPE"),
    armor_set_piece(EQUIP_SLOT_CHEST, "GILDED_CHESTPLATE"),
    armor_set_piece(EQUIP_SLOT_LEGS, "GILDED_LEGGINGS"),
    armor_set_piece(EQUIP_SLOT_BOOTS, "GILDED_BOOTS"),
    armor_set_piece(EQUIP_SLOT_GLOVES, "GILDED_GLOVES"),
];

pub(crate) const ARMOR_SET_SPECS: &[ArmorSetSpec] = &[
    armor_set(
        ARMOR_SET_PEASANT,
        "Peasant Attire",
        ARMOR_TIER_LIGHT,
        LIGHT_ARMOR_RESISTANCE,
        0.0,
        0.0,
        PEASANT_ARMOR_PIECES,
        10,
    ),
    armor_set(
        ARMOR_SET_APPRENTICE,
        "Apprentice Vestments",
        ARMOR_TIER_LIGHT,
        LIGHT_ARMOR_RESISTANCE,
        0.0,
        0.0,
        APPRENTICE_ARMOR_PIECES,
        20,
    ),
    armor_set(
        ARMOR_SET_LEATHER,
        "Ranger Leathers",
        ARMOR_TIER_MEDIUM,
        MEDIUM_ARMOR_RESISTANCE,
        0.0,
        0.0,
        LEATHER_ARMOR_PIECES,
        30,
    ),
    armor_set(
        ARMOR_SET_IRON,
        "Iron Warplate",
        ARMOR_TIER_HEAVY,
        HEAVY_ARMOR_RESISTANCE,
        -HEAVY_ARMOR_MOVE_SPEED_PENALTY,
        -HEAVY_ARMOR_CAST_SPEED_PENALTY,
        IRON_ARMOR_PIECES,
        40,
    ),
    armor_set(
        ARMOR_SET_GILDED,
        "Gilded Warplate",
        ARMOR_TIER_HEAVY,
        HEAVY_ARMOR_RESISTANCE,
        -HEAVY_ARMOR_MOVE_SPEED_PENALTY,
        -HEAVY_ARMOR_CAST_SPEED_PENALTY,
        GILDED_ARMOR_PIECES,
        50,
    ),
];

// Human-male armor presets already shipped in the character pack. Some authored
// presets intentionally omit slots. The five core sets above keep their stable
// item ids; these variants use the deterministic ARMOR_SET_<SET>_<SLOT> item-id
// convention for the pieces they contain.
const PEASANT_PRESET_ARMOR_SLOT_IDS: &[&str] = &[
    EQUIP_SLOT_HEAD,
    EQUIP_SLOT_CAPE,
    EQUIP_SLOT_CHEST,
    EQUIP_SLOT_LEGS,
    EQUIP_SLOT_BOOTS,
    EQUIP_SLOT_GLOVES,
];
const NARCHER_PRESET_ARMOR_SLOT_IDS: &[&str] = &[
    EQUIP_SLOT_CAPE,
    EQUIP_SLOT_CHEST,
    EQUIP_SLOT_LEGS,
    EQUIP_SLOT_BOOTS,
    EQUIP_SLOT_GLOVES,
];
const SMAGE_PRESET_ARMOR_SLOT_IDS: &[&str] = &[
    EQUIP_SLOT_HEAD,
    EQUIP_SLOT_SHOULDER,
    EQUIP_SLOT_CHEST,
    EQUIP_SLOT_LEGS,
    EQUIP_SLOT_BOOTS,
    EQUIP_SLOT_GLOVES,
];

pub(crate) const COMPLETE_ARMOR_SET_SPECS: &[CompleteArmorSetSpec] = &[
    partial_armor_set(
        "PEASANT_BL",
        "Blue Peasant Attire",
        ARMOR_TIER_LIGHT,
        PEASANT_PRESET_ARMOR_SLOT_IDS,
        60,
    ),
    partial_armor_set(
        "PEASANT_RD",
        "Red Peasant Attire",
        ARMOR_TIER_LIGHT,
        PEASANT_PRESET_ARMOR_SLOT_IDS,
        61,
    ),
    complete_armor_set("FMAGE_BL", "Blue Mage Vestments", ARMOR_TIER_LIGHT, 70),
    complete_armor_set("FMAGE_GN", "Green Mage Vestments", ARMOR_TIER_LIGHT, 71),
    complete_armor_set("FMAGE_RD", "Red Mage Vestments", ARMOR_TIER_LIGHT, 72),
    complete_armor_set(
        "WARLOCK_GN",
        "Green Warlock Vestments",
        ARMOR_TIER_LIGHT,
        80,
    ),
    complete_armor_set(
        "WARLOCK_PE",
        "Purple Warlock Vestments",
        ARMOR_TIER_LIGHT,
        81,
    ),
    complete_armor_set(
        "WARLOCK_VT",
        "Violet Warlock Vestments",
        ARMOR_TIER_LIGHT,
        82,
    ),
    complete_armor_set("WIZARD_BL", "Blue Wizard Vestments", ARMOR_TIER_LIGHT, 90),
    complete_armor_set("WIZARD_PE", "Purple Wizard Vestments", ARMOR_TIER_LIGHT, 91),
    complete_armor_set("WIZARD_VT", "Violet Wizard Vestments", ARMOR_TIER_LIGHT, 92),
    complete_armor_set("CLERIC_BL", "Blue Cleric Vestments", ARMOR_TIER_LIGHT, 100),
    complete_armor_set("CLERIC_GO", "Gold Cleric Vestments", ARMOR_TIER_LIGHT, 101),
    complete_armor_set("CLERIC_WH", "White Cleric Vestments", ARMOR_TIER_LIGHT, 102),
    complete_armor_set(
        "NMAGE_BL",
        "Blue Northern Mage Vestments",
        ARMOR_TIER_LIGHT,
        110,
    ),
    complete_armor_set(
        "NMAGE_GN",
        "Green Northern Mage Vestments",
        ARMOR_TIER_LIGHT,
        111,
    ),
    complete_armor_set(
        "NMAGE_RD",
        "Red Northern Mage Vestments",
        ARMOR_TIER_LIGHT,
        112,
    ),
    complete_armor_set(
        "NECR_BL",
        "Blue Necromancer Vestments",
        ARMOR_TIER_LIGHT,
        120,
    ),
    complete_armor_set(
        "NECR_GR",
        "Gray Necromancer Vestments",
        ARMOR_TIER_LIGHT,
        121,
    ),
    complete_armor_set(
        "NECR_PE",
        "Purple Necromancer Vestments",
        ARMOR_TIER_LIGHT,
        122,
    ),
    complete_armor_set(
        "SKEEPER_BK",
        "Black Soul Keeper Vestments",
        ARMOR_TIER_LIGHT,
        130,
    ),
    complete_armor_set(
        "SKEEPER_GN",
        "Green Soul Keeper Vestments",
        ARMOR_TIER_LIGHT,
        131,
    ),
    complete_armor_set(
        "SKEEPER_PE",
        "Purple Soul Keeper Vestments",
        ARMOR_TIER_LIGHT,
        132,
    ),
    complete_armor_set(
        "SKEEPER_RD",
        "Red Soul Keeper Vestments",
        ARMOR_TIER_LIGHT,
        133,
    ),
    partial_armor_set(
        "SMAGE_BL",
        "Blue Storm Mage Vestments",
        ARMOR_TIER_LIGHT,
        SMAGE_PRESET_ARMOR_SLOT_IDS,
        140,
    ),
    partial_armor_set(
        "SMAGE_CN",
        "Cyan Storm Mage Vestments",
        ARMOR_TIER_LIGHT,
        SMAGE_PRESET_ARMOR_SLOT_IDS,
        141,
    ),
    partial_armor_set(
        "SMAGE_RD",
        "Red Storm Mage Vestments",
        ARMOR_TIER_LIGHT,
        SMAGE_PRESET_ARMOR_SLOT_IDS,
        142,
    ),
    partial_armor_set(
        "NARCHER_BL",
        "Blue Archer Leathers",
        ARMOR_TIER_MEDIUM,
        NARCHER_PRESET_ARMOR_SLOT_IDS,
        180,
    ),
    partial_armor_set(
        "NARCHER_GN",
        "Green Archer Leathers",
        ARMOR_TIER_MEDIUM,
        NARCHER_PRESET_ARMOR_SLOT_IDS,
        181,
    ),
    partial_armor_set(
        "NARCHER_RD",
        "Red Archer Leathers",
        ARMOR_TIER_MEDIUM,
        NARCHER_PRESET_ARMOR_SLOT_IDS,
        182,
    ),
    partial_armor_set(
        "NARCHER_OLD_BL",
        "Weathered Blue Archer Leathers",
        ARMOR_TIER_MEDIUM,
        NARCHER_PRESET_ARMOR_SLOT_IDS,
        183,
    ),
    partial_armor_set(
        "NARCHER_OLD_GN",
        "Weathered Green Archer Leathers",
        ARMOR_TIER_MEDIUM,
        NARCHER_PRESET_ARMOR_SLOT_IDS,
        184,
    ),
    partial_armor_set(
        "NARCHER_OLD_PE",
        "Weathered Purple Archer Leathers",
        ARMOR_TIER_MEDIUM,
        NARCHER_PRESET_ARMOR_SLOT_IDS,
        185,
    ),
    partial_armor_set(
        "NARCHER_OLD_WH",
        "Weathered White Archer Leathers",
        ARMOR_TIER_MEDIUM,
        NARCHER_PRESET_ARMOR_SLOT_IDS,
        186,
    ),
    complete_armor_set(
        "BARBARIAN_BL",
        "Blue Barbarian Leathers",
        ARMOR_TIER_MEDIUM,
        200,
    ),
    complete_armor_set(
        "BARBARIAN_GN",
        "Green Barbarian Leathers",
        ARMOR_TIER_MEDIUM,
        201,
    ),
    complete_armor_set(
        "BARBARIAN_RD",
        "Red Barbarian Leathers",
        ARMOR_TIER_MEDIUM,
        202,
    ),
    complete_armor_set("HUNTER_BL", "Blue Hunter Leathers", ARMOR_TIER_MEDIUM, 210),
    complete_armor_set("HUNTER_GN", "Green Hunter Leathers", ARMOR_TIER_MEDIUM, 211),
    complete_armor_set(
        "HUNTER_PE",
        "Purple Hunter Leathers",
        ARMOR_TIER_MEDIUM,
        212,
    ),
    complete_armor_set("HUNTER_RD", "Red Hunter Leathers", ARMOR_TIER_MEDIUM, 213),
    complete_armor_set(
        "NRANGER_BL",
        "Blue Northern Ranger Leathers",
        ARMOR_TIER_MEDIUM,
        220,
    ),
    complete_armor_set(
        "NRANGER_RD",
        "Red Northern Ranger Leathers",
        ARMOR_TIER_MEDIUM,
        221,
    ),
    complete_armor_set("RANGER_GN", "Green Ranger Leathers", ARMOR_TIER_MEDIUM, 230),
    complete_armor_set(
        "RANGER_PE",
        "Purple Ranger Leathers",
        ARMOR_TIER_MEDIUM,
        231,
    ),
    complete_armor_set("RANGER_RD", "Red Ranger Leathers", ARMOR_TIER_MEDIUM, 232),
    complete_armor_set("REAPER_BL", "Blue Reaper Leathers", ARMOR_TIER_MEDIUM, 240),
    complete_armor_set("REAPER_CN", "Cyan Reaper Leathers", ARMOR_TIER_MEDIUM, 241),
    complete_armor_set("REAPER_GN", "Green Reaper Leathers", ARMOR_TIER_MEDIUM, 242),
    complete_armor_set("ROGUE_BL", "Blue Rogue Leathers", ARMOR_TIER_MEDIUM, 250),
    complete_armor_set("ROGUE_GN", "Green Rogue Leathers", ARMOR_TIER_MEDIUM, 251),
    complete_armor_set("ROGUE_RD", "Red Rogue Leathers", ARMOR_TIER_MEDIUM, 252),
    complete_armor_set("DRUID_BL", "Blue Druid Leathers", ARMOR_TIER_MEDIUM, 260),
    complete_armor_set("DRUID_RD", "Red Druid Leathers", ARMOR_TIER_MEDIUM, 261),
    complete_armor_set("DRUID_YE", "Yellow Druid Leathers", ARMOR_TIER_MEDIUM, 262),
    complete_armor_set("THIEF_BK", "Black Thief Leathers", ARMOR_TIER_MEDIUM, 270),
    complete_armor_set("THIEF_BR", "Brown Thief Leathers", ARMOR_TIER_MEDIUM, 271),
    complete_armor_set("THIEF_GN", "Green Thief Leathers", ARMOR_TIER_MEDIUM, 272),
    complete_armor_set("THIEF_RD", "Red Thief Leathers", ARMOR_TIER_MEDIUM, 273),
    complete_armor_set(
        "TOMBSEEKER_GN",
        "Green Tomb Seeker Leathers",
        ARMOR_TIER_MEDIUM,
        280,
    ),
    complete_armor_set(
        "TOMBSEEKER_PE",
        "Purple Tomb Seeker Leathers",
        ARMOR_TIER_MEDIUM,
        281,
    ),
    complete_armor_set(
        "TOMBSEEKER_RD",
        "Red Tomb Seeker Leathers",
        ARMOR_TIER_MEDIUM,
        282,
    ),
    complete_armor_set(
        "TOMBSEEKER_WH",
        "White Tomb Seeker Leathers",
        ARMOR_TIER_MEDIUM,
        283,
    ),
    complete_armor_set("DK_BL", "Blue Death Knight Plate", ARMOR_TIER_HEAVY, 400),
    complete_armor_set("DK_GN", "Green Death Knight Plate", ARMOR_TIER_HEAVY, 401),
    complete_armor_set("DK_RD", "Red Death Knight Plate", ARMOR_TIER_HEAVY, 402),
    complete_armor_set("DUNGPLATE_BL", "Blue Dungeon Plate", ARMOR_TIER_HEAVY, 410),
    complete_armor_set(
        "DUNGPLATE_PE",
        "Purple Dungeon Plate",
        ARMOR_TIER_HEAVY,
        411,
    ),
    complete_armor_set("DUNGPLATE_RD", "Red Dungeon Plate", ARMOR_TIER_HEAVY, 412),
    complete_armor_set(
        "NWARRIOR_RD",
        "Red Northern Warplate",
        ARMOR_TIER_HEAVY,
        420,
    ),
    complete_armor_set("PALADIN_BL", "Blue Paladin Plate", ARMOR_TIER_HEAVY, 430),
    complete_armor_set("PALADIN_GN", "Green Paladin Plate", ARMOR_TIER_HEAVY, 431),
    complete_armor_set("PALADIN_GR", "Gray Paladin Plate", ARMOR_TIER_HEAVY, 432),
    complete_armor_set("PALADIN_RD", "Red Paladin Plate", ARMOR_TIER_HEAVY, 433),
    complete_armor_set("WARRIOR_GN", "Green Warrior Plate", ARMOR_TIER_HEAVY, 440),
    complete_armor_set("WARRIOR_PE", "Purple Warrior Plate", ARMOR_TIER_HEAVY, 441),
    complete_armor_set("WARRIOR_RD", "Red Warrior Plate", ARMOR_TIER_HEAVY, 442),
    complete_armor_set(
        "DBRINGER_BK",
        "Black Deathbringer Plate",
        ARMOR_TIER_MEDIUM,
        450,
    ),
    complete_armor_set(
        "DBRINGER_BL",
        "Blue Deathbringer Plate",
        ARMOR_TIER_MEDIUM,
        451,
    ),
    complete_armor_set(
        "DBRINGER_GN",
        "Green Deathbringer Plate",
        ARMOR_TIER_MEDIUM,
        452,
    ),
    complete_armor_set(
        "DBRINGER_RD",
        "Red Deathbringer Plate",
        ARMOR_TIER_MEDIUM,
        453,
    ),
    complete_armor_set("FOOTMAN_BL", "Blue Footman Plate", ARMOR_TIER_HEAVY, 460),
    complete_armor_set("FOOTMAN_GO", "Gold Footman Plate", ARMOR_TIER_HEAVY, 461),
    complete_armor_set("FOOTMAN_GR", "Gray Footman Plate", ARMOR_TIER_HEAVY, 462),
];

const fn armor_set_piece(slot_id: &'static str, item_def_id: &'static str) -> ArmorSetPieceSpec {
    ArmorSetPieceSpec {
        slot_id,
        item_def_id,
    }
}

const fn armor_set(
    armor_set_id: &'static str,
    display_name: &'static str,
    armor_tier: &'static str,
    resistance: f32,
    move_speed_modifier: f32,
    cast_speed_modifier: f32,
    pieces: &'static [ArmorSetPieceSpec],
    sort_order: u32,
) -> ArmorSetSpec {
    ArmorSetSpec {
        armor_set_id,
        display_name,
        armor_tier,
        physical_resistance: resistance,
        magical_resistance: resistance,
        move_speed_modifier,
        cast_speed_modifier,
        pieces,
        sort_order,
    }
}

const fn complete_armor_set(
    armor_set_id: &'static str,
    display_name: &'static str,
    armor_tier: &'static str,
    sort_order: u32,
) -> CompleteArmorSetSpec {
    CompleteArmorSetSpec {
        armor_set_id,
        display_name,
        armor_tier,
        pieces: &ARMOR_EQUIPMENT_SLOT_IDS,
        sort_order,
    }
}

const fn partial_armor_set(
    armor_set_id: &'static str,
    display_name: &'static str,
    armor_tier: &'static str,
    pieces: &'static [&'static str],
    sort_order: u32,
) -> CompleteArmorSetSpec {
    CompleteArmorSetSpec {
        armor_set_id,
        display_name,
        armor_tier,
        pieces,
        sort_order,
    }
}

pub(crate) fn armor_set_catalog() -> impl Iterator<Item = ResolvedArmorSetSpec> {
    ARMOR_SET_SPECS
        .iter()
        .map(ResolvedArmorSetSpec::Core)
        .chain(
            COMPLETE_ARMOR_SET_SPECS
                .iter()
                .map(ResolvedArmorSetSpec::Complete),
        )
}

fn armor_tier_resistance(armor_tier: &str) -> f32 {
    match normalize_id(armor_tier).as_str() {
        ARMOR_TIER_MEDIUM => MEDIUM_ARMOR_RESISTANCE,
        ARMOR_TIER_HEAVY => HEAVY_ARMOR_RESISTANCE,
        _ => LIGHT_ARMOR_RESISTANCE,
    }
}

fn armor_set_piece_for_slot<'a>(
    spec: &'a ArmorSetSpec,
    slot_id: &str,
) -> Option<&'a ArmorSetPieceSpec> {
    spec.pieces.iter().find(|piece| piece.slot_id == slot_id)
}

pub(crate) fn normalize_id(value: &str) -> String {
    value
        .trim()
        .replace('-', "_")
        .replace(' ', "_")
        .to_ascii_uppercase()
}

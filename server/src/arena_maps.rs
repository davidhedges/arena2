//! Stable authored-arena identities and their immutable shared-data payloads.
//!
//! A disposable match database stores only the selected map ID. Collision and
//! layout remain build artifacts, not per-match rows. Adding a map means adding
//! a catalog variant and its checked-in exporter outputs.

pub const ARENA_MAP_01_ID: &str = "ARENA_MAP_01";
pub const DEFAULT_ARENA_MAP_ID: &str = ARENA_MAP_01_ID;

const ARENA_MAP_01_LAYOUT_JSON: &str = include_str!("map_data/arena_map_01.layout.shared.json");
const ARENA_MAP_01_COLLISION_JSON: &str =
    include_str!("map_data/arena_map_01.collision.shared.json");
const ARENA_MAP_01_QUERY_COLLISION_JSON: &str =
    include_str!("map_data/arena_map_01.query_collision.shared.json");

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ArenaMapId {
    ArenaMap01,
}

impl ArenaMapId {
    pub fn parse(value: &str) -> Option<Self> {
        value
            .eq_ignore_ascii_case(ARENA_MAP_01_ID)
            .then_some(Self::ArenaMap01)
    }

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::ArenaMap01 => ARENA_MAP_01_ID,
        }
    }

    pub const fn profile(self) -> &'static ArenaMapProfile {
        match self {
            Self::ArenaMap01 => &ARENA_MAP_01_PROFILE,
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct ArenaMapProfile {
    pub data_key: &'static str,
    pub layout_json: &'static str,
    pub movement_collision_json: &'static str,
    pub query_collision_json: &'static str,
}

pub const ARENA_MAP_01_PROFILE: ArenaMapProfile = ArenaMapProfile {
    data_key: "arena_map_01",
    layout_json: ARENA_MAP_01_LAYOUT_JSON,
    movement_collision_json: ARENA_MAP_01_COLLISION_JSON,
    query_collision_json: ARENA_MAP_01_QUERY_COLLISION_JSON,
};

pub fn require_arena_map_id(value: &str) -> Result<ArenaMapId, String> {
    ArenaMapId::parse(value).ok_or_else(|| format!("Unknown authored arena map {value}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arena_map_catalog_is_closed_and_case_insensitive_at_the_boundary() {
        assert_eq!(
            ArenaMapId::parse("ARENA_MAP_01"),
            Some(ArenaMapId::ArenaMap01)
        );
        assert_eq!(
            ArenaMapId::parse("arena_map_01"),
            Some(ArenaMapId::ArenaMap01)
        );
        assert_eq!(ArenaMapId::parse("retired_example"), None);
        assert_eq!(ArenaMapId::ArenaMap01.profile().data_key, "arena_map_01");
    }
}

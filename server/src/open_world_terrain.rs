use crate::open_world_scene::{
    default_open_world_scene_profile, OpenWorldSceneProfile, ADVENTURE_ISLAND_PROFILE,
    DESERT_DAY_PROFILE, DOCKS_DAY_PROFILE, GOLDEN_VALLEY_OVERCAST_PROFILE,
    GOLDEN_VALLEY_SUNNY_PROFILE, GREAT_HALL_DAY_PROFILE, IDOL_DAY_PROFILE, OASIS_DAY_PROFILE,
    OPEN_WORLD_HEIGHTFIELD_JSON, TEMPLE_GARDENS_PROFILE,
};
use serde::Deserialize;
use std::sync::OnceLock;

const DEFAULT_SEED: u32 = 614670171;
const WORLD_SIZE: f32 = 320.0;

#[derive(Deserialize)]
struct OpenWorldHeightfieldConfig {
    origin: [f32; 3],
    size: [f32; 3],
    resolution_x: usize,
    resolution_z: usize,
    heights: Vec<f32>,
}

fn open_world_heightfield_for_profile(
    profile: &OpenWorldSceneProfile,
) -> &'static OpenWorldHeightfieldConfig {
    static OPEN_WORLD_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> = OnceLock::new();
    static ADVENTURE_ISLAND_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> = OnceLock::new();
    static DESERT_DAY_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> = OnceLock::new();
    static DOCKS_DAY_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> = OnceLock::new();
    static GOLDEN_VALLEY_OVERCAST_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> =
        OnceLock::new();
    static GOLDEN_VALLEY_SUNNY_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> = OnceLock::new();
    static GREAT_HALL_DAY_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> = OnceLock::new();
    static IDOL_DAY_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> = OnceLock::new();
    static TEMPLE_GARDENS_HEIGHTFIELD: OnceLock<OpenWorldHeightfieldConfig> = OnceLock::new();

    let (heightfield, json) = if profile.scene_name == ADVENTURE_ISLAND_PROFILE.scene_name {
        (&ADVENTURE_ISLAND_HEIGHTFIELD, profile.heightfield_json)
    } else if profile.scene_name == DESERT_DAY_PROFILE.scene_name {
        (&DESERT_DAY_HEIGHTFIELD, profile.heightfield_json)
    } else if profile.scene_name == DOCKS_DAY_PROFILE.scene_name {
        (&DOCKS_DAY_HEIGHTFIELD, profile.heightfield_json)
    } else if profile.scene_name == GOLDEN_VALLEY_OVERCAST_PROFILE.scene_name {
        (
            &GOLDEN_VALLEY_OVERCAST_HEIGHTFIELD,
            profile.heightfield_json,
        )
    } else if profile.scene_name == GOLDEN_VALLEY_SUNNY_PROFILE.scene_name {
        (&GOLDEN_VALLEY_SUNNY_HEIGHTFIELD, profile.heightfield_json)
    } else if profile.scene_name == GREAT_HALL_DAY_PROFILE.scene_name {
        (&GREAT_HALL_DAY_HEIGHTFIELD, profile.heightfield_json)
    } else if profile.scene_name == IDOL_DAY_PROFILE.scene_name {
        (&IDOL_DAY_HEIGHTFIELD, profile.heightfield_json)
    } else if profile.scene_name == TEMPLE_GARDENS_PROFILE.scene_name {
        (&TEMPLE_GARDENS_HEIGHTFIELD, profile.heightfield_json)
    } else if profile.scene_name == OASIS_DAY_PROFILE.scene_name {
        (&OPEN_WORLD_HEIGHTFIELD, OPEN_WORLD_HEIGHTFIELD_JSON)
    } else {
        (&OPEN_WORLD_HEIGHTFIELD, OPEN_WORLD_HEIGHTFIELD_JSON)
    };

    heightfield.get_or_init(|| {
        serde_json::from_str(json)
            .expect("open-world scene heightfield JSON must remain valid and schema-compatible")
    })
}

pub fn open_world_seed() -> u32 {
    DEFAULT_SEED
}

#[allow(dead_code)]
pub fn open_world_half_size() -> f32 {
    open_world_half_size_for_profile(default_open_world_scene_profile())
}

pub fn open_world_half_size_for_profile(profile: &OpenWorldSceneProfile) -> f32 {
    if open_world_heightfield_enabled_for_profile(profile) {
        open_world_heightfield_for_profile(profile).size[0].abs() * 0.5
    } else {
        WORLD_SIZE * 0.5
    }
}

#[allow(dead_code)]
pub fn open_world_min_x() -> f32 {
    open_world_min_x_for_profile(default_open_world_scene_profile())
}

pub fn open_world_min_x_for_profile(profile: &OpenWorldSceneProfile) -> f32 {
    if open_world_heightfield_enabled_for_profile(profile) {
        open_world_heightfield_for_profile(profile).origin[0]
    } else {
        -open_world_half_size_for_profile(profile)
    }
}

#[allow(dead_code)]
pub fn open_world_max_x() -> f32 {
    open_world_max_x_for_profile(default_open_world_scene_profile())
}

pub fn open_world_max_x_for_profile(profile: &OpenWorldSceneProfile) -> f32 {
    if open_world_heightfield_enabled_for_profile(profile) {
        open_world_heightfield_for_profile(profile).origin[0]
            + open_world_heightfield_for_profile(profile).size[0]
    } else {
        open_world_half_size_for_profile(profile)
    }
}

#[allow(dead_code)]
pub fn open_world_min_z() -> f32 {
    open_world_min_z_for_profile(default_open_world_scene_profile())
}

pub fn open_world_min_z_for_profile(profile: &OpenWorldSceneProfile) -> f32 {
    if open_world_heightfield_enabled_for_profile(profile) {
        open_world_heightfield_for_profile(profile).origin[2]
    } else {
        -open_world_half_size_for_profile(profile)
    }
}

#[allow(dead_code)]
pub fn open_world_max_z() -> f32 {
    open_world_max_z_for_profile(default_open_world_scene_profile())
}

pub fn open_world_max_z_for_profile(profile: &OpenWorldSceneProfile) -> f32 {
    if open_world_heightfield_enabled_for_profile(profile) {
        open_world_heightfield_for_profile(profile).origin[2]
            + open_world_heightfield_for_profile(profile).size[2]
    } else {
        open_world_half_size_for_profile(profile)
    }
}

pub fn procedural_open_world_enabled() -> bool {
    false
}

#[allow(dead_code)]
pub fn open_world_heightfield_enabled() -> bool {
    open_world_heightfield_enabled_for_profile(default_open_world_scene_profile())
}

pub fn open_world_heightfield_enabled_for_profile(profile: &OpenWorldSceneProfile) -> bool {
    let heightfield = open_world_heightfield_for_profile(profile);
    heightfield.resolution_x >= 2
        && heightfield.resolution_z >= 2
        && heightfield.heights.len() == heightfield.resolution_x * heightfield.resolution_z
}

#[allow(dead_code)]
pub fn open_world_surface_height(x: f32, z: f32) -> f32 {
    open_world_surface_height_for_profile(default_open_world_scene_profile(), x, z)
}

pub fn open_world_surface_height_for_profile(
    profile: &OpenWorldSceneProfile,
    x: f32,
    z: f32,
) -> f32 {
    if !open_world_heightfield_enabled_for_profile(profile) {
        return profile.ground_y;
    }

    let heightfield = open_world_heightfield_for_profile(profile);
    sample_heightfield(heightfield, x, z)
}

fn sample_heightfield(heightfield: &OpenWorldHeightfieldConfig, x: f32, z: f32) -> f32 {
    let width = heightfield.size[0].abs().max(f32::EPSILON);
    let depth = heightfield.size[2].abs().max(f32::EPSILON);

    let normalized_x = ((x - heightfield.origin[0]) / width).clamp(0.0, 1.0);
    let normalized_z = ((z - heightfield.origin[2]) / depth).clamp(0.0, 1.0);
    let sample_x = normalized_x * (heightfield.resolution_x as f32 - 1.0);
    let sample_z = normalized_z * (heightfield.resolution_z as f32 - 1.0);

    let x0 = sample_x.floor() as usize;
    let z0 = sample_z.floor() as usize;
    let x1 = (x0 + 1).min(heightfield.resolution_x - 1);
    let z1 = (z0 + 1).min(heightfield.resolution_z - 1);
    let tx = sample_x - x0 as f32;
    let tz = sample_z - z0 as f32;

    let h00 = heightfield.heights[z0 * heightfield.resolution_x + x0];
    let h10 = heightfield.heights[z0 * heightfield.resolution_x + x1];
    let h01 = heightfield.heights[z1 * heightfield.resolution_x + x0];
    let h11 = heightfield.heights[z1 * heightfield.resolution_x + x1];

    let hx0 = h00 + (h10 - h00) * tx;
    let hx1 = h01 + (h11 - h01) * tx;
    hx0 + (hx1 - hx0) * tz
}

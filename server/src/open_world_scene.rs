pub const OPEN_WORLD_SCENE_NAME: &str = "Oasis_Day";
pub const OPEN_WORLD_DISPLAY_NAME: &str = "Deserted Temples";
pub const ADVENTURE_ISLAND_SCENE_NAME: &str = "Adventure_Island";
pub const ADVENTURE_ISLAND_DISPLAY_NAME: &str = "Adventure Island";
pub const DESERT_DAY_SCENE_NAME: &str = "Desert_Day";
pub const DESERT_DAY_DISPLAY_NAME: &str = "Desert Day";
pub const DOCKS_DAY_SCENE_NAME: &str = "Docks_Day";
pub const DOCKS_DAY_DISPLAY_NAME: &str = "Docks Day";
pub const GIANT_SKELETON_SCENE_NAME: &str = "Giant_Skeleton";
pub const GIANT_SKELETON_DISPLAY_NAME: &str = "Giant Skeleton";
pub const GOLDEN_VALLEY_OVERCAST_SCENE_NAME: &str = "Golden_Valley_Overcast";
pub const GOLDEN_VALLEY_OVERCAST_DISPLAY_NAME: &str = "Golden Valley Overcast";
pub const GOLDEN_VALLEY_SUNNY_SCENE_NAME: &str = "Golden_Valley_Sunny";
pub const GOLDEN_VALLEY_SUNNY_DISPLAY_NAME: &str = "Golden Valley Sunny";
pub const GREAT_HALL_DAY_SCENE_NAME: &str = "Great_Hall_Day";
pub const GREAT_HALL_DAY_DISPLAY_NAME: &str = "Great Hall Day";
pub const IDOL_DAY_SCENE_NAME: &str = "Idol_Day";
pub const IDOL_DAY_DISPLAY_NAME: &str = "Idol Day";
pub const TEMPLE_GARDENS_SCENE_NAME: &str = "Temple_Gardens";
pub const TEMPLE_GARDENS_DISPLAY_NAME: &str = "Temple Gardens";

pub const KNOWN_OPEN_WORLD_SCENES: &[&str] = &[
    ADVENTURE_ISLAND_SCENE_NAME,
    DESERT_DAY_SCENE_NAME,
    DOCKS_DAY_SCENE_NAME,
    GIANT_SKELETON_SCENE_NAME,
    GOLDEN_VALLEY_OVERCAST_SCENE_NAME,
    GOLDEN_VALLEY_SUNNY_SCENE_NAME,
    GREAT_HALL_DAY_SCENE_NAME,
    IDOL_DAY_SCENE_NAME,
    OPEN_WORLD_SCENE_NAME,
    TEMPLE_GARDENS_SCENE_NAME,
];

const NO_HEIGHTFIELD_JSON: &str = r#"{"version":1,"origin":[0.0,0.0,0.0],"size":[0.0,0.0,0.0],"resolution_x":0,"resolution_z":0,"heights":[]}"#;
const OASIS_DAY_HEIGHTFIELD_JSON: &str =
    include_str!("world_data/oasis_day.heightfield.shared.json");
const OASIS_DAY_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/oasis_day.collision.shared.json");
const ADVENTURE_ISLAND_HEIGHTFIELD_JSON: &str =
    include_str!("world_data/adventure_island.heightfield.shared.json");
const ADVENTURE_ISLAND_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/adventure_island.collision.shared.json");
const DESERT_DAY_HEIGHTFIELD_JSON: &str =
    include_str!("world_data/desert_day.heightfield.shared.json");
const DESERT_DAY_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/desert_day.collision.shared.json");
const DOCKS_DAY_HEIGHTFIELD_JSON: &str =
    include_str!("world_data/docks_day.heightfield.shared.json");
const DOCKS_DAY_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/docks_day.collision.shared.json");
const GIANT_SKELETON_HEIGHTFIELD_JSON: &str =
    include_str!("world_data/giant_skeleton.heightfield.shared.json");
const GIANT_SKELETON_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/giant_skeleton.collision.shared.json");
const GOLDEN_VALLEY_SUNNY_HEIGHTFIELD_JSON: &str =
    include_str!("world_data/golden_valley_sunny.heightfield.shared.json");
const GOLDEN_VALLEY_SUNNY_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/golden_valley_sunny.collision.shared.json");
const GREAT_HALL_DAY_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/great_hall_day.collision.shared.json");
const IDOL_DAY_HEIGHTFIELD_JSON: &str = include_str!("world_data/idol_day.heightfield.shared.json");
const IDOL_DAY_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/idol_day.collision.shared.json");
const TEMPLE_GARDENS_GAMEPLAY_COLLISION_JSON: &str =
    include_str!("world_data/temple_gardens.collision.shared.json");

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct OpenWorldSceneProfile {
    pub scene_name: &'static str,
    pub display_name: &'static str,
    pub spawn_x: f32,
    pub ground_y: f32,
    pub spawn_z: f32,
    pub spawn_yaw: f32,
    pub heightfield_json: &'static str,
    pub gameplay_collision_json: &'static str,
    pub use_procedural_fallback_colliders: bool,
}

pub const OASIS_DAY_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: OPEN_WORLD_SCENE_NAME,
    display_name: OPEN_WORLD_DISPLAY_NAME,
    spawn_x: 62.22,
    ground_y: 12.358,
    spawn_z: 79.47,
    spawn_yaw: 0.0,
    heightfield_json: OASIS_DAY_HEIGHTFIELD_JSON,
    gameplay_collision_json: OASIS_DAY_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: true,
};

pub const ADVENTURE_ISLAND_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: ADVENTURE_ISLAND_SCENE_NAME,
    display_name: ADVENTURE_ISLAND_DISPLAY_NAME,
    spawn_x: -176.3932,
    ground_y: 0.0,
    spawn_z: 77.95966,
    spawn_yaw: 0.0,
    heightfield_json: ADVENTURE_ISLAND_HEIGHTFIELD_JSON,
    gameplay_collision_json: ADVENTURE_ISLAND_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: true,
};

pub const DESERT_DAY_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: DESERT_DAY_SCENE_NAME,
    display_name: DESERT_DAY_DISPLAY_NAME,
    spawn_x: -35.535,
    ground_y: 1.8,
    spawn_z: 11.47,
    spawn_yaw: 0.0,
    heightfield_json: DESERT_DAY_HEIGHTFIELD_JSON,
    gameplay_collision_json: DESERT_DAY_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: true,
};

pub const DOCKS_DAY_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: DOCKS_DAY_SCENE_NAME,
    display_name: DOCKS_DAY_DISPLAY_NAME,
    spawn_x: 413.772,
    ground_y: 52.608,
    spawn_z: 370.805,
    spawn_yaw: 0.0,
    heightfield_json: DOCKS_DAY_HEIGHTFIELD_JSON,
    gameplay_collision_json: DOCKS_DAY_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: true,
};

pub const GIANT_SKELETON_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: GIANT_SKELETON_SCENE_NAME,
    display_name: GIANT_SKELETON_DISPLAY_NAME,
    spawn_x: 24.946,
    ground_y: 8.996,
    spawn_z: -87.789,
    spawn_yaw: 0.0,
    heightfield_json: GIANT_SKELETON_HEIGHTFIELD_JSON,
    gameplay_collision_json: GIANT_SKELETON_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: true,
};

pub const GOLDEN_VALLEY_OVERCAST_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: GOLDEN_VALLEY_OVERCAST_SCENE_NAME,
    display_name: GOLDEN_VALLEY_OVERCAST_DISPLAY_NAME,
    spawn_x: 356.842,
    ground_y: 86.013,
    spawn_z: 330.988,
    spawn_yaw: 0.0,
    heightfield_json: GOLDEN_VALLEY_SUNNY_HEIGHTFIELD_JSON,
    gameplay_collision_json: GOLDEN_VALLEY_SUNNY_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: true,
};

pub const GOLDEN_VALLEY_SUNNY_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: GOLDEN_VALLEY_SUNNY_SCENE_NAME,
    display_name: GOLDEN_VALLEY_SUNNY_DISPLAY_NAME,
    spawn_x: 356.842,
    ground_y: 86.013,
    spawn_z: 330.988,
    spawn_yaw: 0.0,
    heightfield_json: GOLDEN_VALLEY_SUNNY_HEIGHTFIELD_JSON,
    gameplay_collision_json: GOLDEN_VALLEY_SUNNY_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: true,
};

pub const GREAT_HALL_DAY_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: GREAT_HALL_DAY_SCENE_NAME,
    display_name: GREAT_HALL_DAY_DISPLAY_NAME,
    spawn_x: 27.34,
    ground_y: -5.94,
    spawn_z: -3.661,
    spawn_yaw: 0.0,
    heightfield_json: NO_HEIGHTFIELD_JSON,
    gameplay_collision_json: GREAT_HALL_DAY_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: false,
};

pub const IDOL_DAY_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: IDOL_DAY_SCENE_NAME,
    display_name: IDOL_DAY_DISPLAY_NAME,
    spawn_x: 328.09,
    ground_y: 70.01,
    spawn_z: 233.949,
    spawn_yaw: 0.0,
    heightfield_json: IDOL_DAY_HEIGHTFIELD_JSON,
    gameplay_collision_json: IDOL_DAY_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: true,
};

pub const TEMPLE_GARDENS_PROFILE: OpenWorldSceneProfile = OpenWorldSceneProfile {
    scene_name: TEMPLE_GARDENS_SCENE_NAME,
    display_name: TEMPLE_GARDENS_DISPLAY_NAME,
    spawn_x: -1.06860,
    ground_y: 10.0709,
    spawn_z: 129.2195,
    spawn_yaw: 0.0,
    heightfield_json: NO_HEIGHTFIELD_JSON,
    gameplay_collision_json: TEMPLE_GARDENS_GAMEPLAY_COLLISION_JSON,
    use_procedural_fallback_colliders: false,
};

pub const OPEN_WORLD_SPAWN_X: f32 = OASIS_DAY_PROFILE.spawn_x;
pub const OPEN_WORLD_SPAWN_Z: f32 = OASIS_DAY_PROFILE.spawn_z;
pub const OPEN_WORLD_SPAWN_YAW: f32 = OASIS_DAY_PROFILE.spawn_yaw;
pub const OPEN_WORLD_HEIGHTFIELD_JSON: &str = OASIS_DAY_PROFILE.heightfield_json;
pub const OPEN_WORLD_GAMEPLAY_COLLISION_JSON: &str = OASIS_DAY_PROFILE.gameplay_collision_json;

pub fn open_world_scene_profile_for_scene(
    scene_name: &str,
) -> Option<&'static OpenWorldSceneProfile> {
    OPEN_WORLD_SCENE_PROFILES
        .iter()
        .find(|profile| profile.scene_name == scene_name)
}

pub fn is_known_open_world_scene(scene_name: &str) -> bool {
    KNOWN_OPEN_WORLD_SCENES
        .iter()
        .any(|known_scene| *known_scene == scene_name)
}

pub fn default_open_world_scene_profile() -> &'static OpenWorldSceneProfile {
    &OASIS_DAY_PROFILE
}

pub const OPEN_WORLD_SCENE_PROFILES: &[OpenWorldSceneProfile] = &[
    OASIS_DAY_PROFILE,
    ADVENTURE_ISLAND_PROFILE,
    DESERT_DAY_PROFILE,
    DOCKS_DAY_PROFILE,
    GIANT_SKELETON_PROFILE,
    GOLDEN_VALLEY_OVERCAST_PROFILE,
    GOLDEN_VALLEY_SUNNY_PROFILE,
    GREAT_HALL_DAY_PROFILE,
    IDOL_DAY_PROFILE,
    TEMPLE_GARDENS_PROFILE,
];

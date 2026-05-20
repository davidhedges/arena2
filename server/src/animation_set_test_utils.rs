use std::collections::HashMap;
use std::fs;
use std::path::Path;
use std::sync::OnceLock;

use crate::action_ids::normalize_authored_action_id;

pub(crate) fn parse_top_level_animation_set_field(
    asset_contents: &str,
    field_name: &str,
) -> Option<String> {
    let prefix = format!("  {field_name}: ");
    asset_contents
        .lines()
        .find_map(|line| line.strip_prefix(prefix.as_str()))
        .map(|value| normalize_authored_action_id(value.trim()))
        .filter(|value| !value.is_empty())
}

pub(crate) fn animation_set_assets_by_combat_profile() -> &'static HashMap<String, String> {
    static ASSETS: OnceLock<HashMap<String, String>> = OnceLock::new();
    ASSETS.get_or_init(|| {
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("Assets")
            .join("Arena")
            .join("Resources")
            .join("CombatAnimationSets");
        let mut assets = HashMap::new();
        for entry in fs::read_dir(root.as_path()).unwrap_or_else(|error| {
            panic!(
                "failed to read combat animation set folder '{}': {error}",
                root.display()
            )
        }) {
            let path = entry
                .expect("combat animation set directory entry must read")
                .path();
            if path.extension().and_then(|value| value.to_str()) != Some("asset") {
                continue;
            }

            let contents = fs::read_to_string(path.as_path()).unwrap_or_else(|error| {
                panic!(
                    "failed to read combat animation set asset '{}': {error}",
                    path.display()
                )
            });
            if let Some(combat_profile) =
                parse_top_level_animation_set_field(contents.as_str(), "combatProfileId")
            {
                assets.insert(combat_profile, contents);
            }
        }

        assert!(
            !assets.is_empty(),
            "expected at least one CombatAnimationSet asset with combatProfileId"
        );
        assets
    })
}

use spacetimedb::{table, ReducerContext, Table, Timestamp};

#[allow(unused_imports)]
use crate::contract_version::contract_version as _;

/// Netcode audit R5: content stamps for the shared-JSON contract surface.
/// One static row per `*.shared.json` compiled into the module. The client
/// hashes its bundled copies on connect and warns on mismatch — the shared
/// collision/heightfield data must match exactly for movement parity, and
/// the sync step (`GameplayCollisionExporter.SyncSharedMovementData`) is
/// easy to forget.
#[table(accessor = contract_version, public)]
#[derive(Clone)]
pub struct ContractVersion {
    #[primary_key]
    pub key: String,
    pub content_hash: u64,
    pub stamped_at: Timestamp,
}

/// `(key, contents)` for every shared JSON compiled into the module. Keys
/// are server-relative paths; the client maps `world_data/` to its bundled
/// `SharedData/Worlds/` copies and ignores keys it has no copy of.
/// Completeness is enforced by `shared_files_list_is_complete`.
const SHARED_FILES: &[(&str, &str)] = &[
    (
        "arena_layout.shared.json",
        include_str!("arena_layout.shared.json"),
    ),
    (
        "gameplay_collision.shared.json",
        include_str!("gameplay_collision.shared.json"),
    ),
    (
        "gameplay_query_collision.shared.json",
        include_str!("gameplay_query_collision.shared.json"),
    ),
    (
        "melee_manifest.shared.json",
        include_str!("melee_manifest.shared.json"),
    ),
    (
        "open_world_heightfield.shared.json",
        include_str!("open_world_heightfield.shared.json"),
    ),
    (
        "progression_catalog.shared.json",
        include_str!("progression_catalog.shared.json"),
    ),
    (
        "world_data/adventure_island.collision.shared.json",
        include_str!("world_data/adventure_island.collision.shared.json"),
    ),
    (
        "world_data/adventure_island.heightfield.shared.json",
        include_str!("world_data/adventure_island.heightfield.shared.json"),
    ),
    (
        "world_data/adventure_island.query_collision.shared.json",
        include_str!("world_data/adventure_island.query_collision.shared.json"),
    ),
    (
        "world_data/desert_day.collision.shared.json",
        include_str!("world_data/desert_day.collision.shared.json"),
    ),
    (
        "world_data/desert_day.heightfield.shared.json",
        include_str!("world_data/desert_day.heightfield.shared.json"),
    ),
    (
        "world_data/desert_day.query_collision.shared.json",
        include_str!("world_data/desert_day.query_collision.shared.json"),
    ),
    (
        "world_data/docks_day.collision.shared.json",
        include_str!("world_data/docks_day.collision.shared.json"),
    ),
    (
        "world_data/docks_day.heightfield.shared.json",
        include_str!("world_data/docks_day.heightfield.shared.json"),
    ),
    (
        "world_data/docks_day.query_collision.shared.json",
        include_str!("world_data/docks_day.query_collision.shared.json"),
    ),
    (
        "world_data/giant_skeleton.collision.shared.json",
        include_str!("world_data/giant_skeleton.collision.shared.json"),
    ),
    (
        "world_data/giant_skeleton.heightfield.shared.json",
        include_str!("world_data/giant_skeleton.heightfield.shared.json"),
    ),
    (
        "world_data/giant_skeleton.query_collision.shared.json",
        include_str!("world_data/giant_skeleton.query_collision.shared.json"),
    ),
    (
        "world_data/golden_valley_sunny.collision.shared.json",
        include_str!("world_data/golden_valley_sunny.collision.shared.json"),
    ),
    (
        "world_data/golden_valley_sunny.heightfield.shared.json",
        include_str!("world_data/golden_valley_sunny.heightfield.shared.json"),
    ),
    (
        "world_data/golden_valley_sunny.query_collision.shared.json",
        include_str!("world_data/golden_valley_sunny.query_collision.shared.json"),
    ),
    (
        "world_data/grasslands.collision.shared.json",
        include_str!("world_data/grasslands.collision.shared.json"),
    ),
    (
        "world_data/grasslands.heightfield.shared.json",
        include_str!("world_data/grasslands.heightfield.shared.json"),
    ),
    (
        "world_data/great_hall_day.collision.shared.json",
        include_str!("world_data/great_hall_day.collision.shared.json"),
    ),
    (
        "world_data/great_hall_day.query_collision.shared.json",
        include_str!("world_data/great_hall_day.query_collision.shared.json"),
    ),
    (
        "world_data/idol_day.collision.shared.json",
        include_str!("world_data/idol_day.collision.shared.json"),
    ),
    (
        "world_data/idol_day.heightfield.shared.json",
        include_str!("world_data/idol_day.heightfield.shared.json"),
    ),
    (
        "world_data/idol_day.query_collision.shared.json",
        include_str!("world_data/idol_day.query_collision.shared.json"),
    ),
    (
        "world_data/oasis_day.collision.shared.json",
        include_str!("world_data/oasis_day.collision.shared.json"),
    ),
    (
        "world_data/oasis_day.heightfield.shared.json",
        include_str!("world_data/oasis_day.heightfield.shared.json"),
    ),
    (
        "world_data/oasis_day.query_collision.shared.json",
        include_str!("world_data/oasis_day.query_collision.shared.json"),
    ),
    (
        "world_data/temple_gardens.collision.shared.json",
        include_str!("world_data/temple_gardens.collision.shared.json"),
    ),
    (
        "world_data/temple_gardens.query_collision.shared.json",
        include_str!("world_data/temple_gardens.query_collision.shared.json"),
    ),
];

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

/// FNV-1a over the file bytes, skipping CR so checkouts with different line
/// endings hash identically. Mirrored by `ContractVersionGuard` on the
/// client — keep the two implementations in sync.
pub(crate) fn shared_content_hash(contents: &str) -> u64 {
    let mut hash = FNV_OFFSET;
    for byte in contents.bytes() {
        if byte == b'\r' {
            continue;
        }
        hash ^= u64::from(byte);
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

/// Change-gated upsert: writes nothing when stamps are current. `init`
/// covers clean publishes; the `client_connected` call covers hot module
/// updates, which do not re-run `init`.
pub(crate) fn sync_contract_versions(ctx: &ReducerContext) {
    for (key, contents) in SHARED_FILES {
        let content_hash = shared_content_hash(contents);
        match ctx.db.contract_version().key().find((*key).to_string()) {
            Some(existing) if existing.content_hash == content_hash => {}
            Some(existing) => {
                ctx.db.contract_version().key().update(ContractVersion {
                    content_hash,
                    stamped_at: ctx.timestamp,
                    ..existing
                });
            }
            None => {
                ctx.db.contract_version().insert(ContractVersion {
                    key: (*key).to_string(),
                    content_hash,
                    stamped_at: ctx.timestamp,
                });
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Every shared JSON compiled anywhere into the module must be stamped.
    /// Native-only: walks the crate source tree, which wasm cannot do.
    #[test]
    fn shared_files_list_is_complete() {
        let src_root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut on_disk = Vec::new();
        collect_shared_json(&src_root, &src_root, &mut on_disk);
        on_disk.sort();

        let mut listed: Vec<String> = SHARED_FILES
            .iter()
            .map(|(key, _)| (*key).to_string())
            .collect();
        listed.sort();

        assert_eq!(
            listed, on_disk,
            "SHARED_FILES is out of sync with src/**/*.shared.json — update contract_version.rs"
        );
    }

    #[test]
    fn shared_content_hash_ignores_carriage_returns() {
        assert_eq!(shared_content_hash("a\r\nb"), shared_content_hash("a\nb"));
        assert_ne!(shared_content_hash("a\nb"), shared_content_hash("ab"));
    }

    fn collect_shared_json(root: &std::path::Path, dir: &std::path::Path, found: &mut Vec<String>) {
        for entry in std::fs::read_dir(dir).expect("readable src dir") {
            let path = entry.expect("readable dir entry").path();
            if path.is_dir() {
                collect_shared_json(root, &path, found);
            } else if path
                .file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.ends_with(".shared.json"))
            {
                found.push(
                    path.strip_prefix(root)
                        .expect("path under src root")
                        .to_string_lossy()
                        .replace('\\', "/"),
                );
            }
        }
    }
}

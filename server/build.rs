use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

/// Large catalogs embedded by the authoritative runtime. Keep the checked-in
/// copies readable while removing formatting whitespace from the bytes that
/// ship in release modules.
const COMPACT_RUNTIME_CATALOGS: &[(&str, &str)] = &[
    (
        "combat_build_v2_catalog.shared.json",
        "combat_build_v2_catalog.shared.json",
    ),
    (
        "progression_catalog.shared.json",
        "progression_catalog.shared.json",
    ),
    ("npc_catalog.shared.json", "npc_catalog.shared.json"),
    ("melee_manifest.shared.json", "melee_manifest.shared.json"),
];

const COMPACT_WEAPON_CATALOG_OUTPUT: &str = "weapon_appearance_catalog.shared.json";

/// Shared JSON the module compiles in from OUTSIDE `src/`, as
/// `(contract key, path relative to the crate manifest)`. The `src` walk
/// cannot see these, but the client bundles and verifies them, so an unstamped
/// one is reported by `ContractVersionGuard` as a missing server stamp.
const EXTERNAL_SHARED_INPUTS: &[(&str, &str)] = &[
    // inventory.rs includes the Unity Resources copy directly.
    (
        "weapon_appearance_catalog.shared.json",
        "../Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json",
    ),
];

fn main() {
    let manifest_dir =
        PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR must be set"));
    // The dedicated PvP module compiles this same authoritative source tree
    // through match-server/Cargo.toml. Keep shared-content hashes identical
    // without copying either gameplay code or build logic into that crate.
    let src_root = if std::env::var("CARGO_PKG_NAME").as_deref() == Ok("arena-pvp-match") {
        manifest_dir.join("../server/src")
    } else {
        manifest_dir.join("src")
    };
    let out_dir = PathBuf::from(std::env::var("OUT_DIR").expect("OUT_DIR must be set"));

    for (source_name, output_name) in COMPACT_RUNTIME_CATALOGS {
        write_compact_json(&src_root.join(source_name), &out_dir.join(output_name));
    }
    write_compact_json(
        &manifest_dir.join(EXTERNAL_SHARED_INPUTS[0].1),
        &out_dir.join(COMPACT_WEAPON_CATALOG_OUTPUT),
    );

    println!("cargo:rerun-if-changed={}", src_root.display());
    let mut files = Vec::new();
    collect_shared_json(&src_root, &src_root, &mut files);
    for (key, relative_path) in EXTERNAL_SHARED_INPUTS {
        let path = manifest_dir.join(relative_path);
        println!("cargo:rerun-if-changed={}", path.display());
        let bytes = fs::read(&path).unwrap_or_else(|error| {
            panic!(
                "external shared json {} must be readable: {error}",
                path.display()
            )
        });
        files.push(((*key).to_string(), shared_content_hash(&bytes)));
    }
    files.sort_by(|a, b| a.0.cmp(&b.0));

    let generated_path = out_dir.join("shared_file_hashes.rs");
    let mut generated = fs::File::create(&generated_path)
        .expect("failed to create generated shared_file_hashes.rs");

    writeln!(
        generated,
        "pub(crate) const SHARED_FILE_HASHES: &[(&str, u64)] = &["
    )
    .expect("failed to write generated header");
    for (key, hash) in files {
        writeln!(generated, "    ({key:?}, 0x{hash:016x}),")
            .expect("failed to write generated shared file hash");
    }
    writeln!(generated, "];").expect("failed to write generated footer");
}

fn write_compact_json(source_path: &Path, output_path: &Path) {
    println!("cargo:rerun-if-changed={}", source_path.display());
    let source = fs::read(source_path).unwrap_or_else(|error| {
        panic!(
            "shared json {} must be readable: {error}",
            source_path.display()
        )
    });
    let mut parsed_source =
        serde_json::from_slice::<serde_json::Value>(&source).unwrap_or_else(|error| {
            panic!(
                "shared json {} must be valid: {error}",
                source_path.display()
            )
        });

    let compact = if source_path.file_name().and_then(|name| name.to_str())
        == Some("progression_catalog.shared.json")
    {
        // These two fields belong to Unity authoring, not the runtime cue contract.
        // Keep the existing compiled catalog projection free of ownership prose.
        // Hashing below still stamps the complete authored source, as before.
        for cue in parsed_source["combat_vfx_cues"]
            .as_array_mut()
            .expect("progression catalog must contain combat_vfx_cues")
        {
            let fields = cue.as_object_mut().expect("VFX cue must be an object");
            fields.remove("authoring_mode");
            fields.remove("authoring_reason");
        }
        serde_json::to_vec(&parsed_source).expect("runtime progression catalog must serialize")
    } else {
        strip_json_formatting_whitespace(&source)
    };
    let parsed_compact =
        serde_json::from_slice::<serde_json::Value>(&compact).unwrap_or_else(|error| {
            panic!(
                "compact shared json derived from {} must be valid: {error}",
                source_path.display()
            )
        });
    assert_eq!(
        parsed_compact,
        parsed_source,
        "compact shared json derived from {} must preserve its value",
        source_path.display()
    );
    fs::write(output_path, compact).unwrap_or_else(|error| {
        panic!(
            "compact shared json {} must be writable: {error}",
            output_path.display()
        )
    });
}

fn strip_json_formatting_whitespace(source: &[u8]) -> Vec<u8> {
    let mut compact = Vec::with_capacity(source.len());
    let mut in_string = false;
    let mut escaped = false;

    for &byte in source {
        if in_string {
            compact.push(byte);
            if escaped {
                escaped = false;
            } else if byte == b'\\' {
                escaped = true;
            } else if byte == b'"' {
                in_string = false;
            }
        } else if byte == b'"' {
            in_string = true;
            compact.push(byte);
        } else if !matches!(byte, b' ' | b'\n' | b'\r' | b'\t') {
            compact.push(byte);
        }
    }

    compact
}

fn collect_shared_json(root: &Path, dir: &Path, found: &mut Vec<(String, u64)>) {
    for entry in fs::read_dir(dir).expect("readable src dir") {
        let path = entry.expect("readable dir entry").path();
        if path.is_dir() {
            collect_shared_json(root, &path, found);
            continue;
        }

        let Some(file_name) = path.file_name().and_then(|name| name.to_str()) else {
            continue;
        };
        if !file_name.ends_with(".shared.json") {
            continue;
        }

        println!("cargo:rerun-if-changed={}", path.display());
        let key = path
            .strip_prefix(root)
            .expect("path under src root")
            .to_string_lossy()
            .replace('\\', "/");
        let bytes = fs::read(&path).expect("shared json must be readable");
        found.push((key, shared_content_hash(&bytes)));
    }
}

fn shared_content_hash(contents: &[u8]) -> u64 {
    let mut hash = FNV_OFFSET;
    for byte in contents {
        if *byte == b'\r' {
            continue;
        }
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

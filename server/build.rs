use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

fn main() {
    let manifest_dir =
        PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR must be set"));
    let src_root = manifest_dir.join("src");
    println!("cargo:rerun-if-changed={}", src_root.display());
    let mut files = Vec::new();
    collect_shared_json(&src_root, &src_root, &mut files);
    files.sort_by(|a, b| a.0.cmp(&b.0));

    let out_dir = PathBuf::from(std::env::var("OUT_DIR").expect("OUT_DIR must be set"));
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

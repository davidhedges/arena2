#!/usr/bin/env bash
# Validate authored/derived catalog boundaries without Unity or live databases.
# Requires Python 3, Ruby with Minitest, and the repository's Rust toolchain.
set -euo pipefail

truth_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$truth_repo_root"

python3 -m unittest ops.test_combat_build_v2_catalog ops.test_shared_data_contracts ops.test_hub_state_snapshot
python3 ops/generate-combat-build-v2-catalog.py --check
python3 ops/verify-spacetimedb-contracts.py --offline

ruby ops/test_npc_profile_paths.rb
ruby ops/generate-npc-family-profiles.rb --check-paths

cargo test --manifest-path server/Cargo.toml --locked --lib --no-fail-fast
cargo test --manifest-path hub-server/Cargo.toml --locked --lib --no-fail-fast

echo "Source-of-truth checks passed."

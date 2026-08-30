#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODULE="$REPO_ROOT/hub-v2-rehearsal"
WASM="$MODULE/target/wasm32-unknown-unknown/release/arena_hub_v2_rehearsal.wasm"
OUTPUT="$REPO_ROOT/Assets/Arena/Runtime/Generated/RehearsalHubV2SpacetimeDB"

echo "Building isolated Combat Build v2 Hub rehearsal module..."
spacetime build -p "$MODULE"
test -f "$WASM"

echo "Generating Phase 6 rehearsal-only Unity bindings..."
spacetime generate \
    --yes \
    --lang csharp \
    --namespace Arena.RehearsalHubV2Db \
    --bin-path "$WASM" \
    --out-dir "$OUTPUT"

echo "Generated rehearsal bindings at $OUTPUT"

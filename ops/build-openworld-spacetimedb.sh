#!/usr/bin/env bash
# Builds the cached module a disposable open-world instance runs.
#
# That module is the MAIN server module, not match-server: every open-world
# reducer (set_open_world_scene, the world terrain/collision catalogs) is
# compiled out of the PvP flavor, and the main module already matches the
# client's SpacetimeDB.Types bindings. See
# docs/open-world-disposable-instances-2026-08-18.md section 3.
#
# Unity bindings are deliberately NOT generated here: ops/republish-local-clear.sh
# owns the canonical harness-featured regeneration for this module.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OPENWORLD_MODULE_PATH="${OPENWORLD_MODULE_PATH:-$ROOT_DIR/server}"
OPENWORLD_RAW_WASM_PATH="${OPENWORLD_RAW_WASM_PATH:-$OPENWORLD_MODULE_PATH/target/wasm32-unknown-unknown/release/arena.wasm}"
OPENWORLD_WASM_PATH="${OPENWORLD_WASM_PATH:-$OPENWORLD_MODULE_PATH/target/wasm32-unknown-unknown/release/arena.opt.wasm}"
OPENWORLD_DEPFILE_PATH="${OPENWORLD_DEPFILE_PATH:-$OPENWORLD_MODULE_PATH/target/wasm32-unknown-unknown/release/arena.d}"
OPENWORLD_PROVENANCE_PATH="${OPENWORLD_PROVENANCE_PATH:-$OPENWORLD_WASM_PATH.inputs.json}"

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required." >&2
    exit 2
fi
if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is required." >&2
    exit 2
fi

WASM_OPT_BIN="$("$ROOT_DIR/ops/find-wasm-opt.sh")"
export PATH="$(dirname "$WASM_OPT_BIN"):$PATH"

echo "Building disposable open-world module..."
spacetime build -p "$OPENWORLD_MODULE_PATH"

if [ ! -f "$OPENWORLD_RAW_WASM_PATH" ]; then
    echo "Raw open-world WASM not found at $OPENWORLD_RAW_WASM_PATH" >&2
    exit 1
fi

# The provisioner re-uploads this artifact for every instance it publishes, so
# the size pass is worth its build time even though nothing gates on a ceiling.
echo "Applying the canonical size-oriented Binaryen pass..."
"$WASM_OPT_BIN" \
    -Oz \
    --strip-debug \
    --strip-producers \
    "$OPENWORLD_RAW_WASM_PATH" \
    -o "$OPENWORLD_WASM_PATH"

echo "Recording the source inputs for the cached open-world artifact..."
python3 "$ROOT_DIR/match_provisioner/artifact_provenance.py" write \
    --workspace-root "$ROOT_DIR" \
    --depfile "$OPENWORLD_DEPFILE_PATH" \
    --wasm "$OPENWORLD_WASM_PATH" \
    --manifest "$OPENWORLD_PROVENANCE_PATH"

printf 'Built optimized disposable open-world module at %s (%s bytes).\n' \
    "$OPENWORLD_WASM_PATH" \
    "$(wc -c < "$OPENWORLD_WASM_PATH" | tr -d '[:space:]')"

#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ARENA_HARNESS_DATABASE="${ARENA_HARNESS_DATABASE:-arena-spellcasting-terminal-harness}"
ARENA_HARNESS_SERVER="${ARENA_HARNESS_SERVER:-local}"
ARENA_HARNESS_WASM_PATH="${ARENA_HARNESS_WASM_PATH:-$ROOT_DIR/server/target/wasm32-unknown-unknown/release/arena.wasm}"
ARENA_HARNESS_KEEP_DATABASE="${ARENA_HARNESS_KEEP_DATABASE:-0}"
ARENA_HARNESS_CLEANUP_ARMED=0

if ! command -v cargo >/dev/null 2>&1; then
    echo "cargo is required."
    exit 2
fi

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required."
    exit 2
fi

echo "Building harness-enabled SpacetimeDB module..."
cargo build \
    --manifest-path "$ROOT_DIR/server/Cargo.toml" \
    --target wasm32-unknown-unknown \
    --release \
    --features spellcasting_terminal_harness

if [ ! -f "$ARENA_HARNESS_WASM_PATH" ]; then
    echo "WASM not found at $ARENA_HARNESS_WASM_PATH"
    exit 1
fi

echo "Publishing temporary harness database '$ARENA_HARNESS_DATABASE' on '$ARENA_HARNESS_SERVER'..."
cleanup() {
    if [ "$ARENA_HARNESS_CLEANUP_ARMED" != "1" ]; then
        return
    fi

    if [ "$ARENA_HARNESS_KEEP_DATABASE" = "1" ]; then
        echo "Keeping harness database '$ARENA_HARNESS_DATABASE'."
        return
    fi

    echo "Deleting harness database '$ARENA_HARNESS_DATABASE'..."
    spacetime delete -s "$ARENA_HARNESS_SERVER" --yes "$ARENA_HARNESS_DATABASE" >/dev/null || true
}
trap cleanup EXIT

ARENA_HARNESS_CLEANUP_ARMED=1
spacetime publish \
    -s "$ARENA_HARNESS_SERVER" \
    --bin-path "$ARENA_HARNESS_WASM_PATH" \
    --delete-data=always \
    --yes \
    "$ARENA_HARNESS_DATABASE"

echo "Invoking run_spellcasting_terminal_harness..."
spacetime call \
    -s "$ARENA_HARNESS_SERVER" \
    --yes \
    "$ARENA_HARNESS_DATABASE" \
    run_spellcasting_terminal_harness

echo "Spellcasting terminal harness passed."

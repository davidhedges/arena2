#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HUB_MODULE_PATH="${HUB_MODULE_PATH:-$ROOT_DIR/hub-server}"
HUB_WASM_PATH="${HUB_WASM_PATH:-$HUB_MODULE_PATH/target/wasm32-unknown-unknown/release/arena_hub.wasm}"
HUB_GENERATED_OUT="${HUB_GENERATED_OUT:-$ROOT_DIR/Assets/Arena/Runtime/Generated/HubSpacetimeDB}"
HUB_GENERATE_BINDINGS="${HUB_GENERATE_BINDINGS:-1}"

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required." >&2
    exit 2
fi

echo "Building persistent Hub module..."
spacetime build -p "$HUB_MODULE_PATH"

if [ ! -f "$HUB_WASM_PATH" ]; then
    echo "Hub WASM not found at $HUB_WASM_PATH" >&2
    exit 1
fi

if [ "$HUB_GENERATE_BINDINGS" = "1" ]; then
    echo "Generating Hub Unity bindings in namespace Arena.HubDb..."
    spacetime generate \
        --yes \
        --lang csharp \
        --namespace Arena.HubDb \
        --bin-path "$HUB_WASM_PATH" \
        --out-dir "$HUB_GENERATED_OUT"
fi

echo "Built persistent Hub module (bindings=$HUB_GENERATE_BINDINGS)."

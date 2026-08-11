#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

HUB_DATABASE="${HUB_DATABASE:-arena-hub-local}"
HUB_SERVER="${HUB_SERVER:-local}"
HUB_DELETE_DATA="${HUB_DELETE_DATA:-never}"
HUB_AUTO_START="${HUB_AUTO_START:-1}"
HUB_GENERATE_BINDINGS="${HUB_GENERATE_BINDINGS:-1}"
HUB_MODULE_PATH="$ROOT_DIR/hub-server"
HUB_WASM_PATH="$HUB_MODULE_PATH/target/wasm32-unknown-unknown/release/arena_hub.wasm"

case "$HUB_DELETE_DATA" in
    always|on-conflict|never)
        ;;
    *)
        echo "Invalid HUB_DELETE_DATA='$HUB_DELETE_DATA' (expected always, on-conflict, or never)." >&2
        exit 2
        ;;
esac

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required." >&2
    exit 2
fi

if ! spacetime server ping "$HUB_SERVER" >/dev/null 2>&1; then
    if [ "$HUB_SERVER" != "local" ] || [ "$HUB_AUTO_START" != "1" ]; then
        echo "SpacetimeDB server '$HUB_SERVER' is not reachable." >&2
        exit 2
    fi

    server_log="${TMPDIR:-/tmp}/arena-hub-spacetimedb.log"
    echo "Starting local SpacetimeDB (log: $server_log)..."
    nohup spacetime start --non-interactive >"$server_log" 2>&1 &
    server_pid=$!
    server_ready=0
    for _ in {1..30}; do
        if spacetime server ping "$HUB_SERVER" >/dev/null 2>&1; then
            server_ready=1
            break
        fi
        sleep 1
    done
    if [ "$server_ready" != "1" ]; then
        kill "$server_pid" 2>/dev/null || true
        echo "Local SpacetimeDB did not become ready (pid $server_pid). See $server_log." >&2
        exit 1
    fi
fi

HUB_GENERATE_BINDINGS=0 "$ROOT_DIR/ops/build-hub-spacetimedb.sh"

echo "Publishing persistent Hub database '$HUB_DATABASE' (delete-data=$HUB_DELETE_DATA)..."
spacetime publish \
    --yes \
    --server "$HUB_SERVER" \
    --delete-data="$HUB_DELETE_DATA" \
    --bin-path "$HUB_WASM_PATH" \
    "$HUB_DATABASE"

if [ "$HUB_GENERATE_BINDINGS" = "1" ]; then
    HUB_GENERATE_BINDINGS=1 "$ROOT_DIR/ops/build-hub-spacetimedb.sh"
fi

echo "Published persistent Hub database '$HUB_DATABASE' locally."

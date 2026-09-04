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
SPACETIME_DATA="${SPACETIME_DATA:-${XDG_DATA_HOME:-$HOME/.local/share}/spacetime/data}"
SPACETIME_LAUNCHD_LABEL="com.arena.local-spacetimedb"

uses_launchd() {
    [ "$(uname -s)" = "Darwin" ] && command -v launchctl >/dev/null 2>&1
}

start_local_spacetimedb() {
    local server_log="$1"

    if uses_launchd; then
        # launchd owns the local server so it survives the shell (including a
        # Codex command) that performed setup. A stale exited job would block
        # launchctl submit from reusing the label.
        if launchctl list "$SPACETIME_LAUNCHD_LABEL" >/dev/null 2>&1; then
            launchctl remove "$SPACETIME_LAUNCHD_LABEL" >/dev/null 2>&1 || true
        fi
        launchctl submit \
            -l "$SPACETIME_LAUNCHD_LABEL" \
            -o "$server_log" \
            -e "$server_log" \
            -- "$(command -v spacetime)" start --non-interactive \
            --data-dir "$SPACETIME_DATA"
        return
    fi

    nohup spacetime start --non-interactive >"$server_log" 2>&1 &
    server_pid=$!
}

stop_failed_local_spacetimedb_start() {
    if uses_launchd; then
        launchctl remove "$SPACETIME_LAUNCHD_LABEL" >/dev/null 2>&1 || true
    elif [ -n "${server_pid:-}" ]; then
        kill "$server_pid" 2>/dev/null || true
    fi
}

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
    server_pid=""
    start_local_spacetimedb "$server_log"
    server_ready=0
    for _ in {1..30}; do
        if spacetime server ping "$HUB_SERVER" >/dev/null 2>&1; then
            server_ready=1
            break
        fi
        sleep 1
    done
    if [ "$server_ready" != "1" ]; then
        stop_failed_local_spacetimedb_start
        echo "Local SpacetimeDB did not become ready. See $server_log." >&2
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

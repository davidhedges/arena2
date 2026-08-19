#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

export ARENA_PROVISIONER_MANAGEMENT_URL="${ARENA_PROVISIONER_MANAGEMENT_URL:-http://127.0.0.1:3000}"
export ARENA_PROVISIONER_CLIENT_URI="${ARENA_PROVISIONER_CLIENT_URI:-ws://127.0.0.1:3000}"
export ARENA_PROVISIONER_HUB_DATABASE="${ARENA_PROVISIONER_HUB_DATABASE:-arena-hub-local}"
export ARENA_PROVISIONER_MAP_ID="${ARENA_PROVISIONER_MAP_ID:-ARENA_MAP_01}"
export ARENA_PROVISIONER_MATCH_WASM="${ARENA_PROVISIONER_MATCH_WASM:-$ROOT_DIR/match-server/target/wasm32-unknown-unknown/release/arena_match.opt.wasm}"
export ARENA_PROVISIONER_MATCH_MANIFEST="${ARENA_PROVISIONER_MATCH_MANIFEST:-$ARENA_PROVISIONER_MATCH_WASM.inputs.json}"
# Disposable open worlds publish the main server module, which is the flavor
# that still contains the open-world reducers.
export ARENA_PROVISIONER_OPENWORLD_WASM="${ARENA_PROVISIONER_OPENWORLD_WASM:-$ROOT_DIR/server/target/wasm32-unknown-unknown/release/arena.opt.wasm}"
export ARENA_PROVISIONER_OPENWORLD_MANIFEST="${ARENA_PROVISIONER_OPENWORLD_MANIFEST:-$ARENA_PROVISIONER_OPENWORLD_WASM.inputs.json}"
export ARENA_PROVISIONER_STATE_DB="${ARENA_PROVISIONER_STATE_DB:-$ROOT_DIR/Library/ArenaMatchProvisioner/state.sqlite3}"

# Deleting a database leaves its replicas/<id>/ directory on disk, so disposed
# instances keep their commitlog and snapshot until something reclaims them.
# Only defaulted when the data directory is actually on this machine; worker.py
# rejects an explicitly configured path that is not a SpacetimeDB data dir.
LOCAL_SPACETIME_DATA="${XDG_DATA_HOME:-$HOME/.local/share}/spacetime/data"
if [ -z "${ARENA_PROVISIONER_REPLICA_GC_DATA_DIR:-}" ] && [ -d "$LOCAL_SPACETIME_DATA/replicas" ]; then
    export ARENA_PROVISIONER_REPLICA_GC_DATA_DIR="$LOCAL_SPACETIME_DATA"
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is required." >&2
    exit 2
fi
if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required." >&2
    exit 2
fi
if ! spacetime server ping local >/dev/null 2>&1; then
    echo "The local SpacetimeDB server is not reachable." >&2
    exit 2
fi
if [ ! -f "$ARENA_PROVISIONER_MATCH_WASM" ]; then
    echo "Prebuilt match WASM not found at $ARENA_PROVISIONER_MATCH_WASM" >&2
    echo "Build it once before starting the provisioner; the provisioner never compiles per match." >&2
    exit 2
fi
if [ ! -f "$ARENA_PROVISIONER_OPENWORLD_WASM" ]; then
    echo "Prebuilt open-world WASM not found at $ARENA_PROVISIONER_OPENWORLD_WASM" >&2
    echo "PvP still works; open-world travel will fail until ops/build-openworld-spacetimedb.sh runs." >&2
fi

if [ -z "${ARENA_PROVISIONER_TOKEN:-}" ]; then
    login_output="$(spacetime login show --token)"
    ARENA_PROVISIONER_TOKEN="$(printf '%s\n' "$login_output" | awk '/^Your auth token / { print $NF }')"
    unset login_output
    if [ -z "$ARENA_PROVISIONER_TOKEN" ]; then
        echo "Could not obtain the current local CLI token." >&2
        exit 2
    fi
    export ARENA_PROVISIONER_TOKEN
fi

cd "$ROOT_DIR"
if [ "$#" -eq 0 ]; then
    set -- run
fi
exec python3 -m match_provisioner.worker "$@"

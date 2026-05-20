#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ARENA_HOST="${ARENA_HOST:-}"
ARENA_SSH_USER="${ARENA_SSH_USER:-arena}"
ARENA_DATABASE="${ARENA_DATABASE:-arena}"
ARENA_REMOTE_WASM="${ARENA_REMOTE_WASM:-/tmp/arena.wasm}"
ARENA_WASM_PATH="${ARENA_WASM_PATH:-$ROOT_DIR/server/target/wasm32-unknown-unknown/release/arena.wasm}"

if [ -z "$ARENA_HOST" ]; then
    echo "ARENA_HOST is required. Example:"
    echo "  ARENA_HOST=203.0.113.10 $0"
    exit 2
fi

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required locally."
    exit 2
fi

echo "Building SpacetimeDB module..."
spacetime build -p "$ROOT_DIR/server"

if [ ! -f "$ARENA_WASM_PATH" ]; then
    echo "WASM not found at $ARENA_WASM_PATH"
    exit 1
fi

SSH_TARGET="$ARENA_SSH_USER@$ARENA_HOST"

echo "Copying module to $SSH_TARGET:$ARENA_REMOTE_WASM..."
scp "$ARENA_WASM_PATH" "$SSH_TARGET:$ARENA_REMOTE_WASM"

echo "Publishing database '$ARENA_DATABASE' on $ARENA_HOST..."
ssh "$SSH_TARGET" \
    "sudo install -o spacetimedb -g spacetimedb -m 0644 '$ARENA_REMOTE_WASM' '/stdb/$ARENA_DATABASE.wasm' && sudo -u spacetimedb env HOME=/stdb /stdb/spacetime --root-dir=/stdb publish -s local --bin-path '/stdb/$ARENA_DATABASE.wasm' '$ARENA_DATABASE'"

echo "Published $ARENA_DATABASE."

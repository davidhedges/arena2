#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ARENA_DATABASE="${ARENA_DATABASE:-arena}"
ARENA_DELETE_DATA="${ARENA_DELETE_DATA:-always}"
ARENA_GENERATE_BINDINGS="${ARENA_GENERATE_BINDINGS:-1}"
ARENA_ENABLE_LOCAL_DIRECT_MODE="${ARENA_ENABLE_LOCAL_DIRECT_MODE:-1}"
ARENA_PROJECTILE_LOAD_HARNESS="${ARENA_PROJECTILE_LOAD_HARNESS:-0}"
ARENA_VERIFY_DOTNET="${ARENA_VERIFY_DOTNET:-1}"
ARENA_SERVER="${ARENA_SERVER:-local}"
ARENA_AUTO_START="${ARENA_AUTO_START:-1}"

MODULE_PATH="$ROOT_DIR/server"
GENERATED_OUT="$ROOT_DIR/Assets/Arena/Runtime/Generated/SpacetimeDB"
WASM_PATH="$ROOT_DIR/server/target/wasm32-unknown-unknown/release/arena.wasm"

case "$ARENA_DELETE_DATA" in
    1|true|TRUE|yes|YES|always)
        delete_data_mode="always"
        ;;
    0|false|FALSE|no|NO|never)
        delete_data_mode="never"
        ;;
    on-conflict)
        delete_data_mode="on-conflict"
        ;;
    *)
        echo "Invalid ARENA_DELETE_DATA='$ARENA_DELETE_DATA' (expected always, on-conflict, never, 1, or 0)." >&2
        exit 2
        ;;
esac

publish_args=(--delete-data="$delete_data_mode" --yes -s "$ARENA_SERVER")

case "$ARENA_ENABLE_LOCAL_DIRECT_MODE" in
    0|1)
        ;;
    *)
        echo "Invalid ARENA_ENABLE_LOCAL_DIRECT_MODE='$ARENA_ENABLE_LOCAL_DIRECT_MODE' (expected 0 or 1)." >&2
        exit 2
        ;;
esac
if [ "$ARENA_ENABLE_LOCAL_DIRECT_MODE" = "1" ] && [ "$ARENA_SERVER" != "local" ]; then
    echo "Temporary local-direct mode may only be enabled on the local server." >&2
    exit 2
fi

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required."
    exit 2
fi

if ! spacetime server ping "$ARENA_SERVER" >/dev/null 2>&1; then
    if [ "$ARENA_SERVER" != "local" ] || [ "$ARENA_AUTO_START" != "1" ]; then
        echo "SpacetimeDB server '$ARENA_SERVER' is not reachable." >&2
        exit 2
    fi

    server_log="${TMPDIR:-/tmp}/arena-spacetimedb.log"
    echo "Starting local SpacetimeDB (log: $server_log)..."
    nohup spacetime start --non-interactive >"$server_log" 2>&1 &
    server_pid=$!
    server_ready=0
    for _ in {1..30}; do
        if spacetime server ping "$ARENA_SERVER" >/dev/null 2>&1; then
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

if [ "$ARENA_PROJECTILE_LOAD_HARNESS" = "1" ]; then
    echo "Building server WASM with projectile_load_harness..."
    cargo build \
        --manifest-path "$MODULE_PATH/Cargo.toml" \
        --target wasm32-unknown-unknown \
        --release \
        --features projectile_load_harness

    echo "Publishing '$ARENA_DATABASE' (delete-data=$delete_data_mode)..."
    spacetime publish "${publish_args[@]}" --bin-path "$WASM_PATH" "$ARENA_DATABASE"
else
    echo "Building server WASM..."
    spacetime build -p "$MODULE_PATH"

    echo "Publishing '$ARENA_DATABASE' (delete-data=$delete_data_mode)..."
    spacetime publish "${publish_args[@]}" -p "$MODULE_PATH" "$ARENA_DATABASE"
fi

# Re-sync authored catalogs after every publish. A fresh init (delete-data=always) already syncs
# these families, but a data-preserving publish does not, so call the idempotent reducers
# unconditionally.
echo "Re-syncing spell definitions..."
spacetime call -s "$ARENA_SERVER" "$ARENA_DATABASE" publish_spell_definitions

echo "Re-syncing progression catalogs..."
spacetime call -s "$ARENA_SERVER" "$ARENA_DATABASE" publish_progression_catalogs

echo "Re-syncing melee definitions..."
spacetime call -s "$ARENA_SERVER" "$ARENA_DATABASE" publish_melee_definitions

echo "Re-syncing item definitions and affixes..."
spacetime call -s "$ARENA_SERVER" "$ARENA_DATABASE" publish_item_definitions

if [ "$ARENA_ENABLE_LOCAL_DIRECT_MODE" = "1" ]; then
    echo "Enabling temporary local-direct compatibility mode..."
    spacetime call -s "$ARENA_SERVER" "$ARENA_DATABASE" enable_local_direct_mode
fi

echo "Verifying live shared-data contracts..."
python3 "$ROOT_DIR/ops/verify-spacetimedb-contracts.py" \
    --database "$ARENA_DATABASE" \
    --server "$ARENA_SERVER"

if [ "$ARENA_GENERATE_BINDINGS" = "1" ]; then
    # Canonical regen mode (netcode audit R5): bindings are ALWAYS generated
    # from the harness-featured wasm, regardless of publish mode, so both
    # publish modes produce the identical checked-in shape. The harness
    # reducers are unused-but-harmless against a default-features module.
    # This build must run after `spacetime publish -p`, which rewrites
    # $WASM_PATH with default features.
    if [ "$ARENA_PROJECTILE_LOAD_HARNESS" != "1" ]; then
        echo "Building harness-featured WASM for canonical binding generation..."
        cargo build \
            --manifest-path "$MODULE_PATH/Cargo.toml" \
            --target wasm32-unknown-unknown \
            --release \
            --features projectile_load_harness
    fi
    echo "Regenerating Unity bindings from harness-featured WASM (canonical shape)..."
    spacetime generate --yes --lang csharp --bin-path "$WASM_PATH" --out-dir "$GENERATED_OUT"
fi

if [ "$ARENA_VERIFY_DOTNET" = "1" ] && [ -f "$ROOT_DIR/Assembly-CSharp.csproj" ]; then
    echo "Verifying generated Unity C# compile..."
    dotnet build "$ROOT_DIR/Assembly-CSharp.csproj"
fi

echo "Republished '$ARENA_DATABASE' (delete-data=$delete_data_mode, bindings=$ARENA_GENERATE_BINDINGS, harness=$ARENA_PROJECTILE_LOAD_HARNESS, local-direct=$ARENA_ENABLE_LOCAL_DIRECT_MODE)."

#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ARENA_DATABASE="${ARENA_DATABASE:-arena}"
ARENA_DELETE_DATA="${ARENA_DELETE_DATA:-always}"
ARENA_GENERATE_BINDINGS="${ARENA_GENERATE_BINDINGS:-1}"
ARENA_PROJECTILE_LOAD_HARNESS="${ARENA_PROJECTILE_LOAD_HARNESS:-0}"
ARENA_VERIFY_DOTNET="${ARENA_VERIFY_DOTNET:-1}"
ARENA_SERVER="${ARENA_SERVER:-}"

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

publish_args=(--delete-data="$delete_data_mode" --yes)
if [ -n "$ARENA_SERVER" ]; then
    publish_args+=(-s "$ARENA_SERVER")
fi

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required."
    exit 2
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

# Re-sync every table derived from progression_catalog.shared.json. A fresh init
# (delete-data=always) already syncs both families, but a data-preserving publish does not, so call
# both reducers unconditionally (they are idempotent). SpellDefinition is a separate public table
# consumed by client cast/animation/VFX logic; progression catalogs cover cues and ability rows.
echo "Re-syncing spell definitions..."
spacetime call "$ARENA_DATABASE" publish_spell_definitions

echo "Re-syncing progression catalogs..."
spacetime call "$ARENA_DATABASE" publish_progression_catalogs

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

echo "Republished '$ARENA_DATABASE' (delete-data=$delete_data_mode, bindings=$ARENA_GENERATE_BINDINGS, harness=$ARENA_PROJECTILE_LOAD_HARNESS)."

#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ARENA_DATABASE="${ARENA_DATABASE:-arena}"
ARENA_GENERATE_BINDINGS="${ARENA_GENERATE_BINDINGS:-1}"
ARENA_PROJECTILE_LOAD_HARNESS="${ARENA_PROJECTILE_LOAD_HARNESS:-1}"
ARENA_VERIFY_DOTNET="${ARENA_VERIFY_DOTNET:-1}"
ARENA_SERVER="${ARENA_SERVER:-}"

MODULE_PATH="$ROOT_DIR/server"
GENERATED_OUT="$ROOT_DIR/Assets/Arena/Runtime/Generated/SpacetimeDB"
WASM_PATH="$ROOT_DIR/server/target/wasm32-unknown-unknown/release/arena.wasm"

publish_args=(--delete-data=always --yes)
if [ -n "$ARENA_SERVER" ]; then
    publish_args+=(-s "$ARENA_SERVER")
fi

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required."
    exit 2
fi

if [ "$ARENA_PROJECTILE_LOAD_HARNESS" = "1" ]; then
    echo "Building server WASM with projectile_load_harness to match checked-in generated bindings..."
    cargo build \
        --manifest-path "$MODULE_PATH/Cargo.toml" \
        --target wasm32-unknown-unknown \
        --release \
        --features projectile_load_harness

    echo "Publishing '$ARENA_DATABASE' with cleared data..."
    spacetime publish "${publish_args[@]}" --bin-path "$WASM_PATH" "$ARENA_DATABASE"

    if [ "$ARENA_GENERATE_BINDINGS" = "1" ]; then
        echo "Regenerating Unity bindings from published WASM shape..."
        spacetime generate --yes --lang csharp --bin-path "$WASM_PATH" --out-dir "$GENERATED_OUT"
    fi
else
    echo "Building server WASM..."
    spacetime build -p "$MODULE_PATH"

    echo "Publishing '$ARENA_DATABASE' with cleared data..."
    spacetime publish "${publish_args[@]}" -p "$MODULE_PATH" "$ARENA_DATABASE"

    if [ "$ARENA_GENERATE_BINDINGS" = "1" ]; then
        echo "Regenerating Unity bindings from module path..."
        spacetime generate --yes --lang csharp --module-path "$MODULE_PATH" --out-dir "$GENERATED_OUT"
    fi
fi

if [ "$ARENA_VERIFY_DOTNET" = "1" ] && [ -f "$ROOT_DIR/Assembly-CSharp.csproj" ]; then
    echo "Verifying generated Unity C# compile..."
    dotnet build "$ROOT_DIR/Assembly-CSharp.csproj"
fi

echo "Republished '$ARENA_DATABASE' with cleared data."

#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Fast local server-logic publish:
# - keeps current DB rows
# - skips generated C# bindings
# - skips Unity C# verification
# - keeps projectile load harness out unless explicitly requested
ARENA_DELETE_DATA="${ARENA_DELETE_DATA:-never}" \
ARENA_GENERATE_BINDINGS="${ARENA_GENERATE_BINDINGS:-0}" \
ARENA_VERIFY_DOTNET="${ARENA_VERIFY_DOTNET:-0}" \
ARENA_PROJECTILE_LOAD_HARNESS="${ARENA_PROJECTILE_LOAD_HARNESS:-0}" \
exec "$ROOT_DIR/ops/republish-local-clear.sh" "$@"

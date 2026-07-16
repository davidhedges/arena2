#!/usr/bin/env bash
# Fast catalog/cue iteration.
#
# Use this when you've only edited authored catalog data and haven't changed the module schema. It
# rebuilds + publishes the module PRESERVING your data (character, world state), then re-syncs the
# authored catalogs so content edits are live immediately -- no manual reducer calls needed.
#
# A data-preserving republish does NOT re-populate the catalog tables on its own (SpacetimeDB only
# re-syncs on a fresh-DB init), which is why the explicit resync call below exists.
#
# For schema changes / binding regen, use ops/republish-local-clear.sh instead.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARENA_DATABASE="${ARENA_DATABASE:-arena}"
MODULE_PATH="$ROOT_DIR/server"

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required." >&2
    exit 2
fi

echo "Publishing '$ARENA_DATABASE' (preserving data)..."
spacetime publish --delete-data=never --yes -p "$MODULE_PATH" "$ARENA_DATABASE"

echo "Re-syncing spell definitions (gameplay/cast JSON changes go live now)..."
spacetime call "$ARENA_DATABASE" publish_spell_definitions

echo "Re-syncing progression catalogs (cue/ability JSON changes go live now)..."
spacetime call "$ARENA_DATABASE" publish_progression_catalogs

echo "Re-syncing item definitions and affixes..."
spacetime call "$ARENA_DATABASE" publish_item_definitions

echo "Done -- catalog changes are live on '$ARENA_DATABASE', data preserved."

#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SPACETIME_DATA="${SPACETIME_DATA:-${XDG_DATA_HOME:-$HOME/.local/share}/spacetime/data}"
PROVISIONER_STATE_DB="${ARENA_PROVISIONER_STATE_DB:-$ROOT_DIR/Library/ArenaMatchProvisioner/state.sqlite3}"
PROVISIONER_LOCK_PATH="${PROVISIONER_STATE_DB%.*}.lock"
RUNTIME_DIR="${ARENA_LOCAL_MULTIPLAYER_RUNTIME_DIR:-$ROOT_DIR/Library/ArenaLocalMultiplayer}"
DRY_RUN=0

usage() {
    cat <<'EOF'
Usage: ops/cleanup-local-spacetimedb-data.sh [--dry-run]

Delete every database and cache in the local SpacetimeDB data directory, then
remove the local match provisioner's recovery ledger and managed runtime state.

The local SpacetimeDB server and every match provisioner must be stopped. This
script never restarts or republishes them; use ops/setup-local-multiplayer.sh
afterward when a fresh local Hub + disposable-match environment is wanted.

Options:
  --dry-run  Report what would be removed without stopping or deleting anything.
  -h, --help Show this help text.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --dry-run)
            DRY_RUN=1
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
    shift
done

case "$SPACETIME_DATA" in
    ""|/|"$HOME"|"$ROOT_DIR")
        echo "Refusing unsafe SpacetimeDB data path: $SPACETIME_DATA" >&2
        exit 2
        ;;
esac

CONTROL_PLANE_FILES=(
    "$PROVISIONER_STATE_DB"
    "$PROVISIONER_STATE_DB-shm"
    "$PROVISIONER_STATE_DB-wal"
    "$PROVISIONER_LOCK_PATH"
    "$RUNTIME_DIR/provisioner.pid"
    "$RUNTIME_DIR/provisioner.log"
)

echo "Local SpacetimeDB and match-control cleanup"
echo "  mode:     $([ "$DRY_RUN" -eq 1 ] && echo "dry run" || echo "delete")"
echo "  database: $SPACETIME_DATA"
echo "  ledger:   $PROVISIONER_STATE_DB"
echo "  runtime:  $RUNTIME_DIR"
echo

if [ -d "$SPACETIME_DATA" ]; then
    echo "Local database data:"
    du -sh "$SPACETIME_DATA" 2>/dev/null || true
else
    echo "No SpacetimeDB data directory found."
fi

found_control_plane=0
echo "Local match-control state:"
for path in "${CONTROL_PLANE_FILES[@]}"; do
    if [ -e "$path" ]; then
        found_control_plane=1
        size="$(du -sh "$path" 2>/dev/null | awk '{print $1}')"
        if [ "$DRY_RUN" -eq 1 ]; then
            echo "  Would remove: $path ($size physical disk usage)"
        else
            echo "  Remove: $path ($size physical disk usage)"
        fi
    fi
done
if [ "$found_control_plane" -eq 0 ]; then
    echo "  No provisioner ledger or managed runtime files found."
fi
echo

if [ "$DRY_RUN" -eq 1 ]; then
    if [ -d "$SPACETIME_DATA" ]; then
        echo "Would clear all local databases and caches under $SPACETIME_DATA."
    fi
    exit 0
fi

if command -v spacetime >/dev/null 2>&1 && spacetime server ping local >/dev/null 2>&1; then
    echo "The local SpacetimeDB server is still reachable." >&2
    echo "Stop it before running this cleanup so on-disk state is not removed while open." >&2
    exit 2
fi

# Stop the canonical managed provisioner and discard a stale launchd/PID entry.
"$ROOT_DIR/ops/setup-local-multiplayer.sh" stop

# An independently started provisioner is not owned by the setup script. Its
# ledger lock is the authoritative guard against deleting recovery state while
# that worker is still active.
if command -v lsof >/dev/null 2>&1 && [ -e "$PROVISIONER_LOCK_PATH" ] && \
        lsof "$PROVISIONER_LOCK_PATH" >/dev/null 2>&1; then
    echo "A match provisioner still holds $PROVISIONER_LOCK_PATH." >&2
    echo "Stop that worker before running this cleanup." >&2
    exit 2
fi

if [ -d "$SPACETIME_DATA" ]; then
    if ! command -v spacetime >/dev/null 2>&1; then
        echo "spacetime CLI is required to clear the local database directory safely." >&2
        exit 2
    fi
    spacetime server clear --data-dir "$SPACETIME_DATA" --yes
fi

for path in "${CONTROL_PLANE_FILES[@]}"; do
    rm -f -- "$path"
done

echo "After:"
if [ -d "$SPACETIME_DATA" ]; then
    du -sh "$SPACETIME_DATA" 2>/dev/null || true
else
    echo "  SpacetimeDB data directory removed."
fi
df -h "$ROOT_DIR"

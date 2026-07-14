#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET_DIR="$ROOT_DIR/server/target"
DRY_RUN=0

usage() {
    cat <<'EOF'
Usage: ops/cleanup-server-build-artifacts.sh [--dry-run]

Remove generated Rust debug/test artifacts that accumulate during server
development. Release artifacts, including the SpacetimeDB WASM used by the
publish scripts, are preserved.

Options:
  --dry-run  Report what would be removed without deleting anything.
  -h, --help Show this help text.

Do not run this script while a Cargo or SpacetimeDB build/test is active.
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

# Keep the delete set explicit. In particular, never remove either release
# directory because local publish/generate flows consume the release WASM.
CLEAN_DIRS=(
    "$TARGET_DIR/debug"
    "$TARGET_DIR/wasm32-unknown-unknown/debug"
)

echo "Server build artifact cleanup"
echo "  target: $TARGET_DIR"
echo "  mode:   $([ "$DRY_RUN" -eq 1 ] && echo "dry run" || echo "delete")"
echo

found=0
for path in "${CLEAN_DIRS[@]}"; do
    if [ ! -e "$path" ]; then
        echo "Skipping missing path: $path"
        continue
    fi

    found=1
    size="$(du -sh "$path" 2>/dev/null | awk '{print $1}')"
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "Would remove: $path ($size physical disk usage)"
    else
        echo "Removing: $path ($size physical disk usage)"
        rm -rf -- "$path"
    fi
done

if [ "$found" -eq 0 ]; then
    echo "No debug/test build artifacts found."
fi

echo
echo "Preserved release artifacts:"
for path in \
    "$TARGET_DIR/release" \
    "$TARGET_DIR/wasm32-unknown-unknown/release"; do
    if [ -e "$path" ]; then
        size="$(du -sh "$path" 2>/dev/null | awk '{print $1}')"
        echo "  $path ($size physical disk usage)"
    fi
done

if [ "$DRY_RUN" -eq 0 ]; then
    echo
    echo "After cleanup:"
    du -sh "$TARGET_DIR" 2>/dev/null || true
    df -h "$ROOT_DIR"
fi

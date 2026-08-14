#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DRY_RUN=0

usage() {
    cat <<'EOF'
Usage: ops/cleanup-server-build-artifacts.sh [--dry-run]

Remove generated Rust debug/test artifacts that accumulate across the
gameplay, Hub, and disposable-match modules. Release artifacts, including the
SpacetimeDB WASM files used by the publish scripts, are preserved.

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

# Keep both the module and delete sets explicit. In particular, never remove
# any release directory because local publish/generate flows consume the
# release WASM files.
TARGET_DIRS=(
    "$ROOT_DIR/server/target"
    "$ROOT_DIR/hub-server/target"
    "$ROOT_DIR/match-server/target"
)

echo "Server build artifact cleanup"
echo "  mode:   $([ "$DRY_RUN" -eq 1 ] && echo "dry run" || echo "delete")"
echo

found=0
for target_dir in "${TARGET_DIRS[@]}"; do
    echo "Target: $target_dir"
    for path in \
        "$target_dir/debug" \
        "$target_dir/wasm32-unknown-unknown/debug"; do
        if [ ! -e "$path" ]; then
            echo "  Skipping missing path: $path"
            continue
        fi

        found=1
        size="$(du -sh "$path" 2>/dev/null | awk '{print $1}')"
        if [ "$DRY_RUN" -eq 1 ]; then
            echo "  Would remove: $path ($size physical disk usage)"
        else
            echo "  Removing: $path ($size physical disk usage)"
            rm -rf -- "$path"
        fi
    done
    echo
done

if [ "$found" -eq 0 ]; then
    echo "No debug/test build artifacts found."
fi

echo
echo "Preserved release artifacts:"
for target_dir in "${TARGET_DIRS[@]}"; do
    for path in \
        "$target_dir/release" \
        "$target_dir/wasm32-unknown-unknown/release"; do
        if [ -e "$path" ]; then
            size="$(du -sh "$path" 2>/dev/null | awk '{print $1}')"
            echo "  $path ($size physical disk usage)"
        fi
    done
done

if [ "$DRY_RUN" -eq 0 ]; then
    echo
    echo "After cleanup:"
    for target_dir in "${TARGET_DIRS[@]}"; do
        if [ -e "$target_dir" ]; then
            du -sh "$target_dir" 2>/dev/null || true
        fi
    done
    df -h "$ROOT_DIR"
fi

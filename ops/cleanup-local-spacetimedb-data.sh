#!/usr/bin/env bash
set -euo pipefail

SPACETIME_DATA="${SPACETIME_DATA:-$HOME/.local/share/spacetime/data}"

echo "Cleaning local SpacetimeDB bulk data under:"
echo "  $SPACETIME_DATA"
echo

if [ ! -d "$SPACETIME_DATA" ]; then
    echo "No SpacetimeDB data directory found."
    exit 0
fi

echo "Before:"
du -sh "$SPACETIME_DATA" 2>/dev/null || true
df -h "$HOME"
echo

rm -rf \
    "$SPACETIME_DATA/replicas" \
    "$SPACETIME_DATA/program-bytes" \
    "$SPACETIME_DATA/cache"

echo "After:"
du -sh "$SPACETIME_DATA" 2>/dev/null || true
df -h "$HOME"

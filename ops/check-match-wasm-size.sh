#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 1 ]; then
    echo "Usage: $0 <optimized-match-wasm>" >&2
    exit 2
fi

WASM_PATH="$1"
MAX_BYTES="${MATCH_WASM_MAX_BYTES:-3500000}"

if [ ! -f "$WASM_PATH" ]; then
    echo "PvP match WASM not found at $WASM_PATH" >&2
    exit 2
fi

case "$MAX_BYTES" in
    ''|*[!0-9]*)
        echo "MATCH_WASM_MAX_BYTES must be a positive integer." >&2
        exit 2
        ;;
esac
if [ "$MAX_BYTES" -eq 0 ]; then
    echo "MATCH_WASM_MAX_BYTES must be a positive integer." >&2
    exit 2
fi

ACTUAL_BYTES="$(wc -c < "$WASM_PATH" | tr -d '[:space:]')"
if [ "$ACTUAL_BYTES" -gt "$MAX_BYTES" ]; then
    echo "PvP match WASM is too large: $ACTUAL_BYTES bytes exceeds the $MAX_BYTES-byte ceiling." >&2
    echo "Check for unrelated assets/code or a missing canonical wasm-opt pass." >&2
    exit 1
fi

echo "PvP match WASM size guard passed: $ACTUAL_BYTES / $MAX_BYTES bytes."

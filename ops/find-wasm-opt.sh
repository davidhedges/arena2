#!/usr/bin/env bash
# Prints the path to the Binaryen wasm-opt this workspace should use.
# Honors $WASM_OPT, then PATH, then the wasm-opt Unity ships with its WebGL
# build tools so a machine with the editor installed needs no extra install.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ -n "${WASM_OPT:-}" ]; then
    if command -v "$WASM_OPT" >/dev/null 2>&1; then
        command -v "$WASM_OPT"
        exit 0
    fi
    echo "WASM_OPT does not identify an executable: $WASM_OPT" >&2
    exit 1
fi

if command -v wasm-opt >/dev/null 2>&1; then
    command -v wasm-opt
    exit 0
fi

unity_version="$(awk '/^m_EditorVersion:/ { print $2; exit }' "$ROOT_DIR/ProjectSettings/ProjectVersion.txt" 2>/dev/null || true)"
unity_wasm_opt="/Applications/Unity/Hub/Editor/$unity_version/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/binaryen/bin/wasm-opt"
if [ -n "$unity_version" ] && [ -x "$unity_wasm_opt" ]; then
    printf '%s\n' "$unity_wasm_opt"
    exit 0
fi

echo "wasm-opt is required. Install Binaryen or set WASM_OPT to its executable path." >&2
exit 1

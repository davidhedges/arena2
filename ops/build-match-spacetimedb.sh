#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MATCH_MODULE_PATH="${MATCH_MODULE_PATH:-$ROOT_DIR/match-server}"
MATCH_RAW_WASM_PATH="${MATCH_RAW_WASM_PATH:-$MATCH_MODULE_PATH/target/wasm32-unknown-unknown/release/arena_match.wasm}"
MATCH_WASM_PATH="${MATCH_WASM_PATH:-$MATCH_MODULE_PATH/target/wasm32-unknown-unknown/release/arena_match.opt.wasm}"
MATCH_DEPFILE_PATH="${MATCH_DEPFILE_PATH:-$MATCH_MODULE_PATH/target/wasm32-unknown-unknown/release/arena_match.d}"
MATCH_PROVENANCE_PATH="${MATCH_PROVENANCE_PATH:-$MATCH_WASM_PATH.inputs.json}"
MATCH_GENERATED_OUT="${MATCH_GENERATED_OUT:-$ROOT_DIR/Assets/Arena/Runtime/Generated/MatchSpacetimeDB}"
MATCH_GENERATE_BINDINGS="${MATCH_GENERATE_BINDINGS:-1}"

if ! command -v spacetime >/dev/null 2>&1; then
    echo "spacetime CLI is required." >&2
    exit 2
fi
if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is required." >&2
    exit 2
fi

find_wasm_opt() {
    if [ -n "${WASM_OPT:-}" ]; then
        if command -v "$WASM_OPT" >/dev/null 2>&1; then
            command -v "$WASM_OPT"
            return
        fi
        echo "WASM_OPT does not identify an executable: $WASM_OPT" >&2
        return 1
    fi

    if command -v wasm-opt >/dev/null 2>&1; then
        command -v wasm-opt
        return
    fi

    unity_version="$(awk '/^m_EditorVersion:/ { print $2; exit }' "$ROOT_DIR/ProjectSettings/ProjectVersion.txt" 2>/dev/null || true)"
    unity_wasm_opt="/Applications/Unity/Hub/Editor/$unity_version/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/binaryen/bin/wasm-opt"
    if [ -n "$unity_version" ] && [ -x "$unity_wasm_opt" ]; then
        printf '%s\n' "$unity_wasm_opt"
        return
    fi

    echo "wasm-opt is required. Install Binaryen or set WASM_OPT to its executable path." >&2
    return 1
}

WASM_OPT_BIN="$(find_wasm_opt)"
export PATH="$(dirname "$WASM_OPT_BIN"):$PATH"

echo "Building disposable PvP match module..."
spacetime build -p "$MATCH_MODULE_PATH"

if [ ! -f "$MATCH_RAW_WASM_PATH" ]; then
    echo "Raw PvP match WASM not found at $MATCH_RAW_WASM_PATH" >&2
    exit 1
fi

echo "Applying the canonical size-oriented Binaryen pass..."
"$WASM_OPT_BIN" \
    -Oz \
    --strip-debug \
    --strip-producers \
    "$MATCH_RAW_WASM_PATH" \
    -o "$MATCH_WASM_PATH"

"$ROOT_DIR/ops/check-match-wasm-size.sh" "$MATCH_WASM_PATH"

echo "Recording the source inputs for the cached match artifact..."
python3 "$ROOT_DIR/match_provisioner/artifact_provenance.py" write \
    --workspace-root "$ROOT_DIR" \
    --depfile "$MATCH_DEPFILE_PATH" \
    --wasm "$MATCH_WASM_PATH" \
    --manifest "$MATCH_PROVENANCE_PATH"

if [ "$MATCH_GENERATE_BINDINGS" = "1" ]; then
    echo "Generating PvP match Unity bindings in namespace Arena.MatchDb..."
    spacetime generate \
        --yes \
        --lang csharp \
        --namespace Arena.MatchDb \
        --bin-path "$MATCH_WASM_PATH" \
        --out-dir "$MATCH_GENERATED_OUT"
fi

echo "Built optimized disposable PvP match module at $MATCH_WASM_PATH (bindings=$MATCH_GENERATE_BINDINGS)."

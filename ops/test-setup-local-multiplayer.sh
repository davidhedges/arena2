#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=setup-local-multiplayer.sh
source "$ROOT_DIR/ops/setup-local-multiplayer.sh"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

assert_equal() {
    local expected="$1"
    local actual="$2"
    local message="$3"
    if [ "$expected" != "$actual" ]; then
        fail "$message (expected '$expected', got '$actual')"
    fi
}

assert_contains_line() {
    local expected="$1"
    local content="$2"
    local message="$3"
    if ! printf '%s\n' "$content" | grep -Fqx -- "$expected"; then
        fail "$message (missing '$expected')"
    fi
}

uses_launchd() {
    return 0
}

launchctl() {
    cat <<'EOF'
{
    "Label" = "com.arena.local-match-provisioner.test";
    "PID" = 4242;
};
EOF
}

assert_equal "4242" "$(launchd_provisioner_pid)" \
    "launchd dictionary output should expose the provisioner PID"

launchctl() {
    printf '4243\t0\tcom.arena.local-match-provisioner.test\n'
}

assert_equal "4243" "$(launchd_provisioner_pid)" \
    "legacy launchctl output should expose the provisioner PID"

capture_path="$(mktemp)"
trap 'rm -f "$capture_path"' EXIT
launchctl() {
    printf '%s\n' "$@" > "$capture_path"
}

MATCH_WASM_PATH="/tmp/Arena Match/arena_match.opt.wasm"
PROVISIONER_STATE_DB="/tmp/Arena State/state.sqlite3"
PROVISIONER_LOG_PATH="/tmp/Arena Logs/provisioner.log"
ARENA_PROVISIONER_MANAGEMENT_URL="http://127.0.0.1:3100"
ARENA_PROVISIONER_CLIENT_URI="ws://127.0.0.1:3100"
ARENA_PROVISIONER_HUB_DATABASE="arena-hub-test"
ARENA_PROVISIONER_MAP_ID="ARENA_MAP_TEST"
ARENA_PROVISIONER_TOKEN="must-not-appear-in-process-arguments"
start_launchd_provisioner
captured="$(<"$capture_path")"

assert_contains_line "submit" "$captured" "launchd startup should submit a service"
assert_contains_line "$PROVISIONER_LAUNCHD_LABEL" "$captured" \
    "launchd startup should use the repository-scoped label"
assert_contains_line "$PROVISIONER_LOG_PATH" "$captured" \
    "launchd startup should preserve the configured log path"
assert_contains_line "ARENA_PROVISIONER_MATCH_WASM=$MATCH_WASM_PATH" "$captured" \
    "launchd startup should preserve the configured match artifact"
assert_contains_line "ARENA_PROVISIONER_STATE_DB=$PROVISIONER_STATE_DB" "$captured" \
    "launchd startup should preserve the configured ledger"
assert_contains_line "ARENA_PROVISIONER_HUB_DATABASE=$ARENA_PROVISIONER_HUB_DATABASE" "$captured" \
    "launchd startup should preserve the configured Hub database"
assert_contains_line "$ROOT_DIR/ops/run-local-match-provisioner.sh" "$captured" \
    "launchd startup should execute the canonical provisioner runner"
if printf '%s\n' "$captured" | grep -Fq -- "$ARENA_PROVISIONER_TOKEN"; then
    fail "launchd process arguments must not contain the provisioner token"
fi

echo "setup-local-multiplayer tests passed"

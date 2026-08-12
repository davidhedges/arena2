#!/usr/bin/env bash
# Canonical local 2v2 matchmaking environment entry point.
# LLMs/agents: use this script instead of reproducing the Hub, match-build,
# and provisioner startup commands separately.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_DIR="${ARENA_LOCAL_MULTIPLAYER_RUNTIME_DIR:-$ROOT_DIR/Library/ArenaLocalMultiplayer}"
PROVISIONER_PID_PATH="$RUNTIME_DIR/provisioner.pid"
PROVISIONER_LOG_PATH="$RUNTIME_DIR/provisioner.log"
MATCH_WASM_PATH="${ARENA_PROVISIONER_MATCH_WASM:-$ROOT_DIR/match-server/target/wasm32-unknown-unknown/release/arena_match.opt.wasm}"
PROVISIONER_STATE_DB="${ARENA_PROVISIONER_STATE_DB:-$ROOT_DIR/Library/ArenaMatchProvisioner/state.sqlite3}"
PROVISIONER_LOCK_PATH="${PROVISIONER_STATE_DB%.*}.lock"

usage() {
    cat <<'EOF'
Usage: ops/setup-local-multiplayer.sh [setup|status|stop|--help]

  setup   Safely publish the local Hub, rebuild the cached match module, and
          start one background match provisioner. This is the default.
  status  Report whether the local server, match artifact, and managed
          provisioner are ready without changing anything.
  stop    Stop only the provisioner started by this script. The shared local
          SpacetimeDB server is intentionally left running.

The setup command is local-only and preserves Hub data by default. To
intentionally reset only the local Hub when a schema migration cannot be
preserved, run:

  HUB_DELETE_DATA=always ops/setup-local-multiplayer.sh setup

Runtime PID/log files live under ignored Library/ArenaLocalMultiplayer/.
EOF
}

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "$1 is required." >&2
        exit 2
    fi
}

managed_provisioner_pid() {
    if [ ! -f "$PROVISIONER_PID_PATH" ]; then
        return 1
    fi

    local pid
    IFS= read -r pid < "$PROVISIONER_PID_PATH" || return 1
    if [[ ! "$pid" =~ ^[0-9]+$ ]] || [ "$pid" -le 1 ]; then
        return 1
    fi
    printf '%s\n' "$pid"
}

managed_provisioner_is_running() {
    local pid
    pid="$(managed_provisioner_pid)" || return 1
    if ! kill -0 "$pid" 2>/dev/null; then
        return 1
    fi

    # The worker keeps this exact ledger lock open for its whole lifetime.
    # Checking it avoids trusting a stale PID file whose number was reused.
    if command -v lsof >/dev/null 2>&1; then
        lsof -a -p "$pid" "$PROVISIONER_LOCK_PATH" >/dev/null 2>&1
        return
    fi

    local command_line
    command_line="$(ps -p "$pid" -o command= 2>/dev/null || true)"
    [[ "$command_line" == *"match_provisioner.worker"* ]]
}

remove_stale_pid_file() {
    if [ -f "$PROVISIONER_PID_PATH" ] && ! managed_provisioner_is_running; then
        rm -f "$PROVISIONER_PID_PATH"
    fi
}

stop_managed_provisioner() {
    remove_stale_pid_file
    if ! managed_provisioner_is_running; then
        echo "Local match provisioner is not running under this script."
        return 0
    fi

    local pid
    pid="$(managed_provisioner_pid)"
    echo "Stopping local match provisioner (pid $pid)..."
    kill -TERM "$pid"
    for _ in {1..50}; do
        if ! kill -0 "$pid" 2>/dev/null; then
            rm -f "$PROVISIONER_PID_PATH"
            echo "Local match provisioner stopped."
            return 0
        fi
        sleep 0.2
    done

    echo "Provisioner pid $pid did not stop within 10 seconds." >&2
    echo "It was not force-killed; inspect $PROVISIONER_LOG_PATH" >&2
    return 1
}

start_managed_provisioner() {
    mkdir -p "$RUNTIME_DIR"
    remove_stale_pid_file
    if managed_provisioner_is_running; then
        echo "Local match provisioner is already running (pid $(managed_provisioner_pid))."
        return 0
    fi

    printf '\n[%s] Starting local match provisioner\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
        >> "$PROVISIONER_LOG_PATH"
    nohup "$ROOT_DIR/ops/run-local-match-provisioner.sh" run \
        >> "$PROVISIONER_LOG_PATH" 2>&1 </dev/null &
    local pid=$!
    printf '%s\n' "$pid" > "$PROVISIONER_PID_PATH"

    # Give the runner time to validate its token/server and exec Python. A
    # second provisioner holding the ledger lock will fail during this window.
    for _ in {1..40}; do
        if managed_provisioner_is_running; then
            break
        fi
        if ! kill -0 "$pid" 2>/dev/null; then
            break
        fi
        sleep 0.25
    done

    if managed_provisioner_is_running; then
        echo "Local match provisioner is running in the background (pid $pid)."
        echo "Provisioner log: $PROVISIONER_LOG_PATH"
        return 0
    fi

    rm -f "$PROVISIONER_PID_PATH"
    echo "The local match provisioner did not remain running." >&2
    echo "Another manually started provisioner may already own its lock." >&2
    echo "Recent log output:" >&2
    tail -n 30 "$PROVISIONER_LOG_PATH" >&2 || true
    return 1
}

show_status() {
    local ready=0

    if command -v spacetime >/dev/null 2>&1 && spacetime server ping local >/dev/null 2>&1; then
        echo "SpacetimeDB: ready (local)"
    else
        echo "SpacetimeDB: not reachable"
        ready=1
    fi

    if [ -f "$MATCH_WASM_PATH" ]; then
        echo "Match artifact: ready ($MATCH_WASM_PATH)"
    else
        echo "Match artifact: missing ($MATCH_WASM_PATH)"
        ready=1
    fi

    if managed_provisioner_is_running; then
        echo "Provisioner: running (pid $(managed_provisioner_pid))"
        echo "Provisioner log: $PROVISIONER_LOG_PATH"
    else
        echo "Provisioner: not running under this script"
        ready=1
    fi

    return "$ready"
}

setup_environment() {
    require_command spacetime
    require_command python3
    require_command cargo

    mkdir -p "$RUNTIME_DIR"

    # A managed worker must not claim work while its Hub schema and cached
    # match artifact are being replaced. Unmanaged workers fail safely on the
    # provisioner's exclusive ledger lock when start_managed_provisioner runs.
    stop_managed_provisioner

    echo "Publishing the persistent local matchmaking Hub (data-preserving)..."
    HUB_DELETE_DATA="${HUB_DELETE_DATA:-never}" \
        "$ROOT_DIR/ops/republish-local-hub.sh"

    echo "Building the cached disposable-match module and Unity bindings..."
    MATCH_GENERATE_BINDINGS="${MATCH_GENERATE_BINDINGS:-1}" \
        "$ROOT_DIR/ops/build-match-spacetimedb.sh"

    start_managed_provisioner

    echo
    echo "Local multiplayer is ready. Open Unity and request an Unranked 2v2 Bot Match."
    echo "Status: ops/setup-local-multiplayer.sh status"
    echo "Stop:   ops/setup-local-multiplayer.sh stop"
}

command="${1:-setup}"
case "$command" in
    setup)
        if [ "$#" -gt 1 ]; then
            usage >&2
            exit 2
        fi
        setup_environment
        ;;
    status)
        if [ "$#" -gt 1 ]; then
            usage >&2
            exit 2
        fi
        show_status
        ;;
    stop)
        if [ "$#" -gt 1 ]; then
            usage >&2
            exit 2
        fi
        stop_managed_provisioner
        ;;
    -h|--help|help)
        usage
        ;;
    *)
        echo "Unknown command: $command" >&2
        usage >&2
        exit 2
        ;;
esac

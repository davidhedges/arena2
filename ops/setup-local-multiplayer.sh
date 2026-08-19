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
MATCH_PROVENANCE_PATH="${ARENA_PROVISIONER_MATCH_MANIFEST:-$MATCH_WASM_PATH.inputs.json}"
OPENWORLD_WASM_PATH="${ARENA_PROVISIONER_OPENWORLD_WASM:-$ROOT_DIR/server/target/wasm32-unknown-unknown/release/arena.opt.wasm}"
OPENWORLD_PROVENANCE_PATH="${ARENA_PROVISIONER_OPENWORLD_MANIFEST:-$OPENWORLD_WASM_PATH.inputs.json}"
PROVISIONER_STATE_DB="${ARENA_PROVISIONER_STATE_DB:-$ROOT_DIR/Library/ArenaMatchProvisioner/state.sqlite3}"
# SpacetimeDB leaves replicas/<id>/ on disk when a database is deleted, so a
# disposed instance keeps its whole commitlog and snapshot. The provisioner
# reclaims that space after each disposal when pointed at the local data dir.
SPACETIME_DATA="${SPACETIME_DATA:-${XDG_DATA_HOME:-$HOME/.local/share}/spacetime/data}"
PROVISIONER_LOCK_PATH="${PROVISIONER_STATE_DB%.*}.lock"
ROOT_DIR_CHECKSUM="$(printf '%s' "$ROOT_DIR" | cksum)"
PROVISIONER_LAUNCHD_LABEL="com.arena.local-match-provisioner.${ROOT_DIR_CHECKSUM%% *}"

usage() {
    cat <<'EOF'
Usage: ops/setup-local-multiplayer.sh [setup|status|stop|gc|--help]

  setup   Safely publish the local Hub, rebuild the cached match module, and
          start one background match provisioner. This is the default.
  status  Report whether the local server, match artifact, and managed
          provisioner are ready without changing anything.
  stop    Stop only the provisioner started by this script. The shared local
          SpacetimeDB server is intentionally left running.
  gc      Delete replica directories whose database no longer exists. Deleting
          a database does not remove its on-disk replica, so disposed matches
          and open worlds keep their commitlog and snapshot until this runs.

The setup command is local-only and preserves Hub data by default. To
intentionally reset only the local Hub when a schema migration cannot be
preserved, run:

  HUB_DELETE_DATA=always ops/setup-local-multiplayer.sh setup

Runtime PID/log files live under ignored Library/ArenaLocalMultiplayer/.
On macOS, the provisioner is owned by launchd so it survives the shell or
Codex command which performed setup.
EOF
}

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "$1 is required." >&2
        exit 2
    fi
}

uses_launchd() {
    [ "$(uname -s)" = "Darwin" ] && command -v launchctl >/dev/null 2>&1
}

launchd_provisioner_is_loaded() {
    uses_launchd && launchctl list "$PROVISIONER_LAUNCHD_LABEL" >/dev/null 2>&1
}

launchd_provisioner_pid() {
    local job pid
    job="$(launchctl list "$PROVISIONER_LAUNCHD_LABEL" 2>/dev/null)" || return 1
    pid="$(printf '%s\n' "$job" | awk '
        $1 == "\"PID\"" && $2 == "=" {
            gsub(/;/, "", $3)
            print $3
            exit
        }
        NR == 1 && $1 ~ /^[0-9]+$/ {
            print $1
            exit
        }
    ')"
    if [[ ! "$pid" =~ ^[0-9]+$ ]] || [ "$pid" -le 1 ]; then
        return 1
    fi
    printf '%s\n' "$pid"
}

pid_file_provisioner_pid() {
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

managed_provisioner_pid() {
    local pid
    if uses_launchd && pid="$(launchd_provisioner_pid)"; then
        printf '%s\n' "$pid"
        return 0
    fi
    pid_file_provisioner_pid
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

remove_stale_provisioner_state() {
    if launchd_provisioner_is_loaded && ! managed_provisioner_is_running; then
        launchctl remove "$PROVISIONER_LAUNCHD_LABEL" >/dev/null 2>&1 || true
    fi
    if [ -f "$PROVISIONER_PID_PATH" ] && ! managed_provisioner_is_running; then
        rm -f "$PROVISIONER_PID_PATH"
    fi
}

stop_managed_provisioner() {
    remove_stale_provisioner_state
    if ! managed_provisioner_is_running; then
        echo "Local match provisioner is not running under this script."
        return 0
    fi

    local pid
    pid="$(managed_provisioner_pid)"
    echo "Stopping local match provisioner (pid $pid)..."
    if launchd_provisioner_is_loaded; then
        launchctl remove "$PROVISIONER_LAUNCHD_LABEL"
    else
        kill -TERM "$pid"
    fi
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

start_launchd_provisioner() {
    local -a environment_args
    environment_args=(
        "PATH=${PATH:-/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin}"
        "ARENA_PROVISIONER_MATCH_WASM=$MATCH_WASM_PATH"
        "ARENA_PROVISIONER_MATCH_MANIFEST=$MATCH_PROVENANCE_PATH"
        "ARENA_PROVISIONER_OPENWORLD_WASM=$OPENWORLD_WASM_PATH"
        "ARENA_PROVISIONER_OPENWORLD_MANIFEST=$OPENWORLD_PROVENANCE_PATH"
        "ARENA_PROVISIONER_STATE_DB=$PROVISIONER_STATE_DB"
        "ARENA_PROVISIONER_REPLICA_GC_DATA_DIR=$SPACETIME_DATA"
    )
    if [ -n "${ARENA_PROVISIONER_MANAGEMENT_URL:-}" ]; then
        environment_args+=("ARENA_PROVISIONER_MANAGEMENT_URL=$ARENA_PROVISIONER_MANAGEMENT_URL")
    fi
    if [ -n "${ARENA_PROVISIONER_CLIENT_URI:-}" ]; then
        environment_args+=("ARENA_PROVISIONER_CLIENT_URI=$ARENA_PROVISIONER_CLIENT_URI")
    fi
    if [ -n "${ARENA_PROVISIONER_HUB_DATABASE:-}" ]; then
        environment_args+=("ARENA_PROVISIONER_HUB_DATABASE=$ARENA_PROVISIONER_HUB_DATABASE")
    fi
    if [ -n "${ARENA_PROVISIONER_MAP_ID:-}" ]; then
        environment_args+=("ARENA_PROVISIONER_MAP_ID=$ARENA_PROVISIONER_MAP_ID")
    fi

    launchctl submit \
        -l "$PROVISIONER_LAUNCHD_LABEL" \
        -o "$PROVISIONER_LOG_PATH" \
        -e "$PROVISIONER_LOG_PATH" \
        -- /usr/bin/env "${environment_args[@]}" \
        "$ROOT_DIR/ops/run-local-match-provisioner.sh" run
}

start_managed_provisioner() {
    mkdir -p "$RUNTIME_DIR"
    remove_stale_provisioner_state
    if managed_provisioner_is_running; then
        echo "Local match provisioner is already running (pid $(managed_provisioner_pid))."
        return 0
    fi

    printf '\n[%s] Starting local match provisioner\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
        >> "$PROVISIONER_LOG_PATH"
    local pid=""
    if uses_launchd; then
        start_launchd_provisioner
    else
        ARENA_PROVISIONER_REPLICA_GC_DATA_DIR="$SPACETIME_DATA" \
        nohup "$ROOT_DIR/ops/run-local-match-provisioner.sh" run \
            >> "$PROVISIONER_LOG_PATH" 2>&1 </dev/null &
        pid=$!
        printf '%s\n' "$pid" > "$PROVISIONER_PID_PATH"
    fi

    # Give the runner time to validate its token/server and exec Python. A
    # second provisioner holding the ledger lock will fail during this window.
    for _ in {1..40}; do
        if uses_launchd && pid="$(launchd_provisioner_pid 2>/dev/null)"; then
            printf '%s\n' "$pid" > "$PROVISIONER_PID_PATH"
        fi
        if managed_provisioner_is_running; then
            break
        fi
        if ! uses_launchd && ! kill -0 "$pid" 2>/dev/null; then
            break
        fi
        if uses_launchd && ! launchd_provisioner_is_loaded; then
            break
        fi
        sleep 0.25
    done

    if managed_provisioner_is_running; then
        pid="$(managed_provisioner_pid)"
        printf '%s\n' "$pid" > "$PROVISIONER_PID_PATH"
        echo "Local match provisioner is running in the background (pid $pid)."
        if uses_launchd; then
            echo "Provisioner service: $PROVISIONER_LAUNCHD_LABEL"
        fi
        echo "Provisioner log: $PROVISIONER_LOG_PATH"
        return 0
    fi

    if launchd_provisioner_is_loaded; then
        launchctl remove "$PROVISIONER_LAUNCHD_LABEL" >/dev/null 2>&1 || true
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

    local provenance_output
    if provenance_output="$(python3 "$ROOT_DIR/match_provisioner/artifact_provenance.py" verify \
        --wasm "$MATCH_WASM_PATH" \
        --manifest "$MATCH_PROVENANCE_PATH" 2>&1)"; then
        echo "Match artifact: ready ($MATCH_WASM_PATH)"
    else
        echo "Match artifact: stale or missing ($provenance_output)"
        ready=1
    fi

    if provenance_output="$(python3 "$ROOT_DIR/match_provisioner/artifact_provenance.py" verify \
        --wasm "$OPENWORLD_WASM_PATH" \
        --manifest "$OPENWORLD_PROVENANCE_PATH" 2>&1)"; then
        echo "Open-world artifact: ready ($OPENWORLD_WASM_PATH)"
    else
        echo "Open-world artifact: stale or missing ($provenance_output)"
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

    echo "Building the cached disposable-open-world module..."
    "$ROOT_DIR/ops/build-openworld-spacetimedb.sh"

    echo "Reclaiming replica directories left by deleted databases..."
    python3 "$ROOT_DIR/ops/gc-orphaned-replicas.py" --apply || \
        echo "Replica GC skipped; run ops/setup-local-multiplayer.sh gc later." >&2

    start_managed_provisioner

    echo
    echo "Local multiplayer is ready. Open Unity and request an Unranked 2v2 Bot Match,"
    echo "or travel to an open-world destination from Play > Practice."
    echo "Status: ops/setup-local-multiplayer.sh status"
    echo "Stop:   ops/setup-local-multiplayer.sh stop"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
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
    gc)
        shift
        python3 "$ROOT_DIR/ops/gc-orphaned-replicas.py" --apply "$@"
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
fi

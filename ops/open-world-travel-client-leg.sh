#!/usr/bin/env bash
# Client leg for disposable open-world travel, with no human at the keyboard.
#
# Presses the Hub's real Travel_<Scene> button in a batchmode editor and fails
# unless the destination scene becomes active. Pair it with
# ops/open-world-travel-probe.py, which proves the provisioner leg (a fresh
# database appears, and is deleted on exit).
#
# Requires a closed Unity editor: batchmode needs the project lock.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESTINATION="${1:-Giant_Skeleton}"
TRAVEL_SECONDS="${ARENA_OPENWORLD_TRAVEL_SECONDS:-300}"
LOG_PATH="${ARENA_OPENWORLD_TRAVEL_LOG:-${TMPDIR:-/tmp}/arena-openworld-travel.log}"

unity_version="$(awk '/^m_EditorVersion:/ { print $2; exit }' "$ROOT_DIR/ProjectSettings/ProjectVersion.txt")"
UNITY_BIN="${UNITY_BIN:-/Applications/Unity/Hub/Editor/$unity_version/Unity.app/Contents/MacOS/Unity}"
if [ ! -x "$UNITY_BIN" ]; then
    echo "Unity $unity_version not found at $UNITY_BIN; set UNITY_BIN." >&2
    exit 2
fi
if [ -f "$ROOT_DIR/Temp/UnityLockfile" ]; then
    echo "The Unity editor is open and holds the project lock; close it first." >&2
    exit 2
fi

echo "Driving Hub travel to $DESTINATION (log: $LOG_PATH)..."
set +e
ARENA_OPENWORLD_TRAVEL_SCENE="$DESTINATION" \
ARENA_OPENWORLD_TRAVEL_SECONDS="$TRAVEL_SECONDS" \
    "$UNITY_BIN" \
        -batchmode \
        -projectPath "$ROOT_DIR" \
        -executeMethod Arena.EditorTools.OpenWorldTravelHeadlessRunner.Run \
        -logFile "$LOG_PATH"
status=$?
set -e

grep -E "\[OpenWorldTravelHeadlessRunner\]|\[HubController\] Travel" "$LOG_PATH" || true
if [ "$status" -ne 0 ]; then
    echo "Client leg FAILED (exit $status). Full log: $LOG_PATH" >&2
    exit "$status"
fi
echo "Client leg passed: $DESTINATION loaded from the Hub."

# Quitting the editor disconnects the instance, which is what ends it. The
# provisioner deletes the database on its next reconciliation sweep, so the
# disposal half of the change is only proven by waiting for that.
LEDGER="${ARENA_PROVISIONER_STATE_DB:-$ROOT_DIR/Library/ArenaMatchProvisioner/state.sqlite3}"
DISPOSAL_TIMEOUT="${ARENA_OPENWORLD_DISPOSAL_TIMEOUT:-120}"
echo "Waiting up to ${DISPOSAL_TIMEOUT}s for the instance database to be deleted..."
deadline=$(( $(date +%s) + DISPOSAL_TIMEOUT ))
while :; do
    row="$(sqlite3 "$LEDGER" \
        "SELECT state, terminal_phase FROM allocations ORDER BY created_at DESC, rowid DESC LIMIT 1" \
        2>/dev/null || true)"
    case "$row" in
        CLEANED*)
            echo "Instance disposed: $row"
            exit 0
            ;;
    esac
    if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "Instance was NOT disposed within ${DISPOSAL_TIMEOUT}s (ledger row: ${row:-none})." >&2
        exit 1
    fi
    sleep 5
done

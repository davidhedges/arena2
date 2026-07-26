#!/usr/bin/env bash
# Rebuild the checked-in RandomDungeon WITH traps, then audit the result.
#
# One command, because it is one decision: build the Arena trap prefabs, rebuild
# the dungeon at its checked-in seed (which runs the trap placement pass and
# exports the paired trap manifest), validate the foundation, and report the
# resulting kind mix and density.
#
# Needs:  Unity CLOSED. The shared batch preflight refuses a live editor,
#         clears orphaned batch state, and moves an unowned lockfile.
# Usage:  ops/rebuild-dungeon-traps.sh
# Exit:   0 = rebuilt, validated and audited clean; non-zero = see the log.

set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
ROOT="$(pwd)"
UNITY="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity}"
LOG="$ROOT/Logs/dungeon-trap-rebuild.log"
PREFLIGHT="$ROOT/ops/unity-batch-preflight.sh"

if [ ! -x "$UNITY" ]; then
  echo "REFUSING: no Unity at $UNITY (set UNITY_PATH)."
  exit 2
fi
if [ ! -x "$PREFLIGHT" ]; then
  echo "REFUSING: Unity batch preflight is not executable at $PREFLIGHT."
  exit 2
fi
"$PREFLIGHT" "$ROOT" || exit 2

mkdir -p "$ROOT/Logs"
echo "== rebuilding trap prefabs + RandomDungeon (checked-in seed)"
"$UNITY" -batchmode -quit -projectPath "$ROOT" \
  -executeMethod Arena.Editor.WorldInteractionFoundationBuilder.RebuildApprovedFoundationAssets \
  -logFile "$LOG" >/dev/null 2>&1
rc=$?
if [ "$rc" -ne 0 ]; then
  echo "   FAILED (rc=$rc). Tail of $LOG:"
  tail -40 "$LOG"
  exit 1
fi
grep -E "^\[(WorldInteractionFoundationBuilder|RandomDungeonSceneBuilder)\]" "$LOG" | tail -5

echo
echo "== trap audit"
python3 "$ROOT/ops/dungeon-trap-audit.py"
audit=$?

echo
echo "Republish the server so the new trap manifest reaches the module:"
echo "  ops/republish-local-clear.sh"
exit "$audit"

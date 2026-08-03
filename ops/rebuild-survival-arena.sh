#!/usr/bin/env bash
# Rebuild the checked-in SurvivalArena scene: flat 40x40 deck at y=0, lava sea
# ten elevations below it, wrapped in the random dungeon's cavern envelope.
#
# One command, because it is one decision. The scene is fully generated — the
# deck, the broken-rock skirt, the lava sea, the envelope and the lighting all
# come out of SurvivalArenaSceneBuilder, so editing the builder and running this
# is the whole loop. There is nothing to hand-place afterwards.
#
# Needs:  Unity CLOSED. The shared batch preflight refuses a live editor,
#         clears orphaned batch state, and moves an unowned lockfile.
#         With the editor OPEN, use the menu instead:
#           Arena > Survival > Rebuild Survival Arena
# Env:    ARENA_SURVIVAL_ARENA_SEED=<int>  rerolls the cavern envelope only.
# Usage:  ops/rebuild-survival-arena.sh
# Exit:   0 = scene rebuilt and saved; non-zero = see the log.

set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
ROOT="$(pwd)"
UNITY="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity}"
LOG="$ROOT/Logs/survival-arena-rebuild.log"
SCENE="$ROOT/Assets/Arena/Content/Scenes/SurvivalArena.unity"
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
echo "== rebuilding SurvivalArena"
"$UNITY" -batchmode -quit -projectPath "$ROOT" \
  -executeMethod Arena.Editor.Survival.SurvivalArenaSceneBuilder.RebuildSurvivalArenaBatch \
  -logFile "$LOG" >/dev/null 2>&1
rc=$?
if [ "$rc" -ne 0 ]; then
  echo "   FAILED (rc=$rc). Tail of $LOG:"
  tail -40 "$LOG"
  exit 1
fi

if [ ! -f "$SCENE" ]; then
  echo "   FAILED: Unity exited 0 but $SCENE does not exist. Tail of $LOG:"
  tail -40 "$LOG"
  exit 1
fi

echo "== build report"
grep -E "^\[SurvivalArenaSceneBuilder\]|^\[CAVERN_ENVELOPE\]" "$LOG" || {
  echo "   FAILED: neither builder logged its summary. Tail of $LOG:"
  tail -40 "$LOG"
  exit 1
}

echo "OK: $SCENE"

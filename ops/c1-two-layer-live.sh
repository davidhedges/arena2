#!/usr/bin/env bash
#
# Put the layered-topology C1 two-layer episode on the live local server and run
# the §7.2 collision probe against it.
#
# The episode is baked into the dungeon's own scene and collision payload,
# because the server compiles world collision in with `include_str!`
# (server/src/open_world_scene.rs) — there is no runtime selector. The dungeon
# that gets overwritten is regenerated content; rebuild one whenever you want it
# back (Arena > Dungeons > Rebuild Random Dungeon).
#
# Usage: ops/c1-two-layer-live.sh
# Needs: Unity CLOSED (batchmode), a running SpacetimeDB, `websocket-client`.

set -uo pipefail
cd "$(cd "$(dirname "$0")/.." && pwd)" || exit 2

UNITY="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity}"
LOGS="DungeonLabReports/ab_logs"
mkdir -p "$LOGS"

[ -x "$UNITY" ] || { echo "REFUSING: no Unity at $UNITY (set UNITY_PATH)."; exit 2; }
ops/unity-batch-preflight.sh "$PWD" || exit 2

echo "== baking the two-layer episode into the dungeon scene"
"$UNITY" -batchmode -quit -projectPath "$PWD" \
  -executeMethod DungeonLab.Editor.DungeonLabGenerator.BakeTwoLayerEpisodeIntoDungeonScene \
  -logFile "$LOGS/c1_bake.log" >/dev/null 2>&1
if ! grep -q "TWO_LAYER_EPISODE_BAKE" "$LOGS/c1_bake.log"; then
  echo "!! bake failed; tail of $LOGS/c1_bake.log:"
  tail -40 "$LOGS/c1_bake.log"
  exit 2
fi
grep -o "\[TWO_LAYER_EPISODE_BAKE\].*" "$LOGS/c1_bake.log" | head -1

echo "== publishing to the local module"
if ! ARENA_GENERATE_BINDINGS=0 ops/republish-local-clear.sh >"$LOGS/c1_publish.log" 2>&1; then
  echo "!! publish failed; tail of $LOGS/c1_publish.log:"
  tail -25 "$LOGS/c1_publish.log"
  exit 2
fi

echo
exec python3 ops/c1-two-layer-probe.py --database "${ARENA_DATABASE:-arena}"

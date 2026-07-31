#!/usr/bin/env bash
#
# Put the layered-topology C1 two-layer episode on the live local server, run the
# §7.2 collision probe against it, and put the real dungeon back.
#
# WHY IT IS DESTRUCTIVE, and why that is not avoidable. The server compiles its
# world collision in with `include_str!` (`server/src/open_world_scene.rs`), so
# there is no runtime selector that could point at a second payload. The only way
# to get a hand-built episode in front of the real ground-sampling code is to
# write it into the dungeon's own payload, publish, measure, and restore.
#
# Restoration is by BYTE COPY of the files saved here, not by rebuilding — a
# rebuild would pick a fresh seed and produce a different dungeon, and the
# collision bake is not byte-stable across rebuilds anyway.
#
# Usage:
#   ops/c1-two-layer-live.sh            # bake, publish, probe, restore, republish
#   ops/c1-two-layer-live.sh --keep     # stop after the probe, leave the episode live
#   ops/c1-two-layer-live.sh --restore  # restore from the last backup and republish
#
# Needs: Unity CLOSED (batchmode), a running SpacetimeDB, `websocket-client`.

set -uo pipefail
cd "$(cd "$(dirname "$0")/.." && pwd)" || exit 2

UNITY="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity}"
BACKUP="DungeonLabReports/c1_live_backup"
LOGS="DungeonLabReports/ab_logs"
MODE="${1:-run}"

# Everything the bake overwrites. The scene and the payloads are skip-worktree,
# so git cannot restore them — this list IS the safety net.
PAYLOADS=(
  "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity"
  "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.collision.shared.json"
  "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.query_collision.shared.json"
  "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.doors.shared.json"
  "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.traps.shared.json"
  "server/src/world_data/random_dungeon.collision.shared.json"
  "server/src/world_data/random_dungeon.query_collision.shared.json"
  "server/src/world_data/random_dungeon.doors.shared.json"
  "server/src/world_data/random_dungeon.traps.shared.json"
)

backup_payloads() {
  # MEASURED THE HARD WAY 2026-07-31: this used to back up unconditionally, so a
  # second `--keep` run over an already-baked tree copied the EPISODE over the
  # real dungeon's backup and destroyed the only copy — the payloads are
  # skip-worktree, so git could not put them back either. A backup that already
  # exists is the thing keeping the dungeon alive; never write over it.
  if [ -d "$BACKUP" ]; then
    echo "!! REFUSING: a backup already exists at $BACKUP."
    echo "!! The tree is probably still baked from a previous --keep run, and"
    echo "!! backing up now would overwrite the real dungeon with the episode."
    echo "!! Restore first:  ops/c1-two-layer-live.sh --restore"
    return 1
  fi
  mkdir -p "$BACKUP"
  local missing=0
  for file in "${PAYLOADS[@]}"; do
    if [ ! -f "$file" ]; then
      echo "!! MISSING, cannot back up: $file"
      missing=1
      continue
    fi
    mkdir -p "$BACKUP/$(dirname "$file")"
    cp -p "$file" "$BACKUP/$file"
  done
  [ "$missing" = "0" ] || return 1
  # A manifest, so a later restore can prove it is putting back what it took.
  ( cd "$BACKUP" && find . -type f ! -name SHA256SUMS -exec shasum -a 256 {} + \
      | sort > SHA256SUMS )
  echo "== backed up ${#PAYLOADS[@]} files to $BACKUP"
}

restore_payloads() {
  if [ ! -f "$BACKUP/SHA256SUMS" ]; then
    echo "!! no backup at $BACKUP — refusing to restore."
    return 1
  fi
  ( cd "$BACKUP" && shasum -a 256 -c SHA256SUMS --quiet ) || {
    echo "!! the backup itself is corrupt; NOT restoring."
    return 1
  }
  # A backup that holds the EPISODE rather than the dungeon is worse than none:
  # restoring it looks like success and leaves the fixture baked in forever. The
  # episode is ~1 MB against a real dungeon's several MB, and it carries the
  # fixture's own root name, so this is cheap to tell apart.
  if grep -q "Two Layer Episode\|floor_0_9_level_4_suspended" \
       "$BACKUP/server/src/world_data/random_dungeon.collision.shared.json" 2>/dev/null; then
    echo "!! REFUSING: the backup at $BACKUP holds the EPISODE, not the dungeon."
    echo "!! Restoring it would bake the fixture in permanently. Rebuild instead:"
    echo "!!   Arena > Dungeons > Rebuild Random Dungeon"
    return 1
  fi
  for file in "${PAYLOADS[@]}"; do
    cp -p "$BACKUP/$file" "$file"
  done
  echo "== restored ${#PAYLOADS[@]} files from $BACKUP"
  echo "== clearing the backup so the next run takes a fresh one"
  rm -rf "$BACKUP"
}

publish() {
  echo "== publishing to the local module"
  ARENA_GENERATE_BINDINGS=0 ops/republish-local-clear.sh >"$LOGS/c1_publish.log" 2>&1
  local rc=$?
  if [ "$rc" -ne 0 ]; then
    echo "!! publish failed (rc=$rc); tail of $LOGS/c1_publish.log:"
    tail -25 "$LOGS/c1_publish.log"
    return 1
  fi
  return 0
}

mkdir -p "$LOGS"

if [ "$MODE" = "--restore" ]; then
  restore_payloads || exit 2
  publish || exit 2
  echo "RESTORED."
  exit 0
fi

if [ ! -x "$UNITY" ]; then
  echo "REFUSING: no Unity at $UNITY (set UNITY_PATH)."
  exit 2
fi
ops/unity-batch-preflight.sh "$PWD" || exit 2

backup_payloads || exit 2

echo "== baking the two-layer episode into the dungeon scene"
"$UNITY" -batchmode -quit -projectPath "$PWD" \
  -executeMethod DungeonLab.Editor.DungeonLabGenerator.BakeTwoLayerEpisodeIntoDungeonScene \
  -logFile "$LOGS/c1_bake.log" >/dev/null 2>&1
rc=$?
if [ "$rc" -ne 0 ] || ! grep -q "TWO_LAYER_EPISODE_BAKE" "$LOGS/c1_bake.log"; then
  echo "!! bake failed (rc=$rc); tail of $LOGS/c1_bake.log:"
  tail -40 "$LOGS/c1_bake.log"
  restore_payloads
  exit 2
fi
grep -o "\[TWO_LAYER_EPISODE_BAKE\].*" "$LOGS/c1_bake.log" | head -1

# Prove the bake actually changed the payload the server will compile in. A
# probe run against an unchanged dungeon would pass or fail for reasons that
# have nothing to do with the episode.
if cmp -s "$BACKUP/server/src/world_data/random_dungeon.collision.shared.json" \
          "server/src/world_data/random_dungeon.collision.shared.json"; then
  echo "!! the server collision payload is byte-identical to the backup — the"
  echo "!! bake did not reach it. Refusing to report a verdict on stale geometry."
  restore_payloads
  exit 2
fi
echo "== server collision payload changed, as it must have"

publish || { restore_payloads; publish; exit 2; }

echo
python3 ops/c1-two-layer-probe.py --database "${ARENA_DATABASE:-arena}"
probe_rc=$?
echo

if [ "$MODE" = "--keep" ]; then
  echo "== --keep: leaving the episode live. Put the dungeon back with:"
  echo "     ops/c1-two-layer-live.sh --restore"
  exit "$probe_rc"
fi

restore_payloads || exit 2
publish || exit 2
echo "== the real dungeon is back on the local module"
exit "$probe_rc"

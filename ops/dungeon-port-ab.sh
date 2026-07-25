#!/usr/bin/env bash
# Prove (or disprove) that the working tree's generator changes are
# output-neutral, by running the same 200-seed batch twice — once with the
# working tree stashed (so HEAD's generator runs) and once with it restored —
# and diffing the two reports seed by seed.
#
# This is the check a "must not move the hash" gate actually wants: it compares
# against the CURRENT commit rather than a hash recorded some commits ago, so
# unrelated content drift cannot masquerade as a regression.
#
# Usage:  ops/dungeon-port-ab.sh [profile]     # profile defaults to dense
# Needs:  Unity closed (batchmode cannot take Temp/UnityLockfile).
# Exit:   0 = byte-identical reports, 1 = a real difference, 2 = could not run.

set -uo pipefail
# The stash below removes untracked files — including this script. Run a copy from
# outside the repo and point ARENA_REPO at the tree to avoid pulling the rug out.
cd "${ARENA_REPO:-$(cd "$(dirname "$0")/.." && pwd)}" || exit 2
PROFILE="${1:-dense}"
UNITY="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity}"
REPORTS="DungeonLabReports"
LIVE="$REPORTS/dungeon_plan_2026072100_2026072299.json"
PRE="$REPORTS/ab_preport_$PROFILE.json"
POST="$REPORTS/ab_postport_$PROFILE.json"
LOGS="$REPORTS/ab_logs"

if [ -e Temp/UnityLockfile ]; then
  echo "REFUSING: Temp/UnityLockfile exists — quit the Unity editor first."
  exit 2
fi
if [ ! -x "$UNITY" ]; then
  echo "REFUSING: no Unity at $UNITY (set UNITY_PATH)."
  exit 2
fi

mkdir -p "$LOGS"
STASH_TAG="dungeon-port-ab-$$"
stashed=0
restore() {
  [ "$stashed" = "1" ] || return 0
  # Never pop blind: this repo can hold unrelated stashes. Read into a variable
  # rather than testing a pipeline — `grep -q` exits on match and SIGPIPEs its
  # upstream, which `pipefail` reports as failure, so a pipeline condition here
  # is a coin flip.
  local top
  top=$(git stash list | sed -n 1p)
  case "$top" in
    *"$STASH_TAG"*) ;;
    *) echo "!! stash@{0} is [$top], not $STASH_TAG — NOT popping. Recover with:"
       echo "!!   git stash list && git stash pop <the $STASH_TAG entry>"
       return 1 ;;
  esac
  echo "== restoring working tree from stash"
  if git stash pop --quiet; then
    stashed=0
  else
    echo "!! stash pop FAILED. Your work is safe in: git stash list | grep $STASH_TAG"
    return 1
  fi
}
trap restore EXIT INT TERM

# The failure that matters is not a bad pop, it is reporting a verdict on two legs
# that were secretly the same code. Each leg asserts which side it is running on
# before it spends twenty minutes measuring it.
assert_leg() {           # $1 = expected: "head" or "worktree"
  local dirty
  dirty=$(git status --porcelain)
  if [ "$1" = "head" ] && [ -n "$dirty" ]; then
    echo "!! ABORT: the pre-port leg wants a tree matching HEAD, but it is dirty:"
    printf '%s\n' "$dirty" | sed -n '1,8p'
    return 1
  fi
  if [ "$1" = "worktree" ] && [ -z "$dirty" ]; then
    echo "!! ABORT: the post-port leg wants the working-tree changes, but the tree"
    echo "!!        matches HEAD — the stash never came back, so both legs would"
    echo "!!        run identical code and any 'byte-identical' verdict is vacuous."
    return 1
  fi
  return 0
}

run_batch() {           # $1 = label, $2 = destination report
  echo "== batch validate 200 seeds ($PROFILE) — $1"
  rm -f "$LIVE"
  ARENA_DUNGEON_GENERATION_PROFILE="$PROFILE" "$UNITY" \
    -batchmode -quit \
    -projectPath "$PWD" \
    -executeMethod DungeonLab.Editor.DungeonLabGenerator.BatchValidate200Seeds \
    -logFile "$LOGS/$1.log" >/dev/null 2>&1
  local rc=$?
  if [ ! -s "$LIVE" ]; then
    echo "   FAILED (rc=$rc) — no report written. Tail of $LOGS/$1.log:"
    tail -30 "$LOGS/$1.log"
    return 1
  fi
  cp "$LIVE" "$2"
  python3 -c "
import json,sys
d=json.load(open('$2'))
print('   resultHash', d['resultHash'][:16], ' accepted', d['accepted'], '/', d['seedCount'],
      ' catalog', d['catalogDigest'][:8])"
  return 0
}

if ! git diff --quiet || [ -n "$(git status --porcelain --untracked-files=normal)" ]; then
  echo "== stashing working tree (tracked + untracked)"
  git stash push --include-untracked --quiet --message "$STASH_TAG" || exit 2
  stashed=1
else
  echo "== working tree already matches HEAD; the A/B legs would be identical."
  exit 2
fi

assert_leg head     || exit 2
run_batch preport "$PRE" || exit 2
restore || exit 2
assert_leg worktree || exit 2
run_batch postport "$POST" || exit 2

echo
python3 - "$PRE" "$POST" <<'PY'
import json, sys
pre, post = (json.load(open(p)) for p in sys.argv[1:3])
same = pre["resultHash"] == post["resultHash"]
print(f"pre-port  resultHash {pre['resultHash']}  accepted {pre['accepted']}/{pre['seedCount']}")
print(f"post-port resultHash {post['resultHash']}  accepted {post['accepted']}/{post['seedCount']}")
if same:
    print("\nVERDICT: byte-identical. The working tree's generator changes are output-neutral.")
    sys.exit(0)

print("\nVERDICT: the reports differ. Per-seed breakdown of what moved:")
a = {s["seed"]: s for s in pre["seeds"]}
b = {s["seed"]: s for s in post["seeds"]}
fields = ["accepted", "layoutAttempts", "tierAttempts"]
moved = 0
for seed in sorted(set(a) | set(b)):
    x, y = a.get(seed), b.get(seed)
    if x is None or y is None:
        print(f"  {seed}: present in only one run"); moved += 1; continue
    diffs = [f"{f} {x.get(f)}->{y.get(f)}" for f in fields if x.get(f) != y.get(f)]
    for h in sorted(set(x.get("hashes", {})) | set(y.get("hashes", {}))):
        if x.get("hashes", {}).get(h) != y.get("hashes", {}).get(h):
            diffs.append(f"hash.{h}")
    if diffs:
        moved += 1
        pat = (y.get("routeIntent") or x.get("routeIntent") or {}).get("patternId", "?")
        print(f"  {seed} [{pat}]: {', '.join(diffs)}")
print(f"\n{moved} of {post['seedCount']} seeds differ.")
sys.exit(1)
PY

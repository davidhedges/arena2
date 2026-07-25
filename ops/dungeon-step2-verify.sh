#!/usr/bin/env bash
# Step 2 of the route-topology cutover is a DELIBERATE rebaseline: every seed's
# output moves once. So the gate is not "the hash held" — it is:
#
#   1. two independent 200-seed runs produce the byte-identical resultHash
#      (determinism: nothing depends on wall clock, iteration order or state
#      left over from a previous seed), and
#   2. the accept rate is 199/200 or better.
#
# Both legs run the same code on the same tree; ops/dungeon-port-ab.sh (which
# stashes the working tree) is the wrong tool here and is deliberately not used.
#
# Usage:  ops/dungeon-step2-verify.sh [profile]     # profile defaults to dense
# Needs:  Unity closed (batchmode cannot take Temp/UnityLockfile).
# Exit:   0 = deterministic and >= 199/200, 1 = a real problem, 2 = could not run.

set -uo pipefail
cd "${ARENA_REPO:-$(cd "$(dirname "$0")/.." && pwd)}" || exit 2
PROFILE="${1:-dense}"
UNITY="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity}"
REPORTS="DungeonLabReports"
LIVE="$REPORTS/dungeon_plan_2026072100_2026072299.json"
RUN1="$REPORTS/step2_run1_$PROFILE.json"
RUN2="$REPORTS/step2_run2_$PROFILE.json"
LOGS="$REPORTS/step2_logs"

if [ -e Temp/UnityLockfile ]; then
  echo "REFUSING: Temp/UnityLockfile exists — quit the Unity editor first."
  exit 2
fi
if [ ! -x "$UNITY" ]; then
  echo "REFUSING: no Unity at $UNITY (set UNITY_PATH)."
  exit 2
fi

mkdir -p "$LOGS"

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
    tail -40 "$LOGS/$1.log"
    return 1
  fi
  cp "$LIVE" "$2"
  return 0
}

run_batch run1 "$RUN1" || exit 2
run_batch run2 "$RUN2" || exit 2

echo
python3 - "$RUN1" "$RUN2" <<'PY'
import json, sys, collections
a, b = (json.load(open(p)) for p in sys.argv[1:3])
print(f"run 1  resultHash {a['resultHash']}  accepted {a['accepted']}/{a['seedCount']}  catalog {a['catalogDigest'][:8]}")
print(f"run 2  resultHash {b['resultHash']}  accepted {b['accepted']}/{b['seedCount']}  catalog {b['catalogDigest'][:8]}")

ok = True
if a["resultHash"] != b["resultHash"]:
    ok = False
    print("\nNOT DETERMINISTIC — per-seed breakdown of what moved:")
    x = {s["seed"]: s for s in a["seeds"]}
    y = {s["seed"]: s for s in b["seeds"]}
    for seed in sorted(set(x) | set(y)):
        p, q = x.get(seed), y.get(seed)
        if p is None or q is None:
            print(f"  {seed}: present in only one run"); continue
        diffs = [f"{f} {p.get(f)}->{q.get(f)}" for f in ("accepted", "layoutAttempts", "tierAttempts")
                 if p.get(f) != q.get(f)]
        diffs += [f"hash.{h}" for h in sorted(set(p.get("hashes", {})) | set(q.get("hashes", {})))
                  if p.get("hashes", {}).get(h) != q.get("hashes", {}).get(h)]
        if diffs:
            print(f"  {seed}: {', '.join(diffs)}")
else:
    print("\nDETERMINISTIC: two independent runs are byte-identical.")

# What step 2 is actually supposed to have changed, reported rather than asserted.
topo = collections.Counter()
nodes = collections.Counter()
slack = collections.Counter()
for s in a["seeds"]:
    ri = s.get("routeIntent") or {}
    topo[ri.get("patternId", "?")] += 1
    nodes[ri.get("nodeCount", 0)] += 1
    rp = s.get("routePlacement") or {}
    if "latticeSlackSpentCells" in rp:
        slack[rp["latticeSlackSpentCells"]] += 1
print("\ntopology mix   ", a.get("topologySelection", {}).get("selectedPatternCounts", {}))
print("accepted mix   ", a.get("topologySelection", {}).get("acceptedPatternCounts", {}))
print("room counts    ", dict(sorted(nodes.items())))
if slack:
    print("lattice slack  ", dict(sorted(slack.items())))
failed = [(s["seed"], (s.get("routeIntent") or {}).get("patternId"), s.get("lastRejectedAttemptCode"))
          for s in a["seeds"] if s.get("accepted") is not True]
print("failed seeds   ", failed)
print("rejection codes", a.get("rejectionCodes", {}))
print("attempts       ", a.get("attemptDistribution", {}), a.get("tierAttemptDistribution", {}))

if a["accepted"] < a["seedCount"] - 1:
    ok = False
    print(f"\nACCEPT RATE {a['accepted']}/{a['seedCount']} is below the 199/200 bar.")
sys.exit(0 if ok else 1)
PY

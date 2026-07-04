#!/usr/bin/env python3
"""S8 lag-comp audit summary from server logs.

Parses the [LAG_COMP] dual-verdict audit lines
(docs/lag-compensation-design-2026-07-04.md §4) out of `spacetime logs` and
prints per-check press counts, rewind-magnitude distribution, pose sources,
and the money metric: verdict flips (hits that connect only because of lag
comp), split by switch state.

Usage:
  spacetime logs <database> | python3 ops/analyze-s8-lag-comp.py
  python3 ops/analyze-s8-lag-comp.py --database <database>   # runs spacetime logs itself
"""

import argparse
import collections
import re
import subprocess
import sys

GATE_RE = re.compile(
    r"\[LAG_COMP\] (?P<check>melee_gate|impact_recheck) caster=(?P<caster>\S+) "
    r"target=(?P<target>\S+) strike=(?P<strike>\S+) rewound_ms=(?P<rewound_ms>-?\d+) "
    r"source=(?P<source>\S+) enabled=(?P<enabled>\S+) present=(?P<present>\S+) "
    r"rewound=(?P<rewound>\S+) flip=(?P<flip>\S+)"
)
OVERLAY_RE = re.compile(
    r"\[LAG_COMP\] overlay caster=(?P<caster>\S+) target=(?P<target>\S+) "
    r"rewound_ms=(?P<rewound_ms>-?\d+) source=(?P<source>\S+) delta=(?P<delta>[\d.]+)"
)


def percentile(values, p):
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round(p / 100 * (len(ordered) - 1))))
    return ordered[index]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", help="run `spacetime logs <database>` directly")
    args = parser.parse_args()

    if args.database:
        result = subprocess.run(
            ["spacetime", "logs", args.database], capture_output=True, text=True
        )
        if result.returncode != 0:
            sys.stderr.write(result.stderr)
            sys.exit(1)
        lines = result.stdout.splitlines()
    else:
        lines = sys.stdin.read().splitlines()

    gates = [m.groupdict() for line in lines for m in [GATE_RE.search(line)] if m]
    overlays = [m.groupdict() for line in lines for m in [OVERLAY_RE.search(line)] if m]

    if not gates and not overlays:
        print("no [LAG_COMP] audit lines found")
        return

    for check in ("melee_gate", "impact_recheck"):
        rows = [g for g in gates if g["check"] == check]
        if not rows:
            continue
        by_state = collections.defaultdict(list)
        for g in rows:
            by_state[g["enabled"] == "true"].append(g)
        print(f"\n== {check}: {len(rows)} evaluations with a view report")
        for enabled in (False, True):
            state_rows = by_state.get(enabled, [])
            if not state_rows:
                continue
            flips = [g for g in state_rows if g["flip"] == "true"]
            rewinds = [int(g["rewound_ms"]) for g in state_rows]
            sources = collections.Counter(g["source"] for g in state_rows)
            verdict_pairs = collections.Counter(
                (g["present"], g["rewound"]) for g in flips
            )
            print(
                f"  switch {'ON ' if enabled else 'OFF'}: n={len(state_rows)} "
                f"flips={len(flips)} ({100 * len(flips) / len(state_rows):.1f}%) "
                f"rewound_ms p50={percentile(rewinds, 50):.0f} "
                f"p95={percentile(rewinds, 95):.0f} max={max(rewinds)}"
            )
            print(f"    pose sources: {dict(sources)}")
            for (present, rewound), count in verdict_pairs.most_common():
                print(f"    flip present={present} -> rewound={rewound}: {count}")

    if overlays:
        deltas = [float(o["delta"]) for o in overlays]
        rewinds = [int(o["rewound_ms"]) for o in overlays]
        sources = collections.Counter(o["source"] for o in overlays)
        print(f"\n== spell/charge overlays: {len(overlays)} applied")
        print(
            f"  rewound_ms p50={percentile(rewinds, 50):.0f} p95={percentile(rewinds, 95):.0f}; "
            f"pose delta m p50={percentile(deltas, 50):.2f} p95={percentile(deltas, 95):.2f} "
            f"max={max(deltas):.2f}"
        )
        print(f"  pose sources: {dict(sources)}")


if __name__ == "__main__":
    main()

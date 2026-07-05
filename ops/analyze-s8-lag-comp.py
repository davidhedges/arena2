#!/usr/bin/env python3
"""S8/S9/S10 lag-comp audit summary from server logs.

Parses the [LAG_COMP] dual-verdict audit lines
(docs/lag-compensation-design-2026-07-04.md §4,
docs/auto-attack-rewind-design-2026-07-04.md §4,
docs/sweep-projectile-rewind-design-2026-07-05.md §3) out of `spacetime logs`
and prints per-check evaluation counts, rewind-magnitude distribution, pose
sources, and the money metric: verdict flips (hits that connect only because
of lag comp), split by switch state and by signal (press vs standing). S10
adds the `sweep_hit` check — per-victim cone/radius sweep membership rewound
against the attacker-view pose.

Also summarizes the S9 [DEFENSE_LATE] telemetry rider: defensible hits that
resolved undefended with a parry/block press arriving within 400 ms — the
D4b re-evaluation dataset.

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
    r"\[LAG_COMP\] (?P<check>melee_gate|impact_recheck|auto_reach|auto_los|sweep_hit) "
    r"caster=(?P<caster>\S+) "
    r"target=(?P<target>\S+) strike=(?P<strike>\S+) rewound_ms=(?P<rewound_ms>-?\d+) "
    r"source=(?P<source>\S+) enabled=(?P<enabled>\S+) present=(?P<present>\S+) "
    r"rewound=(?P<rewound>\S+) flip=(?P<flip>\S+)(?: signal=(?P<signal>\S+))?"
)
OVERLAY_RE = re.compile(
    r"\[LAG_COMP\] overlay caster=(?P<caster>\S+) target=(?P<target>\S+) "
    r"rewound_ms=(?P<rewound_ms>-?\d+) source=(?P<source>\S+) delta=(?P<delta>[\d.]+)"
)
DEFENSE_LATE_RE = re.compile(
    r"\[DEFENSE_LATE\] defender=(?P<defender>\S+) kind=(?P<kind>\S+) "
    r"late_by_ms=(?P<late_by_ms>\d+) delivery=(?P<delivery>\S+)"
)
# Server log lines start with an RFC3339 timestamp; used only for the
# combat-minute rate in the [DEFENSE_LATE] section.
TIMESTAMP_RE = re.compile(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})")

CHECKS = ("melee_gate", "impact_recheck", "auto_reach", "auto_los", "sweep_hit")


def percentile(values, p):
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round(p / 100 * (len(ordered) - 1))))
    return ordered[index]


def summarize_gate_rows(rows):
    by_state = collections.defaultdict(list)
    for g in rows:
        by_state[g["enabled"] == "true"].append(g)
    for enabled in (False, True):
        state_rows = by_state.get(enabled, [])
        if not state_rows:
            continue
        flips = [g for g in state_rows if g["flip"] == "true"]
        rewinds = [int(g["rewound_ms"]) for g in state_rows]
        sources = collections.Counter(g["source"] for g in state_rows)
        verdict_pairs = collections.Counter((g["present"], g["rewound"]) for g in flips)
        print(
            f"  switch {'ON ' if enabled else 'OFF'}: n={len(state_rows)} "
            f"flips={len(flips)} ({100 * len(flips) / len(state_rows):.1f}%) "
            f"rewound_ms p50={percentile(rewinds, 50):.0f} "
            f"p95={percentile(rewinds, 95):.0f} max={max(rewinds)}"
        )
        print(f"    pose sources: {dict(sources)}")
        for (present, rewound), count in verdict_pairs.most_common():
            print(f"    flip present={present} -> rewound={rewound}: {count}")


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
    late_presses = [
        m.groupdict() for line in lines for m in [DEFENSE_LATE_RE.search(line)] if m
    ]
    # Pre-S9 lines carry no signal= field; every stamped context then was a press.
    for g in gates:
        g["signal"] = g["signal"] or "press"

    if not gates and not overlays and not late_presses:
        print("no [LAG_COMP] or [DEFENSE_LATE] audit lines found")
        return

    for check in CHECKS:
        check_rows = [g for g in gates if g["check"] == check]
        if not check_rows:
            continue
        for signal in ("press", "standing"):
            rows = [g for g in check_rows if g["signal"] == signal]
            if not rows:
                continue
            print(
                f"\n== {check} [signal={signal}]: {len(rows)} evaluations with a view report"
            )
            summarize_gate_rows(rows)

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

    if late_presses:
        lates = [int(p["late_by_ms"]) for p in late_presses]
        by_kind = collections.defaultdict(list)
        for p in late_presses:
            by_kind[p["kind"]].append(int(p["late_by_ms"]))
        deliveries = collections.Counter(p["delivery"] for p in late_presses)

        # Combat-minute rate: minutes in which any [LAG_COMP]/[AUTO_ATTACK]/
        # combat audit line landed, approximated from gate lines' timestamps.
        combat_minutes = {
            m.group(1)[:16]
            for line in lines
            if GATE_RE.search(line) or DEFENSE_LATE_RE.search(line)
            for m in [TIMESTAMP_RE.search(line)]
            if m
        }
        rate = len(late_presses) / max(1, len(combat_minutes))
        print(f"\n== [DEFENSE_LATE] late defensive presses: {len(late_presses)}")
        print(
            f"  rate={rate:.2f}/combat-minute over {len(combat_minutes)} active minutes; "
            f"late_by_ms p50={percentile(lates, 50):.0f} "
            f"p95={percentile(lates, 95):.0f} max={max(lates)}"
        )
        for kind, values in sorted(by_kind.items()):
            print(
                f"  kind={kind}: n={len(values)} late_by_ms p50={percentile(values, 50):.0f} "
                f"p95={percentile(values, 95):.0f}"
            )
        print(f"  deliveries: {dict(deliveries)}")


if __name__ == "__main__":
    main()

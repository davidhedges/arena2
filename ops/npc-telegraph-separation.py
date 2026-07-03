#!/usr/bin/env python3
"""S3 evidence: measured CAST -> damage separation for NPC telegraphed swings.

Pairs each NPC_MELEE_ATTACK CAST with its outcome event (IMPACT/PARRY/BLOCK)
by action_instance_id in the live combat_event table and prints the per-swing
separation against the authored windup the CAST carries in scalar_value.

Combat events prune after 20 seconds server-side, so run this during or
immediately after the encounter being measured. A CAST older than its authored
windup with no outcome event is a whiff/cancel (dodged out of reach, target
lost, NPC CC'd/dead at resolve time). ARENA_NPC_HARMLESS builds still emit
IMPACT (damage 0), so harmless swings measure normally here.

Usage:
  ops/npc-telegraph-separation.py [--database arena] [--server <host>]
"""

import argparse
import subprocess
import sys

QUERY = (
    "SELECT action_instance_id, event_type, created_at_micros, scalar_value, ability_id "
    "FROM combat_event WHERE action_kind = 'NPC_MELEE_ATTACK'"
)

OUTCOME_EVENTS = ("IMPACT", "PARRY", "BLOCK")
# A CAST this much older than its authored windup with no outcome is settled
# as a whiff/cancel rather than still-pending (covers tick rounding + clock).
WHIFF_SETTLE_SLACK_MICROS = 500_000


def run_sql(database, server):
    cmd = ["spacetime", "sql"]
    if server:
        cmd += ["-s", server]
    cmd += [database, QUERY]
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.stderr.write(result.stderr)
        sys.exit(result.returncode)
    return result.stdout


def parse_rows(output):
    rows = []
    for line in output.splitlines():
        if "|" not in line or set(line.strip()) <= {"-", "+", "|", " "}:
            continue
        cells = [cell.strip().strip('"') for cell in line.split("|")]
        if len(cells) != 5 or cells[0] == "action_instance_id":
            continue
        try:
            rows.append(
                {
                    "id": cells[0],
                    "event": cells[1],
                    "micros": int(cells[2]),
                    "scalar": float(cells[3]),
                    "template": cells[4],
                }
            )
        except ValueError:
            continue
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="arena")
    parser.add_argument("--server", default=None)
    args = parser.parse_args()

    rows = parse_rows(run_sql(args.database, args.server))
    casts = {r["id"]: r for r in rows if r["event"] == "CAST"}
    outcomes = {r["id"]: r for r in rows if r["event"] in OUTCOME_EVENTS}

    if not casts:
        print("No NPC_MELEE_ATTACK CAST events in the live window (20 s retention).")
        print("Stand a player in a hostile NPC's reach, then rerun.")
        return

    latest_micros = max(r["micros"] for r in rows)
    deltas = []
    print(f"{'template':<34} {'outcome':<8} {'authored_ms':>11} {'measured_ms':>11}")
    for swing_id, cast in sorted(casts.items(), key=lambda kv: kv[1]["micros"]):
        authored_ms = cast["scalar"] * 1000.0
        outcome = outcomes.get(swing_id)
        if outcome is not None:
            measured_ms = (outcome["micros"] - cast["micros"]) / 1000.0
            deltas.append(measured_ms)
            print(
                f"{cast['template']:<34} {outcome['event']:<8} "
                f"{authored_ms:>11.0f} {measured_ms:>11.1f}"
            )
        elif latest_micros - cast["micros"] > cast["scalar"] * 1_000_000 + WHIFF_SETTLE_SLACK_MICROS:
            print(f"{cast['template']:<34} {'WHIFF':<8} {authored_ms:>11.0f} {'-':>11}")
        else:
            print(f"{cast['template']:<34} {'PENDING':<8} {authored_ms:>11.0f} {'-':>11}")

    if deltas:
        print(
            f"\n{len(deltas)} resolved swings: separation "
            f"min {min(deltas):.1f} ms / mean {sum(deltas) / len(deltas):.1f} ms / "
            f"max {max(deltas):.1f} ms (expect authored windup rounded up to the 33 ms tick)"
        )


if __name__ == "__main__":
    main()

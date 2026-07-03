#!/usr/bin/env python3
"""S4 evidence: predicted-action results grouped by family and reject reason.

Reads the live predicted_action_result table and prints (family, result,
reject_reason) counts plus every LineOfSightBlocked / GapCloseBlocked row.
Under the S4 LOS-unification contract, LineOfSightBlocked must now appear for
the previously-exempt kit: family=Melee presses (plain strikes AND
gap-closers) reject with LineOfSightBlocked when the target is behind cover,
while a clear-sight-but-blocked-dash gap-close stays GapCloseBlocked.

Rows expire ~10 seconds after insert server-side, so run this during or
immediately after the presses being measured.

Enum cells are matched on the live `spacetime sql` rendering (verified
2026-07-04 against an s4-los-probe run: enum columns print as
`(camelCaseTag = ())`, e.g. `(lineOfSightBlocked = ())`), never on Rust
constant names.

Usage:
  ops/action-reject-reasons.py [--database arena] [--server <host>]
"""

import argparse
import subprocess
import sys
from collections import Counter

QUERY = (
    "SELECT owner, family, predicted_action_id, result, reject_reason, created_at_micros "
    "FROM predicted_action_result"
)

# Live wire tags (camelCase, from the `(tag = ())` sum rendering).
RESULT_REJECTED = "rejected"
REASON_LOS = "lineOfSightBlocked"
REASON_GAP_PATH = "gapCloseBlocked"


def enum_tag(cell):
    cell = cell.strip()
    if cell.startswith("(") and "=" in cell:
        return cell[1:].split("=", 1)[0].strip()
    return cell


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
        if len(cells) != 6 or cells[0] == "owner":
            continue
        try:
            rows.append(
                {
                    "owner": cells[0],
                    "family": enum_tag(cells[1]),
                    "token": cells[2],
                    "result": enum_tag(cells[3]),
                    "reason": enum_tag(cells[4]),
                    "micros": int(cells[5]),
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
    if not rows:
        print("No predicted_action_result rows in the live window (~10 s retention).")
        print("Press actions (or run ops/s4-los-probe.py) and rerun immediately.")
        return

    counts = Counter((r["family"], r["result"], r["reason"]) for r in rows)
    print(f"{'family':<12} {'result':<14} {'reject_reason':<22} {'count':>5}")
    for (family, result, reason), count in sorted(counts.items()):
        print(f"{family:<12} {result:<14} {reason:<22} {count:>5}")

    los_rows = [r for r in rows if r["reason"] in (REASON_LOS, REASON_GAP_PATH)]
    if los_rows:
        print("\nLOS / gap-path rejects (token labels identify the pressed action):")
        for r in sorted(los_rows, key=lambda r: r["micros"]):
            print(
                f"  {r['micros']:>16}  {r['family']:<10} {r['reason']:<22} "
                f"token={r['token']} owner={r['owner'][:8]}"
            )
    else:
        print(f"\nNo {REASON_LOS} / {REASON_GAP_PATH} rows in the window.")


if __name__ == "__main__":
    main()

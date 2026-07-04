#!/usr/bin/env python3
"""S6 evidence analyzer: local auto-attack swing scheduling.

Reads the aa_* columns RemotePresentationAbLog appends to
Logs/remote-presentation-ab.csv (1 Hz cumulative counters from
AutoAttackSwingScheduler + the contact-cue ledger's auto split) and prints
per-session deltas against the S6 acceptance criteria:

  - swing-start error vs the converted next_swing_at schedule (aa_start_err_*)
    and vs the authoritative CAST timestamp (aa_cast_align_*, expected within
    one 33 ms tick),
  - duplicate suppression: every fired local swing's CAST consumed
    (aa_supp_cast == aa_fired, aa_expired == 0),
  - falsePos ≈ 0 for the auto contact cues (aa_cue_false_pos),
  - zero double-swing risk events (aa_expired + aa_mismatch),
  - holds produce no local swings (aa_held counts mirror-held schedules —
    these are the behind-cover / out-of-range cases NOT fired locally).

Usage:
  python3 ops/analyze-s6-auto-swing.py             # latest session
  python3 ops/analyze-s6-auto-swing.py --all       # every session in the log
  python3 ops/analyze-s6-auto-swing.py --csv PATH  # explicit log path
"""

import argparse
import os
import sys

AA_COLUMNS = [
    "aa_fired", "aa_supp_cast", "aa_unpred_cast", "aa_held", "aa_late",
    "aa_expired", "aa_mismatch",
    "aa_start_err_last_ms", "aa_start_err_max_ms",
    "aa_cast_align_last_ms", "aa_cast_align_max_abs_ms",
    "aa_cue_fired", "aa_cue_matched", "aa_cue_false_pos", "aa_cue_supp_auth",
]
TICK_MS = 33


def parse_sessions(path):
    sessions = []
    header = None
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            if line.startswith("# session"):
                sessions.append({"label": line.removeprefix("# ").strip(), "rows": []})
                header = None
                continue
            if line.startswith("unix_ms,"):
                header = line.split(",")
                if sessions:
                    sessions[-1]["header"] = header
                continue
            if header is None or not sessions:
                continue
            cells = line.split(",")
            if len(cells) != len(header):
                continue
            sessions[-1]["rows"].append(dict(zip(header, cells)))
    return [s for s in sessions if s.get("rows")]


def val(row, key, default=0.0):
    try:
        return float(row.get(key, default))
    except (TypeError, ValueError):
        return default


def per_swing_samples(rows, counter_key, sample_key):
    """Collect sample_key values on rows where counter_key incremented —
    approximates a per-swing series from 1 Hz cumulative snapshots."""
    samples = []
    prev = None
    for row in rows:
        count = val(row, counter_key)
        if prev is not None and count > prev:
            samples.append(val(row, sample_key))
        prev = count
    return samples


def analyze(session):
    rows = session["rows"]
    header = session.get("header", [])
    if not any(c in header for c in AA_COLUMNS):
        print(f"  (no aa_* columns in this session — client build predates S6)")
        return

    first, last = rows[0], rows[-1]
    delta = {c: val(last, c) - val(first, c) for c in AA_COLUMNS}
    fired = delta["aa_fired"]
    supp = delta["aa_supp_cast"]
    unpred = delta["aa_unpred_cast"]
    held = delta["aa_held"]
    late = delta["aa_late"]
    expired = delta["aa_expired"]
    mismatch = delta["aa_mismatch"]
    duration_s = (val(last, "unix_ms") - val(first, "unix_ms")) / 1000.0

    start_err_samples = per_swing_samples(rows, "aa_fired", "aa_start_err_last_ms")
    align_samples = per_swing_samples(rows, "aa_supp_cast", "aa_cast_align_last_ms")
    start_err_max = val(last, "aa_start_err_max_ms")
    align_max_abs = val(last, "aa_cast_align_max_abs_ms")

    print(f"  duration: {duration_s:.0f}s   precise-clock rows: "
          f"{sum(1 for r in rows if val(r, 's5_est_precise') > 0)}/{len(rows)}")
    print(f"  local swings fired: {fired:.0f}   held by mirror: {held:.0f}   "
          f"late/skipped: {late:.0f}")
    print(f"  CASTs: suppressed as duplicate {supp:.0f}, unpredicted {unpred:.0f}, "
          f"mismatched {mismatch:.0f}")
    if start_err_samples:
        print(f"  start error vs schedule (ms): per-swing {sorted(start_err_samples)}  "
              f"session max {start_err_max:.0f}")
    if align_samples:
        print(f"  fire vs CAST timestamp (ms): per-swing {sorted(align_samples)}  "
              f"session max |align| {align_max_abs:.0f}")
    print(f"  auto contact cues: fired {delta['aa_cue_fired']:.0f}, "
          f"matched {delta['aa_cue_matched']:.0f}, "
          f"falsePos {delta['aa_cue_false_pos']:.0f}, "
          f"suppressedAuth {delta['aa_cue_supp_auth']:.0f}")

    def verdict(label, ok, detail):
        print(f"  [{'PASS' if ok else 'FAIL'}] {label}: {detail}")

    if fired > 0:
        verdict(
            "swing-start error within ~1 tick",
            align_max_abs <= TICK_MS * 1.5 if align_samples else start_err_max <= TICK_MS,
            f"max |fire−CAST| {align_max_abs:.0f} ms, max sched err {start_err_max:.0f} ms "
            f"(tick {TICK_MS} ms)",
        )
        verdict(
            "every fired swing's CAST suppressed",
            supp == fired and expired == 0,
            f"suppressed {supp:.0f}/{fired:.0f}, expired-no-CAST {expired:.0f}",
        )
        verdict(
            "zero double-swing risk events",
            expired == 0 and mismatch == 0,
            f"expired {expired:.0f}, mismatched {mismatch:.0f}",
        )
        verdict(
            "auto cue falsePos ≈ 0",
            delta["aa_cue_false_pos"] == 0,
            f"{delta['aa_cue_false_pos']:.0f} false positives",
        )
    else:
        print("  (no local swings fired this session — armed autos need a precise "
              "clock and the scheduler toggle ON)")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--csv",
        default=os.path.join(os.path.dirname(__file__), "..", "Logs", "remote-presentation-ab.csv"),
    )
    parser.add_argument("--all", action="store_true", help="analyze every session block")
    args = parser.parse_args()

    path = os.path.abspath(args.csv)
    if not os.path.exists(path):
        print(f"no CSV at {path} — run a client session first")
        return 1

    sessions = parse_sessions(path)
    if not sessions:
        print("no sessions in the log")
        return 1

    picked = sessions if args.all else sessions[-1:]
    for session in picked:
        print(f"\n== {session['label']}")
        analyze(session)
    return 0


if __name__ == "__main__":
    sys.exit(main())

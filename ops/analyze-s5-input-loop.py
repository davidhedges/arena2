#!/usr/bin/env python3
"""Summarize the S5 closed-loop input columns from Logs/remote-presentation-ab.csv.

Per session (rows between `# session ...` markers): fallback-ack rate over
the window, lead trajectory and converged value, occupancy distribution,
inject/skip actuation, resyncs, reconcile-error stats, correction budget
spend, jump-delivery ledger, and the estimate-source mix. The design review
§4 acceptance criteria read straight off this:

  - fallback rare-event, not steady-state  -> fb rate (per ack tick)
  - loopback lead converges to ~2 ticks    -> converged lead at 0 ms
  - zero eaten jumps                       -> jump_lost == 0
  - no continuous elastic rubberbanding    -> corr snaps ~0 and absorbed
                                              meters not climbing steadily

Usage: python3 ops/analyze-s5-input-loop.py [path-to-csv]
       (default Logs/remote-presentation-ab.csv)
"""

import statistics
import sys
from pathlib import Path


def load_sessions(path):
    sessions = []
    header = None
    for raw in path.read_text().splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith("# session"):
            sessions.append({"stamp": line[2:], "header": None, "rows": []})
            header = None
            continue
        if header is None:
            header = line.split(",")
            if sessions:
                sessions[-1]["header"] = header
            continue
        if sessions:
            sessions[-1]["rows"].append(dict(zip(header, line.split(","))))
    return sessions


def summarize(session):
    print(f"== {session['stamp']} ({len(session['rows'])} rows)")
    header = session["header"] or []
    if "s5_lead" not in header:
        print("   pre-S5 header (no closed-loop columns) — skipped\n")
        return
    rows = [r for r in session["rows"] if r.get("s5_acks") not in (None, "", "0")]
    if not rows:
        print("   no local-player samples\n")
        return

    first, last = rows[0], rows[-1]
    acks = int(last["s5_acks"]) - int(first["s5_acks"])
    fallbacks = int(last["s5_fb_acks"]) - int(first["s5_fb_acks"])
    leads = [int(r["s5_lead"]) for r in rows]
    occupancy = [int(r["s5_occ"]) for r in rows]
    converged = statistics.mode(leads[-min(30, len(leads)):])
    rec_errs = [float(r["s5_rec_err_last"]) for r in rows]
    precise = sum(1 for r in rows if r["s5_est_precise"] == "1")

    def delta(col):
        return int(last[col]) - int(first[col])

    fb_rate = fallbacks / acks if acks > 0 else 0.0
    print(f"   window: {acks} ack ticks over {len(rows)} s of samples")
    print(f"   fallback acks: {fallbacks}  (rate {fb_rate:.2%} per tick)")
    print(
        f"   lead: first {leads[0]}, converged {converged}, "
        f"min {min(leads)}, max {max(leads)}  "
        f"(raises {delta('s5_raises')}, lowers {delta('s5_lowers')})"
    )
    print(
        f"   occupancy: median {statistics.median(occupancy):.0f}, "
        f"min {min(occupancy)}, max {max(occupancy)}"
    )
    print(
        f"   actuation: injected {delta('s5_injected')}, "
        f"skipped {delta('s5_skipped')}, resyncs {delta('s5_resyncs')}"
    )
    print(
        f"   reconcile err: mean {statistics.fmean(rec_errs):.3f} m, "
        f"max sample {max(rec_errs):.3f} m, session max {last['s5_rec_err_max']} m"
    )
    print(
        f"   correction budget: snaps {delta('s5_corr_snaps')}, "
        f"absorbed {float(last['s5_corr_absorbed_m']) - float(first['s5_corr_absorbed_m']):.2f} m"
    )
    print(
        f"   jumps: predicted {delta('s5_jump_pred')}, "
        f"confirmed {delta('s5_jump_conf')}, LOST {delta('s5_jump_lost')}"
    )
    print(f"   estimate source: precise {precise}/{len(rows)} samples\n")


def main():
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Logs/remote-presentation-ab.csv")
    if not path.exists():
        print(f"no CSV at {path}")
        return 1
    for session in load_sessions(path):
        summarize(session)
    return 0


if __name__ == "__main__":
    sys.exit(main())

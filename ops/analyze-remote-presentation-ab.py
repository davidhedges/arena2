#!/usr/bin/env python3
"""Score F4 A/B legs from Logs/remote-presentation-ab.csv (design review S1).

RemotePresentationAbLog appends one row per second, tagged with the active
timeline (ON = server-time, OFF = arrival). Counters are cumulative within a
session, so each contiguous timeline leg is scored on deltas (last row minus
the row just before the leg started). The comparison metric is the S1 late
ratio: (extrap + starved) / (interp + extrap + starved) — settled samples
(entity authoritatively at rest) are excluded so target idleness cannot
confound the legs.

Usage:
  python3 ops/analyze-remote-presentation-ab.py            # latest session
  python3 ops/analyze-remote-presentation-ab.py --all      # every session
  python3 ops/analyze-remote-presentation-ab.py --csv PATH
"""
import argparse
import os
import sys

INT_COLS = ['p_hard_snaps', 'p_interp', 'p_extrap', 'p_starved', 'p_settled',
            'n_hard_snaps', 'n_interp', 'n_extrap', 'n_starved', 'n_settled']


def parse_sessions(path):
    sessions = []
    header = None
    with open(path) as f:
        for line in f:
            line = line.rstrip('\n')
            if line.startswith('# session'):
                sessions.append({'stamp': line[2:], 'header': None, 'rows': []})
                header = None
                continue
            if not line.strip():
                continue
            if header is None:
                header = line.split(',')
                if sessions:
                    sessions[-1]['header'] = header
                continue
            if sessions:
                sessions[-1]['rows'].append(dict(zip(header, line.split(','))))
    return sessions


def score_session(session):
    print(f"== {session['stamp']} ({len(session['rows'])} rows)")
    if not session['rows']:
        print("   (empty)\n")
        return
    if 'n_starved' not in session['header']:
        print("   pre-S1 header (no starved/settled columns) — not scoreable with the idle-aware metric\n")
        return

    legs = []
    for row in session['rows']:
        if not legs or legs[-1]['tag'] != row['timeline']:
            legs.append({'tag': row['timeline'], 'rows': []})
        legs[-1]['rows'].append(row)

    if len(legs) < 2:
        print(f"   single leg ({legs[0]['tag']}) — NOT an A/B. If a toggle was intended: the semicolon"
              "\n   toggle only registers while the overlay is visible (backslash first).")

    for i, leg in enumerate(legs):
        rows = leg['rows']
        base = legs[i - 1]['rows'][-1] if i > 0 else rows[0]
        last = rows[-1]
        duration = (int(last['unix_ms']) - int(base['unix_ms'])) / 1000.0
        delta = {c: int(last[c]) - int(base[c]) for c in INT_COLS}
        non_settled = delta['n_interp'] + delta['n_extrap'] + delta['n_starved']
        late = delta['n_extrap'] + delta['n_starved']
        late_ratio = late / non_settled if non_settled else float('nan')
        last_errs = [float(r['n_last_err_m']) for r in rows]
        depths = [float(r['n_depth_ticks_avg']) for r in rows]
        print(f"   leg {i}: {leg['tag']:<3} {duration:4.0f}s  npc late ratio {late_ratio:6.1%}  "
              f"interp={delta['n_interp']} extrap={delta['n_extrap']} starved={delta['n_starved']} "
              f"settled={delta['n_settled']} hard_snaps={delta['n_hard_snaps']}")
        print(f"          n_last_err mean={sum(last_errs) / len(last_errs):.3f} "
              f"p95={sorted(last_errs)[int(0.95 * (len(last_errs) - 1))]:.3f}  "
              f"depth min={min(depths):.1f} last={depths[-1]:.1f}")
        p_non = delta['p_interp'] + delta['p_extrap'] + delta['p_starved']
        if p_non + delta['p_settled'] > 0:
            p_ratio = (delta['p_extrap'] + delta['p_starved']) / p_non if p_non else float('nan')
            print(f"          players late ratio {p_ratio:6.1%}  interp={delta['p_interp']} "
                  f"extrap={delta['p_extrap']} starved={delta['p_starved']} settled={delta['p_settled']} "
                  f"hard_snaps={delta['p_hard_snaps']}")
    print()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--csv', default=os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        'Logs', 'remote-presentation-ab.csv'))
    parser.add_argument('--all', action='store_true', help='score every session block, not just the latest')
    args = parser.parse_args()

    sessions = parse_sessions(args.csv)
    if not sessions:
        sys.exit(f"no session blocks found in {args.csv}")

    for session in (sessions if args.all else sessions[-1:]):
        score_session(session)


if __name__ == '__main__':
    main()

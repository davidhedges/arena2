#!/usr/bin/env python3
"""Score F4 A/B legs from Logs/remote-presentation-ab.csv (design review S1/S7).

RemotePresentationAbLog appends one row per second, tagged with the active
timeline (ON = server-time, OFF = arrival). Counters are cumulative within a
session, so each contiguous timeline leg is scored on deltas (last row minus
the row just before the leg started). The comparison metric is the S1 late
ratio: (extrap + starved) / (interp + extrap + starved) — settled samples
(entity authoritatively at rest) are excluded so target idleness cannot
confound the legs.

S7 gate (adaptive delay budget, rerun-2 spec): pooled over the scored legs
(leg 0 is discarded as warmup when the session has >= 5 legs), adaptive-ON
must WIN the NPC late ratio against arrival-OFF and keep the err-p95 win,
with the average paid budget reported. A tie on late ratio at a higher paid
budget is a loss for ON.

Usage:
  python3 ops/analyze-remote-presentation-ab.py            # latest session
  python3 ops/analyze-remote-presentation-ab.py --all      # every session
  python3 ops/analyze-remote-presentation-ab.py --csv PATH
"""
import argparse
import math
import os
import sys

INT_COLS = ['p_hard_snaps', 'p_interp', 'p_extrap', 'p_starved', 'p_settled',
            'n_hard_snaps', 'n_interp', 'n_extrap', 'n_starved', 'n_settled']

MIN_SCORED_LEG_SECONDS = 75.0
MAX_SETTLED_SHARE = 0.40


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


def percentile(values, q):
    if not values:
        return float('nan')
    return sorted(values)[int(q * (len(values) - 1))]


def summarize_leg(index, leg, base):
    rows = leg['rows']
    last = rows[-1]
    duration = (int(last['unix_ms']) - int(base['unix_ms'])) / 1000.0
    delta = {c: int(last[c]) - int(base[c]) for c in INT_COLS}
    non_settled = delta['n_interp'] + delta['n_extrap'] + delta['n_starved']
    late = delta['n_extrap'] + delta['n_starved']
    all_npc = non_settled + delta['n_settled']
    errs = [float(r['n_last_err_m']) for r in rows]
    depths = [float(r['n_depth_ticks_avg']) for r in rows]
    paid = [float(r['n_delay_ms_avg']) for r in rows]
    budgets = [float(r['s7_budget_ms']) for r in rows if 's7_budget_ms' in r]
    p95s = [float(r['s7_late_p95_ms']) for r in rows if 's7_late_p95_ms' in r]
    on_tl = [int(r['s7_npcs_on_tl']) for r in rows if 's7_npcs_on_tl' in r]
    return {
        'index': index,
        'tag': leg['tag'],
        'duration': duration,
        'delta': delta,
        'non_settled': non_settled,
        'late': late,
        'late_ratio': late / non_settled if non_settled else float('nan'),
        'settled_share': delta['n_settled'] / all_npc if all_npc else float('nan'),
        'errs': errs,
        'depths': depths,
        'paid_avg': sum(paid) / len(paid) if paid else float('nan'),
        'budget_avg': sum(budgets) / len(budgets) if budgets else None,
        'late_p95_avg': sum(p95s) / len(p95s) if p95s else None,
        'on_tl_avg': sum(on_tl) / len(on_tl) if on_tl else None,
    }


def print_leg(s):
    d = s['delta']
    print(f"   leg {s['index']}: {s['tag']:<3} {s['duration']:4.0f}s  npc late ratio {s['late_ratio']:6.1%}  "
          f"interp={d['n_interp']} extrap={d['n_extrap']} starved={d['n_starved']} "
          f"settled={d['n_settled']} ({s['settled_share']:.1%}) hard_snaps={d['n_hard_snaps']}")
    budget = (f"  s7 budget avg={s['budget_avg']:.0f} ms (late p95 {s['late_p95_avg']:.0f} ms)"
              if s['budget_avg'] is not None else "")
    if s['on_tl_avg'] is not None:
        budget += f"  on_tl avg={s['on_tl_avg']:.1f}"
    print(f"          n_last_err mean={sum(s['errs']) / len(s['errs']):.3f} "
          f"p95={percentile(s['errs'], 0.95):.3f}  "
          f"depth min={min(s['depths']):.1f} last={s['depths'][-1]:.1f}  "
          f"paid delay avg={s['paid_avg']:.0f} ms{budget}")
    p_non = d['p_interp'] + d['p_extrap'] + d['p_starved']
    if p_non + d['p_settled'] > 0:
        p_ratio = (d['p_extrap'] + d['p_starved']) / p_non if p_non else float('nan')
        print(f"          players late ratio {p_ratio:6.1%}  interp={d['p_interp']} "
              f"extrap={d['p_extrap']} starved={d['p_starved']} settled={d['p_settled']} "
              f"hard_snaps={d['p_hard_snaps']}")


def pool(legs):
    late = sum(l['late'] for l in legs)
    non = sum(l['non_settled'] for l in legs)
    errs = [e for l in legs for e in l['errs']]
    paid = [l['paid_avg'] for l in legs if not math.isnan(l['paid_avg'])]
    snaps = sum(l['delta']['n_hard_snaps'] for l in legs)
    return {
        'late': late,
        'non': non,
        'ratio': late / non if non else float('nan'),
        'err_p95': percentile(errs, 0.95),
        'paid_avg': sum(paid) / len(paid) if paid else float('nan'),
        'hard_snaps': snaps,
    }


def two_proportion_z(a_hits, a_n, b_hits, b_n):
    if a_n <= 0 or b_n <= 0:
        return float('nan')
    p = (a_hits + b_hits) / (a_n + b_n)
    se = math.sqrt(p * (1 - p) * (1 / a_n + 1 / b_n))
    return (a_hits / a_n - b_hits / b_n) / se if se else float('nan')


def print_gate(summaries, marker_based=False):
    print("   -- S7 gate (rerun-2 spec) --")
    scored = []
    for s in summaries:
        # A leg whose counters went backwards saw an entity/session reset
        # mid-leg (play-mode teardown, reconnect, probe/kobold despawn) —
        # its deltas are meaningless.
        if any(v < 0 for v in s['delta'].values()):
            print(f"   leg {s['index']} ({s['tag']}, {s['duration']:.0f}s) discarded — counter reset mid-leg")
            continue
        scored.append(s)

    if marker_based:
        # Marker legs are exactly the driver's legs 0..4; leg 0 is the
        # designated warmup.
        if scored and scored[0]['index'] == 0:
            s = scored.pop(0)
            print(f"   warmup leg {s['index']} ({s['tag']}, {s['duration']:.0f}s) discarded by design")
    else:
        # Heuristic discards for pre-marker CSVs.
        while scored and scored[0]['duration'] < 45.0:
            s = scored.pop(0)
            print(f"   leg {s['index']} ({s['tag']}, {s['duration']:.0f}s) discarded — pre-run stub")

        if len(scored) >= 5:
            s = scored.pop(0)
            print(f"   warmup leg {s['index']} ({s['tag']}, {s['duration']:.0f}s) discarded")

        while (len(scored) >= 5 and scored[-1]['duration'] < MIN_SCORED_LEG_SECONDS
               and sum(1 for s in scored[:-1] if s['tag'] == 'ON') >= 2
               and sum(1 for s in scored[:-1] if s['tag'] == 'OFF') >= 2):
            s = scored.pop()
            print(f"   post-run tail leg {s['index']} ({s['tag']}, {s['duration']:.0f}s) discarded")

    on_legs = [s for s in scored if s['tag'] == 'ON']
    off_legs = [s for s in scored if s['tag'] == 'OFF']
    if not on_legs or not off_legs:
        print("   S7 gate: not evaluable — need both ON and OFF legs\n")
        return

    protocol_ok = True
    for s in scored:
        problems = []
        if s['duration'] < MIN_SCORED_LEG_SECONDS:
            problems.append(f"short ({s['duration']:.0f}s < {MIN_SCORED_LEG_SECONDS:.0f}s)")
        if not (s['settled_share'] < MAX_SETTLED_SHARE):
            problems.append(f"settled {s['settled_share']:.0%} >= {MAX_SETTLED_SHARE:.0%}")
        if problems:
            protocol_ok = False
            print(f"   [PROTOCOL] leg {s['index']} ({s['tag']}): " + ", ".join(problems))
    tags = [s['tag'] for s in scored]
    if any(tags[i] == tags[i + 1] for i in range(len(tags) - 1)):
        protocol_ok = False
        print(f"   [PROTOCOL] legs not interleaved: {'/'.join(tags)}")
    if len(on_legs) < 2 or len(off_legs) < 2:
        protocol_ok = False
        print(f"   [PROTOCOL] need >=2 legs per arm (have {len(on_legs)} ON / {len(off_legs)} OFF)")
    if protocol_ok:
        print(f"   protocol: OK ({len(on_legs)} ON / {len(off_legs)} OFF legs, "
              f"all >= {MIN_SCORED_LEG_SECONDS:.0f}s, settled < {MAX_SETTLED_SHARE:.0%}, interleaved)")

    on = pool(on_legs)
    off = pool(off_legs)
    z = two_proportion_z(on['late'], on['non'], off['late'], off['non'])
    print(f"   pooled ON : late {on['ratio']:6.1%} ({on['late']}/{on['non']})  err p95 {on['err_p95']:.3f} m  "
          f"paid delay avg {on['paid_avg']:.0f} ms  hard snaps {on['hard_snaps']}")
    print(f"   pooled OFF: late {off['ratio']:6.1%} ({off['late']}/{off['non']})  err p95 {off['err_p95']:.3f} m  "
          f"paid delay avg {off['paid_avg']:.0f} ms  hard snaps {off['hard_snaps']}")
    print(f"   late-ratio z (nominal, frames-as-independent): {z:+.1f}")

    late_win = on['ratio'] < off['ratio']
    err_win = on['err_p95'] <= off['err_p95']
    if late_win and err_win:
        verdict, why = "PASS", (f"ON wins late ratio ({on['ratio']:.1%} < {off['ratio']:.1%}) "
                                f"and keeps the err-p95 win ({on['err_p95']:.3f} <= {off['err_p95']:.3f})")
    elif not late_win:
        verdict, why = "FAIL", (f"ON does not win late ratio ({on['ratio']:.1%} vs {off['ratio']:.1%}) "
                                f"while paying {on['paid_avg']:.0f} ms vs {off['paid_avg']:.0f} ms — "
                                "a tie is a loss for ON")
    else:
        verdict, why = "FAIL", (f"ON loses err p95 ({on['err_p95']:.3f} > {off['err_p95']:.3f})")
    if not protocol_ok:
        verdict += " (PROTOCOL VIOLATIONS — rerun before acting on this)"
    print(f"   S7 GATE: {verdict} — {why}\n")


def score_session(session):
    print(f"== {session['stamp']} ({len(session['rows'])} rows)")
    if not session['rows']:
        print("   (empty)\n")
        return
    if 'n_starved' not in session['header']:
        print("   pre-S1 header (no starved/settled columns) — not scoreable with the idle-aware metric\n")
        return

    # Leg identification. Preferred: the s7_run_leg marker written by the
    # scripted driver (-1 outside a run) — pre-run idle and post-run tails
    # can never masquerade as legs. Fallback for older CSVs: contiguous
    # timeline-tag runs plus discard heuristics.
    has_marker = 's7_run_leg' in session['header']
    legs = []
    if has_marker:
        for row_index, row in enumerate(session['rows']):
            marker = int(row['s7_run_leg'])
            if marker < 0:
                continue
            if not legs or legs[-1]['marker'] != marker:
                legs.append({'tag': row['timeline'], 'marker': marker,
                             'first_index': row_index, 'rows': []})
            legs[-1]['rows'].append(row)
    else:
        for row in session['rows']:
            if not legs or legs[-1]['tag'] != row['timeline']:
                legs.append({'tag': row['timeline'], 'rows': []})
            legs[-1]['rows'].append(row)

    if len(legs) < 2:
        print(f"   single leg ({legs[0]['tag']}) — NOT an A/B. If a toggle was intended: the semicolon"
              "\n   toggle only registers while the overlay is visible (backslash first);"
              "\n   period starts the scripted warmup+ON/OFF/ON/OFF run.")

    summaries = []
    for i, leg in enumerate(legs):
        if has_marker:
            # Delta base: the session row just before this leg's first row
            # (counters are cumulative, and pre-leg idle rows carry them).
            first = leg['first_index']
            base = session['rows'][first - 1] if first > 0 else leg['rows'][0]
            index = leg['marker']
        else:
            base = legs[i - 1]['rows'][-1] if i > 0 else leg['rows'][0]
            index = i
        s = summarize_leg(index, leg, base)
        summaries.append(s)
        print_leg(s)

    if len(legs) >= 2:
        print_gate(summaries, marker_based=has_marker)
    else:
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

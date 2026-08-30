#!/usr/bin/env python3
"""S8 live probe: bounded attacker-view rewind + favor-the-defender grace.

Two headless websocket players and NPCs on a throwaway DB
(docs/lag-compensation-design-2026-07-04.md §4):

  sanity   — config row defaults (enabled, 250 ms cap); an honest small
             view report on a stationary in-reach dummy stays Accepted in
             both switch states (no-op at loopback).
  history  — the chased kobold's combat_position_history ring holds ≤ 16
             rows with fresh stamps and slot reuse.
  flip     — attacker A parks 4 m off a shuttle line the runner T tows a
             kobold along; A presses WARRIOR_CHARGE (minimum range 5 m) at
             the moment the kobold's PRESENT distance is inside the minimum
             ring while its pose 250 ms ago (what a ~250 ms-delayed view
             renders) was outside it. Switch OFF: every such press must
             reject OutOfRange. Switch ON: the same geometry must Accept —
             the rewound pose decides. First accepted dash also proves the
             rewind-barrier stamp (combat_rewind_barrier row for A).
  defense  — runner B parks parry-cycling in front of three harmless
             warriors. The gate is deterministic: after the first successful
             parry, the live defense_state row must show the widened window
             (recovery_until − active_until = 10000 − 150 = 9850 ms; the old
             50 ms grace read 9950 ms). Coincidence pairs in the 50–150 ms
             post-parry band are reported as corroboration when they occur.

Two legs, two measurement builds (compile-time flags in server/src/npcs.rs).
The rewind legs need an NPC that never stops chasing (swing cadence freezes
would let the runner escape and hand nearest-wins aggro to the parked
attacker — the S7 lesson); the defense leg needs real swings with zero
damage:

  # rewind + sanity + history + barrier
  ARENA_DATABASE=s8probe ARENA_PROJECTILE_LOAD_HARNESS=1 \
      ARENA_NPC_NO_ATTACK=1 ARENA_NPC_AGGRO_RADIUS=100 \
      ARENA_GENERATE_BINDINGS=0 ARENA_VERIFY_DOTNET=0 \
      ./ops/republish-local-clear.sh
  python3 ops/s8-lag-comp-probe.py --database s8probe --leg rewind

  # defense grace
  ARENA_DATABASE=s8probe ARENA_PROJECTILE_LOAD_HARNESS=1 \
      ARENA_NPC_HARMLESS=1 ARENA_NPC_AGGRO_RADIUS=100 \
      ARENA_GENERATE_BINDINGS=0 ARENA_VERIFY_DOTNET=0 \
      ./ops/republish-local-clear.sh
  python3 ops/s8-lag-comp-probe.py --database s8probe --leg defense
  spacetime delete s8probe

Requires `pip install websocket-client` (scratch venv is fine).
"""

import argparse
import collections
import json
import math
import subprocess
import sys
import threading
import time

import websocket

from combat_build_probe_support import configure_probe_combat_build

SANITY_ABILITY = "PALADIN_SACRED_THRUST"  # retained targeted Technique, range 5.0
SANITY_STRIKE = "SWORD_AND_SHIELD_ALT_LIGHT_3"
CHARGE_ABILITY = "WARRIOR_CHARGE"    # LINEAR gap-close, range 5..18
KOBOLD_TEMPLATE = "KOBOLD_WARRIOR_RD_SWORD_SHIELD"
KOBOLD_VISUAL = "KOBOLD_WARRIOR_RD"

VIEW_DELAY_MS = 250                  # claim the cap; the server clamps here anyway
FLIP_MARGIN = 0.4                    # metres either side of the minimum ring
FLIP_PRESSES_PER_LEG = 4
DEFENSE_LEG_SECONDS = 240.0


class Probe:
    def __init__(self, database, host, name):
        self.database = database
        self.host = host
        self.name = name
        self.request_id = 0
        self.action_seq = 0
        url = f"ws://{host}/v1/database/{database}/subscribe"
        self.ws = websocket.create_connection(
            url, subprotocols=["v1.json.spacetimedb"], timeout=5
        )
        self.identity = None
        first = json.loads(self.ws.recv())
        token = first.get("IdentityToken")
        if token:
            identity = token.get("identity")
            if isinstance(identity, dict):
                identity = identity.get("__identity__", "")
            self.identity = (identity or "").removeprefix("0x").lower()
        # Drain server frames forever — recv must stay live so the library
        # answers server pings, or the server drops the connection (and
        # disconnect cleanup deletes the probe player and its NPCs).
        self.ws.settimeout(None)
        self.recent = collections.deque(maxlen=40)
        self._drain = threading.Thread(target=self._drain_loop, daemon=True)
        self._drain.start()

    def _drain_loop(self):
        try:
            while True:
                message = self.ws.recv()
                if "TransactionUpdate" in message:
                    try:
                        update = json.loads(message)["TransactionUpdate"]
                        status = update.get("status", {})
                        name = update.get("reducer_call", {}).get("reducer_name", "?")
                        if "Failed" in status:
                            self.recent.append(f"{name}: FAILED {status['Failed']}")
                        else:
                            self.recent.append(f"{name}: {next(iter(status), '?')}")
                    except Exception:
                        self.recent.append(message[:200])
        except Exception as e:
            self.recent.append(f"drain died: {type(e).__name__}: {e}")

    def dump_recent(self):
        for entry in self.recent:
            print(f"    [{self.name}] reducer: {entry}")

    def call(self, reducer, args):
        self.request_id += 1
        self.ws.send(
            json.dumps(
                {
                    "CallReducer": {
                        "reducer": reducer,
                        "args": json.dumps(args),
                        "request_id": self.request_id,
                        "flags": 0,
                    }
                }
            )
        )

    def sql(self, query):
        cmd = ["spacetime", "sql", self.database, query]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode != 0:
            sys.stderr.write(result.stderr)
            raise RuntimeError(f"spacetime sql failed: {query}")
        rows = []
        for line in result.stdout.splitlines():
            if "|" not in line or set(line.strip()) <= {"-", "+", "|", " "}:
                continue
            rows.append([cell.strip().strip('"') for cell in line.split("|")])
        return rows[1:] if rows else []

    def physics(self):
        rows = self.sql(
            "SELECT identity, pos_x, pos_y, pos_z, yaw, last_processed_tick FROM player_physics"
        )
        for identity, x, y, z, yaw, tick in rows:
            if identity.removeprefix("0x").lower() == self.identity:
                return float(x), float(y), float(z), float(yaw), int(tick)
        raise RuntimeError(f"no player_physics row for probe {self.name}")

    def send_intent(self, forward, yaw, tick, strafe=0.0):
        self.call("send_movement_intent", [forward, strafe, yaw, False, tick])

    def face(self, tx, tz):
        x, _, z, _, tick = self.physics()
        yaw = math.atan2(tx - x, tz - z)
        self.send_intent(0.0, yaw, tick + 2)
        time.sleep(0.4)
        return yaw

    def move_to(self, tx, tz, tolerance=1.0, timeout=60.0):
        deadline = time.time() + timeout
        last_pos, last_progress_at = None, time.time()
        while time.time() < deadline:
            x, _, z, _, tick = self.physics()
            dx, dz = tx - x, tz - z
            dist = math.hypot(dx, dz)
            if dist <= tolerance:
                self.send_intent(0.0, math.atan2(dx, dz), tick + 2)
                time.sleep(0.4)
                return
            if last_pos is not None and math.hypot(x - last_pos[0], z - last_pos[1]) > 0.15:
                last_progress_at = time.time()
            if time.time() - last_progress_at > 4.0:
                self.send_intent(0.0, 0.0, tick + 2)
                raise RuntimeError(
                    f"[{self.name}] move_to({tx:.1f},{tz:.1f}) stuck at ({x:.1f},{z:.1f})"
                )
            last_pos = (x, z)
            forward = max(0.2, min(1.0, dist / 5.0))
            self.send_intent(forward, math.atan2(dx, dz), tick + 2)
            time.sleep(0.12 if dist < 4.0 else 0.3)
        raise RuntimeError(f"[{self.name}] move_to({tx:.1f},{tz:.1f}) timed out")

    def spawn_hostile_dummy(self):
        self.call("spawn_playground_target", ["HOSTILE"])
        time.sleep(1.0)
        rows = self.sql("SELECT kind, identity FROM playground_target")
        hostile = [r for r in rows if r[0] == "HOSTILE"]
        if not hostile:
            self.dump_recent()
            raise RuntimeError("hostile playground target did not spawn")
        return hostile[-1][1].removeprefix("0x")

    def press_melee(self, strike_id, target_hex, token, view_server_time_ms):
        x, y, z, yaw, _ = self.physics()
        return self.press_melee_at(
            strike_id, target_hex, token, view_server_time_ms, x, y, z, yaw
        )

    def press_melee_at(self, strike_id, target_hex, token, view_server_time_ms, x, y, z, yaw):
        """Press with a cached pose — no SQL between deciding and sending, so
        the server's arrival-anchored rewind clamp lands on the moment the
        claim was computed for (SQL in the press path cost ~200-300 ms and
        made the rewound pose evaluate a third of a second late)."""
        self.action_seq += 1
        self.call(
            "melee_attack",
            [strike_id, target_hex, x, y, z, yaw, token, self.action_seq, view_server_time_ms],
        )
        time.sleep(0.8)
        return self.prediction_result(token)

    def press_parry(self, token):
        x, y, z, yaw, tick = self.physics()
        self.action_seq += 1
        self.call("start_parry", [tick, x, y, z, yaw, token, self.action_seq])

    def prediction_result(self, token):
        rows = self.sql(
            "SELECT predicted_action_id, result, reject_reason FROM predicted_action_result"
        )
        for row_token, result, reason in rows:
            if row_token == token:
                return enum_tag(result), enum_tag(reason)
        return None, None

    def activate_discipline(self, combat_discipline_id):
        self.call("activate_combat_build_discipline", [combat_discipline_id])
        deadline = time.time() + 8.0
        while time.time() < deadline:
            rows = self.sql(
                "SELECT owner, combat_discipline_id "
                "FROM active_combat_build_discipline"
            )
            if any(
                owner.removeprefix("0x").lower() == self.identity
                and discipline == combat_discipline_id
                for owner, discipline in rows
            ):
                return
            time.sleep(0.05)
        raise RuntimeError(f"timed out activating {combat_discipline_id}")


def enum_tag(cell):
    cell = cell.strip()
    if cell.startswith("(") and "=" in cell:
        return cell[1:].split("=", 1)[0].strip()
    return cell


def tag_is(value, expected):
    return (value or "").lower() == expected.lower()


def expect(label, ok, detail, failures):
    print(f"  [{'PASS' if ok else 'FAIL'}] {label}: {detail}")
    if not ok:
        failures.append(label)


def spawn_kobold(probe, template):
    probe.call("spawn_npc", [template, KOBOLD_VISUAL, "HOSTILE"])
    time.sleep(1.2)
    rows = probe.sql("SELECT identity, template_id FROM npc_instance")
    kobolds = [r[0].removeprefix("0x").lower() for r in rows if r[1] == template]
    if not kobolds:
        probe.dump_recent()
        raise RuntimeError(f"NPC did not spawn (npc_instance rows: {rows})")
    kobold = kobolds[-1]
    probe.call("set_npc_target_override", [kobold, probe.identity])
    return kobold


def npc_position(probe, npc_hex):
    rows = probe.sql("SELECT identity, pos_x, pos_z FROM npc_physics")
    for identity, px, pz in rows:
        if identity.removeprefix("0x").lower() == npc_hex:
            return float(px), float(pz)
    raise RuntimeError("NPC has no npc_physics row")


def npc_hit_radius(probe, npc_hex):
    rows = probe.sql("SELECT identity, hit_radius FROM npc_state")
    for identity, radius in rows:
        if identity.removeprefix("0x").lower() == npc_hex:
            return float(radius)
    raise RuntimeError("NPC has no npc_state row")


def server_now_ms(probe, npc_hex):
    """Newest history stamp for a continuously-moving entity ≈ server now."""
    t_ms, _, _ = newest_history_sample(probe, npc_hex)
    return t_ms


def newest_history_sample(probe, npc_hex):
    """(server_time_ms, x, z) of the entity's newest history sample — one
    query yields both the pose and the server-timeline stamp, which is what
    a real client's render state is made of."""
    rows = probe.sql(
        "SELECT identity, stamped_at_micros, pos_x, pos_z FROM combat_position_history"
    )
    best = None
    for identity, stamp, px, pz in rows:
        if identity.removeprefix("0x").lower() != npc_hex:
            continue
        stamp = int(stamp)
        if best is None or stamp > best[0]:
            best = (stamp, float(px), float(pz))
    if best is None:
        raise RuntimeError("no combat_position_history rows for the kobold")
    return best[0] // 1_000, best[1], best[2]


def set_lag_comp(probe, enabled):
    # 4th arg (S10 sweep_rewind_enabled) off — S8 probe exercises melee only.
    probe.call("set_lag_comp_config", [enabled, 250, False, False])
    time.sleep(0.6)
    rows = probe.sql("SELECT config_id, enabled, max_rewind_ms FROM combat_lag_comp_config")
    if not rows or rows[0][1].lower() != str(enabled).lower():
        raise RuntimeError(f"lag comp config did not apply (rows: {rows})")


class RunnerShuttle(threading.Thread):
    """Runs probe T back and forth along a +X line so the chasing kobold
    sweeps repeatedly through the attacker's minimum-range ring. When an
    accepted dash lands the attacker on the kobold (stealing nearest-wins
    aggro — and with NO_ATTACK the kobold then shadows the stationary
    attacker forever at equal MOVE_SPEED), `rescue()` steers the runner to
    the kobold until the runner is nearest again, then resumes the line."""

    def __init__(self, probe, x0, z0, x1, z1):
        super().__init__(daemon=True)
        self.probe = probe
        self.ends = [(x0, z0), (x1, z1)]
        self.stop_flag = threading.Event()
        self.rescue_kobold = None  # kobold hex while a rescue is active

    def rescue(self, kobold_hex):
        self.rescue_kobold = kobold_hex

    def run(self):
        index = 0
        while not self.stop_flag.is_set():
            kobold_hex = self.rescue_kobold
            try:
                x, _, z, _, tick = self.probe.physics()
                if kobold_hex is not None:
                    try:
                        kx, kz = npc_position(self.probe, kobold_hex)
                    except RuntimeError:
                        self.rescue_kobold = None
                        continue
                    if math.hypot(kx - x, kz - z) <= 1.5:
                        self.rescue_kobold = None
                        continue
                    self.probe.send_intent(1.0, math.atan2(kx - x, kz - z), tick + 2)
                    time.sleep(0.25)
                    continue
                tx, tz = self.ends[index]
                dx, dz = tx - x, tz - z
                if math.hypot(dx, dz) <= 1.2:
                    index = 1 - index
                    continue
                self.probe.send_intent(1.0, math.atan2(dx, dz), tick + 2)
            except Exception:
                pass
            time.sleep(0.25)


def flip_leg(attacker, runner, shuttle, kobold_hex, boundary, post, presses_wanted, view_delay_ms, timeout):
    """Watch the kobold cross INWARD through the minimum-range ring and press
    at moments where the present pose is inside it but the pose view_delay_ms
    ago was outside — the attacker-view flip geometry. Returns press results.

    An accepted charge damages the kobold (and the press arms an auto-attack,
    which is cleared immediately) — if the kobold dies mid-leg, respawn it
    beside the runner so aggro re-latches the shuttle immediately.

    Timing model: the kobold is tracked through its own history ring (pose +
    server-time stamp in one query, exactly the data a rendering client
    would hold), the view claim anchors to the stamp of the "what I saw"
    sample, and the press path has no SQL — so the server's arrival-anchored
    250 ms clamp rewinds to within a few ms of the claimed moment."""
    history = collections.deque(maxlen=64)  # (server_time_ms, x, z)
    results = []
    deadline = time.time() + timeout
    last_press_at = 0.0
    missing_since = None
    # The attacker never moves during the leg: cache its pose once and face
    # the crossing sector so the 180° facing arc covers every press.
    attacker.face(post[0], post[1] + 4.0)
    ax, ay, az, ayaw, _ = attacker.physics()
    while len(results) < presses_wanted and time.time() < deadline:
        try:
            t_ms, kx, kz = newest_history_sample(attacker, kobold_hex)
            missing_since = None
        except RuntimeError:
            if missing_since is None:
                missing_since = time.time()
            elif time.time() - missing_since > 3.0:
                print("    (kobold died — respawning beside the runner)")
                kobold_hex = spawn_kobold(runner, KOBOLD_TEMPLATE)
                history.clear()
                missing_since = None
            time.sleep(0.2)
            continue
        now = time.time()
        if not history or t_ms > history[-1][0]:
            history.append((t_ms, kx, kz))
        # Distances are measured from the attacker's ACTUAL cached pose, not
        # the nominal post — move_to's 0.8 m tolerance is enough to flip a
        # verdict when the geometry margin is ~0.5 m (bit live: an OFF press
        # read present=accept because the server's true distance was outside
        # the ring the probe computed against the post).
        d_now = math.hypot(kx - ax, kz - az)
        d_old = None
        old_t_ms = None
        for t, hx, hz in reversed(history):
            if t <= t_ms - view_delay_ms:
                d_old = math.hypot(hx - ax, hz - az)
                old_t_ms = t
                break
        if (
            d_old is not None
            and shuttle.rescue_kobold is None
            and d_now <= boundary - FLIP_MARGIN
            and d_old >= boundary + FLIP_MARGIN
            and now - last_press_at > 3.0
        ):
            # Claim the exact server-time moment of the d_old sample: the
            # server clamps the rewind to ≤250 ms before ARRIVAL, and with a
            # no-SQL press path arrival is within a few ms of t_ms.
            view_ms = max(t_ms - view_delay_ms, old_t_ms)
            token = f"s8-flip-{len(results)}-{int(now)}"
            result, reason = attacker.press_melee_at(
                CHARGE_ABILITY, kobold_hex, token, max(view_ms, 1), ax, ay, az, ayaw
            )
            last_press_at = time.time()
            skip_reasons = ("oncooldown", "onglobalcooldown", "busy", "combowindow")
            if result is None or (tag_is(result, "rejected") and (reason or "").lower() in skip_reasons):
                print(f"    (skipped press: result={result!r} reason={reason!r})")
                continue
            print(
                f"    press d_now={d_now:.2f} d_old={d_old:.2f} boundary={boundary:.2f} "
                f"-> {result}/{reason}"
            )
            results.append((result, reason, d_now, d_old))
            # An accepted press arms an auto-attack on the kobold (standard
            # melee-press behavior) — clear it before it kills the fixture.
            # The dash also made the attacker the kobold's nearest player;
            # hand aggro back to the runner before walking to the post.
            attacker.call("clear_auto_attack_target", [])
            if tag_is(result, "accepted"):
                shuttle.rescue(kobold_hex)
            attacker.move_to(*post, tolerance=0.8)
            attacker.face(post[0], post[1] + 4.0)
            ax, ay, az, ayaw, _ = attacker.physics()
        elif shuttle.rescue_kobold is not None:
            # Geometry is not meaningful mid-rescue; wait it out.
            time.sleep(0.3)
        time.sleep(0.1)
    return results


def timestamp_cell_ms(cell):
    """CLI sql renders Timestamp cells as micros or ISO depending on build."""
    cell = cell.strip().strip('"')
    try:
        return int(cell) / 1000.0
    except ValueError:
        from datetime import datetime

        return datetime.fromisoformat(cell.replace("Z", "+00:00")).timestamp() * 1000.0


def parry_cooldown_window_ms(probe, defender_hex):
    rows = probe.sql("SELECT owner, kind, active_until, recovery_until FROM defense_state")
    for owner, kind, active_until, recovery_until in rows:
        if owner.removeprefix("0x").lower() != defender_hex:
            continue
        if "COOLDOWN" not in kind.upper():
            return None
        return timestamp_cell_ms(recovery_until) - timestamp_cell_ms(active_until)
    return None


def collect_defense_events(probe, defender_hex, seconds):
    """Poll the pruned combat_event window and accumulate PARRY/IMPACT events
    against the defender, deduped by event id."""
    seen = set()
    events = []
    deadline = time.time() + seconds
    while time.time() < deadline:
        rows = probe.sql(
            "SELECT event_id, event_type, hit, created_at_micros, damage FROM combat_event"
        )
        for event_id, event_type, hit, micros, damage in rows:
            if hit.removeprefix("0x").lower() != defender_hex:
                continue
            if event_id in seen:
                continue
            seen.add(event_id)
            events.append((int(micros), event_type, int(damage)))
        time.sleep(6.0)
    events.sort()
    return events


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="s8probe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    parser.add_argument("--leg", choices=["rewind", "defense", "all"], default="rewind")
    args = parser.parse_args()

    failures = []
    attacker = Probe(args.database, args.host, "attacker")
    runner = Probe(args.database, args.host, "runner")
    print(f"attacker={attacker.identity[:8]} runner={runner.identity[:8]} db={args.database}")
    time.sleep(1.0)

    configure_probe_combat_build(
        attacker,
        [SANITY_ABILITY, CHARGE_ABILITY],
        starting_discipline_id="SWORD_AND_SHIELD",
    )

    # Work inside the S7 lap probe's verified clear disc: Desert_Day, circle
    # center spawn+(3, 30), radius 14 (computed from the authored movement
    # collision; the default playground spawn is wall-blocked nearby, which
    # strands shuttle runs).
    for probe in (attacker, runner):
        probe.call("set_open_world_scene", ["Desert_Day"])
    time.sleep(1.5)
    sx, _, sz, _, _ = attacker.physics()
    clear_cx, clear_cz = sx + 3.0, sz + 30.0
    attacker.move_to(clear_cx, clear_cz - 4.0, tolerance=1.0, timeout=90.0)

    if args.leg == "defense":
        run_defense_leg(attacker, runner, failures)
        finish(failures)
        return

    print("\n== sanity: config defaults + honest report is a no-op at loopback")
    rows = attacker.sql("SELECT config_id, enabled, max_rewind_ms FROM combat_lag_comp_config")
    fresh_default_on = not rows or rows[0][1].lower() == "true"
    expect(
        "config defaults",
        fresh_default_on,
        f"rows={rows} (absent row = shipped default: ON since the 2026-07-04 acceptance)",
        failures,
    )
    set_lag_comp(attacker, False)
    ax, az = clear_cx, clear_cz - 4.0
    dummy = attacker.spawn_hostile_dummy()
    dpos_rows = attacker.sql("SELECT identity, pos_x, pos_z FROM player_physics")
    dpos = next(
        (
            (float(px), float(pz))
            for identity, px, pz, *_ in [r[:3] for r in dpos_rows]
            if identity.removeprefix("0x").lower() == dummy.lower()
        ),
        None,
    )
    attacker.face(*dpos)
    # An honest tiny view claim (dummy is stationary; rewound pose == present).
    wall_ms = int(time.time() * 1000)
    result, reason = attacker.press_melee(SANITY_STRIKE, dummy, "s8-sanity-off", 0)
    expect("sanity press (no report, OFF)", tag_is(result, "accepted"), f"{result}/{reason}", failures)
    set_lag_comp(attacker, True)
    time.sleep(2.0)  # clear the first press's global cooldown
    result, reason = attacker.press_melee(SANITY_STRIKE, dummy, "s8-sanity-on", wall_ms)
    expect(
        "sanity press (stale-claim clamped, ON)",
        tag_is(result, "accepted"),
        f"{result}/{reason} (stationary target: rewound pose == present)",
        failures,
    )
    set_lag_comp(attacker, False)
    # The sanity press armed an auto-attack on the dummy, and a parked dummy
    # (or a live auto target) has no business in the flip geometry.
    attacker.call("clear_auto_attack_target", [])
    attacker.call("despawn_all_playground_targets", [])
    time.sleep(0.6)

    print("\n== flip geometry: runner tows the kobold past the attacker's post")
    attacker.activate_discipline("TWO_HANDED_SWORD")
    # Post sits 4 m off the shuttle line: the continuously-chasing kobold
    # (NO_ATTACK build — no swing freezes) trails the runner at ≤ ~2.5 m, so
    # the runner is always its nearest player and the kobold's distance to
    # the post sweeps through the charge minimum ring every pass. The runner
    # walks out to a shuttle end BEFORE the kobold spawns so initial aggro
    # acquires the runner, not the attacker.
    post = (clear_cx, clear_cz)
    shuttle_a = (clear_cx - 11.0, clear_cz + 4.0)
    shuttle_b = (clear_cx + 11.0, clear_cz + 4.0)
    attacker.move_to(*post, tolerance=0.8)
    runner.move_to(*shuttle_a, tolerance=1.2, timeout=120.0)
    kobold = spawn_kobold(runner, KOBOLD_TEMPLATE)
    radius = npc_hit_radius(attacker, kobold)
    boundary = 5.0 + radius  # charge minimum_range + target hit radius
    print(f"  kobold={kobold[:8]} radius={radius:.2f} minimum ring={boundary:.2f} m")
    shuttle = RunnerShuttle(runner, *shuttle_a, *shuttle_b)
    shuttle.start()
    time.sleep(3.0)

    print("\n== history: ring depth and freshness on the moving kobold")
    rows = attacker.sql("SELECT identity, stamped_at_micros FROM combat_position_history")
    kobold_stamps = [
        int(s) for identity, s in rows if identity.removeprefix("0x").lower() == kobold
    ]
    span_ms = (max(kobold_stamps) - min(kobold_stamps)) / 1000 if kobold_stamps else 0
    expect(
        "history ring",
        0 < len(kobold_stamps) <= 16 and span_ms <= 1200,
        f"{len(kobold_stamps)} samples spanning {span_ms:.0f} ms (≤16 slots, ~528 ms when full)",
        failures,
    )

    print(f"\n== flip leg, switch OFF: present-time verdict must hold")
    off_results = flip_leg(
        attacker, runner, shuttle, kobold, boundary, post, FLIP_PRESSES_PER_LEG, VIEW_DELAY_MS,
        timeout=300,
    )
    off_rejects = [r for r in off_results if tag_is(r[0], "rejected") and tag_is(r[1], "outOfRange")]
    expect(
        "flip presses reject while OFF",
        len(off_results) >= 2 and len(off_rejects) == len(off_results),
        f"{len(off_rejects)}/{len(off_results)} rejected OutOfRange",
        failures,
    )

    print(f"\n== flip leg, switch ON: the attacker-view pose must decide")
    set_lag_comp(attacker, True)
    on_results = flip_leg(
        attacker, runner, shuttle, kobold, boundary, post, FLIP_PRESSES_PER_LEG, VIEW_DELAY_MS,
        timeout=420,
    )
    on_accepts = [r for r in on_results if tag_is(r[0], "accepted")]
    # Crossings are scarce in the ON leg (each accepted dash costs a rescue +
    # walk-back + charge cooldown before the next), so the criterion is
    # "every flip press accepts", not a raw count — the OFF leg already
    # proved the same geometry rejects 4/4 with the switch off.
    expect(
        "flip presses accept while ON",
        len(on_results) >= 1 and len(on_accepts) == len(on_results),
        f"{len(on_accepts)}/{len(on_results)} accepted",
        failures,
    )

    print("\n== barrier: the accepted dash stamped a rewind barrier for the attacker")
    rows = attacker.sql("SELECT identity, barrier_micros FROM combat_rewind_barrier")
    attacker_barriers = [
        int(b) for identity, b in rows if identity.removeprefix("0x").lower() == attacker.identity
    ]
    expect(
        "rewind barrier stamped",
        bool(attacker_barriers) and bool(on_accepts),
        f"barrier rows for attacker: {len(attacker_barriers)} (needs ≥1 accepted dash above)",
        failures,
    )

    shuttle.stop_flag.set()
    set_lag_comp(attacker, False)

    if args.leg == "all":
        run_defense_leg(attacker, runner, failures)

    finish(failures)


def run_defense_leg(attacker, runner, failures):
    print(f"\n== defense grace: two warriors on a parry-cycling defender ({DEFENSE_LEG_SECONDS:.0f} s)")
    # Work inside the S7-verified Desert_Day clear disc (spawn+(3,30), r=14):
    # the runner parks at the center as the defender, the attacker observes
    # from 12 m south (warriors parked on the runner keep it their nearest),
    # two warriors spawn beside the runner (HARMLESS build: real telegraphed
    # swings, zero damage), and start_parry cycles so the armed window is up
    # whenever the 10 s success cooldown allows. Each press faces the
    # warriors' midpoint so both swings land inside the 120° defense arc.
    for probe in (attacker, runner):
        probe.call("set_open_world_scene", ["Desert_Day"])
    time.sleep(1.5)
    sx, _, sz, _, _ = runner.physics()
    cx, cz = sx + 3.0, sz + 30.0
    runner.move_to(cx, cz, tolerance=1.0, timeout=90.0)
    attacker.move_to(cx, cz - 12.0, tolerance=1.5, timeout=90.0)
    warriors = [spawn_kobold(runner, KOBOLD_TEMPLATE) for _ in range(3)]

    grace_window = {"value": None}

    def parry_cycle():
        end = time.time() + DEFENSE_LEG_SECONDS
        index = 0
        while time.time() < end:
            index += 1
            try:
                poses = []
                for warrior in warriors:
                    try:
                        poses.append(npc_position(runner, warrior))
                    except RuntimeError:
                        pass
                x, _, z, _, tick = runner.physics()
                if poses:
                    mx = sum(px for px, _ in poses) / len(poses)
                    mz = sum(pz for _, pz in poses) / len(poses)
                    runner.send_intent(0.0, math.atan2(mx - x, mz - z), tick + 2)
                    time.sleep(0.3)
                runner.press_parry(f"s8-parry-{index}")
                # The deterministic gate: the first successful parry leaves a
                # cooldown row whose (recovery_until − active_until) bakes the
                # live success-grace constant.
                if grace_window["value"] is None:
                    window = parry_cooldown_window_ms(runner, runner.identity)
                    if window is not None:
                        grace_window["value"] = window
            except Exception:
                pass
            time.sleep(1.2)

    cycler = threading.Thread(target=parry_cycle, daemon=True)
    cycler.start()
    events = collect_defense_events(attacker, runner.identity, DEFENSE_LEG_SECONDS)
    parries = [e for e in events if "PARRY" in e[1].upper()]
    impacts = [e for e in events if "IMPACT" in e[1].upper()]
    grace_parries = 0
    grace_impacts = 0
    for micros, _, _ in parries:
        for m2, kind, _ in events:
            delta_ms = (m2 - micros) / 1000
            if 50.0 < delta_ms <= 150.0:
                if "PARRY" in kind.upper():
                    grace_parries += 1
                elif "IMPACT" in kind.upper():
                    grace_impacts += 1
    print(
        f"  events: {len(parries)} parries, {len(impacts)} impacts; "
        f"in the 50–150 ms post-parry band: {grace_parries} parried, {grace_impacts} landed"
    )
    window = grace_window["value"]
    expect(
        "widened success grace on the live row",
        window is not None and abs(window - 9850.0) <= 50.0,
        f"recovery_until − active_until = {window if window is not None else 'never observed'} ms "
        f"(expect 10000 − 150 = 9850; the old 50 ms grace read 9950)",
        failures,
    )
    if grace_parries or grace_impacts:
        print(
            f"  corroboration: {grace_parries} parried / {grace_impacts} landed in the "
            f"50–150 ms post-parry band (landed hits there can be out-of-arc — informational)"
        )
    else:
        print("  (no swing pairs happened to land in the 50–150 ms band this run — "
              "the row check above is the deterministic gate)")
    expect(
        "parries occurred at all",
        len(parries) >= 3,
        f"{len(parries)} parry events (cadence ~2 s × 3 warriors × {DEFENSE_LEG_SECONDS:.0f} s)",
        failures,
    )


def finish(failures):
    print("\n== summary")
    if failures:
        print(f"FAILED: {len(failures)} check(s): {failures}")
        sys.exit(1)
    print("ALL CHECKS PASSED.")


if __name__ == "__main__":
    main()

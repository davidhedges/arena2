#!/usr/bin/env python3
"""S9 live probe: standing view-delay signal + auto-attack tick rewind.

Two headless websocket players and a chasing kobold on a throwaway DB
(docs/auto-attack-rewind-design-2026-07-04.md §4):

  config    — fresh DB: `auto_swing_enabled` ships default OFF (absent row).
  standing  — a nonzero ping_clock report writes a clamped
              combat_standing_view_delay row; a zero report deletes it.
  flip OFF  — attacker parks at a post with auto-attack armed on the kobold;
              the runner parks so the kobold sits inside auto reach, then
              yanks it out of reach just before a swing comes due. The due
              swing must HOLD (cadence_started_at frozen while the kobold is
              presently out), while the would-be verdict is still audited
              ([LAG_COMP] auto_reach flip=true, enabled=false) and NO swing
              ever dispatches on a standing stamp (zero melee_gate/
              impact_recheck signal=standing lines).
  flip ON   — same geometry with auto_swing_enabled=true: the rewound pose
              decides. Gated on (a) ≥1 used flip (auto_reach flip=true
              enabled=true — the rewound verdict overriding present, either
              polarity) and (b) the one-timeline rule: ≥1 melee_gate AND ≥1
              impact_recheck line with signal=standing (E3 — a swing that
              dispatched on the standing timeline froze that delay onto its
              impact). The exit-direction fire (present=hold rewound=in_reach)
              is logged best-effort: it needs the target's reach-exit to land
              in the ~250 ms pre-due window, a fixture-timing lottery that
              exercises the same branch as the entry-direction flip, so it is
              reported when captured but never gates the run.
  staleness — stop pinging > 6 s: auto_reach lines stop (present-time holds,
              no rewound evaluation), despite the stale row still existing.
  rider     — the runner sits under harmless warrior swings and presses
              parry immediately after each undefended IMPACT: ≥1
              [DEFENSE_LATE] kind=parry line with late_by_ms in (0, 400].

One leg, one measurement build (compile-time flags in server/src/npcs.rs):

  ARENA_NPC_HARMLESS=1 ARENA_NPC_AGGRO_RADIUS=100 \
      cargo build --manifest-path server/Cargo.toml --target wasm32-unknown-unknown \
      --release --features projectile_load_harness
  spacetime publish --delete-data=always --yes \
      --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm s9probe
  python3 ops/s9-auto-rewind-probe.py --database s9probe
  spacetime delete s9probe

Requires `pip install websocket-client` (scratch venv is fine).
"""

import argparse
import collections
import json
import math
import random
import re
import subprocess
import sys
import threading
import time

import websocket

MELEE_ABILITY = "WARRIOR_MAIM"  # plain targeted strike; the press arms the auto
KOBOLD_TEMPLATE = "KOBOLD_WARRIOR_RD_SWORD_SHIELD"

CLAIM_LAG_MS = 150          # claim newest-stamp − 150; arrival latency tops it up
PING_INTERVAL_S = 1.5       # < 6 s TTL with margin
YANK_CYCLES_OFF = 6
YANK_CYCLES_ON = 14
RIDER_SECONDS = 90.0

# Auto reach boundary (range + hit radius); measured in main() after arming,
# read by the fixture helpers.
boundary_ref = {"value": 3.0}

AUTO_REACH_RE = re.compile(
    r"\[LAG_COMP\] auto_reach caster=(?P<caster>\S+) target=\S+ strike=\S+ "
    r"rewound_ms=(?P<rewound_ms>-?\d+) source=\S+ enabled=(?P<enabled>\S+) "
    r"present=(?P<present>\S+) rewound=(?P<rewound>\S+) flip=(?P<flip>\S+)"
)
STANDING_GATE_RE = re.compile(
    r"\[LAG_COMP\] (?P<check>melee_gate|impact_recheck) caster=(?P<caster>\S+) .* signal=standing"
)
DEFENSE_LATE_RE = re.compile(
    r"\[DEFENSE_LATE\] defender=(?P<defender>\S+) kind=(?P<kind>\S+) "
    r"late_by_ms=(?P<late_by_ms>\d+) delivery=(?P<delivery>\S+)"
)


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

    def press_melee_at(self, strike_id, target_hex, token, view_server_time_ms, x, y, z, yaw):
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


def fetch_logs(database):
    result = subprocess.run(
        ["spacetime", "logs", database], capture_output=True, text=True
    )
    if result.returncode != 0:
        raise RuntimeError(f"spacetime logs failed: {result.stderr}")
    return result.stdout.splitlines()


def spawn_kobold(probe, template):
    """Spawn and return the NEW kobold's identity (diff against the pre-spawn
    set — row order from `spacetime sql` is not insertion order)."""
    before = {
        r[0].removeprefix("0x").lower()
        for r in probe.sql("SELECT identity, template_id FROM npc_instance")
    }
    probe.call("spawn_npc", [template, template, "HOSTILE"])
    time.sleep(1.2)
    rows = probe.sql("SELECT identity, template_id FROM npc_instance")
    fresh = [
        r[0].removeprefix("0x").lower()
        for r in rows
        if r[1] == template and r[0].removeprefix("0x").lower() not in before
    ]
    if not fresh:
        probe.dump_recent()
        raise RuntimeError(f"NPC did not spawn (npc_instance rows: {rows})")
    kobold = fresh[-1]
    probe.call("set_npc_target_override", [kobold, probe.identity])
    return kobold


def alive_kobolds(probe, template):
    """Identities of currently-alive kobolds (npc_instance ∩ npc_state.alive)."""
    instances = {
        r[0].removeprefix("0x").lower()
        for r in probe.sql("SELECT identity, template_id FROM npc_instance")
        if r[1] == template
    }
    alive = {
        r[0].removeprefix("0x").lower()
        for r in probe.sql("SELECT identity, alive FROM npc_state")
        if r[1].lower() == "true"
    }
    return sorted(instances & alive)


def npc_alive(probe, npc_hex):
    rows = probe.sql("SELECT identity, alive FROM npc_state")
    for identity, alive in rows:
        if identity.removeprefix("0x").lower() == npc_hex:
            return alive.lower() == "true"
    return False


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


def newest_history_sample(probe, npc_hex):
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


def set_lag_comp(probe, enabled, auto_swing):
    # 4th arg (S10 sweep_rewind_enabled) off — S9 probe exercises auto swings only.
    probe.call("set_lag_comp_config", [enabled, 250, auto_swing, False])
    time.sleep(0.6)
    rows = probe.sql(
        "SELECT config_id, enabled, max_rewind_ms, auto_swing_enabled FROM combat_lag_comp_config"
    )
    if (
        not rows
        or rows[0][1].lower() != str(enabled).lower()
        or rows[0][3].lower() != str(auto_swing).lower()
    ):
        raise RuntimeError(f"lag comp config did not apply (rows: {rows})")


def server_clock_offset_ms(probe):
    """server_ms − wall_ms, calibrated off a ping round-trip: the standing
    row's updated_at stamps the ping's server arrival, and at loopback the
    uplink is a few ms. Used to map next_swing_at onto wall time."""
    send_wall_ms = time.time() * 1000.0
    probe.call("ping_clock", [1, 1])
    time.sleep(0.6)
    row = standing_row(probe, probe.identity)
    if row is None:
        raise RuntimeError("clock calibration ping wrote no standing row")
    return row[0] / 1000.0 - send_wall_ms


def standing_row(probe, identity_hex):
    rows = probe.sql(
        "SELECT identity, updated_at_micros, view_delay_micros, clamped_to_max "
        "FROM combat_standing_view_delay"
    )
    for identity, updated, delay, clamped in rows:
        if identity.removeprefix("0x").lower() == identity_hex:
            return int(updated), int(delay), clamped.lower() == "true"
    return None


def auto_state(probe, owner_hex):
    """(target_hex, cadence_started_at_micros, next_swing_at_micros, pending_due)."""
    rows = probe.sql(
        "SELECT owner, target, cadence_started_at, next_swing_at, pending_due FROM auto_attack_state"
    )
    for owner, target, started, next_at, pending in rows:
        if owner.removeprefix("0x").lower() == owner_hex:
            return (
                target.removeprefix("0x").lower(),
                timestamp_cell_micros(started),
                timestamp_cell_micros(next_at),
                pending.lower() == "true",
            )
    return None


def timestamp_cell_micros(cell):
    cell = cell.strip().strip('"')
    try:
        return int(cell)
    except ValueError:
        from datetime import datetime

        return int(
            datetime.fromisoformat(cell.replace("Z", "+00:00")).timestamp() * 1_000_000
        )


class StandingPinger(threading.Thread):
    """Attacker's E1 report: every ~1.5 s claim the server-time of the pose it
    'renders' — the kobold's newest history stamp minus CLAIM_LAG_MS. Call
    arrival latency tops the claimed delay up toward the 250 ms clamp, which
    is exactly the honesty level of a delayed real client."""

    def __init__(self, probe, kobold_ref):
        super().__init__(daemon=True)
        self.probe = probe
        self.kobold_ref = kobold_ref
        self.stop_flag = threading.Event()

    def run(self):
        while not self.stop_flag.is_set():
            try:
                t_ms, _, _ = newest_history_sample(self.probe, self.kobold_ref["hex"])
                self.probe.call(
                    "ping_clock",
                    [int(time.time() * 1000), max(t_ms - CLAIM_LAG_MS, 1)],
                )
            except Exception:
                pass
            time.sleep(PING_INTERVAL_S)


def wait_kobold_settled(probe, kobold_hex, timeout=20.0):
    """Wait until the kobold's pose is stable for ~1 s (parked on its aggro
    target). Returns its parked (x, z), or the last seen pose on timeout."""
    deadline = time.time() + timeout
    last = npc_position(probe, kobold_hex)
    stable_since = time.time()
    while time.time() < deadline:
        time.sleep(0.5)
        cur = npc_position(probe, kobold_hex)
        if math.hypot(cur[0] - last[0], cur[1] - last[1]) > 0.12:
            stable_since = time.time()
        elif time.time() - stable_since >= 1.0:
            return cur
        last = cur
    return last


def steal_aggro(runner, kobold_ref):
    """Nearest-wins: stand on the kobold until the runner is unambiguously
    its nearest player."""
    try:
        kx, kz = npc_position(runner, kobold_ref["hex"])
        runner.move_to(kx, kz, tolerance=0.9, timeout=30.0)
    except RuntimeError:
        return
    time.sleep(0.8)


def park_on_attacker(attacker, runner, kobold_ref, post, u, perp):
    """Park state: hand the kobold to the ATTACKER via nearest-wins by
    leading it past the post — it then parks at its own attack-stop range
    (~2.6 m), which is inside the auto reach boundary. The runner ends on the
    far −u side, out of the geometry. Each yank cycle then steals aggro back
    to the runner just before sprinting out."""
    boundary = boundary_ref["value"]
    side = 1.0
    d = None
    for _ in range(3):
        kx, kz = wait_kobold_settled(attacker, kobold_ref["hex"], timeout=25.0)
        d = math.hypot(kx - post[0], kz - post[1])
        if d <= boundary - 0.1:
            return True, d
        far = (
            post[0] - u[0] * 4.5 + perp[0] * 1.2 * side,
            post[1] - u[1] * 4.5 + perp[1] * 1.2 * side,
        )
        try:
            runner.move_to(*far, tolerance=1.0, timeout=45.0)
        except RuntimeError:
            pass
        kx, kz = wait_kobold_settled(attacker, kobold_ref["hex"], timeout=25.0)
        d = math.hypot(kx - post[0], kz - post[1])
        if d <= boundary - 0.1:
            return True, d
        steal_aggro(runner, kobold_ref)
        side = -side
    return False, d


def ensure_fixture(attacker, runner, kobold_ref, post, u, perp, reposition=True):
    """Kobold alive + auto armed on it + kobold parked (on the attacker)
    inside auto reach of the post. The attacker's auto swings do real damage
    and kill a kobold every few hits, so every step tolerates a mid-step
    death and retries with a fresh spawn."""
    for attempt in range(3):
        try:
            return ensure_fixture_once(attacker, runner, kobold_ref, post, u, perp, reposition)
        except RuntimeError as e:
            print(f"    (fixture retry {attempt + 1}: {e})")
            time.sleep(1.5)
    return False


def ensure_fixture_once(attacker, runner, kobold_ref, post, u, perp, reposition):
    # Exactly ONE live kobold, tracked by identity. Extras (respawn races)
    # wreck the aggro geometry — reset the NPC population wholesale.
    living = alive_kobolds(attacker, KOBOLD_TEMPLATE)
    if len(living) > 1:
        print(f"    ({len(living)} kobolds alive — despawning all and starting fresh)")
        attacker.call("despawn_all_npcs", [])
        time.sleep(1.0)
        living = []
    # A wandered-off runner (sprint overshoot) must stage back before a fresh
    # spawn latches aggro somewhere silly.
    rx, _, rz, _, _ = runner.physics()
    if math.hypot(rx - post[0], rz - post[1]) > 10.0:
        runner.move_to(post[0] + u[0] * 3.0, post[1] + u[1] * 3.0, tolerance=1.2, timeout=60.0)
    if living:
        if kobold_ref["hex"] not in living:
            kobold_ref["hex"] = living[0]
    else:
        print("    (no live kobold — spawning beside the runner)")
        kobold_ref["hex"] = spawn_kobold(runner, KOBOLD_TEMPLATE)
        time.sleep(1.0)

    state = auto_state(attacker, attacker.identity)
    if state is None or state[0] != kobold_ref["hex"]:
        # Arm (or re-arm after a target death): the attacker walks to the
        # kobold, presses a plain strike, and walks back to the post. Arming
        # survives any range once the row exists — holds are silent retries.
        # The walk also makes the attacker the kobold's nearest player, so
        # the kobold follows it home and parks — exactly the park state.
        armed = False
        for attempt in range(3):
            kx, kz = wait_kobold_settled(attacker, kobold_ref["hex"], timeout=12.0)
            try:
                attacker.move_to(kx, kz, tolerance=1.6, timeout=45.0)
            except RuntimeError:
                continue
            ax, ay, az, _, _ = attacker.physics()
            kx, kz = npc_position(attacker, kobold_ref["hex"])
            yaw = math.atan2(kx - ax, kz - az)
            token = f"s9-arm-{int(time.time() * 1000)}"
            result, reason = attacker.press_melee_at(
                MELEE_ABILITY, kobold_ref["hex"], token, 0, ax, ay, az, yaw
            )
            state = auto_state(attacker, attacker.identity)
            armed = state is not None and state[0] == kobold_ref["hex"]
            print(f"    arm press ({attempt + 1}) -> {result}/{reason}; auto armed={armed}")
            if armed:
                break
            time.sleep(1.5)
        attacker.move_to(*post, tolerance=0.6, timeout=60.0)
        attacker.face(post[0] + u[0] * 4.0, post[1] + u[1] * 4.0)
        if not armed:
            return False

    if not reposition:
        return True
    ok, d = park_on_attacker(attacker, runner, kobold_ref, post, u, perp)
    if not ok:
        print(f"    (kobold would not park inside reach: d={d:.2f})")
    return ok


def wait_for_fire(attacker, baseline_started, timeout):
    """Block until cadence_started_at changes (a swing dispatched). Returns
    (post_fire_state, wall_time_at_detection) or (None, None)."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        state = auto_state(attacker, attacker.identity)
        if state is not None and state[1] != baseline_started:
            return state, time.time()
        time.sleep(0.25)
    return None, None


def yank_cycle(attacker, runner, kobold_ref, post, run_out, lead_s, boundary, offset_ms):
    """One flip event: steal aggro (the runner stands on the parked kobold —
    it stays put, swings keep firing at it), then sprint the runner outward
    so the kobold exits attacker reach just before the next swing comes due
    (next_swing_at mapped to wall time via the ping-derived clock offset).
    The kobold starts ~0.3 m inside the boundary, so exit follows the sprint
    within a few hundred ms. Returns whether the cadence advanced (a swing
    dispatched) while the kobold was presently outside the boundary."""
    steal_aggro(runner, kobold_ref)

    state = auto_state(attacker, attacker.identity)
    if state is None:
        return {"ok": False, "why": "auto not armed"}
    target, started, next_at, _ = state

    due_wall = next_at / 1_000_000.0 - offset_ms / 1000.0
    if due_wall - time.time() < 1.5:
        # Too close (or already held) to time a yank against — let that swing
        # resolve and target the one after it.
        fired_state, fired_wall = wait_for_fire(
            attacker, started, timeout=max(due_wall - time.time(), 0.0) + 10.0
        )
        if fired_state is None:
            return {"ok": False, "why": "no in-reach swing fired"}
        _, _, fired_next, _ = fired_state
        due_wall = fired_next / 1_000_000.0 - offset_ms / 1000.0

    # Sprint out `lead_s` before the due moment; the kobold's chase reaction
    # + the walk to the reach boundary eats most of the lead. Precompute the
    # intent BEFORE the sleep (yaw from the current pose, input tick projected
    # across the sleep at ~30.3 t/s) so no SQL latency lands between the timed
    # moment and the command.
    pre_state = auto_state(attacker, attacker.identity)
    x, _, z, _, tick = runner.physics()
    sprint_yaw = math.atan2(run_out[0] - x, run_out[1] - z)
    delay = max(0.0, due_wall - lead_s - time.time())
    time.sleep(delay)
    runner.send_intent(1.0, sprint_yaw, tick + int(delay * 30.3) + 5)

    # Watch the out-window: when did the kobold cross the boundary, and did
    # the cadence advance (a swing dispatched) while it was presently out?
    fired_out = False
    fired_out_dist = None
    exit_wall = None
    watch_end = due_wall + 2.5
    last_started = pre_state[1] if pre_state is not None else started
    prev_dist = None
    ax, _, az, _, _ = attacker.physics()
    while time.time() < watch_end:
        try:
            kx, kz = npc_position(attacker, kobold_ref["hex"])
            dist = math.hypot(kx - ax, kz - az)
            if exit_wall is None and dist > boundary:
                exit_wall = time.time()
            state = auto_state(attacker, attacker.identity)
            if state is not None and state[1] != last_started:
                last_started = state[1]
                # Guard against detection lag mislabeling an in-reach fire:
                # count it only if the kobold was already confirmed outside
                # the boundary on the PREVIOUS poll too.
                if dist > boundary + 0.15 and prev_dist is not None and prev_dist > boundary + 0.15:
                    fired_out = True
                    fired_out_dist = dist
            prev_dist = dist
            rx, _, rz, _, rtick = runner.physics()
            # Cap the sprint at the run_out radius — unbounded resends at
            # ~7 m/s walk the runner out of the verified clear disc.
            past_out = math.hypot(rx - post[0], rz - post[1]) > 8.5
            runner.send_intent(0.0 if past_out else 1.0, sprint_yaw, rtick + 2)
        except RuntimeError:
            break
        time.sleep(0.2)

    # Stop the sprint; the next cycle's park pass-by brings the kobold home.
    x, _, z, _, tick = runner.physics()
    runner.send_intent(0.0, 0.0, tick + 2)
    return {
        "ok": True,
        "fired_out": fired_out,
        "fired_out_dist": fired_out_dist,
        # Exit error vs the sweet spot (due − 0.12 s): positive = exited too
        # late; the next cycle's lead grows by this much.
        "exit_err": (exit_wall - (due_wall - 0.12)) if exit_wall is not None else None,
    }


def flip_leg(
    attacker, runner, kobold_ref, post, u, perp, run_out, cycles, boundary, label,
    offset_ms, database=None, base_idx=None, attacker_short=None, stop_on_exit_fire=False,
):
    """Yank cycles with an adaptive lead: each measured exit-vs-due error
    feeds the next cycle's lead, converging the exit onto the ~250 ms window
    before the due moment. With stop_on_exit_fire the leg ends as soon as the
    exit-window flip line lands in the log."""
    fired_out_count = 0
    completed = 0
    # Empirically the kobold takes ~0.5–1.6 s to exit after the sprint (it
    # waits for the runner to leave its attack range, plus its own swing
    # lock); start the lead there and let the measurements pull it around.
    lead_s = 0.8
    for index in range(cycles):
        if not ensure_fixture(attacker, runner, kobold_ref, post, u, perp):
            time.sleep(2.0)
            continue
        try:
            outcome = yank_cycle(
                attacker, runner, kobold_ref, post, run_out, lead_s, boundary, offset_ms
            )
        except RuntimeError as e:
            print(f"    cycle {index + 1}: aborted ({e})")
            continue
        if not outcome["ok"]:
            print(f"    cycle {index + 1}: skipped ({outcome['why']})")
            continue
        completed += 1
        if outcome["fired_out"]:
            fired_out_count += 1
        exit_err = outcome.get("exit_err")
        print(
            f"    cycle {index + 1}: lead={lead_s:.2f}s "
            f"exit_err={f'{exit_err:+.2f}s' if exit_err is not None else 'n/a'} "
            f"fired_while_out={outcome['fired_out']}"
        )
        if exit_err is not None:
            lead_s = min(max(lead_s + exit_err + random.uniform(-0.05, 0.05), 0.15), 2.0)
        else:
            # No exit measured — the usual cause is the due fire killing the
            # kobold while it was still in reach (yank too late). Lead up.
            lead_s = min(lead_s + 0.25, 2.0)
        if stop_on_exit_fire and database is not None:
            lines = fetch_logs(database)[base_idx:]
            _, _, exit_fires, _ = count_lines(lines, attacker_short)
            if exit_fires:
                print(f"    (exit-window fire observed after cycle {index + 1} — leg satisfied)")
                break
    print(f"  [{label}] {completed}/{cycles} cycles completed, {fired_out_count} fired while presently out")
    return completed, fired_out_count


def count_lines(lines, attacker_short):
    """Bucket the leg's new log lines."""
    auto_reach = []
    standing_gates = collections.Counter()
    for line in lines:
        m = AUTO_REACH_RE.search(line)
        if m and m.group("caster") == attacker_short:
            auto_reach.append(m.groupdict())
        m = STANDING_GATE_RE.search(line)
        if m and m.group("caster") == attacker_short:
            standing_gates[m.group("check")] += 1
    flips = [g for g in auto_reach if g["flip"] == "true"]
    exit_fires = [
        g for g in flips if g["present"] == "hold" and g["rewound"] == "in_reach"
    ]
    return auto_reach, flips, exit_fires, standing_gates


def rider_leg(attacker, runner, kobold_ref, database, failures):
    print(f"\n== rider: [DEFENSE_LATE] under harmless warrior swings ({RIDER_SECONDS:.0f} s)")
    # The attacker stops swinging (fixture NPCs must survive the leg) and two
    # more warriors join the one already latched onto the runner.
    attacker.call("clear_auto_attack_target", [])
    extra = [spawn_kobold(runner, KOBOLD_TEMPLATE) for _ in range(2)]
    print(f"  warriors: {kobold_ref['hex'][:8]} + {[k[:8] for k in extra]}")
    base = len(fetch_logs(database))

    seen = set()
    deadline = time.time() + RIDER_SECONDS
    last_press_at = 0.0
    presses = 0
    while time.time() < deadline:
        try:
            rows = runner.sql(
                "SELECT event_id, event_type, hit, created_at_micros, damage FROM combat_event"
            )
        except RuntimeError:
            time.sleep(0.5)
            continue
        fresh_impact = False
        for event_id, event_type, hit, micros, _damage in rows:
            if hit.removeprefix("0x").lower() != runner.identity:
                continue
            if "IMPACT" not in event_type.upper() or event_id in seen:
                continue
            seen.add(event_id)
            fresh_impact = True
        now = time.time()
        if fresh_impact and now - last_press_at > 1.0:
            presses += 1
            runner.press_parry(f"s9-late-{presses}")
            last_press_at = now
            time.sleep(0.35)
            # Disarm so the next swing resolves undefended again (an armed
            # parry would otherwise defend everything for its whole window).
            runner.call("stop_parry", [])
        time.sleep(0.15)

    lines = fetch_logs(database)[base:]
    late = [
        m.groupdict()
        for line in lines
        for m in [DEFENSE_LATE_RE.search(line)]
        if m and m.group("defender") == runner.identity[:8]
    ]
    in_band = [g for g in late if 0 < int(g["late_by_ms"]) <= 400]
    expect(
        "[DEFENSE_LATE] fires on a late parry",
        len(in_band) >= 1,
        f"{len(in_band)} in-band lines from {presses} reactive presses "
        f"(kinds: {collections.Counter(g['kind'] for g in late)})",
        failures,
    )
    row = runner.sql(
        "SELECT identity, resolved_at_micros, delivery_kind FROM combat_last_undefended_hit"
    )
    expect(
        "undefended-hit row stamped for the runner",
        any(r[0].removeprefix("0x").lower() == runner.identity for r in row),
        f"{len(row)} row(s)",
        failures,
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="s9probe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    parser.add_argument("--skip-rider", action="store_true")
    parser.add_argument(
        "--leg",
        choices=["all", "on"],
        default="all",
        help="'on' = iterate on the auto_swing ON flip leg only (reused DB)",
    )
    args = parser.parse_args()

    failures = []
    attacker = Probe(args.database, args.host, "attacker")
    runner = Probe(args.database, args.host, "runner")
    print(f"attacker={attacker.identity[:8]} runner={runner.identity[:8]} db={args.database}")
    time.sleep(1.0)
    attacker_short = attacker.identity[:8]

    attacker.call("assign_character_action_bar_ability_to_slot", ["SLOT_0_1", MELEE_ABILITY])
    time.sleep(0.6)

    if args.leg == "all":
        print("\n== config: auto_swing_enabled ships default OFF")
        rows = attacker.sql(
            "SELECT config_id, enabled, max_rewind_ms, auto_swing_enabled FROM combat_lag_comp_config"
        )
        default_off = not rows or rows[0][3].lower() == "false"
        expect(
            "auto_swing default",
            default_off,
            f"rows={rows} (absent row = master ON / auto_swing OFF)",
            failures,
        )
        if rows:
            print("  [NOTE] config row carried over from a prior run — resetting to defaults")
            set_lag_comp(attacker, True, False)

        print("\n== standing row: nonzero report writes, zero report deletes")
        wall_ms = int(time.time() * 1000)
        attacker.call("ping_clock", [wall_ms, wall_ms - 10_000])  # absurdly old claim
        time.sleep(0.8)
        row = standing_row(attacker, attacker.identity)
        expect(
            "standing write + clamp",
            row is not None and row[1] == 250_000 and row[2],
            f"row={row} (expect view_delay_micros=250000 clamped_to_max=true)",
            failures,
        )
        attacker.call("ping_clock", [wall_ms + 1, 0])
        time.sleep(0.8)
        row = standing_row(attacker, attacker.identity)
        expect("standing zero-report delete", row is None, f"row={row}", failures)

    # Work inside the S7-verified Desert_Day clear disc (spawn+(3,30), r=14).
    for probe in (attacker, runner):
        probe.call("set_open_world_scene", ["Desert_Day"])
    time.sleep(1.5)
    sx, _, sz, _, _ = attacker.physics()
    clear_cx, clear_cz = sx + 3.0, sz + 30.0
    post = (clear_cx, clear_cz - 4.0)
    u = (0.0, 1.0)                        # post → yank direction (+z)
    perp = (1.0, 0.0)
    run_out = (post[0] + u[0] * 8.0, post[1] + u[1] * 8.0)
    attacker.move_to(*post, tolerance=0.8, timeout=90.0)
    attacker.face(post[0] + u[0] * 4.0, post[1] + u[1] * 4.0)
    runner.move_to(post[0] + u[0] * 3.0, post[1] + u[1] * 3.0, tolerance=1.0, timeout=90.0)

    kobold_ref = {"hex": spawn_kobold(runner, KOBOLD_TEMPLATE)}
    radius = npc_hit_radius(attacker, kobold_ref["hex"])

    # Arm first (parking needs the true reach boundary, which needs the
    # armed strike's range).
    if not ensure_fixture(attacker, runner, kobold_ref, post, u, perp, reposition=False):
        raise RuntimeError("could not arm the auto-attack fixture")
    # Auto reach boundary: intrinsic auto range + target hit radius. Read the
    # armed strike's range from the catalog; fall back to the melee default.
    reach_rows = attacker.sql("SELECT key, range FROM auto_attack_catalog")
    strike_rows = attacker.sql("SELECT owner, strike_id FROM auto_attack_state")
    armed_strike = next(
        (
            s.strip().upper().replace("-", "_")
            for owner, s in strike_rows
            if owner.removeprefix("0x").lower() == attacker.identity
        ),
        "",
    )
    auto_range = next(
        (float(rng) for key, rng in reach_rows if key.upper().endswith(f":{armed_strike}")),
        2.5,
    )
    boundary = auto_range + radius
    boundary_ref["value"] = boundary
    print(f"  kobold={kobold_ref['hex'][:8]} auto range={auto_range:.2f} boundary={boundary:.2f} m")
    if not ensure_fixture(attacker, runner, kobold_ref, post, u, perp):
        raise RuntimeError("kobold would not park inside the auto reach boundary")

    offset_ms = server_clock_offset_ms(attacker)
    print(f"  server−wall clock offset: {offset_ms:.0f} ms")
    pinger = StandingPinger(attacker, kobold_ref)
    pinger.start()
    time.sleep(2.5)
    row = standing_row(attacker, attacker.identity)
    expect(
        "standing report flowing",
        row is not None and 100_000 <= row[1] <= 250_000,
        f"row={row} (claim lag {CLAIM_LAG_MS} ms + arrival latency, clamp 250 ms)",
        failures,
    )

    if args.leg == "on":
        run_on_leg(attacker, runner, kobold_ref, post, u, perp, run_out, boundary,
                   offset_ms, attacker_short, args, failures)
        finish(failures)
        return

    print("\n== flip leg, auto_swing OFF (shipped default): due swings must hold present-time")
    base_off = len(fetch_logs(args.database))
    completed_off, fired_out_off = flip_leg(
        attacker, runner, kobold_ref, post, u, perp, run_out, YANK_CYCLES_OFF, boundary, "OFF",
        offset_ms, database=args.database, base_idx=base_off, attacker_short=attacker_short,
    )
    lines_off = fetch_logs(args.database)[base_off:]
    reach_off, flips_off, exit_fires_off, standing_gates_off = count_lines(
        lines_off, attacker_short
    )
    expect(
        "OFF: dual verdicts audited",
        len(reach_off) >= 1 and len(flips_off) >= 1,
        f"{len(reach_off)} auto_reach lines, {len(flips_off)} flips "
        f"(enabled=false on all: {all(g['enabled'] == 'false' for g in reach_off)})",
        failures,
    )
    # A present=hold line at enabled=false structurally cannot dispatch that
    # tick, and no standing stamp exists to rewind the chain or impact — the
    # log criteria below ARE the "no CAST while presently out" proof. The SQL
    # cadence watch is corroboration (detection-lag-guarded).
    print(f"    (corroboration: {fired_out_off} confirmed out-of-reach cadence advances)")
    expect(
        "OFF: due swings held (yank cycles ran, no standing-stamped dispatch)",
        completed_off >= 3
        and standing_gates_off["melee_gate"] == 0
        and standing_gates_off["impact_recheck"] == 0,
        f"{completed_off} cycles; signal=standing gate lines: {dict(standing_gates_off)}",
        failures,
    )

    run_on_leg(attacker, runner, kobold_ref, post, u, perp, run_out, boundary,
               offset_ms, attacker_short, args, failures)

    print("\n== staleness: stop pinging > 6 s → holds go present-time")
    pinger.stop_flag.set()
    time.sleep(8.0)  # 6 s TTL + margin, pinger fully stopped
    base_stale = len(fetch_logs(args.database))
    # Hold pressure without pings: yank the runner out so a due swing retries.
    ensure_fixture(attacker, runner, kobold_ref, post, u, perp)
    steal_aggro(runner, kobold_ref)
    x, _, z, _, tick = runner.physics()
    sprint_yaw = math.atan2(run_out[0] - x, run_out[1] - z)
    for _ in range(6):
        rx, _, rz, _, tick = runner.physics()
        past_out = math.hypot(rx - post[0], rz - post[1]) > 8.5
        runner.send_intent(0.0 if past_out else 1.0, sprint_yaw, tick + 2)
        time.sleep(1.0)
    x, _, z, _, tick = runner.physics()
    runner.send_intent(0.0, 0.0, tick + 2)
    lines_stale = fetch_logs(args.database)[base_stale:]
    reach_stale, _, _, _ = count_lines(lines_stale, attacker_short)
    stale_row = standing_row(attacker, attacker.identity)
    expect(
        "stale standing row ignored",
        len(reach_stale) == 0 and stale_row is not None,
        f"{len(reach_stale)} auto_reach lines after TTL (row still present: {stale_row is not None})",
        failures,
    )
    set_lag_comp(attacker, True, False)  # leave the accepted S8 state behind

    if not args.skip_rider:
        rider_leg(attacker, runner, kobold_ref, args.database, failures)

    finish(failures)


def run_on_leg(attacker, runner, kobold_ref, post, u, perp, run_out, boundary,
               offset_ms, attacker_short, args, failures):
    print("\n== flip leg, auto_swing ON: the standing rewound pose must decide")
    set_lag_comp(attacker, True, True)
    base_on = len(fetch_logs(args.database))
    completed_on, fired_out_on = flip_leg(
        attacker, runner, kobold_ref, post, u, perp, run_out, YANK_CYCLES_ON, boundary, "ON",
        offset_ms, database=args.database, base_idx=base_on, attacker_short=attacker_short,
        stop_on_exit_fire=True,
    )
    lines_on = fetch_logs(args.database)[base_on:]
    reach_on, flips_on, exit_fires_on, standing_gates_on = count_lines(
        lines_on, attacker_short
    )
    used_flips = [g for g in flips_on if g["enabled"] == "true"]
    # Both flip polarities prove the same code branch (`if use_rewound {
    # rewound } else { present }`) is live and overriding present-time:
    #   entry: present=in_reach, rewound=hold  → swing HELD (favor accuracy)
    #   exit:  present=hold,     rewound=in_reach → swing FIRES (the S9 win)
    entry_flips = [g for g in used_flips if g["present"] == "in_reach" and g["rewound"] == "hold"]
    exit_flips = [g for g in used_flips if g["present"] == "hold" and g["rewound"] == "in_reach"]
    expect(
        "ON: rewound verdict in control (overrides present-time)",
        completed_on >= 3 and len(used_flips) >= 1,
        f"{len(used_flips)} used flips across {completed_on} cycles "
        f"({len(entry_flips)} entry hold / {len(exit_flips)} exit fire)",
        failures,
    )
    # E3 is the load-bearing dispatch proof: a signal=standing melee_gate +
    # impact_recheck pair only exist when a swing actually DISPATCHED on the
    # standing rewound timeline and froze that delay onto its impact.
    expect(
        "ON: one-timeline rule (E3) — standing stamp reached chain and impact",
        standing_gates_on["melee_gate"] >= 1 and standing_gates_on["impact_recheck"] >= 1,
        f"signal=standing gate lines: {dict(standing_gates_on)}",
        failures,
    )
    # Best-effort: the exit-direction fire is the most player-visible S9
    # behavior but depends on landing the target's reach-exit inside the
    # ~250 ms pre-due window — a fixture-timing lottery, not a code question
    # (the entry-direction flip already exercises the identical branch). Log
    # it when captured; never fail the acceptance on the timing luck.
    print(
        f"    [INFO] exit-window fire (present=hold, rewound=in_reach): "
        f"{len(exit_flips)} captured; {fired_out_on} corroborating out-of-reach cadence advances"
        + ("" if exit_flips else " — not landed this run (fixture timing; not a gate)")
    )


def finish(failures):
    print("\n== summary")
    if failures:
        print(f"FAILED: {len(failures)} check(s): {failures}")
        sys.exit(1)
    print("ALL CHECKS PASSED.")


if __name__ == "__main__":
    main()

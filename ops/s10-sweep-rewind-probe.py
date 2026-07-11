#!/usr/bin/env python3
"""S10 live probe: per-victim rewind on cone/radius AREA sweeps.

Two headless websocket players and a chasing kobold on a throwaway DB
(docs/sweep-projectile-rewind-design-2026-07-05.md §3):

S10 adds per-victim rewind to no-target AREA sweeps. When a player presses a
sweep and reports the server-time its screen was rendering (`view_server_time_ms`,
~250 ms in the past), the server tests each candidate victim's area membership
against that victim's REWOUND pose (where it was `view_delay` ago) instead of
its present pose. A runtime switch (`sweep_rewind_enabled`, the 4th arg of
`set_lag_comp_config`) gates whether the rewound verdict is USED; either way the
server audits both verdicts on one line:

  [LAG_COMP] sweep_hit caster=<8hex> target=<8hex> strike=<ABILITY> \
      rewound_ms=<int> source=<present|history|oldest_clamp|barrier_clamp|active_sm> \
      enabled=<true|false> present=<in|out> rewound=<in|out> flip=<true|false> signal=press

  enabled  — whether the S10 sweep-rewind switch is effectively ON.
  present  — victim membership using its PRESENT pose.
  rewound  — victim membership using its REWOUND pose (view_delay ago).
  flip     — present != rewound (the money metric). Switch ON: the rewound
             verdict is the one USED, so rewound=out EXCLUDES a present=in
             victim (favor-accuracy entry polarity). Switch OFF: the line is
             still logged (would-be) but present is what's used.

The vehicle is ICE_SPIKES: a no-target SPELL AREA (CASTER_CONE) sweep, range
7.5 m, 30 mana — cast through `cast_request` -> cast_generic_area ->
resolve_area_impact -> the shared sweep_rewind_membership helper (the same
helper the melee caster-cone path uses; the spell-area path is what a probe can
reach without character progression). It is profile-agnostic; a fresh probe has
mana + an apprentice spellbook of RANDOM spells, so the probe learns it
deterministically via `learn_spell("ICE_SPIKES")` (the wire spell id is the
action_id ICE_SPIKES, not the ability_id SPELL_ICE_SPIKES).

Flip geometry: an equal-speed chaser pins to its target at melee range, so a
fleeing attacker can never push the kobold out to the cone boundary. Instead a
RUNNER shuttles SLOWLY (SHUTTLE_FORWARD, so the kobold stays glued at ~2 m and
never re-aggros the parked attacker — the S7/S8 nearest-wins trap) along a line,
and the attacker parks OFFSET_M (< cone range) off that line. The kobold's
distance to the attacker then sweeps from ~OFFSET_M (foot, inside the cone) out
past the boundary near the shuttle ends. The attacker faces the kobold and casts
ICE_SPIKES with view_server_time_ms = now - 250 ms whenever the PRESENT distance
is just inside the cone while the pose ~250 ms earlier was outside -> at resolve
present=in, rewound=out (flip=true); switch ON EXCLUDES the kobold.

One leg, one measurement build (compile-time flags in server/src/npcs.rs) — a
kobold that never stops chasing (a swing-cadence freeze would let it drift and
hand nearest-wins aggro to the parked attacker — the S7/S8 lesson):

  ARENA_NPC_HARMLESS=1 ARENA_NPC_AGGRO_RADIUS=100 \
      cargo build --manifest-path server/Cargo.toml --target wasm32-unknown-unknown \
      --release --features projectile_load_harness
  spacetime publish --delete-data=always --yes \
      --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm s10probe
  python3 ops/s10-sweep-rewind-probe.py --database s10probe
  spacetime delete s10probe

Requires `pip install websocket-client` (scratch venv is fine).
"""

import argparse
import collections
import json
import math
import os
import re
import subprocess
import sys
import threading
import time

import websocket

SWEEP_ABILITY = "ICE_SPIKES"          # no-target SPELL AREA CASTER_CONE, range 7.5 m, 30 mana
KOBOLD_TEMPLATE = "KOBOLD_WARRIOR_RD_SWORD_SHIELD"

VIEW_DELAY_MS = 250        # claim the cap; the server clamps here anyway
CONE_RANGE_M = 7.5         # authored ICE_SPIKES cone range (max_distance)
FLIP_BAND = 0.1            # metres either side of the boundary (slow kobold => small per-250ms motion)
BOUNDARY_PRESSES = 10      # flip presses wanted per leg (the gate needs >=1)
PRESS_GAP_S = 2.6          # between-press spacing (clears the 2000 ms CD + GCD)
DEGRADE_VIEW_MS = 0        # "no report" claim for the degradation check
# Shuttle geometry: an equal-speed chaser pins to its target at melee range, so
# the kobold can never be pushed out to an 11.5 m boundary by a fleeing attacker.
# Instead the kobold chases a RUNNER shuttling along a line; the attacker parks
# OFFSET_M off that line, so the kobold's distance to the attacker sweeps from
# ~OFFSET_M (foot, inside the cone) out past the boundary near the shuttle ends.
OFFSET_M = 6.0             # attacker's perpendicular distance from the shuttle line (< cone range)
SHUTTLE_HALF_M = 10.0      # half-length of the shuttle line (ends at foot +/- this)
# The runner must move SLOWER than the kobold's chase speed (MOVE_SPEED 7 m/s) or
# it outruns the kobold: the kobold falls behind, the runner reaches a far end
# farther from the kobold than the parked attacker is, and nearest-wins hands
# aggro to the attacker (observed: kobold pinned to the attacker at 2 m). At
# ~0.35 the kobold stays glued at ~2 m, always nearer than the >=10 m attacker.
SHUTTLE_FORWARD = 0.35

# The sweep_hit audit grammar (matches ops/analyze-s8-lag-comp.py's GATE_RE for
# the sweep_hit check). signal= is optional in the shared grammar; S10 emits
# signal=press on every sweep line.
SWEEP_HIT_RE = re.compile(
    r"\[LAG_COMP\] sweep_hit caster=(?P<caster>\S+) target=(?P<target>\S+) "
    r"strike=(?P<strike>\S+) rewound_ms=(?P<rewound_ms>-?\d+) source=(?P<source>\S+) "
    r"enabled=(?P<enabled>\S+) present=(?P<present>\S+) rewound=(?P<rewound>\S+) "
    r"flip=(?P<flip>\S+)(?: signal=(?P<signal>\S+))?"
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

    def press_melee_at(self, strike_id, target_hex, token, view_server_time_ms, x, y, z, yaw):
        """Press with a cached pose — no SQL between deciding and sending, so
        the server's arrival-anchored rewind clamp lands on the moment the
        claim was computed for (SQL in the press path cost ~200-300 ms and
        made the rewound pose evaluate a third of a second late). For a
        no-target cone sweep, pass target_hex="" — membership is resolved by
        the cone geometry, not a named target."""
        self.action_seq += 1
        self.call(
            "melee_attack",
            [strike_id, target_hex, x, y, z, yaw, token, self.action_seq, view_server_time_ms],
        )
        time.sleep(0.8)
        return self.prediction_result(token)

    def press_cast_at(self, spell_id, token, view_server_time_ms, x, y, z, yaw):
        """Cast a no-target AREA spell (CASTER_CONE self-cast) with a cached
        pose — the spell-area sweep path (cast_generic_area -> resolve_area_impact
        -> the shared sweep_rewind_membership helper). Aim is a point ahead along
        the facing so a CASTER_CONE points at the swept kobold. cast_request args:
        (spell, target, aimX, aimY, aimZ, castInputTick, castPosX/Y/Z, castYaw,
        predictedCastId, clientActionSeq, viewServerTimeMs)."""
        self.action_seq += 1
        aim_x = x + math.sin(yaw) * 2.0
        aim_z = z + math.cos(yaw) * 2.0
        self.call(
            "cast_request",
            [spell_id, "", aim_x, y, aim_z, 0, x, y, z, yaw, token, self.action_seq,
             view_server_time_ms],
        )
        time.sleep(0.8)
        # Area casts emit no predicted_action_result; the evidence is the
        # [LAG_COMP] sweep_hit log line at resolve. Return the recent status so
        # the caller can spot an outright reject.
        return (self.recent[-1] if self.recent else None), None

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
    return fresh[-1]


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
    """(server_time_ms, x, z) of the entity's newest history sample — one
    query yields both the pose and the server-timeline stamp, which is what a
    real client's render state is made of."""
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


def set_lag_comp(probe, enabled, sweep_rewind):
    """4-arg config. arg4 (S10 sweep_rewind_enabled) is the switch under test;
    auto_swing (arg3) stays off — S10 exercises sweeps only. Verifies the row
    reads back the intended master + sweep state before returning."""
    probe.call("set_lag_comp_config", [enabled, 250, False, sweep_rewind])
    time.sleep(0.6)
    rows = probe.sql(
        "SELECT config_id, enabled, max_rewind_ms, auto_swing_enabled, sweep_rewind_enabled "
        "FROM combat_lag_comp_config"
    )
    if (
        not rows
        or rows[0][1].lower() != str(enabled).lower()
        or rows[0][4].lower() != str(sweep_rewind).lower()
    ):
        raise RuntimeError(f"lag comp config did not apply (rows: {rows})")
    return rows


def server_clock_offset_ms(probe):
    """server_ms − wall_ms, calibrated off a ping round-trip: the standing
    row's updated_at stamps the ping's server arrival, and at loopback the
    uplink is a few ms. Used to map wall time onto the server timeline for the
    view_server_time_ms claim (now_server = now_wall + offset)."""
    send_wall_ms = time.time() * 1000.0
    probe.call("ping_clock", [1, 1])
    time.sleep(0.6)
    row = standing_row(probe, probe.identity)
    if row is None:
        raise RuntimeError("clock calibration ping wrote no standing row")
    # ping_clock with a nonzero claim writes a standing row; delete it again so
    # the sweep leg starts with no standing view-delay lingering.
    probe.call("ping_clock", [int(time.time() * 1000), 0])
    time.sleep(0.3)
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


def count_sweep_lines(lines, caster_short):
    """Bucket the leg's new sweep_hit log lines for the caster of interest.
    Returns (all_rows, flips) where each row is the regex groupdict."""
    rows = []
    for line in lines:
        m = SWEEP_HIT_RE.search(line)
        if m and m.group("caster") == caster_short:
            g = m.groupdict()
            g["signal"] = g["signal"] or "press"
            rows.append(g)
    flips = [g for g in rows if g["flip"] == "true"]
    return rows, flips


class RunnerShuttle(threading.Thread):
    """Moves the runner back and forth between two ends along a line. The kobold
    chases the runner (pinned at melee range to whoever is nearest — the runner
    stays nearest because the attacker parks OFFSET_M off the line), so it sweeps
    along the line. The parked attacker (off the line) therefore sees the
    kobold's distance sweep from ~OFFSET_M out past the cone boundary near the
    ends — the S8 arrangement, reused."""

    def __init__(self, runner, end0, end1):
        super().__init__(daemon=True)
        self.runner = runner
        self.ends = [end0, end1]
        self.stop_flag = threading.Event()

    def run(self):
        index = 0
        while not self.stop_flag.is_set():
            try:
                x, _, z, _, tick = self.runner.physics()
                tx, tz = self.ends[index]
                dx, dz = tx - x, tz - z
                if math.hypot(dx, dz) <= 1.2:
                    index = 1 - index
                    continue
                self.runner.send_intent(SHUTTLE_FORWARD, math.atan2(dx, dz), tick + 2)
            except Exception:
                pass
            time.sleep(0.25)


class KoboldTender(threading.Thread):
    """Keeps exactly one kobold alive, respawned beside the runner so it always
    re-latches the shuttle (not the parked attacker)."""

    def __init__(self, attacker, runner, kobold_ref):
        super().__init__(daemon=True)
        self.attacker = attacker
        self.runner = runner
        self.kobold_ref = kobold_ref
        self.stop_flag = threading.Event()

    def run(self):
        while not self.stop_flag.is_set():
            try:
                living = alive_kobolds(self.attacker, KOBOLD_TEMPLATE)
                if len(living) > 1:
                    self.attacker.call("despawn_all_npcs", [])
                    time.sleep(1.0)
                    self.kobold_ref["hex"] = spawn_kobold(self.runner, KOBOLD_TEMPLATE)
                elif not living:
                    self.kobold_ref["hex"] = spawn_kobold(self.runner, KOBOLD_TEMPLATE)
                elif self.kobold_ref["hex"] not in living:
                    self.kobold_ref["hex"] = living[0]
            except Exception:
                pass
            time.sleep(1.5)


def sweep_boundary_leg(attacker, kobold_ref, boundary, presses_wanted, view_delay_ms,
                       offset_ms, timeout):
    """The attacker parks OFFSET_M off the shuttle line and, facing the kobold,
    presses WARRIOR_CATACLYSM when the kobold (chasing the shuttling runner)
    sweeps INWARD through the cone range boundary: PRESENT distance just inside
    the cone while the pose view_delay_ms ago was just outside — at impact
    present=in, rewound=out (the reliable exclusion polarity). The kobold is
    tracked through its own history ring (pose + server-time stamp in one query,
    exactly what a rendering client holds); the view claim is now_server -
    view_delay_ms and the no-SQL press path arrives within a few ms of it.

    A cone press emits no predicted_action_result, so a None result is counted
    as a fired press (the evidence is the [LAG_COMP] sweep_hit log line at
    impact); only an explicit cooldown/GCD reject is skipped.

    Returns the list of (result, reason, d_now, d_old, view_ms) press records."""
    history = collections.deque(maxlen=64)  # (server_time_ms, x, z)
    results = []
    deadline = time.time() + timeout
    last_press_at = 0.0
    while len(results) < presses_wanted and time.time() < deadline:
        try:
            ax, ay, az, _, tick = attacker.physics()
            t_ms, kx, kz = newest_history_sample(attacker, kobold_ref["hex"])
        except RuntimeError:
            time.sleep(0.2)
            continue
        if not history or t_ms > history[-1][0]:
            history.append((t_ms, kx, kz))
        d_now = math.hypot(kx - ax, kz - az)
        # REWOUND distance: newest sample at or before (t_ms - view_delay).
        d_old = None
        for t, hx, hz in reversed(history):
            if t <= t_ms - view_delay_ms:
                d_old = math.hypot(hx - ax, hz - az)
                break
        # Park and face the kobold so the 65 deg cone contains it; only the range
        # boundary decides membership.
        yaw = math.atan2(kx - ax, kz - az)
        attacker.send_intent(0.0, yaw, tick + 2)
        # The flip: present just inside the cone, the view-delayed pose outside.
        in_flip = (
            d_old is not None
            and d_now <= boundary - FLIP_BAND
            and d_old >= boundary + FLIP_BAND
        )
        now = time.time()
        if in_flip and now - last_press_at > PRESS_GAP_S:
            now_server_ms = time.time() * 1000.0 + offset_ms
            view_ms = max(int(now_server_ms - view_delay_ms), 1)
            token = f"s10sweep{len(results)}x{int(now)}"
            result, reason = attacker.press_cast_at(
                SWEEP_ABILITY, token, view_ms, ax, ay, az, yaw
            )
            last_press_at = time.time()
            if result and "FAILED" in str(result):
                print(f"    (cast failed: {result} d_now={d_now:.1f})")
                continue
            print(
                f"    press d_now={d_now:.2f} d_old={d_old:.2f} boundary={boundary:.2f} "
                f"view_ms={view_ms} -> {result}/{reason}"
            )
            results.append((result, reason, d_now, d_old, view_ms))
        time.sleep(0.08)
    try:
        _, _, _, _, tick = attacker.physics()
        attacker.send_intent(0.0, 0.0, tick + 2)
    except RuntimeError:
        pass
    return results


def degradation_press(attacker, kobold_ref, database, caster_short):
    """A single press with view_server_time_ms=0 (no view report). The server
    must fall back to present-time only — no rewound sweep_hit evaluation
    (absent line, or a line with rewound==present and rewound_ms 0)."""
    base = len(fetch_logs(database))
    ax, ay, az, _, _ = attacker.physics()
    try:
        kx, kz = npc_position(attacker, kobold_ref["hex"])
    except RuntimeError:
        kx, kz = ax, az + 3.0
    ayaw = attacker.face(kx, kz)
    token = f"s10-degrade-{int(time.time())}"
    result, reason = attacker.press_melee_at(
        SWEEP_ABILITY, "", token, DEGRADE_VIEW_MS, ax, ay, az, ayaw
    )
    print(f"    degradation press (view_server_time_ms=0) -> {result}/{reason}")
    time.sleep(1.0)
    lines = fetch_logs(database)[base:]
    rows, _ = count_sweep_lines(lines, caster_short)
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="s10probe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    args = parser.parse_args()

    failures = []
    # Attacker presses the sweep; runner tows the kobold along a shuttle line
    # (S8 arrangement — an equal-speed chaser pins to its target at melee range,
    # so only a moving decoy makes the attacker<->kobold distance sweep).
    attacker = Probe(args.database, args.host, "attacker")
    runner = Probe(args.database, args.host, "runner")
    print(f"attacker={attacker.identity[:8]} runner={runner.identity[:8]} db={args.database}")
    time.sleep(1.0)
    caster_short = attacker.identity[:8]

    # ICE_SPIKES is a profile-agnostic AREA spell — a fresh player has mana + an
    # apprentice spellbook, but the spellbook grants a RANDOM spell set, so learn
    # it deterministically (learn_spell -> player_known_spell -> cast_request
    # authorized). The wire spell id is the action_id (ICE_SPIKES), not the
    # ability_id (SPELL_ICE_SPIKES).
    attacker.call("learn_spell", [SWEEP_ABILITY])
    time.sleep(0.6)

    # --- Check 1a: config default OFF on a fresh DB (before any set call) -----
    print("\n== config: sweep_rewind_enabled ships default OFF")
    rows = attacker.sql(
        "SELECT config_id, enabled, max_rewind_ms, auto_swing_enabled, sweep_rewind_enabled "
        "FROM combat_lag_comp_config"
    )
    default_off = not rows or rows[0][4].lower() == "false"
    expect(
        "config default sweep_rewind OFF",
        default_off,
        f"rows={rows} (absent row = shipped default: sweep rewind OFF)",
        failures,
    )
    if rows and rows[0][4].lower() != "false":
        print("  [NOTE] config row carried over from a prior run — resetting sweep OFF")

    # --- Check 1b: set ON -> row reads sweep_rewind_enabled = true -----------
    on_rows = set_lag_comp(attacker, True, True)
    expect(
        "config set ON reads back true",
        bool(on_rows) and on_rows[0][4].lower() == "true",
        f"rows={on_rows}",
        failures,
    )
    # Back to OFF for the first (would-be) leg.
    set_lag_comp(attacker, True, False)

    # Open desert gives room for the shuttle line + offset.
    for probe in (attacker, runner):
        probe.call("set_open_world_scene", ["Desert_Day"])
    time.sleep(1.5)

    offset_ms = server_clock_offset_ms(attacker)
    print(f"  server−wall clock offset: {offset_ms:.0f} ms")

    # Geometry inside the S7/S8-verified Desert_Day clear disc (spawn+(3,30),
    # r~14): the shuttle line's FOOT is the disc centre, ends at foot +/-
    # SHUTTLE_HALF_M along Z; the attacker parks OFFSET_M off the line (−X of the
    # foot). The kobold (chasing the shuttling runner) then sweeps its distance
    # to the attacker from ~OFFSET_M (foot, inside the cone) out past the
    # boundary near the ends. Everything stays within the disc (OFFSET_M,
    # SHUTTLE_HALF_M < r). boundary = authored cone range (CONE_RANGE_M — it
    # lives in progression_catalog.shared.json, not a queryable table) + radius.
    sx, _, sz, _, _ = attacker.physics()
    foot = (sx + 3.0, sz + 30.0)
    park = (foot[0] - OFFSET_M, foot[1])
    end0 = (foot[0], foot[1] - SHUTTLE_HALF_M)
    end1 = (foot[0], foot[1] + SHUTTLE_HALF_M)
    print(f"  foot={foot} park={park} shuttle ends={end0} <-> {end1}")
    attacker.move_to(*park, tolerance=1.0, timeout=90.0)
    runner.move_to(*end0, tolerance=1.5, timeout=90.0)

    kobold_ref = {"hex": spawn_kobold(runner, KOBOLD_TEMPLATE)}
    radius = npc_hit_radius(attacker, kobold_ref["hex"])
    boundary = CONE_RANGE_M + radius
    print(
        f"  kobold={kobold_ref['hex'][:8]} radius={radius:.2f} "
        f"cone range={CONE_RANGE_M:.2f} boundary={boundary:.2f} m"
    )

    shuttle = RunnerShuttle(runner, end0, end1)
    shuttle.start()
    chaser = KoboldTender(attacker, runner, kobold_ref)
    chaser.start()

    # Let the kobold acquire aggro and start sweeping (fills its history ring).
    print("  waiting for the kobold to aggro and start moving...")
    settle_deadline = time.time() + 25.0
    while time.time() < settle_deadline:
        try:
            newest_history_sample(attacker, kobold_ref["hex"])
            break
        except RuntimeError:
            pass
        time.sleep(0.5)

    # --- TEMP DIAGNOSTIC: which player is the kobold chasing? ----------------
    if os.environ.get("S10_DIAG"):
        print("=== DIAGNOSTIC: kobold aggro target (15 s) ===")
        for _ in range(15):
            try:
                ax, ay, az, _, _ = attacker.physics()
                rx, ry, rz, _, _ = runner.physics()
                kx, kz = npc_position(attacker, kobold_ref["hex"])
                da = math.hypot(kx - ax, kz - az)
                dr = math.hypot(kx - rx, kz - rz)
                print(
                    f"  kobold=({kx:.1f},{kz:.1f}) attacker=({ax:.1f},{az:.1f}) "
                    f"runner=({rx:.1f},{rz:.1f}) d_att={da:.1f} d_run={dr:.1f} "
                    f"target={'ATTACKER' if da < dr else 'runner'}"
                )
            except RuntimeError as e:
                print(f"  {e}")
            time.sleep(1.0)
        # Confirm the cone press actually dispatches (profile/loadout correct).
        try:
            kx, kz = npc_position(attacker, kobold_ref["hex"])
            ax, ay, az, _, _ = attacker.physics()
            ayaw = attacker.face(kx, kz)
            now_server_ms = time.time() * 1000.0 + offset_ms
            attacker.press_cast_at(
                SWEEP_ABILITY, "diagcast", int(now_server_ms - VIEW_DELAY_MS), ax, ay, az, ayaw
            )
            time.sleep(1.0)
            sweeps = attacker.sql("SELECT caster, spell_id FROM pending_area_impact")
            print(f"  pending_area_impact after cast: {sweeps}")
            attacker.dump_recent()
        except Exception as e:
            print(f"  diag press error: {e}")
        shuttle.stop_flag.set()
        chaser.stop_flag.set()
        sys.exit(0)

    # --- Leg 1: switch OFF — would-be flips are logged but NOT used ----------
    print("\n== sweep leg, switch OFF: would-be flips audited (enabled=false), NOT used")
    set_lag_comp(attacker, True, False)
    base_off = len(fetch_logs(args.database))
    off_presses = sweep_boundary_leg(
        attacker, kobold_ref, boundary, BOUNDARY_PRESSES, VIEW_DELAY_MS, offset_ms,
        timeout=300,
    )
    lines_off = fetch_logs(args.database)[base_off:]
    rows_off, flips_off = count_sweep_lines(lines_off, caster_short)
    off_all_disabled = all(g["enabled"] == "false" for g in rows_off)
    expect(
        "OFF: would-be flip logged (enabled=false, flip=true) but not used",
        len(rows_off) >= 1 and off_all_disabled and len(flips_off) >= 1,
        f"{len(rows_off)} sweep_hit lines ({len(flips_off)} flips); "
        f"all enabled=false: {off_all_disabled}; presses={len(off_presses)}",
        failures,
    )

    # --- Leg 2: switch ON — the rewound verdict is USED (the gate) -----------
    print("\n== sweep leg, switch ON: the rewound pose decides (present=in rewound=out excludes)")
    set_lag_comp(attacker, True, True)
    base_on = len(fetch_logs(args.database))
    on_presses = sweep_boundary_leg(
        attacker, kobold_ref, boundary, BOUNDARY_PRESSES, VIEW_DELAY_MS, offset_ms,
        timeout=360,
    )
    lines_on = fetch_logs(args.database)[base_on:]
    rows_on, flips_on = count_sweep_lines(lines_on, caster_short)
    used_flips = [g for g in flips_on if g["enabled"] == "true"]
    # The reliable polarity: the approaching kobold is presently inside the
    # cone (present=in) but its view-delayed pose was outside (rewound=out) —
    # with the switch ON the rewound=out verdict is used, so it is EXCLUDED.
    excluding_flips = [
        g for g in used_flips if g["present"] == "in" and g["rewound"] == "out"
    ]
    expect(
        "ON: rewound verdict in control (present=in rewound=out flip used)",
        len(used_flips) >= 1 and len(excluding_flips) >= 1,
        f"{len(used_flips)} used flips ({len(excluding_flips)} present=in->rewound=out excluding); "
        f"presses={len(on_presses)}",
        failures,
    )

    # --- Check 4: rewind magnitude sane + source=history on a moving kobold --
    used_rewinds = [int(g["rewound_ms"]) for g in used_flips]
    used_sources = collections.Counter(g["source"] for g in used_flips)
    rewind_ok = bool(used_rewinds) and all(150 <= r <= 280 for r in used_rewinds)
    source_ok = used_sources.get("history", 0) >= 1
    expect(
        "rewind magnitude sane + pose source=history",
        rewind_ok and source_ok,
        f"used-flip rewound_ms={sorted(used_rewinds)} (expect 150–280); "
        f"pose sources={dict(used_sources)} (expect history on a moving kobold)",
        failures,
    )

    # --- Check 5: degradation — a no-report press does NO rewound eval -------
    print("\n== degradation: a press with view_server_time_ms=0 stays present-time only")
    degrade_rows = degradation_press(attacker, kobold_ref, args.database, caster_short)
    # Either no sweep_hit line, or a line where the rewound verdict equals the
    # present verdict with rewound_ms 0 (present-time fallback, no real rewind).
    degrade_ok = all(
        g["rewound"] == g["present"] and int(g["rewound_ms"]) == 0 for g in degrade_rows
    )
    expect(
        "degradation: no-report press is present-time only",
        degrade_ok,
        f"{len(degrade_rows)} sweep_hit lines "
        f"({'none' if not degrade_rows else 'all rewound==present, rewound_ms=0'})",
        failures,
    )

    shuttle.stop_flag.set()
    chaser.stop_flag.set()
    set_lag_comp(attacker, True, False)  # leave the switch OFF behind

    finish(failures)


def finish(failures):
    print("\n== summary")
    if failures:
        print(f"FAILED: {len(failures)} check(s): {failures}")
        sys.exit(1)
    print("ALL CHECKS PASSED.")


if __name__ == "__main__":
    main()

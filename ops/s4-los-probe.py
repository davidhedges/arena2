#!/usr/bin/env python3
"""S4 live probe: LineOfSightBlocked for the previously-exempt kit.

Drives a headless websocket player (client_connected spawns a live player;
reducers ride the same socket; state read via `spacetime sql`) through the
Giant_Skeleton scene, using the skull chunk near spawn as the LOS wall:

  control — open ground: a targeted melee press is Accepted and the armed
            auto-attack emits CAST combat events (also prints the live wire
            values for combat_event rows).
  blocked — a hostile playground dummy is spawned into/behind the skull face:
            the same melee press rejects LineOfSightBlocked, the armed
            auto-attack holds (zero CASTs while armed), and a 10 m
            PALADIN_CHARGE press rejects LineOfSightBlocked instead of
            dashing.

Run against a throwaway DB — one-shot `spacetime call` cannot leave
per-identity state, and disconnect cleanup wipes the player:

  cargo build --manifest-path server/Cargo.toml --target wasm32-unknown-unknown \
      --release --features projectile_load_harness
  spacetime publish --delete-data=always --yes \
      --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm s4probe
  python3 ops/s4-los-probe.py --database s4probe
  spacetime delete s4probe

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

SCENE = "Giant_Skeleton"
# Giant_Skeleton profile spawn (server/src/open_world_scene.rs).
SPAWN = (24.946, -87.789)
# Skull chunk TFD_Giant_Skeleton_01A (2) center from
# server/src/world_data/giant_skeleton.query_collision.shared.json;
# footprint roughly x 24.6..31.8, z -85.4..-80.6. The tail piece covers a
# broad area south-west of spawn, so all probe positions stay south/east.
SKULL = (28.2, -83.0)
CONTROL_SPOT = (33.0, -95.0)     # open ground: face south, dummy at ~(33, -97.5)
CONTROL_FACE = (33.0, -105.0)
# South face of the skull, reachable from spawn without crossing the spine
# (the ridge of skeleton pieces running NW->SE blocks the east approach).
BLOCKED_SPOT = (26.5, -87.5)
CHARGE_SPOT = (30.0, -96.0)      # same sight line, inside the 5..18 m band

MELEE_ABILITY = "WARRIOR_MAIM"  # plain targeted strike, range 2.5
MELEE_STRIKE = "WARRIOR_MAIM"
CHARGE_ABILITY = "WARRIOR_CHARGE"  # LINEAR gap-close, range 5..18
CHARGE_STRIKE = "WARRIOR_CHARGE"

AUTO_ATTACK_OBSERVE_SECONDS = 5.0


class Probe:
    def __init__(self, database, host):
        self.database = database
        self.host = host
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
        # disconnect cleanup deletes the probe player). Keep a short tail of
        # reducer outcomes for debugging failed calls.
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
            print(f"    reducer: {entry}")

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
        return rows[1:] if rows else []  # drop header row

    def physics(self):
        # Playground dummies have player_physics rows too — always filter to
        # the probe's own identity.
        rows = self.sql(
            "SELECT identity, pos_x, pos_y, pos_z, yaw, last_processed_tick FROM player_physics"
        )
        for identity, x, y, z, yaw, tick in rows:
            if identity.removeprefix("0x").lower() == self.identity:
                return float(x), float(y), float(z), float(yaw), int(tick)
        raise RuntimeError("no player_physics row for the probe identity")

    def send_intent(self, forward, yaw, tick):
        self.call("send_movement_intent", [forward, 0.0, yaw, False, tick])

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
                    f"move_to({tx:.1f},{tz:.1f}) stuck at ({x:.1f},{z:.1f}) — collision en route"
                )
            last_pos = (x, z)
            yaw = math.atan2(dx, dz)
            # One intent is enough per poll: the server holds the last intent
            # until replaced (missing-command fallback). Throttle hard near the
            # target — a held full-speed intent overshoots the tolerance window
            # between polls and orbits forever.
            forward = max(0.2, min(1.0, dist / 5.0))
            self.send_intent(forward, yaw, tick + 2)
            time.sleep(0.12 if dist < 4.0 else 0.3)
        raise RuntimeError(f"move_to({tx:.1f},{tz:.1f}) timed out")

    def move_along(self, waypoints):
        for wx, wz in waypoints:
            self.move_to(wx, wz)

    def move_until_blocked(self, tx, tz, timeout=20.0):
        """Walk toward a point until movement collision stops us — leaves the
        player flush against the obstacle, no geometry knowledge needed."""
        deadline = time.time() + timeout
        last_pos = None
        while time.time() < deadline:
            x, _, z, _, tick = self.physics()
            if last_pos is not None and math.hypot(x - last_pos[0], z - last_pos[1]) < 0.05:
                self.send_intent(0.0, math.atan2(tx - x, tz - z), tick + 2)
                time.sleep(0.4)
                return
            last_pos = (x, z)
            self.send_intent(0.6, math.atan2(tx - x, tz - z), tick + 2)
            time.sleep(0.4)
        raise RuntimeError("move_until_blocked never hit an obstacle")

    def spawn_hostile_dummy(self):
        self.call("spawn_playground_target", ["HOSTILE"])
        time.sleep(1.0)
        # No WHERE: this CLI build mismatches quoted string predicates on some
        # tables; filter client-side instead.
        rows = self.sql("SELECT kind, identity FROM playground_target")
        hostile = [r for r in rows if r[0] == "HOSTILE"]
        if not hostile:
            self.dump_recent()
            raise RuntimeError(f"hostile playground target did not spawn (rows: {rows})")
        return hostile[0][1].removeprefix("0x")

    def dummy_position(self, dummy_hex):
        rows = self.sql("SELECT identity, pos_x, pos_z FROM player_physics")
        for identity, px, pz in rows:
            if identity.removeprefix("0x").lower() == dummy_hex.lower():
                return float(px), float(pz)
        raise RuntimeError("dummy has no player_physics row")

    def press_melee(self, strike_id, target_hex, token):
        x, y, z, yaw, _ = self.physics()
        self.action_seq += 1
        self.call(
            "melee_attack",
            [strike_id, target_hex, x, y, z, yaw, token, self.action_seq],
        )
        time.sleep(0.8)
        return self.prediction_result(token)

    def prediction_result(self, token):
        rows = self.sql(
            "SELECT predicted_action_id, result, reject_reason FROM predicted_action_result"
        )
        for row_token, result, reason in rows:
            if row_token == token:
                return enum_tag(result), enum_tag(reason)
        return None, None

    def combat_event_summary(self):
        return self.sql(
            "SELECT action_kind, event_type, ability_id, created_at_micros FROM combat_event"
        )


def enum_tag(cell):
    """`spacetime sql` renders enum cells as `(variantName = ())`; return the
    bare variant tag. Verified live 2026-07-04 against predicted_action_result."""
    cell = cell.strip()
    if cell.startswith("(") and "=" in cell:
        return cell[1:].split("=", 1)[0].strip()
    return cell


def expect(label, actual, expected, failures):
    ok = (actual or "").lower() == expected.lower()
    print(f"  [{'PASS' if ok else 'FAIL'}] {label}: got {actual!r}, expected {expected!r}")
    if not ok:
        failures.append(label)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="s4probe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    args = parser.parse_args()

    probe = Probe(args.database, args.host)
    failures = []
    print(f"Connected as {probe.identity} to {args.database}")
    time.sleep(1.0)

    print(f"\n== setup: move to {SCENE}")
    probe.call("set_open_world_scene", [SCENE])
    time.sleep(1.0)
    x, _, z, _, _ = probe.physics()
    print(f"  spawned at ({x:.1f}, {z:.1f})")
    # A fresh headless identity has no character_action_bar_assignment rows,
    # and melee dispatch requires the pressed ability to be on the bar.
    probe.call("assign_character_action_bar_ability_to_slot", ["SLOT_0_1", MELEE_ABILITY])
    probe.call("assign_character_action_bar_ability_to_slot", ["SLOT_1_1", CHARGE_ABILITY])
    time.sleep(0.6)

    print("\n== control: open ground south of the skeleton")
    probe.move_to(*CONTROL_SPOT)
    probe.face(*CONTROL_FACE)
    dummy = probe.spawn_hostile_dummy()
    dpos = probe.dummy_position(dummy)
    print(f"  dummy {dummy[:8]} at ({dpos[0]:.1f}, {dpos[1]:.1f})")
    result, reason = probe.press_melee(MELEE_STRIKE, dummy, "s4-melee-control")
    if result != "Accepted":
        print(f"  (control melee reason: {reason!r})")
        probe.dump_recent()
    expect("control melee press", result, "Accepted", failures)

    probe.call("arm_auto_attack_target", [dummy])
    time.sleep(AUTO_ATTACK_OBSERVE_SECONDS)
    probe.call("clear_auto_attack_target", [])
    control_events = probe.combat_event_summary()
    control_casts = [r for r in control_events if r[1] == "COMBAT_CAST"]
    print(f"  combat_event rows while armed (wire values, live):")
    for r in control_events[:8]:
        print(f"    action_kind={r[0]} event_type={r[1]} ability_id={r[2]}")
    ok = len(control_casts) >= 1
    print(f"  [{'PASS' if ok else 'FAIL'}] control auto-attack: {len(control_casts)} CAST rows while armed")
    if not ok:
        failures.append("control auto-attack CASTs")

    print("\n== blocked: dummy spawned through the nearest wall")
    probe.move_to(*BLOCKED_SPOT)
    probe.move_until_blocked(*SKULL)  # walk flush against the wall face
    probe.face(*SKULL)
    dummy = probe.spawn_hostile_dummy()
    dpos = probe.dummy_position(dummy)
    print(f"  dummy {dummy[:8]} at ({dpos[0]:.1f}, {dpos[1]:.1f})")

    result, reason = probe.press_melee(MELEE_STRIKE, dummy, "s4-melee-blocked")
    expect("blocked melee press result", result, "Rejected", failures)
    expect("blocked melee press reason", reason, "LineOfSightBlocked", failures)

    baseline = len(probe.combat_event_summary())
    probe.call("arm_auto_attack_target", [dummy])
    time.sleep(AUTO_ATTACK_OBSERVE_SECONDS)
    probe.call("clear_auto_attack_target", [])
    # Only count rows newer than the arm (the 20 s window may still hold
    # control-phase rows).
    blocked_events = probe.combat_event_summary()
    new_rows = blocked_events[baseline:] if len(blocked_events) >= baseline else blocked_events
    new_casts = [r for r in new_rows if r[1] == "COMBAT_CAST"]
    ok = len(new_casts) == 0
    print(f"  [{'PASS' if ok else 'FAIL'}] blocked auto-attack held: {len(new_casts)} new CAST rows while armed")
    if not ok:
        failures.append("blocked auto-attack held")

    px, _, pz, _, _ = probe.physics()
    ddx, ddz = dpos[0] - px, dpos[1] - pz
    print(f"  melee/auto distance to dummy: {math.hypot(ddx, ddz):.2f} m")

    probe.move_to(*CHARGE_SPOT)
    probe.face(*dpos)
    px, _, pz, _, _ = probe.physics()
    charge_dist = math.hypot(dpos[0] - px, dpos[1] - pz)
    print(f"  charge distance to dummy: {charge_dist:.2f} m (need 5.5..18)")
    result, reason = probe.press_melee(CHARGE_STRIKE, dummy, "s4-charge-blocked")
    expect("blocked gap-close press result", result, "Rejected", failures)
    expect("blocked gap-close press reason", reason, "LineOfSightBlocked", failures)

    print("\n== summary")
    if failures:
        print(f"FAILED: {len(failures)} check(s): {failures}")
        sys.exit(1)
    print("ALL CHECKS PASSED — LineOfSightBlocked fires for melee, gap-close, "
          "and the armed auto-attack holds behind cover.")


if __name__ == "__main__":
    main()

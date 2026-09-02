#!/usr/bin/env python3
"""Live acceptance probe: Breakaway is a self-directed movement action.

Drives a headless websocket player and presses Breakaway through the same
cast_request path the action bar uses, then reads the production tables to
confirm the disengage contract on the running module:

  movement-action  - the press opens a MovementActionState of kind BACKSTEP.
                     A melee strike would open none.
  special-movement - it opens a special_movement_runtime, i.e. the server is
                     authoring the travel rather than leaving it to physics.
  travels-backward - the player ends ~7m behind where it started, measured
                     against its own facing at the press.
  lands-and-stops  - once the runtime clears, horizontal velocity is zero and
                     the position stops changing. A retained axis or residual
                     authored velocity would keep sliding.
  no-damage        - no combat event is emitted for the caster. A disengage
                     that still borrows a melee strike would emit one.

Run against a throwaway DB:

  cargo build --manifest-path server/Cargo.toml \
    --target wasm32-unknown-unknown --release \
    --features projectile_load_harness
  spacetime publish --delete-data=always --yes -s local \
    --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm bkprobe
  spacetime call -s local bkprobe enable_local_direct_mode
  python3 ops/breakaway-probe.py --database bkprobe
  spacetime delete --yes -s local bkprobe
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

BREAKAWAY = "DAGGER_BREAKAWAY"
AUTHORED_DISTANCE_M = 7.0
POLL_SECONDS = 0.02
SETTLE_SECONDS = 0.6
STILL_TOLERANCE_M = 0.02
STILL_TOLERANCE_MPS = 0.05


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
        self.ws.settimeout(None)
        self.failures = collections.deque(maxlen=40)
        threading.Thread(target=self._drain, daemon=True).start()

    def _drain(self):
        try:
            while True:
                message = self.ws.recv()
                if "TransactionUpdate" not in message:
                    continue
                try:
                    update = json.loads(message)["TransactionUpdate"]
                    status = update.get("status", {})
                    name = update.get("reducer_call", {}).get("reducer_name", "?")
                    if "Failed" in status:
                        self.failures.append(f"{name}: {status['Failed']}")
                except Exception:
                    self.failures.append(message[:200])
        except Exception as e:
            self.failures.append(f"drain died: {type(e).__name__}: {e}")

    def call(self, reducer, args):
        self.request_id += 1
        self.ws.send(json.dumps({"CallReducer": {
            "reducer": reducer, "args": json.dumps(args),
            "request_id": self.request_id, "flags": 0}}))

    def sql(self, query):
        result = subprocess.run(
            ["spacetime", "sql", self.database, query],
            capture_output=True, text=True)
        if result.returncode != 0:
            return []
        rows = []
        for line in result.stdout.splitlines():
            if "|" not in line or set(line.strip()) <= {"-", "+", "|", " "}:
                continue
            rows.append([c.strip().strip('"') for c in line.split("|")])
        return rows[1:] if rows else []

    def mine(self, rows, col=0):
        for row in rows:
            if row[col].removeprefix("0x").lower() == self.identity:
                return row
        return None

    def physics(self):
        row = self.mine(self.sql(
            "SELECT identity, pos_x, pos_y, pos_z, yaw, vel_x, vel_z, last_processed_tick "
            "FROM player_physics"))
        if row is None:
            raise RuntimeError("no player_physics row")
        return {
            "pos": (float(row[1]), float(row[2]), float(row[3])),
            "yaw": float(row[4]),
            "vel": (float(row[5]), float(row[6])),
            "tick": int(float(row[7])),
        }

    def movement_action(self):
        return self.mine(self.sql(
            "SELECT owner, kind, ability_id FROM movement_action_state"))

    def special_movement(self):
        return self.mine(self.sql(
            "SELECT owner, kind FROM special_movement_runtime"))

    def combat_events_for_me(self):
        rows = self.sql("SELECT caster, event_type, ability_id FROM combat_event")
        return [r for r in rows
                if r[0].removeprefix("0x").lower() == self.identity]

    def send_intent(self, forward, strafe, yaw, tick):
        self.call("send_movement_intent", [forward, strafe, yaw, False, tick])

    def cast(self, spell):
        p = self.physics()
        x, y, z = p["pos"]
        self.action_seq += 1
        self.call("cast_request", [
            spell, "", x, y, z, 0, x, y, z, p["yaw"],
            f"bk-probe-{self.action_seq}", self.action_seq, 0])


def check(name, ok, detail):
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    return ok


def wait_for_player(probe, timeout=20.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            return probe.physics()
        except RuntimeError:
            time.sleep(0.3)
    raise RuntimeError("probe player never spawned")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", required=True)
    parser.add_argument("--host", default="127.0.0.1:3000")
    args = parser.parse_args()

    probe = Probe(args.database, args.host)
    print(f"probe identity: {probe.identity}")
    wait_for_player(probe)

    configure_probe_combat_build(probe, [BREAKAWAY])
    probe.call("activate_combat_build_discipline", ["DAGGERS"])
    time.sleep(0.6)

    # Walk forward first so the ground behind the press is proven open.
    yaw = probe.physics()["yaw"]
    walk_deadline = time.time() + 1.6
    while time.time() < walk_deadline:
        tick = probe.physics()["tick"] + 3
        for _ in range(8):
            if time.time() >= walk_deadline:
                break
            probe.send_intent(1.0, 0.0, yaw, tick)
            tick += 1
            time.sleep(0.033)
    probe.send_intent(0.0, 0.0, yaw, probe.physics()["tick"] + 3)
    time.sleep(0.4)

    before = probe.physics()
    events_before = len(probe.combat_events_for_me())
    probe.cast(BREAKAWAY)

    # Catch the runtime while it is open.
    saw_movement_action = None
    saw_special_movement = None
    deadline = time.time() + 3.0
    while time.time() < deadline:
        saw_movement_action = saw_movement_action or probe.movement_action()
        saw_special_movement = saw_special_movement or probe.special_movement()
        if saw_movement_action and saw_special_movement:
            break
        time.sleep(POLL_SECONDS)

    # Let it finish and settle.
    deadline = time.time() + 5.0
    while time.time() < deadline and probe.special_movement() is not None:
        time.sleep(POLL_SECONDS)
    landed = probe.physics()
    time.sleep(SETTLE_SECONDS)
    settled = probe.physics()

    ok = True
    ok &= check(
        "movement-action",
        saw_movement_action is not None
        and saw_movement_action[1].upper() == "BACKSTEP",
        f"row={saw_movement_action}")
    ok &= check(
        "special-movement",
        saw_special_movement is not None,
        f"row={saw_special_movement}")

    dx = landed["pos"][0] - before["pos"][0]
    dz = landed["pos"][2] - before["pos"][2]
    travelled = math.hypot(dx, dz)
    back_x, back_z = -math.sin(before["yaw"]), -math.cos(before["yaw"])
    backward_component = dx * back_x + dz * back_z
    ok &= check(
        "travels-backward",
        travelled >= 0.9 * AUTHORED_DISTANCE_M
        and backward_component > 0.95 * travelled,
        f"travelled {travelled:.2f}m of an authored {AUTHORED_DISTANCE_M}m over "
        f"proven-open ground, backward component {backward_component:.2f}m")

    drift = math.dist(landed["pos"], settled["pos"])
    vel = math.hypot(*settled["vel"])
    ok &= check(
        "lands-and-stops",
        drift <= STILL_TOLERANCE_M and vel <= STILL_TOLERANCE_MPS,
        f"drift {drift:.4f}m over {SETTLE_SECONDS}s after arrival, "
        f"settled horizontal speed {vel:.4f} m/s")

    events_after = probe.combat_events_for_me()
    new_events = [e for e in events_after[events_before:]]
    ok &= check(
        "no-damage",
        len(events_after) == events_before,
        f"{len(new_events)} combat events emitted by the caster {new_events[:3]}")

    for failure in probe.failures:
        print(f"    reducer failure: {failure}")
    print("\nRESULT:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())

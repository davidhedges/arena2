#!/usr/bin/env python3
"""Live acceptance probe for Cauterize's dispel filter and assist-cost burn.

Two ordinary websocket players in an arena instance. The attacker lands a
Graveburst (BLEED-tagged burn + SLOW), the victim self-casts Cauterize, and the
probe reads the production tables to confirm:

  * the BLEED-tagged status is cleansed while the SLOW ride-along survives,
  * the cast sears the cauterized target for 5% of their maximum health,
  * repeated casts floor the target at 1 HP instead of defeating them.

Typical isolated local run:

  cargo build --manifest-path server/Cargo.toml \
    --target wasm32-unknown-unknown --release
  spacetime publish --delete-data=always --yes -s local \
    --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm \
    cauterizeprobe
  python3 ops/cauterize-probe.py --database cauterizeprobe

Requires the `websocket-client` package used by the existing S4-S10 probes.
"""

import argparse
import collections
import json
import math
import threading
import time
import urllib.request

import websocket


CAUTERIZE = "CAUTERIZE"
GRAVEBURST = "GRAVEBURST"
BURN_FRACTION = 0.05


def normalize_identity(value):
    if isinstance(value, dict):
        value = value.get("__identity__", value.get("identity", ""))
    if isinstance(value, list) and len(value) == 1:
        value = value[0]
    return str(value or "").removeprefix("0x").lower()


def option_value(value):
    if isinstance(value, list) and len(value) == 2 and isinstance(value[0], int):
        return value[1] if value[0] == 0 else None
    if isinstance(value, list):
        return value[0] if value else None
    if isinstance(value, dict) and "some" in value:
        return value["some"]
    return value


def wait_until(label, predicate, timeout=12.0, interval=0.05):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            result = predicate()
        except Exception:
            result = None
        if result:
            return result
        time.sleep(interval)
    raise RuntimeError(f"timed out waiting for {label}")


def expect(label, condition, detail, failures):
    status = "PASS" if condition else "FAIL"
    print(f"  [{status}] {label}: {detail}")
    if not condition:
        failures.append(label)


class Probe:
    def __init__(self, database, host, server_url, name):
        self.database = database
        self.server_url = server_url.rstrip("/")
        self.name = name
        self.request_id = 0
        self.action_seq = 0
        self.recent = collections.deque(maxlen=80)
        self.failures = collections.deque(maxlen=20)
        self.ws = websocket.create_connection(
            f"ws://{host}/v1/database/{database}/subscribe",
            subprotocols=["v1.json.spacetimedb"],
            timeout=8,
        )
        first = json.loads(self.ws.recv())
        token = first.get("IdentityToken", {})
        self.identity = normalize_identity(token.get("identity", ""))
        if not self.identity:
            raise RuntimeError(f"{name}: subscription returned no identity")
        self.ws.settimeout(None)
        self._closed = False
        self._drain = threading.Thread(target=self._drain_loop, daemon=True)
        self._drain.start()

    def _drain_loop(self):
        try:
            while True:
                message = self.ws.recv()
                if "TransactionUpdate" not in message:
                    continue
                try:
                    update = json.loads(message)["TransactionUpdate"]
                    reducer = update.get("reducer_call", {}).get("reducer_name", "?")
                    status = update.get("status", {})
                    if "Failed" in status:
                        entry = f"{reducer}: {status['Failed']}"
                        self.failures.append(entry)
                        self.recent.append(f"FAILED {entry}")
                    else:
                        self.recent.append(f"{reducer}: {next(iter(status), '?')}")
                except Exception:
                    self.recent.append(message[:240])
        except Exception as error:
            if not self._closed:
                self.recent.append(f"drain stopped: {type(error).__name__}: {error}")

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
        request = urllib.request.Request(
            f"{self.server_url}/v1/database/{self.database}/sql",
            data=query.encode("utf-8"),
            headers={"Content-Type": "text/plain"},
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=8) as response:
            statements = json.loads(response.read().decode("utf-8"))
        return statements[0].get("rows", []) if statements else []

    def physics(self, identity=None):
        identity = normalize_identity(identity or self.identity)
        rows = self.sql(
            "SELECT identity, pos_x, pos_y, pos_z, yaw, last_processed_tick "
            "FROM player_physics"
        )
        for row_identity, x, y, z, yaw, tick in rows:
            if normalize_identity(row_identity) == identity:
                return float(x), float(y), float(z), float(yaw), int(tick)
        raise RuntimeError(f"{self.name}: no player_physics row for {identity[:8]}")

    def player_state(self, identity=None):
        identity = normalize_identity(identity or self.identity)
        rows = self.sql("SELECT player_id, hp, max_hp, alive FROM player_state")
        for row_identity, hp, max_hp, alive in rows:
            if normalize_identity(row_identity) == identity:
                return int(hp), int(max_hp), bool(alive)
        raise RuntimeError(f"{self.name}: no player_state row for {identity[:8]}")

    def statuses(self, identity=None):
        identity = normalize_identity(identity or self.identity)
        rows = self.sql(
            "SELECT target, effect_kind, stack_group FROM status_effect"
        )
        return sorted(
            (str(kind), str(group))
            for target, kind, group in rows
            if normalize_identity(target) == identity
        )

    def move_to(self, target_x, target_z, tolerance=0.35, timeout=30.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            x, _, z, _, tick = self.physics()
            dx = target_x - x
            dz = target_z - z
            distance = math.hypot(dx, dz)
            yaw = math.atan2(dx, dz)
            if distance <= tolerance:
                self.call("send_movement_intent", [0.0, 0.0, yaw, False, tick + 2])
                time.sleep(0.25)
                return
            self.call(
                "send_movement_intent",
                [min(1.0, max(0.2, distance / 4.0)), 0.0, yaw, False, tick + 2],
            )
            time.sleep(0.09)
        raise RuntimeError(f"{self.name}: move_to timed out")

    def face(self, target_x, target_z):
        x, _, z, _, tick = self.physics()
        yaw = math.atan2(target_x - x, target_z - z)
        self.call("send_movement_intent", [0.0, 0.0, yaw, False, tick + 2])
        time.sleep(0.2)
        return yaw

    def cast(self, spell, target="", aim=None):
        x, y, z, yaw, _ = self.physics()
        aim_x, aim_y, aim_z = aim if aim else (x, y, z)
        self.action_seq += 1
        self.call(
            "cast_request",
            [
                spell,
                target,
                aim_x,
                aim_y,
                aim_z,
                0,
                x,
                y,
                z,
                yaw,
                f"cauterize-probe-{self.name}-{self.action_seq}",
                self.action_seq,
                0,
            ],
        )

    def assert_no_reducer_failures(self):
        if self.failures:
            raise RuntimeError(f"{self.name} reducer failures: {list(self.failures)}")

    def close(self):
        if self._closed:
            return
        self._closed = True
        try:
            self.ws.close()
        except Exception:
            pass


def bleed_statuses(probe, identity):
    return [
        entry for entry in probe.statuses(identity) if "BLEED" in entry[1].upper()
    ]


def slow_statuses(probe, identity):
    return [entry for entry in probe.statuses(identity) if "SLOW" in entry[1].upper()]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="cauterizeprobe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    parser.add_argument("--server-url", default="http://127.0.0.1:3000")
    args = parser.parse_args()

    failures = []
    attacker = Probe(args.database, args.host, args.server_url, "attacker")
    victim = None
    print(f"attacker={attacker.identity[:8]} database={args.database}")
    try:
        wait_until("attacker initialization", lambda: attacker.physics())
        attacker.call("learn_spell", [GRAVEBURST])
        victim = Probe(args.database, args.host, args.server_url, "victim")
        wait_until("victim initialization", lambda: victim.physics())
        victim.call("learn_spell", [CAUTERIZE])
        time.sleep(0.7)
        print(f"victim={victim.identity[:8]}")

        before_ids = {int(row[0]) for row in attacker.sql("SELECT id FROM arena_instance")}
        attacker.call("create_instance", [2])
        arena_id = wait_until(
            "arena creation",
            lambda: next(
                (
                    int(row[0])
                    for row in attacker.sql("SELECT id FROM arena_instance")
                    if int(row[0]) not in before_ids
                ),
                None,
            ),
        )
        attacker.call("join_instance", [arena_id])
        victim.call("join_instance", [arena_id])
        time.sleep(0.5)
        wait_until(
            "victim joins arena",
            lambda: next(
                (
                    True
                    for row in attacker.sql("SELECT identity, instance_id FROM player_world")
                    if normalize_identity(row[0]) == victim.identity
                    and int(option_value(row[1])) == arena_id
                ),
                False,
            ),
        )
        attacker.move_to(0.0, 0.0)
        victim.move_to(0.0, 4.0)
        attacker.face(0.0, 4.0)
        attacker.call("start_match", [arena_id])
        time.sleep(0.5)

        print("\n== Graveburst applies a BLEED-tagged debuff plus a SLOW ride-along")
        vx, vy, vz, _, _ = victim.physics()
        attacker.cast(GRAVEBURST, aim=(vx, vy, vz))
        wait_until(
            "graveburst bleed lands",
            lambda: bool(bleed_statuses(victim, victim.identity)),
        )
        expect(
            "victim carries a BLEED-tagged status and a SLOW",
            bool(bleed_statuses(victim, victim.identity))
            and bool(slow_statuses(victim, victim.identity)),
            f"statuses={victim.statuses(victim.identity)}",
            failures,
        )

        print("\n== Cauterize cleanses the bleed and sears its target")
        hp_before, max_hp, _ = victim.player_state()
        expected_burn = max(1, round(max_hp * BURN_FRACTION))
        victim.cast(CAUTERIZE, target=f"0x{victim.identity}")
        wait_until(
            "cauterize resolves",
            lambda: victim.player_state()[0] != hp_before,
            timeout=6.0,
        )
        time.sleep(0.4)
        hp_after, _, alive = victim.player_state()
        remaining_bleeds = bleed_statuses(victim, victim.identity)
        remaining_slows = slow_statuses(victim, victim.identity)
        expect(
            "BLEED-tagged status removed",
            not remaining_bleeds,
            f"remaining={remaining_bleeds}",
            failures,
        )
        expect(
            "non-matching SLOW survives the dispel filter",
            bool(remaining_slows),
            f"remaining={remaining_slows}",
            failures,
        )
        expect(
            "burn costs the target 5% of maximum health",
            hp_before - hp_after == expected_burn and alive,
            f"hp={hp_before}->{hp_after} (expected -{expected_burn} of {max_hp} max) alive={alive}",
            failures,
        )

        print("\n== repeated casts floor the target at 1 HP")
        floor_hp = None
        for _ in range(40):
            victim.cast(CAUTERIZE, target=f"0x{victim.identity}")
            time.sleep(1.35)
            hp, _, alive = victim.player_state()
            if not alive:
                floor_hp = hp
                break
            if hp <= 1:
                floor_hp = hp
                # One more cast against the floor to prove it never crosses it.
                victim.cast(CAUTERIZE, target=f"0x{victim.identity}")
                time.sleep(1.35)
                floor_hp, _, alive = victim.player_state()
                break
        hp, _, alive = victim.player_state()
        expect(
            "assist-cost burn stops at 1 HP and never defeats the target",
            floor_hp == 1 and hp == 1 and alive,
            f"floor={floor_hp} hp={hp} alive={alive}",
            failures,
        )

        attacker.assert_no_reducer_failures()
        victim.assert_no_reducer_failures()
    except Exception as error:
        print(f"\nprobe aborted: {type(error).__name__}: {error}")
        if attacker:
            print(f"attacker recent: {list(attacker.recent)[-6:]}")
        if victim:
            print(f"victim recent: {list(victim.recent)[-6:]}")
        failures.append(f"probe aborted: {error}")
    finally:
        if victim:
            victim.close()
        attacker.close()

    print("\n=== RESULT ===")
    if failures:
        print(f"FAIL ({len(failures)}): {failures}")
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

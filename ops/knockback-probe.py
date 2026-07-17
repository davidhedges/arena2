#!/usr/bin/env python3
"""Live acceptance probe for docs/knockback-design-2026-07-17.md.

The probe uses two ordinary websocket players plus authored NPC fixtures. The
only test-only reducers are compiled behind the repo's existing
`projectile_load_harness` feature: one queues a zero-damage first-class
knockback packet, and one equips legitimate max-roll resistance affixes on the
victim's current armor/jewelry. All displacement, resistance, collision,
composition, preemption, combat-entry, and teardown behavior runs through the
production paths.

Typical isolated local run:

  cargo build --manifest-path server/Cargo.toml \
    --target wasm32-unknown-unknown --release \
    --features projectile_load_harness
  spacetime publish --delete-data=always --yes -s local \
    --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm \
    knockbackprobe
  python3 ops/knockback-probe.py --database knockbackprobe

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


SHOCKWAVE = "SHOCKWAVE"
KOBOLD_TEMPLATE = "KOBOLD_WARRIOR_RD_SWORD_SHIELD"
KOBOLD_VISUAL = "KOBOLD_WARRIOR_RD"
IMMUNE_TEMPLATE = "BONE_GOLEM"
IMMUNE_VISUAL = "BONE_GOLEM_BL"
KNOCKBACK_AFFIX = "AFFIX_KNOCKBACK_RESISTANCE_MINOR"


def normalize_identity(value):
    if isinstance(value, dict):
        value = value.get("__identity__", value.get("identity", ""))
    if isinstance(value, list) and len(value) == 1:
        value = value[0]
    return str(value or "").removeprefix("0x").lower()


def option_value(value):
    if (
        isinstance(value, list)
        and len(value) == 2
        and isinstance(value[0], int)
    ):
        return value[1] if value[0] == 0 else None
    if isinstance(value, list):
        return value[0] if value else None
    if isinstance(value, dict) and "some" in value:
        return value["some"]
    return value


def wire_identity(value):
    return {"__identity__": f"0x{normalize_identity(value)}"}


def wait_until(label, predicate, timeout=12.0, interval=0.03):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            last = predicate()
        except (RuntimeError, TimeoutError) as error:
            last = str(error)
            time.sleep(interval)
            continue
        if last:
            return last
        time.sleep(interval)
    raise RuntimeError(f"timed out waiting for {label}; last={last}")


def expect(label, condition, detail, failures):
    print(f"  [{'PASS' if condition else 'FAIL'}] {label}: {detail}")
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
        raise RuntimeError(
            f"{self.name}: no player_physics row for {identity[:8]}; rows={rows[:3]}"
        )

    def npc_physics(self, identity):
        identity = normalize_identity(identity)
        rows = self.sql("SELECT identity, pos_x, pos_y, pos_z, yaw FROM npc_physics")
        for row_identity, x, y, z, yaw in rows:
            if normalize_identity(row_identity) == identity:
                return float(x), float(y), float(z), float(yaw), 0
        raise RuntimeError(f"{self.name}: no npc_physics row for {identity[:8]}")

    def player_hp(self, identity):
        identity = normalize_identity(identity)
        for row_identity, hp in self.sql("SELECT player_id, hp FROM player_state"):
            if normalize_identity(row_identity) == identity:
                return int(hp)
        raise RuntimeError(f"{self.name}: no player_state row for {identity[:8]}")

    def runtime(self, identity):
        identity = normalize_identity(identity)
        rows = self.sql(
            "SELECT owner, kind, start_x, start_y, start_z, end_x, end_y, end_z "
            "FROM special_movement_runtime"
        )
        for row in rows:
            if normalize_identity(row[0]) == identity:
                return row
        return None

    def movement_action(self, identity):
        identity = normalize_identity(identity)
        rows = self.sql("SELECT owner, kind FROM movement_action_state")
        return next((row for row in rows if normalize_identity(row[0]) == identity), None)

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
        raise RuntimeError(
            f"{self.name}: move_to({target_x:.1f},{target_z:.1f}) timed out"
        )

    def face(self, target_x, target_z):
        x, _, z, _, tick = self.physics()
        yaw = math.atan2(target_x - x, target_z - z)
        self.call("send_movement_intent", [0.0, 0.0, yaw, False, tick + 2])
        time.sleep(0.2)
        return yaw

    def cast_shockwave(self):
        x, y, z, yaw, _ = self.physics()
        self.action_seq += 1
        token = f"knockback-probe-cast-{self.action_seq}"
        self.call(
            "cast_request",
            [
                SHOCKWAVE,
                "",
                x + math.sin(yaw) * 2.0,
                y,
                z + math.cos(yaw) * 2.0,
                0,
                x,
                y,
                z,
                yaw,
                token,
                self.action_seq,
                0,
            ],
        )

    def zero_damage_shove(self, target, distance=4.0):
        self.call("run_knockback_probe_shove", [wire_identity(target), distance])

    def start_dodge(self):
        x, y, z, yaw, tick = self.physics()
        self.action_seq += 1
        self.call(
            "start_dodge",
            [
                tick + 1,
                tick,
                x,
                y,
                z,
                yaw,
                1.0,
                0.0,
                f"knockback-probe-dodge-{self.action_seq}",
                self.action_seq,
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


def spawn_npc(probe, template, visual):
    before = {
        normalize_identity(row[0])
        for row in probe.sql("SELECT identity, template_id FROM npc_instance")
    }
    probe.call("spawn_npc", [template, visual, "HOSTILE"])
    return wait_until(
        f"{template} spawn",
        lambda: next(
            (
                normalize_identity(row[0])
                for row in probe.sql("SELECT identity, template_id FROM npc_instance")
                if row[1] == template and normalize_identity(row[0]) not in before
            ),
            None,
        ),
    )


def despawn_npc(probe, identity):
    probe.call("despawn_npc", [wire_identity(identity)])
    wait_until(
        "NPC despawn",
        lambda: all(
            normalize_identity(row[0]) != normalize_identity(identity)
            for row in probe.sql("SELECT identity FROM npc_instance")
        ),
    )


def sample_displacement(probe, identity, getter, trigger, seconds=0.8):
    start = getter(identity)
    source = probe.physics()
    dir_x = start[0] - source[0]
    dir_z = start[2] - source[2]
    length = math.hypot(dir_x, dir_z)
    if length <= 0.001:
        dir_x, dir_z = math.sin(start[3]), math.cos(start[3])
    else:
        dir_x, dir_z = dir_x / length, dir_z / length

    trigger()
    deadline = time.time() + seconds
    max_projection = 0.0
    max_lateral = 0.0
    observed_kinds = set()
    max_track_distance = 0.0
    while time.time() < deadline:
        current = getter(identity)
        dx = current[0] - start[0]
        dz = current[2] - start[2]
        projection = dx * dir_x + dz * dir_z
        lateral = abs(dx * -dir_z + dz * dir_x)
        max_projection = max(max_projection, projection)
        max_lateral = max(max_lateral, lateral)
        runtime = probe.runtime(identity)
        if runtime:
            observed_kinds.add(str(runtime[1]))
            track_distance = math.hypot(
                float(runtime[5]) - float(runtime[2]),
                float(runtime[7]) - float(runtime[4]),
            )
            max_track_distance = max(max_track_distance, track_distance)
        time.sleep(0.015)
    return {
        "projection": max_projection,
        "lateral": max_lateral,
        "kinds": observed_kinds,
        "track_distance": max_track_distance,
        "start": start,
    }


def equipment_resistance(probe, owner):
    owner = normalize_identity(owner)
    owned_items = {
        str(row[0])
        for row in probe.sql("SELECT item_instance_id, current_owner FROM item_instance")
        if normalize_identity(option_value(row[1])) == owner
    }
    return sum(
        float(row[2])
        for row in probe.sql(
            "SELECT item_instance_id, affix_id, value FROM item_affix_instance"
        )
        if str(row[0]) in owned_items and row[1] == KNOCKBACK_AFFIX
    )


def engagement_reason(probe, owner):
    owner = normalize_identity(owner)
    for row_owner, reason in probe.sql("SELECT owner, reason FROM combat_engagement"):
        if normalize_identity(row_owner) == owner:
            return str(reason)
    return ""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="knockbackprobe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    parser.add_argument("--server-url", default="http://127.0.0.1:3000")
    args = parser.parse_args()

    failures = []
    attacker = Probe(args.database, args.host, args.server_url, "attacker")
    victim = None
    print(f"attacker={attacker.identity[:8]} database={args.database}")
    try:
        wait_until("attacker initialization", lambda: attacker.physics())
        attacker.call("set_open_world_scene", ["Desert_Day"])
        attacker.call("learn_spell", [SHOCKWAVE])
        time.sleep(0.7)
        spawn_x, _, spawn_z, _, _ = attacker.physics()
        attacker.face(spawn_x, spawn_z + 10.0)

        print("\n== authored Shockwave: NPC displacement + composition")
        kobold = spawn_npc(attacker, KOBOLD_TEMPLATE, KOBOLD_VISUAL)
        motion = sample_displacement(
            attacker,
            kobold,
            attacker.npc_physics,
            attacker.cast_shockwave,
        )
        expect(
            "NPC moves outward by authored knockback",
            3.45 <= motion["projection"] <= 4.25 and motion["lateral"] < 0.35,
            f"projection={motion['projection']:.3f}m lateral={motion['lateral']:.3f}m",
            failures,
        )
        expect(
            "stagger + knockback composes to knockback distance",
            motion["projection"] > 3.0,
            f"projection={motion['projection']:.3f}m (stagger-only would be 0.45m)",
            failures,
        )
        despawn_npc(attacker, kobold)

        print("\n== authored heavy immunity")
        time.sleep(2.1)
        immune = spawn_npc(attacker, IMMUNE_TEMPLATE, IMMUNE_VISUAL)
        immune_motion = sample_displacement(
            attacker,
            immune,
            attacker.npc_physics,
            attacker.cast_shockwave,
        )
        expect(
            "immune heavy remains unmoved",
            immune_motion["projection"] < 0.12,
            f"projection={immune_motion['projection']:.3f}m",
            failures,
        )
        despawn_npc(attacker, immune)

        print("\n== hostile player fixture")
        victim = Probe(args.database, args.host, args.server_url, "victim")
        wait_until("victim initialization", lambda: victim.physics())
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
        if victim.failures:
            raise RuntimeError(f"victim join failed: {list(victim.failures)}")
        wait_until(
            "victim joins arena",
            lambda: next(
                (
                    True
                    for row in attacker.sql(
                        "SELECT identity, instance_id FROM player_world"
                    )
                    if normalize_identity(row[0]) == victim.identity
                    and int(option_value(row[1])) == arena_id
                ),
                False,
            ),
        )
        attacker.move_to(0.0, 0.0)
        victim.move_to(0.0, 3.0)
        attacker.face(0.0, 3.0)
        victim.face(0.0, 8.0)
        attacker.call("start_match", [arena_id])
        time.sleep(0.4)

        print("\n== zero-damage player shove + combat entry")
        hp_before = attacker.player_hp(victim.identity)
        baseline = sample_displacement(
            attacker,
            victim.identity,
            attacker.physics,
            lambda: attacker.zero_damage_shove(victim.identity),
        )
        hp_after = attacker.player_hp(victim.identity)
        expect(
            "player moves outward by authored distance",
            3.45 <= baseline["projection"] <= 4.25
            and baseline["lateral"] < 0.35
            and "KNOCKBACK" in baseline["kinds"],
            f"projection={baseline['projection']:.3f}m track={baseline['track_distance']:.3f}m kinds={sorted(baseline['kinds'])}",
            failures,
        )
        expect(
            "zero-damage shove enters combat without damage",
            hp_after == hp_before
            and engagement_reason(attacker, victim.identity) == "KNOCKBACK",
            f"hp={hp_before}->{hp_after} reason={engagement_reason(attacker, victim.identity)!r}",
            failures,
        )

        print("\n== equipment resistance scaling")
        victim.move_to(0.0, 3.0)
        attacker.face(0.0, 3.0)
        victim.call("set_knockback_probe_equipment_resistance", [True])
        resistance = wait_until(
            "resistance affixes",
            lambda: equipment_resistance(attacker, victim.identity) or None,
        )
        resisted = sample_displacement(
            attacker,
            victim.identity,
            attacker.physics,
            lambda: attacker.zero_damage_shove(victim.identity),
        )
        expected_distance = 4.0 * (1.0 - min(resistance, 0.6))
        expect(
            "equipped resistance scales distance",
            abs(resisted["track_distance"] - expected_distance) <= 0.12,
            f"gear={resistance:.3f} expected={expected_distance:.3f}m track={resisted['track_distance']:.3f}m",
            failures,
        )
        victim.call("set_knockback_probe_equipment_resistance", [False])
        wait_until(
            "resistance affix cleanup",
            lambda: equipment_resistance(attacker, victim.identity) == 0.0,
        )

        print("\n== baked arena-boundary stop")
        attacker.move_to(0.0, 4.0)
        victim.move_to(0.0, 7.3)
        attacker.face(0.0, 7.3)
        wall = sample_displacement(
            attacker,
            victim.identity,
            attacker.physics,
            lambda: attacker.zero_damage_shove(victim.identity),
        )
        expect(
            "shove stops at movement geometry",
            0.2 < wall["track_distance"] < 3.9,
            f"baked track={wall['track_distance']:.3f}m (< 4.0m authored)",
            failures,
        )

        print("\n== dodge preemption")
        attacker.move_to(0.0, 0.0)
        victim.move_to(0.0, 3.0)
        attacker.face(0.0, 3.0)
        victim.face(0.0, 8.0)
        victim.start_dodge()
        wait_until(
            "dodge runtime",
            lambda: victim.movement_action(victim.identity)
            or (
                victim.runtime(victim.identity)
                if victim.runtime(victim.identity)
                and victim.runtime(victim.identity)[1] == "DODGE"
                else None
            ),
            timeout=2.0,
        )
        attacker.zero_damage_shove(victim.identity)
        knockback_runtime = wait_until(
            "knockback takeover",
            lambda: (
                row
                if (row := attacker.runtime(victim.identity)) and row[1] == "KNOCKBACK"
                else None
            ),
            timeout=2.0,
        )
        expect(
            "knockback preempts dodge ownership",
            attacker.movement_action(victim.identity) is None
            and knockback_runtime[1] == "KNOCKBACK",
            f"movement_action={attacker.movement_action(victim.identity)} runtime={knockback_runtime[1]}",
            failures,
        )
        time.sleep(0.7)

        print("\n== authored Shockwave: player path")
        victim.move_to(0.0, 3.0)
        attacker.face(0.0, 3.0)
        time.sleep(2.1)
        hp_before = attacker.player_hp(victim.identity)
        player_spell = sample_displacement(
            attacker,
            victim.identity,
            attacker.physics,
            attacker.cast_shockwave,
        )
        hp_after = attacker.player_hp(victim.identity)
        expect(
            "pilot spell uses player knockback runtime",
            "KNOCKBACK" in player_spell["kinds"]
            and player_spell["track_distance"] > 3.45
            and hp_after < hp_before,
            f"track={player_spell['track_distance']:.3f}m hp={hp_before}->{hp_after} kinds={sorted(player_spell['kinds'])}",
            failures,
        )

        print("\n== disconnect teardown")
        victim.move_to(0.0, 3.0)
        attacker.face(0.0, 3.0)
        attacker.zero_damage_shove(victim.identity)
        wait_until(
            "disconnect test runtime",
            lambda: (
                row
                if (row := attacker.runtime(victim.identity)) and row[1] == "KNOCKBACK"
                else None
            ),
            timeout=2.0,
        )
        disconnected_identity = victim.identity
        victim.close()
        victim = None
        cleared = wait_until(
            "disconnect runtime cleanup",
            lambda: attacker.runtime(disconnected_identity) is None,
            timeout=4.0,
        )
        expect(
            "disconnect removes forced runtime",
            cleared,
            "special_movement_runtime row absent",
            failures,
        )

        time.sleep(0.3)
        attacker.assert_no_reducer_failures()
        print("\n== verdict")
        if failures:
            print(f"FAIL ({len(failures)}): {', '.join(failures)}")
            return 1
        print("PASS: all knockback live acceptance checks passed")
        return 0
    finally:
        if victim is not None:
            victim.close()
        attacker.close()


if __name__ == "__main__":
    raise SystemExit(main())

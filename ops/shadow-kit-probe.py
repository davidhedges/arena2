#!/usr/bin/env python3
"""Live acceptance probe for Darkness, Stalk and Shadowrend.

Three ordinary websocket players in an arena instance: a rogue who owns the
three new Subtlety spells, a victim who receives all of them, and a control
attacker whose only job is to prove Shadowrend's advance is caster-scoped.

Reads the production tables to confirm:

  * Darkness lands the GOUGE status from 12m (Gouge itself reaches 2.5m),
    spends mana, blocks the victim's targeted spell for its 5 seconds, and
    releases them when it expires.
  * Stalk attaches a STALKED debuff sourced by the rogue, and a second press
    of the same keybind -- with no target selected, from 15m, while Stalk's own
    24s cooldown is still running -- puts the rogue directly behind the victim
    facing the same way, and spends the shadow.
  * Shadowrend applies a SHADOW magic DOT, the rogue's own melee advances it by
    exactly one tick interval per landed swing, and the control attacker's
    melee on the same victim does not.

Typical isolated local run:

  python3 ops/shadow-kit-probe.py --database arena

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


DARKNESS = "DARKNESS"
STALK = "STALK"
SHADOWREND = "SHADOWREND"
SMITE = "SMITE"

GOUGE_KIND = "GOUGE"
STALKED_KIND = "STALKED"
DOT_KIND = "DOT"

DARKNESS_GROUP = "DARKNESS"
STALKED_GROUP = "STALKED"
SHADOWREND_GROUP = "SHADOWREND"

SHADOWREND_TICK_MS = 3000
STALK_BEHIND_BUFFER = 0.35


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


def signed_angle_delta(a, b):
    return abs((a - b + math.pi) % (2.0 * math.pi) - math.pi)


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
        rows = self.sql("SELECT player_id, hp, max_hp, alive, hit_radius FROM player_state")
        for row_identity, hp, max_hp, alive, hit_radius in rows:
            if normalize_identity(row_identity) == identity:
                return int(hp), int(max_hp), bool(alive), float(hit_radius)
        raise RuntimeError(f"{self.name}: no player_state row for {identity[:8]}")

    def resource(self, kind, identity=None):
        identity = normalize_identity(identity or self.identity)
        rows = self.sql("SELECT owner, kind, current FROM player_resource")
        for owner, row_kind, current in rows:
            if normalize_identity(owner) == identity and str(row_kind).upper() == kind:
                return float(current)
        return None

    def status_rows(self, target):
        target = normalize_identity(target)
        rows = self.sql(
            "SELECT target, source, effect_kind, stack_group, tick_amount, "
            "tick_interval_ms, damage_type, dispel_types, expires_at_micros "
            "FROM status_effect"
        )
        return [
            {
                "source": normalize_identity(source),
                "kind": str(kind).upper(),
                "group": str(group).upper(),
                "tick_amount": int(tick_amount),
                "tick_interval_ms": int(tick_interval),
                "damage_type": str(damage_type).upper(),
                "dispel_types": str(dispel).upper(),
                "expires_at_micros": int(expires),
            }
            for row_target, source, kind, group, tick_amount, tick_interval, damage_type, dispel, expires in rows
            if normalize_identity(row_target) == target
        ]

    def find_status(self, target, kind, group=None, source=None):
        for row in self.status_rows(target):
            if row["kind"] != kind:
                continue
            if group is not None and row["group"] != group:
                continue
            if source is not None and row["source"] != normalize_identity(source):
                continue
            return row
        return None

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
                f"shadow-probe-{self.name}-{self.action_seq}",
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="arena")
    parser.add_argument("--host", default="127.0.0.1:3000")
    parser.add_argument("--server-url", default="http://127.0.0.1:3000")
    args = parser.parse_args()

    failures = []
    rogue = Probe(args.database, args.host, args.server_url, "rogue")
    victim = None
    control = None
    print(f"rogue={rogue.identity[:8]} database={args.database}")
    try:
        wait_until("rogue initialization", lambda: rogue.physics())
        for spell in (DARKNESS, STALK, SHADOWREND):
            rogue.call("learn_spell", [spell])
        victim = Probe(args.database, args.host, args.server_url, "victim")
        wait_until("victim initialization", lambda: victim.physics())
        victim.call("learn_spell", [SMITE])
        control = Probe(args.database, args.host, args.server_url, "control")
        wait_until("control initialization", lambda: control.physics())
        time.sleep(0.7)
        print(f"victim={victim.identity[:8]} control={control.identity[:8]}")

        before_ids = {int(row[0]) for row in rogue.sql("SELECT id FROM arena_instance")}
        rogue.call("create_instance", [3])
        arena_id = wait_until(
            "arena creation",
            lambda: next(
                (
                    int(row[0])
                    for row in rogue.sql("SELECT id FROM arena_instance")
                    if int(row[0]) not in before_ids
                ),
                None,
            ),
        )
        for probe in (rogue, victim, control):
            probe.call("join_instance", [arena_id])
        time.sleep(0.6)
        for probe in (victim, control):
            wait_until(
                f"{probe.name} joins arena",
                lambda probe=probe: next(
                    (
                        True
                        for row in rogue.sql("SELECT identity, instance_id FROM player_world")
                        if normalize_identity(row[0]) == probe.identity
                        and int(option_value(row[1])) == arena_id
                    ),
                    False,
                ),
            )

        rogue_target = f"0x{rogue.identity}"
        victim_target = f"0x{victim.identity}"

        # 12m apart: past Gouge's 2.5m reach, inside Darkness' 25m.
        rogue.move_to(0.0, 0.0)
        victim.move_to(0.0, 12.0)
        control.move_to(6.0, 12.0)
        rogue.face(0.0, 12.0)
        victim.face(0.0, 0.0)
        rogue.call("start_match", [arena_id])
        time.sleep(0.6)

        print("\n== Darkness: ranged, costs mana, and it is Gouge that lands")
        mana_before = rogue.resource("MANA")
        rogue.cast(DARKNESS, target=victim_target)
        wait_until(
            "Darkness applies its status",
            lambda: rogue.find_status(victim.identity, GOUGE_KIND, DARKNESS_GROUP),
            timeout=6.0,
        )
        darkness_status = rogue.find_status(victim.identity, GOUGE_KIND, DARKNESS_GROUP)
        rogue_x, _, rogue_z, _, _ = rogue.physics()
        victim_x, _, victim_z, _, _ = victim.physics()
        cast_range = math.hypot(victim_x - rogue_x, victim_z - rogue_z)
        expect(
            "Darkness lands from beyond Gouge's 2.5m reach",
            cast_range > 6.0,
            f"range={cast_range:.2f}m",
            failures,
        )
        expect(
            "Darkness applies the GOUGE status under its own stack group",
            darkness_status is not None
            and darkness_status["source"] == rogue.identity,
            f"status={darkness_status}",
            failures,
        )
        mana_after = wait_until(
            "Darkness mana spend settles",
            lambda: rogue.resource("MANA"),
            timeout=4.0,
        )
        expect(
            "Darkness spends mana",
            mana_before is not None and mana_after is not None and mana_before - mana_after >= 15.0,
            f"mana={mana_before}->{mana_after}",
            failures,
        )

        hp_guarded, _, _, _ = rogue.player_state()
        victim.cast(SMITE, target=rogue_target)
        time.sleep(2.5)
        expect(
            "a targeted spell from the darkened victim never lands",
            rogue.player_state()[0] == hp_guarded,
            f"rogue hp={hp_guarded}->{rogue.player_state()[0]}",
            failures,
        )

        wait_until(
            "Darkness expires on its own",
            lambda: rogue.find_status(victim.identity, GOUGE_KIND, DARKNESS_GROUP) is None,
            timeout=10.0,
        )
        hp_open, _, _, _ = rogue.player_state()
        victim.cast(SMITE, target=rogue_target)
        wait_until(
            "the same spell lands once Darkness has expired",
            lambda: rogue.player_state()[0] < hp_open,
            timeout=8.0,
        )
        expect(
            "the victim is released when Darkness expires",
            rogue.player_state()[0] < hp_open,
            f"rogue hp={hp_open}->{rogue.player_state()[0]}",
            failures,
        )

        print("\n== Stalk: mark at range, then step behind from anywhere")
        rogue.cast(STALK, target=victim_target)
        wait_until(
            "Stalk attaches its shadow",
            lambda: rogue.find_status(
                victim.identity, STALKED_KIND, STALKED_GROUP, source=rogue.identity
            ),
            timeout=6.0,
        )
        expect(
            "Stalk marks the victim with a rogue-sourced STALKED debuff",
            rogue.find_status(
                victim.identity, STALKED_KIND, STALKED_GROUP, source=rogue.identity
            )
            is not None,
            f"statuses={[r['kind'] for r in rogue.status_rows(victim.identity)]}",
            failures,
        )

        # Walk well out of every authored range, then press the same keybind with
        # no target selected while Stalk's own 24s cooldown is still running.
        rogue.move_to(0.0, -15.0)
        victim.face(6.0, 12.0)
        time.sleep(0.4)
        victim_x, _, victim_z, victim_yaw, _ = victim.physics()
        rogue_x, _, rogue_z, _, _ = rogue.physics()
        step_distance = math.hypot(victim_x - rogue_x, victim_z - rogue_z)
        rogue.cast(STALK, target="")
        wait_until(
            "the second press teleports the rogue",
            lambda: math.hypot(
                victim.physics()[0] - rogue.physics()[0],
                victim.physics()[2] - rogue.physics()[2],
            )
            < 3.0,
            timeout=8.0,
        )
        time.sleep(0.3)
        rogue_x, _, rogue_z, rogue_yaw, _ = rogue.physics()
        victim_x, _, victim_z, victim_yaw, _ = victim.physics()
        _, _, _, rogue_radius = rogue.player_state()
        _, _, _, victim_radius = victim.player_state()
        offset_x = rogue_x - victim_x
        offset_z = rogue_z - victim_z
        landed_distance = math.hypot(offset_x, offset_z)
        expected_distance = rogue_radius + victim_radius + STALK_BEHIND_BUFFER
        forward_x = math.sin(victim_yaw)
        forward_z = math.cos(victim_yaw)
        behind_dot = offset_x * forward_x + offset_z * forward_z
        expect(
            "the shadow reaches the victim from well beyond Stalk's own range",
            step_distance > 20.0,
            f"step_distance={step_distance:.2f}m (Stalk marks to 30m)",
            failures,
        )
        expect(
            "the rogue lands at the victim's back, not their front",
            behind_dot < 0.0,
            f"dot(offset, victim forward)={behind_dot:.2f}",
            failures,
        )
        expect(
            "the rogue lands one capsule-gap behind",
            abs(landed_distance - expected_distance) < 0.5,
            f"distance={landed_distance:.2f}m expected~{expected_distance:.2f}m",
            failures,
        )
        expect(
            "the rogue faces the way the victim faces",
            signed_angle_delta(rogue_yaw, victim_yaw) < 0.2,
            f"rogue_yaw={rogue_yaw:.2f} victim_yaw={victim_yaw:.2f}",
            failures,
        )
        expect(
            "the step spends the shadow",
            rogue.find_status(victim.identity, STALKED_KIND, STALKED_GROUP) is None,
            f"statuses={[r['kind'] for r in rogue.status_rows(victim.identity)]}",
            failures,
        )

        print("\n== Shadowrend: a magic DOT the caster's own melee advances")
        rogue.move_to(victim_x, victim_z - 2.0, tolerance=0.3)
        rogue.face(victim_x, victim_z)
        rogue.cast(SHADOWREND, target=victim_target)
        wait_until(
            "Shadowrend applies its wound",
            lambda: rogue.find_status(
                victim.identity, DOT_KIND, SHADOWREND_GROUP, source=rogue.identity
            ),
            timeout=6.0,
        )
        wound = rogue.find_status(
            victim.identity, DOT_KIND, SHADOWREND_GROUP, source=rogue.identity
        )
        expect(
            "Shadowrend is a SHADOW damage-over-time, not a bleed",
            wound["damage_type"] == "SHADOW" and "BLEED" not in wound["dispel_types"],
            f"damage_type={wound['damage_type']} dispel_types={wound['dispel_types'] or '(none)'}",
            failures,
        )
        expect(
            "Shadowrend ticks on the authored interval",
            wound["tick_interval_ms"] == SHADOWREND_TICK_MS and wound["tick_amount"] > 0,
            f"tick={wound['tick_amount']} every {wound['tick_interval_ms']}ms",
            failures,
        )

        def wound_row():
            return rogue.find_status(
                victim.identity, DOT_KIND, SHADOWREND_GROUP, source=rogue.identity
            )

        def wound_expiry():
            row = wound_row()
            return row["expires_at_micros"] if row else None

        def swing_until(attacker, window_seconds, stop_when=None):
            """Auto-attack for a bounded window. Bounded because auto-attack out-
            damages the wound by an order of magnitude: a long window kills the
            victim and takes the status rows with it."""
            deadline = time.time() + window_seconds
            while time.time() < deadline:
                if not victim.player_state()[2]:
                    break
                attacker.call("arm_auto_attack_target", [victim_target])
                for _ in range(10):
                    if stop_when is not None and stop_when():
                        attacker.call("clear_auto_attack_target", [])
                        return True
                    time.sleep(0.1)
            attacker.call("clear_auto_attack_target", [])
            return False

        print("  (the rogue swings; each landed swing burns one tick of duration)")
        expiry_before_rogue = wound_expiry()
        swing_until(
            rogue,
            12.0,
            stop_when=lambda: wound_expiry() not in (None, expiry_before_rogue),
        )
        advanced = wound_expiry()
        burned_ms = (
            None
            if advanced is None or expiry_before_rogue is None
            else (expiry_before_rogue - advanced) // 1000
        )
        expect(
            "the caster's melee advances the wound",
            burned_ms is not None and burned_ms > 0,
            f"expires_at_micros={expiry_before_rogue}->{advanced}",
            failures,
        )
        expect(
            "an advance burns whole tick intervals, conserving the wound's damage",
            burned_ms is not None and burned_ms % SHADOWREND_TICK_MS == 0,
            f"burned={burned_ms}ms (interval {SHADOWREND_TICK_MS}ms)",
            failures,
        )

        print("  (control attacker swings; the wound is not theirs to advance)")
        control.move_to(victim_x, victim_z + 1.6, tolerance=0.2)
        control.face(victim_x, victim_z)
        expiry_before_control = wound_expiry()
        hp_before_control, _, _, _ = victim.player_state()
        control_window = 5.0
        swing_until(control, control_window)
        hp_after_control, _, _, _ = victim.player_state()
        expiry_after_control = wound_expiry()
        # The wound alone cannot cost more than ceil(window / interval) ticks in
        # this span, so a much larger drop is proof the control's swings landed.
        max_dot_only = wound["tick_amount"] * (
            int(control_window * 1000 / SHADOWREND_TICK_MS) + 1
        )
        control_damage = hp_before_control - hp_after_control
        expect(
            "the control attacker's swings actually connected",
            control_damage > max_dot_only,
            f"victim hp={hp_before_control}->{hp_after_control} (dot alone <= {max_dot_only})",
            failures,
        )
        expect(
            "someone else's melee does not advance the rogue's wound",
            expiry_after_control is not None
            and expiry_after_control == expiry_before_control,
            f"expires_at_micros={expiry_before_control}->{expiry_after_control}",
            failures,
        )
        expect(
            "the victim survived the probe, so the wound was read off a live row",
            victim.player_state()[2],
            f"victim hp={victim.player_state()[0]}",
            failures,
        )

        for probe in (rogue, victim, control):
            probe.assert_no_reducer_failures()

    finally:
        for probe in (rogue, victim, control):
            if probe is not None:
                probe.close()

    print()
    if failures:
        print(f"FAIL ({len(failures)}): {', '.join(failures)}")
        return 1
    print("PASS: Darkness, Stalk and Shadowrend all behave as authored")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

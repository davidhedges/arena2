#!/usr/bin/env python3
"""Live acceptance probe for Lightning Reflexes, Off Balance, and Trip.

Three ordinary websocket players in an arena instance. The rogue pops Lightning
Reflexes, the attacker throws a Smite into it, and a bystander stands inside
Trip's radius without ever attacking. The probe reads the production tables to
confirm:

  * Lightning Reflexes applies the avoidance buff under its own stack group,
  * a hostile SPELL aimed at the rogue during the window deals no damage,
  * the dodged attacker is left Off Balance,
  * re-pressing the Lightning Reflexes keybind casts Trip instead, consuming
    the buff,
  * Trip stuns the Off Balance attacker and passes over the clean bystander.

Typical isolated local run:

  python3 ops/lightning-reflexes-probe.py --database arena

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


LIGHTNING_REFLEXES = "LIGHTNING_REFLEXES"
SMITE = "SMITE"
FROST_NOVA = "FROST_NOVA"
NECROTIC_AURA = "NECROTIC_AURA"
BLINDING_LIGHT = "BLINDING_LIGHT"
ROOT_KIND = "ROOT"
TARGETED_AVOIDANCE_KIND = "TARGETED_ABILITY_AVOIDANCE"  # Blinding Light
AVOIDANCE_KIND = "ALL_ABILITY_AVOIDANCE"  # Lightning Reflexes
OFF_BALANCE_KIND = "OFF_BALANCE"
STUN_KIND = "STUN"


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
        rows = self.sql("SELECT target, effect_kind, stack_group FROM status_effect")
        return sorted(
            (str(kind), str(group))
            for target, kind, group in rows
            if normalize_identity(target) == identity
        )

    def has_status(self, identity, kind, stack_group=None):
        for effect_kind, group in self.statuses(identity):
            if effect_kind.upper() != kind:
                continue
            if stack_group is None or group.upper() == stack_group:
                return True
        return False

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
                f"lr-probe-{self.name}-{self.action_seq}",
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
    attacker = None
    bystander = None
    print(f"rogue={rogue.identity[:8]} database={args.database}")
    try:
        wait_until("rogue initialization", lambda: rogue.physics())
        rogue.call("learn_spell", [LIGHTNING_REFLEXES])
        rogue.call("learn_spell", [BLINDING_LIGHT])
        attacker = Probe(args.database, args.host, args.server_url, "attacker")
        wait_until("attacker initialization", lambda: attacker.physics())
        attacker.call("learn_spell", [SMITE])
        attacker.call("learn_spell", [FROST_NOVA])
        attacker.call("learn_spell", [NECROTIC_AURA])
        bystander = Probe(args.database, args.host, args.server_url, "bystander")
        wait_until("bystander initialization", lambda: bystander.physics())
        time.sleep(0.7)
        print(f"attacker={attacker.identity[:8]} bystander={bystander.identity[:8]}")

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
        rogue.call("join_instance", [arena_id])
        attacker.call("join_instance", [arena_id])
        bystander.call("join_instance", [arena_id])
        time.sleep(0.6)
        for probe in (attacker, bystander):
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
        # Both hostiles stand inside Trip's 4m radius around the rogue.
        rogue.move_to(0.0, 0.0)
        attacker.move_to(0.0, 3.0)
        bystander.move_to(2.5, 0.0)
        attacker.face(0.0, 0.0)
        rogue.call("start_match", [arena_id])
        time.sleep(0.6)

        rogue_target = f"0x{rogue.identity}"

        print("\n== control: Smite connects with no dodge window up")
        hp_before, _, _ = rogue.player_state()
        attacker.cast(SMITE, target=rogue_target)
        wait_until(
            "control smite lands",
            lambda: rogue.player_state()[0] < hp_before,
            timeout=6.0,
        )
        hp_control, _, _ = rogue.player_state()
        expect(
            "Smite damages the rogue with no buff active",
            hp_control < hp_before,
            f"hp={hp_before}->{hp_control}",
            failures,
        )
        # Clear the attacker's Smite cooldown window before the real leg.
        time.sleep(1.6)

        print("\n== Lightning Reflexes opens a 3s dodge window")
        rogue.cast(LIGHTNING_REFLEXES)
        wait_until(
            "avoidance buff applied",
            lambda: rogue.has_status(rogue.identity, AVOIDANCE_KIND, LIGHTNING_REFLEXES),
            timeout=6.0,
        )
        window_opened_at = time.time()
        expect(
            "rogue carries the avoidance buff under the Lightning Reflexes group",
            rogue.has_status(rogue.identity, AVOIDANCE_KIND, LIGHTNING_REFLEXES),
            f"statuses={rogue.statuses(rogue.identity)}",
            failures,
        )

        print("\n== a hostile spell is dodged and leaves its caster Off Balance")
        hp_before_dodge, _, _ = rogue.player_state()
        attacker.cast(SMITE, target=rogue_target)
        wait_until(
            "off balance applied to the dodged attacker",
            lambda: attacker.has_status(attacker.identity, OFF_BALANCE_KIND),
            timeout=4.0,
        )
        hp_after_dodge, _, _ = rogue.player_state()
        expect(
            "the dodged SPELL deals no damage",
            hp_after_dodge == hp_before_dodge,
            f"hp={hp_before_dodge}->{hp_after_dodge}",
            failures,
        )
        expect(
            "the dodged attacker is Off Balance",
            attacker.has_status(attacker.identity, OFF_BALANCE_KIND),
            f"statuses={attacker.statuses(attacker.identity)}",
            failures,
        )
        expect(
            "the bystander who attacked nothing is not Off Balance",
            not bystander.has_status(bystander.identity, OFF_BALANCE_KIND),
            f"statuses={bystander.statuses(bystander.identity)}",
            failures,
        )

        print("\n== re-pressing the keybind casts Trip inside the window")
        elapsed = time.time() - window_opened_at
        print(f"  (re-press at t+{elapsed:.2f}s of the 3.00s window)")
        rogue.cast(LIGHTNING_REFLEXES)
        wait_until(
            "trip stuns the off balance attacker",
            lambda: attacker.has_status(attacker.identity, STUN_KIND),
            timeout=4.0,
        )
        expect(
            "Trip stuns the Off Balance attacker",
            attacker.has_status(attacker.identity, STUN_KIND),
            f"statuses={attacker.statuses(attacker.identity)}",
            failures,
        )
        expect(
            "Trip passes over the bystander in radius who is not Off Balance",
            not bystander.has_status(bystander.identity, STUN_KIND),
            f"statuses={bystander.statuses(bystander.identity)}",
            failures,
        )
        expect(
            "casting Trip consumes the dodge window",
            not rogue.has_status(rogue.identity, AVOIDANCE_KIND, LIGHTNING_REFLEXES),
            f"statuses={rogue.statuses(rogue.identity)}",
            failures,
        )

        print("\n== the follow-up is spent: pressing again does not re-trip")
        bystander_hp_before, _, _ = bystander.player_state()
        rogue.cast(LIGHTNING_REFLEXES)
        time.sleep(1.0)
        expect(
            "a third press casts nothing (Lightning Reflexes is on cooldown)",
            not rogue.has_status(rogue.identity, AVOIDANCE_KIND, LIGHTNING_REFLEXES)
            and not bystander.has_status(bystander.identity, STUN_KIND)
            and bystander.player_state()[0] == bystander_hp_before,
            f"rogue={rogue.statuses(rogue.identity)} bystander={bystander.statuses(bystander.identity)}",
            failures,
        )

        print("\n== the same window turns aside melee, not just spells")
        # Lightning Reflexes is on a 24s cooldown; that also outlasts Trip's stun.
        print("  (waiting out the 24s Lightning Reflexes cooldown)")
        # Hostile casts re-arm the attacker's auto-attack; left armed at melee
        # range it kills the rogue outright across a 25s wait.
        attacker.call("clear_auto_attack_target", [])
        time.sleep(25.0)
        # Auto-attack reach is 2.28m, so 2.0m sits inside move_to's own 0.35m
        # tolerance band and parks out of range about half the time. 1.6m with a
        # tight tolerance is unambiguously inside it, and still inside Frost Nova.
        attacker.move_to(0.0, 1.6, tolerance=0.2)
        attacker.face(0.0, 0.0)
        hp_before_swing, _, _ = rogue.player_state()

        def swing_connects(timeout):
            # Combat engagement lapses during the cooldown waits and the swing
            # cadence is several seconds, so re-arm rather than trusting one call.
            deadline = time.time() + timeout
            while time.time() < deadline:
                attacker.call("arm_auto_attack_target", [rogue_target])
                for _ in range(20):
                    if rogue.player_state()[0] < hp_before_swing:
                        return True
                    time.sleep(0.25)
            return False

        if not swing_connects(30.0):
            raise RuntimeError("control auto-attack never connected")
        hp_after_swing, _, _ = rogue.player_state()
        expect(
            "auto-attack damages the rogue with no buff active",
            hp_after_swing < hp_before_swing,
            f"hp={hp_before_swing}->{hp_after_swing}",
            failures,
        )

        rogue.cast(LIGHTNING_REFLEXES)
        wait_until(
            "avoidance buff reapplied",
            lambda: rogue.has_status(rogue.identity, AVOIDANCE_KIND, LIGHTNING_REFLEXES),
            timeout=6.0,
        )
        hp_guarded, _, _ = rogue.player_state()
        wait_until(
            "melee swing is dodged and marks its attacker",
            lambda: attacker.has_status(attacker.identity, OFF_BALANCE_KIND),
            timeout=4.0,
        )
        expect(
            "the dodged MELEE swing deals no damage",
            rogue.player_state()[0] == hp_guarded,
            f"hp={hp_guarded}->{rogue.player_state()[0]}",
            failures,
        )
        expect(
            "the dodged melee attacker is Off Balance",
            attacker.has_status(attacker.identity, OFF_BALANCE_KIND),
            f"statuses={attacker.statuses(attacker.identity)}",
            failures,
        )
        attacker.call("clear_auto_attack_target", [])

        print("\n== an AREA spell is dodged too, but leaves nobody Off Balance")
        print("  (waiting out the 24s Lightning Reflexes cooldown)")
        # Hostile casts re-arm the attacker's auto-attack; left armed at melee
        # range it kills the rogue outright across a 25s wait.
        attacker.call("clear_auto_attack_target", [])
        time.sleep(25.0)
        # Frost Nova is centred on its caster with a 4.6m radius; the attacker
        # stands 2m away, so the rogue is inside it.
        hp_before_nova, _, _ = rogue.player_state()
        attacker.cast(FROST_NOVA)
        wait_until(
            "control frost nova lands",
            lambda: rogue.player_state()[0] < hp_before_nova,
            timeout=8.0,
        )
        hp_nova_control, _, _ = rogue.player_state()
        expect(
            "Frost Nova damages the rogue with no buff active",
            hp_nova_control < hp_before_nova,
            f"hp={hp_before_nova}->{hp_nova_control}",
            failures,
        )
        time.sleep(2.0)

        rogue.cast(LIGHTNING_REFLEXES)
        wait_until(
            "avoidance buff reapplied for the area leg",
            lambda: rogue.has_status(rogue.identity, AVOIDANCE_KIND, LIGHTNING_REFLEXES),
            timeout=6.0,
        )
        hp_guarded_area, _, _ = rogue.player_state()
        attacker.cast(FROST_NOVA)
        time.sleep(1.2)
        hp_after_area, _, _ = rogue.player_state()
        expect(
            "the dodged AREA spell deals no damage",
            hp_after_area == hp_guarded_area,
            f"hp={hp_guarded_area}->{hp_after_area}",
            failures,
        )
        expect(
            "the dodged AREA spell applies none of its impact effects",
            not rogue.has_status(rogue.identity, ROOT_KIND),
            f"statuses={rogue.statuses(rogue.identity)}",
            failures,
        )
        expect(
            "an area dodge leaves its caster clean (no Off Balance)",
            not attacker.has_status(attacker.identity, OFF_BALANCE_KIND),
            f"statuses={attacker.statuses(attacker.identity)}",
            failures,
        )

        print("\n== breadth is per-ability: Blinding Light stays targeted-only")
        # Blinding Light authors TARGETED_ABILITY_AVOIDANCE, Lightning Reflexes
        # authors ALL_ABILITY_AVOIDANCE. Widening one must not widen the other.
        # Both checks must fit inside Blinding Light's 5s window, and each cast
        # must clear the attacker's 1.5s global cooldown or it is silently eaten.
        time.sleep(2.0)
        rogue.cast(BLINDING_LIGHT)
        wait_until(
            "blinding light applied",
            lambda: rogue.has_status(rogue.identity, TARGETED_AVOIDANCE_KIND, "BLINDING_LIGHT"),
            timeout=6.0,
        )
        hp_blind_area, _, _ = rogue.player_state()
        attacker.cast(FROST_NOVA)
        time.sleep(1.3)
        hp_after_blind_area, _, _ = rogue.player_state()
        expect(
            "Blinding Light does NOT dodge an AREA spell (unchanged by this work)",
            hp_blind_area - hp_after_blind_area >= 15
            and rogue.has_status(rogue.identity, TARGETED_AVOIDANCE_KIND, "BLINDING_LIGHT"),
            f"hp={hp_blind_area}->{hp_after_blind_area} (expected a full Frost Nova hit)",
            failures,
        )
        time.sleep(0.6)
        hp_blind_target, _, _ = rogue.player_state()
        attacker.cast(SMITE, target=rogue_target)
        time.sleep(1.3)
        hp_after_blind_target, _, _ = rogue.player_state()
        expect(
            "Blinding Light still dodges a TARGETED spell",
            hp_after_blind_target == hp_blind_target
            and rogue.has_status(rogue.identity, TARGETED_AVOIDANCE_KIND, "BLINDING_LIGHT"),
            f"hp={hp_blind_target}->{hp_after_blind_target}",
            failures,
        )

        print("\n== an AURA is deliberately NOT dodged")
        print("  (waiting out the 24s Lightning Reflexes cooldown)")
        # Hostile casts re-arm the attacker's auto-attack; left armed at melee
        # range it kills the rogue outright across a 25s wait.
        attacker.call("clear_auto_attack_target", [])
        time.sleep(25.0)
        rogue.cast(LIGHTNING_REFLEXES)
        wait_until(
            "avoidance buff reapplied for the aura leg",
            lambda: rogue.has_status(rogue.identity, AVOIDANCE_KIND, LIGHTNING_REFLEXES),
            timeout=6.0,
        )
        hp_guarded_aura, _, _ = rogue.player_state()
        attacker.cast(NECROTIC_AURA)
        # Necrotic Aura pulses once a second; sample strictly inside the 3s window
        # so a tick after it lapses cannot be mistaken for one that got through.
        aura_ticked_while_guarded = False
        deadline = time.time() + 2.6
        while time.time() < deadline:
            if rogue.player_state()[0] < hp_guarded_aura:
                aura_ticked_while_guarded = rogue.has_status(
                    rogue.identity, AVOIDANCE_KIND, LIGHTNING_REFLEXES
                )
                break
            time.sleep(0.1)
        expect(
            "Necrotic Aura still damages the rogue mid-window",
            aura_ticked_while_guarded,
            f"hp={hp_guarded_aura}->{rogue.player_state()[0]} "
            f"buff_up_at_tick={aura_ticked_while_guarded}",
            failures,
        )

        rogue.assert_no_reducer_failures()
        attacker.assert_no_reducer_failures()
        bystander.assert_no_reducer_failures()
    except Exception as error:
        print(f"\nprobe aborted: {type(error).__name__}: {error}")
        for probe in (rogue, attacker, bystander):
            if probe:
                print(f"{probe.name} recent: {list(probe.recent)[-6:]}")
        failures.append(f"probe aborted: {error}")
    finally:
        for probe in (bystander, attacker, rogue):
            if probe:
                probe.close()

    print("\n=== RESULT ===")
    if failures:
        print(f"FAIL ({len(failures)}): {failures}")
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

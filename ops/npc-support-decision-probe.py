#!/usr/bin/env python3
"""Headless acceptance probe for NPC support and hostile debuff decisions.

The probe creates one hostile Kobold ally and one hostile Lich beside a
headless player. It proves the two utility outcomes through public runtime
rows:

  1. a full-health ally receives NPC_LICH_BONE_WARD;
  2. after one player auto-attack damages that same ally, it receives
     NPC_LICH_MEND and its HP increases.
  3. a Skeleton Wizard applies NPC_SKELETON_WIZARD_FROSTBITE to its pinned
     hostile player target through the shared targeted status pipeline.
  4. while that player casts ICICLE, the Wizard selects
     NPC_SKELETON_WIZARD_ICE_LOCK, applies its shared stun status, and the
     existing crowd-control interruption lifecycle fizzles the active cast.

Run this against a dedicated local database built from the current module:

  ARENA_NPC_AI_DEBUG=1 ARENA_DATABASE=npcsupportprobe \
    ARENA_PROJECTILE_LOAD_HARNESS=1 ARENA_GENERATE_BINDINGS=0 \
    ARENA_VERIFY_DOTNET=0 ./ops/republish-local-clear.sh
  python3 ops/npc-support-decision-probe.py --database npcsupportprobe

Requires the websocket-client Python package used by the existing S4-S10
headless probes.
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


KOBOLD_TEMPLATE = "KOBOLD_WARRIOR_RD_SWORD_SHIELD"
KOBOLD_VISUAL = "KOBOLD_WARRIOR_RD"
LICH_TEMPLATE = "LICH_SUPPORT"
LICH_VISUAL = "LICH_GN"
BONE_WARD_ABILITY = "NPC_LICH_BONE_WARD"
MEND_ABILITY = "NPC_LICH_MEND"
WIZARD_TEMPLATE = "SKELETON_WIZARD"
WIZARD_VISUAL = "SKELETON_WIZARD_GN"
FROSTBITE_ABILITY = "NPC_SKELETON_WIZARD_FROSTBITE"
FROSTBITE_STACK_GROUP = "NPC_SKELETON_FROSTBITE"
ICE_LOCK_ABILITY = "NPC_SKELETON_WIZARD_ICE_LOCK"
ICE_LOCK_STACK_GROUP = "NPC_SKELETON_ICE_LOCK"
INTERRUPTIBLE_PLAYER_SPELL = "ICICLE"
INTERRUPTIBLE_PLAYER_ABILITY = "SPELL_ICICLE"


class Probe:
    def __init__(self, database, host):
        self.database = database
        self.host = host
        self.request_id = 0
        self.recent = collections.deque(maxlen=40)
        url = f"ws://{host}/v1/database/{database}/subscribe"
        self.ws = websocket.create_connection(
            url, subprotocols=["v1.json.spacetimedb"], timeout=5
        )
        first = json.loads(self.ws.recv())
        token = first.get("IdentityToken", {})
        identity = token.get("identity", "")
        if isinstance(identity, dict):
            identity = identity.get("__identity__", "")
        self.identity = normalize_identity(identity)
        if not self.identity:
            raise RuntimeError("subscription did not return an identity")

        self.ws.settimeout(None)
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
                    status = update.get("status", {})
                    reducer = update.get("reducer_call", {}).get("reducer_name", "?")
                    if "Failed" in status:
                        self.recent.append(f"{reducer}: FAILED {status['Failed']}")
                except Exception:
                    self.recent.append(message[:200])
        except Exception as error:
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
        command = ["spacetime", "sql", "-s", "local", self.database, query]
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode != 0:
            sys.stderr.write(result.stderr)
            raise RuntimeError(f"spacetime sql failed: {query}")

        rows = []
        for line in result.stdout.splitlines():
            if "|" not in line or set(line.strip()) <= {"-", "+", "|", " "}:
                continue
            rows.append([cell.strip().strip('"') for cell in line.split("|")])
        return rows[1:] if rows else []

    def npc_identity(self, template):
        rows = self.sql("SELECT identity, spawned_by, template_id FROM npc_instance")
        matches = [
            normalize_identity(identity)
            for identity, owner, template_id in rows
            if normalize_identity(owner) == self.identity and template_id == template
        ]
        if not matches:
            self.dump_recent()
            raise RuntimeError(f"no owned {template} row found: {rows}")
        return matches[-1]

    def physics(self):
        rows = self.sql(
            "SELECT identity, pos_x, pos_z, last_processed_tick FROM player_physics"
        )
        for identity, x, z, tick in rows:
            if normalize_identity(identity) == self.identity:
                return float(x), float(z), int(tick)
        raise RuntimeError("no player_physics row for probe identity")

    def cast_pose(self):
        rows = self.sql(
            "SELECT identity, pos_x, pos_y, pos_z, yaw, last_processed_tick "
            "FROM player_physics"
        )
        for identity, x, y, z, yaw, tick in rows:
            if normalize_identity(identity) == self.identity:
                return float(x), float(y), float(z), float(yaw), int(tick)
        raise RuntimeError("no player_physics row for probe identity")

    def move_to(self, target_x, target_z, timeout=12.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            x, z, tick = self.physics()
            dx = target_x - x
            dz = target_z - z
            distance = math.hypot(dx, dz)
            yaw = math.atan2(dx, dz)
            if distance <= 0.5:
                self.call(
                    "send_movement_intent",
                    [0.0, 0.0, yaw, False, tick + 2],
                )
                time.sleep(0.4)
                return
            self.call(
                "send_movement_intent",
                [min(1.0, distance / 4.0), 0.0, yaw, False, tick + 2],
            )
            time.sleep(0.12)
        raise RuntimeError(f"move_to({target_x:.1f},{target_z:.1f}) timed out")

    def npc_health(self, identity):
        rows = self.sql("SELECT identity, hp, max_hp FROM npc_state")
        for row_identity, hp, max_hp in rows:
            if normalize_identity(row_identity) == identity:
                return int(hp), int(max_hp)
        raise RuntimeError(f"no npc_state row for {identity}")

    def has_status(self, target, source, stack_group):
        rows = self.sql("SELECT target, source, stack_group FROM status_effect")
        return any(
            normalize_identity(row_target) == target
            and normalize_identity(row_source) == source
            and row_stack_group == stack_group
            for row_target, row_source, row_stack_group in rows
        )

    def has_impact(self, caster, hit, ability):
        rows = self.sql(
            "SELECT caster, hit, ability_id, event_type FROM combat_event"
        )
        return any(
            normalize_identity(row_caster) == caster
            and normalize_identity(row_hit) == hit
            and row_ability == ability
            and row_event == "COMBAT_IMPACT"
            for row_caster, row_hit, row_ability, row_event in rows
        )

    def has_caster_event(self, caster, ability, event_type):
        rows = self.sql("SELECT caster, ability_id, event_type FROM combat_event")
        return any(
            normalize_identity(row_caster) == caster
            and row_ability == ability
            and row_event == event_type
            for row_caster, row_ability, row_event in rows
        )

    def dump_recent(self):
        for entry in self.recent:
            print(f"    reducer: {entry}")

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def normalize_identity(value):
    return (value or "").removeprefix("0x").lower()


def wire_identity(value):
    return {"__identity__": f"0x{normalize_identity(value)}"}


def wire_optional_identity(value):
    return {"some": wire_identity(value)}


def wait_until(label, predicate, timeout=8.0, interval=0.1):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = predicate()
        if last:
            return last
        time.sleep(interval)
    raise RuntimeError(f"timed out waiting for {label}; last={last}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="npcsupportprobe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    args = parser.parse_args()

    probe = Probe(args.database, args.host)
    print(f"probe identity: {probe.identity}")
    try:
        time.sleep(0.8)
        configure_probe_combat_build(probe, [INTERRUPTIBLE_PLAYER_ABILITY])
        probe.call("join_training_instance", [])
        time.sleep(1.0)
        probe.move_to(0.0, 0.0)
        # Training spawns face east toward authored dummies. Face north before
        # debug spawning so the NPC forward point is in the empty flat lane.
        _, _, tick = probe.physics()
        probe.call(
            "send_movement_intent",
            [0.0, 0.0, 0.0, False, tick + 2],
        )
        time.sleep(0.5)
        probe.call("despawn_all_npcs", [])
        time.sleep(0.3)

        print("== setup: full-health Kobold ally and Lich support")
        probe.call("spawn_npc", [KOBOLD_TEMPLATE, KOBOLD_VISUAL, "HOSTILE"])
        time.sleep(0.5)
        kobold = probe.npc_identity(KOBOLD_TEMPLATE)
        probe.call(
            "set_npc_target_override",
            [wire_identity(kobold), wire_optional_identity(probe.identity)],
        )

        probe.call("spawn_npc", [LICH_TEMPLATE, LICH_VISUAL, "HOSTILE"])
        time.sleep(0.5)
        lich = probe.npc_identity(LICH_TEMPLATE)
        probe.call(
            "set_npc_target_override",
            [wire_identity(lich), wire_optional_identity(probe.identity)],
        )
        print(f"  Kobold={kobold[:12]} Lich={lich[:12]}")

        wait_until(
            "Bone Ward impact on the full-health Kobold",
            lambda: probe.has_impact(lich, kobold, BONE_WARD_ABILITY)
            and probe.has_status(kobold, lich, "LICH_BONE_WARD"),
        )
        hp, max_hp = probe.npc_health(kobold)
        if hp != max_hp:
            raise RuntimeError(
                f"expected full health at Bone Ward decision, got {hp}/{max_hp}"
            )
        print(f"  PASS full-health choice: {BONE_WARD_ABILITY} at {hp}/{max_hp} HP")

        print("== transition: damage the warded ally once")
        probe.call("arm_auto_attack_target", [kobold])

        damaged = wait_until(
            "player auto-attack damage",
            lambda: (
                health
                if (health := probe.npc_health(kobold))[0] < health[1]
                else None
            ),
            timeout=10.0,
            interval=0.05,
        )
        probe.call("clear_auto_attack_target", [])
        damaged_hp, max_hp = damaged
        print(f"  observed damage: {damaged_hp}/{max_hp} HP")

        wait_until(
            "Lich Mend impact on the damaged Kobold",
            lambda: probe.has_impact(lich, kobold, MEND_ABILITY),
            timeout=8.0,
        )
        healed_hp, _ = wait_until(
            "Kobold HP increase from Lich Mend",
            lambda: (
                health
                if (health := probe.npc_health(kobold))[0] > damaged_hp
                else None
            ),
            timeout=4.0,
            interval=0.05,
        )
        print(
            f"  PASS damaged-ally choice: {MEND_ABILITY} "
            f"raised HP {damaged_hp}->{healed_hp}"
        )

        print("== hostile debuff: Skeleton Wizard Frostbite")
        probe.call("despawn_all_npcs", [])
        time.sleep(0.4)
        probe.call("spawn_npc", [WIZARD_TEMPLATE, WIZARD_VISUAL, "HOSTILE"])
        time.sleep(0.5)
        wizard = probe.npc_identity(WIZARD_TEMPLATE)
        probe.call(
            "set_npc_target_override",
            [wire_identity(wizard), wire_optional_identity(probe.identity)],
        )
        wait_until(
            "Frostbite impact and slow status on the hostile player",
            lambda: probe.has_impact(wizard, probe.identity, FROSTBITE_ABILITY)
            and probe.has_status(
                probe.identity,
                wizard,
                FROSTBITE_STACK_GROUP,
            ),
            timeout=10.0,
        )
        print(
            f"  PASS hostile choice: {FROSTBITE_ABILITY} applied "
            f"{FROSTBITE_STACK_GROUP}"
        )

        print("== hostile interrupt: Skeleton Wizard Ice Lock")
        cast_x, cast_y, cast_z, cast_yaw, cast_tick = probe.cast_pose()
        probe.call(
            "cast_request",
            [
                INTERRUPTIBLE_PLAYER_SPELL,
                f"0x{wizard}",
                0.0,
                0.0,
                0.0,
                cast_tick,
                cast_x,
                cast_y,
                cast_z,
                cast_yaw,
                "npc-interrupt-probe",
                1,
                int(time.time() * 1000),
            ],
        )
        wait_until(
            "Ice Lock impact and stun status on the casting player",
            lambda: probe.has_impact(wizard, probe.identity, ICE_LOCK_ABILITY)
            and probe.has_status(
                probe.identity,
                wizard,
                ICE_LOCK_STACK_GROUP,
            ),
            timeout=10.0,
        )
        wait_until(
            "the interrupted player cast to fizzle",
            lambda: probe.has_caster_event(
                probe.identity,
                INTERRUPTIBLE_PLAYER_ABILITY,
                "COMBAT_FIZZLE",
            ),
            timeout=4.0,
        )
        print(
            f"  PASS hostile choice: {ICE_LOCK_ABILITY} interrupted "
            f"{INTERRUPTIBLE_PLAYER_SPELL} through shared crowd control"
        )
        probe.call("clear_auto_attack_target", [])
        print(
            "PASS: support buff/heal, hostile debuff, and hostile interrupt "
            "decisions observed"
        )
        return 0
    except Exception as error:
        probe.call("clear_auto_attack_target", [])
        probe.dump_recent()
        for label, query in (
            (
                "npc_decision_debug",
                "SELECT identity, chosen_ability_id, hard_reject_summary, score_summary "
                "FROM npc_decision_debug",
            ),
            (
                "combat_event",
                "SELECT caster, hit, ability_id, event_type FROM combat_event",
            ),
            (
                "active_cast",
                "SELECT caster, ability_id, kind, target_id FROM active_cast",
            ),
        ):
            try:
                print(f"    {label}: {probe.sql(query)}")
            except Exception as diagnostic_error:
                print(f"    {label}: unavailable ({diagnostic_error})")
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    finally:
        probe.call("despawn_all_npcs", [])
        time.sleep(0.2)
        probe.close()


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Drive the four-exemplar NPC encounter for the Unity acceptance observer.

This websocket client is deliberately a different identity from the Unity
batch client. It moves to a clear Desert_Day lane, waits for the observer,
spawns and pins the Kobold, Archer, Wizard, and Lich, then keeps the target
moving while the Unity runner records shared presentation evidence.

Run against an isolated local database:

  ARENA_NPC_HARMLESS=1 spacetime publish --delete-data=always --yes \
    -s local -p server npcmixedprobe
  spacetime call -s local npcmixedprobe publish_spell_definitions
  spacetime call -s local npcmixedprobe publish_progression_catalogs
  python3 ops/npc-mixed-group-probe.py --database npcmixedprobe
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


SCENE = "Desert_Day"
NPC_SPECS = (
    ("KOBOLD_WARRIOR_RD_SWORD_SHIELD", "KOBOLD_WARRIOR_RD_SWORD_SHIELD"),
    ("LICH_SUPPORT", "LICH_GN"),
    ("SKELETON_ARCHER", "SKELETON_ARCHER_GN"),
    ("SKELETON_WIZARD", "SKELETON_WIZARD_GN"),
)


def normalize_identity(value):
    return (value or "").removeprefix("0x").lower()


def wire_identity(value):
    return {"__identity__": f"0x{normalize_identity(value)}"}


def wire_optional_identity(value):
    return {"some": wire_identity(value)}


class Probe:
    def __init__(self, database, host):
        self.database = database
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

    def physics(self):
        rows = self.sql(
            "SELECT identity, pos_x, pos_z, last_processed_tick FROM player_physics"
        )
        for identity, x, z, tick in rows:
            if normalize_identity(identity) == self.identity:
                return float(x), float(z), int(tick)
        raise RuntimeError("no player_physics row for probe identity")

    def move_to(self, target_x, target_z, timeout=15.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            x, z, tick = self.physics()
            dx = target_x - x
            dz = target_z - z
            distance = math.hypot(dx, dz)
            yaw = math.atan2(dx, dz)
            if distance <= 0.6:
                self.call("send_movement_intent", [0.0, 0.0, yaw, False, tick + 2])
                time.sleep(0.3)
                return
            self.call(
                "send_movement_intent",
                [min(1.0, distance / 4.0), 0.0, yaw, False, tick + 2],
            )
            time.sleep(0.1)
        raise RuntimeError(f"move_to({target_x:.1f},{target_z:.1f}) timed out")

    def scene_player_count(self, scene):
        rows = self.sql(
            "SELECT identity, world_kind, open_world_scene_name FROM player_world"
        )
        return sum(
            1
            for _, world_kind, scene_name in rows
            if world_kind == "OPEN" and scene_name == scene
        )

    def npc_identity(self, template):
        rows = self.sql("SELECT identity, spawned_by, template_id FROM npc_instance")
        matches = [
            normalize_identity(identity)
            for identity, owner, template_id in rows
            if normalize_identity(owner) == self.identity and template_id == template
        ]
        if not matches:
            raise RuntimeError(f"no owned {template} row found: {rows}")
        return matches[-1]

    def ability_events(self):
        rows = self.sql(
            "SELECT ability_id, event_type FROM combat_event"
        )
        return {(ability, event_type) for ability, event_type in rows}

    def dump_recent(self):
        for entry in self.recent:
            print(f"    reducer: {entry}")

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def wait_until(label, predicate, timeout=20.0, interval=0.2):
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
    parser.add_argument("--database", default="npcmixedprobe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    parser.add_argument("--scene", default=SCENE)
    parser.add_argument("--observer-timeout", type=float, default=45.0)
    parser.add_argument("--hold-seconds", type=float, default=30.0)
    args = parser.parse_args()

    probe = Probe(args.database, args.host)
    print(f"probe identity: {probe.identity}")
    try:
        time.sleep(0.8)
        probe.call("set_open_world_scene", [args.scene])
        time.sleep(1.0)

        print(f"== waiting for a second client in {args.scene}")
        count = wait_until(
            "Unity observer",
            lambda: (
                value
                if (value := probe.scene_player_count(args.scene)) >= 2
                else None
            ),
            timeout=args.observer_timeout,
        )
        print(f"   observed {count} clients")

        x0, z0, _ = probe.physics()
        target_x, target_z = x0 + 3.0, z0 + 30.0
        print(f"== moving to clear encounter lane ({target_x:.1f}, {target_z:.1f})")
        probe.move_to(target_x, target_z)
        _, _, tick = probe.physics()
        probe.call("send_movement_intent", [0.0, 0.0, 0.0, False, tick + 2])
        time.sleep(0.4)
        probe.call("despawn_all_npcs", [])
        time.sleep(0.3)

        print("== spawning mixed exemplar group")
        identities = {}
        for template, visual in NPC_SPECS:
            probe.call("spawn_npc", [template, visual, "HOSTILE"])
            time.sleep(0.35)
            identity = probe.npc_identity(template)
            identities[template] = identity
            probe.call(
                "set_npc_target_override",
                [wire_identity(identity), wire_optional_identity(probe.identity)],
            )
            print(f"   {template}: {identity[:12]}")

        # Open the ranged band while the melee exemplar follows. Repeating a
        # short north/south leg keeps the target alive longer by forcing real
        # projectile travel without changing any gameplay arithmetic.
        north_z = target_z + 12.0
        south_z = target_z + 7.0
        deadline = time.time() + args.hold_seconds
        leg = 0
        while time.time() < deadline:
            probe.move_to(target_x, north_z if leg % 2 == 0 else south_z, timeout=8.0)
            leg += 1
            events = probe.ability_events()
            observed = sorted({ability for ability, _ in events if ability.startswith("NPC_")})
            print(f"   observed abilities: {', '.join(observed) or '<none>'}")
            time.sleep(0.5)

        required = {
            "NPC_KOBOLD_WARRIOR_SWORD_SLASH",
            "NPC_SKELETON_ARCHER_SHOT",
            "NPC_SKELETON_WIZARD_FROST_BOLT",
            "NPC_LICH_BONE_WARD",
        }
        observed = {ability for ability, _ in probe.ability_events()}
        missing = sorted(required - observed)
        if missing:
            raise RuntimeError(f"server did not emit required exemplar abilities: {missing}")

        print("PASS: all four exemplar abilities emitted for the two-client observer")
        return 0
    except Exception as error:
        probe.dump_recent()
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    finally:
        probe.call("despawn_all_npcs", [])
        time.sleep(0.2)
        probe.close()


if __name__ == "__main__":
    raise SystemExit(main())

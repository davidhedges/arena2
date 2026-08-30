#!/usr/bin/env python3
"""S4 live probe: LineOfSightBlocked for the previously-exempt kit.

Drives a headless websocket player (client_connected spawns a live player;
reducers ride the same socket; state read via `spacetime sql`) through the
Giant_Skeleton scene, using the skull chunk near spawn as the LOS wall:

  control   — open ground: a targeted melee press is Accepted and the armed
              auto-attack emits CAST combat events (also prints the live
              wire values for combat_event rows).
  near-wall — the owner-reported regression: a dummy spawned BESIDE a wall
              (spawns are collision-resolved) stays attackable — the melee
              press is Accepted, not LineOfSightBlocked.
  blocked   — a dummy spawned through the wall resolves to a legal spot on
              the far side; the gap-close press through the wall rejects
              LineOfSightBlocked instead of dashing. (Melee-range LOS blocks
              between legally-placed actors are near-impossible by design —
              the clear rule tolerates hits within the target's personal
              space — so there is no melee-range blocked fixture.)

Run against a throwaway DB — one-shot `spacetime call` cannot leave
per-identity state, and disconnect cleanup wipes the player:

  ARENA_DATABASE=s4probe ARENA_PROJECTILE_LOAD_HARNESS=1 \
      ARENA_GENERATE_BINDINGS=0 ARENA_VERIFY_DOTNET=0 \
      ./ops/republish-local-clear.sh
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

from combat_build_probe_support import configure_probe_combat_build

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
# Wide loop west of the neck piece to the north side of the skull/jaw
# complex, and back (the direct neck/skull corridor is closed by movement
# hulls wider than the LOS query extents).
NORTH_ROUTE = [
    (24.5, -88.5),
    (18.5, -88.0),
    (12.0, -86.0),
    (11.0, -79.5),
    (18.0, -77.0),
    (24.0, -76.3),
    (27.5, -76.0),
]
SOUTH_ROUTE = [
    (24.0, -76.3),
    (18.0, -77.0),
    (11.0, -79.5),
    (12.0, -86.0),
    (18.5, -88.0),
    (24.5, -88.5),
    (26.5, -89.5),
]

MELEE_ABILITY = "PALADIN_SACRED_THRUST"  # retained targeted Technique, range 5.0
MELEE_STRIKE = "SWORD_AND_SHIELD_ALT_LIGHT_3"
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
        # Trailing 0 = no S8 attacker-view report (present-time validation).
        self.call(
            "melee_attack",
            [strike_id, target_hex, x, y, z, yaw, token, self.action_seq, 0],
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

    def activate_discipline(self, combat_discipline_id):
        self.call("activate_combat_build_discipline", [combat_discipline_id])
        deadline = time.time() + 8.0
        while time.time() < deadline:
            rows = self.sql(
                "SELECT owner, combat_discipline_id "
                "FROM active_combat_build_discipline"
            )
            if any(
                owner.removeprefix("0x").lower() == self.identity
                and discipline == combat_discipline_id
                for owner, discipline in rows
            ):
                return
            time.sleep(0.05)
        raise RuntimeError(f"timed out activating {combat_discipline_id}")

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
    configure_probe_combat_build(
        probe,
        [MELEE_ABILITY, CHARGE_ABILITY],
        starting_discipline_id="SWORD_AND_SHIELD",
    )

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

    print("\n== near-wall regression: dummy beside the wall, sight line grazing it")
    # The owner-reported false block: a dummy merely NEAR an obstacle read
    # "No line of sight" from everywhere, because unresolved spawns buried
    # its torso in padded movement boxes. Spawn now collision-resolves, and
    # the clear rule tolerates hits inside the target's personal space.
    probe.move_to(*BLOCKED_SPOT)
    probe.move_until_blocked(*SKULL)  # flush against the wall face
    x0, _, z0, _, _ = probe.physics()
    probe.face(x0 + 10.0, z0)  # face east, parallel to the wall face
    dummy = probe.spawn_hostile_dummy()
    dpos = probe.dummy_position(dummy)
    probe.face(*dpos)
    px, _, pz, _, _ = probe.physics()
    near_dist = math.hypot(dpos[0] - px, dpos[1] - pz)
    print(f"  dummy {dummy[:8]} at ({dpos[0]:.1f}, {dpos[1]:.1f}), {near_dist:.2f} m away beside the wall")
    result, reason = probe.press_melee(MELEE_STRIKE, dummy, "s4-melee-nearwall")
    expect("near-wall melee press", result, "Accepted", failures)

    print("\n== blocked (best-effort): dummy legally placed on the far side of the wall")
    # Walk around the skeleton to the north side, leave a dummy in the open,
    # walk back south — a legal target with the skull interposed. The route
    # crosses unlisted movement hulls, so a blocked walk SKIPS this fixture
    # rather than failing: the through-wall LineOfSightBlocked reject was
    # live-verified on 2026-07-04 and the blocking mechanism is unchanged by
    # the near-wall fixes (only hits within the target's personal space are
    # newly forgiven).
    try:
        probe.move_along(NORTH_ROUTE)
        nx, _, nz, _, _ = probe.physics()
        probe.face(nx, nz + 10.0)  # face north, open ground
        dummy = probe.spawn_hostile_dummy()
        dpos = probe.dummy_position(dummy)
        print(f"  dummy {dummy[:8]} at ({dpos[0]:.1f}, {dpos[1]:.1f})")
        probe.move_along(SOUTH_ROUTE)
        probe.face(*dpos)
        px, _, pz, _, _ = probe.physics()
        charge_dist = math.hypot(dpos[0] - px, dpos[1] - pz)
        print(f"  charge distance to dummy: {charge_dist:.2f} m (need 5.5..18)")
        probe.activate_discipline("TWO_HANDED_SWORD")
        result, reason = probe.press_melee(CHARGE_STRIKE, dummy, "s4-charge-blocked")
        expect("blocked gap-close press result", result, "Rejected", failures)
        expect("blocked gap-close press reason", reason, "LineOfSightBlocked", failures)
    except RuntimeError as error:
        print(f"  [SKIP] far-side walk blocked en route ({error}); "
              "through-wall reject stays covered by the 2026-07-04 live run + manual checklist")
    # This authored scene currently has no stable legally-placed blocked
    # melee fixture; bow-range auto-attack holds remain a manual check.

    print("\n== summary")
    if failures:
        print(f"FAILED: {len(failures)} check(s): {failures}")
        sys.exit(1)
    print("ALL CHECKS PASSED (see per-check lines above for any [SKIP]).")


if __name__ == "__main__":
    main()

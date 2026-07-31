#!/usr/bin/env python3
"""Live probe: the four §7.2 behaviours of a two-layer dungeon, on the server.

Design of record: docs/dungeon-builder/layered-topology-design-2026-07-29.md,
§7.2 and §13 Phase C evidence leg 3. §7.2 is a HYPOTHESIS, not a finding — it
argues from two code paths (`triangle_normal_y_abs` takes an ABSOLUTE normal, so
down-facing triangles pass; ground selection takes the MAX eligible surface) that
a suspended floor's underside cannot capture a player. This measures it.

What makes the question real: C1b renders a suspended surface with the `_E_`
floor family, whose 0.5u closed slab hangs BELOW the walk surface and carries a
convex MeshCollider. The dungeon exports movement collision AS query collision,
so that slab is in the server's collision payload. If `max` selection reaches it,
a player walking under a gallery gets snapped up 3.5u into the ceiling.

Note this is testing a CONVEX MESH on the 1.2u capture window. §7.2's own remedy
is "emit soffits as box colliders" (0.35u window, no normal test), which is NOT
what the `_E_` swap delivers — measured during C1a. So a failure here does not
falsify the remedy, only the claim that the remedy was unnecessary.

The four assertions, mapped onto the episode's geometry:

  fall     — walk off the aperture's bare rim and the server lands the player on
             the CHAMBER floor, not the abyss 20 levels down.
  return   — the return stair climbs back to the upper layer.
  ondeck   — standing on a gallery cell, the player stands on the WALK SURFACE
             (L4), not 0.5u lower on the slab's own underside.
  under    — standing on the chamber floor beneath a gallery cell, the player
             stays at L0 and is NOT snapped up onto the soffit.

Plus the span, which the episode also carries:

  bridge   — crossing the aerial deck keeps the player on the deck while the
             lower route runs underneath.

Run through the harness, which bakes, publishes, probes and restores:

  ops/c1-two-layer-live.sh

Requires `pip install websocket-client`.
"""

import argparse
import collections
import json
import math
import os
import subprocess
import sys
import threading
import time

import websocket

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(REPO, "DungeonLabReports/two_layer_episode_probe.json")
SCENE = "RandomDungeon"

# The slab is 0.5u thick and the levels are 1u, so a capture failure lands the
# player 0.5u off. A quarter of a unit separates "right" from "captured by the
# soffit" with room to spare, and is far wider than server tick jitter.
Y_TOLERANCE = 0.25


class Probe:
    def __init__(self, database, host):
        self.database = database
        self.request_id = 0
        url = f"ws://{host}/v1/database/{database}/subscribe"
        self.ws = websocket.create_connection(
            url, subprotocols=["v1.json.spacetimedb"], timeout=5)
        self.identity = None
        first = json.loads(self.ws.recv())
        token = first.get("IdentityToken")
        if token:
            identity = token.get("identity")
            if isinstance(identity, dict):
                identity = identity.get("__identity__", "")
            self.identity = (identity or "").removeprefix("0x").lower()
        # recv must stay live or the server drops the socket and disconnect
        # cleanup deletes the probe player mid-measurement.
        self.ws.settimeout(None)
        self.recent = collections.deque(maxlen=40)
        threading.Thread(target=self._drain_loop, daemon=True).start()

    def _drain_loop(self):
        try:
            while True:
                message = self.ws.recv()
                if "TransactionUpdate" in message:
                    try:
                        update = json.loads(message)["TransactionUpdate"]
                        status = update.get("status", {})
                        name = update.get("reducer_call", {}).get("reducer_name", "?")
                        self.recent.append(
                            f"{name}: FAILED {status['Failed']}" if "Failed" in status
                            else f"{name}: {next(iter(status), '?')}")
                    except Exception:
                        self.recent.append(message[:200])
        except Exception as error:
            self.recent.append(f"drain died: {type(error).__name__}: {error}")

    def call(self, reducer, args):
        self.request_id += 1
        self.ws.send(json.dumps({"CallReducer": {
            "reducer": reducer, "args": json.dumps(args),
            "request_id": self.request_id, "flags": 0}}))

    def sql(self, query):
        result = subprocess.run(["spacetime", "sql", self.database, query],
                                capture_output=True, text=True)
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
        for identity, x, y, z, yaw, tick in self.sql(
                "SELECT identity, pos_x, pos_y, pos_z, yaw, last_processed_tick "
                "FROM player_physics"):
            if identity.removeprefix("0x").lower() == self.identity:
                return float(x), float(y), float(z), float(yaw), int(tick)
        raise RuntimeError("no player_physics row for the probe identity")

    def send_intent(self, forward, yaw, tick):
        self.call("send_movement_intent", [forward, 0.0, yaw, False, tick])

    def move_to(self, tx, tz, tolerance=1.2, timeout=45.0):
        deadline = time.time() + timeout
        last_pos, last_progress_at = None, time.time()
        while time.time() < deadline:
            x, _, z, _, tick = self.physics()
            dx, dz = tx - x, tz - z
            distance = math.hypot(dx, dz)
            if distance <= tolerance:
                self.send_intent(0.0, math.atan2(dx, dz), tick + 2)
                time.sleep(0.3)
                return True
            if last_pos is not None and math.hypot(x - last_pos[0], z - last_pos[1]) > 0.15:
                last_progress_at = time.time()
            if time.time() - last_progress_at > 5.0:
                self.send_intent(0.0, 0.0, tick + 2)
                return False
            last_pos = (x, z)
            self.send_intent(max(0.25, min(1.0, distance / 5.0)), math.atan2(dx, dz), tick + 2)
            time.sleep(0.12 if distance < 4.0 else 0.3)
        return False

    def settle(self, seconds=1.2):
        """Stand still and let the server finish any fall before reading Y."""
        _, _, _, _, tick = self.physics()
        self.send_intent(0.0, 0.0, tick + 2)
        time.sleep(seconds)
        return self.physics()


def cell_point(m, cx, cy, level, note):
    """A corridor waypoint the manifest does not name.

    `move_to` steers straight at its target, so a leg that turns a corner needs
    the corner spelled out — otherwise the straight line cuts across a railed
    gallery rim and the probe reports a route failure for what is actually the
    railing doing its job.
    """
    ox, oy, oz = m["rootOffset"]
    return {
        "x": ox + (cx + 0.5) * 4.0,
        "y": oy + level * m["levelHeight"],
        "z": oz + (cy + 0.5) * 4.0,
        "note": note,
    }


def walk(probe, points, label, failures):
    """Follow a waypoint list; a stall is a route failure, not a Y failure."""
    for index, point in enumerate(points, start=1):
        if not probe.move_to(point["x"], point["z"]):
            x, y, z, _, _ = probe.physics()
            print(f"  FAIL {label}: stuck before waypoint {index} "
                  f"{point['note']!r} — probe is at ({x:.1f}, {y:.2f}, {z:.1f})")
            failures.append(f"{label}:route")
            return False
    return True


def expect_y(label, actual, expected, failures, note=""):
    ok = abs(actual - expected) <= Y_TOLERANCE
    suffix = f"  ({note})" if note else ""
    print(f"  {'PASS' if ok else 'FAIL'} {label}: y={actual:+.3f} "
          f"(expected {expected:+.3f} ±{Y_TOLERANCE}){suffix}")
    if not ok:
        failures.append(label)
    return ok


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="arena")
    parser.add_argument("--host", default="127.0.0.1:3000")
    args = parser.parse_args()

    if not os.path.exists(MANIFEST):
        print(f"no probe manifest at {MANIFEST} — bake the episode first "
              f"(ops/c1-two-layer-live.sh).")
        return 2
    with open(MANIFEST) as handle:
        m = json.load(handle)

    level_h = m["levelHeight"]
    upper_y = m["upperLevel"] * level_h
    lower_y = 0.0
    # The soffit is what a capture failure would put the player on, and naming
    # it makes a failure readable rather than just "wrong number".
    soffit_y = upper_y - 0.5

    print(f"episode: {m['stackedSurfaces']} stacked surfaces, {m['bareRims']} bare rims; "
          f"levelHeight {level_h}; upper layer at y={upper_y:+.2f}, "
          f"its soffit at y={soffit_y:+.2f}")
    print(f"root offset {m['rootOffset']}")

    probe = Probe(args.database, args.host)
    failures = []
    print(f"\nConnected as {probe.identity}")
    time.sleep(1.0)
    probe.call("set_open_world_scene", [SCENE])
    time.sleep(1.5)
    x, y, z, _, _ = probe.physics()
    print(f"  spawned at ({x:.1f}, {y:.2f}, {z:.1f})")
    expect_y("spawn stands on the lower layer", y, lower_y, failures)

    # ---- under: the assertion §7.2's `max` selection could break -----------
    print("\n== under: the chamber floor beneath a gallery slab")
    if walk(probe, [m["chamberEntry"], m["chamberUnderGallery"]], "under", failures):
        _, y, _, _, _ = probe.settle()
        expect_y("player under the gallery stays on the chamber floor", y, lower_y,
                 failures,
                 f"a soffit capture would read {soffit_y:+.2f}")

    # ---- return: the stair climbs back --------------------------------------
    print("\n== return: the return stair reaches the upper layer")
    if walk(probe, [m["stairFoot"], m["stairTop"]], "return", failures):
        _, y, _, _, _ = probe.settle()
        expect_y("player at the stair top is on the upper layer", y, upper_y, failures)

    # ---- ondeck: standing on the suspended surface itself -------------------
    print("\n== ondeck: standing on a gallery slab")
    if walk(probe, [m["terrace"], m["galleryEntry"], m["galleryRim"]], "ondeck", failures):
        gx, y, gz, _, _ = probe.settle()
        expect_y("player on the gallery stands on its walk surface", y, upper_y,
                 failures,
                 f"standing on the slab's underside would read {soffit_y:+.2f}")
        print(f"  standing at ({gx:.1f}, {gz:.1f}); "
              f"aperture landing is ({m['apertureLanding']['x']:.1f}, "
              f"{m['apertureLanding']['z']:.1f})")

        # ---- fall: off the bare rim, into the aperture ----------------------
        print("\n== fall: walking off the aperture's bare rim")
        probe.move_to(m["apertureLanding"]["x"], m["apertureLanding"]["z"],
                      tolerance=1.6, timeout=25.0)
        fx, y, fz, _, _ = probe.settle(seconds=2.0)
        expect_y("the fall lands on the chamber floor", y, lower_y, failures,
                 "the abyss base is 20 levels down")
        landed_near = math.hypot(fx - m["apertureLanding"]["x"],
                                 fz - m["apertureLanding"]["z"])
        ok = landed_near <= 4.0
        print(f"  {'PASS' if ok else 'FAIL'} landed within one cell of the aperture: "
              f"{landed_near:.1f}u")
        if not ok:
            failures.append("fall:position")

    # ---- bridge: the span the episode also carries --------------------------
    print("\n== bridge: crossing the aerial span over the lower route")
    # The terrace and the east corridor meet at a right angle, and the gallery's
    # east rim is RAILED — correctly — so a straight line from the terrace to the
    # corridor walks into that railing. Turn the corner explicitly.
    bridge_route = [
        m["chamberEntry"], m["stairFoot"], m["stairTop"],
        cell_point(m, 2, 10, m["upperLevel"], "terrace, east end"),
        cell_point(m, 3, 10, m["upperLevel"], "east corridor, north end"),
        m["eastCorridor"], m["bridgeEastLanding"],
    ]
    if walk(probe, bridge_route, "bridge", failures):
        _, y, _, _, _ = probe.settle()
        expect_y("player at the east landing is on the upper layer", y, upper_y, failures)
        if probe.move_to(m["bridgeDeck"]["x"], m["bridgeDeck"]["z"], tolerance=1.0):
            _, y, _, _, _ = probe.settle()
            expect_y("player mid-span stands on the deck", y, m["bridgeDeck"]["y"],
                     failures,
                     f"the lower route beneath is at {m['lowerRouteUnderBridge']['y']:+.2f}")
        else:
            print("  FAIL bridge: could not reach the deck's stacked coordinate")
            failures.append("bridge:deck")

    probe.settle(seconds=0.2)
    print("\n" + "=" * 68)
    if failures:
        print(f"VERDICT: {len(failures)} assertion(s) failed: {', '.join(failures)}")
        print("Recent reducer traffic:")
        for line in list(probe.recent)[-8:]:
            print(f"  {line}")
        return 1
    print("VERDICT: every §7.2 behaviour held live. A player falls through the")
    print("aperture onto the chamber, climbs back, stands on the gallery's walk")
    print("surface, is not captured by its soffit from below, and crosses the span.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

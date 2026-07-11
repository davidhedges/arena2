#!/usr/bin/env python3
"""S7 lap probe: automated chase target for the F4 adaptive-delay A/B.

Drives a headless websocket player (client_connected spawns a live player;
reducers ride the same socket; state read via `spacetime sql` —
ops/s5-input-loop-probe.py mechanics) that:

  1. moves to the requested open-world scene (set_open_world_scene),
  2. spawns one hostile kobold next to itself (spawn_npc; the measurement
     build's ARENA_NPC_NO_ATTACK=1 + ARENA_NPC_AGGRO_RADIUS=100 flags keep it
     in continuous harmless chase),
  3. runs continuous full-speed laps around a waypoint circle for the whole
     session, streaming estimate-gated movement intents (the S5 stream loop),
  4. reports chase telemetry every 5 s (kobold gap, kobold motion) and a
     final chase-continuity verdict.

The OBSERVING Unity client (same scene, standing ~45 m away so nearest-wins
aggro never picks it) produces the actual A/B evidence via
RemotePresentationAbLog + the overlay's scripted A/B run (period key).
This probe exists so nobody has to sweat settled% by hand: the kobold chases
the probe, not the tester.

The kobold is owned by the probe identity, so probe exit (disconnect
cleanup) despawns it automatically.

Run against the LIVE local database the client observes (default `arena`,
republished with the measurement flags):

  ARENA_NPC_NO_ATTACK=1 ARENA_NPC_AGGRO_RADIUS=100 ./ops/republish-local-clear.sh
  python3 ops/s7-lap-probe.py                      # defaults: arena, Desert_Day
  python3 ops/s7-lap-probe.py --seconds 480 --radius 12

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

TICK_SECONDS = 0.033
MOVE_SPEED = 7.0
KOBOLD_TEMPLATE = "KOBOLD_WARRIOR_RD_SWORD_SHIELD"


class Probe:
    def __init__(self, database, host):
        self.database = database
        self.host = host
        self.request_id = 0
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
        # disconnect cleanup deletes the probe player AND its kobold).
        self.ws.settimeout(None)
        self.recent = collections.deque(maxlen=40)
        self._drain = threading.Thread(target=self._drain_loop, daemon=True)
        self._drain.start()

    def _drain_loop(self):
        try:
            while True:
                message = self.ws.recv()
                if "TransactionUpdate" in message and "Failed" in message:
                    try:
                        update = json.loads(message)["TransactionUpdate"]
                        status = update.get("status", {})
                        name = update.get("reducer_call", {}).get("reducer_name", "?")
                        if "Failed" in status:
                            self.recent.append(f"{name}: FAILED {status['Failed']}")
                    except Exception:
                        self.recent.append(message[:200])
        except Exception as e:
            self.recent.append(f"drain died: {type(e).__name__}: {e}")

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
        rows = self.sql(
            "SELECT identity, pos_x, pos_z, last_processed_tick FROM player_physics"
        )
        for identity, x, z, tick in rows:
            if identity.removeprefix("0x").lower() == self.identity:
                return float(x), float(z), int(tick)
        raise RuntimeError("no player_physics row for the probe identity")

    def npc_positions(self):
        rows = self.sql("SELECT identity, pos_x, pos_z FROM npc_physics")
        return {
            identity.removeprefix("0x").lower(): (float(x), float(z))
            for identity, x, z in rows
        }

    def send_intent(self, forward, yaw, tick):
        self.call("send_movement_intent", [forward, 0.0, yaw, False, tick])

    def dump_recent(self):
        for entry in self.recent:
            print(f"    reducer: {entry}")


class TickEstimator:
    """Wall-clock anchor on an observed (tick, time) pair, anchored at the
    midpoint of the sql round trip; re-anchored on every physics poll so
    server cadence drift never accumulates (S5 probe mechanics)."""

    def __init__(self):
        self.anchor_tick = 0
        self.anchor_time = 0.0

    def anchor(self, tick, before, after):
        self.anchor_tick = tick
        self.anchor_time = (before + after) / 2.0

    def current(self):
        return self.anchor_tick + (time.time() - self.anchor_time) / TICK_SECONDS


def spawn_kobold(probe, template):
    probe.call("spawn_npc", [template, template, "HOSTILE"])
    time.sleep(1.0)
    rows = probe.sql("SELECT identity, template_id FROM npc_instance")
    kobolds = [r[0].removeprefix("0x").lower() for r in rows if r[1] == template]
    if not kobolds:
        probe.dump_recent()
        raise RuntimeError(f"kobold did not spawn (npc_instance rows: {rows})")
    kobold = kobolds[-1]
    probe.call("set_npc_target_override", [kobold, probe.identity])
    return kobold


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="arena",
                        help="live database the observing client is on (default: arena)")
    parser.add_argument("--host", default="localhost:3000")
    parser.add_argument("--scene", default="Desert_Day",
                        help="open-world scene (must match the observing client)")
    parser.add_argument("--seconds", type=float, default=1800.0,
                        help="lap-duration CAP. The probe must OUTLIVE the scripted run — "
                             "a mid-run timeout despawns the kobold and counter-resets the "
                             "leg it dies in (burned a leg on 2026-07-04). Leave the default "
                             "and Ctrl-C after the overlay shows COMPLETE.")
    # Circuit defaults were computed from the authored movement-collision
    # data (desert_day.collision.shared.json): largest clear disc (ring+4 m,
    # 2 m prop margin) with a collision-free corridor from the scene spawn.
    # A ring around the spawn itself is wall-blocked to the south — do not
    # default to (0, 0).
    parser.add_argument("--radius", type=float, default=14.0,
                        help="lap circle radius in meters")
    parser.add_argument("--center-dx", type=float, default=3.0,
                        help="lap circle center offset east of the scene spawn")
    parser.add_argument("--center-dz", type=float, default=30.0,
                        help="lap circle center offset north of the scene spawn")
    parser.add_argument("--waypoints", type=int, default=8)
    parser.add_argument("--lead", type=int, default=3,
                        help="intent stream lead in ticks (raise to 4-5 under heavy shaping)")
    parser.add_argument("--template", default=KOBOLD_TEMPLATE)
    parser.add_argument("--no-spawn", action="store_true",
                        help="do not spawn a kobold (one is already chasing)")
    args = parser.parse_args()

    probe = Probe(args.database, args.host)
    print(f"probe identity: {probe.identity}")
    time.sleep(1.0)  # let client_connected finish spawning

    print(f"== setup: scene {args.scene}")
    probe.call("set_open_world_scene", [args.scene])
    time.sleep(1.5)
    x0, z0, _ = probe.physics()
    print(f"   probe at scene spawn ({x0:.1f}, {z0:.1f})")

    cx, cz = x0 + args.center_dx, z0 + args.center_dz
    ring = [
        (cx + args.radius * math.sin(2 * math.pi * i / args.waypoints),
         cz + args.radius * math.cos(2 * math.pi * i / args.waypoints))
        for i in range(args.waypoints)
    ]
    leg_len = 2 * args.radius * math.sin(math.pi / args.waypoints)
    print(f"== laps: {args.waypoints}-point circle r={args.radius:.0f} m at ({cx:.1f}, {cz:.1f}), "
          f"~{2 * math.pi * args.radius / MOVE_SPEED:.0f} s/lap, for {args.seconds:.0f} s")

    est = TickEstimator()
    before = time.time()
    px, pz, tick = probe.physics()
    est.anchor(tick, before, time.time())
    target_tick = int(est.current()) + args.lead

    # Walk to the circuit before spawning so the fixture starts beside the
    # measured route. spawn_kobold pins its target explicitly to this probe.
    print("   walking to the circuit before spawning the pinned kobold")
    walk_deadline = time.time() + math.hypot(ring[0][0] - px, ring[0][1] - pz) / MOVE_SPEED + 15.0
    while time.time() < walk_deadline:
        before = time.time()
        px, pz, tick = probe.physics()
        est.anchor(tick, before, time.time())
        dx, dz = ring[0][0] - px, ring[0][1] - pz
        if math.hypot(dx, dz) <= 3.0:
            break
        yaw = math.atan2(dx, dz)
        desired = int(est.current()) + args.lead
        while target_tick < desired:
            target_tick += 1
            probe.send_intent(1.0, yaw, target_tick)
        time.sleep(0.1)
    else:
        raise RuntimeError("could not reach the circuit start — check --center-dx/--center-dz")

    kobold_hex = None
    if not args.no_spawn:
        kobold_hex = spawn_kobold(probe, args.template)
        print(f"   kobold spawned at the circuit: {kobold_hex[:12]}… "
              "(hostile, chase-only measurement build)")
    else:
        npcs = probe.npc_positions()
        if npcs:
            kobold_hex = next(iter(npcs))
            print(f"   using existing NPC {kobold_hex[:12]}…")
        else:
            print("   WARNING: --no-spawn but no NPC found; chase telemetry disabled")

    print("   (start the observing client's scripted A/B run now — overlay backslash, then period)")
    wp_index = 1 % args.waypoints
    laps = 0
    skips = 0
    wp_deadline = time.time() + leg_len / MOVE_SPEED + 8.0
    next_poll = 0.0
    next_telemetry = time.time() + 5.0
    last_kobold = None
    kobold_samples = 0
    kobold_moving = 0
    gaps = []

    deadline = time.time() + args.seconds
    try:
        while time.time() < deadline:
            now = time.time()

            if now >= next_poll:
                before = now
                px, pz, tick = probe.physics()
                est.anchor(tick, before, time.time())
                next_poll = time.time() + 0.35

                wx, wz = ring[wp_index]
                if math.hypot(wx - px, wz - pz) <= 3.5:
                    wp_index = (wp_index + 1) % args.waypoints
                    if wp_index == 0:
                        laps += 1
                    wp_deadline = time.time() + leg_len / MOVE_SPEED + 8.0
                elif time.time() > wp_deadline:
                    print(f"   [stuck] waypoint {wp_index} unreachable from "
                          f"({px:.1f}, {pz:.1f}) — skipping (adjust --center-dx/--center-dz/--radius "
                          "if this repeats)")
                    wp_index = (wp_index + 1) % args.waypoints
                    skips += 1
                    wp_deadline = time.time() + leg_len / MOVE_SPEED + 8.0

            wx, wz = ring[wp_index]
            yaw = math.atan2(wx - px, wz - pz)
            desired = int(est.current()) + args.lead
            while target_tick < desired:
                target_tick += 1
                probe.send_intent(1.0, yaw, target_tick)

            if now >= next_telemetry and kobold_hex is not None:
                next_telemetry = now + 5.0
                try:
                    npcs = probe.npc_positions()
                except RuntimeError:
                    npcs = {}
                pos = npcs.get(kobold_hex)
                if pos is not None:
                    gap = math.hypot(pos[0] - px, pos[1] - pz)
                    gaps.append(gap)
                    kobold_samples += 1
                    moved = (last_kobold is not None
                             and math.hypot(pos[0] - last_kobold[0], pos[1] - last_kobold[1]) > 2.0)
                    if moved or last_kobold is None:
                        kobold_moving += 1
                    flag = "" if (moved or last_kobold is None) else "  [KOBOLD STALLED]"
                    print(f"   t+{args.seconds - (deadline - now):4.0f}s  laps={laps} "
                          f"probe=({px:6.1f},{pz:6.1f})  kobold gap={gap:5.1f} m{flag}")
                    last_kobold = pos
                else:
                    print(f"   t+{args.seconds - (deadline - now):4.0f}s  KOBOLD ROW MISSING")

            time.sleep(0.005)
    except KeyboardInterrupt:
        print("\n   interrupted — stopping laps")

    # Park the probe so its player reads settled, not starved, after the run.
    px, pz, tick = probe.physics()
    probe.send_intent(0.0, 0.0, tick + 2)

    print("\n== summary")
    print(f"   laps completed: {laps}  waypoint skips: {skips}")
    if kobold_samples:
        moving_share = kobold_moving / kobold_samples
        gaps_sorted = sorted(gaps)
        print(f"   kobold: moving in {kobold_moving}/{kobold_samples} samples "
              f"({moving_share:.0%}); gap min/median/max = "
              f"{gaps_sorted[0]:.1f}/{gaps_sorted[len(gaps_sorted) // 2]:.1f}/{gaps_sorted[-1]:.1f} m")
        ok = moving_share >= 0.90 and gaps_sorted[len(gaps_sorted) // 2] < 30.0
        print(f"   [{'PASS' if ok else 'FAIL'}] continuous chase "
              "(moving >= 90% of samples, median gap < 30 m)")
        print("   probe exiting — disconnect cleanup despawns the probe player and its kobold")
        return 0 if ok else 1
    print("   no kobold telemetry recorded")
    return 1


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Live probe: a player walks onto a random-dungeon trap and takes the hit.

Drives a headless websocket player (client_connected spawns a live player;
reducers ride the same socket; state read via `spacetime sql`) from the
RandomDungeon spawn to a generated trap, and measures what the server actually
does rather than what the design says it should:

  dormant   — with the probe at spawn, the trap state table is EMPTY. Rows exist
              only while a trap is firing, which is the whole replication claim.
  arm       — stepping onto the plate inserts exactly one row for that trap, and
              the row carries the cycle anchor the client scrubs from.
  telegraph — no damage lands during trigger_delay_ms + hazard_start_ms. For
              TRAP_SPIKES that is 530 ms of warning, the §9.3 reaction window.
  hit       — hp drops by the authored amount, and the damage is emitted as a
              system-source combat_effect_event (source Identity::ZERO).
  once      — standing still through one whole cycle yields exactly ONE hit;
              the second hit only arrives after the trap re-arms.
  rest      — the row is gone once the cycle ends and the probe has left.

The route is computed from the SAVED SCENE (floor cells plus stair links), so
this works on any rebuilt dungeon without hand-recorded coordinates.

Run against the live local module (the trap manifest is compiled into it):

  ops/rebuild-dungeon-traps.sh && ops/republish-local-clear.sh
  python3 ops/trap-probe.py --database arena

Requires `pip install websocket-client` (scratch venv is fine).
"""

import argparse
import collections
import heapq
import json
import math
import os
import re
import subprocess
import sys
import threading
import time

import websocket

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCENE_PATH = os.path.join(REPO, "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity")
TRAP_MANIFEST = os.path.join(
    REPO, "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.traps.shared.json")
PROFILE_MANIFEST = os.path.join(
    REPO, "Assets/Arena/Resources/SharedData/WorldInteractions/"
          "world_trap_profiles.shared.json")

SCENE = "RandomDungeon"
CELL_SIZE = 4.0
# RANDOM_DUNGEON_PROFILE spawns at the world origin (open_world_scene.rs).
SPAWN = (0.0, 0.0)


# --------------------------------------------------------------------------
# Route planning from the built scene
# --------------------------------------------------------------------------

def read_scene_graph():
    """Floor cells and the walkable graph: same-level neighbours plus stairs."""
    with open(SCENE_PATH, errors="ignore") as handle:
        text = handle.read()
    floors = {}
    for match in re.finditer(r"floor_(-?\d+)_(-?\d+)_level_(-?\d+)", text):
        floors[(int(match.group(1)), int(match.group(2)))] = int(match.group(3))

    adjacency = collections.defaultdict(set)
    for cell, level in floors.items():
        for dx, dz in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            neighbor = (cell[0] + dx, cell[1] + dz)
            if floors.get(neighbor) == level:
                adjacency[cell].add(neighbor)
                adjacency[neighbor].add(cell)
    for name in set(re.findall(r"transition_stair_[A-Za-z0-9_]+", text)):
        match = re.search(r"_(\d+)_(\d+)_to_(\d+)_(\d+)_", name)
        if not match:
            continue
        a = (int(match.group(1)), int(match.group(2)))
        b = (int(match.group(3)), int(match.group(4)))
        if a in floors and b in floors:
            adjacency[a].add(b)
            adjacency[b].add(a)
    return floors, adjacency


def solve_grid_offset(traps):
    votes = collections.Counter()
    for trap in traps:
        parts = trap["trap_definition_id"].split(":")
        if len(parts) != 6:
            continue
        votes[(round(trap["origin"]["x"] - int(parts[3]) * CELL_SIZE, 3),
               round(trap["origin"]["z"] - int(parts[4]) * CELL_SIZE, 3))] += 1
    return votes.most_common(1)[0][0] if votes else (0.0, 0.0)


def plan_route():
    traps = json.load(open(TRAP_MANIFEST))["traps"]
    profiles = {p["profile_id"]: p for p in json.load(open(PROFILE_MANIFEST))["profiles"]}
    if not traps:
        raise SystemExit("No traps in the manifest — rebuild the dungeon first "
                         "(ops/rebuild-dungeon-traps.sh).")

    floors, adjacency = read_scene_graph()
    offset = solve_grid_offset(traps)

    def cell_of(x, z):
        return (round((x - offset[0]) / CELL_SIZE), round((z - offset[1]) / CELL_SIZE))

    def world_of(cell):
        return (cell[0] * CELL_SIZE + offset[0], cell[1] * CELL_SIZE + offset[1])

    spawn_cell = cell_of(*SPAWN)
    dist, prev, queue = {spawn_cell: 0}, {spawn_cell: None}, [(0, spawn_cell)]
    while queue:
        d, cur = heapq.heappop(queue)
        if d > dist.get(cur, 1 << 30):
            continue
        for neighbor in adjacency.get(cur, ()):
            if d + 1 < dist.get(neighbor, 1 << 30):
                dist[neighbor] = d + 1
                prev[neighbor] = cur
                heapq.heappush(queue, (d + 1, neighbor))

    # Prefer a full-cell spike field: its 4x4 trigger volume makes "standing on
    # it" unambiguous, and it is the only kind carrying a telegraph to measure.
    candidates = []
    for trap in traps:
        cell = cell_of(trap["origin"]["x"], trap["origin"]["z"])
        if cell in dist:
            candidates.append((trap["trap_profile_id"] != "TRAP_SPIKES", dist[cell], trap, cell))
    if not candidates:
        raise SystemExit("No trap is walkable from the spawn on this dungeon — "
                         "rebuild for a different seed, or extend the route graph.")
    candidates.sort(key=lambda row: (row[0], row[1]))
    _, steps, trap, cell = candidates[0]

    path, cur = [], cell
    while cur:
        path.append(cur)
        cur = prev[cur]
    path.reverse()

    points = [world_of(p) for p in path]
    waypoints = [points[0]]
    for i in range(1, len(points) - 1):
        before = (points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1])
        after = (points[i + 1][0] - points[i][0], points[i + 1][1] - points[i][1])
        if before != after:
            waypoints.append(points[i])
    waypoints.append(points[-1])
    return trap, profiles[trap["trap_profile_id"]], waypoints, steps


# --------------------------------------------------------------------------
# Probe
# --------------------------------------------------------------------------

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

    def hp(self):
        for player_id, hp, max_hp in self.sql(
                "SELECT player_id, hp, max_hp FROM player_state"):
            if player_id.removeprefix("0x").lower() == self.identity:
                return int(hp), int(max_hp)
        raise RuntimeError("no player_state row for the probe identity")

    def trap_rows(self):
        return self.sql(
            "SELECT trap_definition_id, cycle_started_at, activation, cycle_ends_at_micros "
            "FROM world_trap_state")

    def trap_damage_events(self):
        rows = []
        for spell_id, effect_type, source, target, base, final, micros in self.sql(
                "SELECT spell_id, effect_type, source, target, base_amount, final_amount, "
                "created_at_micros FROM combat_effect_event"):
            if target.removeprefix("0x").lower() != self.identity:
                continue
            if not spell_id.startswith("TRAP_"):
                continue
            rows.append({
                "spell_id": spell_id, "effect_type": effect_type,
                "source": source.removeprefix("0x").lower(),
                "base": int(base), "final": int(final), "micros": int(micros),
            })
        return sorted(rows, key=lambda row: row["micros"])

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

    def stop(self):
        _, _, _, _, tick = self.physics()
        self.send_intent(0.0, 0.0, tick + 2)


def expect(label, actual, expected, failures):
    ok = actual == expected
    print(f"  {'PASS' if ok else 'FAIL'} {label}: {actual!r}"
          + ("" if ok else f" (expected {expected!r})"))
    if not ok:
        failures.append(label)
    return ok


def expect_between(label, actual, low, high, failures):
    ok = low <= actual <= high
    print(f"  {'PASS' if ok else 'FAIL'} {label}: {actual}"
          + ("" if ok else f" (expected {low}..{high})"))
    if not ok:
        failures.append(label)
    return ok


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="arena")
    parser.add_argument("--host", default="127.0.0.1:3000")
    args = parser.parse_args()

    trap, profile, waypoints, steps = plan_route()
    trap_id = trap["trap_definition_id"]
    telegraph_ms = profile["trigger_delay_ms"] + profile["hazard_start_ms"]
    cycle_total_ms = profile["trigger_delay_ms"] + profile["cycle_ms"] + profile["rearm_ms"]
    expected_damage = next(
        (entry["amount"] for entry in profile["on_hit"] if entry["effect"] == "DAMAGE"), 0)
    print(f"target trap      {trap_id}")
    print(f"  profile        {profile['profile_id']}  telegraph {telegraph_ms} ms, "
          f"cycle {cycle_total_ms} ms, damage {expected_damage}")
    print(f"  route          {steps} cells, {len(waypoints)} waypoints, "
          f"ends at ({trap['origin']['x']:.0f}, {trap['origin']['z']:.0f})")

    probe = Probe(args.database, args.host)
    failures = []
    print(f"\nConnected as {probe.identity}")
    time.sleep(1.0)
    probe.call("set_open_world_scene", [SCENE])
    time.sleep(1.5)
    x, _, z, _, _ = probe.physics()
    print(f"  spawned at ({x:.1f}, {z:.1f})")

    print("\n== dormant: no trap is firing while the probe stands at spawn")
    time.sleep(1.0)
    expect("trap state rows at spawn", len(probe.trap_rows()), 0, failures)

    print(f"\n== walk: {len(waypoints)} waypoints to the plate")
    baseline_hp, max_hp = probe.hp()
    for index, (wx, wz) in enumerate(waypoints[1:], start=1):
        if not probe.move_to(wx, wz):
            x, _, z, _, _ = probe.physics()
            print(f"  FAIL stuck at waypoint {index} ({wx:.0f}, {wz:.0f}); "
                  f"probe is at ({x:.1f}, {z:.1f})")
            failures.append("route")
            break
    else:
        x, _, z, _, _ = probe.physics()
        print(f"  reached ({x:.1f}, {z:.1f}); plate centre is "
              f"({trap['origin']['x']:.1f}, {trap['origin']['z']:.1f}); "
              f"hp {baseline_hp}/{max_hp} on arrival")

    print("\n== arm: the plate inserts exactly one state row")
    armed_row = None
    deadline = time.time() + 8.0
    while time.time() < deadline:
        rows = [row for row in probe.trap_rows() if row[0] == trap_id]
        if rows:
            armed_row = rows[0]
            break
        time.sleep(0.05)
    if armed_row is None:
        print("  FAIL no state row appeared within 8 s of standing on the plate")
        failures.append("arm")
        probe.stop()
        return 1
    activation = int(armed_row[2])
    print(f"  row: activation {activation}, cycle_started_at {armed_row[1]}")
    expect("state rows for this trap", len(
        [row for row in probe.trap_rows() if row[0] == trap_id]), 1, failures)

    # Wall-clock polling through `spacetime sql` costs ~200 ms a call, which is
    # the same order as the telegraph being measured. Every timing assertion
    # below therefore compares SERVER timestamps: `activation` is the cycle
    # anchor in micros and combat_effect_event carries created_at_micros.
    print(f"\n== telegraph + hit: damage must wait out {telegraph_ms} ms of warning")
    hit = None
    deadline = time.time() + cycle_total_ms / 1000.0 + 6.0
    while time.time() < deadline:
        for event in probe.trap_damage_events():
            if event["micros"] >= activation:
                hit = event
                break
        if hit:
            break
        time.sleep(0.1)

    if hit is None:
        print("  FAIL no trap damage landed within a full cycle of arming")
        failures.append("hit")
    else:
        delay_ms = (hit["micros"] - activation) / 1000.0
        current_hp, _ = probe.hp()
        print(f"  event: {hit['spell_id']} {hit['effect_type']} base {hit['base']} "
              f"final {hit['final']} source {hit['source'][:8]} at +{delay_ms:.0f} ms")
        print(f"  hp now {current_hp}/{max_hp}")
        expect("damage amount", hit["final"], expected_damage, failures)
        expect("event spell id", hit["spell_id"], profile["profile_id"], failures)
        expect("event source is the system (Identity::ZERO)",
               set(hit["source"]) <= {"0"}, True, failures)
        # One server tick (33 ms) of slack on the late side; the hazard window
        # runs to hazard_end_ms, so anything inside it is legitimate.
        expect_between("damage waited out the telegraph (ms)", int(delay_ms),
                       telegraph_ms - 34,
                       profile["trigger_delay_ms"] + profile["hazard_end_ms"] + 34,
                       failures)

        print("\n== once: one activation deals exactly one hit")
        window_end = activation + cycle_total_ms * 1000
        in_window = [event for event in probe.trap_damage_events()
                     if activation <= event["micros"] < window_end]
        expect("hits inside this activation window", len(in_window), 1, failures)

        print("\n== rearm: the next activation lands another hit")
        deadline = time.time() + cycle_total_ms / 1000.0 + 8.0
        later = []
        while time.time() < deadline:
            later = [event for event in probe.trap_damage_events()
                     if event["micros"] >= window_end]
            if later:
                break
            time.sleep(0.2)
        if later:
            gap_ms = (later[0]["micros"] - hit["micros"]) / 1000.0
            print(f"  second hit {gap_ms:.0f} ms after the first "
                  f"(cycle is {cycle_total_ms} ms)")
        expect("a second activation landed", len(later) >= 1, True, failures)

    print("\n== rest: leaving the plate lets the row expire")
    start_x, _, start_z, _, _ = probe.physics()
    probe.move_to(waypoints[-2][0], waypoints[-2][1])
    deadline = time.time() + cycle_total_ms / 1000.0 + 6.0
    cleared = False
    while time.time() < deadline:
        if not [row for row in probe.trap_rows() if row[0] == trap_id]:
            cleared = True
            break
        time.sleep(0.2)
    expect("state row cleared after leaving", cleared, True, failures)
    probe.stop()

    print()
    if failures:
        print(f"FAIL: {len(failures)} check(s) — {', '.join(failures)}")
        for entry in probe.recent:
            print(f"    reducer: {entry}")
        return 1
    print("OK: traps arm on proximity, telegraph, damage once per activation, "
          "re-arm, and go dormant.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

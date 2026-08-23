#!/usr/bin/env python3
"""Live probe: a special movement must not leave the server free-running input.

Drives a headless websocket player (client_connected spawns a live player;
reducers ride the same socket; state read via `spacetime sql`) and verifies the
post-special-movement handoff contract on the running module:

  handoff-axes   — after the runtime ends, player_intent.forward/strafe are
                   ZERO even though the player was streaming forward = 1.0
                   right up to the dash. A retained axis free-runs the ticks
                   between the handoff and the client's first re-anchored
                   command against the newly imposed facing yaw: motion the
                   client's replay model does not contain, which lands as a
                   post-dash slide once the correction arrives.
  handoff-still  — with no commands sent after the runtime ends, the server
                   is not still DRIVING the player: horizontal velocity stays
                   zero for the whole window. Velocity rather than travelled
                   distance, because a dash that ends against a wall pins the
                   position while the server happily holds 7 m/s into it —
                   measured live on the pre-fix module, which retained the
                   axis and read vel=(0, -7.0) at a standstill.
  handoff-yaw    — the handoff keeps the runtime's facing yaw, so the arrival
                   facing is what the next command turns FROM.

  drive-control  — positive control, run first: the streamed commands actually
                   move the player. Without it handoff-still passes vacuously
                   on a probe player that could not move in the first place.

Run against a throwaway DB — disconnect cleanup wipes the probe player, and a
one-shot `spacetime call` cannot leave per-identity state:

  spacetime publish --delete-data=always --yes -p server smhprobe
  python3 ops/special-movement-handoff-probe.py --database smhprobe
  spacetime delete smhprobe

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
# The dash is short; poll fast enough to catch the tick it ends on.
RUNTIME_POLL_SECONDS = 0.02
# Free-run evidence window. The client's re-anchor hole is ~5-9 ticks, so half
# a second is comfortably longer than the window a retained axis would move in.
SETTLE_SECONDS = 0.5
# Server writes positions as f32 text; anything under a millimetre is noise.
STILL_TOLERANCE_METERS = 0.001
# Ground velocity is written from the intent, so a genuine standstill is exact
# to within f32 sign noise on the trig.
STILL_TOLERANCE_METERS_PER_SECOND = 0.01


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
        # disconnect cleanup deletes the probe player).
        self.ws.settimeout(None)
        self.failures = collections.deque(maxlen=40)
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
                    name = update.get("reducer_call", {}).get("reducer_name", "?")
                    if "Failed" in status:
                        self.failures.append(f"{name}: {status['Failed']}")
                except Exception:
                    self.failures.append(message[:200])
        except Exception as e:
            self.failures.append(f"drain died: {type(e).__name__}: {e}")

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
        result = subprocess.run(
            ["spacetime", "sql", self.database, query],
            capture_output=True,
            text=True,
        )
        if result.returncode != 0:
            sys.stderr.write(result.stderr)
            raise RuntimeError(f"spacetime sql failed: {query}")
        rows = []
        for line in result.stdout.splitlines():
            if "|" not in line or set(line.strip()) <= {"-", "+", "|", " "}:
                continue
            rows.append([cell.strip().strip('"') for cell in line.split("|")])
        return rows[1:] if rows else []  # drop header row

    def _mine(self, rows):
        for row in rows:
            if row[0].removeprefix("0x").lower() == self.identity:
                return row
        return None

    def physics(self):
        row = self._mine(
            self.sql(
                "SELECT identity, pos_x, pos_y, pos_z, yaw, last_processed_tick,"
                " vel_x, vel_z FROM player_physics"
            )
        )
        if row is None:
            raise RuntimeError("no player_physics row for the probe identity")
        return {
            "pos": (float(row[1]), float(row[2]), float(row[3])),
            "yaw": float(row[4]),
            "tick": int(row[5]),
            "vel": (float(row[6]), float(row[7])),
        }

    def intent(self):
        row = self._mine(
            self.sql("SELECT identity, forward, strafe, yaw FROM player_intent")
        )
        if row is None:
            raise RuntimeError("no player_intent row for the probe identity")
        return {
            "forward": float(row[1]),
            "strafe": float(row[2]),
            "yaw": float(row[3]),
        }

    def movement_blocked(self):
        row = self._mine(
            self.sql("SELECT player_id, movement_blocked, alive FROM player_state")
        )
        return None if row is None else row[1] == "true"

    def special_movement(self):
        return self._mine(
            self.sql("SELECT owner, kind, facing_yaw_start FROM special_movement_runtime")
        )

    def send_intent(self, forward, strafe, yaw, tick):
        self.call("send_movement_intent", [forward, strafe, yaw, False, tick])

    def start_dodge(self, physics, forward, strafe):
        pos_x, pos_y, pos_z = physics["pos"]
        self.call(
            "start_dodge",
            [
                physics["tick"],
                physics["tick"],
                pos_x,
                pos_y,
                pos_z,
                physics["yaw"],
                forward,
                strafe,
                "",
                0,
            ],
        )


def check(name, ok, detail):
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    return ok


def wait_for_player(probe, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            return probe.physics()
        except RuntimeError:
            time.sleep(0.2)
    raise RuntimeError("probe player never spawned")


def stream_forward(probe, seconds, yaw, lead=3):
    """Streams forward = 1.0 along `yaw` so the fallback intent is a moving one.

    Re-anchors the tick number on every sql round trip: the server's real
    cadence drifts from an ideal 33 ms, and a command numbered past the accept
    window is rejected outright rather than buffered.
    """
    deadline = time.time() + seconds
    while time.time() < deadline:
        tick = probe.physics()["tick"] + lead
        for _ in range(8):
            if time.time() >= deadline:
                break
            probe.send_intent(1.0, 0.0, yaw, tick)
            tick += 1
            time.sleep(TICK_SECONDS)


def wait_for_runtime(probe, present, timeout=5.0):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        row = probe.special_movement()
        if (row is not None) == present:
            return row if row is not None else last
        last = row or last
        time.sleep(RUNTIME_POLL_SECONDS)
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", required=True)
    parser.add_argument("--host", default="127.0.0.1:3000")
    args = parser.parse_args()

    probe = Probe(args.database, args.host)
    print(f"probe identity: {probe.identity}")
    wait_for_player(probe)

    # Walk out along +Z first, purely to prove the return leg has open ground:
    # a retained axis cannot slide a player who is already against a wall, and
    # handoff-still would then pass for the wrong reason.
    outbound_start = probe.physics()
    stream_forward(probe, seconds=1.5, yaw=0.0)
    outbound_end = probe.physics()
    headroom = math.dist(outbound_start["pos"], outbound_end["pos"])

    # Hold a moving intent right up to the dash, then stop authoring entirely —
    # exactly what the real client does at the boundary, where it discards its
    # in-flight input and re-anchors command numbering forward. The dash and
    # the free-run window both face back down the corridor we just walked.
    freerun_yaw = math.pi
    stream_forward(probe, seconds=0.6, yaw=freerun_yaw)
    before = probe.intent()
    physics = probe.physics()
    driven = math.dist(outbound_end["pos"], physics["pos"])
    ok_control = check(
        "drive-control",
        driven > 0.3 and headroom > 1.0,
        f"streamed commands moved the player {driven:.3f}m back down"
        f" {headroom:.3f}m of proven-open ground (now={physics['pos']})",
    )
    if not ok_control:
        print("  handoff-still cannot be trusted without this; aborting.")
        for failure in probe.failures:
            print(f"    reducer failure: {failure}")
        return 1

    probe.start_dodge(physics, forward=1.0, strafe=0.0)

    runtime = wait_for_runtime(probe, present=True)
    if runtime is None:
        print("  [FAIL] dodge never opened a special_movement_runtime row")
        for failure in probe.failures:
            print(f"    reducer failure: {failure}")
        return 1
    runtime_yaw = float(runtime[2])
    print(f"  runtime opened: kind={runtime[1]} facing_yaw_start={runtime_yaw:.4f}")

    if wait_for_runtime(probe, present=False) is None and probe.special_movement():
        print("  [FAIL] special_movement_runtime never cleared")
        return 1
    at_end = probe.physics()
    handoff = probe.intent()

    # No commands from here on: whatever moves the player now is the server
    # free-running its fallback intent. Sample the whole window rather than
    # only its endpoints — a free-run that runs out of open ground mid-window
    # would otherwise read as "never moved".
    trace = []
    settled = at_end
    deadline = time.time() + SETTLE_SECONDS
    while time.time() < deadline:
        settled = probe.physics()
        trace.append(settled)
        time.sleep(0.05)
    drift = max(math.dist(at_end["pos"], sample["pos"]) for sample in trace)
    speed = max(math.hypot(*sample["vel"]) for sample in trace)
    ticked = trace[-1]["tick"] - at_end["tick"]
    print(
        f"  settle window: {len(trace)} samples, {ticked} server ticks,"
        f" blocked={probe.movement_blocked()}, max drift {drift:.4f}m,"
        f" max speed {speed:.4f}m/s, intent={probe.intent()}"
    )

    print(f"streamed intent before dash: forward={before['forward']:.3f} strafe={before['strafe']:.3f}")
    ok = True
    ok &= check(
        "handoff-axes",
        handoff["forward"] == 0.0 and handoff["strafe"] == 0.0,
        f"forward={handoff['forward']:.3f} strafe={handoff['strafe']:.3f} (want 0/0)",
    )
    ok &= check(
        "handoff-still",
        speed <= STILL_TOLERANCE_METERS_PER_SECOND
        and drift <= STILL_TOLERANCE_METERS,
        f"max speed={speed:.4f}m/s, max drift={drift:.4f}m over"
        f" {SETTLE_SECONDS:.1f}s with no commands sent"
        f" (end={at_end['pos']} last={settled['pos']})",
    )
    ok &= check(
        "handoff-yaw",
        abs(handoff["yaw"] - runtime_yaw) < 0.001,
        f"intent yaw={handoff['yaw']:.4f} runtime facing={runtime_yaw:.4f}",
    )

    for failure in probe.failures:
        print(f"    reducer failure: {failure}")
    print("RESULT:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())

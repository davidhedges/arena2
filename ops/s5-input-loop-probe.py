#!/usr/bin/env python3
"""S5 live probe: the server side of closed-loop input buffering.

Drives a headless websocket player (client_connected spawns a live player;
reducers ride the same socket; state read via `spacetime sql`) and verifies
the S5 server contract live:

  ack-idle    — with no commands in flight, every tick reports
                last_tick_consumed_command = false, buffered_command_count = 0
                (the server tells the truth about fallback).
  ack-stream  — streaming one intent per tick at a ~3-tick lead, sampled acks
                report consumed = true for the overwhelming majority of ticks
                and buffer occupancy in the setpoint neighbourhood (1..4).
  ack-burst   — sending 6 ticks ahead in one burst reads back as occupancy
                >= 4 (the surplus signal the client loop lowers lead from).
  jump-slide  — jumps sent at the exact estimated consume boundary land: some
                arrive with input_tick == last_processed_tick and are slid one
                tick ([MOVE_JUMP_SLIDE] in the module log) instead of eaten;
                every slide is followed by an authoritative jump (grounded
                drops with upward vel_y). Jumps 3+ ticks stale stay dropped
                (informational count).
  no-dup      — while streaming and boundary-jumping concurrently, the
                player_command queue never holds two rows for one input tick
                (the slide/merge upsert rule).

The client half of S5 (lead convergence, correction presentation) runs in the
Unity editor — see docs/latency-testing.md for the shaped acceptance run.

Run against a throwaway DB — one-shot `spacetime call` cannot leave
per-identity state, and disconnect cleanup wipes the player:

  cargo build --manifest-path server/Cargo.toml --target wasm32-unknown-unknown \
      --release --features projectile_load_harness
  spacetime publish --delete-data=always --yes \
      --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm s5probe
  python3 ops/s5-input-loop-probe.py --database s5probe
  spacetime delete s5probe

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
            "SELECT identity, grounded, vel_y, last_processed_tick,"
            " last_tick_consumed_command, buffered_command_count FROM player_physics"
        )
        for identity, grounded, vel_y, tick, consumed, buffered in rows:
            if identity.removeprefix("0x").lower() == self.identity:
                return {
                    "grounded": grounded == "true",
                    "vel_y": float(vel_y),
                    "tick": int(tick),
                    "consumed": consumed == "true",
                    "buffered": int(buffered),
                }
        raise RuntimeError("no player_physics row for the probe identity")

    def command_tick_duplicates(self):
        rows = self.sql("SELECT identity, input_tick FROM player_command")
        ticks = [
            int(t)
            for ident, t in rows
            if ident.removeprefix("0x").lower() == self.identity
        ]
        return [t for t, n in collections.Counter(ticks).items() if n > 1]

    def send_intent(self, forward, yaw, jump, tick):
        self.call("send_movement_intent", [forward, 0.0, yaw, jump, tick])

    def module_log_slides(self):
        result = subprocess.run(
            ["spacetime", "logs", self.database],
            capture_output=True,
            text=True,
        )
        return [
            line for line in result.stdout.splitlines() if "MOVE_JUMP_SLIDE" in line
        ]


class TickEstimator:
    """Wall-clock anchor on an observed (tick, time) pair, anchored at the
    midpoint of the sql round trip. Server tick cadence drifts from an ideal
    33 ms under load, so long-lived users must re-anchor (see stream loop)."""

    def __init__(self, probe):
        before = time.time()
        state = probe.physics()
        after = time.time()
        self.anchor_tick = state["tick"]
        self.anchor_time = (before + after) / 2.0
        self.last_state = state

    def current(self):
        return self.anchor_tick + (time.time() - self.anchor_time) / TICK_SECONDS


def check(name, ok, detail):
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    return ok


def scenario_ack_idle(probe):
    print("== ack-idle: no commands in flight")
    time.sleep(1.5)  # let any prior intent stream drain past the cursor
    samples = [probe.physics() for _ in range(6)]
    fallback = [s for s in samples if not s["consumed"]]
    empty = [s for s in samples if s["buffered"] == 0]
    ok = check(
        "idle ticks report fallback truthfully",
        len(fallback) == len(samples),
        f"{len(fallback)}/{len(samples)} samples consumed=false",
    )
    ok &= check(
        "idle buffer occupancy is zero",
        len(empty) == len(samples),
        f"{len(empty)}/{len(samples)} samples buffered=0",
    )
    return ok


def scenario_ack_stream(probe, seconds=8.0, lead=3):
    """Miniature client loop: sends are gated on the re-anchored tick
    estimate (send only while the next number stays within estimate+lead),
    which is the skip actuation the S5 client runs — a pure wall-clock sender
    accumulates surplus whenever the server tick cadence drifts from 33 ms."""
    print(f"== ack-stream: estimate-gated intents at lead {lead} for {seconds:.0f}s")
    est = TickEstimator(probe)
    samples = []
    end = time.time() + seconds
    next_sample = time.time() + 1.0  # skip the warmup second
    first_target = int(est.current()) + lead
    target = first_target - 1
    highest = first_target
    while time.time() < end:
        desired = int(est.current()) + lead
        if target < desired:
            target += 1
            probe.send_intent(0.0, 0.0, False, target)
            highest = max(highest, target)
        if time.time() >= next_sample:
            est = TickEstimator(probe)  # re-anchor on the fresh row
            state = est.last_state
            if state["tick"] >= first_target:
                samples.append(state)
            next_sample = time.time() + 0.4
        time.sleep(0.005)
    consumed = [s for s in samples if s["consumed"]]
    in_band = [s for s in samples if 1 <= s["buffered"] <= 5]
    ok = check(
        "streamed ticks consume real commands",
        len(samples) > 0 and len(consumed) >= 0.8 * len(samples),
        f"{len(consumed)}/{len(samples)} sampled acks consumed=true",
    )
    ok &= check(
        "buffer occupancy sits near the setpoint",
        len(samples) > 0 and len(in_band) >= 0.6 * len(samples),
        f"{len(in_band)}/{len(samples)} sampled acks buffered in 1..5",
    )
    return ok, highest


def scenario_ack_burst(probe, highest_sent):
    print("== ack-burst: 6 ticks ahead in one burst reads as surplus")
    # Wait for any stream leftovers to drain so the burst is the only content.
    deadline = time.time() + 5.0
    while time.time() < deadline and probe.physics()["buffered"] > 0:
        time.sleep(0.2)
    est = TickEstimator(probe)
    # Far enough ahead that none of the burst is consumed before the sql
    # sample returns (~5 ticks of subprocess latency).
    base = max(int(est.current()) + 8, highest_sent + 1)
    for i in range(6):
        probe.send_intent(0.0, 0.0, False, base + i)
    occupancies = [probe.physics()["buffered"] for _ in range(2)]
    ok = check(
        "burst occupancy visible on the ack surface",
        5 <= max(occupancies) <= 6,
        f"max sampled occupancy {max(occupancies)} (sent exactly 6 ahead)",
    )
    # Let the burst drain fully before later scenarios.
    time.sleep(14 * TICK_SECONDS + 0.3)
    return ok, base + 5


def scenario_jump_slide(probe, highest_sent, trials=40):
    print(f"== jump-slide: {trials} boundary jumps at the estimated consume tick")
    slides_before = len(probe.module_log_slides())
    est = TickEstimator(probe)
    jump_trials = 0
    jumps_observed = 0
    stale_trials = 0
    stale_jumps = 0
    floor_tick = highest_sent
    for trial in range(trials):
        # Wait until the consume tick has passed everything we ever sent, so
        # the boundary send is genuinely "late", not just buffered.
        while True:
            state = probe.physics()
            if state["tick"] > floor_tick and state["grounded"]:
                break
            time.sleep(0.1)
        est = TickEstimator(probe)
        if trial % 4 == 3:
            # Control: 3+ ticks stale — must stay dropped.
            target = int(est.current()) - 3
            stale_trials += 1
            is_stale_trial = True
        else:
            target = int(est.current())
            jump_trials += 1
            is_stale_trial = False
        probe.send_intent(0.0, 0.0, True, target)
        floor_tick = max(floor_tick, target + 1)
        # A jump holds vel_y > 0 for ~320 ms; poll fast enough to catch it.
        saw_jump = False
        deadline = time.time() + 0.8
        while time.time() < deadline:
            state = probe.physics()
            if not state["grounded"] and state["vel_y"] > 0.5:
                saw_jump = True
                break
            time.sleep(0.05)
        if saw_jump:
            if is_stale_trial:
                stale_jumps += 1
            else:
                jumps_observed += 1
            # Land before the next trial.
            while not probe.physics()["grounded"]:
                time.sleep(0.1)
    slides = len(probe.module_log_slides()) - slides_before
    print(
        f"  boundary trials: {jump_trials}, jumps observed: {jumps_observed}, "
        f"[MOVE_JUMP_SLIDE] lines: {slides}"
    )
    print(
        f"  stale controls (3+ ticks late): {stale_trials}, jumps: {stale_jumps} "
        "(informational — estimate error can rescue a few)"
    )
    ok = check(
        "slide path exercised live",
        slides >= 3,
        f"{slides} MOVE_JUMP_SLIDE lines during the window",
    )
    ok &= check(
        "boundary jumps land instead of vanishing",
        jump_trials > 0 and jumps_observed >= max(slides, int(0.6 * jump_trials)),
        f"{jumps_observed}/{jump_trials} boundary jumps produced an authoritative jump",
    )
    return ok, floor_tick


def scenario_no_duplicates(probe, highest_sent, seconds=6.0):
    print("== no-dup: stream + boundary jumps never duplicate a queued tick")
    est = TickEstimator(probe)
    duplicates = []
    end = time.time() + seconds
    next_send = time.time()
    target = max(int(est.current()) + 2, highest_sent + 1)
    i = 0
    while time.time() < end:
        now = time.time()
        if now >= next_send:
            target = max(target + 1, int(est.current()) + 2)
            probe.send_intent(0.0, 0.0, False, target)
            if i % 10 == 5:
                # Late jump against the current estimate while the stream has
                # already buffered that slot — exercises the merge rule.
                probe.send_intent(0.0, 0.0, True, int(est.current()))
            i += 1
            next_send = now + TICK_SECONDS
        duplicates.extend(probe.command_tick_duplicates())
        time.sleep(0.02)
    return check(
        "player_command holds one row per input tick",
        not duplicates,
        f"duplicate ticks observed: {sorted(set(duplicates))!r}"
        if duplicates
        else "none observed",
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", required=True)
    parser.add_argument("--host", default="localhost:3000")
    args = parser.parse_args()

    probe = Probe(args.database, args.host)
    print(f"probe identity: {probe.identity}")
    time.sleep(1.0)  # let client_connected finish spawning

    results = []
    results.append(scenario_ack_idle(probe))
    ok, highest = scenario_ack_stream(probe)
    results.append(ok)
    ok, highest = scenario_ack_burst(probe, highest)
    results.append(ok)
    ok, highest = scenario_jump_slide(probe, highest)
    results.append(ok)
    results.append(scenario_no_duplicates(probe, highest))

    print()
    if all(results):
        print("ALL CHECKS GREEN")
        return 0
    print("CHECKS FAILED")
    return 1


if __name__ == "__main__":
    sys.exit(main())

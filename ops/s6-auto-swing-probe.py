#!/usr/bin/env python3
"""S6 live probe: auto_attack_state replication + swing schedule truth.

Server-data half of the S6 acceptance (local auto-attack swing scheduling):
a headless websocket player (client_connected spawns a live player; reducers
ride the same socket — ops/s4-los-probe.py mechanics) subscribes to
auto_attack_state exactly like the Unity client does and verifies the data
contract the client scheduler relies on:

  replication — the owner's auto_attack_state row arrives over a real
              subscription (the table was private until S6; the review's
              "replicated row" premise is only true after this change).
  alignment — every on-time auto CAST's created_at lands within ~2 ticks
              AFTER the next_swing_at the row advertised beforehand, so a
              client that fires at next_swing_at is presenting the truth.
  hold      — with the target out of range the row flips pending_due=true
              and NO CAST is emitted (the client mirror must not swing);
              walking back releases the swing at an arbitrary later time
              (>> next_swing_at), which is why the client never predicts a
              held swing. (The behind-cover hold is the same mark_pending_due
              path, verified live by ops/s4-los-probe.py.)

Run against a throwaway DB — one-shot `spacetime call` cannot leave
per-identity state, and disconnect cleanup wipes the player:

  cargo build --manifest-path server/Cargo.toml --target wasm32-unknown-unknown \
      --release --features projectile_load_harness
  spacetime publish --delete-data=always --yes \
      --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm s6probe
  python3 ops/s6-auto-swing-probe.py --database s6probe
  spacetime delete s6probe

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

SCENE = "Desert_Day"
OBSERVE_SECONDS = 12.0
HOLD_OBSERVE_SECONDS = 6.0
RELEASE_OBSERVE_SECONDS = 8.0
# Server tick is 33 ms; a due swing fires on the first tick at/after
# next_swing_at, so on-time alignment must sit in [0, ~2 ticks].
ALIGN_SLACK_EARLY_MS = 5
ALIGN_SLACK_LATE_MS = 66


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
        # Wire-observed auto_attack_state rows: (recv_monotonic, parsed row).
        self.aas_rows = []
        self.aas_lock = threading.Lock()
        self.recent = collections.deque(maxlen=40)
        self.ws.settimeout(None)
        self._drain = threading.Thread(target=self._drain_loop, daemon=True)
        self._drain.start()

    def subscribe(self, queries):
        self.request_id += 1
        self.ws.send(
            json.dumps({"Subscribe": {"query_strings": queries, "request_id": self.request_id}})
        )

    # ------------------------------------------------------------------
    # Frame drain: keep recv alive forever (server pings), and harvest
    # auto_attack_state inserts from subscription frames.
    # ------------------------------------------------------------------
    def _drain_loop(self):
        try:
            while True:
                message = self.ws.recv()
                try:
                    frame = json.loads(message)
                except Exception:
                    continue
                update = None
                if "InitialSubscription" in frame:
                    update = frame["InitialSubscription"].get("database_update")
                elif "TransactionUpdateLight" in frame:
                    # Tick-driven transactions (not caller-attributed) arrive
                    # as light updates; the Unity SDK consumes both shapes.
                    update = frame["TransactionUpdateLight"].get("update")
                elif "TransactionUpdate" in frame:
                    tx = frame["TransactionUpdate"]
                    status = tx.get("status", {})
                    name = tx.get("reducer_call", {}).get("reducer_name", "?")
                    if "Failed" in status:
                        self.recent.append(f"{name}: FAILED {status['Failed']}")
                    else:
                        self.recent.append(f"{name}: {next(iter(status), '?')}")
                    committed = status.get("Committed")
                    if isinstance(committed, dict):
                        update = committed
                if not isinstance(update, dict):
                    continue
                for table in update.get("tables", []):
                    if table.get("table_name") != "auto_attack_state":
                        continue
                    for upd in table.get("updates", []):
                        for insert in upd.get("inserts", []):
                            row = self._parse_aas_row(insert)
                            if row is not None:
                                with self.aas_lock:
                                    self.aas_rows.append((time.monotonic(), row))
        except Exception as e:
            self.recent.append(f"drain died: {type(e).__name__}: {e}")

    @staticmethod
    def _parse_aas_row(insert):
        """Rows arrive as JSON-encoded positional arrays; Identity and
        Timestamp cells are single-element arrays (verified live 2026-07-04):
        [["0x<owner>"], ["0x<target>"], combat_profile_id, mode_id, strike_id,
         [cadence_micros], [next_swing_micros], pending_due, movement_epoch]."""
        try:
            value = json.loads(insert) if isinstance(insert, str) else insert
        except Exception:
            return None
        if not isinstance(value, list) or len(value) < 9:
            return None

        def micros(cell):
            if isinstance(cell, list) and cell:
                cell = cell[0]
            if isinstance(cell, dict):
                for key in cell:
                    if "timestamp" in key.lower():
                        return int(cell[key])
            if isinstance(cell, (int, float)):
                return int(cell)
            return None

        def ident(cell):
            if isinstance(cell, list) and cell:
                cell = cell[0]
            if isinstance(cell, dict):
                cell = cell.get("__identity__", "")
            return str(cell).removeprefix("0x").lower()

        return {
            "owner": ident(value[0]),
            "target": ident(value[1]),
            "strike_id": value[4] if isinstance(value[4], str) else "",
            "cadence_started_micros": micros(value[5]),
            "next_swing_micros": micros(value[6]),
            "pending_due": bool(value[7]),
        }

    def wire_rows(self):
        with self.aas_lock:
            return list(self.aas_rows)

    def dump_recent(self):
        for entry in self.recent:
            print(f"    reducer: {entry}")

    # ------------------------------------------------------------------
    # Reducer + sql plumbing (s4 probe mechanics).
    # ------------------------------------------------------------------
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
        return rows[1:] if rows else []

    def physics(self):
        rows = self.sql(
            "SELECT identity, pos_x, pos_y, pos_z, yaw, last_processed_tick FROM player_physics"
        )
        for identity, x, y, z, yaw, tick in rows:
            if identity.removeprefix("0x").lower() == self.identity:
                return float(x), float(y), float(z), float(yaw), int(tick)
        raise RuntimeError("no player_physics row for the probe identity")

    def send_intent(self, forward, yaw, tick):
        self.call("send_movement_intent", [forward, 0.0, yaw, False, tick])

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
                    f"move_to({tx:.1f},{tz:.1f}) stuck at ({x:.1f},{z:.1f})"
                )
            last_pos = (x, z)
            yaw = math.atan2(dx, dz)
            forward = max(0.2, min(1.0, dist / 5.0))
            self.send_intent(forward, yaw, tick + 2)
            time.sleep(0.12 if dist < 4.0 else 0.3)
        raise RuntimeError(f"move_to({tx:.1f},{tz:.1f}) timed out")

    def spawn_hostile_dummy(self):
        self.call("spawn_playground_target", ["HOSTILE"])
        time.sleep(1.0)
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

    def auto_casts(self):
        """All auto_attack COMBAT_CAST rows by the probe, keyed by
        created_at_micros (combat_event retention ~20 s — poll while armed)."""
        rows = self.sql(
            "SELECT caster, source_kind, event_type, action_kind, created_at_micros FROM combat_event"
        )
        casts = {}
        for caster, source_kind, event_type, action_kind, created in rows:
            if caster.removeprefix("0x").lower() != self.identity:
                continue
            if source_kind != "auto_attack" or event_type != "COMBAT_CAST":
                continue
            casts[int(created)] = action_kind
        return casts


def observe(probe, seconds, casts):
    deadline = time.time() + seconds
    while time.time() < deadline:
        casts.update(probe.auto_casts())
        time.sleep(0.5)


def check(label, ok, failures, detail=""):
    print(f"  [{'PASS' if ok else 'FAIL'}] {label}{(': ' + detail) if detail else ''}")
    if not ok:
        failures.append(label)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default="s6probe")
    parser.add_argument("--host", default="127.0.0.1:3000")
    args = parser.parse_args()

    probe = Probe(args.database, args.host)
    failures = []
    print(f"Connected as {probe.identity} to {args.database}")

    # Subscribe the way the Unity client does (owner-filtered rows are the
    # shipping shape; the probe takes the whole table to also observe the
    # dummy-owner absence).
    probe.subscribe(["SELECT * FROM auto_attack_state"])
    time.sleep(0.6)

    print(f"\n== setup: scene {SCENE}, dummy in melee range")
    probe.call("set_open_world_scene", [SCENE])
    time.sleep(1.2)
    x, _, z, _, _ = probe.physics()
    print(f"  spawned at ({x:.1f}, {z:.1f})")
    dummy = probe.spawn_hostile_dummy()
    dx, dz = probe.dummy_position(dummy)
    print(f"  dummy {dummy[:8]} at ({dx:.1f}, {dz:.1f})")

    print("\n== in-range cadence: arm and observe")
    probe.call("arm_auto_attack_target", [dummy])
    casts = {}
    observe(probe, OBSERVE_SECONDS, casts)

    wire = probe.wire_rows()
    own_rows = [r for _, r in wire if r["owner"] == probe.identity]
    check(
        "auto_attack_state rows replicate over the subscription",
        len(own_rows) >= 2,
        failures,
        f"{len(own_rows)} owner rows observed on the wire",
    )

    cast_times = sorted(casts)
    check(
        "armed auto-attack emits CASTs",
        len(cast_times) >= 3,
        failures,
        f"{len(cast_times)} auto CASTs in {OBSERVE_SECONDS:.0f}s",
    )

    # Alignment: each CAST vs the schedule advertised before it.
    schedules = sorted(
        {r["next_swing_micros"] for r in own_rows if r["next_swing_micros"]}
    )
    aligns = []
    for cast_micros in cast_times:
        candidates = [
            s for s in schedules
            if s - ALIGN_SLACK_EARLY_MS * 1000 <= cast_micros
        ]
        if not candidates:
            continue
        best = max(candidates)
        aligns.append((cast_micros - best) / 1000.0)
    aligned = [a for a in aligns if -ALIGN_SLACK_EARLY_MS <= a <= ALIGN_SLACK_LATE_MS]
    print(
        "  per-swing CAST minus advertised next_swing_at (ms): "
        + ", ".join(f"{a:.1f}" for a in aligns)
    )
    check(
        "every on-time CAST lands within 2 ticks after next_swing_at",
        len(aligns) >= 3 and len(aligned) == len(aligns),
        failures,
        f"{len(aligned)}/{len(aligns)} within [-{ALIGN_SLACK_EARLY_MS}, {ALIGN_SLACK_LATE_MS}] ms",
    )
    check(
        "in-range schedule rows carry pending_due=false",
        own_rows and all(not r["pending_due"] for r in own_rows),
        failures,
    )

    print("\n== hold: walk out of range, expect pending_due=true and zero CASTs")
    # The swing usually comes due (and gets held) while still walking out, so
    # the pending_due flip must be watched from the start of the walk.
    hold_rows_before = len(probe.wire_rows())
    away_dir = math.atan2(x - dx, z - dz)  # from dummy through the probe
    for attempt, bearing in enumerate(
        (away_dir, away_dir + math.pi / 2, away_dir - math.pi / 2, away_dir + math.pi)
    ):
        ax = dx + 14.0 * math.sin(bearing)
        az = dz + 14.0 * math.cos(bearing)
        try:
            probe.move_to(ax, az, tolerance=1.5)
            break
        except RuntimeError as e:
            px, _, pz, _, _ = probe.physics()
            if math.hypot(px - dx, pz - dz) >= 7.0:
                print(f"  ({e}; still {math.hypot(px - dx, pz - dz):.1f} m out — good enough)")
                break
            print(f"  ({e}; retrying on another bearing)")
    else:
        raise RuntimeError("could not get out of range on any bearing")
    pre_hold_casts = dict(casts)
    observe(probe, HOLD_OBSERVE_SECONDS, casts)
    new_hold_casts = [t for t in casts if t not in pre_hold_casts]
    hold_rows = [r for _, r in probe.wire_rows()[hold_rows_before:] if r["owner"] == probe.identity]
    held_schedule = max(
        (r["next_swing_micros"] for _, r in probe.wire_rows() if r["owner"] == probe.identity and r["next_swing_micros"]),
        default=None,
    )
    check(
        "zero auto CASTs while out of range",
        len(new_hold_casts) == 0,
        failures,
        f"{len(new_hold_casts)} CASTs during the {HOLD_OBSERVE_SECONDS:.0f}s hold",
    )
    check(
        "hold marks pending_due=true on the replicated row",
        any(r["pending_due"] for r in hold_rows),
        failures,
        f"{sum(1 for r in hold_rows if r['pending_due'])} pending_due rows observed",
    )

    print("\n== release: walk back in, the held swing fires late (never predictable)")
    probe.move_to(dx + 1.5 * math.sin(away_dir), dz + 1.5 * math.cos(away_dir), tolerance=0.8)
    pre_release_casts = dict(casts)
    observe(probe, RELEASE_OBSERVE_SECONDS, casts)
    release_times = sorted(t for t in casts if t not in pre_release_casts)
    check(
        "held swing releases once back in range",
        len(release_times) >= 1,
        failures,
        f"{len(release_times)} CASTs after returning",
    )
    if release_times and held_schedule:
        release_lag_ms = (release_times[0] - held_schedule) / 1000.0
        print(f"  release CAST fired {release_lag_ms:.0f} ms after the held next_swing_at")
        check(
            "held release is far off-schedule (client must not predict it)",
            release_lag_ms > ALIGN_SLACK_LATE_MS * 2,
            failures,
            f"{release_lag_ms:.0f} ms late vs the advertised schedule",
        )

    probe.call("clear_auto_attack_target", [])
    time.sleep(0.5)

    print(f"\n{'ALL PASS' if not failures else 'FAILURES: ' + ', '.join(failures)}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

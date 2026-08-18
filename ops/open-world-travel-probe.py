#!/usr/bin/env python3
"""End-to-end proof that open-world travel provisions and disposes an instance.

Drives the whole disposable-open-world path with no Unity editor:

  1. opens an anonymous Hub websocket, which is a fresh non-owner identity —
     the same shape of identity a real client has;
  2. calls `request_open_world_instance`, the reducer the Hub travel button
     now calls;
  3. waits for the provisioner to publish a database and mark the ticket READY;
  4. connects to that instance with the SAME identity token and asserts the
     player was seated in the requested scene;
  5. disconnects and asserts the database is deleted, which is the entire
     point of the change (docs/open-world-disposable-instances-2026-08-18.md).

Requires `pip install websocket-client`.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import threading
import time
import uuid

import websocket


def sql(database: str, query: str) -> list[list[str]]:
    result = subprocess.run(
        ["spacetime", "sql", database, query], capture_output=True, text=True
    )
    if result.returncode != 0:
        raise RuntimeError(f"spacetime sql failed on {database}: {result.stderr.strip()}")
    rows = []
    for line in result.stdout.splitlines():
        if "|" not in line or set(line.strip()) <= {"-", "+", "|", " "}:
            continue
        rows.append([cell.strip().strip('"') for cell in line.split("|")])
    return rows[1:] if rows else []


def database_exists(name_or_identity: str) -> bool:
    result = subprocess.run(
        ["spacetime", "describe", "--json", name_or_identity],
        capture_output=True,
        text=True,
    )
    return result.returncode == 0 and result.stdout.strip().startswith("{")


class Connection:
    """One live websocket client identity."""

    def __init__(self, database: str, host: str, token: str | None = None):
        self.database = database
        self.request_id = 0
        header = [f"Authorization: Bearer {token}"] if token else None
        self.ws = websocket.create_connection(
            f"ws://{host}/v1/database/{database}/subscribe",
            subprotocols=["v1.json.spacetimedb"],
            header=header,
            timeout=10,
        )
        self.identity = None
        self.token = token
        first = json.loads(self.ws.recv())
        identity_token = first.get("IdentityToken") or {}
        identity = identity_token.get("identity")
        if isinstance(identity, dict):
            identity = identity.get("__identity__", "")
        self.identity = (identity or "").removeprefix("0x").lower()
        self.token = identity_token.get("token") or token
        # The socket must keep draining or the server drops it, which would
        # tear down exactly the state under test.
        self.ws.settimeout(None)
        self.recent: list[str] = []
        threading.Thread(target=self._drain, daemon=True).start()

    def _drain(self) -> None:
        try:
            while True:
                message = self.ws.recv()
                if "TransactionUpdate" in message:
                    update = json.loads(message)["TransactionUpdate"]
                    name = update.get("reducer_call", {}).get("reducer_name", "?")
                    status = update.get("status", {})
                    if "Failed" in status:
                        self.recent.append(f"{name}: FAILED {status['Failed']}")
        except Exception as error:  # noqa: BLE001 - diagnostic tail only
            self.recent.append(f"drain ended: {type(error).__name__}: {error}")

    def call(self, reducer: str, args: list) -> None:
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

    def close(self) -> None:
        try:
            self.ws.close()
        except Exception:  # noqa: BLE001 - closing is best effort
            pass


def wait_for_ticket(hub: str, identity: str, deadline: float) -> tuple[str, str]:
    """Returns (database_identity, status) once the ticket stops moving."""
    last = ""
    while time.time() < deadline:
        rows = sql(
            hub,
            "SELECT player_identity, status, failure_code FROM match_ticket",
        )
        for player_identity, status, failure_code in rows:
            if player_identity.removeprefix("0x").lower() != identity:
                continue
            if status != last:
                print(f"  ticket {status}")
                last = status
            if status == "FAILED":
                raise RuntimeError(f"ticket failed: {failure_code}")
            if status == "READY":
                for row in sql(
                    hub,
                    "SELECT player_identity, database_identity, map_id FROM match_assignment",
                ):
                    if row[0].removeprefix("0x").lower() == identity:
                        return row[1], row[2]
                raise RuntimeError("READY ticket has no assignment row")
        time.sleep(1.0)
    raise RuntimeError("timed out waiting for a READY ticket")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--destination", default="Giant_Skeleton")
    parser.add_argument("--hub-database", default="arena-hub-local")
    parser.add_argument("--host", default="127.0.0.1:3000")
    parser.add_argument("--ready-timeout", type=float, default=180.0)
    parser.add_argument("--delete-timeout", type=float, default=120.0)
    args = parser.parse_args()

    started = time.time()
    hub = Connection(args.hub_database, args.host)
    print(f"Hub identity {hub.identity}")

    hub.call(
        "request_open_world_instance",
        [uuid.uuid4().hex, args.destination],
    )
    print(f"Requested {args.destination}")

    database_identity, map_id = wait_for_ticket(
        args.hub_database, hub.identity, started + args.ready_timeout
    )
    ready_seconds = time.time() - started
    print(f"  assignment map_id={map_id} database={database_identity[:16]}…")
    print(f"  ready in {ready_seconds:.1f}s")
    if map_id != args.destination:
        raise RuntimeError(f"assignment targets {map_id}, not {args.destination}")
    if not database_exists(database_identity):
        raise RuntimeError("assigned database does not exist")

    instance = Connection(database_identity, args.host, token=hub.token)
    if instance.identity != hub.identity:
        raise RuntimeError(
            f"instance authenticated {instance.identity}, not the reserved {hub.identity}"
        )
    print(f"Connected to the instance as the reserved identity")

    seated = None
    deadline = time.time() + 30.0
    while time.time() < deadline and seated is None:
        for identity, world_kind, scene in sql(
            database_identity,
            "SELECT identity, world_kind, open_world_scene_name FROM player_world",
        ):
            if identity.removeprefix("0x").lower() == hub.identity:
                seated = (world_kind, scene)
        if seated is None:
            time.sleep(0.5)
    if seated is None:
        for entry in instance.recent:
            print(f"    reducer: {entry}")
        raise RuntimeError("the reserved player never got a player_world row")
    print(f"  player_world kind={seated[0]} scene={seated[1]}")
    if seated != ("OPEN", args.destination):
        raise RuntimeError(f"player was seated in {seated}, not OPEN/{args.destination}")

    phase = sql(database_identity, "SELECT phase, queue_kind FROM match_bootstrap_config")
    print(f"  bootstrap phase={phase[0][0]} queue_kind={phase[0][1]}")

    instance.close()
    print("Disconnected from the instance; waiting for disposal…")
    deadline = time.time() + args.delete_timeout
    while time.time() < deadline:
        if not database_exists(database_identity):
            print(f"  database deleted after {time.time() - started:.1f}s total")
            hub.close()
            print("PASS")
            return 0
        time.sleep(2.0)
    hub.close()
    raise RuntimeError("the instance database still exists after the disposal timeout")


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except RuntimeError as error:
        print(f"FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)

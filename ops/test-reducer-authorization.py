#!/usr/bin/env python3
"""Check reducer authorization in disposable local PvP and open-world instances.

Run after ops/setup-local-multiplayer.sh setup. Uses a new anonymous Hub
player, the managed provisioner's owner credential, and exact-ticket cleanup.
Existing Hub builds and persistent gameplay databases are never modified.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import runpy
import subprocess
import sys
import time
import uuid

from websocket import ABNF

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "ops"))
from match_provisioner.worker import HttpApi, ProvisionerError

RUNTIME = runpy.run_path(str(ROOT / "ops/test-combat-build-runtime.py"))


class Connection(RUNTIME["Connection"]):
    def receive(self):
        # Return on control frames too so wait_for can enforce its deadline
        # even when the server keeps pinging after an unexpected response.
        opcode, frame = self.ws.recv_data_frame(control_frame=True)
        if opcode == ABNF.OPCODE_CLOSE:
            raise RuntimeError("Probe WebSocket closed before the reducer result")
        if opcode not in (ABNF.OPCODE_TEXT, ABNF.OPCODE_BINARY):
            return {}
        value = json.loads(frame.data)
        return value if isinstance(value, dict) else {}


require_status = RUNTIME["require_status"]
wait_for_hub_status = RUNTIME["wait_for_hub_status"]
wait_for_cleanup = RUNTIME["wait_for_cleanup"]

MANAGEMENT_URL = "http://127.0.0.1:3000"
CLIENT_URI = "ws://127.0.0.1:3000"


def player_call(connection, reducer: str, args: list) -> tuple[str, str]:
    # Correlate by caller and request ID as scheduled notifications can arrive
    # on the same socket. Use the existing gameplay socket: an HTTP call opens
    # a second player connection whose disconnect would abort the allocation.
    connection.request_id += 1
    request_id = connection.request_id
    connection.ws.send(json.dumps({"CallReducer": {
        "reducer": reducer, "args": json.dumps(args),
        "request_id": request_id, "flags": 0,
    }}))

    def select(frame):
        if "TransactionUpdate" not in frame:
            for key, value in frame.items():
                if "error" in key.lower():
                    raise RuntimeError(f"{reducer}: {key}: {value}")
        update = frame.get("TransactionUpdate", {})
        call = update.get("reducer_call", {})
        if call.get("reducer_name") != reducer:
            return None
        if RUNTIME["normalize_identity"](update.get("caller_identity")) != connection.identity:
            return None
        status = update.get("status", {})
        # Failed calls may carry request_id=0. This probe has exactly one call
        # outstanding, and the caller check excludes host-scheduled events.
        if call.get("request_id") not in (0, request_id):
            return None
        if "Committed" in status:
            return "committed", ""
        if "Failed" in status:
            return "failed", str(status["Failed"])
        if "OutOfEnergy" in status:
            raise RuntimeError("Authorization probe ran out of energy")
        return None

    return connection.wait_for(select)


def owner_api() -> HttpApi:
    token = os.environ.get("ARENA_PROVISIONER_TOKEN", "").strip()
    if not token:
        result = subprocess.run(
            ["spacetime", "login", "show", "--token"],
            check=True, capture_output=True, text=True,
        )
        token = next(
            (line.split()[-1] for line in result.stdout.splitlines()
             if line.startswith("Your auth token ")), "",
        )
    if not token:
        raise RuntimeError("No local provisioner owner credential available")
    return HttpApi(MANAGEMENT_URL, token)


def check_operational_probes(database: str) -> None:
    # Exercise the actual configuration helpers and their SQL readback without
    # starting the unrelated movement/combat scenarios or another player socket.
    for filename, switches in (
        ("s8-lag-comp-probe.py", (True,)),
        ("s9-auto-rewind-probe.py", (True, True)),
        ("s10-sweep-rewind-probe.py", (True, True)),
    ):
        module = runpy.run_path(str(ROOT / "ops" / filename))
        probe_type = module["Probe"]
        probe = probe_type.__new__(probe_type)
        probe.database = database
        probe.host = "127.0.0.1:3000"
        module["set_lag_comp"](probe, *switches)
        module["set_lag_comp"](probe, *(False for _ in switches))
        print(f"PASS operational probe: {filename} owner config on/off", flush=True)


def check_instance(kind: str, owner: HttpApi) -> list[str]:
    failures: list[str] = []
    hub = Connection(CLIENT_URI, "arena-hub-local", 20.0)
    match = None
    assignment = None
    requested = False

    def check(condition: bool, detail: str) -> None:
        print(f"{'PASS' if condition else 'FAIL'} {kind}: {detail}", flush=True)
        if not condition:
            failures.append(f"{kind}: {detail}")

    def denied(api, database: str, reducer: str, args: list, role: str) -> None:
        if reducer in ("game_tick", "game_loop_watchdog_tick"):
            expected = ("Only the database scheduler may invoke scheduled reducers",)
            if role == "player":
                expected += ("no such reducer",)
        else:
            expected = ("Only the module owner may run administrative reducers",)
        if role == "player":
            status, detail = player_call(api, reducer, args)
            check(status == "failed" and any(reason in detail for reason in expected),
                  f"player {reducer}: {status} {detail}")
            return
        try:
            api.call(database, reducer, args)
        except ProvisionerError as error:
            detail = str(error)
            check(any(reason in detail for reason in expected),
                  f"{role} {reducer} denied: {detail}")
        else:
            check(False, f"{role} {reducer} unexpectedly committed")

    try:
        hub.subscribe(['SELECT * FROM "my_match_status"'])
        request_id = f"reducer-auth-{uuid.uuid4().hex}"
        reducer, args = (
            ("request_unranked_2_v_2_bot_match", [request_id])
            if kind == "pvp"
            else ("request_open_world_instance", [request_id, "Adventure_Island"])
        )
        require_status(hub.call(reducer, args), "committed")
        requested = True
        assignment = wait_for_hub_status(hub, "READY")
        database = assignment["database_identity"]
        if assignment["server_uri"].rstrip("/") != CLIENT_URI:
            raise RuntimeError("Refusing a non-local match assignment")
        match = Connection(CLIENT_URI, database, 20.0, hub.token)
        check(match.identity == hub.identity, "anonymous identity preserved through handoff")
        player = match
        owner_row = owner.sql(database, "SELECT * FROM match_module_owner")[0]
        check(
            RUNTIME["normalize_identity"](owner_row["identity"]) != match.identity,
            "gameplay caller is not the module owner",
        )

        config = [True, 125, True, False]
        owner.call(database, "set_lag_comp_config", config)
        before = owner.sql(database, "SELECT * FROM combat_lag_comp_config")
        check(
            len(before) == 1 and before[0]["enabled"] is True
            and before[0]["max_rewind_ms"] == 125
            and before[0]["auto_swing_enabled"] is True
            and before[0]["sweep_rewind_enabled"] is False,
            "owner can configure lag compensation",
        )
        denied(player, database, "set_lag_comp_config", [False, 999, False, True], "player")
        check(owner.sql(database, "SELECT * FROM combat_lag_comp_config") == before,
              "denied configuration call leaves settings unchanged")
        owner.call(database, "set_lag_comp_config", config)

        denied(player, database, "run_status_runtime_harness", [], "player")
        owner.call(database, "run_status_runtime_harness", [])
        check(True, "owner can run the status harness")

        for reducer, table in (
            ("game_tick", "game_loop_timer"),
            ("game_loop_watchdog_tick", "game_loop_watchdog"),
        ):
            rows = owner.sql(database, f"SELECT * FROM {table}")
            if len(rows) != 1:
                raise RuntimeError(f"Expected one active {table} row, received {len(rows)}")
            timer = [rows[0]["scheduled_id"], rows[0]["scheduled_at"]]
            denied(player, database, reducer, [timer], "player")
            denied(owner, database, reducer, [timer], "owner")

        before = owner.sql(database, "SELECT * FROM game_loop_timer")
        deadline = time.monotonic() + 5.0
        advanced = False
        while time.monotonic() < deadline:
            after = owner.sql(database, "SELECT * FROM game_loop_timer")
            if after and before and after[0]["scheduled_id"] > before[0]["scheduled_id"]:
                advanced = True
                break
            time.sleep(0.1)
        check(advanced, "host scheduler continues advancing the simulation")
        if kind == "pvp":
            check_operational_probes(database)
        print(json.dumps({"kind": kind, "match_build_id": assignment["match_build_id"]}), flush=True)
    finally:
        try:
            # If provisioning failed before READY, cancel this new player's
            # pending ticket as well; never leave it for a later probe to find.
            if assignment is None and requested:
                initial = hub.subscribe(['SELECT * FROM "my_match_status"'])
                rows = RUNTIME["inserted_rows"](initial, "my_match_status")
                if rows:
                    assignment = RUNTIME["parse_match_status"](rows[0])
            if assignment is not None:
                require_status(hub.call("cancel_match_ticket", [assignment["ticket_id"]]), "committed")
        finally:
            if match is not None:
                match.close()
            hub.close()
        if assignment is not None and assignment["database_identity"]:
            wait_for_cleanup(ROOT / "Library/ArenaMatchProvisioner/state.sqlite3",
                             assignment["ticket_id"], 45.0)
            print(f"PASS {kind}: exact-ticket cleanup reached CLEANED", flush=True)
    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kind", choices=("pvp", "open-world", "both"), default="both")
    args = parser.parse_args()
    kinds = ("pvp", "open-world") if args.kind == "both" else (args.kind,)
    owner = owner_api()
    failures = []
    for kind in kinds:
        failures.extend(check_instance(kind, owner))
    if failures:
        raise RuntimeError("Reducer authorization failed: " + "; ".join(failures))
    print("PASS: reducer authorization", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

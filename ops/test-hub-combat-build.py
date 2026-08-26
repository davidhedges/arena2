#!/usr/bin/env python3
"""Exercise the canonical combat-build reducer against a live local Hub.

The probe uses one fresh anonymous identity, so it cannot mutate a developer's
saved build. It verifies the Phase 2 state boundary through caller-filtered
views: deterministic initialization, atomic save/reload, revision rejection,
rollback, per-discipline weapons, Staff schools, and dormant remove/re-add.
"""

from __future__ import annotations

import argparse
import json
import time
from typing import Any, Callable

import websocket


PROTOCOL = "v1.json.spacetimedb"
QUERIES = [
    'SELECT * FROM "my_combat_build"',
]


def option_value(value: Any) -> Any:
    if isinstance(value, list) and value:
        if value[0] == 0 and len(value) >= 2:
            return value[1]
        if value[0] == 1:
            return None
    return value


def decoded_row(value: Any) -> list[Any] | dict[str, Any] | None:
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except json.JSONDecodeError:
            return None
    return value if isinstance(value, (list, dict)) else None


def row_value(row: list[Any] | dict[str, Any], index: int, field: str) -> Any:
    if isinstance(row, dict):
        if field not in row:
            raise RuntimeError(f"row is missing {field!r}")
        return row[field]
    if index >= len(row):
        raise RuntimeError(f"row is missing positional field {index} ({field})")
    return row[index]


def inserted_rows(update: dict[str, Any] | None, table_name: str) -> list[Any]:
    rows: list[Any] = []
    if not isinstance(update, dict):
        return rows
    for table in update.get("tables", []):
        if table.get("table_name") != table_name:
            continue
        for table_update in table.get("updates", []):
            for insert in table_update.get("inserts", []):
                row = decoded_row(insert)
                if row is not None:
                    rows.append(row)
    return rows


class Connection:
    def __init__(
        self,
        server_uri: str,
        database: str,
        timeout_seconds: float,
        token: str | None = None,
    ) -> None:
        url = f"{server_uri.rstrip('/')}/v1/database/{database}/subscribe"
        headers = {"Authorization": f"Bearer {token}"} if token else None
        self.ws = websocket.create_connection(
            url,
            subprotocols=[PROTOCOL],
            timeout=timeout_seconds,
            header=headers,
        )
        self.ws.settimeout(timeout_seconds)
        self.timeout_seconds = timeout_seconds
        self.request_id = 0
        first = self.receive()
        issued = first.get("IdentityToken")
        if not isinstance(issued, dict):
            raise RuntimeError(f"expected IdentityToken, received {list(first)}")
        identity = issued.get("identity")
        if isinstance(identity, list) and identity:
            identity = identity[0]
        if isinstance(identity, dict):
            identity = identity.get("__identity__")
        self.identity = str(identity or "").removeprefix("0x").lower()
        self.token = str(issued.get("token") or token or "")
        if not self.identity or not self.token:
            raise RuntimeError("SpacetimeDB did not issue an identity and token")

    def receive(self) -> dict[str, Any]:
        while True:
            message = self.ws.recv()
            if not isinstance(message, str):
                continue
            try:
                frame = json.loads(message)
            except json.JSONDecodeError:
                continue
            if isinstance(frame, dict):
                return frame

    def wait_for(
        self,
        predicate: Callable[[dict[str, Any]], Any],
    ) -> Any:
        deadline = time.monotonic() + self.timeout_seconds
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("timed out waiting for a Hub WebSocket frame")
            self.ws.settimeout(remaining)
            try:
                frame = self.receive()
            except websocket.WebSocketTimeoutException as error:
                raise TimeoutError("timed out waiting for a Hub WebSocket frame") from error
            result = predicate(frame)
            if result is not None:
                self.ws.settimeout(self.timeout_seconds)
                return result

    def subscribe(self) -> dict[str, Any]:
        self.request_id += 1
        request_id = self.request_id
        self.ws.send(
            json.dumps(
                {"Subscribe": {"query_strings": QUERIES, "request_id": request_id}}
            )
        )

        def select(frame: dict[str, Any]) -> dict[str, Any] | None:
            initial = frame.get("InitialSubscription")
            if not isinstance(initial, dict):
                return None
            if int(initial.get("request_id", request_id)) != request_id:
                return None
            return initial.get("database_update")

        return self.wait_for(select)

    def call(self, reducer: str, args: list[Any]) -> tuple[str, str]:
        self.request_id += 1
        request_id = self.request_id
        self.ws.send(
            json.dumps(
                {
                    "CallReducer": {
                        "reducer": reducer,
                        "args": json.dumps(args),
                        "request_id": request_id,
                        "flags": 0,
                    }
                }
            )
        )

        def select(frame: dict[str, Any]) -> tuple[str, str] | None:
            update = frame.get("TransactionUpdate")
            if not isinstance(update, dict):
                return None
            reducer_call = update.get("reducer_call", {})
            if reducer_call.get("reducer_name") != reducer:
                return None
            status = update.get("status", {})
            if "Committed" in status:
                return ("committed", "")
            if "Failed" in status:
                return ("failed", str(status["Failed"]))
            if "OutOfEnergy" in status:
                return ("out_of_energy", "reducer ran out of energy")
            return None

        return self.wait_for(select)

    def close(self) -> None:
        self.ws.close()


def parse_state(update: dict[str, Any]) -> dict[str, Any]:
    build_rows = inserted_rows(update, "my_combat_build")
    if len(build_rows) != 1:
        raise RuntimeError(f"expected one combat build row, received {len(build_rows)}")
    build = build_rows[0]
    state = {
        "owner": str(row_value(build, 0, "owner")),
        "starting_discipline_id": option_value(
            row_value(build, 1, "starting_discipline_id")
        ),
        "revision": int(row_value(build, 2, "revision")),
        "selected": [],
        "configurations": [],
        "schools": [],
        "assignments": [],
        "passives": [],
    }

    selected_disciplines = row_value(build, 3, "selected_disciplines")
    configurations = row_value(build, 4, "discipline_configurations")
    if not isinstance(selected_disciplines, list):
        raise RuntimeError("combat build selected_disciplines is not an array")
    if not isinstance(configurations, list):
        raise RuntimeError("combat build discipline_configurations is not an array")

    for row in selected_disciplines:
        state["selected"].append(
            {
                "slot_index": int(row_value(row, 0, "slot_index")),
                "combat_discipline_id": str(
                    row_value(row, 1, "combat_discipline_id")
                ),
            }
        )

    for row in configurations:
        combat_discipline_id = str(row_value(row, 0, "combat_discipline_id"))
        weapon = row_value(row, 1, "weapon")
        schools = row_value(row, 2, "staff_school_ids")
        assignments = row_value(row, 3, "active_assignments")
        passives = row_value(row, 4, "passive_ability_ids")
        if not isinstance(schools, list):
            raise RuntimeError("combat build staff_school_ids is not an array")
        if not isinstance(assignments, list):
            raise RuntimeError("combat build active_assignments is not an array")
        if not isinstance(passives, list):
            raise RuntimeError("combat build passive_ability_ids is not an array")
        state["configurations"].append(
            {
                "combat_discipline_id": combat_discipline_id,
                "main_hand_item_def_id": str(
                    row_value(weapon, 0, "main_hand_item_def_id")
                ),
                "main_hand_color_id": str(
                    row_value(weapon, 1, "main_hand_color_id")
                ),
                "off_hand_item_def_id": str(
                    row_value(weapon, 2, "off_hand_item_def_id")
                ),
                "off_hand_color_id": str(
                    row_value(weapon, 3, "off_hand_color_id")
                ),
            }
        )
        if combat_discipline_id == "STAFF":
            state["schools"].extend(str(school_id) for school_id in schools)
        for assignment in assignments:
            state["assignments"].append(
                {
                    "combat_discipline_id": combat_discipline_id,
                    "action_slot": str(row_value(assignment, 0, "action_slot")),
                    "ability_id": str(row_value(assignment, 1, "ability_id")),
                }
            )
        for ability_id in passives:
            state["passives"].append(
                {
                    "combat_discipline_id": combat_discipline_id,
                    "ability_id": str(ability_id),
                }
            )

    state["selected"].sort(key=lambda row: row["slot_index"])
    state["configurations"].sort(key=lambda row: row["combat_discipline_id"])
    state["schools"].sort()
    state["assignments"].sort(
        key=lambda row: (row["combat_discipline_id"], row["action_slot"])
    )
    state["passives"].sort(
        key=lambda row: (row["combat_discipline_id"], row["ability_id"])
    )
    return state


def read_state(
    server_uri: str,
    database: str,
    timeout_seconds: float,
    token: str | None = None,
) -> tuple[dict[str, Any], str, str]:
    connection = Connection(server_uri, database, timeout_seconds, token)
    try:
        return parse_state(connection.subscribe()), connection.token, connection.identity
    finally:
        connection.close()


def save_draft(
    server_uri: str,
    database: str,
    timeout_seconds: float,
    token: str,
    draft: dict[str, Any],
) -> tuple[str, str]:
    connection = Connection(server_uri, database, timeout_seconds, token)
    try:
        connection.subscribe()
        return connection.call("save_combat_build", [draft])
    finally:
        connection.close()


def weapon(main_hand: str, main_color: str = "") -> dict[str, str]:
    return {
        "main_hand_item_def_id": main_hand,
        "main_hand_color_id": main_color,
        "off_hand_item_def_id": "",
        "off_hand_color_id": "",
    }


def configuration(
    discipline_id: str,
    weapon_input: dict[str, str],
    schools: list[str],
    action_slot: str,
    ability_id: str,
) -> dict[str, Any]:
    return {
        "combat_discipline_id": discipline_id,
        "weapon": weapon_input,
        "staff_school_ids": schools,
        "active_assignments": [
            {"action_slot": action_slot, "ability_id": ability_id}
        ],
        "passive_ability_ids": [],
    }


def draft(
    revision: int,
    selected_ids: list[str],
    configurations: list[dict[str, Any]],
) -> dict[str, Any]:
    return {
        "revision": revision,
        "starting_discipline_id": None,
        "selected_disciplines": [
            {"slot_index": index, "combat_discipline_id": discipline_id}
            for index, discipline_id in enumerate(selected_ids)
        ],
        "discipline_configurations": configurations,
    }


def require_status(
    actual: tuple[str, str],
    expected_status: str,
    expected_error_code: str | None = None,
) -> None:
    status, detail = actual
    if status != expected_status:
        raise RuntimeError(
            f"expected reducer status {expected_status}, received {status}: {detail}"
        )
    if expected_error_code and expected_error_code not in detail:
        raise RuntimeError(
            f"expected reducer failure {expected_error_code}, received: {detail}"
        )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-uri", default="ws://127.0.0.1:3000")
    parser.add_argument("--hub-database", default="arena-hub-local")
    parser.add_argument("--timeout-seconds", type=float, default=15.0)
    args = parser.parse_args()

    initial, token, identity = read_state(
        args.server_uri, args.hub_database, args.timeout_seconds
    )
    if initial["revision"] != 1:
        raise RuntimeError(f"default revision must be 1, received {initial['revision']}")
    if initial["starting_discipline_id"] is not None:
        raise RuntimeError("default starting discipline must remain unset")
    if initial["selected"] != [
        {"slot_index": 0, "combat_discipline_id": "DAGGERS"}
    ]:
        raise RuntimeError(f"unexpected canonical default: {initial['selected']}")

    configurations = [
        configuration(
            "DAGGERS",
            weapon("TRAINING_DAGGER_PAIR"),
            [],
            "slot_0_0",
            "DAGGER_QUICK_CUT",
        ),
        configuration(
            "STAFF",
            weapon("NEWBIE_STAFF_01", "DEFAULT"),
            ["RUIN", "ARCANA"],
            "slot_0_1",
            "SPELL_FIREBALL",
        ),
        configuration(
            "ARCHER_BOW",
            weapon("TRAINING_BOW", "DEFAULT"),
            [],
            "slot_0_2",
            "ARCHER_POWER_SHOT",
        ),
    ]

    first_draft = draft(initial["revision"], ["DAGGERS", "STAFF"], configurations)
    require_status(
        save_draft(
            args.server_uri,
            args.hub_database,
            args.timeout_seconds,
            token,
            first_draft,
        ),
        "committed",
    )
    first_saved, _, reloaded_identity = read_state(
        args.server_uri, args.hub_database, args.timeout_seconds, token
    )
    if reloaded_identity != identity:
        raise RuntimeError("save/reload changed the authenticated identity")
    if first_saved["revision"] != initial["revision"] + 1:
        raise RuntimeError("successful save did not advance the revision exactly once")
    weapons = {
        row["combat_discipline_id"]: row["main_hand_item_def_id"]
        for row in first_saved["configurations"]
    }
    if weapons != {
        "ARCHER_BOW": "TRAINING_BOW",
        "DAGGERS": "TRAINING_DAGGER_PAIR",
        "STAFF": "NEWBIE_STAFF_01",
    }:
        raise RuntimeError(f"per-discipline weapons did not reload exactly: {weapons}")
    if first_saved["schools"] != ["ARCANA", "RUIN"]:
        raise RuntimeError(f"Staff schools did not reload exactly: {first_saved['schools']}")

    removed_draft = draft(first_saved["revision"], ["DAGGERS"], configurations)
    require_status(
        save_draft(
            args.server_uri,
            args.hub_database,
            args.timeout_seconds,
            token,
            removed_draft,
        ),
        "committed",
    )
    removed, _, _ = read_state(
        args.server_uri, args.hub_database, args.timeout_seconds, token
    )
    if removed["selected"] != [
        {"slot_index": 0, "combat_discipline_id": "DAGGERS"}
    ]:
        raise RuntimeError("removed Staff remained selected")
    if removed["configurations"] != first_saved["configurations"]:
        raise RuntimeError("removing Staff discarded or rewrote dormant configurations")
    if removed["schools"] != first_saved["schools"]:
        raise RuntimeError("removing Staff discarded its dormant school selection")
    if removed["assignments"] != first_saved["assignments"]:
        raise RuntimeError("removing Staff discarded its dormant action-bar assignment")

    readd_draft = draft(removed["revision"], ["DAGGERS", "STAFF"], configurations)
    require_status(
        save_draft(
            args.server_uri,
            args.hub_database,
            args.timeout_seconds,
            token,
            readd_draft,
        ),
        "committed",
    )
    readded, _, _ = read_state(
        args.server_uri, args.hub_database, args.timeout_seconds, token
    )
    if readded["configurations"] != first_saved["configurations"]:
        raise RuntimeError("re-adding Staff did not restore its exact dormant configuration")
    if readded["schools"] != ["ARCANA", "RUIN"]:
        raise RuntimeError("re-adding Staff did not restore Ruin + Arcana")

    stale_draft = draft(readded["revision"] - 1, ["DAGGERS", "STAFF"], configurations)
    require_status(
        save_draft(
            args.server_uri,
            args.hub_database,
            args.timeout_seconds,
            token,
            stale_draft,
        ),
        "failed",
        "COMBAT_BUILD_STALE_REVISION",
    )
    after_stale, _, _ = read_state(
        args.server_uri, args.hub_database, args.timeout_seconds, token
    )
    if after_stale != readded:
        raise RuntimeError("stale-revision rejection mutated canonical Hub state")

    invalid_configurations = json.loads(json.dumps(configurations))
    invalid_configurations[0]["weapon"]["main_hand_item_def_id"] = "TRAINING_BOW"
    invalid_draft = draft(
        readded["revision"], ["DAGGERS", "STAFF"], invalid_configurations
    )
    require_status(
        save_draft(
            args.server_uri,
            args.hub_database,
            args.timeout_seconds,
            token,
            invalid_draft,
        ),
        "failed",
        "COMBAT_BUILD_INVALID_WEAPON_LOADOUT",
    )
    after_invalid, _, _ = read_state(
        args.server_uri, args.hub_database, args.timeout_seconds, token
    )
    if after_invalid != readded:
        raise RuntimeError("invalid-draft rejection mutated canonical Hub state")

    print(
        json.dumps(
            {
                "event": "hub_combat_build_phase_2_pass",
                "identity": identity[:12],
                "revision": readded["revision"],
                "selected_disciplines": [
                    row["combat_discipline_id"] for row in readded["selected"]
                ],
                "dormant_disciplines": ["ARCHER_BOW"],
                "staff_schools": readded["schools"],
                "checks": [
                    "deterministic_default",
                    "save_reload",
                    "stale_revision_rejection",
                    "invalid_draft_rollback",
                    "dormant_remove_readd",
                    "per_discipline_weapons",
                ],
            },
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

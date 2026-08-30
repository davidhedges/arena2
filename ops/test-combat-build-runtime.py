#!/usr/bin/env python3
"""Exercise Combat Build v2 against a provisioned live local match.

The probe creates a fresh anonymous Hub identity, saves two Dagger Forms plus
one School, launches an unranked 2v2 bot match, and verifies exact snapshot
materialization, cross-weapon Spells, weapon-gated Techniques, cast interruption,
Staff Technique absence, switching, Perk/Trait state, and cleanup.

The canonical local stack must already be running:

  python3 ops/test-combat-build-runtime.py
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import sqlite3
import subprocess
import time
import uuid
from typing import Any, Callable

import websocket


PROTOCOL = "v1.json.spacetimedb"
DatabaseRow = list[Any] | dict[str, Any]
HUB_QUERIES = [
    'SELECT * FROM "my_hub_armor_selection"',
    'SELECT * FROM "my_combat_build_v_2"',
    'SELECT * FROM "my_match_status"',
]
MATCH_OWNER_TABLES = (
    "match_combat_build_v_2",
    "match_selected_specialization_v_2",
    "match_discipline_configuration_v_2",
    "match_technique_selection_v_2",
    "match_spell_selection_v_2",
    "match_perk_selection_v_2",
    "match_trait_selection_v_2",
    "active_combat_build_discipline",
    "player_equipment_presentation",
    "equipment_loadout",
)
EXPECTED_SPECIALIZATIONS = [
    (0, "DAGGERS_BLADEDANCER", "DAGGERS", "FORM"),
    (1, "DAGGERS_SHADOW", "DAGGERS", "FORM"),
    (2, "RUIN", "STAFF", "SCHOOL"),
]
EXPECTED_TECHNIQUES = {
    ("DAGGERS_BLADEDANCER", "DAGGERS", "DAGGER_LIGHTNING_REFLEXES", 0),
}
EXPECTED_SPELLS = {
    ("DAGGERS_SHADOW", "DAGGERS", "DAGGER_DARKNESS", 0),
    ("RUIN", "STAFF", "SPELL_METEOR", 1),
}
EXPECTED_PERKS = {
    ("RUIN", "STAFF", "RUIN_FLAMING_WEAPON"),
}
EXPECTED_WEAPONS = {
    "DAGGERS": "TRAINING_DAGGER_PAIR",
    "STAFF": "NEWBIE_STAFF_01",
}


def option_value(value: Any) -> Any:
    if isinstance(value, list) and value:
        if value[0] == 0 and len(value) >= 2:
            return value[1]
        if value[0] == 1:
            return None
    return value


def normalize_identity(value: Any) -> str:
    value = option_value(value)
    if isinstance(value, list) and value:
        value = value[0]
    if isinstance(value, dict):
        value = value.get("__identity__", "")
    return str(value or "").removeprefix("0x").lower()


def decoded_row(value: Any) -> DatabaseRow | None:
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except json.JSONDecodeError:
            return None
    return value if isinstance(value, (list, dict)) else None


def row_value(row: DatabaseRow, index: int, field: str) -> Any:
    if isinstance(row, dict):
        if field not in row:
            raise RuntimeError(f"database row is missing expected field {field!r}")
        return row[field]
    if index >= len(row):
        raise RuntimeError(
            f"database row has no positional field {index} for expected field {field!r}"
        )
    return row[index]


def inserted_rows(update: dict[str, Any] | None, table_name: str) -> list[DatabaseRow]:
    rows: list[DatabaseRow] = []
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


def committed_update(frame: dict[str, Any]) -> dict[str, Any] | None:
    if "InitialSubscription" in frame:
        return frame["InitialSubscription"].get("database_update")
    if "TransactionUpdateLight" in frame:
        return frame["TransactionUpdateLight"].get("update")
    if "TransactionUpdate" in frame:
        committed = frame["TransactionUpdate"].get("status", {}).get("Committed")
        return committed if isinstance(committed, dict) else None
    return None


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
        self.identity = normalize_identity(issued.get("identity"))
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
        timeout_seconds: float | None = None,
    ) -> Any:
        timeout_seconds = timeout_seconds or self.timeout_seconds
        deadline = time.monotonic() + timeout_seconds
        try:
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(
                        f"websocket condition was not met within {timeout_seconds}s"
                    )
                self.ws.settimeout(min(self.timeout_seconds, remaining))
                try:
                    frame = self.receive()
                except websocket.WebSocketTimeoutException as error:
                    raise TimeoutError(
                        f"websocket condition was not met within {timeout_seconds}s"
                    ) from error
                result = predicate(frame)
                if result is not None:
                    return result
        finally:
            self.ws.settimeout(self.timeout_seconds)

    def subscribe(self, queries: list[str]) -> dict[str, Any]:
        self.request_id += 1
        request_id = self.request_id
        self.ws.send(
            json.dumps(
                {"Subscribe": {"query_strings": queries, "request_id": request_id}}
            )
        )

        def select(frame: dict[str, Any]) -> dict[str, Any] | None:
            error = frame.get("SubscriptionError")
            if isinstance(error, dict) and int(error.get("request_id", -1)) == request_id:
                raise RuntimeError(f"subscription failed: {error}")
            initial = frame.get("InitialSubscription")
            if not isinstance(initial, dict):
                return None
            if int(initial.get("request_id", request_id)) != request_id:
                return None
            update = initial.get("database_update")
            if not isinstance(update, dict):
                raise RuntimeError("initial subscription omitted its database update")
            return update

        return self.wait_for(select)

    def call(self, reducer: str, args: list[Any]) -> tuple[str, str, dict[str, Any] | None]:
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

        def select(
            frame: dict[str, Any],
        ) -> tuple[str, str, dict[str, Any] | None] | None:
            update = frame.get("TransactionUpdate")
            if not isinstance(update, dict):
                return None
            reducer_call = update.get("reducer_call", {})
            if reducer_call.get("reducer_name") != reducer:
                return None
            status = update.get("status", {})
            if "Committed" in status:
                committed = status["Committed"]
                return (
                    "committed",
                    "",
                    committed if isinstance(committed, dict) else None,
                )
            if "Failed" in status:
                return ("failed", str(status["Failed"]), None)
            if "OutOfEnergy" in status:
                return ("out_of_energy", "reducer ran out of energy", None)
            return None

        return self.wait_for(select)

    def close(self) -> None:
        self.ws.close()


def require_status(
    result: tuple[str, str, dict[str, Any] | None],
    expected: str,
    detail_fragment: str | None = None,
) -> str:
    status, detail, _ = result
    if status != expected:
        raise RuntimeError(f"expected reducer status {expected}, received {status}: {detail}")
    if detail_fragment and detail_fragment.lower() not in detail.lower():
        raise RuntimeError(
            f"expected reducer detail containing {detail_fragment!r}, received: {detail}"
        )
    return detail


def parse_match_status(row: DatabaseRow) -> dict[str, str]:
    return {
        "ticket_id": str(row_value(row, 0, "ticket_id")),
        "status": str(row_value(row, 3, "status")),
        "match_id": str(option_value(row_value(row, 8, "match_id")) or ""),
        "server_uri": str(option_value(row_value(row, 9, "server_uri")) or ""),
        "database_identity": normalize_identity(
            row_value(row, 10, "database_identity")
        ),
        "match_build_id": str(
            option_value(row_value(row, 11, "match_build_id")) or ""
        ),
    }


def wait_for_hub_status(connection: Connection, expected: str) -> dict[str, str]:
    def select(frame: dict[str, Any]) -> dict[str, str] | None:
        for row in reversed(inserted_rows(committed_update(frame), "my_match_status")):
            status = parse_match_status(row)
            if status["status"] == expected:
                return status
        return None

    return connection.wait_for(select, 45.0)


def weapon(main_hand_item_def_id: str, color_id: str = "") -> dict[str, str]:
    return {
        "combat_discipline_id": "",
        "main_hand_item_def_id": main_hand_item_def_id,
        "main_hand_color_id": color_id,
        "off_hand_item_def_id": "",
        "off_hand_color_id": "",
    }


def runtime_draft(revision: int) -> dict[str, Any]:
    daggers = weapon("TRAINING_DAGGER_PAIR")
    daggers["combat_discipline_id"] = "DAGGERS"
    staff = weapon("NEWBIE_STAFF_01")
    staff["combat_discipline_id"] = "STAFF"
    return {
        "schema_version": 2,
        "revision": revision,
        "starting_discipline_id": [0, "DAGGERS"],
        "selected_specializations": [
            {"slot_index": slot, "specialization_id": specialization_id}
            for slot, specialization_id, _, _ in EXPECTED_SPECIALIZATIONS
        ],
        "dormant_specializations": [],
        "discipline_configurations": [daggers, staff],
        "selected_features": [
            {
                "specialization_id": "DAGGERS_BLADEDANCER",
                "ability_id": "DAGGER_LIGHTNING_REFLEXES",
                "preferred_bar_order": [0, 0],
            },
            {
                "specialization_id": "DAGGERS_SHADOW",
                "ability_id": "DAGGER_DARKNESS",
                "preferred_bar_order": [0, 0],
            },
            {
                "specialization_id": "RUIN",
                "ability_id": "SPELL_METEOR",
                "preferred_bar_order": [0, 1],
            },
            {
                "specialization_id": "RUIN",
                "ability_id": "RUIN_FLAMING_WEAPON",
                "preferred_bar_order": [1, {}],
            },
        ],
        "selected_traits": ["MASTERY"],
    }


def match_queries(identity: str) -> list[str]:
    identity_literal = f"0x{identity}"
    queries = [
        f'SELECT * FROM "{table}" WHERE ("{table}"."owner" = {identity_literal})'
        for table in MATCH_OWNER_TABLES
    ]
    queries.extend(
        [
            'SELECT * FROM "player_physics"',
            'SELECT * FROM "player_world"',
            'SELECT * FROM "match_participant"',
            'SELECT * FROM "status_effect" '
            f'WHERE ("status_effect"."target" = {identity_literal})',
            'SELECT * FROM "active_cast" '
            f'WHERE ("active_cast"."caster" = {identity_literal})',
        ]
    )
    return queries


def snapshot(
    connection: Connection,
    identity: str,
) -> dict[str, list[DatabaseRow]]:
    if connection.identity != identity:
        raise RuntimeError("snapshot connection changed the authenticated identity")
    update = connection.subscribe(match_queries(identity))
    return {
        table_name: inserted_rows(update, table_name)
        for table_name in (
            *MATCH_OWNER_TABLES,
            "player_physics",
            "player_world",
            "match_participant",
            "status_effect",
            "active_cast",
        )
    }


def assert_frozen_snapshot(
    rows: dict[str, list[DatabaseRow]],
    expected_active: str,
) -> dict[str, Any]:
    selected = sorted(
        (
            int(row_value(row, 2, "slot_index")),
            str(row_value(row, 3, "specialization_id")),
            str(row_value(row, 4, "combat_discipline_id")),
            str(row_value(row, 5, "specialization_kind")),
        )
        for row in rows["match_selected_specialization_v_2"]
    )
    if selected != EXPECTED_SPECIALIZATIONS:
        raise RuntimeError(f"frozen selected Specializations differ: {selected}")

    configurations = {
        str(row_value(row, 2, "combat_discipline_id")): str(
            row_value(row, 3, "main_hand_item_def_id")
        )
        for row in rows["match_discipline_configuration_v_2"]
    }
    if configurations != EXPECTED_WEAPONS:
        raise RuntimeError(f"frozen weapon configurations differ: {configurations}")
    if any(
        not str(option_value(row_value(row, 7, "main_hand_item_id")) or "")
        for row in rows["match_discipline_configuration_v_2"]
    ):
        raise RuntimeError("a selected discipline weapon was not materialized")

    techniques = {
        (
            str(row_value(row, 2, "specialization_id")),
            str(row_value(row, 3, "combat_discipline_id")),
            str(row_value(row, 4, "ability_id")),
            int(row_value(row, 5, "bar_order")),
        )
        for row in rows["match_technique_selection_v_2"]
    }
    if techniques != EXPECTED_TECHNIQUES:
        raise RuntimeError(f"frozen Technique selections differ: {techniques}")
    if any(parent == "STAFF" for _, parent, _, _ in techniques):
        raise RuntimeError("Staff materialized a Technique")
    spells = {
        (
            str(row_value(row, 2, "specialization_id")),
            str(row_value(row, 3, "combat_discipline_id")),
            str(row_value(row, 4, "ability_id")),
            int(row_value(row, 5, "bar_order")),
        )
        for row in rows["match_spell_selection_v_2"]
    }
    if spells != EXPECTED_SPELLS:
        raise RuntimeError(f"frozen Spell selections differ: {spells}")
    perks = {
        (
            str(row_value(row, 2, "specialization_id")),
            str(row_value(row, 3, "combat_discipline_id")),
            str(row_value(row, 4, "ability_id")),
        )
        for row in rows["match_perk_selection_v_2"]
    }
    if perks != EXPECTED_PERKS:
        raise RuntimeError(f"frozen Perk selections differ: {perks}")
    traits = {
        str(row_value(row, 2, "ability_id"))
        for row in rows["match_trait_selection_v_2"]
    }
    if traits != {"MASTERY"}:
        raise RuntimeError(f"frozen Trait selections differ: {traits}")
    build_rows = rows["match_combat_build_v_2"]
    if len(build_rows) != 1 or bool(row_value(build_rows[0], 4, "mastery_active")):
        raise RuntimeError("mixed-parent build did not materialize inactive Mastery")

    if len(rows["active_combat_build_discipline"]) != 1:
        raise RuntimeError("active combat discipline row is missing or duplicated")
    active = rows["active_combat_build_discipline"][0]
    active_discipline = str(row_value(active, 1, "combat_discipline_id"))
    if active_discipline != expected_active:
        raise RuntimeError(f"active discipline differs: {active_discipline}")

    if len(rows["player_equipment_presentation"]) != 1:
        raise RuntimeError("equipment presentation row is missing or duplicated")
    equipment = rows["player_equipment_presentation"][0]
    main_hand_item_def_id = str(
        option_value(row_value(equipment, 8, "main_hand_item_def_id")) or ""
    )
    if main_hand_item_def_id != EXPECTED_WEAPONS[expected_active]:
        raise RuntimeError(
            f"{expected_active} equipped {main_hand_item_def_id!r}, expected "
            f"{EXPECTED_WEAPONS[expected_active]!r}"
        )

    return {
        "active_discipline": active_discipline,
        "main_hand_item_def_id": main_hand_item_def_id,
        "techniques": sorted(techniques),
        "spells": sorted(spells),
        "perks": sorted(perks),
        "mastery_active": False,
    }


def physics_row(rows: dict[str, list[DatabaseRow]], identity: str) -> DatabaseRow:
    matches = [
        row
        for row in rows["player_physics"]
        if normalize_identity(row_value(row, 0, "identity")) == identity
    ]
    if len(matches) != 1:
        raise RuntimeError(f"player physics row is missing or duplicated for {identity[:12]}")
    return matches[0]


def caster_position(
    rows: dict[str, list[DatabaseRow]], identity: str
) -> tuple[float, float, float, float]:
    row = physics_row(rows, identity)
    return (
        float(row_value(row, 1, "pos_x")),
        float(row_value(row, 2, "pos_y")),
        float(row_value(row, 3, "pos_z")),
        float(row_value(row, 7, "yaw")),
    )


def cast_args(
    action_id: str,
    rows: dict[str, list[DatabaseRow]],
    identity: str,
    target_identity: str = "",
) -> list[Any]:
    pos_x, pos_y, pos_z, yaw = caster_position(rows, identity)
    aim_x, aim_y, aim_z = pos_x, pos_y, pos_z
    if target_identity:
        target = physics_row(rows, target_identity)
        aim_x = float(row_value(target, 1, "pos_x"))
        aim_y = float(row_value(target, 2, "pos_y"))
        aim_z = float(row_value(target, 3, "pos_z"))
    return [
        action_id,
        target_identity,
        aim_x,
        aim_y,
        aim_z,
        0,
        pos_x,
        pos_y,
        pos_z,
        yaw,
        "",
        0,
        0,
    ]


def other_player_identity(rows: dict[str, list[DatabaseRow]], identity: str) -> str:
    participants = rows["match_participant"]
    own_rows = [
        row
        for row in participants
        if normalize_identity(row_value(row, 0, "identity")) == identity
    ]
    if len(own_rows) != 1:
        raise RuntimeError("the provisioned match omitted the caller participant")
    own_team = int(row_value(own_rows[0], 2, "team_id"))
    candidates = sorted(
        normalize_identity(row_value(row, 0, "identity"))
        for row in participants
        if int(row_value(row, 2, "team_id")) != own_team
    )
    if not candidates:
        raise RuntimeError("the provisioned 2v2 match exposed no target player")
    return min(
        candidates,
        key=lambda candidate: _horizontal_distance(
            physics_row(rows, identity), physics_row(rows, candidate)
        ),
    )


def _horizontal_distance(first: DatabaseRow, second: DatabaseRow) -> float:
    dx = float(row_value(first, 1, "pos_x")) - float(row_value(second, 1, "pos_x"))
    dz = float(row_value(first, 3, "pos_z")) - float(row_value(second, 3, "pos_z"))
    return dx * dx + dz * dz


def wait_for_status(
    connection: Connection,
    identity: str,
    stack_group: str,
    timeout_seconds: float = 5.0,
) -> dict[str, list[DatabaseRow]]:
    deadline = time.monotonic() + timeout_seconds
    last_rows: dict[str, list[DatabaseRow]] | None = None
    while time.monotonic() < deadline:
        last_rows = snapshot(connection, identity)
        if any(
            str(row_value(row, 5, "stack_group")) == stack_group
            for row in last_rows["status_effect"]
        ):
            return last_rows
        time.sleep(0.1)
    observed = [] if last_rows is None else [
        str(row_value(row, 5, "stack_group"))
        for row in last_rows["status_effect"]
    ]
    raise RuntimeError(
        f"timed out waiting for status stack group {stack_group!r}; observed={observed}"
    )


def wait_for_active_cast(
    connection: Connection,
    identity: str,
    expected_present: bool,
    timeout_seconds: float = 5.0,
) -> dict[str, list[DatabaseRow]]:
    deadline = time.monotonic() + timeout_seconds
    last_rows: dict[str, list[DatabaseRow]] | None = None
    while time.monotonic() < deadline:
        last_rows = snapshot(connection, identity)
        if bool(last_rows["active_cast"]) == expected_present:
            return last_rows
        time.sleep(0.05)
    observed = 0 if last_rows is None else len(last_rows["active_cast"])
    raise RuntimeError(
        f"timed out waiting for active_cast present={expected_present}; observed={observed}"
    )


def capture_authorization_logs(database_identity: str) -> list[str]:
    result = subprocess.run(
        [
            "spacetime",
            "logs",
            database_identity,
            "--server",
            "local",
            "--num-lines",
            "500",
        ],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise RuntimeError(f"spacetime logs failed: {result.stderr.strip()}")
    lines = [line for line in result.stdout.splitlines() if "[COMBAT_BUILD_AUTH]" in line]
    expected_reasons = {
        "reason=WRONG_WEAPON",
        "reason=UNSELECTED_FEATURE",
    }
    missing = sorted(
        reason for reason in expected_reasons if not any(reason in line for line in lines)
    )
    if missing:
        raise RuntimeError(
            f"instrumented authorization logs omitted denial reasons: {missing}; lines={lines}"
        )
    return lines


def wait_for_cleanup(
    ledger_path: pathlib.Path,
    ticket_id: str,
    timeout_seconds: float,
) -> str:
    expected_log_id = hashlib.sha256(ticket_id.encode()).hexdigest()[:12]
    deadline = time.monotonic() + timeout_seconds
    last_state = "MISSING"
    while time.monotonic() < deadline:
        if ledger_path.exists():
            with sqlite3.connect(ledger_path) as connection:
                row = connection.execute(
                    "SELECT state FROM allocations WHERE ticket_id = ?", (ticket_id,)
                ).fetchone()
            if row is not None:
                last_state = str(row[0])
                if last_state == "CLEANED":
                    return expected_log_id
        time.sleep(0.25)
    raise RuntimeError(
        f"timed out waiting for cleanup of ticket {expected_log_id}; state={last_state}"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-uri", default="ws://127.0.0.1:3000")
    parser.add_argument("--hub-database", default="arena-hub-local")
    parser.add_argument("--timeout-seconds", type=float, default=20.0)
    parser.add_argument("--cleanup-timeout-seconds", type=float, default=45.0)
    parser.add_argument(
        "--ledger",
        type=pathlib.Path,
        default=pathlib.Path("Library/ArenaMatchProvisioner/state.sqlite3"),
    )
    args = parser.parse_args()

    hub = Connection(args.server_uri, args.hub_database, args.timeout_seconds)
    match: Connection | None = None
    assignment: dict[str, str] | None = None
    denial_details: dict[str, str] = {}
    switch_sequence: list[dict[str, Any]] = []
    auth_logs: list[str] = []
    interruption_passed = False
    cross_weapon_spells: list[str] = []
    try:
        initial = hub.subscribe(HUB_QUERIES)
        build_rows = inserted_rows(initial, "my_combat_build_v_2")
        if len(build_rows) != 1:
            raise RuntimeError(
                f"expected one default Hub combat build, received {len(build_rows)}"
            )
        revision = int(row_value(build_rows[0], 2, "revision"))
        require_status(
            hub.call("save_combat_build_v_2", [runtime_draft(revision)]), "committed"
        )

        request_id = f"combat-build-runtime-{uuid.uuid4().hex}"
        require_status(
            hub.call("request_unranked_2_v_2_bot_match", [request_id]), "committed"
        )
        assignment = wait_for_hub_status(hub, "READY")
        if not all(
            assignment[key]
            for key in (
                "ticket_id",
                "match_id",
                "server_uri",
                "database_identity",
                "match_build_id",
            )
        ):
            raise RuntimeError(f"Hub READY assignment is incomplete: {assignment}")

        match = Connection(
            assignment["server_uri"],
            assignment["database_identity"],
            args.timeout_seconds,
            hub.token,
        )
        if match.identity != hub.identity:
            raise RuntimeError("Hub and match identities differ")

        rows = snapshot(match, hub.identity)
        switch_sequence.append(assert_frozen_snapshot(rows, "DAGGERS"))
        target_identity = other_player_identity(rows, hub.identity)

        require_status(
            match.call(
                "cast_request",
                cast_args("LIGHTNING_REFLEXES", rows, hub.identity),
            ),
            "committed",
        )
        rows = wait_for_status(match, hub.identity, "LIGHTNING_REFLEXES")
        time.sleep(1.6)

        require_status(
            match.call("cast_request", cast_args("METEOR", rows, hub.identity)),
            "committed",
        )
        cross_weapon_spells.append("SCHOOL:METEOR@DAGGERS")
        rows = wait_for_active_cast(match, hub.identity, True)
        yaw = caster_position(rows, hub.identity)[3]
        require_status(
            match.call("send_movement_intent", [1.0, 0.0, yaw, False, 1]),
            "committed",
        )
        rows = wait_for_active_cast(match, hub.identity, False)
        interruption_passed = True

        require_status(match.call("activate_combat_build_discipline", ["STAFF"]), "committed")
        rows = snapshot(match, hub.identity)
        switch_sequence.append(assert_frozen_snapshot(rows, "STAFF"))

        denial_details["wrong_weapon_technique"] = require_status(
            match.call(
                "cast_request",
                cast_args("LIGHTNING_REFLEXES", rows, hub.identity),
            ),
            "failed",
        )
        denial_details["unselected_feature"] = require_status(
            match.call("cast_request", cast_args("NOVA", rows, hub.identity)),
            "failed",
        )

        time.sleep(1.6)
        require_status(
            match.call(
                "cast_request",
                cast_args("DARKNESS", rows, hub.identity, target_identity),
            ),
            "committed",
        )
        cross_weapon_spells.append("FORM:DARKNESS@STAFF")

        require_status(match.call("activate_combat_build_discipline", ["DAGGERS"]), "committed")
        rows = snapshot(match, hub.identity)
        switch_sequence.append(assert_frozen_snapshot(rows, "DAGGERS"))
        auth_logs = capture_authorization_logs(assignment["database_identity"])
    finally:
        if assignment is not None:
            require_status(
                hub.call("cancel_match_ticket", [assignment["ticket_id"]]), "committed"
            )
            wait_for_hub_status(hub, "CLOSED")
        if match is not None:
            match.close()
        hub.close()

    if assignment is None:
        raise RuntimeError("runtime probe ended without a match assignment")
    ticket_log_id = wait_for_cleanup(
        args.ledger, assignment["ticket_id"], args.cleanup_timeout_seconds
    )
    print(
        json.dumps(
            {
                "event": "combat_build_v2_runtime_phase_7_pass",
                "identity": hub.identity[:12],
                "database": hashlib.sha256(
                    assignment["database_identity"].encode()
                ).hexdigest()[:12],
                "ticket": ticket_log_id,
                "selected_specializations": [
                    specialization_id
                    for _, specialization_id, _, _ in EXPECTED_SPECIALIZATIONS
                ],
                "derived_disciplines": sorted(EXPECTED_WEAPONS),
                "feature_count": 4,
                "switch_sequence": switch_sequence,
                "denials": sorted(denial_details),
                "authorization_log_reasons": sorted(
                    {
                        line.split("reason=", 1)[1].split()[0]
                        for line in auth_logs
                        if "reason=" in line
                    }
                ),
                "cross_weapon_spells": cross_weapon_spells,
                "movement_interrupt": interruption_passed,
                "staff_techniques": 0,
                "mastery_active": False,
                "alternate_authority_schema": "ABSENT",
                "cleanup": "CLEANED",
            },
            sort_keys=True,
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

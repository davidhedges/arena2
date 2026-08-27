#!/usr/bin/env python3
"""Exercise the frozen combat-build runtime against a live local match.

The probe creates a fresh anonymous Hub identity, saves a three-discipline
build, launches an unranked 2v2 bot match, and verifies the Phase 4 runtime
boundary. It intentionally tests both accepted and denied reducer calls, then
cancels the ticket and waits for exact-identity provisioner cleanup.

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
    'SELECT * FROM "my_hub_loadout"',
    'SELECT * FROM "my_combat_build"',
    'SELECT * FROM "my_match_status"',
]
MATCH_OWNER_TABLES = (
    "match_combat_build",
    "match_combat_build_discipline",
    "match_discipline_configuration",
    "match_staff_school_selection",
    "match_discipline_action_bar_assignment",
    "match_discipline_passive_selection",
    "active_combat_discipline",
    "player_equipment_presentation",
    "character_action_bar_assignment",
    "character_discipline_loadout",
    "character_discipline_ability_selection",
    "character_combat_discipline_weapon_loadout",
    "player_known_spell",
    "equipment_loadout",
)
EXPECTED_SELECTED = ["DAGGERS", "ARCHER_BOW", "STAFF"]
EXPECTED_ASSIGNMENTS = {
    ("DAGGERS", "slot_0_0", "DAGGER_QUICK_CUT"),
    ("ARCHER_BOW", "slot_0_0", "ARCHER_POWER_SHOT"),
    ("STAFF", "slot_0_0", "SPELL_FIREBALL"),
    ("STAFF", "slot_0_1", "SPELL_MANA_SHIELD"),
}
EXPECTED_WEAPONS = {
    "DAGGERS": "TRAINING_DAGGER_PAIR",
    "ARCHER_BOW": "TRAINING_BOW",
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
        "main_hand_item_def_id": main_hand_item_def_id,
        "main_hand_color_id": color_id,
        "off_hand_item_def_id": "",
        "off_hand_color_id": "",
    }


def runtime_draft(revision: int) -> dict[str, Any]:
    return {
        "revision": revision,
        "starting_discipline_id": None,
        "selected_disciplines": [
            {"slot_index": index, "combat_discipline_id": discipline_id}
            for index, discipline_id in enumerate(EXPECTED_SELECTED)
        ],
        "discipline_configurations": [
            {
                "combat_discipline_id": "DAGGERS",
                "weapon": weapon("TRAINING_DAGGER_PAIR"),
                "staff_school_ids": [],
                "active_assignments": [
                    {
                        "action_slot": "slot_0_0",
                        "ability_id": "DAGGER_QUICK_CUT",
                    }
                ],
                "passive_ability_ids": [],
            },
            {
                "combat_discipline_id": "ARCHER_BOW",
                "weapon": weapon("TRAINING_BOW", "DEFAULT"),
                "staff_school_ids": [],
                "active_assignments": [
                    {
                        "action_slot": "slot_0_0",
                        "ability_id": "ARCHER_POWER_SHOT",
                    }
                ],
                "passive_ability_ids": [],
            },
            {
                "combat_discipline_id": "STAFF",
                "weapon": weapon("NEWBIE_STAFF_01", "DEFAULT"),
                "staff_school_ids": ["RUIN", "ARCANA"],
                "active_assignments": [
                    {"action_slot": "slot_0_0", "ability_id": "SPELL_FIREBALL"},
                    {
                        "action_slot": "slot_0_1",
                        "ability_id": "SPELL_MANA_SHIELD",
                    },
                ],
                "passive_ability_ids": ["RUIN_FLAMING_WEAPON"],
            },
        ],
    }


def match_queries(identity: str) -> list[str]:
    identity_literal = f"0x{identity}"
    queries = [
        f'SELECT * FROM "{table}" WHERE ("{table}"."owner" = {identity_literal})'
        for table in MATCH_OWNER_TABLES
    ]
    queries.extend(
        [
            'SELECT * FROM "player_physics" '
            f'WHERE ("player_physics"."identity" = {identity_literal})',
            'SELECT * FROM "status_effect" '
            f'WHERE ("status_effect"."target" = {identity_literal})',
            'SELECT "item_spell".* FROM "item_instance" '
            'JOIN "item_spell" ON '
            '"item_instance"."item_instance_id" = "item_spell"."item_instance_id" '
            f'WHERE ("item_instance"."current_owner_key" = \'{identity}\')',
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
        for table_name in (*MATCH_OWNER_TABLES, "player_physics", "status_effect", "item_spell")
    }


def assert_frozen_snapshot(
    rows: dict[str, list[DatabaseRow]],
    expected_active: str,
) -> dict[str, Any]:
    selected = sorted(
        (
            int(row_value(row, 2, "slot_index")),
            str(row_value(row, 3, "combat_discipline_id")),
        )
        for row in rows["match_combat_build_discipline"]
    )
    if [discipline for _, discipline in selected] != EXPECTED_SELECTED:
        raise RuntimeError(f"frozen selected disciplines differ: {selected}")

    configurations = {
        str(row_value(row, 2, "combat_discipline_id")): str(
            row_value(row, 3, "main_hand_item_def_id")
        )
        for row in rows["match_discipline_configuration"]
    }
    if configurations != EXPECTED_WEAPONS:
        raise RuntimeError(f"frozen weapon configurations differ: {configurations}")
    if any(
        not str(option_value(row_value(row, 7, "main_hand_item_id")) or "")
        for row in rows["match_discipline_configuration"]
    ):
        raise RuntimeError("a selected discipline weapon was not materialized")

    schools = sorted(
        str(row_value(row, 2, "spell_school_id"))
        for row in rows["match_staff_school_selection"]
    )
    if schools != ["ARCANA", "RUIN"]:
        raise RuntimeError(f"Staff schools differ: {schools}")

    assignments = {
        (
            str(row_value(row, 2, "combat_discipline_id")),
            str(row_value(row, 3, "action_slot")),
            str(row_value(row, 4, "ability_id")),
        )
        for row in rows["match_discipline_action_bar_assignment"]
    }
    if assignments != EXPECTED_ASSIGNMENTS:
        raise RuntimeError(f"frozen active assignments differ: {assignments}")
    passives = {
        (
            str(row_value(row, 2, "combat_discipline_id")),
            str(row_value(row, 3, "ability_id")),
        )
        for row in rows["match_discipline_passive_selection"]
    }
    if passives != {("STAFF", "RUIN_FLAMING_WEAPON")}:
        raise RuntimeError(f"frozen passive selections differ: {passives}")

    if len(rows["active_combat_discipline"]) != 1:
        raise RuntimeError("active combat discipline row is missing or duplicated")
    active = rows["active_combat_discipline"][0]
    active_discipline = str(row_value(active, 1, "discipline_id"))
    active_profile = str(row_value(active, 2, "combat_profile_id"))
    if active_discipline != expected_active or active_profile != expected_active:
        raise RuntimeError(
            f"active discipline/profile differs: {active_discipline}/{active_profile}"
        )

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

    bars = rows["character_action_bar_assignment"]
    switches = {
        (
            str(row_value(row, 2, "combat_profile_id")),
            str(row_value(row, 3, "slot_id")),
            str(row_value(row, 4, "action_kind")),
            str(row_value(row, 5, "action_id")),
        )
        for row in bars
        if str(row_value(row, 4, "action_kind")) == "COMBAT_DISCIPLINE_SWITCH"
    }
    expected_switches = {
        ("GLOBAL", f"DISCIPLINE_{index}", "COMBAT_DISCIPLINE_SWITCH", discipline)
        for index, discipline in enumerate(EXPECTED_SELECTED)
    }
    if switches != expected_switches:
        raise RuntimeError(f"global discipline switches differ: {switches}")

    projected_abilities = {
        (
            str(row_value(row, 2, "combat_profile_id")),
            str(row_value(row, 3, "slot_id")),
            str(row_value(row, 6, "ability_id")),
        )
        for row in bars
        if str(row_value(row, 4, "action_kind")) == "ABILITY"
        and str(row_value(row, 2, "combat_profile_id")) != "GLOBAL"
    }
    expected_projected_abilities = {
        (discipline, action_slot.upper(), ability_id)
        for discipline, action_slot, ability_id in EXPECTED_ASSIGNMENTS
    }
    if projected_abilities != expected_projected_abilities:
        raise RuntimeError(
            f"per-discipline compatibility action bars differ: {projected_abilities}"
        )

    for legacy_table in (
        "character_discipline_loadout",
        "character_discipline_ability_selection",
    ):
        if rows[legacy_table]:
            raise RuntimeError(
                f"provisioned frozen build unexpectedly populated {legacy_table}"
            )

    return {
        "active_discipline": active_discipline,
        "main_hand_item_def_id": main_hand_item_def_id,
        "active_assignments": sorted(assignments),
        "passives": sorted(passives),
    }


def caster_position(rows: dict[str, list[DatabaseRow]]) -> tuple[float, float, float, float]:
    if len(rows["player_physics"]) != 1:
        raise RuntimeError("player physics row is missing or duplicated")
    row = rows["player_physics"][0]
    return (
        float(row_value(row, 1, "pos_x")),
        float(row_value(row, 2, "pos_y")),
        float(row_value(row, 3, "pos_z")),
        float(row_value(row, 7, "yaw")),
    )


def cast_args(action_id: str, rows: dict[str, list[DatabaseRow]]) -> list[Any]:
    pos_x, pos_y, pos_z, yaw = caster_position(rows)
    return [
        action_id,
        "",
        pos_x,
        pos_y,
        pos_z,
        0,
        pos_x,
        pos_y,
        pos_z,
        yaw,
        "",
        0,
        0,
    ]


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
        "reason=WRONG_ACTION_BAR",
        "reason=WRONG_STAFF_SCHOOL",
        "reason=DORMANT_DISCIPLINE",
        "reason=UNASSIGNED",
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
    try:
        initial = hub.subscribe(HUB_QUERIES)
        build_rows = inserted_rows(initial, "my_combat_build")
        if len(build_rows) != 1:
            raise RuntimeError(
                f"expected one default Hub combat build, received {len(build_rows)}"
            )
        revision = int(row_value(build_rows[0], 2, "revision"))
        require_status(
            hub.call("save_combat_build", [runtime_draft(revision)]), "committed"
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

        for label, reducer, reducer_args in (
            (
                "assign_action_bar",
                "assign_character_action_bar_ability_to_slot",
                ["slot_0_5", "DAGGER_QUICK_CUT"],
            ),
            ("clear_action_bar", "clear_character_action_bar_slot", ["slot_0_0"]),
            (
                "assign_weapon",
                "assign_combat_discipline_weapon_loadout",
                ["DAGGERS", None, None],
            ),
        ):
            denial_details[label] = require_status(
                match.call(reducer, reducer_args), "failed", "frozen"
            )

        require_status(match.call("set_combat_discipline", ["ARCHER_BOW"]), "committed")
        rows = snapshot(match, hub.identity)
        switch_sequence.append(assert_frozen_snapshot(rows, "ARCHER_BOW"))
        denial_details["wrong_action_bar"] = require_status(
            match.call("cast_request", cast_args("MANA_SHIELD", rows)), "failed"
        )

        require_status(match.call("set_combat_discipline", ["STAFF"]), "committed")
        rows = snapshot(match, hub.identity)
        switch_sequence.append(assert_frozen_snapshot(rows, "STAFF"))

        for label, action_id in (
            ("unassigned", "NOVA"),
            ("wrong_staff_school", "GIGANTISM"),
            ("dormant_discipline", "FRENZY"),
        ):
            denial_details[label] = require_status(
                match.call("cast_request", cast_args(action_id, rows)), "failed"
            )

        require_status(match.call("learn_spell", ["NOVA"]), "committed")
        denial_details["learned_only"] = require_status(
            match.call("cast_request", cast_args("NOVA", rows)), "failed"
        )
        require_status(
            match.call("assign_equipped_spellbook_spell", [0, "BOLT"]), "committed"
        )
        denial_details["spellbook_only"] = require_status(
            match.call("cast_request", cast_args("BOLT", rows)), "failed"
        )

        rows = snapshot(match, hub.identity)
        known_spells = {
            str(row_value(row, 2, "spell_id")) for row in rows["player_known_spell"]
        }
        if "NOVA" not in known_spells:
            raise RuntimeError("learn_spell did not persist NOVA collection ownership")
        item_spells = {str(row_value(row, 3, "spell_id")) for row in rows["item_spell"]}
        if "BOLT" not in item_spells:
            raise RuntimeError("spellbook assignment did not persist BOLT")

        require_status(
            match.call("cast_request", cast_args("MANA_SHIELD", rows)), "committed"
        )
        rows = wait_for_status(match, hub.identity, "MANA_SHIELD")

        require_status(match.call("set_combat_discipline", ["DAGGERS"]), "committed")
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
                "event": "combat_build_runtime_phase_4_pass",
                "identity": hub.identity[:12],
                "database": hashlib.sha256(
                    assignment["database_identity"].encode()
                ).hexdigest()[:12],
                "ticket": ticket_log_id,
                "selected_disciplines": EXPECTED_SELECTED,
                "staff_schools": ["ARCANA", "RUIN"],
                "combined_ability_count": 5,
                "active_ability_count": 4,
                "switch_sequence": switch_sequence,
                "denials": sorted(denial_details),
                "authorization_log_reasons": sorted(
                    {
                        line.split("reason=", 1)[1].split()[0]
                        for line in auth_logs
                        if "reason=" in line
                    }
                ),
                "positive_cast": "MANA_SHIELD",
                "collection_only_checks": ["learned:NOVA", "spellbook:BOLT"],
                "cleanup": "CLEANED",
            },
            sort_keys=True,
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

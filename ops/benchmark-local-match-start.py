#!/usr/bin/env python3
"""Benchmark the local disposable PvP match-start path.

The probe keeps one anonymous identity for the entire run, requests serial
unranked 2v2 bot matches through the public Hub API, authenticates to every
assigned match with that same identity, and applies the production 44-query
PvP initial subscription plus a canonical combat-build audit subscription.
Each ticket is cancelled after its sample, and the probe waits for the
provisioner's exact-identity cleanup ledger to report all sampled databases
CLEANED.

This intentionally measures through the match initial-state boundary, not
Unity scene loading. Pair its JSON output with the provisioner's correlated
``match_startup_timing`` events for publish/bootstrap stage percentiles.

The local SpacetimeDB server and provisioner must already be running:

  python3 ops/benchmark-local-match-start.py --samples 20
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import pathlib
import sqlite3
import statistics
import time
import uuid
from typing import Any, Callable

import websocket


PROTOCOL = "v1.json.spacetimedb"
DatabaseRow = list[Any] | dict[str, Any]
HUB_QUERIES = [
    'SELECT * FROM "my_hub_player"',
    'SELECT * FROM "my_hub_loadout"',
    'SELECT * FROM "my_match_status"',
    'SELECT * FROM "my_combat_build"',
]
PVP_STATIC_TABLES = [
    "ability_catalog",
    "action_presentation_catalog",
    "combat_vfx_cue_catalog",
    "combat_projectile_definition",
    "combat_profile_catalog",
    "combat_discipline_catalog",
    "combat_mode_catalog",
    "action_bar_slot_catalog",
    "item_definition",
    "armor_set_definition",
    "item_affix_definition",
    "spell_definition",
    "melee_definition",
    "melee_ability_catalog",
    "melee_gap_close_catalog",
    "melee_attack_modifier_catalog",
    "auto_attack_catalog",
    "combat_rule_catalog",
    "resource_catalog",
    "stat_scaling_catalog",
]
PVP_LOCAL_FILTERS = [
    ("character_action_bar_assignment", "owner"),
    ("character_appearance", "owner"),
    ("player_known_spell", "owner"),
    ("global_cooldown", "caster"),
    ("spell_cooldown", "caster"),
    ("predicted_action_result", "owner"),
    ("fixed_action_charge_state", "owner"),
    ("active_combat_discipline", "owner"),
    ("character_discipline_loadout", "owner"),
    ("character_discipline_ability_selection", "owner"),
    ("character_combat_discipline_weapon_loadout", "owner"),
    ("active_combat_mode", "owner"),
    ("auto_attack_state", "owner"),
    ("equipment_loadout", "owner"),
    ("player_equipment_presentation", "owner"),
    ("active_armor_set", "owner"),
]
MATCH_COMBAT_BUILD_TABLES = (
    "match_combat_build",
    "match_combat_build_discipline",
    "match_discipline_configuration",
    "match_staff_school_selection",
    "match_discipline_action_bar_assignment",
    "match_discipline_passive_selection",
)
WEAPON_APPEARANCE_CATALOG_PATH = (
    pathlib.Path(__file__).resolve().parents[1]
    / "Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json"
)


def effective_weapon_color_id(item_def_id: str, configured_color_id: str) -> str:
    item_def_id = item_def_id.strip().upper()
    configured_color_id = configured_color_id.strip().upper()
    if not item_def_id:
        if configured_color_id:
            raise RuntimeError("a configured weapon color requires a weapon definition")
        return ""
    if configured_color_id:
        return configured_color_id

    catalog = json.loads(WEAPON_APPEARANCE_CATALOG_PATH.read_text())
    for family in catalog.get("families", []):
        if str(family.get("item_def_id", "")).strip().upper() != item_def_id:
            continue
        default_color_id = str(family.get("default_color_id", "")).strip().upper()
        variant_color_ids = {
            str(variant.get("color_id", "")).strip().upper()
            for variant in family.get("variants", [])
        }
        if not default_color_id or default_color_id not in variant_color_ids:
            raise RuntimeError(
                f"weapon {item_def_id!r} has no valid authored default color"
            )
        return default_color_id
    raise RuntimeError(f"unknown weapon appearance family {item_def_id!r}")


def normalize_identity(value: Any) -> str:
    value = option_value(value)
    if isinstance(value, list) and value:
        value = value[0]
    if isinstance(value, dict):
        value = value.get("__identity__", "")
    return str(value or "").removeprefix("0x").lower()


def option_value(value: Any) -> Any:
    # SpacetimeDB's JSON protocol represents Rust Option<T> as the tagged
    # algebraic value [0, value] for Some and either [1] or [1, {}] for None.
    if isinstance(value, list) and value:
        if value[0] == 0 and len(value) >= 2:
            return value[1]
        if value[0] == 1:
            return None
    return value


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


def database_update(frame: dict[str, Any]) -> dict[str, Any] | None:
    if "InitialSubscription" in frame:
        return frame["InitialSubscription"].get("database_update")
    if "TransactionUpdateLight" in frame:
        return frame["TransactionUpdateLight"].get("update")
    if "TransactionUpdate" in frame:
        committed = frame["TransactionUpdate"].get("status", {}).get("Committed")
        return committed if isinstance(committed, dict) else None
    return None


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


def parse_match_status(row: DatabaseRow) -> dict[str, str]:
    if len(row) < 14:
        raise RuntimeError(f"unexpected my_match_status row length: {len(row)}")
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
        "map_id": str(option_value(row_value(row, 12, "map_id")) or ""),
    }


def parse_hub_armor(row: DatabaseRow) -> dict[str, Any]:
    if len(row) < 12:
        raise RuntimeError(f"unexpected my_hub_loadout row length: {len(row)}")
    return {
        "owner": normalize_identity(row_value(row, 0, "owner")),
        "armor_set_id": str(row_value(row, 5, "armor_set_id")),
    }


def parse_hub_combat_build(row: DatabaseRow) -> dict[str, Any]:
    selected_rows = row_value(row, 3, "selected_disciplines")
    configuration_rows = row_value(row, 4, "discipline_configurations")
    if not isinstance(selected_rows, list) or not isinstance(configuration_rows, list):
        raise RuntimeError("Hub combat build contains malformed nested rows")
    selected = [
        {
            "slot_index": int(row_value(selected_row, 0, "slot_index")),
            "combat_discipline_id": str(
                row_value(selected_row, 1, "combat_discipline_id")
            ),
        }
        for selected_row in selected_rows
    ]
    selected.sort(key=lambda value: value["slot_index"])
    if not selected:
        raise RuntimeError("Hub combat build has no selected disciplines")

    selected_ids = {value["combat_discipline_id"] for value in selected}
    configurations = []
    schools = []
    assignments = []
    passives = []
    for configuration_row in configuration_rows:
        discipline_id = str(
            row_value(configuration_row, 0, "combat_discipline_id")
        )
        if discipline_id not in selected_ids:
            continue
        weapon = row_value(configuration_row, 1, "weapon")
        configurations.append(
            {
                "combat_discipline_id": discipline_id,
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
        staff_school_ids = row_value(configuration_row, 2, "staff_school_ids")
        active_assignments = row_value(configuration_row, 3, "active_assignments")
        passive_ability_ids = row_value(configuration_row, 4, "passive_ability_ids")
        if not all(
            isinstance(values, list)
            for values in (staff_school_ids, active_assignments, passive_ability_ids)
        ):
            raise RuntimeError("Hub combat-build configuration contains malformed arrays")
        schools.extend(str(value) for value in staff_school_ids)
        assignments.extend(
            {
                "combat_discipline_id": discipline_id,
                "action_slot": str(row_value(value, 0, "action_slot")),
                "ability_id": str(row_value(value, 1, "ability_id")),
            }
            for value in active_assignments
        )
        passives.extend(
            {"combat_discipline_id": discipline_id, "ability_id": str(value)}
            for value in passive_ability_ids
        )

    starting_discipline_id = str(
        option_value(row_value(row, 1, "starting_discipline_id"))
        or selected[0]["combat_discipline_id"]
    )
    configurations.sort(key=lambda value: value["combat_discipline_id"])
    schools.sort()
    assignments.sort(
        key=lambda value: (value["combat_discipline_id"], value["action_slot"])
    )
    passives.sort(
        key=lambda value: (value["combat_discipline_id"], value["ability_id"])
    )
    return {
        "owner": normalize_identity(row_value(row, 0, "owner")),
        "contract_schema_version": 1,
        "revision": int(row_value(row, 2, "revision")),
        "starting_discipline_id": starting_discipline_id,
        "selected_disciplines": selected,
        "discipline_configurations": configurations,
        "staff_school_ids": schools,
        "active_assignments": assignments,
        "passive_selections": passives,
    }


def require_single_inserted_row(
    update: dict[str, Any] | None,
    table_name: str,
) -> DatabaseRow:
    rows = inserted_rows(update, table_name)
    if len(rows) != 1:
        raise RuntimeError(
            f"initial subscription expected one {table_name} row, received {len(rows)}"
        )
    return rows[0]


def parse_applied_match_combat_build(
    rows_by_table: dict[str, list[DatabaseRow]],
) -> dict[str, Any] | None:
    singular_tables = (
        "match_combat_build",
        "active_armor_set",
        "player_equipment_presentation",
    )
    if any(len(rows_by_table.get(table_name, [])) != 1 for table_name in singular_tables):
        return None
    selected_rows = rows_by_table.get("match_combat_build_discipline", [])
    configuration_rows = rows_by_table.get("match_discipline_configuration", [])
    if not selected_rows or len(configuration_rows) != len(selected_rows):
        return None

    build = rows_by_table["match_combat_build"][0]
    armor = rows_by_table["active_armor_set"][0]
    equipment = rows_by_table["player_equipment_presentation"][0]
    selected_disciplines = [
        {
            "slot_index": int(row_value(row, 2, "slot_index")),
            "combat_discipline_id": str(row_value(row, 3, "combat_discipline_id")),
        }
        for row in selected_rows
    ]
    selected_disciplines.sort(key=lambda value: value["slot_index"])
    configurations = [
        {
            "combat_discipline_id": str(row_value(row, 2, "combat_discipline_id")),
            "main_hand_item_def_id": str(row_value(row, 3, "main_hand_item_def_id")),
            "main_hand_color_id": str(row_value(row, 4, "main_hand_color_id")),
            "off_hand_item_def_id": str(row_value(row, 5, "off_hand_item_def_id")),
            "off_hand_color_id": str(row_value(row, 6, "off_hand_color_id")),
            "main_hand_item_id": str(option_value(row_value(row, 7, "main_hand_item_id")) or ""),
            "off_hand_item_id": str(option_value(row_value(row, 8, "off_hand_item_id")) or ""),
        }
        for row in configuration_rows
    ]
    configurations.sort(key=lambda value: value["combat_discipline_id"])
    schools = sorted(
        str(row_value(row, 2, "spell_school_id"))
        for row in rows_by_table.get("match_staff_school_selection", [])
    )
    assignments = sorted(
        (
            {
                "combat_discipline_id": str(
                    row_value(row, 2, "combat_discipline_id")
                ),
                "action_slot": str(row_value(row, 3, "action_slot")),
                "ability_id": str(row_value(row, 4, "ability_id")),
            }
            for row in rows_by_table.get(
                "match_discipline_action_bar_assignment", []
            )
        ),
        key=lambda value: (value["combat_discipline_id"], value["action_slot"]),
    )
    passives = sorted(
        (
            {
                "combat_discipline_id": str(
                    row_value(row, 2, "combat_discipline_id")
                ),
                "ability_id": str(row_value(row, 3, "ability_id")),
            }
            for row in rows_by_table.get("match_discipline_passive_selection", [])
        ),
        key=lambda value: (value["combat_discipline_id"], value["ability_id"]),
    )
    canonical_owners = {normalize_identity(row_value(build, 0, "owner"))}
    for table_name in MATCH_COMBAT_BUILD_TABLES[1:]:
        canonical_owners.update(
            normalize_identity(row_value(row, 1, "owner"))
            for row in rows_by_table.get(table_name, [])
        )
    return {
        "build_owner": normalize_identity(row_value(build, 0, "owner")),
        "canonical_owners": canonical_owners,
        "contract_schema_version": int(
            row_value(build, 1, "contract_schema_version")
        ),
        "revision": int(row_value(build, 2, "revision")),
        "starting_discipline_id": str(
            row_value(build, 3, "starting_discipline_id")
        ),
        "selected_disciplines": selected_disciplines,
        "discipline_configurations": configurations,
        "staff_school_ids": schools,
        "active_assignments": assignments,
        "passive_selections": passives,
        "armor_owner": normalize_identity(row_value(armor, 0, "owner")),
        "armor_set_id": str(row_value(armor, 1, "armor_set_id")),
        "equipment_owner": normalize_identity(row_value(equipment, 0, "owner")),
        "equipped_main_hand_item_def_id": str(
            option_value(row_value(equipment, 8, "main_hand_item_def_id")) or ""
        ),
        "equipped_off_hand_item_def_id": str(
            option_value(row_value(equipment, 9, "off_hand_item_def_id")) or ""
        ),
        "equipped_main_hand_color_id": str(
            row_value(equipment, 12, "main_hand_color_id")
        ),
        "equipped_off_hand_color_id": str(
            row_value(equipment, 13, "off_hand_color_id")
        ),
    }


def percentile(values: list[float], quantile: float) -> float:
    """Nearest-rank percentile, suitable for the deliberately small sample set."""
    if not values:
        raise ValueError("cannot calculate a percentile without samples")
    ordered = sorted(values)
    rank = max(1, math.ceil(quantile * len(ordered)))
    return round(ordered[rank - 1], 3)


def summarize(samples: list[dict[str, Any]]) -> dict[str, Any]:
    metric_names = [
        "request_to_ready_ms",
        "ready_to_match_transport_ms",
        "match_transport_to_initial_state_ms",
        "ready_to_initial_state_ms",
        "request_to_initial_state_ms",
    ]
    summary: dict[str, Any] = {"sample_count": len(samples), "percentile": "nearest-rank"}
    for name in metric_names:
        values = [float(sample[name]) for sample in samples]
        summary[name] = {
            "min": round(min(values), 3),
            "p50": percentile(values, 0.50),
            "p95": percentile(values, 0.95),
            "max": round(max(values), 3),
            "mean": round(statistics.fmean(values), 3),
        }
    return summary


def pvp_initial_queries(identity: str) -> list[str]:
    identity_literal = f"0x{identity}"
    queries = [f'SELECT * FROM "{table}"' for table in PVP_STATIC_TABLES]
    for key in (
        "map_data/arena_map_01.layout.shared.json",
        "map_data/arena_map_01.collision.shared.json",
        "map_data/arena_map_01.query_collision.shared.json",
    ):
        queries.append(
            'SELECT * FROM "contract_version" '
            f'WHERE ("contract_version"."key" = \'{key}\')'
        )
    queries.append(
        'SELECT * FROM "player_world" '
        f'WHERE ("player_world"."identity" = {identity_literal})'
    )
    queries.append(
        'SELECT "arena_instance".* FROM "player_world" '
        'JOIN "arena_instance" ON "player_world"."instance_scope_id" = "arena_instance"."id" '
        f'WHERE ("player_world"."identity" = {identity_literal})'
    )
    for table, column in PVP_LOCAL_FILTERS:
        queries.append(
            f'SELECT * FROM "{table}" '
            f'WHERE ("{table}"."{column}" = {identity_literal})'
        )
    queries.append(
        'SELECT * FROM "item_instance" '
        f'WHERE ("item_instance"."current_owner_key" = \'{identity}\')'
    )
    queries.append(
        'SELECT "item_spell".* FROM "item_instance" '
        'JOIN "item_spell" ON "item_instance"."item_instance_id" = "item_spell"."item_instance_id" '
        f'WHERE ("item_instance"."current_owner_key" = \'{identity}\')'
    )
    queries.append(
        'SELECT "item_affix_instance".* FROM "item_instance" '
        'JOIN "item_affix_instance" ON '
        '"item_instance"."item_instance_id" = "item_affix_instance"."item_instance_id" '
        f'WHERE ("item_instance"."current_owner_key" = \'{identity}\')'
    )
    if len(queries) != 44:
        raise AssertionError(f"PvP initial query count changed: {len(queries)}")
    return queries


class Connection:
    def __init__(
        self,
        server_uri: str,
        database: str,
        timeout_seconds: float,
        token: str | None = None,
    ):
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
                tx = frame.get("TransactionUpdate")
                if isinstance(tx, dict):
                    status = tx.get("status", {})
                    if "Failed" in status:
                        reducer = tx.get("reducer_call", {}).get("reducer_name", "unknown")
                        raise RuntimeError(f"reducer {reducer} failed: {status['Failed']}")
                    if "OutOfEnergy" in status:
                        raise RuntimeError("reducer call ran out of energy")
                return frame

    def subscribe(self, queries: list[str]) -> int:
        self.request_id += 1
        self.ws.send(
            json.dumps(
                {"Subscribe": {"query_strings": queries, "request_id": self.request_id}}
            )
        )
        return self.request_id

    def call(self, reducer: str, args: list[Any]) -> int:
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
        return self.request_id

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

    def wait_for_initial_subscription(self, request_id: int) -> dict[str, Any]:
        def select(frame: dict[str, Any]) -> dict[str, Any] | None:
            initial = frame.get("InitialSubscription")
            if not isinstance(initial, dict):
                return None
            if int(initial.get("request_id", request_id)) != request_id:
                return None
            return initial

        return self.wait_for(select)

    def close(self) -> None:
        self.ws.close()


class Benchmark:
    def __init__(self, server_uri: str, hub_database: str, timeout_seconds: float):
        self.timeout_seconds = timeout_seconds
        self.hub = Connection(server_uri, hub_database, timeout_seconds)
        subscription = self.hub.subscribe(HUB_QUERIES)
        initial = self.hub.wait_for_initial_subscription(subscription)
        update = initial.get("database_update")
        self.hub_armor = parse_hub_armor(
            require_single_inserted_row(initial.get("database_update"), "my_hub_loadout")
        )
        self.hub_combat_build = parse_hub_combat_build(
            require_single_inserted_row(update, "my_combat_build")
        )
        if {
            self.hub_armor["owner"],
            self.hub_combat_build["owner"],
        } != {self.hub.identity}:
            raise RuntimeError("Hub build state does not belong to the authenticated identity")
        if not self.hub_armor["armor_set_id"]:
            raise RuntimeError("Hub armor selection is empty")
        if self.hub_combat_build["revision"] < 1:
            raise RuntimeError("Hub combat-build revision was not initialized")
        if len(self.hub_combat_build["selected_disciplines"]) != len(
            self.hub_combat_build["discipline_configurations"]
        ):
            raise RuntimeError("Hub combat build lacks a selected-discipline configuration")

    def wait_for_hub_status(self, expected: str) -> dict[str, str]:
        def select(frame: dict[str, Any]) -> dict[str, str] | None:
            rows = inserted_rows(database_update(frame), "my_match_status")
            for row in reversed(rows):
                status = parse_match_status(row)
                if status["status"] == expected:
                    return status
            return None

        return self.hub.wait_for(select)

    def sample(self, ordinal: int) -> dict[str, Any]:
        client_request_id = f"benchmark-{uuid.uuid4().hex}"
        request_started = time.perf_counter()
        self.hub.call("request_unranked_2_v_2_bot_match", [client_request_id])
        assignment = self.wait_for_hub_status("READY")
        ready_at = time.perf_counter()
        match: Connection | None = None
        try:
            if not all(
                assignment[key]
                for key in (
                    "ticket_id",
                    "match_id",
                    "server_uri",
                    "database_identity",
                    "match_build_id",
                    "map_id",
                )
            ):
                raise RuntimeError(f"Hub READY assignment is incomplete: {assignment}")
            match = Connection(
                assignment["server_uri"],
                assignment["database_identity"],
                self.timeout_seconds,
                token=self.hub.token,
            )
            transport_at = time.perf_counter()
            if match.identity != self.hub.identity:
                raise RuntimeError(
                    f"match identity {match.identity} does not match Hub identity {self.hub.identity}"
                )
            initial_request = match.subscribe(pvp_initial_queries(self.hub.identity))
            initial = match.wait_for_initial_subscription(initial_request)
            update = initial.get("database_update")
            tables = {
                table.get("table_name")
                for table in update.get("tables", [])
                if isinstance(table, dict)
            } if isinstance(update, dict) else set()
            missing = {"player_world", "arena_instance", "contract_version"} - tables
            if missing:
                raise RuntimeError(
                    "initial PvP subscription omitted required tables: " + ", ".join(sorted(missing))
                )

            identity_literal = f"0x{self.hub.identity}"
            audit_request = match.subscribe(
                [
                    f'SELECT * FROM "{table_name}" '
                    f'WHERE ("{table_name}"."owner" = {identity_literal})'
                    for table_name in MATCH_COMBAT_BUILD_TABLES
                ]
            )
            audit = match.wait_for_initial_subscription(audit_request)
            audit_update = audit.get("database_update")
            observed = {
                table_name: inserted_rows(audit_update, table_name)
                for table_name in MATCH_COMBAT_BUILD_TABLES
            }
            for table_name in ("active_armor_set", "player_equipment_presentation"):
                observed[table_name] = inserted_rows(update, table_name)
            applied = parse_applied_match_combat_build(observed)
            if applied is None:
                counts = {
                    table_name: len(rows) for table_name, rows in observed.items()
                }
                raise RuntimeError(
                    "match canonical combat build is incomplete; "
                    f"initial tables={sorted(tables)}; observed rows={counts}"
                )
            initial_at = time.perf_counter()
            if applied["canonical_owners"] != {self.hub.identity} or any(
                applied[field] != self.hub.identity
                for field in ("build_owner", "armor_owner", "equipment_owner")
            ):
                raise RuntimeError(
                    "match combat-build rows do not belong to the authenticated identity"
                )
            for field in (
                "contract_schema_version",
                "revision",
                "starting_discipline_id",
                "selected_disciplines",
                "staff_school_ids",
                "active_assignments",
                "passive_selections",
            ):
                if applied[field] != self.hub_combat_build[field]:
                    raise RuntimeError(
                        f"match combat-build {field} differs from the frozen Hub value"
                    )
            applied_configuration_contract = [
                {
                    key: value
                    for key, value in configuration.items()
                    if key not in {"main_hand_item_id", "off_hand_item_id"}
                }
                for configuration in applied["discipline_configurations"]
            ]
            if applied_configuration_contract != self.hub_combat_build[
                "discipline_configurations"
            ]:
                raise RuntimeError(
                    "match per-discipline weapon configurations differ from the frozen Hub build"
                )
            for configuration in applied["discipline_configurations"]:
                if not configuration["main_hand_item_id"]:
                    raise RuntimeError("match discipline weapon was not materialized")
                if bool(configuration["off_hand_item_id"]) != bool(
                    configuration["off_hand_item_def_id"]
                ):
                    raise RuntimeError("match off-hand materialization differs from its definition")
            if applied["armor_set_id"] != self.hub_armor["armor_set_id"]:
                raise RuntimeError("match armor differs from the frozen Hub selection")
            starting_configuration = next(
                configuration
                for configuration in self.hub_combat_build[
                    "discipline_configurations"
                ]
                if configuration["combat_discipline_id"]
                == self.hub_combat_build["starting_discipline_id"]
            )
            for match_field, expected_field in (
                ("equipped_main_hand_item_def_id", "main_hand_item_def_id"),
                ("equipped_off_hand_item_def_id", "off_hand_item_def_id"),
                ("equipped_main_hand_color_id", "main_hand_color_id"),
                ("equipped_off_hand_color_id", "off_hand_color_id"),
            ):
                expected_value = starting_configuration[expected_field]
                if expected_field == "main_hand_color_id":
                    expected_value = effective_weapon_color_id(
                        starting_configuration["main_hand_item_def_id"], expected_value
                    )
                elif expected_field == "off_hand_color_id":
                    expected_value = effective_weapon_color_id(
                        starting_configuration["off_hand_item_def_id"], expected_value
                    )
                if applied[match_field] != expected_value:
                    raise RuntimeError(
                        f"starting discipline equipment {match_field} differs from its configuration"
                    )

            result = {
                "sample": ordinal,
                "ticket": hashlib.sha256(assignment["ticket_id"].encode()).hexdigest()[:12],
                "match": assignment["match_id"],
                "match_build_id": assignment["match_build_id"],
                "map_id": assignment["map_id"],
                "contract_schema_version": self.hub_combat_build[
                    "contract_schema_version"
                ],
                "combat_build_revision": self.hub_combat_build["revision"],
                "starting_discipline_id": self.hub_combat_build[
                    "starting_discipline_id"
                ],
                "selected_disciplines": [
                    value["combat_discipline_id"]
                    for value in self.hub_combat_build["selected_disciplines"]
                ],
                "armor_set_id": self.hub_armor["armor_set_id"],
                "request_to_ready_ms": round((ready_at - request_started) * 1000.0, 3),
                "ready_to_match_transport_ms": round((transport_at - ready_at) * 1000.0, 3),
                "match_transport_to_initial_state_ms": round((initial_at - transport_at) * 1000.0, 3),
                "ready_to_initial_state_ms": round((initial_at - ready_at) * 1000.0, 3),
                "request_to_initial_state_ms": round((initial_at - request_started) * 1000.0, 3),
            }
            print(json.dumps({"event": "benchmark_sample", **result}, sort_keys=True), flush=True)
            return result
        finally:
            self.hub.call("cancel_match_ticket", [assignment["ticket_id"]])
            self.wait_for_hub_status("CLOSED")
            if match is not None:
                match.close()

    def close(self) -> None:
        self.hub.close()


def wait_for_cleanup(
    ledger_path: pathlib.Path,
    ticket_log_ids: set[str],
    timeout_seconds: float,
) -> dict[str, str]:
    deadline = time.monotonic() + timeout_seconds
    last: dict[str, str] = {}
    while time.monotonic() < deadline:
        if ledger_path.exists():
            with sqlite3.connect(ledger_path) as connection:
                rows = connection.execute("SELECT ticket_id, state FROM allocations").fetchall()
            last = {
                hashlib.sha256(str(ticket_id).encode()).hexdigest()[:12]: str(state)
                for ticket_id, state in rows
                if hashlib.sha256(str(ticket_id).encode()).hexdigest()[:12] in ticket_log_ids
            }
            if ticket_log_ids and set(last) == ticket_log_ids and set(last.values()) == {"CLEANED"}:
                return last
        time.sleep(0.25)
    unresolved = sorted(ticket_log_ids - {key for key, state in last.items() if state == "CLEANED"})
    raise RuntimeError(
        f"timed out waiting for exact-identity cleanup; unresolved ticket logs: {unresolved}"
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--samples", type=int, default=20)
    parser.add_argument("--server-uri", default="ws://127.0.0.1:3000")
    parser.add_argument("--hub-database", default="arena-hub-local")
    parser.add_argument("--timeout-seconds", type=float, default=15.0)
    parser.add_argument("--cleanup-timeout-seconds", type=float, default=40.0)
    parser.add_argument(
        "--ledger",
        type=pathlib.Path,
        default=pathlib.Path("Library/ArenaMatchProvisioner/state.sqlite3"),
    )
    args = parser.parse_args()
    if args.samples < 1:
        parser.error("--samples must be at least 1")
    return args


def main() -> int:
    args = parse_args()
    benchmark = Benchmark(args.server_uri, args.hub_database, args.timeout_seconds)
    samples: list[dict[str, Any]] = []
    try:
        for ordinal in range(1, args.samples + 1):
            samples.append(benchmark.sample(ordinal))
    finally:
        benchmark.close()

    cleanup = wait_for_cleanup(
        args.ledger,
        {str(sample["ticket"]) for sample in samples},
        args.cleanup_timeout_seconds,
    )
    print(
        json.dumps(
            {
                "event": "benchmark_summary",
                "cleanup": {"cleaned": len(cleanup), "expected": len(samples)},
                "metrics_ms": summarize(samples),
            },
            sort_keys=True,
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

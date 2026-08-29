#!/usr/bin/env python3
"""Canonical frozen-build setup shared by local combat acceptance probes.

The helper deliberately knows nothing about legacy learned-spell or mutable
match action-bar state. It derives discipline and Staff-school ownership from
the authored catalog, submits one complete draft to the feature-gated local
probe reducer, and waits until the ordinary frozen runtime rows are visible.
"""

from __future__ import annotations

import json
import pathlib
import time
from typing import Any, Iterable


ROOT = pathlib.Path(__file__).resolve().parents[1]
CATALOG_PATH = ROOT / "server/src/progression_catalog.shared.json"

WEAPONS = {
    "DAGGERS": {
        "main_hand_item_def_id": "TRAINING_DAGGER_PAIR",
        "main_hand_color_id": "",
        "off_hand_item_def_id": "",
        "off_hand_color_id": "",
    },
    "TWO_HANDED_SWORD": {
        "main_hand_item_def_id": "TRAINING_TWO_HAND_SWORD",
        "main_hand_color_id": "",
        "off_hand_item_def_id": "",
        "off_hand_color_id": "",
    },
    "SWORD_AND_SHIELD": {
        "main_hand_item_def_id": "TRAINING_ONE_HAND_SWORD",
        "main_hand_color_id": "",
        "off_hand_item_def_id": "TRAINING_SHIELD",
        "off_hand_color_id": "",
    },
    "ARCHER_BOW": {
        "main_hand_item_def_id": "TRAINING_BOW",
        "main_hand_color_id": "",
        "off_hand_item_def_id": "",
        "off_hand_color_id": "",
    },
    "STAFF": {
        "main_hand_item_def_id": "NEWBIE_STAFF_01",
        "main_hand_color_id": "",
        "off_hand_item_def_id": "",
        "off_hand_color_id": "",
    },
}


def normalize_identity(value: Any) -> str:
    if isinstance(value, dict):
        value = value.get("__identity__", value.get("identity", ""))
    if isinstance(value, list) and len(value) == 1:
        value = value[0]
    return str(value or "").removeprefix("0x").lower()


def _catalog() -> dict[str, Any]:
    return json.loads(CATALOG_PATH.read_text())


def build_probe_combat_draft(
    active_ability_ids: Iterable[str],
    passive_ability_ids: Iterable[str] = (),
    *,
    starting_discipline_id: str | None = None,
) -> dict[str, Any]:
    active_ids = [str(value).strip().upper() for value in active_ability_ids]
    passive_ids = [str(value).strip().upper() for value in passive_ability_ids]
    requested_ids = active_ids + passive_ids
    if not requested_ids:
        active_ids = ["DAGGER_QUICK_CUT"]
        requested_ids = active_ids.copy()
    if any(not value for value in requested_ids):
        raise ValueError("probe ability ids must be nonempty")
    if len(set(requested_ids)) != len(requested_ids):
        raise ValueError("probe combat build cannot select a duplicate ability")

    source = _catalog()
    rules = source["combat_build_contract"]["rules"]
    ability_rows = {row["ability_id"]: row for row in source["abilities"]}
    configurations: dict[str, dict[str, Any]] = {}
    selected_disciplines: list[str] = []

    for expected_kind, ability_ids in (("ACTIVE", active_ids), ("PASSIVE", passive_ids)):
        for ability_id in ability_ids:
            row = ability_rows.get(ability_id)
            if row is None or row.get("actor_scope") != "PLAYER":
                raise ValueError(f"unknown player ability {ability_id!r}")
            if row.get("selection_kind") != expected_kind:
                raise ValueError(
                    f"ability {ability_id!r} is {row.get('selection_kind')}, not {expected_kind}"
                )
            discipline_id = str(row.get("combat_discipline_id") or "")
            if discipline_id not in WEAPONS:
                raise ValueError(
                    f"ability {ability_id!r} has no canonical probe discipline"
                )
            if discipline_id not in configurations:
                selected_disciplines.append(discipline_id)
                configurations[discipline_id] = {
                    "combat_discipline_id": discipline_id,
                    "weapon": dict(WEAPONS[discipline_id]),
                    "staff_school_ids": [],
                    "active_assignments": [],
                    "passive_ability_ids": [],
                }
            configuration = configurations[discipline_id]
            school_id = row.get("spell_school_id")
            if school_id and school_id not in configuration["staff_school_ids"]:
                configuration["staff_school_ids"].append(school_id)
            if expected_kind == "ACTIVE":
                action_slot_ids = rules["action_slot_ids"]
                assignment_index = len(configuration["active_assignments"])
                if assignment_index >= len(action_slot_ids):
                    raise ValueError(f"discipline {discipline_id!r} exhausted action slots")
                configuration["active_assignments"].append(
                    {
                        "action_slot": action_slot_ids[assignment_index],
                        "ability_id": ability_id,
                    }
                )
            else:
                configuration["passive_ability_ids"].append(ability_id)

    maximum_disciplines = int(rules["maximum_selected_disciplines"])
    if len(selected_disciplines) > maximum_disciplines:
        raise ValueError(
            f"probe build selects {len(selected_disciplines)} disciplines; maximum is {maximum_disciplines}"
        )
    if len(active_ids) > int(rules["maximum_active_abilities"]):
        raise ValueError("probe build exceeds the canonical active-ability budget")
    if len(requested_ids) > int(rules["combined_ability_budget"]):
        raise ValueError("probe build exceeds the canonical combined-ability budget")

    starting = (starting_discipline_id or selected_disciplines[0]).strip().upper()
    if starting not in configurations:
        raise ValueError(f"starting discipline {starting!r} is not selected")

    return {
        "revision": 0,
        "starting_discipline_id": starting,
        "selected_disciplines": [
            {"slot_index": index, "combat_discipline_id": discipline_id}
            for index, discipline_id in enumerate(selected_disciplines)
        ],
        "discipline_configurations": [
            configurations[discipline_id] for discipline_id in selected_disciplines
        ],
    }


def configure_probe_combat_build(
    probe: Any,
    active_ability_ids: Iterable[str],
    passive_ability_ids: Iterable[str] = (),
    *,
    starting_discipline_id: str | None = None,
    timeout: float = 12.0,
) -> dict[str, Any]:
    draft = build_probe_combat_draft(
        active_ability_ids,
        passive_ability_ids,
        starting_discipline_id=starting_discipline_id,
    )
    expected_assignments = {
        (
            configuration["combat_discipline_id"],
            assignment["action_slot"],
            assignment["ability_id"],
        )
        for configuration in draft["discipline_configurations"]
        for assignment in configuration["active_assignments"]
    }
    expected_start = draft["starting_discipline_id"]
    identity = normalize_identity(probe.identity)
    probe.call(
        "configure_local_direct_probe_combat_build",
        [json.dumps(draft, separators=(",", ":"))],
    )

    deadline = time.time() + timeout
    while time.time() < deadline:
        root_rows = probe.sql(
            "SELECT owner, starting_discipline_id FROM match_combat_build"
        )
        assignment_rows = probe.sql(
            "SELECT owner, combat_discipline_id, action_slot, ability_id "
            "FROM match_discipline_action_bar_assignment"
        )
        active_rows = probe.sql(
            "SELECT owner, combat_discipline_id FROM active_combat_build_discipline"
        )
        roots = [
            str(start)
            for owner, start in root_rows
            if normalize_identity(owner) == identity
        ]
        assignments = {
            (str(discipline), str(slot), str(ability))
            for owner, discipline, slot, ability in assignment_rows
            if normalize_identity(owner) == identity
        }
        active = [
            str(discipline)
            for owner, discipline in active_rows
            if normalize_identity(owner) == identity
        ]
        if roots == [expected_start] and assignments == expected_assignments and active == [expected_start]:
            print(
                f"  [BUILD] {getattr(probe, 'name', identity[:8])}: "
                f"{expected_start}, {len(expected_assignments)} active assignments"
            )
            return draft
        time.sleep(0.05)

    dump_recent = getattr(probe, "dump_recent", None)
    if callable(dump_recent):
        dump_recent()
    raise RuntimeError(
        f"timed out waiting for canonical probe combat build for {identity[:8]}"
    )

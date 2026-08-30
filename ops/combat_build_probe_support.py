#!/usr/bin/env python3
"""Canonical Combat Build v2 setup shared by local acceptance probes.

The helper derives Form/School ownership from the checked-in v2 catalog,
submits one complete draft to the feature-gated local probe reducer, and waits
until the ordinary selected-only v2 runtime rows are visible.
"""

from __future__ import annotations

import json
import pathlib
import time
from typing import Any, Iterable


ROOT = pathlib.Path(__file__).resolve().parents[1]
CATALOG_PATH = ROOT / "server/src/progression_catalog.shared.json"
V2_CATALOG_PATH = ROOT / "server/src/combat_build_v2_catalog.shared.json"

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


def _catalogs() -> tuple[dict[str, Any], dict[str, Any]]:
    return json.loads(CATALOG_PATH.read_text()), json.loads(V2_CATALOG_PATH.read_text())


def _feature_index(v2_catalog: dict[str, Any]) -> dict[str, dict[str, str]]:
    features: dict[str, dict[str, str]] = {}
    for specialization in v2_catalog["specializations"]:
        specialization_id = specialization["specialization_id"]
        parent_id = specialization["combat_discipline_id"]
        for field, loadout_kind in (
            ("technique_ability_ids", "TECHNIQUE"),
            ("spell_ability_ids", "SPELL"),
            ("perk_ability_ids", "PERK"),
        ):
            for ability_id in specialization[field]:
                if ability_id in features:
                    raise ValueError(f"duplicate v2 feature mapping for {ability_id!r}")
                features[ability_id] = {
                    "specialization_id": specialization_id,
                    "combat_discipline_id": parent_id,
                    "loadout_kind": loadout_kind,
                }
    return features


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

    source, v2_catalog = _catalogs()
    rules = v2_catalog["rules"]
    ability_rows = {row["ability_id"]: row for row in source["abilities"]}
    feature_rows = _feature_index(v2_catalog)
    configurations: dict[str, dict[str, Any]] = {}
    selected_specializations: list[str] = []
    selected_features: list[dict[str, Any]] = []
    technique_orders: dict[str, int] = {}
    spell_order = 0

    for expected_kinds, ability_ids in (
        (("TECHNIQUE", "SPELL"), active_ids),
        (("PERK",), passive_ids),
    ):
        for ability_id in ability_ids:
            row = ability_rows.get(ability_id)
            if row is None or row.get("actor_scope") != "PLAYER":
                raise ValueError(f"unknown player ability {ability_id!r}")
            feature = feature_rows.get(ability_id)
            if feature is None:
                raise ValueError(
                    f"ability {ability_id!r} is not a selectable v2 Combat Feature"
                )
            loadout_kind = feature["loadout_kind"]
            if loadout_kind not in expected_kinds:
                raise ValueError(
                    f"ability {ability_id!r} is {loadout_kind}, not one of {expected_kinds}"
                )
            discipline_id = feature["combat_discipline_id"]
            specialization_id = feature["specialization_id"]
            if discipline_id not in WEAPONS:
                raise ValueError(
                    f"ability {ability_id!r} has no canonical probe discipline"
                )
            if specialization_id not in selected_specializations:
                selected_specializations.append(specialization_id)
            if discipline_id not in configurations:
                configurations[discipline_id] = {
                    "combat_discipline_id": discipline_id,
                    **WEAPONS[discipline_id],
                }
            if loadout_kind == "TECHNIQUE":
                preferred_bar_order = technique_orders.get(discipline_id, 0)
                technique_orders[discipline_id] = preferred_bar_order + 1
            elif loadout_kind == "SPELL":
                preferred_bar_order = spell_order
                spell_order += 1
            else:
                preferred_bar_order = None
            selected_features.append(
                {
                    "specialization_id": specialization_id,
                    "ability_id": ability_id,
                    "preferred_bar_order": preferred_bar_order,
                }
            )

    maximum_specializations = int(rules["maximum_selected_specializations"])
    if len(selected_specializations) > maximum_specializations:
        raise ValueError(
            f"probe build selects {len(selected_specializations)} specializations; "
            f"maximum is {maximum_specializations}"
        )
    if len(requested_ids) > int(rules["global_feature_capacity"]):
        raise ValueError("probe build exceeds the global Combat Feature capacity")

    selected_disciplines = list(configurations)
    starting = (starting_discipline_id or selected_disciplines[0]).strip().upper()
    if starting not in configurations:
        raise ValueError(f"starting discipline {starting!r} is not selected")

    return {
        "schema_version": int(v2_catalog["schema_version"]),
        "revision": 0,
        "starting_discipline_id": starting,
        "selected_specializations": [
            {"slot_index": index, "specialization_id": specialization_id}
            for index, specialization_id in enumerate(selected_specializations)
        ],
        "dormant_specializations": [],
        "discipline_configurations": list(configurations.values()),
        "selected_features": selected_features,
        "selected_traits": [],
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
    _, v2_catalog = _catalogs()
    feature_rows = _feature_index(v2_catalog)
    expected_specializations = {
        (row["slot_index"], row["specialization_id"])
        for row in draft["selected_specializations"]
    }
    expected_features = {"TECHNIQUE": set(), "SPELL": set(), "PERK": set()}
    for row in draft["selected_features"]:
        feature = feature_rows[row["ability_id"]]
        expected_features[feature["loadout_kind"]].add(
            (
                row["specialization_id"],
                feature["combat_discipline_id"],
                row["ability_id"],
                row["preferred_bar_order"],
            )
        )
    expected_start = draft["starting_discipline_id"]
    identity = normalize_identity(probe.identity)
    probe.call(
        "configure_local_direct_probe_combat_build",
        [json.dumps(draft, separators=(",", ":"))],
    )

    deadline = time.time() + timeout
    while time.time() < deadline:
        root_rows = probe.sql(
            "SELECT owner, starting_discipline_id FROM match_combat_build_v_2"
        )
        specialization_rows = probe.sql(
            "SELECT owner, slot_index, specialization_id "
            "FROM match_selected_specialization_v_2"
        )
        technique_rows = probe.sql(
            "SELECT owner, specialization_id, combat_discipline_id, ability_id, bar_order "
            "FROM match_technique_selection_v_2"
        )
        spell_rows = probe.sql(
            "SELECT owner, specialization_id, combat_discipline_id, ability_id, bar_order "
            "FROM match_spell_selection_v_2"
        )
        perk_rows = probe.sql(
            "SELECT owner, specialization_id, combat_discipline_id, ability_id "
            "FROM match_perk_selection_v_2"
        )
        active_rows = probe.sql(
            "SELECT owner, combat_discipline_id FROM active_combat_build_discipline"
        )
        roots = [
            str(start)
            for owner, start in root_rows
            if normalize_identity(owner) == identity
        ]
        specializations = {
            (int(slot), str(specialization))
            for owner, slot, specialization in specialization_rows
            if normalize_identity(owner) == identity
        }
        features = {
            "TECHNIQUE": {
                (str(specialization), str(discipline), str(ability), int(order))
                for owner, specialization, discipline, ability, order in technique_rows
                if normalize_identity(owner) == identity
            },
            "SPELL": {
                (str(specialization), str(discipline), str(ability), int(order))
                for owner, specialization, discipline, ability, order in spell_rows
                if normalize_identity(owner) == identity
            },
            "PERK": {
                (str(specialization), str(discipline), str(ability), None)
                for owner, specialization, discipline, ability in perk_rows
                if normalize_identity(owner) == identity
            },
        }
        active = [
            str(discipline)
            for owner, discipline in active_rows
            if normalize_identity(owner) == identity
        ]
        if (
            roots == [expected_start]
            and specializations == expected_specializations
            and features == expected_features
            and active == [expected_start]
        ):
            print(
                f"  [BUILD] {getattr(probe, 'name', identity[:8])}: "
                f"{expected_start}, {len(expected_specializations)} specializations, "
                f"{len(draft['selected_features'])} features"
            )
            return draft
        time.sleep(0.05)

    dump_recent = getattr(probe, "dump_recent", None)
    if callable(dump_recent):
        dump_recent()
    raise RuntimeError(
        f"timed out waiting for canonical probe combat build for {identity[:8]}"
    )

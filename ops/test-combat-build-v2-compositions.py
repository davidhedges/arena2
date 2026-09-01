#!/usr/bin/env python3
"""Probe the remaining Combat Build v2 composition gates in real local matches.

The first disposable match freezes three Schools with exactly 18 Spells and
active Mastery. After its ticket is created, the Hub build is changed to three
Dagger Forms; the first match must remain unchanged. A second disposable match
then proves those three Forms share one Dagger configuration and one switch
target. The probe also verifies a nineteenth feature is rejected atomically.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import runpy
import uuid
from typing import Any


RUNTIME = runpy.run_path(
    str(pathlib.Path(__file__).with_name("test-combat-build-runtime.py"))
)
Connection = RUNTIME["Connection"]
inserted_rows = RUNTIME["inserted_rows"]
normalize_identity = RUNTIME["normalize_identity"]
option_value = RUNTIME["option_value"]
require_status = RUNTIME["require_status"]
row_value = RUNTIME["row_value"]
snapshot = RUNTIME["snapshot"]
wait_for_cleanup = RUNTIME["wait_for_cleanup"]
wait_for_hub_status = RUNTIME["wait_for_hub_status"]

SCHOOL_SPECIALIZATIONS = ["RUIN", "ARCANA", "BLIGHT"]
SCHOOL_FEATURES = [
    ("RUIN", ability_id)
    for ability_id in (
        "SPELL_FIREBALL",
        "SPELL_ORBITING_BLADES",
        "SPELL_METEOR",
        "SPELL_ELECTROCUTE",
        "SPELL_LIGHTNING",
        "SPELL_CAPACITOR",
        "SPELL_ERUPTION",
        "SPELL_FLAMETHROWER",
        "SPELL_FLAMING_ORB",
        "SPELL_BOLT",
        "SPELL_FIERY_ORBS",
        "SPELL_CAUTERIZE",
        "SPELL_FLASHFIRE",
        "SPELL_IMMOLATION",
        "SPELL_COMBUSTION",
        "SPELL_FULMINATION",
    )
]
SCHOOL_FEATURES.extend(
    [
        ("ARCANA", "SPELL_MANA_SHIELD"),
        ("BLIGHT", "SPELL_ICICLE"),
    ]
)
DAGGER_SPECIALIZATIONS = [
    "DAGGERS_BLADEDANCER",
    "DAGGERS_EXECUTIONER",
    "DAGGERS_SHADOW",
]
DAGGER_FEATURES = [
    ("DAGGERS_BLADEDANCER", "DAGGER_DISARM"),
    ("DAGGERS_EXECUTIONER", "DAGGER_FIND_WEAKNESS"),
    ("DAGGERS_EXECUTIONER", "DAGGER_GOUGE"),
    ("DAGGERS_EXECUTIONER", "DAGGER_TEMPLE_STRIKE"),
    ("DAGGERS_SHADOW", "DAGGER_COUP_DE_GRACE"),
]
REMOVED_STAFF_TECHNIQUES = {
    "STAFF_STRIKE",
    "STAFF_STRIKE_2",
    "STAFF_SWEEP",
    "STAFF_THRUST",
}


def configuration(parent: str, item_def_id: str) -> dict[str, str]:
    return {
        "combat_discipline_id": parent,
        "main_hand_item_def_id": item_def_id,
        "main_hand_color_id": "",
        "off_hand_item_def_id": "",
        "off_hand_color_id": "",
    }


def draft(
    revision: int,
    starting_parent: str,
    specializations: list[str],
    configurations: list[dict[str, str]],
    features: list[tuple[str, str]],
) -> dict[str, Any]:
    return {
        "schema_version": 2,
        "revision": revision,
        "starting_discipline_id": [0, starting_parent],
        "selected_specializations": [
            {"slot_index": index, "specialization_id": specialization_id}
            for index, specialization_id in enumerate(specializations)
        ],
        "dormant_specializations": [],
        "discipline_configurations": configurations,
        "selected_features": [
            {
                "specialization_id": specialization_id,
                "ability_id": ability_id,
                "preferred_bar_order": [0, order],
            }
            for order, (specialization_id, ability_id) in enumerate(features)
        ],
        "selected_traits": ["MASTERY"],
    }


def build_row(connection: Any) -> Any:
    update = connection.subscribe(
        [
            'SELECT * FROM "my_combat_build_v_2"',
            'SELECT * FROM "my_match_status"',
        ]
    )
    rows = inserted_rows(update, "my_combat_build_v_2")
    if len(rows) != 1:
        raise RuntimeError(f"expected one v2 Hub aggregate, received {len(rows)}")
    return rows[0]


def start_match(hub: Any) -> tuple[dict[str, str], Any]:
    request_id = f"combat-build-v2-composition-{uuid.uuid4().hex}"
    require_status(
        hub.call("request_unranked_2_v_2_bot_match", [request_id]), "committed"
    )
    assignment = wait_for_hub_status(hub, "READY")
    match = Connection(
        assignment["server_uri"], assignment["database_identity"], 20.0, hub.token
    )
    if match.identity != hub.identity:
        raise RuntimeError("Hub and match identities differ")
    return assignment, match


def close_match(
    hub: Any,
    match: Any,
    assignment: dict[str, str],
    ledger: pathlib.Path,
    cleanup_timeout_seconds: float,
) -> str:
    match.close()
    require_status(
        hub.call("cancel_match_ticket", [assignment["ticket_id"]]), "committed"
    )
    return wait_for_cleanup(
        ledger, assignment["ticket_id"], cleanup_timeout_seconds
    )


def selected_rows(rows: dict[str, list[Any]]) -> list[tuple[int, str, str, str]]:
    return sorted(
        (
            int(row_value(row, 2, "slot_index")),
            str(row_value(row, 3, "specialization_id")),
            str(row_value(row, 4, "combat_discipline_id")),
            str(row_value(row, 5, "specialization_kind")),
        )
        for row in rows["match_selected_specialization_v_2"]
    )


def require_mastery(rows: dict[str, list[Any]]) -> None:
    roots = rows["match_combat_build_v_2"]
    traits = rows["match_trait_selection_v_2"]
    if len(roots) != 1 or not bool(row_value(roots[0], 4, "mastery_active")):
        raise RuntimeError("single-parent build did not activate Mastery")
    if [str(row_value(row, 2, "ability_id")) for row in traits] != ["MASTERY"]:
        raise RuntimeError("single-parent build did not materialize exactly MASTERY")


def validate_three_schools(rows: dict[str, list[Any]]) -> dict[str, Any]:
    expected = [
        (index, specialization_id, "STAFF", "SCHOOL")
        for index, specialization_id in enumerate(SCHOOL_SPECIALIZATIONS)
    ]
    if selected_rows(rows) != expected:
        raise RuntimeError("three-School selected order or parent projection differs")
    configurations = rows["match_discipline_configuration_v_2"]
    if len(configurations) != 1 or row_value(
        configurations[0], 2, "combat_discipline_id"
    ) != "STAFF":
        raise RuntimeError("three Schools did not derive exactly one Staff configuration")
    if rows["match_technique_selection_v_2"]:
        raise RuntimeError("three Schools materialized a Staff Technique")
    spells = rows["match_spell_selection_v_2"]
    if len(spells) != 18:
        raise RuntimeError(f"18-feature School build materialized {len(spells)} Spells")
    orders = sorted(int(row_value(row, 5, "bar_order")) for row in spells)
    if orders != list(range(18)):
        raise RuntimeError(f"18-feature Spell ordering is not fully reachable: {orders}")
    ability_ids = {str(row_value(row, 4, "ability_id")) for row in spells}
    if ability_ids & REMOVED_STAFF_TECHNIQUES:
        raise RuntimeError("removed Staff melee abilities entered v2 selection")
    require_mastery(rows)
    return {
        "specializations": SCHOOL_SPECIALIZATIONS,
        "derived_disciplines": ["STAFF"],
        "spell_count": len(spells),
        "technique_count": 0,
        "mastery_active": True,
    }


def validate_three_dagger_forms(rows: dict[str, list[Any]]) -> dict[str, Any]:
    expected = [
        (index, specialization_id, "DAGGERS", "FORM")
        for index, specialization_id in enumerate(DAGGER_SPECIALIZATIONS)
    ]
    if selected_rows(rows) != expected:
        raise RuntimeError("three-Dagger-Form selected order or parent projection differs")
    configurations = rows["match_discipline_configuration_v_2"]
    if len(configurations) != 1 or row_value(
        configurations[0], 2, "combat_discipline_id"
    ) != "DAGGERS":
        raise RuntimeError("three Dagger Forms did not share one Dagger configuration")
    active = rows["active_combat_build_discipline"]
    if len(active) != 1 or row_value(active[0], 1, "combat_discipline_id") != "DAGGERS":
        raise RuntimeError("three Dagger Forms did not derive one active switch target")
    if len(rows["player_equipment_presentation"]) != 1:
        raise RuntimeError("three Dagger Forms duplicated equipment presentation")
    techniques = rows["match_technique_selection_v_2"]
    spells = rows["match_spell_selection_v_2"]
    expected_features = set(DAGGER_FEATURES)
    observed_features = {
        (
            str(row_value(row, 2, "specialization_id")),
            str(row_value(row, 4, "ability_id")),
        )
        for row in techniques
    }
    if observed_features != expected_features or spells:
        raise RuntimeError(
            "three Dagger Forms did not preserve the requested moved-ability ownership"
        )
    require_mastery(rows)
    return {
        "specializations": DAGGER_SPECIALIZATIONS,
        "derived_disciplines": ["DAGGERS"],
        "technique_count": len(techniques),
        "spell_count": len(spells),
        "mastery_active": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-uri", default="ws://127.0.0.1:3000")
    parser.add_argument("--hub-database", default="arena-hub-local")
    parser.add_argument("--cleanup-timeout-seconds", type=float, default=45.0)
    parser.add_argument(
        "--ledger",
        type=pathlib.Path,
        default=pathlib.Path("Library/ArenaMatchProvisioner/state.sqlite3"),
    )
    args = parser.parse_args()

    hub = Connection(args.server_uri, args.hub_database, 20.0)
    open_matches: list[tuple[dict[str, str], Any]] = []
    results: dict[str, Any] = {}
    try:
        initial = build_row(hub)
        revision = int(row_value(initial, 2, "revision"))
        school_draft = draft(
            revision,
            "STAFF",
            SCHOOL_SPECIALIZATIONS,
            [configuration("STAFF", "NEWBIE_STAFF_01")],
            SCHOOL_FEATURES,
        )
        over_capacity = dict(school_draft)
        over_capacity["selected_features"] = [
            *school_draft["selected_features"],
            {
                "specialization_id": "BLIGHT",
                "ability_id": "SPELL_FROZEN_SPLINTERS",
                "preferred_bar_order": [0, 18],
            },
        ]
        detail = require_status(
            hub.call("save_combat_build_v_2", [over_capacity]), "failed"
        )
        if "COMBAT_BUILD_V2_FEATURE_CAPACITY" not in detail:
            raise RuntimeError(f"nineteenth feature failed for the wrong reason: {detail}")

        require_status(
            hub.call("save_combat_build_v_2", [school_draft]), "committed"
        )
        school_revision = revision + 1
        school_assignment, school_match = start_match(hub)
        open_matches.append((school_assignment, school_match))

        dagger_draft = draft(
            school_revision,
            "DAGGERS",
            DAGGER_SPECIALIZATIONS,
            [configuration("DAGGERS", "TRAINING_DAGGER_PAIR")],
            DAGGER_FEATURES,
        )
        require_status(
            hub.call("save_combat_build_v_2", [dagger_draft]), "committed"
        )
        school_rows = snapshot(school_match, hub.identity)
        results["three_schools"] = validate_three_schools(school_rows)
        results["freeze_isolation"] = "HUB_CHANGED_MATCH_UNCHANGED"
        results["three_schools"]["ticket"] = close_match(
            hub,
            school_match,
            school_assignment,
            args.ledger,
            args.cleanup_timeout_seconds,
        )
        open_matches.clear()

        hub_identity = hub.identity
        hub_token = hub.token
        hub.close()
        hub = Connection(
            args.server_uri,
            args.hub_database,
            20.0,
            token=hub_token,
        )
        if hub.identity != hub_identity:
            raise RuntimeError("Hub identity changed while reconnecting between matches")
        build_row(hub)

        dagger_assignment, dagger_match = start_match(hub)
        open_matches.append((dagger_assignment, dagger_match))
        dagger_rows = snapshot(dagger_match, hub.identity)
        results["three_dagger_forms"] = validate_three_dagger_forms(dagger_rows)
        results["three_dagger_forms"]["ticket"] = close_match(
            hub,
            dagger_match,
            dagger_assignment,
            args.ledger,
            args.cleanup_timeout_seconds,
        )
        open_matches.clear()
    finally:
        for assignment, match in open_matches:
            try:
                match.close()
            except Exception:
                pass
            try:
                status, _, _ = hub.call(
                    "cancel_match_ticket", [assignment["ticket_id"]]
                )
                if status == "committed":
                    wait_for_hub_status(hub, "CLOSED")
            except Exception:
                pass
        hub.close()

    results["nineteenth_feature"] = "REJECTED_AT_18_CAPACITY"
    results["event"] = "combat_build_v2_compositions_phase_7_pass"
    results["identity"] = hashlib.sha256(hub.identity.encode()).hexdigest()[:12]
    print(json.dumps(results, sort_keys=True), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

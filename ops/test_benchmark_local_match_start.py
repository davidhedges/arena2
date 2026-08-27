#!/usr/bin/env python3
"""Regression tests for the local match benchmark's SpacetimeDB row decoding."""

from __future__ import annotations

import json
import runpy
import unittest
from pathlib import Path
from typing import Any


BENCHMARK = runpy.run_path(Path(__file__).with_name("benchmark-local-match-start.py"))


class BenchmarkRowDecodingTests(unittest.TestCase):
    def test_empty_weapon_color_resolves_to_authored_default(self) -> None:
        effective_color = BENCHMARK["effective_weapon_color_id"]
        self.assertEqual(
            effective_color("TRAINING_DAGGER_PAIR", ""),
            "DEFAULT",
        )
        self.assertEqual(
            effective_color("TRAINING_DAGGER_PAIR", "default"),
            "DEFAULT",
        )
        self.assertEqual(effective_color("", ""), "")

    def test_option_none_accepts_both_protocol_encodings(self) -> None:
        option_value = BENCHMARK["option_value"]
        self.assertIsNone(option_value([1]))
        self.assertIsNone(option_value([1, {}]))
        self.assertEqual(option_value([0, "ITEM"]), "ITEM")

    def test_inserted_rows_accepts_view_arrays_and_table_objects(self) -> None:
        inserted_rows = BENCHMARK["inserted_rows"]
        update = {
            "tables": [
                {
                    "table_name": "my_view",
                    "updates": [{"inserts": [json.dumps(["value", 1])]}],
                },
                {
                    "table_name": "ordinary_table",
                    "updates": [{"inserts": [json.dumps({"key": "value"})]}],
                },
            ]
        }
        self.assertEqual(inserted_rows(update, "my_view"), [["value", 1]])
        self.assertEqual(inserted_rows(update, "ordinary_table"), [{"key": "value"}])

    def test_hub_combat_build_decodes_selected_configuration(self) -> None:
        parse_hub = BENCHMARK["parse_hub_combat_build"]
        owner = {"__identity__": "0xabc"}
        build = parse_hub(
            {
                "owner": owner,
                "starting_discipline_id": [1, {}],
                "revision": 4,
                "selected_disciplines": [
                    {"slot_index": 0, "combat_discipline_id": "DAGGERS"}
                ],
                "discipline_configurations": [
                    {
                        "combat_discipline_id": "DAGGERS",
                        "weapon": {
                            "main_hand_item_def_id": "TRAINING_DAGGER_PAIR",
                            "main_hand_color_id": "",
                            "off_hand_item_def_id": "",
                            "off_hand_color_id": "",
                        },
                        "staff_school_ids": [],
                        "active_assignments": [
                            {
                                "action_slot": "slot_0_0",
                                "ability_id": "DAGGER_QUICK_CUT",
                            }
                        ],
                        "passive_ability_ids": [],
                    }
                ],
            }
        )

        self.assertEqual(build["owner"], "abc")
        self.assertEqual(build["starting_discipline_id"], "DAGGERS")
        self.assertEqual(build["revision"], 4)
        self.assertEqual(
            build["active_assignments"],
            [
                {
                    "combat_discipline_id": "DAGGERS",
                    "action_slot": "slot_0_0",
                    "ability_id": "DAGGER_QUICK_CUT",
                }
            ],
        )

    def test_applied_match_combat_build_decodes_object_rows(self) -> None:
        parse_applied = BENCHMARK["parse_applied_match_combat_build"]
        owner = {"__identity__": "0xabc"}
        rows: dict[str, list[dict[str, Any]]] = {
            "match_combat_build": [
                {
                    "owner": owner,
                    "contract_schema_version": 1,
                    "revision": 4,
                    "starting_discipline_id": "DAGGERS",
                }
            ],
            "match_combat_build_discipline": [
                {
                    "key": "abc:0",
                    "owner": owner,
                    "slot_index": 0,
                    "combat_discipline_id": "DAGGERS",
                }
            ],
            "match_discipline_configuration": [
                {
                    "key": "abc:DAGGERS",
                    "owner": owner,
                    "combat_discipline_id": "DAGGERS",
                    "main_hand_item_def_id": "TRAINING_DAGGER_PAIR",
                    "main_hand_color_id": "",
                    "off_hand_item_def_id": "",
                    "off_hand_color_id": "",
                    "main_hand_item_id": [0, "starter-daggers"],
                    "off_hand_item_id": [1, {}],
                }
            ],
            "match_staff_school_selection": [],
            "match_discipline_action_bar_assignment": [
                {
                    "key": "abc:DAGGERS:slot_0_0",
                    "owner": owner,
                    "combat_discipline_id": "DAGGERS",
                    "action_slot": "slot_0_0",
                    "ability_id": "DAGGER_QUICK_CUT",
                }
            ],
            "match_discipline_passive_selection": [],
            "active_armor_set": [{"owner": owner, "armor_set_id": "PEASANT"}],
            "player_equipment_presentation": [
                {
                    "owner": owner,
                    "main_hand_item_def_id": [0, "TRAINING_DAGGER_PAIR"],
                    "off_hand_item_def_id": [1, {}],
                    "main_hand_color_id": "",
                    "off_hand_color_id": "",
                }
            ],
        }
        applied = parse_applied(rows)
        self.assertIsNotNone(applied)
        assert applied is not None
        self.assertEqual(applied["build_owner"], "abc")
        self.assertEqual(applied["canonical_owners"], {"abc"})
        self.assertEqual(
            applied["equipped_main_hand_item_def_id"], "TRAINING_DAGGER_PAIR"
        )
        self.assertEqual(applied["equipped_off_hand_item_def_id"], "")
        self.assertEqual(
            applied["active_assignments"],
            [
                {
                    "combat_discipline_id": "DAGGERS",
                    "action_slot": "slot_0_0",
                    "ability_id": "DAGGER_QUICK_CUT",
                }
            ],
        )


if __name__ == "__main__":
    unittest.main()

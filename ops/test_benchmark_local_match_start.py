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

    def test_hub_combat_build_decodes_v2_selected_configuration(self) -> None:
        parse_hub = BENCHMARK["parse_hub_combat_build"]
        owner = {"__identity__": "0xabc"}
        build = parse_hub(
            {
                "owner": owner,
                "schema_version": 2,
                "revision": 4,
                "starting_discipline_id": [1, {}],
                "selected_specializations": [
                    {"slot_index": 0, "specialization_id": "DAGGERS_BLADEDANCER"}
                ],
                "dormant_specializations": [],
                "discipline_configurations": [
                    {
                        "combat_discipline_id": "DAGGERS",
                        "main_hand_item_def_id": "TRAINING_DAGGER_PAIR",
                        "main_hand_color_id": "",
                        "off_hand_item_def_id": "",
                        "off_hand_color_id": "",
                    }
                ],
                "selected_features": [
                    {
                        "specialization_id": "DAGGERS_BLADEDANCER",
                        "ability_id": "DAGGER_QUICK_CUT",
                        "preferred_bar_order": [0, 0],
                    }
                ],
                "selected_traits": ["MASTERY"],
            }
        )

        self.assertEqual(build["owner"], "abc")
        self.assertEqual(build["starting_discipline_id"], "DAGGERS")
        self.assertEqual(build["revision"], 4)
        self.assertEqual(
            build["selected_features"],
            [
                {
                    "specialization_id": "DAGGERS_BLADEDANCER",
                    "ability_id": "DAGGER_QUICK_CUT",
                    "preferred_bar_order": 0,
                }
            ],
        )
        self.assertEqual(build["selected_traits"], ["MASTERY"])

    def test_applied_match_combat_build_decodes_object_rows(self) -> None:
        parse_applied = BENCHMARK["parse_applied_match_combat_build"]
        owner = {"__identity__": "0xabc"}
        rows: dict[str, list[dict[str, Any]]] = {
            "match_combat_build_v_2": [
                {
                    "owner": owner,
                    "contract_schema_version": 2,
                    "revision": 4,
                    "starting_discipline_id": "DAGGERS",
                    "mastery_active": True,
                }
            ],
            "match_selected_specialization_v_2": [
                {
                    "key": "abc:0",
                    "owner": owner,
                    "slot_index": 0,
                    "specialization_id": "DAGGERS_BLADEDANCER",
                    "combat_discipline_id": "DAGGERS",
                    "specialization_kind": "FORM",
                }
            ],
            "match_discipline_configuration_v_2": [
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
            "match_technique_selection_v_2": [
                {
                    "key": "abc:DAGGER_QUICK_CUT",
                    "owner": owner,
                    "specialization_id": "DAGGERS_BLADEDANCER",
                    "combat_discipline_id": "DAGGERS",
                    "ability_id": "DAGGER_QUICK_CUT",
                    "bar_order": 0,
                }
            ],
            "match_spell_selection_v_2": [],
            "match_perk_selection_v_2": [],
            "match_trait_selection_v_2": [
                {"key": "abc:MASTERY", "owner": owner, "ability_id": "MASTERY"}
            ],
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
            applied["selected_features"],
            [
                {
                    "specialization_id": "DAGGERS_BLADEDANCER",
                    "ability_id": "DAGGER_QUICK_CUT",
                    "preferred_bar_order": 0,
                }
            ],
        )
        self.assertTrue(applied["mastery_active"])
        self.assertEqual(applied["selected_traits"], ["MASTERY"])


if __name__ == "__main__":
    unittest.main()

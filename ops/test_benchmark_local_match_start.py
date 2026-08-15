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

    def test_applied_match_loadout_decodes_object_rows(self) -> None:
        parse_applied = BENCHMARK["parse_applied_match_loadout"]
        owner = {"__identity__": "0xabc"}
        rows: dict[str, list[dict[str, Any]]] = {
            "character_discipline_loadout": [
                {
                    "owner": owner,
                    "primary_discipline_id": "WAR",
                    "secondary_discipline_id_1": "SUBTLETY",
                    "secondary_discipline_id_2": "RUIN",
                }
            ],
            "character_discipline_ability_selection": [
                {"owner": owner, "ability_id": "WARRIOR_HEW"},
                {"owner": owner, "ability_id": "SPELL_FIREBALL"},
            ],
            "active_armor_set": [{"owner": owner, "armor_set_id": "PEASANT"}],
            "player_equipment_presentation": [
                {
                    "owner": owner,
                    "main_hand_item_def_id": [0, "TRAINING_TWO_HAND_SWORD"],
                    "off_hand_item_def_id": [1, {}],
                    "main_hand_color_id": "DEFAULT",
                    "off_hand_color_id": "",
                }
            ],
        }
        applied = parse_applied(rows)
        self.assertIsNotNone(applied)
        assert applied is not None
        self.assertEqual(applied["discipline_owner"], "abc")
        self.assertEqual(applied["main_hand_item_def_id"], "TRAINING_TWO_HAND_SWORD")
        self.assertEqual(applied["off_hand_item_def_id"], "")
        self.assertEqual(
            applied["selected_ability_ids"],
            {"WARRIOR_HEW", "SPELL_FIREBALL"},
        )


if __name__ == "__main__":
    unittest.main()

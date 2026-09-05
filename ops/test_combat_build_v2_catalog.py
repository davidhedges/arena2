"""Regression checks for the reviewed Combat Build v2 generation boundary."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "combat_build_v2_generator",
    Path(__file__).with_name("generate-combat-build-v2-catalog.py"),
)
assert SPEC is not None and SPEC.loader is not None
GENERATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GENERATOR)


class CombatBuildV2CatalogTests(unittest.TestCase):
    def setUp(self) -> None:
        self.contract = json.loads(GENERATOR.DEFAULT_CONTRACT.read_text())
        self.progression = json.loads(GENERATOR.DEFAULT_PROGRESSION.read_text())

    def generate(self) -> dict:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            contract_path = root / "contract.json"
            progression_path = root / "progression.json"
            contract_path.write_text(json.dumps(self.contract))
            progression_path.write_text(json.dumps(self.progression))
            with patch.object(GENERATOR, "REPO_ROOT", root):
                return GENERATOR.make_catalog(contract_path, progression_path)

    def test_checked_in_catalog_is_reproducible_including_order_and_provenance(self) -> None:
        catalog = GENERATOR.make_catalog(
            GENERATOR.DEFAULT_CONTRACT, GENERATOR.DEFAULT_PROGRESSION
        )
        self.assertEqual(
            GENERATOR.DEFAULT_OUTPUT.read_text(),
            json.dumps(catalog, indent=2, sort_keys=True) + "\n",
        )

    def test_classification_owns_order_even_when_progression_order_disagrees(self) -> None:
        baseline = self.generate()
        indices = [
            index
            for index, row in enumerate(self.contract["feature_classification"])
            if row["proposed_specialization_id"] == "DAGGERS_EXECUTIONER"
            and row["loadout_kind"] == "TECHNIQUE"
        ]
        first, last = indices[0], indices[-1]
        rows = self.contract["feature_classification"]
        rows[first], rows[last] = rows[last], rows[first]
        generated = self.generate()
        expected = baseline["specializations"]
        heartseeker = next(
            row for row in expected
            if row["specialization_id"] == "DAGGERS_EXECUTIONER"
        )
        ids = heartseeker["technique_ability_ids"]
        ids[0], ids[-1] = ids[-1], ids[0]
        self.assertEqual(generated["specializations"], expected)

    def test_missing_classification_is_rejected_instead_of_dropping_a_feature(self) -> None:
        removed = self.contract["feature_classification"].pop()
        with self.assertRaisesRegex(ValueError, removed["ability_id"]):
            self.generate()

    def test_new_selectable_ability_requires_classification(self) -> None:
        self.progression["abilities"].append({
            "ability_id": "TEST_UNCLASSIFIED_PLAYER_ABILITY",
            "actor_scope": "PLAYER",
            "selection_kind": "ACTIVE",
        })
        with self.assertRaisesRegex(ValueError, "TEST_UNCLASSIFIED_PLAYER_ABILITY"):
            self.generate()

    def test_duplicate_classification_is_rejected(self) -> None:
        self.contract["feature_classification"].append(
            self.contract["feature_classification"][0].copy()
        )
        with self.assertRaisesRegex(ValueError, "classified more than once"):
            self.generate()

    def test_progression_discipline_must_match_classified_parent(self) -> None:
        for value in ["STAFF", None]:
            with self.subTest(discipline=value):
                ability = next(row for row in self.progression["abilities"]
                               if row["ability_id"] == "DAGGER_DISARM")
                ability["combat_discipline_id"] = value
                with self.assertRaisesRegex(ValueError, "DAGGER_DISARM.*disagrees"):
                    self.generate()

    def test_duplicate_progression_identity_is_rejected(self) -> None:
        self.progression["abilities"].append(self.progression["abilities"][0].copy())
        with self.assertRaisesRegex(ValueError, "duplicate ability ids"):
            self.generate()

    def test_duplicate_specialization_is_rejected(self) -> None:
        self.contract["specializations"].append(
            self.contract["specializations"][0].copy()
        )
        with self.assertRaisesRegex(ValueError, "duplicate specialization"):
            self.generate()

    def test_unknown_specialization_is_rejected(self) -> None:
        self.contract["feature_classification"][0]["proposed_specialization_id"] = "UNKNOWN"
        with self.assertRaisesRegex(ValueError, "unknown specialization UNKNOWN"):
            self.generate()

    def test_unknown_loadout_kind_is_rejected(self) -> None:
        self.contract["feature_classification"][0]["loadout_kind"] = "UNKNOWN"
        with self.assertRaisesRegex(ValueError, "unsupported loadout kind UNKNOWN"):
            self.generate()

    def test_nonselectable_ability_cannot_leak_into_player_classification(self) -> None:
        ability_id = self.contract["feature_classification"][0]["ability_id"]
        ability = next(
            row for row in self.progression["abilities"] if row["ability_id"] == ability_id
        )
        ability["actor_scope"] = "NPC"
        with self.assertRaisesRegex(ValueError, f"unexpected=\\['{ability_id}'\\]"):
            self.generate()


if __name__ == "__main__":
    unittest.main()

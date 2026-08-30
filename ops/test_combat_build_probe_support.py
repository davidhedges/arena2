#!/usr/bin/env python3

import json
import unittest

from combat_build_probe_support import (
    build_probe_combat_draft,
    configure_probe_combat_build,
)


class FakeProbe:
    identity = "ab" * 32
    name = "fake"

    def __init__(self):
        self.draft = None

    def call(self, reducer, args):
        self.asserted_reducer = reducer
        self.draft = json.loads(args[0])

    def sql(self, query):
        owner = f"0x{self.identity}"
        if "FROM match_combat_build_v_2" in query:
            return [[owner, self.draft["starting_discipline_id"]]]
        if "FROM match_selected_specialization_v_2" in query:
            return [
                [owner, row["slot_index"], row["specialization_id"]]
                for row in self.draft["selected_specializations"]
            ]
        if "FROM match_technique_selection_v_2" in query:
            return [
                [
                    owner,
                    row["specialization_id"],
                    "TWO_HANDED_SWORD",
                    row["ability_id"],
                    row["preferred_bar_order"],
                ]
                for row in self.draft["selected_features"]
            ]
        if "FROM match_spell_selection_v_2" in query:
            return []
        if "FROM match_perk_selection_v_2" in query:
            return []
        if "FROM active_combat_build_discipline" in query:
            return [[owner, self.draft["starting_discipline_id"]]]
        raise AssertionError(query)


class CombatBuildProbeSupportTests(unittest.TestCase):
    def test_mixed_probe_build_uses_specialization_ownership_and_weapons(self):
        draft = build_probe_combat_draft(
            ["WARRIOR_MAIM", "WARRIOR_CHARGE", "SPELL_SMITE"],
            starting_discipline_id="TWO_HANDED_SWORD",
        )

        self.assertEqual(
            [
                row["specialization_id"]
                for row in draft["selected_specializations"]
            ],
            ["TWO_HANDED_SWORD_VANGUARD", "DIVINITY"],
        )
        warrior, staff = draft["discipline_configurations"]
        self.assertEqual(
            warrior["main_hand_item_def_id"],
            "TRAINING_TWO_HAND_SWORD",
        )
        self.assertEqual(
            [row["preferred_bar_order"] for row in draft["selected_features"]],
            [0, 1, 0],
        )
        self.assertEqual(staff["combat_discipline_id"], "STAFF")

    def test_staff_probe_build_derives_distinct_authored_schools(self):
        draft = build_probe_combat_draft(
            ["SPELL_SMITE", "SPELL_FROST_NOVA", "SPELL_NECROTIC_AURA"]
        )

        self.assertEqual(draft["starting_discipline_id"], "STAFF")
        self.assertEqual(
            [row["specialization_id"] for row in draft["selected_specializations"]],
            ["DIVINITY", "BLIGHT", "MORTALITY"],
        )

    def test_probe_build_rejects_intrinsic_as_an_active(self):
        with self.assertRaisesRegex(ValueError, "not a selectable v2 Combat Feature"):
            build_probe_combat_draft(["DAGGER_STALK_SHADOWSTEP"])

    def test_configure_waits_for_exact_frozen_assignments_and_active_discipline(self):
        probe = FakeProbe()

        draft = configure_probe_combat_build(
            probe,
            ["WARRIOR_MAIM", "WARRIOR_CHARGE"],
        )

        self.assertEqual(
            probe.asserted_reducer,
            "configure_local_direct_probe_combat_build",
        )
        self.assertEqual(draft["starting_discipline_id"], "TWO_HANDED_SWORD")


if __name__ == "__main__":
    unittest.main()

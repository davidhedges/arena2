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
        if "FROM match_combat_build" in query:
            return [[owner, self.draft["starting_discipline_id"]]]
        if "FROM match_discipline_action_bar_assignment" in query:
            return [
                [
                    owner,
                    configuration["combat_discipline_id"],
                    assignment["action_slot"],
                    assignment["ability_id"],
                ]
                for configuration in self.draft["discipline_configurations"]
                for assignment in configuration["active_assignments"]
            ]
        if "FROM active_combat_build_discipline" in query:
            return [[owner, self.draft["starting_discipline_id"]]]
        raise AssertionError(query)


class CombatBuildProbeSupportTests(unittest.TestCase):
    def test_mixed_probe_build_uses_canonical_ownership_weapons_and_slots(self):
        draft = build_probe_combat_draft(
            ["WARRIOR_MAIM", "WARRIOR_CHARGE", "SPELL_SMITE"],
            starting_discipline_id="TWO_HANDED_SWORD",
        )

        self.assertEqual(
            [
                row["combat_discipline_id"]
                for row in draft["selected_disciplines"]
            ],
            ["TWO_HANDED_SWORD", "STAFF"],
        )
        warrior, staff = draft["discipline_configurations"]
        self.assertEqual(
            warrior["weapon"]["main_hand_item_def_id"],
            "TRAINING_TWO_HAND_SWORD",
        )
        self.assertEqual(
            [row["action_slot"] for row in warrior["active_assignments"]],
            ["slot_0_0", "slot_0_1"],
        )
        self.assertEqual(staff["staff_school_ids"], ["DIVINITY"])

    def test_staff_probe_build_derives_distinct_authored_schools(self):
        draft = build_probe_combat_draft(
            ["SPELL_SMITE", "SPELL_FROST_NOVA", "SPELL_NECROTIC_AURA"]
        )

        configuration = draft["discipline_configurations"][0]
        self.assertEqual(draft["starting_discipline_id"], "STAFF")
        self.assertEqual(
            configuration["staff_school_ids"],
            ["DIVINITY", "BLIGHT", "MORTALITY"],
        )

    def test_probe_build_rejects_intrinsic_as_an_active(self):
        with self.assertRaisesRegex(ValueError, "INTRINSIC, not ACTIVE"):
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

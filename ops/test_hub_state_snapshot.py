import copy
import unittest

from ops import hub_state_snapshot as snapshot


def fixture():
    tables = {}
    for table in snapshot.TABLES:
        owner = snapshot.PLAYER_TABLES.get(table, "key")
        tables[table] = {
            "schema": {"elements": [{"name": {"some": owner}}, {"name": {"some": "value"}}]},
            "rows": [["original", "saved"]],
        }
    return tables


class HubStateSnapshotTests(unittest.TestCase):
    def test_new_identity_is_allowed_without_changing_existing_profiles(self):
        before = fixture()
        after = copy.deepcopy(before)
        for table in snapshot.PLAYER_TABLES:
            after[table]["rows"].append(["new", "default"])
        snapshot.require_preserved(before, after)

    def test_saved_mutation_deletion_or_extra_child_is_rejected(self):
        before = fixture()
        for rows in ([], [["original", "changed"]], [["original", "saved"], ["original", "extra"]]):
            with self.subTest(rows=rows):
                after = copy.deepcopy(before)
                after["specialization_feature_selection_v_2"]["rows"] = rows
                with self.assertRaisesRegex(ValueError, "specialization_feature_selection"):
                    snapshot.require_preserved(before, after)

    def test_dormant_state_and_cutover_audit_are_protected(self):
        before = fixture()
        for table in ("dormant_specialization_v_2", "combat_build_v_2_cutover_audit"):
            with self.subTest(table=table):
                after = copy.deepcopy(before)
                after[table]["rows"] = []
                with self.assertRaisesRegex(ValueError, table):
                    snapshot.require_preserved(before, after)

    def test_catalog_change_is_rejected(self):
        before = fixture()
        after = copy.deepcopy(before)
        after["hub_weapon_definition"]["rows"].append(["new", "weapon"])
        with self.assertRaisesRegex(ValueError, "hub_weapon_definition"):
            snapshot.require_preserved(before, after)

    def test_schema_and_table_inventory_changes_are_rejected(self):
        before = fixture()
        after = copy.deepcopy(before)
        after["combat_build_v_2"]["schema"]["elements"].append({"name": {"some": "unexpected"}})
        with self.assertRaisesRegex(ValueError, "Schema changed"):
            snapshot.require_preserved(before, after)
        del after["combat_build_v_2"]
        with self.assertRaisesRegex(ValueError, "inventory"):
            snapshot.require_preserved(before, after)

    def test_table_row_order_is_irrelevant(self):
        before = fixture()
        before["hub_weapon_definition"]["rows"].append(["second", "saved"])
        after = copy.deepcopy(before)
        after["hub_weapon_definition"]["rows"].reverse()
        snapshot.require_preserved(before, after)


if __name__ == "__main__":
    unittest.main()

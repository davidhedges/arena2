"""Check contract key mapping and drift detection without a running database."""

import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location(
    "shared_contracts", Path(__file__).with_name("verify-spacetimedb-contracts.py")
)
CONTRACTS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CONTRACTS)


class SharedDataContractsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.client = self.root / "Assets/Arena/Resources/SharedData"
        self.source = self.root / "server/src"
        self.write(self.client / "weapon_appearance_catalog.shared.json", b"{}")
        self.pair("open_world_heightfield.shared.json", "open_world_heightfield.shared.json")

    def write(self, path, data=b"{}\n"):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def pair(self, source, client):
        self.write(self.source / source)
        self.write(self.client / client)

    def test_maps_and_worlds_use_server_relative_keys(self):
        self.pair("map_data/arena.layout.shared.json", "Maps/arena.layout.shared.json")
        self.pair("world_data/traps.shared.json", "WorldInteractions/traps.shared.json")
        self.pair("world_data/terrain.shared.json", "Worlds/terrain.shared.json")
        keys = CONTRACTS.client_contract_paths(self.root)
        self.assertIn("map_data/arena.layout.shared.json", keys)
        self.assertIn("world_data/traps.shared.json", keys)
        self.assertIn("world_data/terrain.shared.json", keys)
        self.assertEqual(CONTRACTS.verify_mirrors(self.root), 4)

    def test_different_mirror_is_rejected(self):
        self.write(self.client / "open_world_heightfield.shared.json", b'{"changed":true}')
        with self.assertRaisesRegex(RuntimeError, "MIRROR MISMATCH"):
            CONTRACTS.verify_mirrors(self.root)

    def test_missing_source_is_rejected(self):
        self.write(self.client / "Maps/missing.shared.json")
        with self.assertRaisesRegex(RuntimeError, "MISSING SOURCE map_data/missing"):
            CONTRACTS.verify_mirrors(self.root)

    def test_unbundled_map_is_rejected(self):
        self.write(self.source / "map_data/unbundled.shared.json")
        with self.assertRaisesRegex(RuntimeError, "MISSING BUNDLE map_data/unbundled"):
            CONTRACTS.verify_mirrors(self.root)

    def test_missing_required_root_is_rejected(self):
        (self.client / "weapon_appearance_catalog.shared.json").unlink()
        with self.assertRaisesRegex(RuntimeError, "MISSING BUNDLE weapon_appearance"):
            CONTRACTS.verify_mirrors(self.root)

    def test_duplicate_keys_across_world_folders_are_rejected(self):
        self.pair("world_data/traps.shared.json", "Worlds/traps.shared.json")
        self.write(self.client / "WorldInteractions/traps.shared.json")
        with self.assertRaisesRegex(RuntimeError, "ambiguous"):
            CONTRACTS.verify_mirrors(self.root)

    def test_line_endings_match_runtime_hash_policy(self):
        self.write(self.client / "open_world_heightfield.shared.json", b"{}\r\n")
        self.assertEqual(CONTRACTS.verify_mirrors(self.root), 1)
        self.assertEqual(CONTRACTS.shared_content_hash(self.client / "open_world_heightfield.shared.json"),
                         CONTRACTS.shared_content_hash(self.source / "open_world_heightfield.shared.json"))

    def test_runtime_only_catalog_does_not_require_a_client_copy(self):
        self.write(self.source / "progression_catalog.shared.json")
        self.assertEqual(CONTRACTS.verify_mirrors(self.root), 1)


if __name__ == "__main__":
    unittest.main()

from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

from match_provisioner.artifact_provenance import (
    ArtifactProvenanceError,
    collect_build_inputs,
    verify_artifact_manifest,
    write_artifact_manifest,
)


class ArtifactProvenanceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "server/src/spells/catalog.rs"
        self.source.parent.mkdir(parents=True)
        self.source.write_text("pub const SPELL: &str = \"CURRENT\";\n", encoding="utf-8")
        self.wasm = self.root / "match-server/target/arena_match.opt.wasm"
        self.wasm.parent.mkdir(parents=True)
        self.wasm.write_bytes(b"current wasm")
        self.manifest = Path(f"{self.wasm}.inputs.json")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write_manifest(self) -> None:
        write_artifact_manifest(
            self.root,
            self.wasm,
            self.manifest,
            [self.source.relative_to(self.root)],
        )

    def test_current_sources_and_wasm_verify(self) -> None:
        self.write_manifest()

        verified = verify_artifact_manifest(self.wasm, self.manifest)

        self.assertEqual(len(verified["inputs"]), 1)

    def test_changed_source_is_rejected(self) -> None:
        self.write_manifest()
        self.source.write_text("pub const SPELL: &str = \"NEW\";\n", encoding="utf-8")

        with self.assertRaisesRegex(ArtifactProvenanceError, "cached match WASM is stale"):
            verify_artifact_manifest(self.wasm, self.manifest)

    def test_changed_wasm_is_rejected(self) -> None:
        self.write_manifest()
        self.wasm.write_bytes(b"different wasm")

        with self.assertRaisesRegex(ArtifactProvenanceError, "does not match"):
            verify_artifact_manifest(self.wasm, self.manifest)

    def test_depfile_collection_excludes_generated_target_files(self) -> None:
        generated = self.root / "match-server/target/generated.rs"
        generated.write_text("generated", encoding="utf-8")
        cargo_toml = self.root / "match-server/Cargo.toml"
        cargo_toml.write_text("[package]\nname = \"match\"\n", encoding="utf-8")
        depfile = self.root / "arena_match.d"
        depfile.write_text(
            f"{self.wasm}: {self.source} {generated}\n",
            encoding="utf-8",
        )

        inputs = collect_build_inputs(self.root, depfile)

        self.assertIn(Path("server/src/spells/catalog.rs"), inputs)
        self.assertIn(Path("match-server/Cargo.toml"), inputs)
        self.assertNotIn(Path("match-server/target/generated.rs"), inputs)


if __name__ == "__main__":
    unittest.main()

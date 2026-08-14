#!/usr/bin/env python3
"""Write and verify source provenance for the cached disposable-match WASM."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import sys
import tempfile
from typing import Iterable


SCHEMA_VERSION = 1
REBUILD_COMMAND = "ops/setup-local-multiplayer.sh setup"
EXPLICIT_BUILD_INPUTS = (
    "match-server/Cargo.toml",
    "match-server/Cargo.lock",
    "server/Cargo.toml",
    "server/Cargo.lock",
)
IGNORED_INPUT_PARTS = {".git", "Library", "Temp", "target"}


class ArtifactProvenanceError(RuntimeError):
    """The cached match artifact cannot be proven current."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_depfile(depfile: Path) -> list[Path]:
    try:
        content = depfile.read_text(encoding="utf-8").replace("\\\n", " ")
    except OSError as error:
        raise ArtifactProvenanceError(f"cannot read Cargo depfile {depfile}: {error}") from error
    _, separator, dependencies = content.partition(":")
    if not separator:
        raise ArtifactProvenanceError(f"Cargo depfile has no dependency list: {depfile}")
    try:
        return [Path(value) for value in shlex.split(dependencies)]
    except ValueError as error:
        raise ArtifactProvenanceError(f"cannot parse Cargo depfile {depfile}: {error}") from error


def collect_build_inputs(workspace_root: Path, depfile: Path) -> list[Path]:
    workspace_root = workspace_root.resolve()
    candidates = parse_depfile(depfile)
    candidates.extend(workspace_root / relative for relative in EXPLICIT_BUILD_INPUTS)
    inputs: set[Path] = set()
    for candidate in candidates:
        resolved = candidate.resolve()
        if not resolved.is_file():
            continue
        try:
            relative = resolved.relative_to(workspace_root)
        except ValueError:
            continue
        if any(part in IGNORED_INPUT_PARTS for part in relative.parts):
            continue
        inputs.add(relative)
    if not inputs:
        raise ArtifactProvenanceError("Cargo depfile produced no workspace build inputs")
    return sorted(inputs, key=lambda path: path.as_posix())


def _source_fingerprint(rows: Iterable[dict[str, str]]) -> str:
    digest = hashlib.sha256()
    for row in rows:
        digest.update(row["path"].encode("utf-8"))
        digest.update(b"\0")
        digest.update(row["sha256"].encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()


def write_artifact_manifest(
    workspace_root: Path,
    wasm_path: Path,
    manifest_path: Path,
    inputs: Iterable[Path],
) -> dict[str, object]:
    workspace_root = workspace_root.resolve()
    wasm_path = wasm_path.resolve()
    manifest_path = manifest_path.resolve()
    if not wasm_path.is_file():
        raise ArtifactProvenanceError(f"match WASM does not exist: {wasm_path}")
    rows: list[dict[str, str]] = []
    for input_path in sorted(set(inputs), key=lambda path: path.as_posix()):
        relative = input_path
        if input_path.is_absolute():
            try:
                relative = input_path.resolve().relative_to(workspace_root)
            except ValueError as error:
                raise ArtifactProvenanceError(
                    f"build input is outside workspace: {input_path}"
                ) from error
        source = (workspace_root / relative).resolve()
        try:
            source.relative_to(workspace_root)
        except ValueError as error:
            raise ArtifactProvenanceError(f"unsafe build input path: {relative}") from error
        if not source.is_file():
            raise ArtifactProvenanceError(f"build input does not exist: {relative}")
        rows.append({"path": relative.as_posix(), "sha256": sha256_file(source)})
    if not rows:
        raise ArtifactProvenanceError("artifact manifest requires at least one build input")
    manifest: dict[str, object] = {
        "schema_version": SCHEMA_VERSION,
        "workspace_root": str(workspace_root),
        "wasm_path": os.path.relpath(wasm_path, workspace_root),
        "wasm_sha256": sha256_file(wasm_path),
        "source_fingerprint": _source_fingerprint(rows),
        "inputs": rows,
    }
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(
        "w",
        encoding="utf-8",
        dir=manifest_path.parent,
        prefix=f".{manifest_path.name}.",
        delete=False,
    ) as destination:
        json.dump(manifest, destination, indent=2, sort_keys=True)
        destination.write("\n")
        temporary_path = Path(destination.name)
    os.replace(temporary_path, manifest_path)
    return manifest


def verify_artifact_manifest(
    wasm_path: Path,
    manifest_path: Path,
    *,
    expected_wasm_sha256: str | None = None,
) -> dict[str, object]:
    wasm_path = wasm_path.resolve()
    manifest_path = manifest_path.resolve()
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise ArtifactProvenanceError(
            f"match artifact provenance is missing: {manifest_path}; run {REBUILD_COMMAND}"
        ) from error
    except (OSError, json.JSONDecodeError) as error:
        raise ArtifactProvenanceError(
            f"match artifact provenance is unreadable: {error}; run {REBUILD_COMMAND}"
        ) from error
    if not isinstance(manifest, dict) or manifest.get("schema_version") != SCHEMA_VERSION:
        raise ArtifactProvenanceError(
            f"match artifact provenance schema is unsupported; run {REBUILD_COMMAND}"
        )
    workspace_value = manifest.get("workspace_root")
    input_rows = manifest.get("inputs")
    recorded_wasm_sha256 = manifest.get("wasm_sha256")
    if not isinstance(workspace_value, str) or not isinstance(input_rows, list):
        raise ArtifactProvenanceError(
            f"match artifact provenance is incomplete; run {REBUILD_COMMAND}"
        )
    workspace_root = Path(workspace_value).resolve()
    if not wasm_path.is_file():
        raise ArtifactProvenanceError(
            f"cached match WASM is missing: {wasm_path}; run {REBUILD_COMMAND}"
        )
    current_wasm_sha256 = sha256_file(wasm_path)
    if recorded_wasm_sha256 != current_wasm_sha256:
        raise ArtifactProvenanceError(
            f"cached match WASM does not match its provenance manifest; run {REBUILD_COMMAND}"
        )
    if expected_wasm_sha256 is not None and expected_wasm_sha256 != current_wasm_sha256:
        raise ArtifactProvenanceError(
            "the running provisioner loaded a different match WASM; "
            f"restart it with {REBUILD_COMMAND}"
        )
    current_rows: list[dict[str, str]] = []
    changed: list[str] = []
    for row in input_rows:
        if not isinstance(row, dict):
            raise ArtifactProvenanceError(
                f"match artifact provenance contains an invalid input; run {REBUILD_COMMAND}"
            )
        relative_value = row.get("path")
        recorded_sha256 = row.get("sha256")
        if not isinstance(relative_value, str) or not isinstance(recorded_sha256, str):
            raise ArtifactProvenanceError(
                f"match artifact provenance contains an invalid input; run {REBUILD_COMMAND}"
            )
        relative = Path(relative_value)
        if relative.is_absolute() or ".." in relative.parts:
            raise ArtifactProvenanceError(
                f"match artifact provenance contains an unsafe path; run {REBUILD_COMMAND}"
            )
        source = (workspace_root / relative).resolve()
        try:
            source.relative_to(workspace_root)
        except ValueError as error:
            raise ArtifactProvenanceError(
                f"match artifact provenance escapes the workspace; run {REBUILD_COMMAND}"
            ) from error
        current_sha256 = sha256_file(source) if source.is_file() else "MISSING"
        current_rows.append({"path": relative.as_posix(), "sha256": current_sha256})
        if current_sha256 != recorded_sha256:
            changed.append(relative.as_posix())
    if not current_rows or manifest.get("source_fingerprint") != _source_fingerprint(current_rows):
        detail = ", ".join(changed[:5]) or "input fingerprint mismatch"
        if len(changed) > 5:
            detail += f" (+{len(changed) - 5} more)"
        raise ArtifactProvenanceError(
            f"cached match WASM is stale ({detail}); run {REBUILD_COMMAND}"
        )
    return manifest


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    write = subparsers.add_parser("write", help="write provenance after a successful build")
    write.add_argument("--workspace-root", type=Path, required=True)
    write.add_argument("--depfile", type=Path, required=True)
    write.add_argument("--wasm", type=Path, required=True)
    write.add_argument("--manifest", type=Path, required=True)
    verify = subparsers.add_parser("verify", help="reject a stale cached match artifact")
    verify.add_argument("--wasm", type=Path, required=True)
    verify.add_argument("--manifest", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        if args.command == "write":
            inputs = collect_build_inputs(args.workspace_root, args.depfile)
            manifest = write_artifact_manifest(
                args.workspace_root,
                args.wasm,
                args.manifest,
                inputs,
            )
            print(
                "Wrote match artifact provenance "
                f"({len(manifest['inputs'])} inputs, {manifest['source_fingerprint']})"
            )
        else:
            manifest = verify_artifact_manifest(args.wasm, args.manifest)
            print(
                "Match artifact provenance verified "
                f"({len(manifest['inputs'])} inputs, {manifest['source_fingerprint']})"
            )
    except ArtifactProvenanceError as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

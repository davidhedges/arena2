#!/usr/bin/env python3
"""Verify Unity's bundled shared-data hashes against a live SpacetimeDB."""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path


FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3
ROW_PATTERN = re.compile(r'^\s*"([^"]+)"\s*\|\s*(\d+)\s*$')


def shared_content_hash(path: Path) -> int:
    value = FNV_OFFSET
    for byte in path.read_bytes():
        if byte == ord("\r"):
            continue
        value ^= byte
        value = (value * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return value


def client_contracts(repo_root: Path) -> dict[str, tuple[int, Path]]:
    shared_root = repo_root / "Assets/Arena/Resources/SharedData"
    contracts: dict[str, tuple[int, Path]] = {}
    for path in sorted(shared_root.rglob("*.json")):
        relative = path.relative_to(shared_root)
        if relative.parts[0] in {"Worlds", "WorldInteractions"}:
            key = f"world_data/{path.name}"
        else:
            key = path.name

        if key in contracts:
            other = contracts[key][1]
            raise RuntimeError(
                f"Bundled shared-data key '{key}' is ambiguous: {other} and {path}"
            )
        contracts[key] = (shared_content_hash(path), path)
    return contracts


def live_contracts(database: str, server: str) -> dict[str, int]:
    command = [
        "spacetime",
        "sql",
        "-s",
        server,
        database,
        "SELECT key, content_hash FROM contract_version",
    ]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip() or "no CLI output"
        raise RuntimeError(f"spacetime sql failed: {detail}")
    rows: dict[str, int] = {}
    for line in result.stdout.splitlines():
        match = ROW_PATTERN.match(line)
        if match:
            rows[match.group(1)] = int(match.group(2))
    if not rows:
        raise RuntimeError(
            f"Live database '{database}' on '{server}' returned no contract_version rows."
        )
    return rows


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", default="arena")
    parser.add_argument("--server", default="local")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent
    expected = client_contracts(repo_root)
    live = live_contracts(args.database, args.server)
    missing = []
    mismatched = []
    for key, (expected_hash, path) in expected.items():
        actual_hash = live.get(key)
        if actual_hash is None:
            missing.append((key, path))
        elif actual_hash != expected_hash:
            mismatched.append((key, expected_hash, actual_hash, path))

    for key, path in missing:
        print(f"MISSING {key} ({path.relative_to(repo_root)})", file=sys.stderr)
    for key, expected_hash, actual_hash, path in mismatched:
        print(
            f"MISMATCH {key}: client {expected_hash:016x} != "
            f"server {actual_hash:016x} ({path.relative_to(repo_root)})",
            file=sys.stderr,
        )

    if missing or mismatched:
        print(
            f"Shared-data contract verification FAILED: {len(mismatched)} mismatched, "
            f"{len(missing)} missing, {len(expected) - len(missing) - len(mismatched)} verified.",
            file=sys.stderr,
        )
        return 1

    print(
        f"Shared-data contract verification PASS: {len(expected)} client contracts "
        f"match '{args.database}' on '{args.server}'."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"Shared-data contract verification ERROR: {error}", file=sys.stderr)
        raise SystemExit(2)

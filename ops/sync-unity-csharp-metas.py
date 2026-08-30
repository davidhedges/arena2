#!/usr/bin/env python3
"""Create missing and remove orphaned Unity metadata for C# source trees."""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]


def metadata_for(relative_path: str) -> str:
    guid = hashlib.sha256(
        f"arena2-unity-csharp:{relative_path}".encode("utf-8")
    ).hexdigest()[:32]
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "MonoImporter:\n"
        "  externalObjects: {}\n"
        "  serializedVersion: 2\n"
        "  defaultReferences: []\n"
        "  executionOrder: 0\n"
        "  icon: {instanceID: 0}\n"
        "  userData:\n"
        "  assetBundleName:\n"
        "  assetBundleVariant:\n"
    )


def resolve_scoped_root(value: str) -> Path:
    path = (REPOSITORY_ROOT / value).resolve()
    if path == REPOSITORY_ROOT or REPOSITORY_ROOT not in path.parents:
        raise ValueError(f"path must be a repository subdirectory: {value}")
    if not path.is_dir():
        raise ValueError(f"path is not a directory: {value}")
    return path


def synchronize(root: Path) -> tuple[int, int]:
    created = 0
    removed = 0
    for source in sorted(root.rglob("*.cs")):
        meta = source.with_name(f"{source.name}.meta")
        if meta.exists():
            continue
        relative = source.relative_to(REPOSITORY_ROOT).as_posix()
        meta.write_text(metadata_for(relative), encoding="utf-8")
        created += 1

    for meta in sorted(root.rglob("*.cs.meta")):
        source = meta.with_name(meta.name.removesuffix(".meta"))
        if source.exists():
            continue
        meta.unlink()
        removed += 1
    return created, removed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("roots", nargs="+", help="Repository-relative C# source roots")
    arguments = parser.parse_args()

    total_created = 0
    total_removed = 0
    for value in arguments.roots:
        root = resolve_scoped_root(value)
        created, removed = synchronize(root)
        total_created += created
        total_removed += removed
        print(f"{root.relative_to(REPOSITORY_ROOT)}: created={created} removed={removed}")
    print(f"unity-csharp-metas: created={total_created} removed={total_removed}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Headless UI Toolkit preview: render a UXML screen to a PNG, no editor focus.

Runs a throwaway Unity project at .ui-preview/ (git-ignored; Library persists,
so runs after the first are fast) so it works even while the main editor holds
the project lock. Each run syncs the UI Toolkit assets + fonts + theme art from
the repo, launches Unity batchmode play mode, renders the UXML through a
runtime panel into a RenderTexture, and writes the PNG.

Usage:
  ops/ui-preview.py                                   # SystemMenu, open state
  ops/ui-preview.py --uxml Assets/Arena/Resources/UI/Toolkit/SystemMenu.uxml \
      --classes "SystemMenu:is-open,Window:is-open,QualityDropdown:is-open" \
      --out /tmp/menu.png
  ops/ui-preview.py --fresh                           # rebuild the mini project
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
PREVIEW = REPO / ".ui-preview"
RUNNER_SRC = REPO / "ops/ui-preview/UiPreviewRunner.cs"

# Everything the UXML/USS/theme graph can reference must exist at the same
# project-relative paths inside the preview project.
SYNC_DIRS = [
    "Assets/Arena/Resources/UI/Toolkit",
    "Assets/Arena/Content/UI",
]

MANIFEST = """{
  "dependencies": {
    "com.unity.modules.uielements": "1.0.0",
    "com.unity.modules.imageconversion": "1.0.0"
  }
}
"""


def unity_binary() -> Path:
    version = (REPO / "ProjectSettings/ProjectVersion.txt").read_text().split()[1]
    binary = Path(f"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity")
    if not binary.exists():
        sys.exit(f"Unity {version} not found at {binary}")
    return binary


def build_project(fresh: bool) -> bool:
    first_run = fresh or not (PREVIEW / "Library").exists()
    if fresh and PREVIEW.exists():
        shutil.rmtree(PREVIEW)

    (PREVIEW / "ProjectSettings").mkdir(parents=True, exist_ok=True)
    (PREVIEW / "Packages").mkdir(exist_ok=True)
    shutil.copy(REPO / "ProjectSettings/ProjectVersion.txt", PREVIEW / "ProjectSettings/ProjectVersion.txt")
    (PREVIEW / "Packages/manifest.json").write_text(MANIFEST)

    editor_dir = PREVIEW / "Assets/Editor"
    editor_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy(RUNNER_SRC, editor_dir / "UiPreviewRunner.cs")

    for rel in SYNC_DIRS:
        src, dst = REPO / rel, PREVIEW / rel
        if dst.exists():
            shutil.rmtree(dst)
        shutil.copytree(src, dst, ignore=shutil.ignore_patterns("*.meta"))
    return first_run


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--uxml", default="Assets/Arena/Resources/UI/Toolkit/SystemMenu.uxml")
    parser.add_argument("--out", default=str(REPO / ".ui-preview/preview.png"))
    parser.add_argument("--classes", default="SystemMenu:is-open,Window:is-open",
                        help="comma list of Name:class to add before capture")
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--fresh", action="store_true", help="rebuild the mini project from scratch")
    args = parser.parse_args()

    first_run = build_project(args.fresh)
    out = Path(args.out).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists():
        out.unlink()

    env = os.environ.copy()
    env.update({
        "ARENA_UIPREVIEW_UXML": args.uxml,
        "ARENA_UIPREVIEW_OUT": str(out),
        "ARENA_UIPREVIEW_CLASSES": args.classes,
        "ARENA_UIPREVIEW_WIDTH": str(args.width),
        "ARENA_UIPREVIEW_HEIGHT": str(args.height),
    })

    log = PREVIEW / "preview.log"
    command = [
        str(unity_binary()), "-batchmode",
        "-projectPath", str(PREVIEW),
        "-executeMethod", "UiPreviewRunner.Run",
        "-logFile", str(log),
    ]
    timeout = 1200 if first_run else 300
    try:
        result = subprocess.run(command, env=env, timeout=timeout)
    except subprocess.TimeoutExpired:
        sys.exit(f"Unity preview timed out after {timeout}s; see {log}")

    if not out.exists():
        tail = "\n".join(log.read_text().splitlines()[-25:]) if log.exists() else "(no log)"
        sys.exit(f"preview failed (unity exit {result.returncode}); log tail:\n{tail}")

    print(out)
    return 0


if __name__ == "__main__":
    sys.exit(main())

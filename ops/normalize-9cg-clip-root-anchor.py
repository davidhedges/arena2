#!/usr/bin/env python3
"""Re-anchor Studio 9CG player clips to the body's centre of mass.

The defect
----------
Every clip in these packs ships with Root Transform Position (XZ) set to
`Based Upon: Original`, which anchors the pose to the position the clip had in
the vendor's source scene. The packs author a move as one continuous travelling
take, so each phase is authored somewhere different: the greatsword whirlwind's
End clip sits 0.27 m from the same pack's combat idle.

Root motion is discarded (`Animator.applyRootMotion = false`), so that authored
offset never moves the character - it becomes a constant offset of the *pose*
from the pinned gameplay root, and it differs per clip. Finishing the whirlwind
therefore leaves the character standing ~0.29 m from where the combat idle puts
them, and the blend back to idle drags them across that gap. That is the slide.

Measured on the whirlwind, at the 0.88 normalized time where
`PlayerAnimator.PhasedMeleeEndCompleteNormalizedTime` actually cuts the End clip:

    as shipped                     0.291 m stance offset from idle
    Based Upon: Center of Mass     0.006 m

What this deliberately does NOT touch
-------------------------------------
* Bake Into Pose. Baking XZ into the pose is the usual advice and it is wrong
  here - measured, it puts the clip's full 3.3 m of authored travel into the body.
* Root Transform Rotation basis. The vendor's rotation authoring is correct:
  the whirlwind chains Start 45 -> 317, Loop 317 -> 317, End 317 -> 45.12, landing
  exactly on the combat idle's yaw. Normalizing rotation would square up every
  bladed combat stance in the packs for no gain.

Scope note: run this per pack, not per clip. The anchor is only consistent if a
pack's idle and its action clips share it.
"""
from __future__ import annotations

import argparse
import pathlib
import sys

PACK_ROOT = "Assets/Arena/Content/Animation/Extracted"
PACKS = ("DaggersAnimationPack", "ArcherAnimationPack", "MageAnimationPack",
         "GreatSwordAnimations", "SwordAndShieldAnimationPack")

KEY = "m_KeepOriginalPositionXZ"
WANT = "0"


def rewrite(path: pathlib.Path) -> bool:
    """Set the XZ anchor inside m_AnimationClipSettings. True if the file changed."""
    lines = path.read_text(errors="surrogateescape").split("\n")
    in_settings = False
    changed = False
    for i, line in enumerate(lines):
        if line == "  m_AnimationClipSettings:":
            in_settings = True
            continue
        if in_settings:
            if line.startswith("  m_") and not line.startswith("    "):
                break
            if line.strip().split(":", 1)[0] == KEY:
                replacement = f"    {KEY}: {WANT}"
                if line != replacement:
                    lines[i] = replacement
                    changed = True
                break
    if changed:
        path.write_text("\n".join(lines), errors="surrogateescape")
    return changed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pack", action="append", default=[],
                        help="pack to process; repeatable. Default: all five.")
    parser.add_argument("--apply", action="store_true",
                        help="write the change (default is a dry run)")
    args = parser.parse_args()

    packs = args.pack or list(PACKS)
    unknown = [p for p in packs if p not in PACKS]
    if unknown:
        print(f"unknown pack(s): {', '.join(unknown)}", file=sys.stderr)
        return 2

    repo = pathlib.Path(__file__).resolve().parent.parent
    total_scanned = total_touched = 0
    for pack in packs:
        root = repo / PACK_ROOT / pack
        if not root.is_dir():
            print(f"missing pack: {pack}", file=sys.stderr)
            return 1
        scanned = touched = 0
        for clip in sorted(root.rglob("*.anim")):
            scanned += 1
            if args.apply:
                touched += rewrite(clip)
            else:
                text = clip.read_text(errors="surrogateescape")
                touched += f"    {KEY}: {WANT}" not in text
        print(f"  {pack:28s} {touched:4d} / {scanned:4d}")
        total_scanned += scanned
        total_touched += touched

    verb = "re-anchored" if args.apply else "would re-anchor"
    print(f"\n{verb} {total_touched} of {total_scanned} clips")
    if not args.apply:
        print("dry run - pass --apply to write")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

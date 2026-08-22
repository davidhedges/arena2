#!/usr/bin/env python3
"""Audit how far each Studio 9CG player clip sits from its own pack's combat idle.

Why this exists
---------------
These packs are humanoid, and the game discards root motion
(`Animator.applyRootMotion = false`), so the 1-8 m of travel authored into the
clips never moves the character - measured body drift is 0.000 m. What *does*
survive is `Based Upon: Original` on Root Transform Rotation and Position (XZ):
it keeps the offset and facing the clip had in the vendor's source scene, as a
constant offset of the pose from the pinned gameplay root.

That offset differs per clip. Every blend between two clips therefore has to
travel the difference, and the blend back to idle at the end of an action is the
one you feel as a slide. The greatsword whirlwind is the clearest case: Start
sits exactly on the combat idle, but Loop and End sit 0.27 m away and 88 deg
rotated, so finishing the move swings the character back through all of it.

This script reports that gap per clip so the offenders can be found and re-aimed.
It only reads; it never edits a clip.

NOTE (2026-08-23): all five packs have since been re-anchored to the body's centre
of mass by `ops/normalize-9cg-clip-root-anchor.py`, which changes how Unity
*interprets* these curves without rewriting them. The numbers below therefore still
describe the vendor's authored spread - they no longer predict the in-game pose
offset. Measure that with real Animator playback, not from the curves.
"""
from __future__ import annotations

import argparse
import math
import pathlib
import re
import sys

PACK_ROOT = "Assets/Arena/Content/Animation/Extracted"
PACKS = ("DaggersAnimationPack", "ArcherAnimationPack", "MageAnimationPack",
         "GreatSwordAnimations", "SwordAndShieldAnimationPack")

_TIME = re.compile(r"^time: (-?[\d.eE+-]+)$")
_VALUE = re.compile(r"^value: (-?[\d.eE+-]+)$")
_ATTRIBUTE = re.compile(r"^attribute: (.+)$")


def read_root_pose(path: pathlib.Path):
    """First-key RootT.xz and RootQ yaw. Returns None for non-humanoid clips.

    Only m_FloatCurves is read; m_EditorCurves duplicates it."""
    curves: dict[str, list[float]] = {}
    pending: list[float] = []
    current_time = None
    in_float_curves = False
    with path.open(errors="replace") as handle:
        for line in handle:
            if line.startswith("  m_FloatCurves:"):
                in_float_curves = True
                continue
            if line.startswith("  m_PPtrCurves") or line.startswith("  m_SampleRate"):
                in_float_curves = False
            if not in_float_curves:
                continue
            stripped = line.strip()
            match = _TIME.match(stripped)
            if match:
                current_time = float(match.group(1))
                continue
            match = _VALUE.match(stripped)
            if match and current_time is not None:
                pending.append(float(match.group(1)))
                current_time = None
                continue
            match = _ATTRIBUTE.match(stripped)
            if match:
                curves[match.group(1).strip()] = pending
                pending = []
    if "RootT.x" not in curves or "RootQ.w" not in curves:
        return None
    x = curves["RootT.x"][0]
    z = curves["RootT.z"][0]
    qx, qy, qz, qw = (curves[f"RootQ.{c}"][0] for c in "xyzw")
    yaw = math.degrees(math.atan2(2 * (qw * qy + qx * qz), 1 - 2 * (qy * qy + qz * qz)))
    return x, z, yaw


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pack", default="", help="audit one pack only")
    parser.add_argument("--min-pos", type=float, default=0.15,
                        help="report clips at least this far (m) from the idle pose")
    parser.add_argument("--min-yaw", type=float, default=30.0,
                        help="report clips at least this rotated (deg) from the idle pose")
    parser.add_argument("--top", type=int, default=30, help="rows to print")
    args = parser.parse_args()

    repo = pathlib.Path(__file__).resolve().parent.parent
    exit_code = 0
    for pack in PACKS:
        if args.pack and args.pack != pack:
            continue
        root = repo / PACK_ROOT / pack
        if not root.is_dir():
            print(f"missing pack: {pack}", file=sys.stderr)
            exit_code = 1
            continue

        idles = sorted(root.rglob("Idle_Combat.anim"))
        if not idles:
            print(f"{pack}: no Idle_Combat.anim to reference", file=sys.stderr)
            exit_code = 1
            continue
        reference = read_root_pose(idles[0])
        if reference is None:
            print(f"{pack}: {idles[0].name} has no humanoid root curves", file=sys.stderr)
            exit_code = 1
            continue
        ref_x, ref_z, ref_yaw = reference

        rows = []
        for clip in sorted(root.rglob("*.anim")):
            pose = read_root_pose(clip)
            if pose is None:
                continue
            x, z, yaw = pose
            offset = math.hypot(x - ref_x, z - ref_z)
            swing = abs((yaw - ref_yaw + 180) % 360 - 180)
            if offset >= args.min_pos or swing >= args.min_yaw:
                rows.append((offset, swing, clip.relative_to(repo)))
        rows.sort(reverse=True)

        print(f"\n{pack}  (idle reference: xz=({ref_x:.3f},{ref_z:.3f}) yaw={ref_yaw % 360:.1f}deg)")
        print(f"  {len(rows)} clips at least {args.min_pos} m or {args.min_yaw} deg off the idle pose")
        for offset, swing, rel in rows[:args.top]:
            print(f"  {offset:6.3f} m {swing:6.1f} deg  {rel}")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())

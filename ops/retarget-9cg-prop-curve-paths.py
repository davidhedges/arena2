#!/usr/bin/env python3
"""Point Studio 9CG prop object-curves at the runtime avatar's actual hierarchy.

The defect
----------
The 9CG player packs animate their weapon prop nodes with generic transform
curves alongside the humanoid muscle curves. Those curves bind by *path*, and the
packs serialize the vendor skeleton's casing:

    root/pelvis/spine_01/spine_02/spine_03/clavicle_r/.../hand_r/Weapon_R

The Arena runtime avatar is the N-Hance Stylized Modular Human, whose first two
bones are capitalised:

    Root/Pelvis/spine_01/spine_02/spine_03/clavicle_r/.../hand_r/Weapon_R

Unity's object-curve binding is case-sensitive (so is `Transform.Find`, which is
how `CombatAnimationSetEditor` validates these). Measured in a batchmode probe on
6000.4.0f1: a curve authored at `root/...` leaves a `Root/...` node untouched.
So every one of those curves silently fails to bind and the prop sits frozen at
whatever pose the mount was calibrated to.

For the mage staff that is the whole bug. `Weapon_R` is calibrated to the pack's
*bind* pose, but the pack's combat clips hold it at bind rolled 34.927 deg about
the socket's local X - which is across the shaft, not along it. 151 of the 253
mage clips Arena uses hold exactly that constant, so the staff renders ~35 deg
tilted in every drawn pose; the other ~100 animate the socket outright (the
Ultimate Attack plants it 1.9 m away) and lose that motion entirely.

What this deliberately does NOT touch
-------------------------------------
* The mount calibrations. They are correct: the socket is authored at the pack's
  bind pose precisely so a bound curve reads as an absolute pose and the N-Hance
  wrapper children ride the delta from bind. Baking the 34.927 deg into
  `ArenaWeaponMountCalibration` would look right today and double-apply the
  moment the curves start binding.
* `Weapon_L`, and every other prop node the packs animate. A path is only
  retargeted when the runtime node it would drive is an Arena animation socket
  *calibrated to that pack's bind pose* (see SOCKETS below). The runtime
  `Weapon_L` is the raw N-Hance off-hand socket, still at N-Hance's own pose and
  shared with dagger_off / bow_drawn; binding the pack's curves to it would snap
  those weapons into the 9CG frame. Every mage clip holds Weapon_L dead at bind
  anyway, so retargeting it is all cost and no gain.

Scope note: run this per pack. Retargeting a pack makes its props live, which is
a visible change for every weapon whose mount hangs off the socket being driven.
"""
from __future__ import annotations

import argparse
import pathlib
import re
import sys

PACK_ROOT = "Assets/Arena/Content/Animation/Extracted"
PACKS = ("DaggersAnimationPack", "ArcherAnimationPack", "MageAnimationPack",
         "GreatSwordAnimations", "SwordAndShieldAnimationPack")

RUNTIME_AVATAR = "Assets/Arena/Resources/PlayerArmature.prefab"

# Prop node -> the pack whose bind pose the runtime socket of that name is
# calibrated to, per ArenaWeaponMountCalibration. Only these are retargeted;
# anything else the packs animate has no Arena-owned socket behind it.
SOCKETS = {
    "MageAnimationPack": ("Weapon_R", "Weapon_Holder"),
    "GreatSwordAnimations": ("weapon_r",),
}

PATH_LINE = re.compile(r"^    path: (\S.*)$")


def path_key(path: str) -> tuple[str, str]:
    ancestors, _, leaf = path.rpartition("/")
    return ancestors.lower(), leaf


def runtime_paths(repo: pathlib.Path) -> dict[tuple[str, str], str]:
    """Every transform path in the runtime avatar, keyed by (lowered ancestors, exact leaf).

    Only the ancestor chain is matched case-insensitively. The leaf has to match
    exactly, because the runtime right hand carries both `Weapon_R` (the mage
    staff socket) and `weapon_r` (the greatsword one) as case-only siblings.
    Keys that still collide are dropped: an ambiguous target is not something to
    guess at.
    """
    text = (repo / RUNTIME_AVATAR).read_text(errors="surrogateescape")
    if text.startswith("version https://git-lfs"):
        raise SystemExit(f"{RUNTIME_AVATAR} is an unsmudged LFS pointer; run `git lfs pull`")

    blocks = {}
    for match in re.finditer(r"^--- !u!(\d+) &(\d+)$", text, re.M):
        end = text.find("\n--- !u!", match.end())
        blocks[int(match.group(2))] = (int(match.group(1)),
                                       text[match.end():end if end > 0 else len(text)])

    names = {}
    for file_id, (class_id, body) in blocks.items():
        if class_id == 1:
            name = re.search(r"^  m_Name: (.*)$", body, re.M)
            if name:
                names[file_id] = name.group(1).strip()

    transforms = {}
    for file_id, (class_id, body) in blocks.items():
        if class_id != 4:
            continue
        game_object = re.search(r"m_GameObject: \{fileID: (\d+)\}", body)
        father = re.search(r"m_Father: \{fileID: (\d+)\}", body)
        transforms[file_id] = (names.get(int(game_object.group(1)), "?") if game_object else "?",
                               int(father.group(1)) if father else 0)

    resolved: dict[tuple[str, str], str] = {}
    collisions: set[tuple[str, str]] = set()
    for file_id in transforms:
        segments = []
        walk = file_id
        while walk in transforms:
            name, parent = transforms[walk]
            segments.append(name)
            walk = parent
        # The Animator sits on the prefab root, so curve paths start below it.
        path = "/".join(reversed(segments[:-1]))
        if not path:
            continue
        key = path_key(path)
        if key in resolved and resolved[key] != path:
            collisions.add(key)
        resolved[key] = path
    for key in collisions:
        resolved.pop(key, None)
    return resolved


def rewrite(path: pathlib.Path, sockets: tuple[str, ...],
            avatar_paths: dict[tuple[str, str], str], apply: bool) -> tuple[int, set[str]]:
    """Retarget this clip's prop paths. Returns (lines changed, unresolved paths)."""
    lines = path.read_text(errors="surrogateescape").split("\n")
    changed = 0
    unresolved: set[str] = set()
    for i, line in enumerate(lines):
        match = PATH_LINE.match(line)
        if not match:
            continue
        curve_path = match.group(1)
        if curve_path.rsplit("/", 1)[-1] not in sockets:
            continue
        target = avatar_paths.get(path_key(curve_path))
        if target is None:
            unresolved.add(curve_path)
            continue
        if target == curve_path:
            continue
        lines[i] = f"    path: {target}"
        changed += 1
    if changed and apply:
        path.write_text("\n".join(lines), errors="surrogateescape")
    return changed, unresolved


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
    avatar_paths = runtime_paths(repo)

    total_scanned = total_touched = 0
    unresolved: set[str] = set()
    for pack in packs:
        root = repo / PACK_ROOT / pack
        if not root.is_dir():
            print(f"missing pack: {pack}", file=sys.stderr)
            return 1
        sockets = SOCKETS.get(pack, ())
        scanned = touched = curves = 0
        for clip in sorted(root.rglob("*.anim")):
            scanned += 1
            if not sockets:
                continue
            changed, missing = rewrite(clip, sockets, avatar_paths, args.apply)
            unresolved |= missing
            if changed:
                touched += 1
                curves += changed
        note = "no Arena animation socket" if not sockets else f"{curves} curve paths"
        print(f"  {pack:28s} {touched:4d} / {scanned:4d} clips   {note}")
        total_scanned += scanned
        total_touched += touched

    for path in sorted(unresolved):
        print(f"  ! no runtime avatar transform matches '{path}'", file=sys.stderr)

    verb = "retargeted" if args.apply else "would retarget"
    print(f"\n{verb} {total_touched} of {total_scanned} clips")
    if not args.apply:
        print("dry run - pass --apply to write")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

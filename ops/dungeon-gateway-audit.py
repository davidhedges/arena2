#!/usr/bin/env python3
"""Audit gateway coverage in the LAST generated dungeon, straight from the scene.

Reads the saved RandomDungeon scene, reconstructs the built geometry from the
object names the generator stamps (floor_/shell_/shell_corner_/partition_/
cliff_/gateway_), finds every one-cell opening that sits in a wall line, and
reports which took a gateway and why the rest did not. It also FAILS on any
gateway standing on fewer than two real wall flanks — a free-standing arch.

The contract it checks (owner rulings, 2026-07-26):
  * both flanks must be real walls — a straight shell or a partition;
  * an angled corner is NOT a flank: the chamfer deletes the two faces it owns
    and spans a diagonal between their far endpoints, so nothing stands on the
    edge itself. Entrances framed by chamfers stay bare on purpose;
  * when the two flanks differ in height, the SHORTER one sets the opening;
  * the smallest door is 4u, so a 2u flank yields no gateway.

This exists so "a gateway is missing" is answered with the built geometry rather
than by reading the generator and guessing. It needs no Unity: run it any time,
including while the editor holds the project lock.

Usage:  ops/dungeon-gateway-audit.py [path/to/scene.unity]
"""
import collections
import os
import re
import sys

SCENE = sys.argv[1] if len(sys.argv) > 1 else (
    "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity")

# Direction bitflags, matching ElevationEdgeModel.Direction.
N, E, S, W = 1, 2, 4, 8
STEP = {N: (0, 1), E: (1, 0), S: (0, -1), W: (-1, 0)}
OPP = {N: S, S: N, E: W, W: E}
LETTER = {N: "N", E: "E", S: "S", W: "W"}
NAMED = {"north": N, "east": E, "south": S, "west": W}
# COMP_Wall_01_M_straight_* nominal course heights, in level units.
COURSE_UNITS = {"large": 6, "med": 4, "small": 2}
ALLOWED_GATEWAY_HEIGHTS = (4, 6, 8, 10, 12)


def guid_to_prefab_name(guids, root="Assets"):
    """Resolve prefab guids to file names via .meta files."""
    found = {}
    for dirpath, _dirs, files in os.walk(root):
        for name in files:
            if not name.endswith(".meta"):
                continue
            try:
                with open(os.path.join(dirpath, name), "r", errors="ignore") as handle:
                    head = handle.read(400)
            except OSError:
                continue
            match = re.search(r"^guid: (\w+)", head, re.M)
            if match and match.group(1) in guids:
                found[match.group(1)] = name[:-len(".meta")]
    return found


def read_instances(scene_path):
    """Yield (name, prefab_guid) for every prefab instance in the scene."""
    with open(scene_path, "r", errors="ignore") as handle:
        text = handle.read()
    docs = re.split(r"^--- !u!(\d+) &(\d+)", text, flags=re.M)
    for index in range(1, len(docs), 3):
        class_id, body = docs[index], docs[index + 2]
        if class_id != "1001":
            continue
        name = None
        for mod in re.finditer(r"propertyPath: (\S+)\n      value: (\S*)\n", body):
            if mod.group(1) == "m_Name":
                name = mod.group(2)
        source = re.search(r"m_SourcePrefab: \{fileID: \d+, guid: (\w+),", body)
        if name:
            yield name, (source.group(1) if source else None)


class Dungeon:
    def __init__(self, instances, prefab_names):
        self.levels = {}
        self.shell = collections.defaultdict(int)
        self.partition = set()
        self.cliff = set()
        self.gateways = {}
        self.corner_cells = set()
        self.stair_cells = set()
        for name, guid in instances:
            self._ingest(name, prefab_names.get(guid, ""))
        self.corner_faces = self._corner_faces()

    def _ingest(self, name, prefab):
        match = re.fullmatch(r"floor_(-?\d+)_(-?\d+)_level_(-?\d+)(?:_round)?", name)
        if match:
            self.levels[(int(match.group(1)), int(match.group(2)))] = int(match.group(3))
            return
        match = re.fullmatch(r"shell_(-?\d+)_(-?\d+)_(\d+)_(\d+)", name)
        if match:
            size = prefab.replace("COMP_Wall_01_M_straight_", "").replace(".prefab", "")
            key = (int(match.group(1)), int(match.group(2)), int(match.group(3)))
            self.shell[key] += COURSE_UNITS.get(size, 0)
            return
        match = re.fullmatch(r"partition_(north|east|south|west)_(-?\d+)_(-?\d+)", name)
        if match:
            self.partition.add(
                (int(match.group(2)), int(match.group(3)), NAMED[match.group(1)]))
            return
        match = re.fullmatch(r"tier_corner_(-?\d+)_(-?\d+)_c\d+", name)
        if match:
            self.corner_cells.add((int(match.group(1)), int(match.group(2))))
            return
        match = re.fullmatch(r"gateway_(\w+?)_(-?\d+)_(-?\d+)_(\d+)", name)
        if match and match.group(1) not in ("header", "bars"):
            self.gateways[
                (int(match.group(2)), int(match.group(3)), int(match.group(4)))
            ] = match.group(1)
            return
        match = re.fullmatch(r"cliff_(north|east|south|west)_(-?\d+)_(-?\d+)_\d+_\d+", name)
        if match:
            self.cliff.add(
                (int(match.group(2)), int(match.group(3)), NAMED[match.group(1)]))
            return
        match = re.match(r"transition_stair_.*?_(\d+)_(\d+)_to_(\d+)_(\d+)_visual", name)
        if match:
            self.stair_cells.add((int(match.group(1)), int(match.group(2))))
            self.stair_cells.add((int(match.group(3)), int(match.group(4))))

    def _corner_faces(self):
        """The two cell faces each tier corner swallowed.

        A convex corner sits on a floor cell and eats its two void-facing sides;
        a concave corner sits in the void notch it sweeps into and eats the two
        sides fronting the floor. Both fall out of the same test: the faces
        whose neighbour differs in level from the corner cell itself.
        """
        faces = collections.defaultdict(set)
        for cell in self.corner_cells:
            level = self.levels.get(cell)
            for direction, (dx, dy) in STEP.items():
                if self.levels.get((cell[0] + dx, cell[1] + dy)) != level:
                    faces[cell].add(direction)
        return faces

    def direction(self, first, second):
        delta = (second[0] - first[0], second[1] - first[1])
        for direction, step in STEP.items():
            if step == delta:
                return direction
        return None

    def wall(self, first, second):
        """What kind of wall element, if any, is associated with this edge."""
        direction = self.direction(first, second)
        back = OPP[direction]
        for cell, facing in ((first, direction), (second, back)):
            key = (cell[0], cell[1], facing)
            if key in self.shell:
                return "shell", self.shell[key]
            if key in self.partition:
                return "partition", 4
        for cell, facing in ((first, direction), (second, back)):
            if cell in self.corner_cells and facing in self.corner_faces[cell]:
                return "tiercorner", None
        for cell, facing in ((first, direction), (second, back)):
            if (cell[0], cell[1], facing) in self.cliff:
                return "cliffonly", None
        return None, None

    def real_flank(self, first, second):
        """A flank a door can actually stand against.

        A chamfer does not qualify — it deletes the faces it owns rather than
        standing on them. Nor does a bare cliff face with no shell.
        """
        kind, height = self.wall(first, second)
        return (kind, height) if kind in ("shell", "partition") else (None, None)

    def flank_support(self, cell, direction):
        """Best of the two flank pairs the generator considers.

        transverse: the wall line the door is a gap in.
        corridor:   the side walls of the cell being entered.
        Returns (real_flank_count, opening_height_or_None).
        """
        dx, dy = STEP[direction]
        outer = (cell[0] + dx, cell[1] + dy)
        tangent = (0, 1) if dx else (1, 0)
        pairs = [
            [((cell[0] + s * tangent[0], cell[1] + s * tangent[1]),
              (outer[0] + s * tangent[0], outer[1] + s * tangent[1]))
             for s in (1, -1)],
            [(outer, (outer[0] + s * tangent[0], outer[1] + s * tangent[1]))
             for s in (1, -1)],
        ]
        best = (0, None)
        for pair in pairs:
            walls = [self.real_flank(*edge) for edge in pair]
            count = sum(1 for kind, _ in walls if kind)
            height = min(h for _, h in walls) if count == 2 else None
            if count > best[0]:
                best = (count, height)
        return best

    def openings(self):
        """Every one-cell gap that sits in a wall line — where a door belongs."""
        found, seen = [], set()
        for first, level in self.levels.items():
            for direction, (dx, dy) in STEP.items():
                second = (first[0] + dx, first[1] + dy)
                if self.levels.get(second) != level:
                    continue
                key = tuple(sorted((first, second)))
                if key in seen:
                    continue
                seen.add(key)
                if self.wall(first, second)[0] in ("shell", "partition"):
                    continue  # closed, not an opening
                tangent = (0, 1) if dx else (1, 0)
                sides = [
                    ((first[0] + s * tangent[0], first[1] + s * tangent[1]),
                     (second[0] + s * tangent[0], second[1] + s * tangent[1]))
                    for s in (1, -1)
                ]
                flanks = [self.wall(*side) for side in sides]
                if any(kind is None for kind, _ in flanks):
                    continue  # open ground, not a wall line
                if all(kind == "cliffonly" for kind, _ in flanks):
                    continue
                found.append((first, second, direction, flanks[0], flanks[1]))
        return sorted(found)

    def verdict(self, first, second, direction, left, right):
        style = (self.gateways.get((first[0], first[1], direction))
                 or self.gateways.get((second[0], second[1], OPP[direction])))
        if style:
            return style, ""
        flanks = [w for w in (left, right) if w[0] in ("shell", "partition")]
        if len(flanks) < 2:
            missing = ("ANGLED CORNER" if "tiercorner" in {left[0], right[0]}
                       else "bare cliff")
            return None, f"correctly skipped: flank is {missing}, not a wall"
        shorter = min(height for _, height in flanks)
        if shorter not in ALLOWED_GATEWAY_HEIGHTS:
            return None, f"correctly skipped: shorter flank {shorter}u is below the 4u minimum"
        reason = "UNEXPECTED — two real flanks, rejected by path/reservation rules"
        if first in self.stair_cells or second in self.stair_cells:
            reason += " [stair cell]"
        return None, reason

    def floating_gateways(self):
        """Gateways standing on fewer than two real flanks — always a bug."""
        bad = []
        for (x, z, direction), style in sorted(self.gateways.items()):
            count, _ = self.flank_support((x, z), direction)
            if count < 2:
                bad.append((f"gateway_{style}_{x}_{z}_{direction}", count))
        return bad


def main():
    if not os.path.exists(SCENE):
        sys.exit(f"no scene at {SCENE}")
    instances = list(read_instances(SCENE))
    prefab_names = guid_to_prefab_name({g for _, g in instances if g})
    dungeon = Dungeon(instances, prefab_names)
    openings = dungeon.openings()

    print(f"scene       {SCENE}")
    print(f"floor cells {len(dungeon.levels)}   angled corners {len(dungeon.corner_cells)}"
          f"   gateways placed {len(dungeon.gateways)}")
    print(f"openings sitting in a wall line: {len(openings)}\n")

    placed, reasons = 0, collections.Counter()
    for first, second, direction, left, right in openings:
        style, reason = dungeon.verdict(first, second, direction, left, right)
        if style:
            placed += 1
        else:
            reasons[reason.split(" [")[0]] += 1
        flank = f"{left[0]}{'' if left[1] is None else f'/{left[1]}u'}"
        flank += f"  {right[0]}{'' if right[1] is None else f'/{right[1]}u'}"
        print(f"  {str(first):>10} -> {str(second):<10} {LETTER[direction]} "
              f"lvl{dungeon.levels[first]:<3} {flank:<28} "
              f"{('GATEWAY ' + style) if style else '-- BARE --':<18} {reason}")

    print(f"\n{placed}/{len(openings)} openings have a gateway")
    for reason, count in reasons.most_common():
        print(f"  {count:>2} bare: {reason}")

    floating = dungeon.floating_gateways()
    unexpected = sum(c for r, c in reasons.items() if r.startswith("UNEXPECTED"))
    print()
    if floating:
        print(f"FAIL: {len(floating)} gateway(s) stand on fewer than two real flanks")
        for name, count in floating:
            print(f"  {name}: {count}/2 flanks")
    else:
        print("PASS: every placed gateway has two real wall flanks")
    if unexpected:
        print(f"WARN: {unexpected} opening(s) with two real flanks took no gateway")
    return 1 if floating else 0


if __name__ == "__main__":
    sys.exit(main())

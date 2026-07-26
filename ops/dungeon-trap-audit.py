#!/usr/bin/env python3
"""Audit trap placement in the LAST generated dungeon.

Reads the exported trap manifest plus the trap profile catalog, and cross-checks
both against the geometry reconstructed from the saved RandomDungeon scene --
the same `floor_x_z_level_L` / `gateway_kind_x_z_dir` naming
`ops/dungeon-gateway-audit.py` relies on.

Reports: trap count and density, kind mix, corridor/room split, and the minimum
distance from any trap to the spawn floor.

FAILS on:
  * a trap whose profile is missing from the catalog;
  * a trap whose world origin disagrees with the cell encoded in its own
    definition id (the manifest and the scene have drifted apart);
  * a hazard sample that leaves the floor -- a saw sweeping through a wall or out
    over the abyss is a placement bug even though it collides with nothing;
  * a trap standing in a gateway cell or one of its neighbours.

Needs no Unity: run it any time, including while the editor holds the lock.

Usage:  ops/dungeon-trap-audit.py [path/to/scene.unity]
"""
import collections
import json
import math
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCENE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    REPO, "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity")
TRAP_MANIFEST = os.path.join(
    REPO, "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.traps.shared.json")
PROFILE_MANIFEST = os.path.join(
    REPO, "Assets/Arena/Resources/SharedData/WorldInteractions/"
          "world_trap_profiles.shared.json")

CELL_SIZE = 4.0
# Sampled at 30 Hz across the hazard window: finer than any authored track and
# the same rate the server ticks at.
HAZARD_SAMPLE_MS = 33
# A sweep sits on the edge its two cells share, so its origin is half a cell off
# the anchor centre.
MAX_ANCHOR_OFFSET = CELL_SIZE / 2.0 + 0.01
NAMED_DIRECTION = {"north": (0, 1), "east": (1, 0), "south": (0, -1), "west": (-1, 0)}
DIRECTION_STEP = {1: (0, 1), 2: (1, 0), 4: (0, -1), 8: (-1, 0)}


def load_json(path):
    with open(path, "r") as handle:
        return json.load(handle)


def read_scene(scene_path):
    """Floor cells and gateway cells, from the names the generator stamps.

    Prefab instances store their name as a modification (`value: floor_...`),
    plain GameObjects as `m_Name:`, so both spellings are scanned.
    """
    with open(scene_path, "r", errors="ignore") as handle:
        text = handle.read()

    floors = {}
    for match in re.finditer(r"floor_(-?\d+)_(-?\d+)_level_(-?\d+)", text):
        floors[(int(match.group(1)), int(match.group(2)))] = int(match.group(3))

    gateways = set()
    for match in re.finditer(r"gateway_(\w+?)_(-?\d+)_(-?\d+)_(\d+)\b", text):
        if match.group(1) in ("header", "bars"):
            continue
        cell = (int(match.group(2)), int(match.group(3)))
        step = DIRECTION_STEP.get(int(match.group(4)), (0, 0))
        gateways.add(cell)
        gateways.add((cell[0] + step[0], cell[1] + step[1]))

    return floors, gateways


def trap_anchor_cell(trap_definition_id):
    """RANDOM_DUNGEON:TRAP:{KIND}:{x}:{z}:{level}"""
    parts = trap_definition_id.split(":")
    if len(parts) != 6:
        return None
    try:
        return int(parts[3]), int(parts[4])
    except ValueError:
        return None


def hazard_offset_at(track, clip_ms):
    if not track:
        return (0.0, 0.0, 0.0)
    first, last = track[0], track[-1]
    if clip_ms <= first["t_ms"]:
        return tuple(first["offset"][axis] for axis in "xyz")
    if clip_ms >= last["t_ms"]:
        return tuple(last["offset"][axis] for axis in "xyz")
    for previous, following in zip(track, track[1:]):
        if clip_ms > following["t_ms"]:
            continue
        span = following["t_ms"] - previous["t_ms"]
        blend = 0.0 if span <= 0 else (clip_ms - previous["t_ms"]) / span
        a, b = previous["offset"], following["offset"]
        return tuple(a[axis] + (b[axis] - a[axis]) * blend for axis in "xyz")
    return tuple(last["offset"][axis] for axis in "xyz")


def rotate_yaw(x, z, yaw_degrees):
    """Unity's Y rotation: local +X -> (cos, -sin), local +Z -> (sin, cos)."""
    radians = math.radians(yaw_degrees)
    cosine, sine = math.cos(radians), math.sin(radians)
    return (x * cosine + z * sine, -x * sine + z * cosine)


def solve_grid_offset(traps):
    """World offset of the cell grid, recovered from the traps themselves.

    Each trap's definition id carries the cell it was placed on and the manifest
    carries the post-recenter world origin, so the offset is exact; a trap that
    disagrees is drift between the scene and the manifest.
    """
    votes = collections.Counter()
    for trap in traps:
        cell = trap_anchor_cell(trap["trap_definition_id"])
        if cell is None:
            continue
        votes[(round(trap["origin"]["x"] - cell[0] * CELL_SIZE, 3),
               round(trap["origin"]["z"] - cell[1] * CELL_SIZE, 3))] += 1
    if not votes:
        return None
    return votes.most_common(1)[0][0]


def cell_for_world(x, z, offset):
    return (int(math.floor((x - offset[0]) / CELL_SIZE + 0.5)),
            int(math.floor((z - offset[1]) / CELL_SIZE + 0.5)))


def main():
    for path in (TRAP_MANIFEST, PROFILE_MANIFEST):
        if not os.path.exists(path):
            print(f"MISSING {os.path.relpath(path, REPO)}")
            return 2

    traps = load_json(TRAP_MANIFEST)["traps"]
    profiles = {p["profile_id"]: p for p in load_json(PROFILE_MANIFEST)["profiles"]}
    floors, gateway_cells = read_scene(SCENE)
    if not floors:
        print(f"MISSING floor cells in {os.path.relpath(SCENE, REPO)}")
        return 2

    print(f"scene            {os.path.relpath(SCENE, REPO)}")
    print(f"floor cells      {len(floors)}")
    if not traps:
        print("traps            0 (trap pass disabled, or the scene predates it)")
        return 0

    offset = solve_grid_offset(traps)
    if offset is None:
        print("FAIL: no trap carries a parseable cell in its definition id")
        return 1

    failures = []
    kind_counts = collections.Counter()
    corridor = room = 0
    min_spawn = float("inf")

    # "Room-ish" = floor on all four sides. The room graph is not recorded in
    # the built scene, so this is the circulation proxy the audit can actually
    # compute; a corridor cell almost always has at least one wall side.
    interior = {cell for cell in floors
                if all((cell[0] + dx, cell[1] + dz) in floors
                       for dx, dz in ((0, 1), (0, -1), (1, 0), (-1, 0)))}

    for trap in traps:
        trap_id = trap["trap_definition_id"]
        profile = profiles.get(trap["trap_profile_id"])
        if profile is None:
            failures.append(f"{trap_id}: unknown profile '{trap['trap_profile_id']}'")
            continue
        kind_counts[trap["trap_profile_id"]] += 1

        origin, yaw = trap["origin"], trap["yaw_degrees"]
        min_spawn = min(min_spawn, math.hypot(origin["x"], origin["z"]))

        anchor = trap_anchor_cell(trap_id)
        if anchor is not None:
            drift = math.hypot(origin["x"] - (anchor[0] * CELL_SIZE + offset[0]),
                               origin["z"] - (anchor[1] * CELL_SIZE + offset[1]))
            if drift > MAX_ANCHOR_OFFSET:
                failures.append(
                    f"{trap_id}: origin is {drift:.2f} u from the cell in its own id")
            if anchor in gateway_cells:
                failures.append(f"{trap_id}: stands in a gateway cell")

        cell = cell_for_world(origin["x"], origin["z"], offset)
        if cell in interior:
            room += 1
        else:
            corridor += 1

        hazard = profile["hazard_volume"]
        clip_ms = profile["hazard_start_ms"]
        while clip_ms <= profile["hazard_end_ms"]:
            track_offset = hazard_offset_at(profile["hazard_track"], clip_ms)
            world_x, world_z = rotate_yaw(
                hazard["center"]["x"] + track_offset[0],
                hazard["center"]["z"] + track_offset[2],
                yaw)
            sample = cell_for_world(origin["x"] + world_x, origin["z"] + world_z, offset)
            if sample not in floors:
                failures.append(
                    f"{trap_id}: hazard leaves the floor at t={clip_ms} ms (cell {sample})")
                break
            clip_ms += HAZARD_SAMPLE_MS

    total = len(traps)
    print(f"traps            {total}  (1 per {len(floors) / total:.1f} floor cells)")
    print("kind mix         "
          + ", ".join(f"{kind} {count} ({count * 100 // total}%)"
                      for kind, count in sorted(kind_counts.items())))
    print(f"circulation      corridor-ish {corridor}, room-ish {room}")
    print(f"min dist spawn   {min_spawn:.2f} u")
    print(f"grid offset      ({offset[0]:.2f}, {offset[1]:.2f})")

    if failures:
        print(f"\nFAIL: {len(failures)} problem(s)")
        for failure in failures[:20]:
            print("  " + failure)
        return 1

    print("\nOK")
    return 0


if __name__ == "__main__":
    sys.exit(main())

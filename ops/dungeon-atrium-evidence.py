#!/usr/bin/env python3
"""Phase D exit criteria for the atrium topology, read off the seed reports.

The layered-topology design (§13 Phase D) asks for three things, and none of
them is a hash:

  * density 0-5 all >= 199/200 accepted with the atrium in the mix;
  * no OPEN_VOLUME_VIOLATION;
  * the atrium's declared entrances bind at >= 2 distinct elevations on >= 90%
    of its seeds.

The third is the one worth automating carefully. "Entrances bind at two
elevations" is not a property of the topology file — the file merely DECLARES a
binding — it is a property of what the generator resolved, so it is read from
each seed's route intent, per seed, and counted.

Usage:  ops/dungeon-atrium-evidence.py DungeonLabReports/d5_atrium_density*.json
"""

import json
import re
import sys

ATRIUM_RECIPE = "episode_atrium_hub_01"


def slot_node_id(report):
    """The route node the atrium recipe was placed at, or None."""
    for slot in (report.get("routeIntent") or {}).get("recipeSlots", []):
        if slot.get("id") == ATRIUM_RECIPE:
            return slot.get("routeNodeId"), slot
    return None, None


def entrance_levels(report, node_id):
    """Every absolute elevation an edge incident on the atrium arrives at.

    An unbound edge end carries no absolute level in the projection (that is
    conditional, for the hash reason D1 recorded), so it resolves to the node's
    own declared level — which is exactly what "unbound means the base" means.
    """
    intent = report.get("routeIntent") or {}
    base = None
    for node in intent.get("nodes", []):
        if node.get("id") == node_id:
            base = node.get("relativeElevationLevels")
    levels = set()
    for edge in (intent.get("graph") or {}).get("traversalEdges", []):
        if edge.get("fromNode") == node_id:
            levels.add(edge.get("fromAbsoluteLevel", base))
        elif edge.get("toNode") == node_id:
            levels.add(edge.get("toAbsoluteLevel", base))
    return {lvl for lvl in levels if lvl is not None}


def density_of(path):
    match = re.search(r"density(\d+)", path)
    return match.group(1) if match else path


def main(paths):
    print(f"{'density':>8}  {'accepted':>10}  {'atrium':>7}  {'>=2 elev':>9}  "
          f"{'openVol':>8}  {'violations':>10}  elevations")
    ok = True
    for path in paths:
        report = json.load(open(path))
        seeds = report["seeds"]
        accepted = report["accepted"]
        total = report["seedCount"]
        placed = 0
        two_elevations = 0
        open_volume_cells = 0
        violations = 0
        seen = {}
        for seed in seeds:
            if not seed.get("accepted"):
                continue
            node_id, _slot = slot_node_id(seed)
            if node_id is None:
                continue
            placed += 1
            levels = entrance_levels(seed, node_id)
            if len(levels) >= 2:
                two_elevations += 1
            seen[tuple(sorted(levels))] = seen.get(tuple(sorted(levels)), 0) + 1
            open_volume_cells += (seed.get("tieredLevelPlan") or {}).get("openVolumeCells", 0)
            for check in (seed.get("validation") or {}).get("checks", []):
                if check.get("code") == "OPEN_VOLUME_VIOLATION" and not check.get("passed"):
                    violations += 1

        pct = (100.0 * two_elevations / placed) if placed else 0.0
        shape = "  ".join(f"{list(k)}x{v}" for k, v in sorted(seen.items()))
        print(f"{density_of(path):>8}  {accepted:>4}/{total:<5}  {placed:>7}  "
              f"{pct:>8.1f}%  {open_volume_cells:>8}  {violations:>10}  {shape}")

        if accepted < total - 1:
            ok = False
            print(f"           FAIL: accepted {accepted}/{total} is below 199/200")
        if violations:
            ok = False
            print(f"           FAIL: {violations} OPEN_VOLUME_VIOLATION")
        if placed and pct < 90.0:
            ok = False
            print(f"           FAIL: only {pct:.1f}% of atrium seeds bound two elevations")
        if placed == 0:
            ok = False
            print("           FAIL: the atrium was never placed")

    print()
    print("EXIT CRITERIA: " + ("MET" if ok else "NOT MET"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

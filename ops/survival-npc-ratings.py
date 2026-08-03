#!/usr/bin/env python3
"""Derive survival-mode threat ratings for every NPC template.

The rating is computed from data the catalogs already carry, so it never needs
hand-authoring and never goes stale when templates are added or retuned. See
docs/survival-mode-design-2026-08-03.md §4.

    rating = 100 * (hp/hp_med)^0.5
                 * (dps/dps_med)^0.65
                 * (1 + 0.35*(speed - speed_med)/speed_med)
                 * (1.15 if effective range > 5m else 1.0)

`dps` is the best single action in the kit, using the SAME cadence the server
executes (action-entry windup/recovery overrides, not template defaults).

KNOWN MODELLING GAP: the runtime planner picks by utility, distance, health and
cooldown gates, so an NPC does not always use its highest-dps action. This is a
deliberate upper bound on sustained threat, not a simulation. Treat the rating
as an ordering over the roster, not a damage prediction.

Usage:
    ops/survival-npc-ratings.py                 # tier table
    ops/survival-npc-ratings.py --all           # every template, ranked
    ops/survival-npc-ratings.py --json out.json # machine-readable roster
"""

import argparse
import json
import statistics
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
NPC_CATALOG = REPO / "server/src/npc_catalog.shared.json"
PROGRESSION_CATALOG = REPO / "server/src/progression_catalog.shared.json"

MIN_CYCLE_MS = 400

RANGED_THRESHOLD_M = 5.0
RANGED_BONUS = 1.15
HP_EXPONENT = 0.5
DPS_EXPONENT = 0.65
SPEED_WEIGHT = 0.35

# Quintile cuts, recomputed and reported below so drift is visible.
TIER_NAMES = ["I", "II", "III", "IV", "V"]


def entry_cadence_ms(entry, template):
    """Mirror npc_action_windup_ms / npc_action_recovery_ms in npcs.rs.

    The action-kit entry overrides the template default whenever it is nonzero;
    264 of 299 current entries override windup. Using the template value here
    would mis-rate almost the whole roster.
    """
    windup = entry.get("windup_ms") or 0
    recovery = entry.get("recovery_ms") or 0
    if windup == 0:
        windup = template.get("attack_windup_ms") or 0
    if recovery == 0:
        recovery = template.get("attack_recovery_ms") or 0
    return windup, recovery


def ability_profile(ability, entry, template):
    """Return (dps, effective_range) for one authored ability."""
    gameplay = ability.get("gameplay", {})
    kind = gameplay.get("kind")
    cooldown_ms = gameplay.get("cooldown_ms") or 0

    if kind == "MELEE":
        damage = gameplay.get("base_damage") or 0
        windup, recovery = entry_cadence_ms(entry, template)
        cycle = max(cooldown_ms, windup + recovery, MIN_CYCLE_MS)
        return damage / (cycle / 1000.0), gameplay.get("range") or 0.0

    if kind == "SPELL":
        delivery = gameplay.get("delivery") or {}
        damage = delivery.get("damage") or 0
        cast_time = gameplay.get("cast_time_ms") or 0
        # Runtime stamps the movement hold at cast end + npc_action_recovery_ms
        # (npcs.rs:1671) — the same entry-then-template fallback melee uses.
        # Not a fixed constant.
        _, recovery = entry_cadence_ms(entry, template)
        cycle = max(cooldown_ms, cast_time + recovery, MIN_CYCLE_MS)
        return damage / (cycle / 1000.0), delivery.get("max_distance") or 0.0

    # PASSIVE / COMBAT_MODE_TOGGLE / AUTO_ATTACK_REPLACEMENT contribute no dps.
    return 0.0, 0.0


def build_roster():
    npc_catalog = json.loads(NPC_CATALOG.read_text())
    progression = json.loads(PROGRESSION_CATALOG.read_text())
    abilities = {a["ability_id"]: a for a in progression["abilities"]}

    roster = []
    for template in npc_catalog["templates"]:
        best_dps = 0.0
        max_range = 0.0
        missing = []
        for entry in template["action_kit"]:
            ability = abilities.get(entry["ability_id"])
            if ability is None:
                missing.append(entry["ability_id"])
                continue
            dps, rng = ability_profile(ability, entry, template)
            best_dps = max(best_dps, dps)
            max_range = max(max_range, rng)

        roster.append(
            {
                "template_id": template["template_id"],
                "species_id": template["species_id"],
                "max_hp": template["max_hp"],
                "dps": best_dps,
                "move_speed": template.get("move_speed", 5.0),
                "range": max_range,
                "ranged": max_range > RANGED_THRESHOLD_M,
                "brain_profile_id": template["brain_profile_id"],
                "missing_abilities": missing,
            }
        )
    return roster


def rate(roster):
    hp_med = statistics.median(r["max_hp"] for r in roster)
    dps_med = statistics.median(r["dps"] for r in roster)
    speed_med = statistics.median(r["move_speed"] for r in roster)

    for r in roster:
        if dps_med <= 0 or hp_med <= 0 or speed_med <= 0:
            raise SystemExit("catalog medians are degenerate; cannot rate roster")
        hp_factor = (r["max_hp"] / hp_med) ** HP_EXPONENT
        dps_factor = (r["dps"] / dps_med) ** DPS_EXPONENT if r["dps"] > 0 else 0.0
        speed_factor = 1 + SPEED_WEIGHT * (r["move_speed"] - speed_med) / speed_med
        ranged_factor = RANGED_BONUS if r["ranged"] else 1.0
        r["rating"] = round(100 * hp_factor * dps_factor * speed_factor * ranged_factor, 1)
        r["gold_value"] = max(1, -(-int(r["rating"] * 0.35 * 10) // 10))

    roster.sort(key=lambda r: r["rating"])
    return {"hp": hp_med, "dps": dps_med, "speed": speed_med}


def tier_cuts(roster):
    return [roster[int(len(roster) * f)]["rating"] for f in (0.2, 0.4, 0.6, 0.8)]


def assign_tiers(roster, cuts):
    for r in roster:
        tier = 0
        for cut in cuts:
            if r["rating"] >= cut:
                tier += 1
        r["tier"] = TIER_NAMES[tier]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--all", action="store_true", help="print every template, ranked")
    parser.add_argument("--json", metavar="PATH", help="write the rated roster as JSON")
    args = parser.parse_args()

    roster = build_roster()
    medians = rate(roster)
    cuts = tier_cuts(roster)
    assign_tiers(roster, cuts)

    zero_dps = [r["template_id"] for r in roster if r["dps"] == 0]
    unresolved = [r for r in roster if r["missing_abilities"]]

    print(f"{len(roster)} templates rated")
    print(f"medians: hp={medians['hp']:.0f} dps={medians['dps']:.2f} speed={medians['speed']:.1f}")
    print(f"spread:  {roster[0]['rating']:.0f} .. {roster[-1]['rating']:.0f} "
          f"({roster[-1]['rating'] / roster[0]['rating']:.1f}x)")
    print(f"tier cuts: {[round(c) for c in cuts]}\n")

    if args.all:
        for r in roster:
            print(f"{r['rating']:7.1f}  {r['tier']:<3} {r['template_id']:<34} "
                  f"hp={r['max_hp']:<4} dps={r['dps']:5.1f} spd={r['move_speed']:<4} "
                  f"ranged={int(r['ranged'])} gold={r['gold_value']}")
    else:
        for tier in TIER_NAMES:
            members = [r for r in roster if r["tier"] == tier]
            if not members:
                continue
            examples = ", ".join(f"{m['template_id']}({m['rating']:.0f})" for m in members[:3])
            print(f"Tier {tier:<3} {members[0]['rating']:5.0f}-{members[-1]['rating']:<5.0f} "
                  f"n={len(members):<3} {examples}")

    if zero_dps:
        print(f"\nWARNING: {len(zero_dps)} template(s) rate 0 dps: {', '.join(zero_dps)}")
    if unresolved:
        print(f"\nWARNING: {len(unresolved)} template(s) reference unknown abilities:")
        for r in unresolved:
            print(f"  {r['template_id']}: {', '.join(r['missing_abilities'])}")

    if args.json:
        Path(args.json).write_text(json.dumps({"medians": medians, "tier_cuts": cuts, "roster": roster}, indent=2))
        print(f"\nwrote {args.json}")

    return 1 if (zero_dps or unresolved) else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Generate the compact runtime Combat Build v2 catalog from its reviewed ledger."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CONTRACT = REPO_ROOT / "docs/combat-build-v2-phase-0-contract-2026-08-29.json"
DEFAULT_PROGRESSION = REPO_ROOT / "server/src/progression_catalog.shared.json"
DEFAULT_OUTPUT = REPO_ROOT / "server/src/combat_build_v2_catalog.shared.json"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--contract", type=Path, default=DEFAULT_CONTRACT)
    parser.add_argument("--progression", type=Path, default=DEFAULT_PROGRESSION)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true")
    return parser.parse_args()


def relative(path: Path) -> str:
    return str(path.resolve().relative_to(REPO_ROOT))


def make_catalog(contract_path: Path, progression_path: Path) -> dict[str, Any]:
    contract_bytes = contract_path.read_bytes()
    contract = json.loads(contract_bytes)
    progression = json.loads(progression_path.read_text())
    progression_abilities = {
        row["ability_id"]: row for row in progression["abilities"]
    }
    if len(progression_abilities) != len(progression["abilities"]):
        raise ValueError("progression contains duplicate ability ids")

    classified = contract["feature_classification"]
    grouped: dict[str, dict[str, list[str]]] = {}
    for specialization in contract["specializations"]:
        specialization_id = specialization["specialization_id"]
        if specialization_id in grouped:
            raise ValueError(f"duplicate specialization {specialization_id}")
        grouped[specialization_id] = {
            "TECHNIQUE": [],
            "SPELL": [],
            "PERK": [],
        }
    classified_ids: set[str] = set()
    for row in classified:
        ability_id = row["ability_id"]
        if ability_id not in progression_abilities:
            raise ValueError(f"classified ability {ability_id} is absent from progression")
        if ability_id in classified_ids:
            raise ValueError(f"ability {ability_id} is classified more than once")
        classified_ids.add(ability_id)
        specialization_id = row["proposed_specialization_id"]
        if specialization_id not in grouped:
            raise ValueError(f"ability {ability_id} has unknown specialization {specialization_id}")
        loadout_kind = row["loadout_kind"]
        if loadout_kind not in grouped[specialization_id]:
            raise ValueError(f"ability {ability_id} has unsupported loadout kind {loadout_kind}")
        grouped[specialization_id][loadout_kind].append(ability_id)

    selectable_ids = {
        ability_id
        for ability_id, row in progression_abilities.items()
        if row["actor_scope"] == "PLAYER"
        and row["selection_kind"] in {"ACTIVE", "PASSIVE"}
    }
    missing = sorted(selectable_ids - classified_ids)
    unexpected = sorted(classified_ids - selectable_ids)
    if missing or unexpected:
        raise ValueError(
            "classification must cover exactly the selectable player abilities: "
            f"missing={missing}, unexpected={unexpected}"
        )

    specializations = []
    for row in contract["specializations"]:
        specialization_id = row["specialization_id"]
        features = grouped[specialization_id]
        for ability_ids in features.values():
            for ability_id in ability_ids:
                authored = progression_abilities[ability_id].get("combat_discipline_id")
                expected = row["combat_discipline_id"]
                if authored != expected:
                    raise ValueError(
                        f"ability {ability_id} discipline {authored!r} disagrees with "
                        f"{specialization_id} parent {expected!r}"
                    )
        specializations.append(
            {
                **row,
                # The ledger owns membership and order within each feature kind.
                # Progression sort_order is separate metadata and must not
                # silently reorder a reviewed build catalog during regeneration.
                "technique_ability_ids": features["TECHNIQUE"],
                "spell_ability_ids": features["SPELL"],
                "perk_ability_ids": features["PERK"],
            }
        )

    traits = []
    for index, row in enumerate(contract["trait_catalog"]):
        traits.append(
            {
                "ability_id": row["trait_id"],
                "display_name": row["display_name"],
                "loadout_kind": "TRAIT",
                "sort_order": (index + 1) * 10,
                "effect_kind": "SINGLE_PARENT_OUTGOING_DAMAGE_MULTIPLIER",
                "modifier_scalar": row["damage_bonus"],
                "condition": row["condition"],
                "damage_scope": row["damage_scope"],
                "excludes": row["excludes"],
            }
        )

    decisions = contract["decisions"]
    return {
        "schema_version": 2,
        "source_contract": relative(contract_path),
        "source_contract_sha256": hashlib.sha256(contract_bytes).hexdigest(),
        "rules": {
            "minimum_selected_specializations": 1,
            "maximum_selected_specializations": decisions["maximum_specializations"],
            "global_feature_capacity": decisions["global_feature_capacity"],
            "trait_capacity": decisions["trait_capacity"],
            "default_starting_discipline": "selected_specializations[0].parent_discipline",
            "direct_action_input_ids": [
                binding["input_action_id"]
                for binding in contract["input_contract"]["bindings"]
            ],
        },
        "specializations": specializations,
        "traits": traits,
        "intrinsic_abilities": contract["intrinsic_ledger"],
        "removed_player_abilities": contract["removal_ledger"],
        "default_build": contract["reset_ledger"]["default_seed"],
    }


def main() -> int:
    args = parse_args()
    catalog = make_catalog(args.contract.resolve(), args.progression.resolve())
    rendered = json.dumps(catalog, indent=2, sort_keys=True) + "\n"
    if args.check:
        if not args.output.exists() or args.output.read_text() != rendered:
            raise SystemExit(f"{args.output} is stale; rerun {Path(__file__).name}")
        print(f"Combat Build v2 catalog is current: {args.output}")
        return 0
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(rendered)
    feature_count = sum(
        len(row[f"{kind.lower()}_ability_ids"])
        for row in catalog["specializations"]
        for kind in ("TECHNIQUE", "SPELL", "PERK")
    )
    print(
        f"Wrote {args.output}: {len(catalog['specializations'])} specializations, "
        f"{feature_count} selectable features, {len(catalog['traits'])} traits"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

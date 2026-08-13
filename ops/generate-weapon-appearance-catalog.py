#!/usr/bin/env python3
"""Generate the shared Arena weapon-family and color-variant catalog."""

from __future__ import annotations

import argparse
import json
import re
from collections import defaultdict
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
WEAPON_ROOT = REPO_ROOT / "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Weapon"
DEFAULT_OUTPUT = REPO_ROOT / "Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json"

COLOR_SPECS = {
    "Default": ("Default", "#A9A9A9"),
    "Bk": ("Black", "#25272B"),
    "Bl": ("Blue", "#3E78C5"),
    "Br": ("Brown", "#765038"),
    "Cl": ("Classic", "#B69A6A"),
    "Cn": ("Cyan", "#39B9C8"),
    "Gn": ("Green", "#4D9958"),
    "Go": ("Gold", "#D5A936"),
    "Gr": ("Gray", "#858991"),
    "Or": ("Orange", "#D87935"),
    "Pe": ("Purple", "#8159AA"),
    "Rd": ("Red", "#B84949"),
    "Wh": ("White", "#E4E2DC"),
    "Ye": ("Yellow", "#D7C746"),
}
COLOR_ORDER = ("Default", "Cl", "Bk", "Bl", "Br", "Cn", "Gn", "Go", "Gr", "Or", "Pe", "Rd", "Wh", "Ye")
COLOR_SORT = {color_id: index for index, color_id in enumerate(COLOR_ORDER)}

DISCIPLINE_ORDER = {"SUBTLETY": 0, "WAR": 1, "ZEAL": 2, "PRECISION": 3}
KIND_ORDER = {
    "DAGGER_PAIR": 0,
    "TWO_HAND_SWORD": 0,
    "TWO_HAND_AXE": 1,
    "TWO_HAND_HAMMER": 2,
    "POLEARM": 3,
    "ONE_HAND_SWORD": 0,
    "ONE_HAND_AXE": 1,
    "ONE_HAND_HAMMER": 2,
    "ONE_HAND_FIST": 3,
    "SHIELD": 4,
    "BOW": 0,
}
ICON_BY_KIND = {
    "DAGGER_PAIR": "training_dagger_pair",
    "TWO_HAND_SWORD": "training_two_hand_sword",
    "TWO_HAND_AXE": "newbie_two_hand_axe_01",
    "TWO_HAND_HAMMER": "training_two_hand_sword",
    "POLEARM": "training_two_hand_sword",
    "ONE_HAND_SWORD": "training_one_hand_sword",
    "ONE_HAND_AXE": "newbie_one_hand_axe_02",
    "ONE_HAND_HAMMER": "training_one_hand_sword",
    "ONE_HAND_FIST": "training_one_hand_sword",
    "SHIELD": "training_shield",
    "BOW": "training_bow",
}

# These item ids predate the appearance picker and are already used by
# inventories, starter loadouts, combat tests, and authored Unity visuals.
# Reuse them as the canonical ids for the matching N-Hance model families
# instead of publishing visually duplicate NH_* choices.
LEGACY_FAMILY_ALIASES = {
    "Dagger_1H_Newbie_01": ("NEWBIE_DAGGER_PAIR_01", "Newbie Daggers I"),
    "Dagger_1H_Newbie_02": ("NEWBIE_DAGGER_PAIR_02", "Newbie Daggers II"),
    "Dagger_1H_Newbie_03": ("NEWBIE_DAGGER_PAIR_03", "Newbie Daggers III"),
    "Sword_2H_Newbie_01": ("NEWBIE_TWO_HAND_SWORD_01", "Newbie Two-Handed Sword I"),
    "Sword_2H_Newbie_02": ("NEWBIE_TWO_HAND_SWORD_02", "Newbie Two-Handed Sword II"),
    "Axe_2HL_Newbie_01": ("NEWBIE_TWO_HAND_AXE_01", "Newbie Two-Handed Axe I"),
    "Sword_1H_Newbie_01": ("NEWBIE_ONE_HAND_SWORD_01", "Newbie One-Handed Sword I"),
    "Sword_1H_Newbie_02": ("NEWBIE_ONE_HAND_SWORD_02", "Newbie One-Handed Sword II"),
    "Axe_1H_Newbie_02": ("NEWBIE_ONE_HAND_AXE_02", "Newbie One-Handed Axe II"),
    "Axe_1H_Newbie_03": ("NEWBIE_ONE_HAND_AXE_03", "Newbie One-Handed Axe III"),
    "Shield_Newbie_01": ("NEWBIE_SHIELD_01", "Newbie Shield I"),
    "Shield_Newbie_02": ("NEWBIE_SHIELD_02", "Newbie Shield II"),
    "Shield_Newbie_03": ("NEWBIE_SHIELD_03", "Newbie Shield III"),
    "Bow_Newbie_01": ("NEWBIE_BOW_01", "Newbie Bow I"),
    "Bow_Newbie_02": ("NEWBIE_BOW_02", "Newbie Bow II"),
    "Bow_Newbie_03": ("NEWBIE_BOW_03", "Newbie Bow III"),
}

TRAINING_FAMILIES = (
    {
        "item_def_id": "TRAINING_DAGGER_PAIR",
        "display_name": "Training Daggers",
        "icon_id": "training_dagger_pair",
        "weapon_kind": "DAGGER_PAIR",
        "hand_requirement": "TWO_HAND",
        "equip_slot": "MAIN_HAND",
        "primary_discipline_id": "SUBTLETY",
        "sort_order": 0,
        "default_color_id": "DEFAULT",
        "variants": [{
            "color_id": "DEFAULT",
            "prefab_path": "Assets/Arena/Resources/CombatAnimationSets/DaggerMainPackAuthored.prefab",
            "off_hand_prefab_path": "Assets/Arena/Resources/CombatAnimationSets/DaggerOffPackAuthored.prefab",
        }],
    },
    {
        "item_def_id": "TRAINING_TWO_HAND_SWORD",
        "display_name": "Training Two-Handed Sword",
        "icon_id": "training_two_hand_sword",
        "weapon_kind": "TWO_HAND_SWORD",
        "hand_requirement": "TWO_HAND",
        "equip_slot": "MAIN_HAND",
        "primary_discipline_id": "WAR",
        "sort_order": 0,
        "default_color_id": "DEFAULT",
        "variants": [{
            "color_id": "DEFAULT",
            "prefab_path": "Assets/Arena/Resources/CombatAnimationSets/GreatSwordPackAuthored.prefab",
        }],
    },
    {
        "item_def_id": "TRAINING_ONE_HAND_SWORD",
        "display_name": "Training One-Handed Sword",
        "icon_id": "training_one_hand_sword",
        "weapon_kind": "ONE_HAND_SWORD",
        "hand_requirement": "ONE_HAND",
        "equip_slot": "MAIN_HAND",
        "primary_discipline_id": "ZEAL",
        "sort_order": 0,
        "default_color_id": "DEFAULT",
        "variants": [{
            "color_id": "DEFAULT",
            "prefab_path": "Assets/Arena/Resources/CombatAnimationSets/SwordPackAuthored.prefab",
        }],
    },
    {
        "item_def_id": "TRAINING_SHIELD",
        "display_name": "Training Shield",
        "icon_id": "training_shield",
        "weapon_kind": "SHIELD",
        "hand_requirement": "OFF_HAND",
        "equip_slot": "OFF_HAND",
        "primary_discipline_id": "ZEAL",
        "sort_order": 0,
        "default_color_id": "DEFAULT",
        "variants": [{
            "color_id": "DEFAULT",
            "prefab_path": "Assets/Arena/Resources/CombatAnimationSets/ShieldPackAuthored.prefab",
        }],
    },
    {
        "item_def_id": "TRAINING_BOW",
        "display_name": "Training Bow",
        "icon_id": "training_bow",
        "weapon_kind": "BOW",
        "hand_requirement": "TWO_HAND",
        "equip_slot": "MAIN_HAND",
        "primary_discipline_id": "PRECISION",
        "sort_order": 0,
        "default_color_id": "DEFAULT",
        "variants": [{
            "color_id": "DEFAULT",
            "prefab_path": "Assets/Arena/Resources/CombatAnimationSets/ArcherBowDrawnPackAuthored.prefab",
            "stowed_prefab_path": "Assets/Arena/Resources/CombatAnimationSets/ArcherBowStowedPackAuthored.prefab",
            "quiver_prefab_path": "Assets/Arena/Resources/CombatAnimationSets/ArcherQuiverPackAuthored.prefab",
        }],
    },
)

LEGACY_SELECTABLE_IDS = {
    *(item_def_id for item_def_id, _ in LEGACY_FAMILY_ALIASES.values()),
    *(family["item_def_id"] for family in TRAINING_FAMILIES),
}


def weapon_contract(stem: str) -> tuple[str, str, str, str] | None:
    if "_Off_" in stem:
        return None
    if stem.startswith("Dagger_1H_"):
        return "SUBTLETY", "DAGGER_PAIR", "TWO_HAND", "MAIN_HAND"
    if stem.startswith(("Axe_2H_", "Axe_2HL_")):
        return "WAR", "TWO_HAND_AXE", "TWO_HAND", "MAIN_HAND"
    if stem.startswith("Sword_2H_"):
        return "WAR", "TWO_HAND_SWORD", "TWO_HAND", "MAIN_HAND"
    if stem.startswith("Hammer_2H_"):
        return "WAR", "TWO_HAND_HAMMER", "TWO_HAND", "MAIN_HAND"
    if stem.startswith("Polearm_"):
        return "WAR", "POLEARM", "TWO_HAND", "MAIN_HAND"
    if stem.startswith("Axe_1H_"):
        return "ZEAL", "ONE_HAND_AXE", "ONE_HAND", "MAIN_HAND"
    if stem.startswith("Sword_1H_"):
        return "ZEAL", "ONE_HAND_SWORD", "ONE_HAND", "MAIN_HAND"
    if stem.startswith("Hammer_1H_"):
        return "ZEAL", "ONE_HAND_HAMMER", "ONE_HAND", "MAIN_HAND"
    if stem.startswith("Fist_1H_"):
        return "ZEAL", "ONE_HAND_FIST", "ONE_HAND", "MAIN_HAND"
    if stem.startswith("Shield_"):
        return "ZEAL", "SHIELD", "OFF_HAND", "OFF_HAND"
    if stem.startswith("Bow_"):
        return "PRECISION", "BOW", "TWO_HAND", "MAIN_HAND"
    return None


def roman(value: int) -> str:
    if value <= 0 or value > 39:
        return str(value)
    parts: list[str] = []
    for amount, glyph in ((10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")):
        while value >= amount:
            value -= amount
            parts.append(glyph)
    return "".join(parts)


def display_name(family_stem: str, weapon_kind: str) -> str:
    prefixes = ("Dagger_1H_", "Axe_2HL_", "Axe_2H_", "Axe_1H_", "Sword_2H_", "Sword_1H_", "Hammer_2H_", "Hammer_1H_", "Fist_1H_", "Polearm_", "Shield_", "Bow_")
    core = next((family_stem[len(prefix):] for prefix in prefixes if family_stem.startswith(prefix)), family_stem)
    words: list[str] = []
    aliases = {
        "DK": "Death Knight",
        "Dung": "Dungeon",
        "NArcher": "Northern Archer",
        "NRanger": "Northern Ranger",
        "NWarrior": "Northern Warrior",
        "Ud": "Undead",
    }
    for token in core.split("_"):
        if token.isdigit():
            words.append(roman(int(token)))
        else:
            words.append(aliases.get(token, re.sub(r"(?<=[a-z])(?=[A-Z])", " ", token)))
    kind_label = {
        "DAGGER_PAIR": "Daggers",
        "TWO_HAND_SWORD": "Two-Handed Sword",
        "TWO_HAND_AXE": "Two-Handed Axe",
        "TWO_HAND_HAMMER": "Two-Handed Hammer",
        "POLEARM": "Polearm",
        "ONE_HAND_SWORD": "One-Handed Sword",
        "ONE_HAND_AXE": "One-Handed Axe",
        "ONE_HAND_HAMMER": "One-Handed Hammer",
        "ONE_HAND_FIST": "Fist Weapon",
        "SHIELD": "Shield",
        "BOW": "Bow",
    }[weapon_kind]
    return f"{' '.join(words)} {kind_label}".strip()


def asset_path(path: Path) -> str:
    return path.relative_to(REPO_ROOT).as_posix()


def build_catalog() -> dict[str, object]:
    if not WEAPON_ROOT.is_dir():
        raise SystemExit(f"N-Hance weapon root was not found: {WEAPON_ROOT}")

    grouped: dict[tuple[str, str, str, str, str], list[tuple[str, Path]]] = defaultdict(list)
    for prefab in sorted(WEAPON_ROOT.rglob("*.prefab")):
        split = prefab.stem.rsplit("_", 1)
        if len(split) != 2 or split[1] not in COLOR_SPECS:
            continue
        family_stem, color_id = split
        contract = weapon_contract(prefab.stem)
        if contract is None:
            continue
        discipline_id, weapon_kind, hand_requirement, equip_slot = contract
        grouped[(discipline_id, weapon_kind, hand_requirement, equip_slot, family_stem)].append((color_id, prefab))

    families: list[dict[str, object]] = []
    family_keys = sorted(
        grouped,
        key=lambda key: (DISCIPLINE_ORDER[key[0]], 1 if key[3] == "OFF_HAND" else 0, KIND_ORDER[key[1]], key[4]),
    )
    for sort_order, key in enumerate(family_keys, start=1):
        discipline_id, weapon_kind, hand_requirement, equip_slot, family_stem = key
        variants: list[dict[str, str]] = []
        for color_id, prefab in sorted(grouped[key], key=lambda row: COLOR_SORT[row[0]]):
            variant = {"color_id": color_id.upper(), "prefab_path": asset_path(prefab)}
            if weapon_kind == "DAGGER_PAIR":
                off_path = prefab.with_name(f"{family_stem}_Off_{color_id}.prefab")
                variant["off_hand_prefab_path"] = asset_path(off_path if off_path.is_file() else prefab)
            variants.append(variant)
        default_variant = variants[0]
        item_def_id, authored_display_name = LEGACY_FAMILY_ALIASES.get(
            family_stem,
            (f"NH_{family_stem.upper()}", display_name(family_stem, weapon_kind)),
        )
        families.append(
            {
                "item_def_id": item_def_id,
                "display_name": authored_display_name,
                "icon_id": ICON_BY_KIND[weapon_kind],
                "weapon_kind": weapon_kind,
                "hand_requirement": hand_requirement,
                "equip_slot": equip_slot,
                "primary_discipline_id": discipline_id,
                "sort_order": sort_order,
                "default_color_id": default_variant["color_id"],
                "variants": variants,
            }
        )

    nhance_variant_count = sum(len(family["variants"]) for family in families)
    if len(families) != 121 or nhance_variant_count != 382:
        raise SystemExit(
            f"Unexpected compatible N-Hance catalog shape: {len(families)} families, {nhance_variant_count} variants"
        )

    # Keep established training weapons first as the deterministic fallback for
    # existing players whose older loadout has no appearance selection yet.
    families[0:0] = TRAINING_FAMILIES
    family_ids = {str(family["item_def_id"]) for family in families}
    if len(family_ids) != len(families):
        raise SystemExit("Weapon appearance catalog contains duplicate item definition ids")
    if not LEGACY_SELECTABLE_IDS.issubset(family_ids):
        missing = sorted(LEGACY_SELECTABLE_IDS - family_ids)
        raise SystemExit(f"Weapon appearance catalog omitted legacy selectable ids: {missing}")
    variant_count = sum(len(family["variants"]) for family in families)
    if len(families) != 126 or variant_count != 387:
        raise SystemExit(f"Unexpected combined weapon catalog shape: {len(families)} families, {variant_count} variants")

    represented_prefabs = {
        variant[path_key]
        for family in families
        for variant in family["variants"]
        for path_key in ("prefab_path", "off_hand_prefab_path")
        if path_key in variant
    }
    omitted_compatible_prefabs = [
        asset_path(prefab)
        for prefab in sorted(WEAPON_ROOT.rglob("*.prefab"))
        if weapon_contract(prefab.stem) is not None
        and asset_path(prefab) not in represented_prefabs
    ]
    if omitted_compatible_prefabs:
        raise SystemExit(
            "Compatible installed weapon prefabs were omitted from the catalog:\n"
            + "\n".join(omitted_compatible_prefabs)
        )

    used_colors = {variant["color_id"] for family in families for variant in family["variants"]}
    colors = [
        {"color_id": color_id.upper(), "display_name": COLOR_SPECS[color_id][0], "hex": COLOR_SPECS[color_id][1]}
        for color_id in COLOR_ORDER
        if color_id.upper() in used_colors
    ]
    return {"schema_version": 1, "colors": colors, "families": families}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true", help="fail if the checked-in catalog is stale")
    args = parser.parse_args()
    rendered = json.dumps(build_catalog(), indent=2) + "\n"
    if args.check:
        actual = args.output.read_text(encoding="utf-8") if args.output.is_file() else ""
        if actual != rendered:
            raise SystemExit(f"Weapon appearance catalog is stale: run {Path(__file__).relative_to(REPO_ROOT)}")
        return
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(rendered, encoding="utf-8")


if __name__ == "__main__":
    main()

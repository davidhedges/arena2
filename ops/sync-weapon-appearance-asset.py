#!/usr/bin/env python3
"""Sync JSON-authored weapon variants into the checked-in Unity catalog asset."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
CATALOG_JSON = REPO_ROOT / "Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json"
UNITY_ASSET = REPO_ROOT / "Assets/Arena/Resources/CharacterAppearance/EquipmentAppearanceCatalog.asset"
GENERATED_ENTRY_PREFIX = "  - itemDefId: NH_"
LEGACY_GENERATED_MARKER = "  # BEGIN GENERATED WEAPON APPEARANCES\n"
GENERATED_ENTRY_SENTINEL = (
    "  - itemDefId: TRAINING_DAGGER_PAIR\n"
    "    colorId: DEFAULT\n"
)
QUIVER_PATH = REPO_ROOT / "Assets/Arena/Resources/CombatAnimationSets/ArcherQuiverPackAuthored.prefab"
NHANCE_WEAPON_ROOT = REPO_ROOT / "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Weapon"
PLACEMENT_PROFILE_VALUES = {
    "LEGACY_ANIMATION_BINDING": 0,
    "NHANCE_NATIVE": 1,
}


def prefab_reference(asset_path: str | Path) -> tuple[str, str]:
    path = Path(asset_path)
    if not path.is_absolute():
        path = REPO_ROOT / path
    meta = path.with_suffix(path.suffix + ".meta").read_text(encoding="utf-8")
    guid_match = re.search(r"^guid:\s*([0-9a-f]+)\s*$", meta, re.MULTILINE)
    if guid_match is None:
        raise SystemExit(f"Prefab meta has no GUID: {path}.meta")

    yaml = path.read_text(encoding="utf-8")
    root_game_object: str | None = None
    for match in re.finditer(r"^--- !u!4 &(-?\d+)\n(.*?)(?=^--- !u!|\Z)", yaml, re.MULTILINE | re.DOTALL):
        block = match.group(2)
        if "m_Father: {fileID: 0}" not in block:
            continue
        game_object_match = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", block)
        if game_object_match is not None:
            root_game_object = game_object_match.group(1)
            break
    if root_game_object is None:
        stripped_root = re.search(r"^--- !u!1 &(-?\d+) stripped\nGameObject:", yaml, re.MULTILINE)
        if stripped_root is not None:
            root_game_object = stripped_root.group(1)
    if root_game_object is None:
        raise SystemExit(f"Prefab has no root GameObject: {path}")
    return root_game_object, guid_match.group(1)


def visual_roles(family: dict[str, object], variant: dict[str, str]) -> list[tuple[str, str, str]]:
    kind = str(family["weapon_kind"])
    prefab_path = variant["prefab_path"]
    placement_profile = str(family["placement_profile_id"])
    if kind == "DAGGER_PAIR":
        return [
            ("dagger_main", prefab_path, placement_profile),
            ("dagger_off", variant["off_hand_prefab_path"], placement_profile),
        ]
    if kind in {"TWO_HAND_SWORD", "TWO_HAND_AXE", "TWO_HAND_HAMMER", "POLEARM"}:
        return [("greatsword", prefab_path, placement_profile)]
    if kind in {"ONE_HAND_SWORD", "ONE_HAND_AXE", "ONE_HAND_HAMMER", "ONE_HAND_FIST"}:
        return [("sword", prefab_path, placement_profile)]
    if kind == "SHIELD":
        return [("shield", prefab_path, placement_profile)]
    if kind == "BOW":
        return [
            ("bow_drawn", prefab_path, placement_profile),
            ("bow_stowed", variant.get("stowed_prefab_path", prefab_path), placement_profile),
            (
                "quiver",
                variant.get("quiver_prefab_path", str(QUIVER_PATH)),
                variant["quiver_placement_profile_id"],
            ),
        ]
    if kind == "STAFF":
        return [("staff", prefab_path, placement_profile)]
    raise SystemExit(f"Unsupported weapon kind in shared catalog: {kind}")


def generated_yaml() -> str:
    catalog = json.loads(CATALOG_JSON.read_text(encoding="utf-8"))
    lines: list[str] = []
    reference_cache: dict[str, tuple[str, str]] = {}
    for family in catalog["families"]:
        for variant in family["variants"]:
            for role_id, path, placement_profile in visual_roles(family, variant):
                if placement_profile not in PLACEMENT_PROFILE_VALUES:
                    raise SystemExit(
                        f"Unsupported placement profile {placement_profile!r} for "
                        f"{family['item_def_id']}/{variant['color_id']}/{role_id}"
                    )
                if path not in reference_cache:
                    reference_cache[path] = prefab_reference(path)
                file_id, guid = reference_cache[path]
                lines.extend(
                    (
                        f"  - itemDefId: {family['item_def_id']}",
                        f"    colorId: {variant['color_id']}",
                        f"    visualRoleId: {role_id}",
                        "    raceId: HUMAN",
                        "    sexId: MALE",
                        "    enabled: 1",
                        f"    prefab: {{fileID: {file_id}, guid: {guid}, type: 3}}",
                        f"    placementProfile: {PLACEMENT_PROFILE_VALUES[placement_profile]}",
                    )
                )
    return "\n".join(lines) + "\n"


def nhance_weapon_prefab_guids() -> set[str]:
    guids: set[str] = set()
    for meta_path in NHANCE_WEAPON_ROOT.rglob("*.prefab.meta"):
        match = re.search(r"^guid:\s*([0-9a-f]+)\s*$", meta_path.read_text(encoding="utf-8"), re.MULTILINE)
        if match is None:
            raise SystemExit(f"Prefab meta has no GUID: {meta_path}")
        guids.add(match.group(1))
    return guids


def sync_authored_placement_profiles(prefix: str) -> str:
    native_prefab_guids = nhance_weapon_prefab_guids()

    def replace_entry(match: re.Match[str]) -> str:
        entry = match.group(0)
        prefab_match = re.search(r"^    prefab: \{.*guid: ([0-9a-f]+),.*\}$", entry, re.MULTILINE)
        if prefab_match is None:
            return entry

        entry = re.sub(r"^    placementProfile: .*\n", "", entry, flags=re.MULTILINE)
        if prefab_match.group(1) not in native_prefab_guids:
            return entry

        return re.sub(
            r"(^    prefab: .*\n)",
            rf"\1    placementProfile: {PLACEMENT_PROFILE_VALUES['NHANCE_NATIVE']}\n",
            entry,
            count=1,
            flags=re.MULTILINE,
        )

    return re.sub(
        r"^  - itemDefId: .*?(?=^  - itemDefId: |\Z)",
        replace_entry,
        prefix,
        flags=re.MULTILINE | re.DOTALL,
    )


def expected_asset() -> str:
    existing = UNITY_ASSET.read_text(encoding="utf-8")
    generated_index = existing.find(LEGACY_GENERATED_MARKER)
    if generated_index < 0:
        generated_index = existing.find(GENERATED_ENTRY_SENTINEL)
    if generated_index < 0:
        generated_index = existing.find(GENERATED_ENTRY_PREFIX)
    prefix = existing[:generated_index] if generated_index >= 0 else existing
    prefix = sync_authored_placement_profiles(prefix)
    if not prefix.endswith("\n"):
        prefix += "\n"
    # Unity's NativeFormatImporter does not reliably accept a comment between
    # serialized list elements. Keep the generated boundary implicit and use
    # the first fully-authored entry above as the regeneration sentinel.
    return prefix + generated_yaml()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    rendered = expected_asset()
    if args.check:
        if UNITY_ASSET.read_text(encoding="utf-8") != rendered:
            raise SystemExit(f"Weapon visuals are stale: run {Path(__file__).relative_to(REPO_ROOT)}")
        return
    UNITY_ASSET.write_text(rendered, encoding="utf-8")


if __name__ == "__main__":
    main()

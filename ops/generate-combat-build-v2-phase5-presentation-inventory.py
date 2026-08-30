#!/usr/bin/env python3
"""Generate/check the Combat Build v2 Phase 5 presentation compatibility audit."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
PHASE0 = REPO_ROOT / "docs" / "combat-build-v2-phase-0-contract-2026-08-29.json"
OUTPUT = (
    REPO_ROOT
    / "docs"
    / "combat-build-v2-phase-5-presentation-inventory-2026-08-29.json"
)

SOURCE_ANCHORS = [
    (
        "SERVER_PRESENTATION_DISCOVERY_BY_EXECUTOR",
        "server/src/progression.rs",
        '.filter(|ability| ability_gameplay_kind(ability) == "SPELL")',
    ),
    (
        "GLOBAL_SPELL_MAP_FIRST",
        "Assets/Arena/Runtime/Presentation/Animation/SpellCastAnimationResolver.cs",
        "if (!TryGetMapEntry(normalizedSpellId, out SpellCastAnimationMap.Entry mapEntry))",
    ),
    (
        "EQUIPPED_DISCIPLINE_OVERRIDE",
        "Assets/Arena/Runtime/Presentation/Animation/SpellCastAnimationResolver.cs",
        "ApplyCombatSetOverride(set, normalizedSpellId, ref entry);",
    ),
    (
        "AUTHORITATIVE_CAST_FIZZLE",
        "server/src/spells/casting.rs",
        "pub(crate) fn fizzle_active_cast_for_interrupt(",
    ),
    (
        "AUTO_ATTACK_CLEAR_PRIMITIVE",
        "server/src/auto_attack.rs",
        "pub(crate) fn clear_auto_attack_for_owner(",
    ),
    (
        "LOCAL_MOVEMENT_CANCEL_REQUEST",
        "Assets/Arena/Runtime/Input/LocalPlayerMotor.cs",
        "conn.Reducers.CancelActiveCastRequest(",
    ),
    (
        "IMMEDIATE_CLIENT_CANCEL_PHASE",
        "Assets/Arena/Runtime/Presentation/SpellCastPresentationController.cs",
        "CombatSpellAnimationPhase.Cancel,",
    ),
    (
        "CANCEL_CLEARS_TEMPORARY_PROP",
        "Assets/Arena/Runtime/Presentation/PlayerAnimator.cs",
        "_weaponAttachments?.ReleaseTemporaryAnimatedProp(request.ActionId);",
    ),
    (
        "REHEARSAL_DISTINCT_PARENT_SWITCH_TARGETS",
        "match-v2-rehearsal/src/switching.rs",
        "seen.insert(selected.combat_discipline_id.as_str())",
    ),
    (
        "REHEARSAL_INTERRUPT_MATRIX",
        "match-v2-rehearsal/src/switching.rs",
        "pub(crate) const ALL: [Self; 14]",
    ),
]

INTERRUPT_MATRIX = [
    "MOVEMENT",
    "JUMP",
    "DISCIPLINE_SWITCH",
    "TECHNIQUE",
    "SPELL",
    "DODGE",
    "BLOCK",
    "PARRY",
    "INTERACT",
    "FIXED_COMBAT_ACTION",
    "AUTO_ATTACK_START",
    "STAGGER",
    "KNOCKBACK",
    "DEATH",
]


def source_anchor(anchor_id: str, relative_path: str, needle: str) -> dict[str, Any]:
    path = REPO_ROOT / relative_path
    text = path.read_text(encoding="utf-8")
    offset = text.find(needle)
    if offset < 0:
        raise RuntimeError(f"missing Phase 5 source anchor {anchor_id}: {relative_path}: {needle}")
    return {
        "anchor_id": anchor_id,
        "path": relative_path,
        "line": text.count("\n", 0, offset) + 1,
        "needle": needle,
        "source_sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(),
    }


def compatibility_rows(contract: dict[str, Any]) -> list[dict[str, Any]]:
    profiles = contract["animation_compatibility"]["combat_animation_profiles"]
    expected_profiles = set(profiles)
    rows: list[dict[str, Any]] = []
    for feature in contract["feature_classification"]:
        loadout_kind = feature["loadout_kind"]
        if loadout_kind == "SPELL":
            requirement = "ALL_EQUIPPABLE_DISCIPLINES"
            expected = profiles
            if set(feature["animation_compatibility_profiles"]) != expected_profiles:
                raise RuntimeError(
                    f"semantic Spell {feature['ability_id']} lacks all-Discipline coverage"
                )
        elif (
            loadout_kind == "TECHNIQUE"
            and feature["presentation_discovery"] == "SPELL_EXECUTOR"
        ):
            requirement = "ONE_PARENT_DISCIPLINE"
            expected = [feature["proposed_parent_discipline_id"]]
            if feature["animation_compatibility_profiles"] != expected:
                raise RuntimeError(
                    f"spell-executor Technique {feature['ability_id']} escaped its parent profile"
                )
        else:
            continue
        rows.append(
            {
                "ability_id": feature["ability_id"],
                "action_id": feature["action_id"],
                "gameplay_kind": feature["gameplay_kind"],
                "loadout_kind": loadout_kind,
                "presentation_discovery": feature["presentation_discovery"],
                "compatibility_requirement": requirement,
                "required_profiles": expected,
                "hold_release_cancel_required": loadout_kind == "SPELL",
            }
        )
    return sorted(rows, key=lambda row: row["ability_id"])


def blessed_shield_disposition(contract: dict[str, Any]) -> dict[str, Any]:
    feature = next(
        row
        for row in contract["feature_classification"]
        if row["ability_id"] == "PALADIN_BLESSED_SHIELD"
    )
    expected = {
        "loadout_kind": "TECHNIQUE",
        "gameplay_kind": "SPELL",
        "proposed_parent_discipline_id": "SWORD_AND_SHIELD",
        "animation_compatibility_profiles": ["SWORD_AND_SHIELD"],
    }
    for key, value in expected.items():
        if feature[key] != value:
            raise RuntimeError(f"Blessed Shield disposition diverged at {key}")
    return {
        "ability_id": feature["ability_id"],
        "action_id": feature["action_id"],
        "classification": "WEAPON_GATED_TECHNIQUE_USING_SPELL_EXECUTOR",
        "required_equipped_discipline": "SWORD_AND_SHIELD",
        "temporary_prop_policy": (
            "The existing CombatSpellAnimationPhase.Cancel path releases the action-owned "
            "temporary shield before the new equipped Discipline presentation is applied."
        ),
        "weapon_independent_spell_conversion": False,
    }


def staff_disposition(contract: dict[str, Any]) -> dict[str, Any]:
    removals = {
        row["ability_id"]: row
        for row in contract["removal_ledger"]
        if row["ability_id"].startswith("STAFF_")
    }
    expected_ids = {"STAFF_STRIKE", "STAFF_STRIKE_2", "STAFF_SWEEP", "STAFF_THRUST"}
    if set(removals) != expected_ids:
        raise RuntimeError("Staff Technique removal ledger is incomplete")
    return {
        "removed_player_ability_ids": sorted(removals),
        "selectable_or_intrinsic_technique_count": 0,
        "ordinary_auto_attack": "RETAINED",
        "private_presentation_exception": "STAFF_STRIKE_2 clip/action data only; grants no feature authorization",
    }


def build_inventory() -> dict[str, Any]:
    contract = json.loads(PHASE0.read_text(encoding="utf-8"))
    rows = compatibility_rows(contract)
    semantic_spell_count = sum(row["loadout_kind"] == "SPELL" for row in rows)
    spell_executor_technique_count = sum(
        row["loadout_kind"] == "TECHNIQUE" for row in rows
    )
    phase0_animation = contract["animation_compatibility"]
    if semantic_spell_count != phase0_animation["semantic_spell_count"]:
        raise RuntimeError("semantic Spell coverage count diverged from Phase 0")
    if spell_executor_technique_count != phase0_animation["spell_executor_technique_count"]:
        raise RuntimeError("spell-executor Technique coverage count diverged from Phase 0")

    return {
        "schema_version": 1,
        "scope": "COMBAT_BUILD_V2_SWITCH_INTERRUPT_PRESENTATION_COMPATIBILITY",
        "presentation_discovery_source": "gameplay.kind",
        "compatibility_scope_source": "loadout_kind",
        "combat_animation_profiles": phase0_animation["combat_animation_profiles"],
        "semantic_spell_count": semantic_spell_count,
        "spell_executor_technique_count": spell_executor_technique_count,
        "compatibility_rows": rows,
        "interrupt_matrix": [
            {
                "accepted_action": action,
                "authoritative_result": "ACTIVE_CAST_FIZZLE",
                "client_result": "COMBAT_SPELL_ANIMATION_CANCEL",
                "postcondition": "NO_ACTIVE_HOLD_OR_TEMPORARY_PROP",
            }
            for action in INTERRUPT_MATRIX
        ],
        "source_anchors": [source_anchor(*row) for row in SOURCE_ANCHORS],
        "blessed_shield": blessed_shield_disposition(contract),
        "staff": staff_disposition(contract),
        "architecture_boundary": {
            "global_map": "Assets/Arena/Resources/SpellCastAnimationMap.asset",
            "shared_recipe_catalog": "Assets/Arena/Runtime/Presentation/Animation/SpellCastAnimationCatalog.cs",
            "override_scope": "CURRENT_EQUIPPED_COMBAT_ANIMATION_SET",
            "form_or_school_override": False,
            "new_animator_controller_or_topology": False,
            "result": "NO_ANIMATION_ARCHITECTURE_EXPANSION_REQUIRED",
        },
        "cutover_note": (
            "Phase 5 proves the normalized policy in the disposable match. Phase 7 connects "
            "the inventoried canonical action entry points to this one policy atomically."
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    rendered = json.dumps(build_inventory(), indent=2, sort_keys=True) + "\n"
    if args.check:
        if not OUTPUT.is_file() or OUTPUT.read_text(encoding="utf-8") != rendered:
            raise SystemExit(f"Combat Build v2 Phase 5 inventory is stale: {OUTPUT}")
        print(f"Combat Build v2 Phase 5 inventory is current: {OUTPUT}")
        return 0
    OUTPUT.write_text(rendered, encoding="utf-8")
    print(f"Wrote Combat Build v2 Phase 5 inventory: {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

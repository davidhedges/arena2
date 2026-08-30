#!/usr/bin/env python3
"""Generate and validate the reviewed Combat Build v2 Phase 0 contract ledger."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CATALOG = REPO_ROOT / "server/src/progression_catalog.shared.json"
DEFAULT_BASELINE = REPO_ROOT / "Library/ArenaLocalMultiplayer/combat-build-v2.before.json"
DEFAULT_OUTPUT = REPO_ROOT / "docs/combat-build-v2-phase-0-contract-2026-08-29.json"

BASELINE_EVIDENCE = {
    "ignored_snapshot_path": "Library/ArenaLocalMultiplayer/combat-build-v2.before.json",
    "sha256": "9c9b6864142859c5b305c96e8270f72341508dc85a9c6cc63bc88a57ceaa3af5",
    "row_counts": {
        "combat_build": 8,
        "combat_build_discipline": 12,
        "discipline_action_bar_assignment": 14,
        "discipline_configuration": 12,
        "discipline_passive_selection": 2,
        "hub_player": 8,
        "hub_player_armor_selection": 8,
        "staff_school_selection": 4,
    },
}

ANIMATION_PROFILES = [
    "DAGGERS",
    "TWO_HANDED_SWORD",
    "SWORD_AND_SHIELD",
    "ARCHER_BOW",
    "STAFF",
]

FORMS = [
    ("DAGGERS_BLADEDANCER", "DAGGERS", "Bladedancer", 10),
    ("DAGGERS_EXECUTIONER", "DAGGERS", "Executioner", 20),
    ("DAGGERS_SHADOW", "DAGGERS", "Shadow", 30),
    ("TWO_HANDED_SWORD_VANGUARD", "TWO_HANDED_SWORD", "Vanguard", 10),
    ("TWO_HANDED_SWORD_REAVER", "TWO_HANDED_SWORD", "Reaver", 20),
    ("TWO_HANDED_SWORD_BERSERKER", "TWO_HANDED_SWORD", "Berserker", 30),
    ("SWORD_AND_SHIELD_GUARDIAN", "SWORD_AND_SHIELD", "Guardian", 10),
    ("SWORD_AND_SHIELD_VINDICATOR", "SWORD_AND_SHIELD", "Vindicator", 20),
    ("SWORD_AND_SHIELD_TEMPLAR", "SWORD_AND_SHIELD", "Templar", 30),
    ("ARCHER_BOW_MARKSMAN", "ARCHER_BOW", "Marksman", 10),
    ("ARCHER_BOW_SKIRMISHER", "ARCHER_BOW", "Skirmisher", 20),
    ("ARCHER_BOW_VOLLEY", "ARCHER_BOW", "Volley", 30),
]

FORM_FEATURES = {
    "DAGGERS_BLADEDANCER": {
        "DAGGER_QUICK_CUT",
        "DAGGER_SLICE",
        "DAGGER_DASHING_CUT",
        "DAGGER_ROUNDHOUSE",
        "DAGGER_SPINNING_SLASH",
        "DAGGER_BLADE_FLURRY",
        "DAGGER_DEADLY_FLOURISH",
        "DAGGER_PURSUE",
        "DAGGER_DOWNWARD_SLASH",
        "DAGGER_DIVING_STRIKE",
        "DAGGER_LIGHTNING_REFLEXES",
        "SUBTLETY_FLEET_FOOTED",
    },
    "DAGGERS_EXECUTIONER": {
        "DAGGER_GUT_RIPPER",
        "DAGGER_NERVE_STRIKE",
        "DAGGER_COUP_DE_GRACE",
        "DAGGER_PRECISION_STRIKE",
        "DAGGER_EVISCERATE",
        "DAGGER_VITAL_STRIKE",
        "DAGGER_DEATH_CROSS",
        "DAGGER_DISEMBOWEL",
        "DAGGER_FLAY",
        "DAGGER_BLADE_TWISTING",
        "SUBTLETY_SURPRISE_ATTACKS",
    },
    "DAGGERS_SHADOW": {
        "DAGGER_STEALTH",
        "DAGGER_FIND_WEAKNESS",
        "DAGGER_DISARM",
        "DAGGER_GOUGE",
        "DAGGER_TEMPLE_STRIKE",
        "DAGGER_DARKNESS",
        "DAGGER_STALK",
        "DAGGER_SHADOWREND",
        "SUBTLETY_OPPORTUNIST",
        "SUBTLETY_TACTICAL_ADVANTAGE",
        "SUBTLETY_LINGERING_SHADE",
    },
    "TWO_HANDED_SWORD_VANGUARD": {
        "WARRIOR_HEW",
        "WARRIOR_MAIM",
        "WARRIOR_GROUND_TO_AIR_PLACEHOLDER",
        "WARRIOR_CRUSHING_BLOW",
        "WARRIOR_SUNDER",
        "WARRIOR_CARVE",
        "WARRIOR_GROUND_SLASH",
        "WARRIOR_FORTIFY",
        "WARRIOR_IRON_WILL",
        "WARRIOR_CHARGE",
        "WARRIOR_IMPALE",
        "WARRIOR_DISENGAGE_STRIKE",
    },
    "TWO_HANDED_SWORD_REAVER": {
        "WARRIOR_CATACLYSM",
        "WARRIOR_BUZZSAW",
        "WARRIOR_WHIRLWIND",
        "WARRIOR_CLEAVE",
        "WARRIOR_BUTCHER",
        "WARRIOR_TENDERIZE",
        "WARRIOR_DREAD_STRIKE",
        "WARRIOR_EARTHSHATTER",
        "WARRIOR_SHOCKWAVE",
        "WARRIOR_INTIMIDATE",
    },
    "TWO_HANDED_SWORD_BERSERKER": {
        "WARRIOR_MOMENTUM",
        "WARRIOR_DEFIANCE",
        "WARRIOR_BATTLE_CRY",
        "WARRIOR_FRENZY",
        "WARRIOR_ENRAGE",
        "WARRIOR_SECOND_WIND",
        "WARRIOR_BERSERKING",
        "WARRIOR_BATTLE_TRANCE",
        "WARRIOR_FEAST",
        "WARRIOR_RESTLESS",
        "WARRIOR_BLOODLUST",
    },
    "SWORD_AND_SHIELD_GUARDIAN": {
        "PALADIN_SHIELD_PUMMEL",
        "PALADIN_GUARDIAN_RUSH",
        "PALADIN_RETRIBUTION",
        "PALADIN_CHARGE",
        "PALADIN_AVENGE",
        "PALADIN_AIR_TO_GROUND_3",
        "PALADIN_BLESSED_SHIELD",
        "PALADIN_THORNS_AURA",
        "PALADIN_WARDING_AURA",
    },
    "SWORD_AND_SHIELD_VINDICATOR": {
        "PALADIN_VINDICATOR_SLASH",
        "PALADIN_REBUKE",
        "PALADIN_HALLOWED_THRUST",
        "PALADIN_SACRED_THRUST",
        "PALADIN_SERRATED_BLADES",
        "PALADIN_AIR_TO_GROUND_1",
        "PALADIN_CONSECRATE",
        "PALADIN_AURA_OF_VENGEANCE",
        "PALADIN_RADIANT_BURST",
        "PALADIN_SACRED_FLAME",
    },
    "SWORD_AND_SHIELD_TEMPLAR": {
        "PALADIN_CLEANSING_TOUCH",
        "PALADIN_ABSOLUTION",
        "PALADIN_FERVOR",
        "PALADIN_MANA_FONT",
        "PALADIN_STAMINA_FONT",
        "PALADIN_BLADE_BARRIER",
    },
    "ARCHER_BOW_MARKSMAN": {
        "ARCHER_POWER_SHOT",
        "ARCHER_HEARTSEEKER",
        "ARCHER_DRAW_MODE_TOGGLE",
        "ARCHER_CAREFUL_AIM",
    },
    "ARCHER_BOW_SKIRMISHER": {
        "ARCHER_BACKSTEP",
        "ARCHER_DISENGAGE",
        "ARCHER_EVASIVE_SHOT",
        "ARCHER_MAVERICK",
        "ARCHER_POINT_BLANK",
    },
    "ARCHER_BOW_VOLLEY": {
        "ARCHER_TRIPLE_SHOT",
        "ARCHER_DOUBLE_SHOT",
        "ARCHER_RAIN_OF_ARROWS",
        "ARCHER_RAPID_FIRE",
        "ARCHER_PERFORATION",
    },
}

FORM_OWNED_SPELLS = {
    "DAGGER_DARKNESS",
    "DAGGER_STALK",
    "DAGGER_SHADOWREND",
    "PALADIN_CONSECRATE",
    "PALADIN_CLEANSING_TOUCH",
    "PALADIN_ABSOLUTION",
    "PALADIN_FERVOR",
    "PALADIN_MANA_FONT",
    "PALADIN_STAMINA_FONT",
    "PALADIN_THORNS_AURA",
    "PALADIN_WARDING_AURA",
    "PALADIN_AURA_OF_VENGEANCE",
    "PALADIN_BLADE_BARRIER",
    "PALADIN_RADIANT_BURST",
    "PALADIN_SACRED_FLAME",
}

REMOVED_PLAYER_ABILITIES = {
    "STAFF_STRIKE": "Remove selectable Staff melee Technique.",
    "STAFF_STRIKE_2": "Remove intrinsic Staff melee combo follow-up; retain only private clip/action data needed by ordinary Staff autoattack presentation.",
    "STAFF_SWEEP": "Remove selectable Staff melee Technique.",
    "STAFF_THRUST": "Remove selectable Staff melee Technique.",
}

INTRINSIC_DISPOSITIONS = {
    "WARRIOR_HEW_2": "Retain only as a private TWO_HANDED_SWORD combo follow-up.",
    "WARRIOR_AIR_TO_GROUND_PLACEHOLDER": "Retain only as a private TWO_HANDED_SWORD combo follow-up.",
    "WARRIOR_SKYFALL_4": "Retain only as a private TWO_HANDED_SWORD combo follow-up.",
    "DAGGER_TRIP": "Retain only as a private DAGGERS combo follow-up.",
    "DAGGER_STALK_SHADOWSTEP": "Retain only as the private continuation of the DAGGERS_SHADOW Spell DAGGER_STALK.",
}

DIRECT_INPUTS = [
    ("COMBAT_ACTION_00", "1", "Alpha1", False),
    ("COMBAT_ACTION_01", "2", "Alpha2", False),
    ("COMBAT_ACTION_02", "3", "Alpha3", False),
    ("COMBAT_ACTION_03", "4", "Alpha4", False),
    ("COMBAT_ACTION_04", "5", "Alpha5", False),
    ("COMBAT_ACTION_05", "6", "Alpha6", False),
    ("COMBAT_ACTION_06", "7", "Alpha7", False),
    ("COMBAT_ACTION_07", "8", "Alpha8", False),
    ("COMBAT_ACTION_08", "9", "Alpha9", False),
    ("COMBAT_ACTION_09", "0", "Alpha0", False),
    ("COMBAT_ACTION_10", "E", "E", False),
    ("COMBAT_ACTION_11", "R", "R", False),
    ("COMBAT_ACTION_12", "T", "T", False),
    ("COMBAT_ACTION_13", "F", "F", False),
    ("COMBAT_ACTION_14", "G", "G", False),
    ("COMBAT_ACTION_15", "Z", "Z", False),
    ("COMBAT_ACTION_16", "X", "X", False),
    ("COMBAT_ACTION_17", "C", "C", False),
]

FIXTURES = [
    ("VALID_SINGLE_FORM", True, None),
    ("VALID_THREE_SAME_PARENT_FORMS", True, None),
    ("VALID_THREE_SCHOOLS", True, None),
    ("VALID_MIXED_FORM_SCHOOL", True, None),
    ("VALID_EIGHTEEN_TECHNIQUES_ONE_PARENT", True, None),
    ("VALID_EIGHTEEN_SPELLS", True, None),
    ("VALID_DORMANT_ORDER_REFLOW", True, None),
    ("VALID_MASTERY_ONE_PARENT_THREE_FORMS", True, None),
    ("INVALID_SCHEMA_VERSION", False, "SCHEMA_VERSION"),
    ("INVALID_ZERO_SPECIALIZATIONS", False, "SPECIALIZATION_COUNT"),
    ("INVALID_FOUR_SPECIALIZATIONS", False, "SPECIALIZATION_COUNT"),
    ("INVALID_NONCONTIGUOUS_SPECIALIZATION_SLOTS", False, "SPECIALIZATION_SLOTS"),
    ("INVALID_DUPLICATE_SPECIALIZATION", False, "DUPLICATE_SPECIALIZATION"),
    ("INVALID_UNKNOWN_SPECIALIZATION", False, "UNKNOWN_SPECIALIZATION"),
    ("INVALID_STARTING_DISCIPLINE", False, "STARTING_DISCIPLINE"),
    ("INVALID_MISSING_DISCIPLINE_CONFIGURATION", False, "MISSING_CONFIGURATION"),
    ("INVALID_WEAPON_CONFIGURATION", False, "WEAPON_CONFIGURATION"),
    ("INVALID_EMPTY_SPECIALIZATION", False, "EMPTY_SPECIALIZATION"),
    ("INVALID_FEATURE_OWNER", False, "FEATURE_OWNER"),
    ("INVALID_FEATURE_KIND", False, "FEATURE_KIND"),
    ("INVALID_STAFF_TECHNIQUE", False, "STAFF_TECHNIQUE"),
    ("INVALID_NINETEEN_FEATURES", False, "FEATURE_CAPACITY"),
    ("INVALID_FOUR_TRAITS", False, "TRAIT_CAPACITY"),
    ("INVALID_TRAIT_SATISFIES_NONEMPTY", False, "EMPTY_SPECIALIZATION"),
    ("INVALID_PERK_BAR_ORDER", False, "PASSIVE_BAR_ORDER"),
    ("INVALID_TRAIT_BAR_ORDER", False, "PASSIVE_BAR_ORDER"),
    ("INVALID_DUPLICATE_FEATURE", False, "DUPLICATE_FEATURE"),
    ("INVALID_SCHOOL_PARENT", False, "SPECIALIZATION_PARENT"),
    ("INVALID_FORM_PARENT_STAFF", False, "SPECIALIZATION_PARENT"),
    ("INVALID_WEAPON_BOUND_SPELL_EXECUTOR", False, "SPELL_EXECUTOR"),
    ("INVALID_DORMANT_UNKNOWN_FEATURE", False, "DORMANT_CATALOG"),
    ("INVALID_ATOMIC_SAVE_DOES_NOT_MUTATE", False, "ATOMIC_REJECT"),
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=DEFAULT_CATALOG)
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true")
    return parser.parse_args()


def form_catalog() -> list[dict[str, Any]]:
    return [
        {
            "specialization_id": specialization_id,
            "combat_discipline_id": discipline_id,
            "specialization_kind": "FORM",
            "display_name": display_name,
            "sort_order": sort_order,
        }
        for specialization_id, discipline_id, display_name, sort_order in FORMS
    ]


def form_owners() -> dict[str, str]:
    owners: dict[str, str] = {}
    for specialization_id, feature_ids in FORM_FEATURES.items():
        for ability_id in feature_ids:
            if ability_id in owners:
                raise ValueError(f"feature {ability_id} is assigned to multiple Forms")
            owners[ability_id] = specialization_id
    return owners


def load_baseline(path: Path) -> dict[str, Any]:
    if not path.exists():
        return BASELINE_EVIDENCE
    encoded = path.read_bytes()
    payload = json.loads(encoded)
    tables = payload.get("tables", {})
    observed = {
        "ignored_snapshot_path": str(path.relative_to(REPO_ROOT)),
        "sha256": hashlib.sha256(encoded).hexdigest(),
        "row_counts": {name: len(value.get("rows", [])) for name, value in sorted(tables.items())},
    }
    if observed != BASELINE_EVIDENCE:
        raise ValueError(
            "the local pre-reset snapshot does not match the locked Phase 0 baseline: "
            f"observed={observed} expected={BASELINE_EVIDENCE}"
        )
    return observed


def make_contract(catalog_path: Path, baseline_path: Path) -> dict[str, Any]:
    source = json.loads(catalog_path.read_text())
    abilities = {
        ability["ability_id"]: ability
        for ability in source["abilities"]
        if ability.get("actor_scope") == "PLAYER"
    }
    forms = form_catalog()
    forms_by_id = {form["specialization_id"]: form for form in forms}
    owners = form_owners()

    authored_form_ids = set(forms_by_id)
    if set(FORM_FEATURES) != authored_form_ids:
        raise ValueError("FORM_FEATURES keys must exactly equal the authored Form catalog")

    schools = [
        {
            "specialization_id": school["spell_school_id"],
            "combat_discipline_id": "STAFF",
            "specialization_kind": "SCHOOL",
            "display_name": school["display_name"],
            "sort_order": school["sort_order"],
        }
        for school in source["combat_build_contract"]["spell_schools"]
    ]

    feature_rows: list[dict[str, Any]] = []
    for ability_id, ability in sorted(abilities.items()):
        selection_kind = ability["selection_kind"]
        if ability_id in REMOVED_PLAYER_ABILITIES or selection_kind == "INTRINSIC":
            continue
        if selection_kind not in {"ACTIVE", "PASSIVE"}:
            raise ValueError(f"unexpected selectable kind for {ability_id}: {selection_kind}")

        if ability_id in owners:
            specialization_id = owners[ability_id]
            specialization = forms_by_id[specialization_id]
        else:
            if ability.get("combat_discipline_id") != "STAFF" or not ability.get("spell_school_id"):
                raise ValueError(f"selectable feature {ability_id} has no reviewed Form/School owner")
            specialization_id = ability["spell_school_id"]
            specialization = next(
                school for school in schools if school["specialization_id"] == specialization_id
            )

        if selection_kind == "PASSIVE":
            loadout_kind = "PERK"
            bar_domain = "NONE"
        elif specialization["specialization_kind"] == "SCHOOL" or ability_id in FORM_OWNED_SPELLS:
            loadout_kind = "SPELL"
            bar_domain = "GLOBAL_SPELL"
        else:
            loadout_kind = "TECHNIQUE"
            bar_domain = "PARENT_TECHNIQUE"

        gameplay_kind = ability["gameplay"]["kind"]
        if loadout_kind == "SPELL" and gameplay_kind != "SPELL":
            raise ValueError(f"semantic Spell {ability_id} retains weapon-bound executor {gameplay_kind}")
        if loadout_kind == "TECHNIQUE" and specialization["combat_discipline_id"] == "STAFF":
            raise ValueError(f"Staff Technique is forbidden: {ability_id}")

        if loadout_kind == "SPELL":
            compatibility_profiles = ANIMATION_PROFILES
        elif loadout_kind == "TECHNIQUE":
            compatibility_profiles = [specialization["combat_discipline_id"]]
        else:
            compatibility_profiles = []

        feature_rows.append(
            {
                "ability_id": ability_id,
                "action_id": ability.get("action_id"),
                "display_name": ability["display_name"],
                "current_combat_discipline_id": ability.get("combat_discipline_id"),
                "current_spell_school_id": ability.get("spell_school_id"),
                "current_selection_kind": selection_kind,
                "gameplay_kind": gameplay_kind,
                "proposed_specialization_id": specialization_id,
                "proposed_specialization_kind": specialization["specialization_kind"],
                "proposed_parent_discipline_id": specialization["combat_discipline_id"],
                "loadout_kind": loadout_kind,
                "bar_domain": bar_domain,
                "requires_equipped_parent_weapon": loadout_kind == "TECHNIQUE",
                "presentation_discovery": (
                    "SPELL_EXECUTOR" if gameplay_kind == "SPELL" else "EXECUTOR_NATIVE"
                ),
                "animation_compatibility_profiles": compatibility_profiles,
                "cutover_disposition": "RESET_AND_RESEED",
                "review_status": "LOCKED_PHASE_0",
            }
        )

    classified_ids = {row["ability_id"] for row in feature_rows}
    expected_selectable_ids = {
        ability_id
        for ability_id, ability in abilities.items()
        if ability["selection_kind"] in {"ACTIVE", "PASSIVE"}
        and ability_id not in REMOVED_PLAYER_ABILITIES
    }
    if classified_ids != expected_selectable_ids:
        missing = sorted(expected_selectable_ids - classified_ids)
        extra = sorted(classified_ids - expected_selectable_ids)
        raise ValueError(f"classification ledger mismatch: missing={missing}, extra={extra}")

    intrinsic_ids = {
        ability_id
        for ability_id, ability in abilities.items()
        if ability["selection_kind"] == "INTRINSIC"
        and ability_id not in REMOVED_PLAYER_ABILITIES
    }
    if intrinsic_ids != set(INTRINSIC_DISPOSITIONS):
        raise ValueError(
            "intrinsic disposition mismatch: "
            f"missing={sorted(intrinsic_ids - set(INTRINSIC_DISPOSITIONS))}, "
            f"extra={sorted(set(INTRINSIC_DISPOSITIONS) - intrinsic_ids)}"
        )

    form_counts = {
        form_id: sum(row["proposed_specialization_id"] == form_id for row in feature_rows)
        for form_id in sorted(forms_by_id)
    }
    empty_forms = [form_id for form_id, count in form_counts.items() if count == 0]
    if empty_forms:
        raise ValueError(f"seeded Forms may not be empty: {empty_forms}")

    baseline = load_baseline(baseline_path)
    loadout_counts: dict[str, int] = {}
    for row in feature_rows:
        loadout_counts[row["loadout_kind"]] = loadout_counts.get(row["loadout_kind"], 0) + 1
    spell_executor_techniques = sum(
        row["loadout_kind"] == "TECHNIQUE" and row["gameplay_kind"] == "SPELL"
        for row in feature_rows
    )

    return {
        "schema_version": 1,
        "phase": 0,
        "status": "LOCKED",
        "source_catalog": str(catalog_path.relative_to(REPO_ROOT)),
        "decisions": {
            "maximum_specializations": 3,
            "global_feature_capacity": 18,
            "trait_capacity": 3,
            "separate_spell_bar_capacity": None,
            "separate_technique_bar_capacity": None,
            "perk_activation": "ACTIVE_WHILE_SOURCE_SPECIALIZATION_SELECTED",
            "dormant_order_collision": "KEEP_ACTIVE_ORDER_THEN_REFLOW_RETURNING_FEATURES",
            "hub_build_cutover": "SNAPSHOT_THEN_RESET_COMBAT_BUILD_ROWS_ONLY",
            "mastery_damage_bonus": 0.10,
            "mastery_condition": "SELECTED_AND_EXACTLY_ONE_DISTINCT_PARENT_DISCIPLINE",
            "staff_autoattack": "RETAIN",
            "staff_techniques": "FORBIDDEN",
        },
        "specializations": forms + schools,
        "trait_catalog": [
            {
                "trait_id": "MASTERY",
                "display_name": "Mastery",
                "damage_bonus": 0.10,
                "condition": "EXACTLY_ONE_DISTINCT_PARENT_DISCIPLINE",
                "damage_scope": "NORMAL_PLAYER_AUTHORED_OUTGOING_DAMAGE",
                "excludes": ["SYSTEM", "SELF_INFLICTED_FINAL", "COPIED_FINAL"],
            }
        ],
        "input_contract": {
            "ordering_domain": "ONE_GLOBAL_ACTIVE_FEATURE_ORDER",
            "presentation_domains": ["GLOBAL_SPELL", "CURRENT_PARENT_TECHNIQUE"],
            "selected_active_is_automatically_actionable": True,
            "direct_access_required": True,
            "bindings": [
                {
                    "input_action_id": action_id,
                    "default_label": label,
                    "default_key_code": key_code,
                    "requires_shift": requires_shift,
                    "order": index,
                }
                for index, (action_id, label, key_code, requires_shift) in enumerate(DIRECT_INPUTS)
            ],
        },
        "feature_classification": feature_rows,
        "intrinsic_ledger": [
            {
                "ability_id": ability_id,
                "loadout_kind": "INTRINSIC",
                "selectable": False,
                "counts_toward_capacity": False,
                "disposition": disposition,
            }
            for ability_id, disposition in sorted(INTRINSIC_DISPOSITIONS.items())
        ],
        "removal_ledger": [
            {
                "ability_id": ability_id,
                "current_selection_kind": abilities[ability_id]["selection_kind"],
                "current_gameplay_kind": abilities[ability_id]["gameplay"]["kind"],
                "disposition": disposition,
                "may_retain_private_presentation_data": ability_id == "STAFF_STRIKE_2",
            }
            for ability_id, disposition in sorted(REMOVED_PLAYER_ABILITIES.items())
        ],
        "fixtures": [
            {"fixture_id": fixture_id, "valid": valid, "expected_error": expected_error}
            for fixture_id, valid, expected_error in FIXTURES
        ],
        "animation_compatibility": {
            "presentation_discovery_source": "gameplay.kind",
            "coverage_source": "loadout_kind",
            "combat_animation_profiles": ANIMATION_PROFILES,
            "spell_requirement": "ALL_PROFILES",
            "technique_spell_executor_requirement": "PARENT_PROFILE_ONLY",
            "existing_validator": "CombatVFXAuthoringValidator validates every SpellCastAnimationMap entry across every CombatAnimationSet and preserves Direct2H-to-Direct1H fallback.",
            "phase_0_result": "NO_ANIMATION_ARCHITECTURE_CHANGE_REQUIRED; executable validator extension remains Phase 5.",
            "semantic_spell_count": loadout_counts.get("SPELL", 0),
            "spell_executor_technique_count": spell_executor_techniques,
        },
        "reset_ledger": {
            "baseline": baseline,
            "preserve_tables": ["hub_player", "hub_player_armor_selection"],
            "reset_tables": [
                "combat_build",
                "combat_build_discipline",
                "discipline_configuration",
                "staff_school_selection",
                "discipline_action_bar_assignment",
                "discipline_passive_selection",
            ],
            "default_seed": {
                "schema_version": 2,
                "revision": 0,
                "starting_discipline_id": "DAGGERS",
                "selected_specializations": [
                    {
                        "slot_index": 0,
                        "specialization_id": "DAGGERS_BLADEDANCER",
                    }
                ],
                "discipline_configurations": [
                    {
                        "combat_discipline_id": "DAGGERS",
                        "main_hand_item_def_id": "TRAINING_DAGGER_PAIR",
                        "main_hand_color_id": "",
                        "off_hand_item_def_id": "",
                        "off_hand_color_id": "",
                    }
                ],
                "selected_features": [
                    {
                        "specialization_id": "DAGGERS_BLADEDANCER",
                        "ability_id": "DAGGER_QUICK_CUT",
                        "preferred_bar_order": 0,
                    }
                ],
                "selected_traits": [],
                "dormant_specializations": [],
            },
            "converter": "NONE",
        },
        "summary": {
            "form_count": len(forms),
            "school_count": len(schools),
            "classified_feature_count": len(feature_rows),
            "loadout_kind_counts": dict(sorted(loadout_counts.items())),
            "form_feature_counts": form_counts,
            "removed_player_ability_count": len(REMOVED_PLAYER_ABILITIES),
            "retained_private_intrinsic_count": len(INTRINSIC_DISPOSITIONS),
        },
    }


def main() -> int:
    args = parse_args()
    contract = make_contract(args.catalog.resolve(), args.baseline.resolve())
    rendered = json.dumps(contract, indent=2, sort_keys=True) + "\n"
    if args.check:
        if not args.output.exists() or args.output.read_text() != rendered:
            raise SystemExit(f"{args.output} is stale; rerun {Path(__file__).name}")
        print(f"Combat Build v2 Phase 0 contract is current: {args.output}")
        return 0
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(rendered)
    print(f"Wrote Combat Build v2 Phase 0 contract: {args.output}")
    print(json.dumps(contract["summary"], sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

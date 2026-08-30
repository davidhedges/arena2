#!/usr/bin/env python3
"""Generate/check the exhaustive Combat Build v2 runtime call-site inventory."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
SERVER_ROOT = REPO_ROOT / "server" / "src"
OUTPUT = (
    REPO_ROOT
    / "docs"
    / "combat-build-v2-phase-4-runtime-callsite-inventory-2026-08-29.json"
)

API_DISPOSITIONS = {
    "active_selectable_ability_for_authored_action": {
        "classification": "ACTIVE_INVOCATION",
        "phase4_disposition": "ROUTE_BY_V2_LOADOUT_KIND",
        "cutover_target": (
            "TECHNIQUE uses selected Technique plus active parent Discipline; "
            "SPELL uses the global selected Spell set under any selected active parent"
        ),
    },
    "active_selectable_ability_for_ability_id": {
        "classification": "ACTIVE_INVOCATION",
        "phase4_disposition": "ROUTE_BY_V2_LOADOUT_KIND",
        "cutover_target": (
            "TECHNIQUE uses selected Technique plus active parent Discipline; "
            "SPELL uses the global selected Spell set under any selected active parent"
        ),
    },
    "player_build_contains_active_ability": {
        "classification": "PERSISTENT_ACTIVE_RECONCILIATION",
        "phase4_disposition": "SELECTED_ACTIVE_MEMBERSHIP",
        "cutover_target": (
            "selected Technique-or-Spell membership independent of current parent; "
            "dormant and unselected features fail closed"
        ),
    },
    "player_has_selected_passive_ability": {
        "classification": "SPECIALIZATION_PASSIVE_EFFECT",
        "phase4_disposition": "SELECTED_PERK_SOURCE_PREDICATE",
        "cutover_target": (
            "selected Perk row whose source Specialization is selected; existing passive "
            "call sites do not become character-wide Traits"
        ),
    },
}

DIRECT_TABLE_ACCESSORS = (
    "match_combat_build_discipline",
    "match_staff_school_selection",
    "match_discipline_action_bar_assignment",
    "match_discipline_passive_selection",
)
ALLOWED_DIRECT_ACCESS_FILES = {
    "server/src/match_contract.rs",
    "server/src/progression.rs",
}


def line_number(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def enclosing_function(text: str, offset: int) -> str:
    matches = list(
        re.finditer(
            r"(?:pub(?:\(crate\))?\s+)?(?:async\s+)?fn\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
            text[:offset],
        )
    )
    return matches[-1].group(1) if matches else "<module>"


def line_snippet(text: str, offset: int) -> str:
    start = text.rfind("\n", 0, offset) + 1
    end = text.find("\n", offset)
    if end < 0:
        end = len(text)
    return " ".join(text[start:end].strip().split())


def is_function_definition(text: str, offset: int) -> bool:
    prefix = text[max(0, offset - 48) : offset]
    return re.search(r"\bfn\s+$", prefix) is not None


def source_files() -> list[Path]:
    return sorted(SERVER_ROOT.rglob("*.rs"))


def runtime_calls() -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for path in source_files():
        text = path.read_text(encoding="utf-8")
        relative = path.relative_to(REPO_ROOT).as_posix()
        for api, disposition in API_DISPOSITIONS.items():
            for match in re.finditer(rf"\b{re.escape(api)}\s*\(", text):
                if is_function_definition(text, match.start()):
                    continue
                rows.append(
                    {
                        "api": api,
                        "path": relative,
                        "line": line_number(text, match.start()),
                        "enclosing_function": enclosing_function(text, match.start()),
                        "snippet": line_snippet(text, match.start()),
                        **disposition,
                    }
                )
    rows.sort(key=lambda row: (row["path"], row["line"], row["api"]))
    return rows


def direct_table_accesses() -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    unexpected: list[str] = []
    for path in source_files():
        text = path.read_text(encoding="utf-8")
        relative = path.relative_to(REPO_ROOT).as_posix()
        for accessor in DIRECT_TABLE_ACCESSORS:
            for match in re.finditer(rf"\b{re.escape(accessor)}\s*\(", text):
                row = {
                    "accessor": accessor,
                    "path": relative,
                    "line": line_number(text, match.start()),
                    "enclosing_function": enclosing_function(text, match.start()),
                    "snippet": line_snippet(text, match.start()),
                    "disposition": (
                        "MATERIALIZATION_OR_CENTRAL_AUTHORITY_ONLY; no leaf gameplay consumer"
                    ),
                }
                rows.append(row)
                if relative not in ALLOWED_DIRECT_ACCESS_FILES:
                    unexpected.append(
                        f"{relative}:{row['line']} directly accesses {accessor}"
                    )
    if unexpected:
        raise RuntimeError("unexpected combat-build table bypasses:\n" + "\n".join(unexpected))
    rows.sort(key=lambda row: (row["path"], row["line"], row["accessor"]))
    return rows


def build_inventory() -> dict[str, Any]:
    calls = runtime_calls()
    counts = {
        api: sum(row["api"] == api for row in calls) for api in API_DISPOSITIONS
    }
    return {
        "schema_version": 1,
        "scope": "SERVER_PLAYER_ACTIVE_PASSIVE_AUTHORIZATION_CALL_SITES",
        "source_root": "server/src",
        "api_call_counts": counts,
        "runtime_calls": calls,
        "direct_table_accesses": direct_table_accesses(),
        "direct_table_access_policy": {
            "allowed_files": sorted(ALLOWED_DIRECT_ACCESS_FILES),
            "rule": (
                "Frozen v1 table access is confined to materialization and the centralized "
                "authorization module; leaf gameplay code must use inventoried APIs"
            ),
        },
        "mastery_damage_insertion": {
            "path": "server/src/combat.rs",
            "function": "resolve_damage_amount",
            "future_cutover_location": "non-system non_crit_multiplier",
            "normal_player_authored_paths": [
                "AUTO_ATTACK",
                "TECHNIQUE",
                "SPELL",
                "OWNED_PERIODIC",
            ],
            "excluded_source_kinds": [
                "SYSTEM",
                "SELF_INFLICTED_FINAL",
                "COPIED_FINAL",
            ],
            "reason": (
                "Player and NPC targets both call resolve_damage_amount; self-inflicted, "
                "redirected/reckoning/assist-cost final amounts and Fulmination copied final "
                "damage branch before the normal outgoing multiplier chain"
            ),
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    rendered = json.dumps(build_inventory(), indent=2, sort_keys=True) + "\n"
    if args.check:
        if not OUTPUT.is_file() or OUTPUT.read_text(encoding="utf-8") != rendered:
            raise SystemExit(f"Combat Build v2 Phase 4 inventory is stale: {OUTPUT}")
        print(f"Combat Build v2 Phase 4 inventory is current: {OUTPUT}")
        return 0
    OUTPUT.write_text(rendered, encoding="utf-8")
    print(f"Wrote Combat Build v2 Phase 4 inventory: {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

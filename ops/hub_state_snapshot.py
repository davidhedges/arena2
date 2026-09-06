#!/usr/bin/env python3
"""Read and compare the current local Hub's durable v2 state; never mutates a database."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request

# Queue assignments, connections and maintenance timers are intentionally not save data.
PLAYER_TABLES = {
    "hub_player": "identity",
    "hub_player_armor_selection": "owner",
    "combat_build_v_2": "owner",
    "selected_specialization_v_2": "owner",
    "dormant_specialization_v_2": "owner",
    "discipline_configuration_v_2": "owner",
    "specialization_feature_selection_v_2": "owner",
    "trait_selection_v_2": "owner",
}
OTHER_TABLES = (
    "combat_build_v_2_cutover_audit", "combat_build_v_2_contract_definition",
    "combat_specialization_definition_v_2", "combat_feature_definition_v_2",
    "combat_trait_definition_v_2", "hub_armor_set_definition",
    "hub_weapon_definition", "hub_weapon_color_definition",
)
TABLES = (*PLAYER_TABLES, *OTHER_TABLES)


def canonical(value: object) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"))


def local_token() -> str:
    result = subprocess.run(["spacetime", "login", "show", "--token"],
                            capture_output=True, text=True, check=True)
    for line in result.stdout.splitlines():
        if line.startswith("Your auth token "):
            return line.split()[-1]
    raise RuntimeError("The local CLI credential was unavailable; no credential was logged.")


def request(database: str, path: str, token: str, query: str | None = None) -> bytes:
    if database != "arena-hub-local" and not database.startswith("arena-cleanup-restore-"):
        raise ValueError("Only the local Hub and explicitly named cleanup restore rehearsals are allowed.")
    if not all(c.isascii() and (c.isalnum() or c == "-") for c in database):
        raise ValueError("Invalid local database name.")
    req = urllib.request.Request(
        f"http://127.0.0.1:3000/v1/database/{database}{path}",
        data=query.encode() if query is not None else None,
        headers={"Authorization": f"Bearer {token}", "Content-Type": "text/plain"},
    )
    with urllib.request.urlopen(req, timeout=30) as response:
        return response.read()


def capture_tables(database: str, token: str) -> dict:
    tables = {}
    for table in TABLES:
        payload = json.loads(request(database, "/sql", token, f"SELECT * FROM {table}"))
        if len(payload) != 1:
            raise ValueError(f"Expected exactly one query result for {table}")
        result = payload[0]
        elements = result["schema"]["elements"]
        if not elements or any(len(row) != len(elements) for row in result["rows"]):
            raise ValueError(f"Invalid row schema for {table}")
        tables[table] = {"schema": result["schema"], "rows": sorted(result["rows"], key=canonical)}
    return tables


def require_preserved(before: dict, after: dict) -> None:
    if set(before) != set(TABLES) or set(after) != set(TABLES):
        raise ValueError("Snapshot table inventory does not match the current v2 contract.")
    identity_index = column_index(before["hub_player"], "identity")
    owners = {canonical(row[identity_index]) for row in before["hub_player"]["rows"]}
    for table in TABLES:
        if before[table]["schema"] != after[table]["schema"]:
            raise ValueError(f"Schema changed: {table}")
        rows = after[table]["rows"]
        if table in PLAYER_TABLES:
            owner_index = column_index(after[table], PLAYER_TABLES[table])
            rows = [row for row in rows if canonical(row[owner_index]) in owners]
        if sorted(before[table]["rows"], key=canonical) != sorted(rows, key=canonical):
            raise ValueError(f"Existing saved rows changed, disappeared, or acquired children: {table}")


def column_index(table: dict, name: str) -> int:
    return [element["name"]["some"] for element in table["schema"]["elements"]].index(name)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("snapshot", "verify"))
    parser.add_argument("--file", type=Path, required=True)
    parser.add_argument("--database", default="arena-hub-local")
    args = parser.parse_args()
    token = local_token()
    tables = capture_tables(args.database, token)
    # Refuse an unstable read. This is a quiescent local capture, not an atomic online-backup API.
    if tables != capture_tables(args.database, token):
        raise RuntimeError("Hub state changed during capture; retry when saved-state writes are idle.")
    if args.command == "verify":
        before = json.loads(args.file.read_text())
        if before.get("format") != "arena-hub-durable-v2-1":
            raise ValueError("Unsupported snapshot format.")
        require_preserved(before["tables"], tables)
        print("All existing Hub profiles, saved children, cutover audit and catalogs are unchanged.")
    else:
        payload = {"format": "arena-hub-durable-v2-1", "database": args.database,
                   "tables": tables}
        args.file.parent.mkdir(parents=True, exist_ok=True)
        with args.file.open("x") as output:
            args.file.chmod(0o600)
            output.write(json.dumps(payload, indent=2, sort_keys=True) + "\n")
        print("Snapshot SHA-256:", hashlib.sha256(args.file.read_bytes()).hexdigest())
    print("Captured row counts:", {table: len(value["rows"]) for table, value in tables.items()})


if __name__ == "__main__":
    main()

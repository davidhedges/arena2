# Combat-build progression cutover — Phase 3 evidence

Date: 2026-08-26

Status: **COMPLETE — the immutable Hub snapshot, exact provisioner transport,
match revalidation, and canonical match materialization pass the Phase 3 exit
gate. Phase 4 was not started.**

## 1. Approved boundary

This slice implemented only Phase 3 of
`docs/combat-build-progression-cutover-plan-2026-08-26.md`:

- freeze one complete versioned canonical combat build at ticket creation;
- transport that snapshot without positional reinterpretation;
- revalidate and retain the exact snapshot in the disposable match;
- materialize canonical selected-discipline rows and match-local weapon
  instances;
- use first-selected startup while leaving room for a future explicit default;
- regenerate affected bindings and update provisioner/benchmark coverage; and
- remove the match-side durable legacy loadout writer.

This phase did not change gameplay action-bar resolution, discipline switching,
cast/passive authorization, the current Unity loadout plumbing, or either the
current or future Disciplines UI. Those remain Phase 4 and later work.

## 2. One frozen handoff authority

Ticket creation calls the same shared `CombatBuildCatalog` validator used by
the Hub save reducer, reconstructs the complete saved aggregate in deterministic
order, and serializes the resulting `CombatBuildSnapshot` as compact JSON. The
private `match_player_combat_build_snapshot` row freezes:

- ticket and player identity;
- contract schema version;
- combat-build revision;
- exact canonical snapshot JSON;
- separately scoped armor selection; and
- capture timestamp.

The operation is in the ticket-creation reducer transaction. A missing build,
orphan child row, validation failure, serialization failure, or missing armor
selection aborts ticket creation. Later Hub edits cannot mutate the frozen row.

The provisioner queries only `match_player_combat_build_snapshot`. It checks
ticket/player ownership and nonempty schema, revision, JSON, and armor metadata,
but deliberately does not parse or reinterpret the JSON. Both PvP and
open-world bootstrap calls contain nine total arguments: seven allocation
fields, the exact snapshot JSON, and armor. Recovery from an already bootstrapped
database exact-compares the reservation's schema version, revision, JSON bytes,
and armor; any drift quarantines the allocation.

The disposable module deserializes a deny-unknown-fields typed snapshot, applies
the shared catalog validator again, reserializes it, and requires byte-for-byte
canonical equality before inserting any reservation or build row. Unsupported
versions, oversized or malformed JSON, semantic invalidity, or noncanonical
bytes fail closed. There is no default legacy substitution.

## 3. Canonical match materialization

Successful bootstrap creates these public, owner-scoped canonical rows:

- `match_combat_build`;
- `match_combat_build_discipline`;
- `match_discipline_configuration`;
- `match_staff_school_selection`;
- `match_discipline_action_bar_assignment`; and
- `match_discipline_passive_selection`.

The exact complete snapshot remains in the private `match_reservation`; the
materialized rows contain the selected battle projection. Durable dormant
discipline configurations therefore remain preserved in the Hub/frozen
snapshot without becoming an alternate in-match authorization surface.

Each selected discipline receives validated match-local weapon instances and
stores their instance IDs with its frozen weapon definitions and colors. Empty
color IDs retain their contract meaning of “authored default”; runtime equipment
resolves that default without changing the frozen canonical row. The first
selected discipline is the effective starting discipline when the snapshot has
no explicit starting/default value, and only that configuration is initially
equipped. The exact snapshot already carries an explicit starting discipline
when the future UI selects one.

Provisioned players skip legacy `CharacterDisciplineLoadout` and
`CharacterDisciplineAbilitySelection` defaulting. Those structures remain only
for the still-supported local-direct path and later runtime/client cutovers;
they cannot substitute for a missing provisioned-match build.

## 4. Legacy and parallel-path audit

The following production handoff surfaces no longer contain primary/secondary
slots or an undifferentiated selected-ability list:

- `server/src/match_contract.rs`;
- `match_provisioner/worker.py`;
- `match_provisioner/test_worker.py`;
- `ops/benchmark-local-match-start.py`; and
- `ops/test_benchmark_local_match_start.py`.

`save_character_discipline_loadout` is removed, and regenerated main/match
bindings no longer expose `SaveCharacterDisciplineLoadout`. Generated bootstrap
bindings expose one `combat_build_snapshot_json` and armor argument, while
generated `MatchReservation` contains contract version, revision, exact JSON,
and armor instead of positional combat fields.

One deliberate residue remains: the private Hub
`MatchPlayerLoadoutSnapshot` table and its generated row type. Removing that
table during the data-preserving SpacetimeDB 2.1 publish was reported as a
breaking migration and would have required resetting the persistent local Hub.
No approved converter existed, and a reset would have discarded the Phase 2
canonical builds. The table was therefore retained as an explicit schema
tombstone:

- no ticket reducer inserts it;
- no provisioner, reservation, match bootstrap, view, or benchmark reads it;
- terminal cleanup can only delete pre-cutover rows; and
- its physical removal remains open for the explicitly destructive Phase 7
  cutover.

This is inert preserved schema, not a second handoff path. The legacy
`HubPlayerLoadout` remains current-UI/armor compatibility state; ticket capture
reads only its separately scoped `armor_set_id`, never its discipline, ability,
or weapon fields. Other local-direct/runtime/client legacy concepts remain open
and classified in
`docs/combat-build-legacy-consumer-inventory-2026-08-26.json`; none is claimed
complete ahead of its approved phase.

## 5. Live proof

The canonical data-preserving setup published the Hub without a reset, rebuilt
the disposable artifacts, regenerated bindings, reclaimed no live database,
and restarted the managed provisioner. Final status reported SpacetimeDB, PvP
artifact, open-world artifact, and provisioner ready.

The protected-loadout guard reported:

```text
Verified 51 pre-existing Hub loadout row(s) unchanged; 4 new row(s) allowed
```

Three allowed rows belong to disposable diagnostic identities used to trace the
first live-sample failure; the fourth belongs to the passing final benchmark.
No reset or import occurred.

The first live probe found a boundary defect that unit tests did not expose:
the default Daggers build stores an empty color sentinel, but match equipment
initially treated it as a literal catalog color. Module logs identified the
failure, `require_weapon_family_color` was corrected to resolve an empty value
to the authored family default, and both Rust and benchmark regression tests
were added before rebuilding.

The final one-sample probe then compared the current canonical Hub aggregate to
all six applied match tables, materialized item identities, effective starting
equipment, and armor, and verified exact allocation cleanup:

```json
{
  "armor_set_id": "PEASANT",
  "combat_build_revision": 1,
  "contract_schema_version": 1,
  "match_build_id": "sha256-03fe3afa5fc874e8e777",
  "request_to_initial_state_ms": 1500.791,
  "selected_disciplines": ["DAGGERS"],
  "starting_discipline_id": "DAGGERS"
}
```

Cleanup result: `1/1` exact sampled allocation reached `CLEANED`.

Final artifact evidence:

- PvP WASM size guard: `3,480,397 / 3,500,000` bytes;
- PvP source provenance:
  `79faaa1269824a4c47307ac0ada206c03a7714d6c3712f54d9ed26147045f81a`;
- open-world source provenance:
  `76bebb7f0a38e50cb5fbc8e42b970cd91e19bdec8ea0ca7d2ead4d17c400055d`.

## 6. Verification evidence

Final commands and results:

```text
cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast
PASS — 833 passed, 0 failed

cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast
PASS — 27 passed, 0 failed

python3 -m unittest match_provisioner.test_worker \
  ops.test_benchmark_local_match_start
PASS — 28 passed, 0 failed

ops/dungeon-compile-gate.sh
PASS — Assembly-CSharp, Assembly-CSharp-Editor, and Arena.EditModeTests
compiled with 0 errors

ops/setup-local-multiplayer.sh status
PASS — server, both cached artifacts, and provisioner ready

python3 ops/benchmark-local-match-start.py --samples 1
PASS — exact canonical state reached initial subscription; cleanup 1/1
```

Rust match-contract tests cover malformed, unsupported-version,
noncanonical-byte, semantically invalid, and valid structured snapshots.
Provisioner tests cover exact transport, missing/malformed snapshot metadata,
restart recovery, reservation equality, and quarantine on drift. Python
benchmark tests cover canonical Hub/match row decoding and authored default
weapon-color resolution.

Unity was closed by the owner. No Unity batch-mode run was performed. The
generated C# bindings are validated through the repository compile gate;
interactive Unity presentation was not changed or claimed as verified.

## 7. Exit decision

Phase 3 exit gate: **PASS**.

Phase 4 was not started. Starting it requires a new explicit authorization.

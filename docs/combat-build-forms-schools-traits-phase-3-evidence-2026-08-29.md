# Combat Build v2 Phase 3 evidence

Date: 2026-08-29
Status: PASS

## Scope

Phase 3 adds the canonical v2 snapshot byte contract, an immutable
selected-only materialization plan, provisioner pass-through fixtures, and a
disposable match handoff module. It does not change the canonical Hub ticket
schema, production provisioner behavior, canonical match/open-world bootstrap
reducers, generated production bindings, runtime authorization, or saved v1
combat builds.

The Phase 3 live runner publishes only uniquely named `arena-cbv2-p3-*` Hub,
PvP, open-world, and local-direct rehearsal databases and deletes all of them
on exit.

## Canonical snapshot contract

`CombatBuildV2Catalog` now owns bounded canonical serialization and parsing of
the versioned v2 snapshot:

- payloads must contain 1..=65,536 bytes;
- unknown JSON fields and malformed JSON fail during typed decoding;
- schema versions other than 2 fail through the Phase 1 validator;
- typed snapshots requiring normalization are rejected;
- noncanonical JSON formatting or field bytes are rejected even when the JSON
  is semantically equivalent; and
- validated snapshots reserialize to the exact incoming bytes.

An exact-byte test locks the complete default snapshot serialization. Further
tests cover JSON round-trip, normalization idempotence, pretty/noncanonical
JSON rejection, size bounds, and schema-v1 rejection.

## Selected-only materialization plan

The shared pure planner derives immutable gameplay state from a validated
snapshot. It projects:

- ordered selected Forms/Schools with their parent Disciplines;
- ordered distinct parent Disciplines and only their weapon configurations;
- one merged Technique ordering per non-Staff parent;
- one global Spell ordering;
- selected Perks and Traits; and
- the computed Mastery predicate.

Dormant Specializations, their features, and their otherwise retained parent
configurations do not enter the plan. PvP, open-world, and local-direct
rehearsals all consume the same planner. The three-School fixture produces
three Schools, one Staff configuration, zero Techniques, three Spells, one
Perk, one Trait, and active Mastery.

## Hub freeze and provisioner pass-through

The isolated Hub rehearsal now has a phase-owned v2 frozen-ticket row. Its
anonymous handoff reducer saves and reloads the three-School aggregate, creates
canonical snapshot JSON, validates the materialization plan, and freezes the
exact bytes with schema version, revision, player, and armor metadata.

The production Python provisioner remains unchanged because its existing
contract already treats combat-build JSON as opaque. Two new v2 fixtures prove
that it:

- forwards the exact v2 JSON bytes and armor argument to bootstrap; and
- requires schema, revision, armor, and byte-for-byte reservation equality on
  reconciliation, quarantining even whitespace-only drift.

The test fake recognizes both the legacy `contract_schema_version` JSON field
and v2 `schema_version` solely to emulate the corresponding disposable match
parser. The worker itself never parses either payload shape.

## Disposable match rehearsal

`match-v2-rehearsal` validates the exact canonical bytes before writing any
row. Its one-shot bootstrap/local-direct reducers then persist a reservation
and materialize separate selected-Specialization, parent configuration,
Technique, Spell, Perk, and Trait tables plus the normalized root.

An old-version or noncanonical payload returns before reservation or gameplay
state is written. Queue kind does not alter build semantics; only `UNRANKED`,
`OPEN_WORLD`, and the explicit rehearsal-only `LOCAL_DIRECT` path are admitted.

The shell handoff hex-encodes JSON solely to prevent terminal/argument quoting
from changing bytes between disposable identities. The match reducer decodes
the envelope and validates the original JSON bytes.

## Live rehearsal

`ops/run-combat-build-v2-phase3-rehearsal.sh` completed with:

- Hub: `arena-cbv2-p3-hub-20260830005500-84841`;
- PvP: `arena-cbv2-p3-pvp-20260830005500-84841`;
- open world: `arena-cbv2-p3-world-20260830005500-84841`; and
- local direct: `arena-cbv2-p3-direct-20260830005500-84841`.

The live round-trip was:

`Hub aggregate -> canonical v2 bytes -> handoff payload -> reservation ->
selected-only match rows`.

PvP, open-world, and local-direct results each reported schema 2, revision 2,
exact reservation-byte equality, active Mastery, and row counts of one root,
three Schools, one Staff configuration, zero Techniques, three Spells, one
Perk, and one Trait. Their returned snapshot envelope exactly matched the Hub
frozen-ticket bytes.

The live schema-v1 local-direct call failed with
`COMBAT_BUILD_V2_UNSUPPORTED_SCHEMA_VERSION` and left zero match-build roots;
the subsequent valid fixture succeeded. The six canonical v1 Hub combat-build
table counts were identical before and after the rehearsal. All four
disposable database names were retired.

The recoverable pre-reset snapshot remains unchanged at SHA-256
`9c9b6864142859c5b305c96e8270f72341508dc85a9c6cc63bc88a57ceaa3af5`.

## Verification

- `cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast`: PASS,
  794 passed, 0 failed.
- `cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast`:
  PASS, 25 passed, 0 failed.
- `cargo test --manifest-path hub-v2-rehearsal/Cargo.toml --lib --no-fail-fast`:
  PASS, 11 passed, 0 failed.
- `cargo test --manifest-path match-v2-rehearsal/Cargo.toml --lib --no-fail-fast`:
  PASS, nine passed, 0 failed.
- `python3 -m unittest match_provisioner.test_worker`: PASS, 25 passed, 0
  failed.
- `python3 ops/generate-combat-build-v2-catalog.py --check`: PASS.
- `bash -n ops/run-combat-build-v2-phase2-rehearsal.sh
  ops/run-combat-build-v2-phase3-rehearsal.sh`: PASS.
- `ops/run-combat-build-v2-phase3-rehearsal.sh`: PASS; all four disposable
  databases were retired.
- `ops/setup-local-multiplayer.sh setup`: PASS, data-preserving. The canonical
  Hub reported no migration and generated production bindings had no Git diff.
  PvP WASM remained 3,333,453 / 3,500,000 bytes with provenance
  `a84b4cd69da40ea57094646589a42fea423d872c3bab346373f894ff3b31d58e`;
  open-world provenance is
  `5fc04fd256a033ec8c2c50c3eef9a8df84f2fd2410efe631fe720b253f6c56ca`.
- `ops/setup-local-multiplayer.sh status`: PASS. SpacetimeDB, both artifacts,
  and the provisioner are ready/running.
- `git diff --check`: PASS.

No Unity Editor or batch-mode run was used.

## Exit gate

PASS. The live rehearsal round-tripped a v2 Hub aggregate through exact
canonical bytes and reservation equality into disposable PvP and open-world
state, including three Schools and zero Staff Techniques. Local-direct uses
the same validator and rejects old versions without mutation. The canonical
Hub-to-match path and persistent v1 state remain unchanged.

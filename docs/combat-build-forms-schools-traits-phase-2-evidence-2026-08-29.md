# Combat Build v2 Phase 2 evidence

Date: 2026-08-29
Status: PASS

## Scope

Phase 2 implements and exercises the future Combat Build v2 Hub aggregate in
an isolated rehearsal module. It does not change the canonical `hub-server`
schema, reducers, subscriptions, match tickets, generated client bindings, or
saved v1 combat builds.

The rehearsal module is `hub-v2-rehearsal`. Its runner publishes only to a
unique `arena-cbv2-p2-*` local identity and always deletes that identity on
exit.

## Aggregate and public catalog

The private aggregate consists of:

- one revisioned `combat_build_v2` root per owner;
- ordered selected-Specialization rows;
- remembered dormant-Specialization rows;
- one configuration per retained parent Discipline;
- Specialization-owned feature selections with optional preferred bar order;
  and
- a separate selected-Trait set.

The caller-filtered `my_combat_build_v2` view reconstructs one aggregate. The
rehearsal publishes read-only v2 contract, Specialization, feature, and Trait
definitions for client discovery. The shared catalog now exposes projections
for those definitions without exposing its validation internals or connecting
v2 to a production consumer.

The projected catalog contains 18 Specializations, 208 selectable features,
and one Trait. `STAFF_STRIKE` and the other removed Staff melee abilities do
not appear as selectable features.

## Save, default, and rollback semantics

Connection creates the reviewed v2 default only when an owner has no root.
Save checks the caller's expected revision, validates the complete proposed
draft through the Phase 1 validator, normalizes it, and only then replaces the
aggregate children and increments the revision.

SpacetimeDB reducer transactions provide atomicity around replacement. A
stale revision or invalid draft returns before replacement; a reducer error
also rolls back the transaction. Unit and live probes both compare the stored
draft before and after rejected saves.

Dormant rows retain their feature selections and preferred orders but do not
contribute to capacity, authorization projections, Perks, Traits, or Mastery.
When a dormant same-parent Form returns, active rows win order collisions and
the returning row deterministically reflows.

## Live rehearsal

`ops/run-combat-build-v2-phase2-rehearsal.sh` published the isolated database
`arena-cbv2-p2-20260830003643-81352`, invoked the anonymous
`run_phase_2_live_probe` reducer, queried the stored aggregate and catalog, and
retired the database.

The live sequence proved:

- three selected Schools reload as one Staff parent with three global Spells,
  no Technique bar, and active Mastery;
- two same-parent Dagger Forms reload as one Dagger Technique bar;
- a dormant Executioner selection retains preferred order and reflows behind
  the already-active Bladedancer selection when restored;
- a stale revision is rejected;
- the removed `STAFF_STRIKE` feature is rejected and the prior aggregate is
  unchanged; and
- final revision 4 persists as one root, two selected Specializations, no
  dormant Specializations, one parent configuration, two feature selections,
  and one Trait selection.

All six probe booleans were true. The public catalog counts were 18 / 208 / 1.
The six canonical v1 combat-build table counts were identical before and
after the rehearsal. The recoverable pre-reset snapshot remains unchanged at
SHA-256
`9c9b6864142859c5b305c96e8270f72341508dc85a9c6cc63bc88a57ceaa3af5`.

## Verification

- `cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast`: PASS,
  792 passed, 0 failed.
- `cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast`:
  PASS, 25 passed, 0 failed.
- `cargo test --manifest-path hub-v2-rehearsal/Cargo.toml --lib --no-fail-fast`:
  PASS, nine passed, 0 failed. These include the four shared validator tests
  and five persistence/wire/projection tests.
- `python3 ops/generate-combat-build-v2-catalog.py --check`: PASS.
- `bash -n ops/run-combat-build-v2-phase2-rehearsal.sh`: PASS.
- `ops/run-combat-build-v2-phase2-rehearsal.sh`: PASS; the disposable database
  was retired.
- `ops/setup-local-multiplayer.sh setup`: PASS, data-preserving. The canonical
  Hub reported no migration, generated bindings had no Git diff, the PvP WASM
  passed at 3,333,453 / 3,500,000 bytes, and both cached disposable artifacts
  were refreshed.
- `ops/setup-local-multiplayer.sh status`: PASS. SpacetimeDB, both artifacts,
  and the provisioner are ready/running.
- `git diff --check`: PASS.

No Unity Editor or batch-mode run was used.

## Exit gate

PASS. Live reducer storage proves default, atomic revisioned save/reload,
stale-write rejection, invalid-draft rollback, Staff-Technique rejection,
same-parent and three-School semantics, dormant restore, and Mastery. The
canonical v1 Hub schema and saved combat-build rows remain unchanged, and no
v2 rehearsal identity remains live.

# Combat Build v2 Phase 1 evidence

Date: 2026-08-29  
Status: PASS

## Scope

Phase 1 adds the authoritative v2 Form/School/Trait taxonomy, exhaustive
feature projection, versioned draft/snapshot serialization, canonical
normalization, and one pure validator. It deliberately does not change Hub
tables/reducers, tickets, match bootstrap or authorization, runtime combat,
client UI, input assets, or animation assets.

The v1 production contract remains in `server/src/combat_build.rs`. V2 compiles
beside it in `server/src/combat_build_v2.rs`; no v1 consumer imports v2 in this
phase.

## Catalog

`server/src/combat_build_v2_catalog.shared.json` is a compact projection of the
reviewed Phase 0 ledger. It contains:

- 12 Forms and the six existing Schools;
- 208 selectable features classified exactly once: 80 Techniques, 104 Spells,
  and 24 Perks;
- five retained, private, nonselectable intrinsic actions;
- one character-wide Trait, `MASTERY`, with its 10% modifier and
  one-distinct-parent predicate;
- the exact four-entry Staff-melee removal ledger;
- capacity, input identity, and default-build rules; and
- the legal v2 default build.

The v2 projection is separate from the legacy progression ownership fields so
the existing v1 Hub remains coherent until cutover. V2 Specialization ownership
is the only v2 ownership authority; parent Discipline is derived from it.

`ops/generate-combat-build-v2-catalog.py` reproduces the catalog from the
locked contract and current mechanics catalog. Its embedded source-contract
SHA-256 is
`ff728ac147767b3befa97bd444fff51cece96057294b27507be72192db52796a`.
The server build compacts and stamps the shared file alongside the other
runtime catalogs.

The Phase 0 fixture label `VALID_EIGHTEEN_TECHNIQUES_ONE_FORM` was corrected to
`VALID_EIGHTEEN_TECHNIQUES_ONE_PARENT`: no seeded Form owns 18 Techniques, and
the approved rule is one shared capacity across selected Forms.

## Catalog fail-closed checks

Catalog construction rejects:

- noncanonical, duplicate, empty, unknown-parent, or incorrectly sorted
  Specializations;
- a School outside Staff, a Staff Form, any School Technique, or an empty
  Specialization;
- a missing, duplicate, extra, removed, or structurally mismatched player
  feature;
- a semantic Spell whose established executor is weapon-bound;
- a Perk that is not structurally passive;
- a retained intrinsic that is selectable or consumes capacity;
- any deviation from the four reviewed Staff removals or their private-data
  dispositions;
- an invalid Mastery definition, ruleset, or 18-action input projection; and
- invalid Discipline weapon/color/hand-shape data.

The exhaustive comparison is against every current player `ACTIVE`, `PASSIVE`,
and `INTRINSIC` progression record. NPC abilities remain outside the build
catalog.

## Pure build validation and normalization

The serde contract includes:

- versioned v2 drafts and snapshots;
- 0–2 ordered selected-Specialization slots plus remembered dormant
  Specializations;
- one configuration per selected or dormant parent Discipline;
- Specialization feature rows with optional preferred bar order; and
- a separate selected-Trait set.

Validation enforces exact schema/revision, 1–3 unique contiguous selected
Specializations, derived starting parent, configuration and weapon legality,
feature ownership, nonempty selected Specializations, the combined 18-feature
capacity, the three-Trait capacity, passive order exclusion, dormant catalog
legality, and duplicate rejection.

Only selected Specializations contribute to counts, authorization projections,
Perks, or Mastery. Multiple Forms/Schools with one parent derive one parent
Discipline. Staff derives no Technique bar.

Sparse or colliding active orders normalize to contiguous per-domain order.
The global Spell scope and each parent Technique scope normalize independently;
input row order breaks a collision, allowing a reducer to place already-active
rows before returning dormant rows. Dormant preferences remain unchanged until
reactivation. Revalidating a normalized snapshot is idempotent.

The returned projection contains ordered unique parents, one Technique bar per
non-Staff parent, the global Spell list, selected Perks, selected Traits, and
the computed Mastery predicate. Bar order is presentation data, never
authorization.

## Executable fixture evidence

All 32 Phase 0 fixture identities execute in the Rust tests. Coverage includes:

- one Form, three same-parent Forms, three Schools, and mixed Form/School
  builds;
- 18-Technique and 18-Spell success plus nineteenth-feature rejection;
- dormant exclusion and deterministic collision reflow;
- Mastery with three same-parent Forms and three Schools, and its rejection of
  a mixed-parent bonus;
- every structural, ownership, capacity, passive-order, schema, weapon,
  duplicate, unknown, and old-version failure;
- catalog mutations for School/Staff/feature-kind/executor invariants; and
- pure rejection without mutation of previously accepted state.

Snapshot JSON round-trip and normalization idempotence are also tested.

## Verification

- `python3 ops/generate-combat-build-v2-phase0.py --check`: PASS.
- `python3 ops/generate-combat-build-v2-catalog.py --check`: PASS.
- `cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast`: PASS,
  792 passed, 0 failed. The baseline 20 progression dead-code/unused warnings
  remain.
- `cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast`:
  PASS, 25 passed, 0 failed.
- `cargo test --manifest-path match-server/Cargo.toml --lib combat_build:: --no-fail-fast`:
  PASS, nine v1 combat-build tests passed.
- The broader match-crate native suite remains an unsuitable all-tests gate:
  736 tests pass and 13 source/asset-walk tests fail because they resolve
  `CARGO_MANIFEST_DIR` to `match-server` while compiling files from
  `server/src`. Those failures are path-harness failures (source reads,
  collision assets, and the shared-file inventory), not v2 validator or v1
  combat-build failures. The release match module builds successfully.
- `ops/setup-local-multiplayer.sh setup`: PASS, data-preserving. The unchanged
  v1 Hub republished without a migration, Unity bindings regenerated without a
  diff, and both disposable artifacts rebuilt. PvP WASM passed its size guard
  at 3,333,453 / 3,500,000 bytes.
- `ops/setup-local-multiplayer.sh status`: PASS. SpacetimeDB, match artifact,
  open-world artifact, and provisioner are ready/running.
- `git diff --check`: PASS.

No Unity Editor or batch-mode run was used; Phase 1 changes no client or
animation asset.

## Exit gate

PASS. Every retained selectable and intrinsic player feature maps exactly once,
the four removed Staff abilities map nowhere in v2, Staff/School Technique and
weapon-bound Spell mutations fail closed, all v2 fixtures pass, v1 server/Hub
combat-build regressions remain green, and no production persistence/runtime
consumer has been switched.

# Combat-build progression cutover — Phase 1 evidence

Date: 2026-08-26

Historical checkpoint: this document records the repository when Phase 1
closed. Phase 2 was subsequently approved and completed; current authority is
the main cutover plan plus
`docs/combat-build-progression-phase-2-evidence-2026-08-26.md`.

Status: **COMPLETE — the canonical catalog and pure contract exit gate passes.
No runtime or persistence consumer was partially switched at this checkpoint.**

## 1. Approved boundary

The owner approved Phase 1 of
`docs/combat-build-progression-cutover-plan-2026-08-26.md`. This slice was
limited to:

- a canonical five-discipline and separate six-school catalog projection;
- explicit canonical ability and weapon ownership metadata;
- the single authored discipline, Staff-school, action-slot, combined-budget,
  and active-budget rule projection;
- typed draft and snapshot structures;
- one pure combat-build validator with stable errors;
- exhaustive catalog validation and invariant fixtures; and
- proof that current Hub, match handoff, runtime authorization, generated
  schema, and UI consumers remain wholly on their pre-cutover path.

The slice did not add a Hub table/reducer, a second save path, a match snapshot
field, a provisioner argument, a runtime authorization predicate, a generated
binding field, a migration/reset tool, or replacement UI.

## 2. Canonical authored projection

`server/src/progression_catalog.shared.json#combat_build_contract` is schema
version 1 and contains exactly:

- combat disciplines: `DAGGERS`, `TWO_HANDED_SWORD`,
  `SWORD_AND_SHIELD`, `ARCHER_BOW`, and `STAFF`;
- Staff schools: `BLIGHT`, `MORTALITY`, `RUIN`, `DIVINITY`, `ARCANA`, and
  `PRIMAL`;
- selected-discipline range 1..3;
- selected-Staff-school range 1..3;
- combined active/passive budget 20;
- active maximum 16;
- one counted active or passive minimum per selected discipline;
- first-selected default start behavior; and
- the exact 27 player action-slot IDs.

Every player ability now has explicit `selection_kind` and
`combat_discipline_id`. Every Staff-owned player ability also has exactly one
of the six `spell_school_id` values, and no non-Staff player ability has one.
The exhaustive counts are:

| Actor scope | Selection kind | Rows |
|---|---:|---:|
| Player | Active | 187 |
| Player | Passive | 23 |
| Player | Intrinsic | 6 |
| NPC | Intrinsic, outside player build | 197 |

All 216 player rows have canonical discipline ownership. All 138 weapon-family
rows have canonical `combat_discipline_id`; their counts are 7 Daggers, 35
Two-Handed Sword, 60 Sword & Shield, 24 Bow, and 12 Staff. The generator authors
and freshness-checks that metadata. Combat profiles remain a one-to-one private
runtime-mechanics projection of the five canonical IDs, and every mode maps to
one of those IDs.

The catalog validator rejects duplicate IDs, missing or unknown schools,
schools on non-Staff abilities, missing Staff schools, damage-type/school
domain overlap, invalid selection kinds, unmapped abilities, unmapped or
ill-shaped weapons, invalid colors/hand pairs, unknown modes, and action-slot
projection drift. Exact wire IDs are required; case/whitespace aliases are not
accepted.

## 3. One pure contract

`server/src/combat_build.rs` contains the only target semantic validator:

- `CombatBuildDraft` models the atomic revision-checked input;
- `CombatBuildSnapshot` is the versioned fully validated structure reserved
  for the later frozen handoff;
- selected disciplines own weapon configuration, exact active slots, selected
  passives, and Staff schools;
- dormant configurations are preserved and individually validated but do not
  count toward selected minima or budgets;
- absent explicit start selection resolves to slot 0; and
- `CombatBuildErrorCode` supplies stable `COMBAT_BUILD_*` wire codes.

Validation order follows draft order and returns a deterministic first error.
The module is compiled in the authoritative server sources but deliberately
has no reducer or runtime caller in Phase 1. Later phases must call this
implementation rather than copy its rules.

## 4. Fixture and invariant proof

The production validator executes all 29 frozen cases in
`docs/fixtures/combat-build-contract-v1/cases.json`, including:

- Daggers + Bow + Staff with Ruin + Arcana at 16 active + 4 passive;
- 15 active + 5 passive at the combined cap;
- passive-only per-discipline minimum;
- first-selected and explicit starting discipline behavior;
- dormant configuration exclusion;
- discipline count/order/duplicate failures;
- active and combined budget failures;
- Staff school count, duplicate, damage-type, and unselected-school failures;
- duplicate/kind/ownership/slot/unknown ability failures; and
- cross-discipline weapon rejection.

Additional production-validator tests cover stale revision, missing and
duplicate configurations, duplicate exact slots, invalid colors, lowercase ID
alias rejection, deleted dormant references, legal and incomplete Sword &
Shield pairs, and catalog mutation across every required validation domain.

## 5. No-partial-cutover audit

The target symbols were searched outside the pure module and authored catalog:

```sh
rg -n --glob '!server/src/combat_build.rs' \
  --glob '!docs/**' \
  --glob '!server/src/progression_catalog.shared.json' \
  --glob '!Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json' \
  '\b(CombatBuildDraft|CombatBuildSnapshot|validate_draft|selection_kind|spell_school_id|combat_build_contract)\b' \
  server hub-server match-server match_provisioner ops Assets/Arena
```

Result: only the three unused canonical metadata fields in the legacy
`AbilityDefinition` parse DTO. They are retained solely so the old parser can
read the enriched shared JSON; no runtime function reads them. There are zero
calls to `validate_draft` outside its tests and zero Hub/match/UI target
consumers.

The changed-file audit found no generated binding, `HubNetworkManager`,
`DisciplinesScreen`, `match_contract`, provisioner, or benchmark source change.
Canonical setup regenerated both Hub and match bindings, and they remained
byte-identical in Git. Public SpacetimeDB schemas are unchanged.

This proves there is no mixed target/legacy runtime. It does not claim the old
path is removed. The following compatibility surfaces intentionally remain
open in `docs/combat-build-legacy-consumer-inventory-2026-08-26.json`:

- the ten-row legacy runtime `combat_disciplines` projection, including six
  `SPELL_SCHOOL` rows;
- legacy ability `discipline_id` ownership used by current runtime consumers;
- weapon `primary_discipline_id` used by current Hub/equipment consumers;
- primary/secondary persistence, undifferentiated selected abilities, old
  action-bar/runtime authorization, positional handoff, generated bindings,
  and legacy UI.

The new canonical fields are target metadata, not a second reader or writer.
The old fields remain the sole current runtime authority until a later atomic
phase replaces their consumer set. No ledger entry was falsely closed.

## 6. Verification evidence

Pre-edit baseline:

- server: 824/824 passed;
- Hub: 18/18 passed;
- canonical local stack healthy; and
- 34 persistent Hub loadout rows protected by the release guard.

Final commands and results:

```text
cd server && cargo test --no-fail-fast
PASS — 832 passed, 0 failed

cd hub-server && cargo test --no-fail-fast
PASS — 18 passed, 0 failed

python3 -m unittest match_provisioner/test_worker.py
PASS — 22 passed, 0 failed

python3 ops/test_benchmark_local_match_start.py
PASS — 3 passed, 0 failed

python3 ops/generate-weapon-appearance-catalog.py --check
PASS

ops/dungeon-compile-gate.sh
PASS — Assembly-CSharp, Assembly-CSharp-Editor, and Arena.EditModeTests
compiled with 0 errors

git diff --check
PASS
```

The required data-preserving synchronization used only:

```text
ops/setup-local-multiplayer.sh setup
ops/setup-local-multiplayer.sh status
python3 ops/benchmark-local-match-start.py --samples 1
python3 <arena-spell-pipeline>/scripts/hub_loadout_guard.py verify ...
```

Observed final state:

- SpacetimeDB, match artifact, open-world artifact, and managed provisioner
  ready;
- PvP artifact source fingerprint
  `f3f93bab192aa8839c900507a04334825df849eb95d452453b3c2560e3c4963e`;
- open-world artifact source fingerprint
  `39b9694fdccba95ea521bb0c955c77f72e79634777e9285679caefeb13cfa492`;
- anonymous benchmark match build `sha256-2c0ef195e7ed8ae6a9b6` reached initial
  state and cleanup reported `1/1`;
- all 34 protected pre-existing Hub loadout rows unchanged; and
- two verification-benchmark identities allowed by the guard.

Unity was closed by the owner. No Unity batch-mode run was authorized or
performed in Phase 1. The normal .NET compile gate passed, and generated
bindings had no tracked change; interactive Unity reconnection is therefore
not represented as verified or required for this schema-neutral phase.

## 7. Exit decision

Phase 1 exit gate: **PASS**.

- all target catalog rows map exactly once;
- all frozen rule fixtures and exhaustive catalog/structure tests pass; and
- no runtime consumer is partially switched.

At this historical checkpoint, Phase 2 was the next unapproved item. It was
subsequently owner-approved and completed; this document does not retroactively
claim that its implementation existed during the Phase 1 gate.

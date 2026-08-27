# Combat Build / Progression Cutover Plan

Date: 2026-08-26

Status: **PHASE 2 COMPLETE — the canonical catalog/validator and the persistent
Hub combat-build aggregate are live locally. One revision-checked atomic
`save_combat_build` reducer is the only Hub combat-build writer; the single
caller-filtered `my_combat_build` aggregate view and regenerated bindings expose
the canonical state. Phase 3 and later
implementation are not authorized by this document; each requires explicit
approval.**

## 1. Goal

Replace the current primary/secondary discipline loadout with one authoritative
combat build that answers:

> What combat tools and abilities am I bringing into battle?

The build selects one to three equal weapon disciplines and allocates one
combined active/passive ability budget among them. Staff is one weapon
discipline. Selecting spell schools configures Staff; a school is never a
discipline slot, weapon, action bar, or independent equipment loadout.

This plan covers the catalog, validation, durable Hub state, match snapshot,
provisioner handoff, match runtime, action-bar and passive authorization,
migration, generated client contract, documentation, and removal of the old
paths. It deliberately does **not** build the replacement UI. The future UI is
described only far enough to lock the backend contract it will consume.

Character-stat allocation is not part of this combat-build contract. The
current screen's session-only stat controls are a placeholder, not durable
progression, and must leave the Disciplines path when the legacy screen is
retired. This plan does not invent a replacement stat-allocation system or
change stat-scaling combat rules; either would need a separate owner-approved
design.

## 2. Locked product rules

These are decisions, not open design forks:

1. The selectable combat disciplines are the five weapons: Daggers,
   Two-Handed Sword, Bow, Sword & Shield, and Staff.
2. Reuse the existing exact wire identifiers; do not introduce aliases or
   renamed variants:

   | Combat discipline | Canonical ID |
   |---|---|
   | Daggers | `DAGGERS` |
   | Two-Handed Sword | `TWO_HANDED_SWORD` |
   | Bow | `ARCHER_BOW` |
   | Sword & Shield | `SWORD_AND_SHIELD` |
   | Staff | `STAFF` |

   No identifier rename is part of this migration. Presentation-copy changes
   are future UI scope; the IDs above do not change.
3. There is no primary/secondary distinction. A build contains an ordered set
   of one to three distinct, equal disciplines.
4. Match startup equips the first selected discipline for now. The contract
   reserves an optional `starting_discipline_id`; a future UI may set it, and
   it must refer to one of the selected disciplines. When it is unset, slot 0
   is the deterministic fallback.
5. Each discipline owns its durable weapon configuration and its own action
   bar. Runtime equipment is only the currently equipped projection of that
   configuration. Switching discipline atomically switches weapon equipment,
   combat behavior, and the visible/actionable bar.
6. A discipline's configuration persists while that discipline is outside the
   active three. Dormant abilities, passives, schools, slots, and weapon choices
   do not count toward the active build, grant casts, or apply effects. They are
   restored when the discipline is selected again, subject to current catalog
   validation and the global budget.
7. Active abilities are selected by assigning them to exact slots on their
   owning discipline's bar. There is no second `selected_active_ability_ids`
   truth. One active ability may occupy at most one slot in the build.
8. Selected passives have no action-bar slot. A selected passive is always in
   effect while its owning discipline is among the selected one to three,
   regardless of which weapon is currently equipped.
9. Active assignments plus selected passives consume one combined global
   budget of 20. No more than 16 of the selected abilities may be active
   assignments. An active or passive costs one unless a later, separately
   approved rules change explicitly introduces weights. There is no separate
   passive cap: unused active capacity may be spent on passives, while the
   combined total remains at most 20.
10. A valid build has at least one selected discipline. Every selected
    discipline contributes at least one selected active or passive. There are
    no primary/secondary minima and no other per-discipline minimum.
11. All catalog abilities are owned/unlocked. The current combat build is the
    complete player-facing authorization boundary for what is brought into a
    match. Learned-spell rows, equipped spellbook contents, inventory, or any
    other collection system must not independently grant a cast or a bar slot.
12. The build editor is responsible for exact slots. Server validation, not
    the UI, remains authoritative.

## 3. Staff and spell-school rules

1. `STAFF` occupies exactly one of the one to three discipline slots and owns
   exactly one weapon configuration and one action bar.
2. Staff configuration may select one to three distinct schools. When Staff is
   in the active build, at least one school is required.
3. The school IDs are the existing consolidated schools:
   `BLIGHT`, `MORTALITY`, `RUIN`, `DIVINITY`, `ARCANA`, and `PRIMAL`.
4. Every Staff-selectable active and passive has exactly one `spell_school_id`.
   It may be assigned/selected only when that school is among the Staff
   configuration's selected schools.
5. Selecting `RUIN` + `ARCANA`, for example, means the one Staff bar may contain
   abilities from both schools. It does not produce two Staff disciplines, two
   staves, two Staff bars, or two budget allocations.
6. Damage types are separate combat metadata. Fire, Frost, Lightning, Holy,
   Shadow, Air, Necromancy, and similar granular labels are **not** spell
   schools and must never be inferred into `spell_school_id` or accepted by the
   Staff-school validator.
7. Removing a selected Staff school while its abilities are still assigned is
   rejected as one invalid draft. The client must submit the school removal
   and the necessary ability removals together; the server never silently
   rewrites the build.

## 4. Locked ability-budget constants

The authoritative shared rules are:

```text
COMBINED_ABILITY_BUDGET = 20
MAX_ACTIVE_ABILITIES = 16
```

The combined count includes exact active assignments plus selected passives
across all selected disciplines. The active count includes exact active
assignments across all selected discipline bars. The two limits are
independent validations: 17 actives fail even if the combined count is 20 or
less, and 21 total abilities fail even if no more than 16 are active.

These values must live in one shared rules/catalog projection and be treated as
data for later tuning; they must not be repeated as Rust and C# literals. The
existing action-bar slot domain can be preserved and projected from its one
authoritative definition. The active maximum limits how many of those slots
may be occupied across all selected bars.

## 5. Target terminology and catalog model

### 5.1 Canonical identities

`CombatDiscipline` becomes the only selectable top-level combat identity. Its
five IDs are the existing profile IDs in §2. The current dual namespace—weapon
families such as `WAR` plus a separately selected `combat_profile_id`—must not
survive as two authorities.

The old mappings migrate as follows:

| Legacy discipline ID | Canonical discipline | Staff school emitted |
|---|---|---|
| `SUBTLETY` | `DAGGERS` | — |
| `WAR` | `TWO_HANDED_SWORD` | — |
| `ZEAL` | `SWORD_AND_SHIELD` | — |
| `PRECISION` | `ARCHER_BOW` | — |
| `BLIGHT` | `STAFF` | `BLIGHT` |
| `MORTALITY` | `STAFF` | `MORTALITY` |
| `RUIN` | `STAFF` | `RUIN` |
| `DIVINITY` | `STAFF` | `DIVINITY` |
| `ARCANA` | `STAFF` | `ARCANA` |
| `PRIMAL` | `STAFF` | `PRIMAL` |

The catalog cutover must:

- contain exactly five `combat_disciplines` rows keyed by the canonical IDs;
- introduce a separate `spell_schools` catalog containing exactly the six
  consolidated schools;
- migrate every selectable ability to a canonical `combat_discipline_id`;
- set `spell_school_id` only for Staff abilities and require it for all Staff
  actives/passives;
- expose an explicit canonical selection kind (`ACTIVE`, `PASSIVE`, or
  `INTRINSIC`) rather than making eligibility depend on scattered tag parsing;
- keep intrinsic actions outside the selectable budget and document each one;
- key combat modes, resource behavior, weapons, and animations from the
  canonical discipline, or derive any internal animation profile from it
  without a separately selectable/profile-owned loadout; and
- reject duplicate IDs, unknown schools, non-Staff school fields, Staff
  abilities without schools, and damage-type-as-school values at catalog sync.

The implementation may retain a private runtime type named `CombatProfile`
only if it is a pure derived view of the canonical discipline. It may not have
its own catalog selection, persistence, reducer, action bar, or fallback
mapping. Prefer deleting the redundant public `CombatProfileCatalog` and the
many-to-one helpers entirely.

### 5.2 Canonical durable build

Names below are contract names, not permission to implement schema in this
planning slice:

```text
CombatBuild
  owner
  starting_discipline_id        optional; future UI setting
  revision
  updated_at

CombatBuildDiscipline
  owner
  slot_index                    0..2; order is stable
  combat_discipline_id          unique per owner

DisciplineConfiguration
  owner
  combat_discipline_id          exists for active or dormant disciplines
  main_hand_item_def_id
  main_hand_color_id
  off_hand_item_def_id
  off_hand_color_id

StaffSchoolSelection
  owner
  spell_school_id               1..3 when active; persists while dormant

DisciplineActionBarAssignment
  owner
  combat_discipline_id
  action_slot                   exact player-assignable slot
  ability_id                    unique per owner/build

DisciplinePassiveSelection
  owner
  combat_discipline_id
  ability_id                    unique per owner/build
```

The row model may use composite string keys where SpacetimeDB requires them,
but those keys are storage mechanics, not new sources of truth.

Active selection is derived from `DisciplineActionBarAssignment`; passive
selection is derived from `DisciplinePassiveSelection`. The combined count for
a build is:

```text
assignments owned by selected disciplines
+ passive selections owned by selected disciplines
```

Dormant configuration rows remain stored but are excluded from that count.

### 5.3 Authority by environment

| Environment | Authority |
|---|---|
| Authored catalog | Legal disciplines, schools, abilities, slot domain, budget |
| Persistent Hub | Durable current build and dormant discipline configurations |
| Frozen ticket snapshot | Immutable copy of one validated Hub revision |
| Disposable match | Runtime materialization of that frozen snapshot |
| Unity client | Draft editing and presentation only |

The Hub accepts one atomic, revision-checked `save_combat_build` draft. All
Hub callers—including a future Disciplines screen and any retained equipment
editor—use that contract and the same validator. The match module does not save
durable builds. A local-direct test/default path may construct the same typed
snapshot but may not define an alternate schema or validation policy.

The ticket snapshot and match bootstrap use one versioned structured build
contract. Do not replace the current positional primary/secondary parameters
with another loose positional list. Hub child snapshot rows or one typed nested
payload are both acceptable if the provisioner round-trips the structure
without reinterpreting it.

## 6. Authoritative validation invariants

One pure validator is shared conceptually between Hub save, snapshot freeze,
match bootstrap, and test fixtures. Hub is the durable write authority; later
boundaries revalidate defense-in-depth and never implement a different rule
set.

A draft is valid only when all of the following hold:

- revision matches the current Hub build revision;
- discipline count is 1..3, IDs are canonical, and IDs are distinct;
- slot indices are contiguous, unique, and in `0..2`;
- `starting_discipline_id`, when present, is selected;
- every selected discipline has valid weapon definitions/hand pairing for
  that discipline;
- every active assignment has a legal action slot, canonical active ability,
  matching owning discipline, and a unique ability ID;
- every passive selection is a canonical passive with a matching owning
  discipline and unique ability ID;
- active assignments and passive selections do not overlap;
- every selected discipline has at least one counted active or passive;
- the total counted actives plus passives is at most
  `COMBINED_ABILITY_BUDGET`;
- the total active assignments is at most `MAX_ACTIVE_ABILITIES`;
- if Staff is selected, its school count is 1..3 and distinct;
- each selected Staff ability belongs to one of those schools;
- non-Staff disciplines cannot own Staff-school selections;
- dormant rows are individually catalog-valid but do not affect current-build
  minima, budget, casting, passives, or weapon switching; and
- unknown/deleted catalog references produce a stable error and never silently
  select a replacement.

Saving is all-or-nothing. Failed validation changes no build rows and does not
bump the revision. Catalog reconciliation may report an invalid saved draft,
but it must not silently prune or refill abilities, schools, or disciplines.

## 7. Runtime behavior

### 7.1 Match start and weapon switching

At match materialization, create all selected discipline configurations from
the frozen snapshot. Equip `starting_discipline_id` when present;
otherwise equip slot 0. Switching to another selected discipline:

1. verifies the target is in the frozen build;
2. updates the active discipline;
3. applies that discipline's weapon pair as the runtime equipment projection;
4. switches resource/mode behavior as appropriate; and
5. exposes that discipline's assigned action bar.

The switch is one authoritative transaction. A client cannot switch to a
dormant discipline or request a weapon/profile combination independently.

### 7.2 Active authorization

A player active is callable only when all are true:

- it exists as an exact assignment on the currently equipped discipline's bar;
- the owning discipline is selected;
- Staff school membership is valid when applicable; and
- normal runtime conditions such as resource, cooldown, target, and state pass.

The current behavior that copies profile-neutral spells to every bar must be
deleted. Any genuinely universal action must be explicitly authored as
`INTRINSIC` and resolved through the one documented intrinsic-action path.

### 7.3 Passive authorization

Every player passive effect begins with the same predicate:

```text
passive ability is selected
AND its owning discipline is in the frozen active build
```

It does **not** require that discipline to be currently equipped. Profile-only,
discipline-only, spell-known, equipment-only, or unconditional passive checks
are invalid replacements. All bespoke passive call sites in combat code must
route through the one predicate and be covered by an exhaustive inventory test.

### 7.4 Collection and spellbook boundary

All authored actives/passives are eligible for build selection. The build is
the sole player combat authorization source. Therefore:

- `PlayerKnownSpell`, `learn_spell`, equipped `ItemSpell`/spellbook contents,
  and spellbook slot capacity stop authorizing action-bar assignment or casts;
- the old Spell Catalog / Spellbook UI cannot create a match-legal ability
  outside the combat build;
- inventory/spellbook items may remain for a future, separately approved
  modifier or collection purpose, but their current availability-granting
  behavior must be removed; and
- `docs/spellbook-composer-design-2026-07-20.md` must be revised or archived as
  superseded before cutover because its stated
  `spellbook -> known -> action bar -> cast` chain conflicts with this rule.

NPC/scripted ability authorization is out of the player build and must remain
explicitly separate; it cannot be used as a player fallback.

## 8. Implementation phases and exit gates

No phase below is approved merely because this plan exists. Before editing a
phase, state its exact boundary and obtain the owner's explicit approval. Keep
each phase reviewable and stop at its exit gate.

### Phase 0 — rule lock, baseline, and cutover choice

Deliverables:

- machine-readable inventory of every legacy symbol/consumer listed in §10;
- fixtures representing valid/invalid builds before schema work;
- baseline full Rust server, Hub, provisioner, and relevant client edit-mode
  results recorded in the evidence log; and
- explicit owner approval for the local database cutover strategy.

Phase 0 working artifacts:

- `docs/combat-build-legacy-consumer-inventory-2026-08-26.json`;
- `docs/fixtures/combat-build-contract-v1/cases.json`; and
- `docs/combat-build-progression-phase-0-evidence-2026-08-26.md`.

The evidence log records the current gate state. Listing these artifacts does
not authorize Phase 1 or waive an unresolved exit condition.

Owner ruling on 2026-08-26: the recommended protected-export, offline-converter,
explicit-local-reset, and verified-import strategy is approved. The strategy
approval is not approval to execute the destructive reset before the converter
report is reviewed.

Recommended cutover strategy: because there is no production database, export
any wanted local Hub builds, republish the final schema with an explicitly
approved local reset, and import through a temporary offline conversion tool.
The converter uses the §5.1 mapping and is not shipped as a runtime reducer.
If local data must be preserved without reset, stop and design an additive
migration as a separately approved change. Never hide an implicit reset in a
setup or catalog command.

Exit gate: no unresolved catalog identity, data-retention choice, or failing
baseline test.

Phase 0 completion: **PASS on 2026-08-26.** The evidence log records 824/824
server tests, 18/18 Hub tests, 22/22 provisioner tests, 87/87 Unity EditMode
tests, clean C# compilation, a data-preserving canonical local publish, all 33
protected Hub loadout rows unchanged, and a successful disposable-match
initial-state and cleanup sample. No Phase 1 work started.

### Phase 1 — canonical catalog and pure contract

Deliverables:

- five-discipline catalog, separate six-school catalog, and migrated ability
  ownership/school metadata;
- one shared rules projection for discipline limit, Staff-school limit,
  action-slot domain, combined budget, and active maximum;
- typed draft/snapshot structures and one pure validator with stable error
  codes;
- exhaustive catalog validator for ability kind, discipline, school, damage
  type separation, weapons, and modes; and
- unit/property tests for every invariant in §6, including the
  Daggers + Bow + Staff / Ruin + Arcana example.

This phase may not yet add a second production save path. If temporary adapters
are needed for tests, they stay test-only.

Exit gate: all catalog rows map exactly once, all rules tests pass, and no
runtime consumer has been partially switched.

Phase 1 completion: **PASS on 2026-08-26.** The canonical
`combat_build_contract` projection contains exactly five weapon disciplines,
six separate Staff schools, and the one authored rules set. All 216 player
abilities and 138 weapon definitions have explicit canonical ownership; NPC
abilities remain explicitly intrinsic and outside the player build. The pure
validator executes all 29 frozen fixtures and the additional structural,
dormant-reference, catalog-mutation, hand-pairing, exact-ID, and stale-revision
tests. The full server suite passes 832/832 and the Hub suite passes 18/18.
No Hub reducer/table, match handoff, runtime authorization, generated schema,
or UI consumer was switched. The legacy projection remains the sole current
runtime path and stays open in the deletion ledger until its atomic later-phase
cutover. Detailed commands and audit evidence are in
`docs/combat-build-progression-phase-1-evidence-2026-08-26.md`.

### Phase 2 — Hub durable state and atomic save

Deliverables:

- canonical Hub build/configuration tables;
- one revision-checked atomic save reducer using the Phase 1 validator;
- deterministic defaults using canonical IDs;
- per-discipline weapon validation and dormant-configuration persistence;
- Staff school persistence; and
- one caller-filtered Hub aggregate subscription plus regenerated Hub bindings.

Legacy Hub write reducers become unavailable in the same release. A temporary
read adapter is permitted only inside an explicitly named migration build and
must be on the deletion ledger; there is no dual-write period.

Exit gate: save/reload, stale-revision rejection, invalid-draft rollback,
dormant remove/re-add, and weapon-per-discipline tests pass against Hub state.

Phase 2 completion: **PASS on 2026-08-26.** The Hub now stores the canonical
build in one root plus selected-discipline, discipline-configuration, Staff
school, action-assignment, and passive-selection child tables. The sole
`save_combat_build` reducer consumes one typed draft, checks the saved revision,
and invokes the exact Phase 1 validator source before replacing any rows in the
reducer transaction. The legacy discipline and weapon save reducers and their
generated bindings are removed; the single `my_combat_build` view returns the
complete nested caller-owned aggregate, and no public child view remains. There
is no dual-write or runtime converter.
The positional `HubPlayerLoadout` remains read-only combat staging solely for
the still-unmodified Phase 3 ticket handoff (armor continues to be a separate
setting). A live anonymous Hub probe passed deterministic default, save/reload,
stale-revision, invalid rollback, dormant remove/re-add, Staff-school, and
per-discipline weapon checks. Detailed commands, residual-ledger classification,
and publish evidence are in
`docs/combat-build-progression-phase-2-evidence-2026-08-26.md`.

### Phase 3 — frozen snapshot, provisioner, and match materialization

Deliverables:

- versioned structured Hub ticket snapshot containing the complete build;
- provisioner transport updated from positional legacy fields to that one
  structure;
- match bootstrap revalidation and canonical materialized rows;
- first-selected startup with optional future default support;
- per-discipline weapon instances/configuration in the match; and
- regenerated match bindings and updated provisioner tests.

Freeze remains immutable: editing the Hub after ticket creation cannot mutate
the in-flight snapshot or running match.

Exit gate: the Hub snapshot, provisioner arguments, reservation, and applied
match rows are structurally and semantically identical for the captured
revision. Failure is closed; no default legacy loadout is substituted.

Phase 3 completion: **PASS on 2026-08-26.** Ticket creation now validates the
saved canonical aggregate and freezes one compact, versioned JSON snapshot plus
the separately scoped armor selection. The provisioner transports that JSON
unchanged, verifies exact reservation equality on recovery, and fails closed on
missing, malformed, or mismatched metadata. Match bootstrap parses and
revalidates the same shared contract, rejects noncanonical bytes, retains the
exact frozen snapshot in its reservation, and materializes selected-discipline,
Staff-school, exact action-slot, passive, and per-discipline weapon rows. The
first selected discipline supplies the starting weapon when no future explicit
default is present. Provisioned players receive no positional default loadout
or undifferentiated ability-selection substitute.

The old Hub `MatchPlayerLoadoutSnapshot` table is retained only as a
data-preserving schema tombstone because removing it would have required an
unapproved destructive Hub reset. No reducer or provisioner inserts or reads
it; cleanup can only delete pre-cutover terminal rows. Its physical removal
remains explicitly ledgered for the final destructive cutover. The match-side
legacy save reducer and generated bindings are removed. A data-preserving local
publish, protected-loadout verification, full Rust/Python/C# gates, and a live
one-sample exact handoff/cleanup probe passed. Detailed commands and residual
classification are in
`docs/combat-build-progression-phase-3-evidence-2026-08-26.md`.

### Phase 4 — runtime bar, switch, cast, and passive cutover

Deliverables:

- exact per-discipline action-bar resolution;
- atomic discipline/weapon/bar switching;
- build-only player active authorization;
- selected-passive authorization independent of equipped discipline;
- removal of school-as-discipline and profile-neutral copy behavior;
- removal of learned-spell/spellbook alternate player authorization; and
- exhaustive tests of every player cast and passive entry point.

Exit gate: the runtime scenarios in §11 pass, and instrumented denials prove
that dormant/unassigned/wrong-school abilities cannot execute.

### Phase 5 — Unity plumbing for the future editor (no new screen)

Deliverables:

- generated Hub/match bindings consumed by network state;
- one client draft model mirroring the canonical contract;
- removal of client primary/secondary and locally invented budget validators;
- current gameplay HUD/input consumes per-discipline exact assignments; and
- current equipment editing, if retained, writes the same atomic Hub build
  contract and edits the selected discipline's configuration.

This phase does not build or restyle the replacement Disciplines UI. The old
screen may be disabled if it cannot safely edit the new contract; it must not
remain able to write a legacy shape.

Exit gate: ordinary Unity compilation and edit-mode tests pass, and the
existing gameplay HUD can switch/read the three frozen bars. Per repository
policy, Unity batch mode is not used without specific current-chat approval.

### Phase 6 — replacement UI (future session)

Backend contract consumed by that future task:

- ordered selection of one to three disciplines;
- optional starting/default discipline selection;
- one-to-three Staff schools only inside Staff configuration;
- 20-point active/passive combined budget and 16-active subcap display;
- exact active slot assignment per discipline;
- selected passive editing;
- per-discipline validity and atomic whole-draft save; and
- dormant configuration preview/restoration.

Exit gate: UI behavior tests exercise the same server error codes and do not
reimplement validation as an independent authority.

### Phase 7 — destructive legacy removal and final audit

This is a required phase, not optional cleanup. It needs explicit approval
because final schema removal may reset local databases.

Deliverables:

- remove every production row, reducer, field, helper, default, adapter,
  subscription, generated binding, UI call, benchmark assertion, and doc
  assertion listed in §10;
- regenerate bindings from the final schema rather than hand-editing them;
- delete temporary converters/adapters after their one approved use;
- revise or archive conflicting current design documents/prototypes;
- run the negative and positive audits in §12; and
- publish `docs/combat-build-progression-cutover-evidence-YYYY-MM-DD.md` with
  commands, outputs, schema/table list, grep allowlist, tests, manual probes,
  and the completed deletion ledger.

Exit gate: zero unresolved ledger items, zero unapproved allowlist hits, and all
tests/interactive probes pass. Until this gate passes, the migration is not
finished and must not be reported as complete.

## 9. Deterministic legacy-data conversion

If Phase 0 approves preserving current local Hub builds, the offline converter
must be pure, versioned, tested, and deleted from production after use:

1. map each legacy selected discipline with the §5.1 table;
2. preserve original selection order after de-duplicating mapped IDs (multiple
   selected schools collapse into one `STAFF` slot);
3. set `starting_discipline_id` unset so slot 0 remains the fallback;
4. migrate each legacy ability to its canonical discipline; for old spell
   disciplines, also add its exact consolidated school to Staff configuration;
5. classify selected actives/passives from canonical catalog metadata;
6. assign legacy actives, which have no durable exact-slot information, to the
   first free legal slots in stable legacy order and record that fact in the
   conversion report;
7. preserve the current saved weapon/color pair for the mapped owning
   discipline and seed missing discipline weapon configurations from canonical
   starter definitions;
8. never interpret a damage type as a Staff school;
9. validate the complete converted draft with the production validator; and
10. stop and report any build over 20 total abilities, over 16 active
    abilities, or otherwise invalid. Do not prune, refill, or run a legacy
    fallback.

The report includes owner, old revision, new draft hash, mapping decisions,
and every failure. Import occurs only after the report is accepted.

## 10. Required deletion ledger

Phase 0 expands this table to every concrete symbol found by repository search.
Phase 7 closes every row. `Removed` means absent from production source,
generated bindings, current docs, current prototypes, tests, and operations;
historical archived documents may retain the term only with an explicit
superseded/archive status and are excluded by a documented audit allowlist.

Documentation reconciliation performed on 2026-08-26 is recorded in
`docs/combat-build-documentation-conflict-audit-2026-08-26.md`. Conflicting
designs and prototypes were either revised or explicitly archived in place to
preserve linked provenance. That closes their status as competing current
documentation; it does not close any runtime, schema, generated-binding, or
legacy Toolkit UI ledger item.

| Legacy concept/path | Known current locations | Required final state |
|---|---|---|
| Primary/secondary limits and defaults | `server/src/progression.rs`, `hub-server/src/lib.rs`, `DisciplineLoadoutRules.cs` | constants and behavior removed |
| Fixed primary + two secondary storage | `CharacterDisciplineLoadout`, `HubPlayerLoadout`, Hub snapshot, `MatchReservation` | old fields/tables removed |
| Ambiguous `selected_ability_ids` | Hub, snapshot, reservation, provisioner, benchmark | exact active assignments + passive rows only |
| School rows inside discipline catalog | `progression_catalog.shared.json`, Hub projections, Disciplines prototype | separate school catalog only |
| `discipline_kind == SPELL_SCHOOL` branching | server, Hub, client/prototype | removed |
| Old selectable IDs and profile mappings | `SUBTLETY/WAR/ZEAL/PRECISION`, `combat_discipline_for_profile`, Staff fallback-to-`ARCANA` helpers | converted to canonical discipline IDs; mapping helpers removed |
| Redundant public combat-profile selection | `CombatProfileCatalog`, active discipline/profile pairs, client resolvers | deleted or strictly private derived type with no state/selection |
| Weapon `primary_discipline_id` metadata | weapon appearance catalog, Hub weapon definition, equipment filters | renamed/rekeyed to canonical `combat_discipline_id` |
| One global/primary Hub weapon pair | `HubPlayerLoadout`, equipment save/default/reconcile code | per-discipline configuration only |
| Match-side durable loadout save | `save_character_discipline_loadout` and related reducers | removed; Hub is durable authority |
| Profile-neutral spell copied to every bar | action-bar synthesis in `progression.rs` | removed; explicit intrinsic path only |
| Profile/discipline-only passive gates | `server/src/progression.rs`, passive branches in `server/src/combat.rs` and spell code | one selected-passive predicate |
| Learned/spellbook alternate authorization | `player_knows_spell`, `PlayerKnownSpell`, `learn_spell`, spellbook-capacity/action-bar/cast checks | removed from player build/cast authorization |
| Positional bootstrap loadout fields | `server/src/match_contract.rs`, `match_provisioner/worker.py`, `test_worker.py` | one versioned structured snapshot |
| Legacy Unity snapshot/save DTOs | `HubNetworkManager.cs`, generated Hub/match bindings, subscriptions | regenerated/rewritten for canonical model |
| Client-local primary/secondary validator | `DisciplineLoadoutRules.cs`, `DisciplinesScreen.cs` | shared projected rules + server errors |
| Session-only stat-allocation controls presented as progression | `DisciplinesScreen.cs`, `DisciplineLoadoutRules.cs` | removed from combat-build screen; no replacement invented here |
| Current legacy Disciplines UI/prototype | Toolkit screen/UXML/USS and `docs/ui-prototypes/disciplines` | disabled then replaced/archived; no writer remains |
| Legacy Equipment discipline filtering | `EquipmentScreen.cs` | edits canonical discipline configuration |
| Legacy benchmark assumptions | `ops/benchmark-local-match-start.py` | validates full canonical snapshot/handoff |
| Conflicting current documentation | `docs/combat-authoring-contract.md`, `docs/spellbook-composer-design-2026-07-20.md` | revised/archived 2026-08-26; retain audit classification |
| Damage types called spell schools | `docs/reward-choice-flow-design-2026-07-25.md`, reward-choice prototype variables/content | archived in place 2026-08-26 as incompatible; no longer current authority |
| Temporary migration adapters/tools | Phase-specific code | deleted after verified cutover |

Renaming a symbol while preserving its old authority does not close a ledger
row. Leaving a compatibility read, fallback default, legacy reducer, or hidden
UI call also does not close it.

## 11. Required behavior scenarios

Automated tests and an interactive local probe must cover at least:

1. **Three weapon bars:** select Daggers, Bow, Staff. Each has different exact
   assignments. Switching weapons changes equipment and the actionable bar,
   then switching back restores both.
2. **Mixed Staff schools:** Staff selects Ruin + Arcana and assigns abilities
   from both to the same Staff bar. Exactly one Staff weapon/configuration/bar
   exists.
3. **School rejection:** a Primal Staff ability is rejected when only Ruin +
   Arcana are selected; a damage type is rejected as a school ID.
4. **Combined and active budgets:** actives and passives across all selected
   disciplines count together. `16 active + 4 passive` and
   `15 active + 5 passive` succeed at 20 total; 17 active fails even when total
   is no more than 20; and 21 total fails even when active count is no more
   than 16.
5. **Per-discipline minimum:** a selected discipline with zero active/passive
   choices is rejected; one passive alone or one active alone satisfies it.
6. **Passive independence:** a selected Staff passive remains active while Bow
   is equipped and through every weapon swap; it stops when Staff leaves the
   active build.
7. **Dormant preservation:** remove a configured discipline, save another
   valid build, reload, re-add it, and recover weapon, bar, passives, and Staff
   schools without those rows affecting the dormant interval's budget/effects.
8. **Start behavior:** absent an explicit default, the first selected
   discipline starts equipped. A fixture with a valid future default proves
   the field can override slot 0 without changing selection semantics.
9. **Freeze isolation:** edit the Hub after a ticket snapshot; the running
   match keeps the captured revision.
10. **Authorization denial:** unassigned, dormant, wrong-bar, wrong-school,
    learned-only, and spellbook-only abilities all fail; assigned current-bar
    abilities succeed when normal combat conditions pass.
11. **Weapon validation:** each discipline restores its own legal weapon pair;
    cross-discipline weapons and illegal hand pairs fail atomically.
12. **Catalog removal:** a now-invalid dormant reference is reported and
    blocks reactivation; it is never silently replaced.

## 12. Final proof: no parallel or conflicting paths

### 12.1 Negative source audit

The final evidence log records scoped `rg` commands and explains every
allowlisted hit. Outside generated migration fixtures and explicitly archived
history, production/current-source searches must find no:

- `primary_discipline_id`, `secondary_discipline_id_1`, or
  `secondary_discipline_id_2` loadout fields;
- primary/secondary discipline minimum constants or validation messages;
- `selected_ability_ids` used as an undifferentiated combat-build list;
- `SPELL_SCHOOL` discipline kind or school-as-discipline branch;
- fallback mapping of `STAFF` to `ARCANA`;
- profile-neutral ability replication across bars;
- player cast/bar authorization through `player_knows_spell`, learned rows, or
  equipped spellbook contents;
- legacy save reducer or positional bootstrap signature; or
- old generated fields, prototype writers, benchmark keys, and current-doc
  assertions.

The legacy IDs may still appear as `spell_school_id` values (`ARCANA`, etc.) or
inside an archived/conversion fixture. Each such hit must be classified; a
global text search alone is not treated as proof.

### 12.2 Positive ownership audit

The evidence log must also prove:

- exactly one Hub durable write path calls the canonical validator;
- every ticket snapshot originates from one validated Hub revision;
- every player active entry point checks an exact current-discipline assignment;
- every player passive effect checks the canonical selected-passive predicate;
- every discipline switch gets weapon and bar from the same frozen discipline
  configuration;
- Staff school membership is validated in catalog, save, snapshot/bootstrap,
  and runtime authorization;
- all rule constants come from one authored projection; and
- generated bindings match the final Hub and match schemas.

### 12.3 Verification commands and artifacts

The final phase runs and records, in proportion to the changed surfaces:

- full `server` Rust tests;
- full `hub-server` Rust tests;
- `match_provisioner/test_worker.py`;
- contract/catalog validation and migration fixtures;
- Unity compilation and relevant edit-mode tests through a non-batch workflow;
- `ops/setup-local-multiplayer.sh status` and the canonical local stack only
  when an explicitly approved interactive end-to-end probe is being run;
- updated `ops/benchmark-local-match-start.py` loadout snapshot/handoff guard;
- Hub-before/Hub-after snapshot evidence showing no unintended mutation; and
- the twelve scenarios in §11.

Any skipped command, environment limitation, manual-only gate, or allowlisted
legacy hit is written explicitly. “Tests pass” without command output and the
completed ledger is not sufficient completion evidence.

## 13. Definition of done

This progression migration is complete only when:

- the canonical model and every locked rule in §§2–7 are implemented;
- builds accept at most 20 total abilities and at most 16 active assignments,
  with no independent passive cap;
- the replacement UI phase, when separately approved, edits exact canonical
  slots and has no legacy writer;
- all active and passive player authorization derives from the frozen build;
- Staff schools configure one Staff discipline and are nowhere treated as
  disciplines;
- dormant per-discipline configuration persists without granting effects;
- the Hub-to-match snapshot is immutable and exact;
- all deletion-ledger rows are closed;
- negative and positive audits in §12 pass;
- current documentation uses “school” only for the six consolidated Staff
  schools and “damage type” for granular damage categories; and
- the dated evidence document is checked in alongside the final implementation.

Passing an intermediate phase, retaining a compatibility path “temporarily,”
or merely hiding the old UI does not satisfy this definition.

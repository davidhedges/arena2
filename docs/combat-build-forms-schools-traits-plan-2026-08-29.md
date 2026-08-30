# Combat Build V2 — Forms, Schools, Combat Features, and Traits

Date: 2026-08-29

Status: **DRAFT FOR OWNER REVIEW.** This document proposes a new combat-build
contract; it does not authorize implementation of any phase. Until a phase is
explicitly approved and its exit gate passes, the completed v1 combat-build
contract remains authoritative.

Baseline:

- `docs/combat-build-progression-cutover-plan-2026-08-26.md`
- `docs/combat-build-progression-cutover-evidence-2026-08-27.md`
- `docs/combat-build-probe-recovery-evidence-2026-08-29.md`

## 1. Goal

Replace the current ability-centric combat build with a hierarchy that creates
distinct, constrained builds:

```text
Combat Discipline (weapon family or Spellcasting/Staff)
  -> Specialization (weapon Form or Spellcasting School)
    -> Combat Feature (Technique, Spell, or Form/School-scoped passive)

Character
  -> Trait (character-wide passive, independent of Discipline/Form/School)
```

A character selects up to three Forms and/or Schools. These are the top-level
build slots. Multiple selected Forms may belong to the same weapon Discipline,
and multiple selected Schools all belong to Spellcasting/Staff. Selected
Combat Features share one global capacity of 18. Selected Traits use a
separate capacity of 3.

This plan is build composition only. It does not introduce levels, unlock
requirements, capacity growth, respec costs, or other progression pacing.

## 2. Scope assessment

This is a broad contract migration but not an animation rewrite.

| Surface | Expected change | Risk |
|---|---|---|
| Ability/Form/School taxonomy | Every retained selectable player ability must receive one reviewed specialization and loadout classification; removed abilities need an explicit deletion disposition | High |
| Pure build contract | Top-level selection changes from unique Disciplines to Specializations; capacities and dynamic bar ordering change | High |
| Hub persistence and snapshot | Current v1 tables and JSON snapshot cannot represent repeated parent Disciplines or a global Spell bar | High |
| Match authorization | Techniques remain weapon-gated; Spells become selected-build-gated but weapon-independent | High |
| Passive authorization | Existing passives divide into specialization-scoped passives and character-wide Traits | Medium-high |
| Build editor and HUD | Hierarchical editor plus simultaneous Spell/Technique bars replace the current per-Discipline bar model | High |
| Cast cancellation | Weapon switch and all other accepted action paths must use the existing authoritative interrupt flow | Medium |
| Animation | Preserve the existing global spell mapping and equipped-Discipline override architecture | Low |

The largest uncertainty is content classification, followed by persistent-data
reset safety and action-bar/input topology. Animation should remain a guarded
integration surface.

## 3. Terminology

These names distinguish structural concepts. User-facing copy may be refined
without changing the contract.

### 3.1 Combat Discipline

A weapon/runtime equipment family. Preserve the five current canonical IDs:

| Discipline | Canonical ID | Runtime weapon |
|---|---|---|
| Daggers | `DAGGERS` | Daggers |
| Two-Handed Sword | `TWO_HANDED_SWORD` | Two-Handed Sword |
| Sword & Shield | `SWORD_AND_SHIELD` | Sword and Shield |
| Bow | `ARCHER_BOW` | Bow |
| Spellcasting | `STAFF` | Staff |

`STAFF` remains the wire ID to avoid an unrelated identifier migration;
“Spellcasting” may be its user-facing label.

### 3.2 Specialization

The internal generic identity for either:

- a weapon **Form**; or
- a Spellcasting **School**.

Specializations, not Disciplines, occupy the build's three top-level slots.
The UI always says Form or School as appropriate; it need not expose the word
“Specialization.”

### 3.3 Combat Feature

The neutral internal term for a specialization-owned selection that consumes
the global combat-feature capacity:

- **Technique** — active, owned by a non-Staff weapon Form, and usable only
  while its parent Discipline's weapon is equipped;
- **Spell** — active and usable with any currently equipped weapon; or
- **Perk** — a passive selection owned by a Form/School.

“Perk” is the approved user-facing name and prevents the contract from calling
passive effects Techniques or Spells.

### 3.4 Trait

A character-wide passive with no Discipline, Form, or School prerequisite. It
uses the separate Trait capacity and does not satisfy a selected
Specialization's nonempty requirement.

### 3.5 Gameplay and presentation kinds

Loadout identity must remain independent of implementation and animation:

- `loadout_kind` determines selection pool, bar, and weapon requirement;
- existing `selection_kind` continues to distinguish active, passive, and
  intrinsic records where useful;
- existing `gameplay.kind` continues to choose the authoritative execution
  path (`MELEE`, `SPELL`, `MOVEMENT`, `PASSIVE`, and so on); and
- animation presentation continues to resolve independently from the loadout
  classifier.

A Technique may legitimately use the current spell execution or spell-action
animation pipeline. Reclassifying it as a Technique must not force it into the
melee Animator path.

## 4. Locked product rules from owner discussion

1. A build selects one to three distinct Specializations.
2. A Form and a School each consume exactly one top-level slot.
3. Multiple selected Forms may have the same parent weapon Discipline. Two or
   three Dagger Forms are legal.
4. Multiple selected Schools are legal and all have parent Discipline
   `STAFF`. Three Schools and no weapon Forms means the character only ever
   equips a Staff.
5. The same Form or School cannot occupy more than one slot.
6. Selecting multiple Forms from one Discipline does not create duplicate
   weapons, weapon configurations, switch targets, or Technique bars.
7. All selected Techniques, Spells, and Perks count against one global
   combat-feature capacity of 18. There is no per-Form, per-School,
   Technique-bar, or Spell-bar capacity. Every selected active feature appears
   on its applicable bar; the global feature capacity is the only active-build
   selection limit.
8. Traits use a separate capacity of 3 and are independent of all Disciplines,
   Forms, and Schools. The initial Trait catalog contains `MASTERY`.
9. Every selected Form/School must contribute at least one selected Combat
   Feature. A Trait does not satisfy this rule. An empty build cannot be saved.
10. Removing a Form/School makes its feature selections dormant rather than
    deleting them. Dormant selections grant no authorization, passive effect,
    bar presence, or capacity usage. Reactivation never fails only because a
    preferred bar position is occupied: active ordering remains stable and
    returning features are placed deterministically into the next available
    positions.
11. All selected Spells appear on one character-wide Spell bar. The Spell bar
    is always present and does not change when weapons switch.
12. All selected Techniques for the currently equipped non-Staff Discipline
    appear on one Technique bar. Techniques from multiple selected Forms of
    that same Discipline merge into that bar.
13. Schools cannot own Techniques, and `STAFF` has no Technique bar. The
    current `STAFF_STRIKE`, `STAFF_STRIKE_2`, `STAFF_SWEEP`, and `STAFF_THRUST`
    player ability records are removal-ledger items. Private strike/clip data
    required by the ordinary Staff autoattack may remain, but it cannot grant
    a selectable/intrinsic Technique or bar action.
14. A Technique can execute only when its parent non-Staff Discipline is
    currently equipped. Selection places it automatically on that
    Discipline's merged bar.
15. A Spell can execute while any selected Discipline is equipped, including
    without a Staff. Selection places it automatically on the global Spell bar.
16. Switching Disciplines resets auto-attack timing and weapon-specific action
    state. A later Trait/Perk may modify this, but no such exception is part of
    this plan.
17. Movement, jumping, weapon switching, or another accepted combat action
    during a cast cancels that cast. Future exception Traits/Perks are outside
    this plan.
18. Spell animation defaults are global per spell/action. The currently
    equipped Discipline may override the recipe when its combat pose requires
    it.
19. Multiple Forms from one Discipline share that Discipline's animation set.
    No Form-level animation set or animation override is introduced.
20. A selected Perk is active whenever its source Form/School is selected,
    regardless of the currently equipped weapon. It stops applying when its
    source becomes dormant. Individual effects may still modify only matching
    attacks, weapons, damage types, or states.
21. `MASTERY` grants bonus outgoing damage only when it is selected and the
    frozen build derives exactly one distinct parent Discipline. Multiple Forms
    from one weapon Discipline or multiple Schools under `STAFF` still count as
    one Discipline. Its modifier applies through the shared authoritative
    outgoing-damage path to autoattacks, Techniques, Spells, and owned
    periodic/projectile damage. System damage and packets already authored or
    copied as final damage remain on their existing no-rescale path to prevent
    double dipping.
22. Staff retains its ordinary autoattack. Removing Staff Techniques does not
    make Staff incapable of basic attacks.

## 5. Resolved decisions and remaining Phase 0 authoring

The owner has resolved the product-level structure:

1. **Initial Forms and ownership.** Phase 0 seeds a small coherent initial Form
   catalog for every non-Staff Discipline and assigns every retained non-Trait
   player feature to exactly one Form or School. This is authoring work, not a
   request for new ability mechanics or balance changes. Schools may own only
   Spells and Perks, and every seeded Form/School must own enough content to
   support a legal nonempty selection.
2. **Capacity and bars.** The global combat-feature capacity is 18. Spell and
   Technique bars have no separate selection cap and must dynamically
   accommodate every selected active feature. Therefore:

   ```text
   selected Techniques + selected Spells + selected Perks <= 18
   displayed global Spells = every selected Spell
   displayed Techniques = every selected Technique for the equipped parent
   ```

3. **Trait launch.** Trait capacity is 3 and v2 ships with one initial Trait,
   `MASTERY`, using the one-distinct-parent rule in section 4.
4. **Scoped passives.** “Perk” is the approved name. A selected Perk is active
   whenever its source Form/School is selected, independent of equipped weapon.
5. **Dormant ordering.** Dormant feature choices and preferred order are
   remembered. A reused position is resolved by deterministic reflow/append;
   it never creates a save conflict or silently drops a feature.
6. **Existing Hub data.** Existing combat-build rows will be snapshotted for
   evidence and then reset during the approved cutover. No v1-to-v2 build
   converter will be implemented. Unrelated Hub identity/data is outside the
   reset scope.
7. **Staff basic attack.** Staff retains its ordinary autoattack but owns no
   selectable or intrinsic Technique.

Phase 0 locks `MASTERY` at a 10% outgoing-damage bonus and assigns 18 stable,
direct input identities to the single global active-feature order. The default
keyboard bindings reuse the existing unshifted combat keys (`1`–`0`, `E`, `R`,
`T`, `F`, `G`, `Z`, `X`, `C`). The Spell and current-parent Technique bars are
two projections of that global order, so all 18 legal selected actives remain
directly reachable without a second cap. The complete reviewed contract is in
`docs/combat-build-v2-phase-0-contract-2026-08-29.json`.

## 6. Target catalog contract

### 6.1 Specialization catalog

Add one authoritative catalog:

```text
CombatSpecialization
  specialization_id
  combat_discipline_id
  specialization_kind       FORM | SCHOOL
  display_name
  sort_order
```

Catalog invariants:

- every Specialization has exactly one canonical parent Discipline;
- `SCHOOL` is legal only under `STAFF`;
- `FORM` is legal only under non-Staff weapon Disciplines unless separately
  approved later;
- specialization IDs are globally unique and exact-wire values;
- each of the six current Schools becomes one `SCHOOL` Specialization;
- every selectable Discipline has at least one selectable Specialization;
- Schools may own Spells and Perks but never Techniques; and
- no ability with `loadout_kind=TECHNIQUE` may derive parent Discipline
  `STAFF`.

### 6.2 Ability classification

Add an explicit loadout classifier without overloading `gameplay.kind`:

```text
loadout_kind
  TECHNIQUE
  SPELL
  PERK
  TRAIT
  INTRINSIC
```

Required combinations:

| `loadout_kind` | Existing structural kind | Specialization | Bar/effect |
|---|---|---|---|
| `TECHNIQUE` | Active | Non-Staff Form required | Parent Discipline's Technique bar |
| `SPELL` | Active | Required | Global Spell bar |
| `PERK` | Passive | Required | Passive while source Specialization is selected |
| `TRAIT` | Passive | Forbidden | Character-wide passive |
| `INTRINSIC` | Intrinsic | Forbidden unless explicitly documented | Existing intrinsic path |

`specialization_id` is the authored ownership source. A denormalized
`combat_discipline_id` may remain on published runtime rows to limit consumer
churn, but catalog validation must require it to equal the Specialization's
parent. It cannot become a second ownership authority.

Loadout and executor kinds are orthogonal but not every combination is legal.
A Technique may retain `gameplay.kind=SPELL` when that is its established
execution/presentation path. A weapon-independent Spell may not retain
`gameplay.kind=MELEE`, `AUTO_ATTACK_REPLACEMENT`, `COMBAT_MODE_TOGGLE`, or
another executor that requires its source weapon/current combat profile unless
that executor is first made genuinely weapon-independent and separately
reviewed.

The classification ledger must mark `STAFF_STRIKE`, `STAFF_STRIKE_2`,
`STAFF_SWEEP`, and `STAFF_THRUST` as removed player abilities rather than
assigning them to a School. Their action/clip identifiers may remain only where
private Staff autoattack presentation still requires them.

### 6.3 Required classification ledger

Before changing runtime behavior, produce a machine-readable ledger containing
every selectable player ability with:

- ability and action ID;
- current Discipline, `selection_kind`, and `gameplay.kind`;
- proposed Specialization;
- proposed `loadout_kind`;
- proposed bar domain or passive scope;
- whether it needs its source weapon equipped;
- whether it currently uses spell-cast animation presentation;
- reset/cutover note; and
- owner-review status.

This review is material. The current catalog contains 35 non-Staff active
abilities whose executor is `SPELL`; many are likely Techniques, but changing
their loadout classification must not change their mechanics or presentation.
All 24 current player passives are specialization-scoped and must be assigned
to Forms/Schools as Perks. `MASTERY` is the one initial character-wide Trait.

## 7. Target durable and frozen build contract

The versioned target shape is conceptually:

```text
CombatBuildV2
  owner
  starting_discipline_id           optional
  revision
  updated_at

SelectedSpecialization
  owner
  slot_index                       0..2
  specialization_id               unique per owner

DisciplineConfiguration
  owner
  combat_discipline_id             active or dormant
  main_hand_item_def_id
  main_hand_color_id
  off_hand_item_def_id
  off_hand_color_id

SpecializationFeatureSelection
  owner
  specialization_id               active or dormant
  ability_id
  preferred_bar_order              optional for Technique/Spell; absent for Perk

TraitSelection
  owner
  ability_id
```

The physical SpacetimeDB row keys may differ, but they may not introduce
parallel sources of truth.

### 7.1 Derived runtime projections

From the selected Specializations derive:

- the ordered unique parent Disciplines, using first Specialization occurrence
  to determine fallback switch/start order;
- exactly one weapon configuration per distinct parent Discipline;
- one merged Technique list per distinct non-Staff parent
  Discipline;
- one global Spell list;
- one selected Perk set; and
- one selected Trait set.

Top-level Specialization slots are build-composition slots, not runtime weapon
switch slots. For example:

```text
Selected: Dagger Form A, Dagger Form B, Ruin School
Weapons:  DAGGERS, STAFF
Bars:     one merged Dagger Technique bar and one always-present global
          Spell bar; Staff has no Technique bar
```

### 7.2 Dormant state

Persist configurations for unselected Specializations and Disciplines:

- dormant Specialization features retain their chosen IDs and preferred order;
- dormant Discipline configurations retain weapons and colors;
- dormant selections do not count, authorize, or apply effects;
- catalog-invalid dormant references are reported and block reactivation; and
- reactivation never prunes or replaces content; active ordering remains stable
  while any returning position collision is resolved by deterministic
  reflow/append.

### 7.3 Dynamic bars, ordering, and input

Every selected active feature is automatically actionable on its applicable
bar. There is no separate assigned-versus-selected state and no fixed Spell- or
Technique-bar capacity. At most 18 active features can exist because active
features and Perks share the global capacity.

`preferred_bar_order` controls presentation and input ordering only; it grants
no authorization. The canonical merge preserves active feature order, accepts
the returning dormant feature's preferred position when free, and otherwise
places it into the next available position using one deterministic server and
client algorithm. Duplicate or sparse authored preferences are normalized and
returned in the saved aggregate rather than rejected as position conflicts.

Ordering scopes are:

- Spell: one global order across every selected Specialization;
- Technique: one order among selected Forms sharing the same non-Staff parent
  Discipline; and
- Perk/Trait: no bar order.

The Phase 0 input contract defines stable `COMBAT_ACTION_00` through
`COMBAT_ACTION_17` identities. Keyboard defaults use the 18 existing unshifted
combat keys; controller and user-rebinding presentation may map those same
identities without changing authority, ordering, or capacity. No selected
active may become unreachable or encounter a hidden cap below 18.

## 8. Authoritative validation invariants

One pure validator must be reused by Hub save, ticket freeze, match bootstrap,
local-direct test admission, and reset/default seeding.

A v2 draft is valid only when:

- revision and schema version are current;
- selected Specialization count is 1..3;
- selected slots are contiguous and unique;
- specialization IDs are canonical and distinct;
- repeated parent Discipline IDs are allowed;
- the optional starting Discipline is among the derived unique parents;
- every derived parent Discipline has exactly one valid weapon configuration;
- every selected Specialization owns at least one selected Combat Feature;
- every selected feature is owned by its referenced Specialization;
- every selected feature has the correct `loadout_kind` and structural kind;
- Techniques are owned by Forms with non-Staff parent Disciplines;
- Schools own no Techniques and Staff produces no Technique assignments;
- total selected Techniques + Spells + Perks is at most 18;
- selected Traits are within the separate Trait capacity;
- Traits do not count toward the global feature capacity or nonempty minimum;
- selected Techniques and Spells are automatically included in their derived
  bar lists;
- ordering preferences are scoped and normalized under section 7.3 and cannot
  grant authorization;
- Perks and Traits have no bar order;
- ability IDs cannot be selected twice anywhere in the active build;
- Schools always derive parent Discipline `STAFF`;
- Forms derive their authored weapon Discipline;
- loadout/executor combinations satisfy the weapon-independence rules in
  section 6.2;
- dormant rows are individually catalog-valid but excluded from all active
  counts, minima, authorization, and effects; and
- an invalid draft fails atomically without bumping revision or rewriting
  feature selections. Canonical bar-order normalization is returned explicitly
  in the saved aggregate.

The server returns stable error codes. The client may preview the same rules
but never becomes an independent validation authority.

## 9. Runtime authorization and behavior

### 9.1 Techniques

A Technique request succeeds only when:

- the Technique is selected from a currently selected Specialization;
- its source Specialization is a Form with a non-Staff parent Discipline;
- that parent Discipline is currently equipped; and
- normal resource, cooldown, target, state, and gameplay rules pass.

The predicate must not care which of several same-Discipline Forms supplied
the other selected Techniques on the merged bar. Bar position is not an
authorization input.

No equivalent Staff predicate exists. Staff has an ordinary autoattack but no
selected or intrinsic Technique action.

### 9.2 Spells

A Spell request succeeds only when:

- the Spell is selected from a currently selected Specialization;
- normal resource, cooldown, target, state, and gameplay rules pass.

The current equipped Discipline is not an authorization requirement. The
Spell remains selected once, not copied into every Technique bar.

### 9.3 Perks

A Perk applies when:

```text
Perk is selected
AND its source Specialization is selected
```

Equipped Discipline is not part of this common predicate. Individual effects
may still apply only to matching actions, weapons, damage types, or states.
Every current passive call site must be re-inventoried against this predicate.

### 9.4 Traits

A Trait applies when it is in the selected Trait set. It has no Discipline,
Form, School, weapon, or action-bar prerequisite. The initial `MASTERY` Trait
additionally requires the frozen build to derive exactly one distinct parent
Discipline. Its bonus is applied in the shared authoritative outgoing-damage
multiplier path in `resolve_damage_amount` and is recalculated from frozen build
composition, not from the currently equipped weapon. Existing final/copied or
system-damage bypasses remain intact so the same damage is not amplified twice.
`Weaponmaster` remains a future example only; this plan does not implement its
global-cooldown behavior.

### 9.5 Weapon switching

Runtime weapon switching iterates distinct derived parent Disciplines, not the
three Specialization slots. Switching:

1. verifies the target parent Discipline is represented by at least one
   selected Specialization;
2. cancels any active cast through the existing authoritative fizzle path;
3. equips the target Discipline's one frozen weapon configuration;
4. resets auto-attack timing, combo/potential state, and other documented
   weapon-specific transient state;
5. updates resource/mode behavior; and
6. exposes the target Discipline's merged Technique bar when the target is
   non-Staff, hides the Technique bar when the target is Staff, and leaves the
   Spell bar unchanged.

No duplicate Dagger or Staff switch entries are produced when multiple Forms
or Schools share that parent.

### 9.6 Cast interruption

Inventory every accepted player action entry point and route it through one
documented active-cast interruption policy. At minimum cover:

- movement and jump intent;
- weapon/Discipline switch;
- Technique activation;
- another Spell activation;
- dodge, block, parry, interact, and fixed combat actions where applicable;
- auto-attack start; and
- server-imposed interrupts such as stagger, knockback, and death.

The server remains authoritative. Local prediction may hide the cast bar and
preempt presentation immediately, but it cannot be the sole cancellation
source. Existing spell-specific release/channel terminal behavior remains
intact unless an audited action explicitly interrupts it.

## 10. Action bars and UI behavior

### 10.1 Build editor

The editor presents:

- three ordered Form/School slots;
- parent Discipline and Form/School identity in each slot;
- one global 18-point combat-feature capacity meter;
- per-Specialization Technique, Spell, and Perk choices without per-Form caps;
- a separate three-point Trait panel containing `MASTERY`;
- one reorderable, dynamically sized global Spell list;
- one reorderable, dynamically sized merged Technique list per distinct
  non-Staff parent Discipline;
- one weapon configuration per distinct parent Discipline;
- an explicit invalid state for empty Specializations and capacity overflow;
  and
- dormant configuration restoration with deterministic ordering reflow.

The editor submits one revision-checked whole-build draft.

### 10.2 Runtime HUD

The gameplay HUD shows:

- one Spell bar that is always present;
- one Technique bar for the currently equipped non-Staff Discipline, hidden
  while Staff is equipped;
- one weapon-switch entry per distinct parent Discipline;
- cooldown/resource/unavailable state on both bars; and
- stable key labels sourced from the reviewed input/access contract.

With three Dagger Forms, the character has one Dagger switch entry and one
merged Dagger Technique bar. With three Schools, the character has one Staff
switch entry, no Technique bar, and the global Spell bar.

## 11. Animation guardrails

The implementation must preserve the current animation architecture:

```text
action/spell ID
  -> global SpellCastAnimationMap / shared catalog recipe
    -> optional override from the currently equipped CombatAnimationSet
```

Rules:

- no Form- or School-specific `CombatAnimationSet`;
- no Form-level spell animation override;
- no duplicated animation recipe per Form/School;
- no new Animator Controller, layer, or state topology for this migration;
- `loadout_kind` does not choose the animation subsystem;
- Techniques may keep existing spell-action presentation when appropriate;
- every semantic Spell must resolve, or explicitly select no animation, under
  every equippable `CombatAnimationSet`;
- cast-time Spell hold/release/cancel presentation must validate under every
  equippable Discipline;
- the equipped Discipline, not the source Specialization, selects any
  Discipline animation override; and
- the existing cancel phase must clear held presentation and temporary props
  before or while a weapon switch applies the new animation set.

`PALADIN_BLESSED_SHIELD` is a required classification/content review item. Its
current presentation duplicates an equipped shield. If it becomes a
weapon-independent Spell, it needs a weapon-independent presentation; if its
shield requirement is essential, it should be a Technique.

Animation-map discovery and base presentation validity continue to follow the
actual execution/presentation path, currently `gameplay.kind=SPELL` and the
spell-cast map. This preserves validation for Techniques that legitimately use
the spell pipeline. `loadout_kind` adds the required compatibility scope:

- semantic Spells validate resolution, hold/release/cancel behavior, VFX
  anchors, and temporary props under every equippable `CombatAnimationSet`;
- Techniques using spell presentation validate under their one non-Staff
  parent Discipline; and
- explicit no-animation entries remain deliberate authored outcomes.

Phase 0 records this compatibility matrix before implementation. It extends
the existing all-set resolver checks rather than assuming every Staff spell
needs new animation content.

## 12. Persistent-data and snapshot cutover

Existing v1 combat builds will not be converted. The owner has approved a
combat-build reset because v1 cannot represent the new specialization,
dynamic-bar, and capacity contract without non-lossless guesses.

Before mutation, Phase 0 records a read-only inventory and recoverable snapshot
of existing combat-build rows, including owner counts and revisions. The
coordinated cutover then deletes only obsolete v1 combat-build/loadout rows and
seeds a legal v2 default build under the reviewed Form/School catalog. Identity,
account, and unrelated Hub data are outside the reset scope.

No v1-to-v2 converter or preservation policy is implemented. The reset must be
explicit in the Phase 7 command/evidence, verify its exact row targets before
mutation, and prove afterward that obsolete build rows are gone, every owner
receives a valid v2 aggregate when required by the normal defaulting path, and
unrelated Hub data remains intact.

The frozen ticket snapshot receives a new schema version. Old in-flight
snapshots fail closed after the coordinated cutover; they are not interpreted
with guessed v2 semantics. Hub, provisioner, PvP match, open-world match, local
direct probes, and generated clients must change in one reviewed handoff.

## 13. Implementation phases

Approving this document does not approve a phase. Before editing a phase,
state its exact boundary and obtain explicit owner approval. Stop at each exit
gate.

The current v1 local stack remains the canonical runnable authority until the
coordinated Phase 7 cutover. Phases 2–6 must nevertheless prove their contracts
against phase-owned, throwaway local Hub/match databases and anonymous probes
where their surface permits it. Those rehearsal resources may publish complete
v2 schemas and reducers only under isolated disposable identities; they must
not write canonical Hub state, accept user traffic, or become a second
production/local-player authority. Each phase deletes or clearly retires its
rehearsal resource after evidence is captured.

### Phase 0 — decision lock, classification ledger, and baseline

Deliverables:

- lock the `MASTERY` percentage and 18-active-feature input/access scheme from
  section 5;
- author the complete initial Form catalog, seeding at least one nonempty Form
  for every non-Staff Discipline;
- define the legal post-reset default v2 build using that seeded catalog;
- classify every retained selectable player ability by Specialization and
  `loadout_kind`, and give every removed ability an explicit disposition;
- assign all 24 current passives to Forms/Schools as Perks and author the new
  `MASTERY` Trait catalog/presentation entry;
- add `STAFF_STRIKE`, `STAFF_STRIKE_2`, `STAFF_SWEEP`, and `STAFF_THRUST` to
  the explicit player-ability removal ledger, separately identifying any
  private action/clip data still required by the ordinary Staff autoattack;
- record the dynamic-bar ordering and input/access decision;
- create v2 valid/invalid fixtures for every invariant in section 8;
- record the read-only v1 combat-build inventory and recoverable pre-reset
  snapshot;
- record the animation compatibility matrix described in section 11;
- record full server, Hub, provisioner, compile, and relevant client-test
  baselines.

Exit gate: every retained player ability has approved ownership and loadout
classification, all four Staff melee player abilities have an approved
removal/private-data disposition, the initial Form catalog and `MASTERY`
percentage are reviewed, every selected active is reachable through the
approved input scheme, the reset targets are exact, and no baseline failure is
unclassified.

### Phase 1 — catalog taxonomy and pure v2 validator

Deliverables:

- Specialization catalog and projected rules;
- `loadout_kind` and Specialization ownership on every retained player
  ability;
- removal of the four Staff melee player-ability catalog records from the v2
  projection, with no School Technique projection or Staff Technique bar;
- the `MASTERY` Trait with its one-distinct-parent predicate and reviewed
  damage modifier;
- exhaustive catalog cross-field validation;
- versioned v2 draft/snapshot structures;
- one pure validator implementing section 8; and
- fixture/property tests, including same-Discipline Forms and three Schools.

Boundary: no Hub tables, production save path, match schema, runtime
authorization, UI, or animation assets change.

Exit gate: every retained player ability maps exactly once, every removed
player ability maps nowhere, Staff/School Technique fixtures fail validation,
all other v2 fixtures pass, and the v1 production path remains internally
consistent until the coordinated cutover.

Completion record (2026-08-29): PASS. The v2 catalog and pure validator are
implemented in parallel with the unchanged v1 production contract. All 32
locked fixtures execute, exhaustive catalog projection checks pass, and the
full server and Hub regression suites remain green. See
`docs/combat-build-forms-schools-traits-phase-1-evidence-2026-08-29.md`.

### Phase 2 — Hub v2 persistence in an isolated rehearsal database

Deliverables:

- v2 row/aggregate replacement model for selected Specializations,
  Discipline configurations, Specialization features, and Traits;
- revision-checked save reducer, validation, atomic replacement, default, and
  aggregate projection using the Phase 1 validator;
- dormant Form/School and Discipline persistence semantics;
- Hub unit tests for save/reload, stale revision, rollback, repeated-parent
  Forms, three-School Staff, no Staff Techniques, dormant restore with
  deterministic ordering reflow, `MASTERY`, and capacities;
- an anonymous live save/reload/rejection probe against a phase-owned
  disposable local Hub database.

Boundary: v2 tables, reducers, subscriptions, and generated clients may be
published only to the isolated rehearsal database. Do not publish them to the
canonical Hub identity, connect the player client, or mutate canonical v1
state. The v1 Hub remains the only player-facing writer and reader.

Exit gate: the live rehearsal proves atomic save/reload, stale-write rejection,
rollback, ownership filtering, Staff-Technique rejection, and dormant restore;
the canonical v1 Hub and its saved state are unchanged.

Completion record (2026-08-29): PASS. A separate rehearsal module implements
the revisioned aggregate, caller-filtered view, catalog projections, default,
validation, atomic replacement, and dormant persistence. Its anonymous live
probe passed all six save/reload/rejection checks, proved canonical v1 row
counts unchanged, and retired the disposable database. See
`docs/combat-build-forms-schools-traits-phase-2-evidence-2026-08-29.md`.

### Phase 3 — v2 snapshot and materialization in an isolated rehearsal path

Deliverables:

- versioned v2 snapshot serialization and canonical-byte tests;
- provisioner pass-through and reservation-equality tests using v2 fixtures;
- pure PvP/open-world materialization planning for selected
  Specializations, derived Disciplines, weapons, Techniques, Spells, Perks,
  and Traits;
- local-direct fixture admission through the Phase 1 validator; and
- old-version rejection tests.

Boundary: publish v2 ticket rows, match tables, bootstrap signatures, and
generated clients only under phase-owned disposable local Hub/match identities.
Do not alter the canonical Hub-to-match path or persistent v1 data.

Exit gate: an anonymous live rehearsal round-trips a v2 Hub aggregate ->
canonical snapshot -> provisioner payload -> disposable match state with exact
semantic equality, including three Schools and zero Staff Techniques; the
canonical live path remains v1.

Completion record (2026-08-29): PASS. The shared v2 contract now owns bounded
canonical snapshot bytes and a selected-only materialization plan. Disposable
PvP, open-world, and local-direct identities received the exact frozen Hub
bytes and each materialized three Schools, one Staff parent, zero Techniques,
three Spells, one Perk, one Trait, and Mastery. Schema v1 failed without
mutation, provisioner v2 pass-through/equality fixtures passed, canonical v1
counts stayed fixed, and all rehearsal identities were retired. See
`docs/combat-build-forms-schools-traits-phase-3-evidence-2026-08-29.md`.

### Phase 4 — v2 runtime authorization in a disposable match

Deliverables:

- internal normalized v2 match-build view;
- exact merged Technique-bar authorization by active Discipline;
- exact global Spell-bar authorization independent of active Discipline;
- one selected-Perk predicate based on selected source Specialization;
- one selected-Trait predicate independent of Specialization, including
  `MASTERY`'s one-distinct-parent condition;
- a complete inventory and disposition plan for every player active/passive
  call site; and
- focused authorization tests using v2 fixtures.

Boundary: connect these predicates only to a phase-owned disposable match
schema/reducers. Do not remove canonical v1 authorization, redesign the HUD,
or change animation assets.

Exit gate: anonymous reducer probes show that Spells work under every equipped
Discipline, Techniques fail under the wrong weapon, Staff exposes no Technique
authorization, and dormant/unselected features and unselected Traits fail
closed; `MASTERY` modifies all reviewed outgoing-damage paths only for a
one-parent build, and the call-site inventory is exhaustive.

Completion record (2026-08-29): PASS. The disposable match now reconstructs a
fail-closed normalized v2 view with distinct Technique, Spell, Perk, Trait,
persistent-membership, and Mastery predicates. All eight anonymous live checks
passed across Dagger, Staff, and Two-Handed Sword parents. The generated
inventory classifies all 42 centralized active/passive calls and proves all 19
direct frozen-table accesses remain confined to materialization or central
authorization. Canonical v1 authorization remains untouched. See
`docs/combat-build-forms-schools-traits-phase-4-evidence-2026-08-29.md`.

### Phase 5 — weapon switching, cast interruption, and animation compatibility

Deliverables:

- switch targets derived from distinct parent Disciplines;
- auto-attack and weapon-state reset on switch;
- existing cast-fizzle integration on weapon switch and other accepted action
  paths;
- immediate client presentation cancellation using the existing Cancel phase;
- presentation discovery validation keyed from the execution/presentation
  path, plus compatibility coverage scoped by semantic `loadout_kind`;
- all-Discipline resolution/hold/cancel validation for Spells;
- targeted disposition of equipped-prop exceptions such as Blessed Shield;
- an anonymous switch/cast-cancel/authorization probe in the phase-owned
  disposable match, including switching to Staff and observing no Technique
  authorization.

Boundary: preserve the Animator controller, shared recipe catalog, global map,
and equipped-Discipline override model.

Exit gate: the interrupt matrix passes with no stuck holds/props, same-weapon
Forms do not duplicate switch state, the Staff has no selectable/intrinsic
Technique action, and no animation architecture expansion was required.

Completion record (2026-08-29): PASS. The disposable match now derives switch
targets from distinct selected parents, merges repeated-parent Technique bars,
resets auto-attack/combo/potential/weapon transients on a real parent switch,
and routes all fourteen accepted interrupt families through one modeled
fizzle/Cancel policy. The anonymous live probe passed all eleven checks,
including Staff ordinary auto-attack with no Technique and Blessed Shield prop
cleanup. The generated compatibility audit covers all 104 semantic Spells
under all five equipped animation profiles and all 23 spell-executor
Techniques under their parent profile without changing the animation
architecture. Canonical v1 rows remain untouched. See
`docs/combat-build-forms-schools-traits-phase-5-evidence-2026-08-29.md`.

### Phase 6 — Unity v2 models and views against the rehearsal contract

Deliverables:

- one transport-neutral client draft model mirroring the v2 contract;
- Form/School selection and filtered feature pickers;
- global 18-point feature capacity and separate three-point Trait capacity
  presentation, including `MASTERY`;
- merged Technique and global Spell ordering/editing with no independent bar
  cap;
- per-derived-Discipline weapon editing;
- dormant restoration with deterministic conflict-free reflow;
- exact stable server error display;
- dual-bar HUD/input view models, including a hidden Technique bar while Staff
  is equipped; and
- focused editor/HUD behavior tests using fixtures plus the isolated v2
  rehearsal subscription/save path.

Boundary: the v2 screen/HUD may connect only through an explicit developer
rehearsal configuration to the phase-owned disposable v2 Hub/match identities.
Keep it unreachable from the canonical v1 network contract. Do not add
hand-authored generated bindings or a compatibility save.

Exit gate: ordinary non-batch compilation and focused model/UI behavior tests
pass, and a developer rehearsal demonstrates save/reload plus the required bar
transitions, while the canonical v1 screen, HUD, and saved state remain
coherent.

### Phase 7 — coordinated full-stack v2 cutover

Deliverables:

- activate the Hub v2 tables, one revision-checked atomic save reducer, and
  caller-filtered aggregate subscription;
- activate the v2 ticket snapshot, provisioner transport, PvP/open-world
  materialization, match tables, and runtime predicates;
- regenerate Hub and match bindings through the canonical setup workflow;
- connect the prepared Unity editor/network model to the atomic v2 draft;
- always-present Spell bar;
- current non-Staff-Discipline Technique bar, hidden while Staff is equipped;
- dynamic ordering and reviewed input access for every selected active across
  both bars;
- one switch entry per derived parent Discipline;
- tooltip/icon/cooldown/resource state for both domains;
- remove the v1 writer and conflicting v1 runtime authorization in the same
  cutover;
- remove the four Staff melee player abilities and verify that any retained
  private Staff autoattack presentation data grants no feature authorization;
- execute the approved combat-build-only Hub reset from section 12 and seed
  legal v2 defaults; and
- publish and verify the coherent Hub + disposable-match + client contract.

No intermediate publish may expose v2 on only one side of the Hub,
provisioner, match, or client boundary.

Exit gate: live Hub save/reload, exact snapshot handoff, all runtime
authorization, normal client compilation, both HUD bars, persistent-data
verification, and the required scenarios in section 14 pass in a real local
match. Unity batch mode is not used without specific current-chat
authorization.

### Phase 8 — legacy removal, documentation, and final proof

Deliverables:

- delete v1 selected-Discipline-as-top-level and Staff-school-child schema;
- delete v1 per-Discipline mixed action-bar and passive-selection paths;
- delete obsolete Staff melee ability/selection/authorization data while
  retaining only explicitly audited private Staff autoattack presentation;
- remove adapters, fallbacks, old generated bindings, and obsolete UI code;
- revise current combat-authoring documentation;
- complete negative and positive ownership audits; and
- publish a dated evidence document with the reset/data disposition,
  test results, live probes, artifact provenance, and deletion ledger.

Exit gate: no unresolved legacy authority, no alternate authorization path,
no stale generated contract, and all release gates pass.

## 14. Required behavior scenarios

Automated tests and live probes must cover at least:

1. **Two Dagger Forms:** both consume top-level slots, contribute Techniques,
   share one weapon configuration and merged Dagger Technique bar, and produce
   one Dagger switch entry.
2. **Three Dagger Forms:** all three slots may share `DAGGERS`; no duplicate
   equipment, bar, or switch state is created.
3. **Three Schools:** three School slots derive only `STAFF`; the player equips
   only a Staff, retains one global Spell bar, and has no Technique bar or
   selectable/intrinsic Staff Technique.
4. **Mixed parents:** two Dagger Forms plus one School derive exactly Daggers
   and Staff; the Dagger Technique bar is available with Daggers, is hidden
   with Staff, and the Spell bar stays stable throughout.
5. **Global Spell:** a selected School Spell casts while Daggers, Bow, and
   Staff are equipped and resolves the appropriate equipped-Discipline
   animation presentation.
6. **Form-owned Spell:** a Spell granted by a weapon Form remains usable while
   another Discipline is equipped.
7. **Technique gating:** a selected Dagger Technique fails while Bow is
   equipped and succeeds when Daggers are equipped.
8. **No Staff Techniques:** all four removed Staff melee player abilities are
   absent from selection, bar, snapshot, and authorization projections;
   ordinary Staff autoattack presentation does not authorize one of them as a
   Combat Feature.
9. **Global capacity and bars:** one Form may consume most or all available
   feature capacity; no per-Form or per-bar cap is invented; the nineteenth
   selected feature fails; and every selected active among the legal 18 is
   present and reachable on its dynamic Spell or current-Discipline Technique
   bar.
10. **Empty selection:** a selected Form/School with zero Technique, Spell, or
   Perk choices fails even if Traits or other Specializations are populated.
11. **Mastery:** selected `MASTERY` grants its reviewed outgoing-damage bonus
    to a build containing one Dagger Form, three Dagger Forms, or three Schools;
    it grants no bonus once the build contains two distinct parent Disciplines.
    It obeys the Trait cap, consumes no feature capacity, does not satisfy the
    nonempty requirement, and covers autoattack, Technique, Spell, and owned
    periodic/projectile damage without rescaling existing final/copied damage
    packets a second time.
12. **Perk scope:** selected Perks remain active while their source
    Specialization is selected regardless of equipped weapon, and stop
    immediately when their source Specialization becomes dormant.
13. **Dormant restore:** removing and re-adding a Form/School restores features
    and preferred ordering; an occupied preferred position deterministically
    reflows/appends the returning feature without dropping content or rejecting
    the save.
14. **Switch reset:** switching parent Discipline resets auto-attack timing,
    combo/potential state, and any other audited weapon-specific state.
15. **Cast interruption:** movement, jump, weapon switch, Technique, another
    Spell, and applicable fixed actions terminate the cast authoritatively and
    clear client hold/prop presentation.
16. **Animation exception:** equipped-prop Spells either work under every
    weapon or are classified as Techniques; no missing-weapon visual is
    accepted silently.
17. **Freeze isolation:** editing the Hub after ticket creation does not alter
    the running match's Specializations, features, Traits, bar order, or
    weapons.
18. **Reset isolation:** the approved cutover snapshot is captured, only v1
    combat-build rows are removed, legal v2 defaults are created through the
    normal defaulting path, and unrelated Hub identity/data remains unchanged.
19. **Authorization denial:** dormant, unselected, wrong-weapon,
    unknown-Specialization, and over-capacity requests all fail with stable
    reasons; bar order never grants authority.

## 15. Verification and release safety

Each implementation phase records checks in proportion to its changed surface.
The final gate includes:

- full `server` and `hub-server` Rust suites;
- provisioner and benchmark tests;
- classification-ledger completeness and catalog validation;
- pre-reset v1 combat-build inventory/snapshot plus exact reset-scope and v2
  default-seeding evidence;
- normal non-batch C# compilation and relevant EditMode tests;
- `git diff --check`;
- canonical `ops/setup-local-multiplayer.sh setup` and `status` only in an
  explicitly approved implementation phase;
- verification that the approved combat-build reset preserves unrelated Hub
  state around publication;
- a loadout-aware anonymous Hub-to-match benchmark;
- live scenarios for repeated-parent Forms, three Schools, no Staff
  Techniques, Staff Technique-bar hiding, cross-weapon Spell casting,
  wrong-weapon Technique denial, dynamic uncapped bars, `MASTERY`, and
  interrupts; and
- Unity Hub/showcase observation or explicit diagnostics after regenerated
  bindings are processed.

Do not reconstruct the Hub/disposable-match workflow from lower-level scripts.
Do not execute the approved reset before the explicitly approved Phase 7
cutover, its pre-reset snapshot, and exact-target check. Never run Unity with
`-batchmode` unless that exact run is authorized in the current chat.

## 16. Explicit non-goals

This plan does not include:

- level-based unlocking of the second or third Specialization slot;
- learned abilities, skill trees, XP, or unlock currencies;
- respec costs or cooldowns;
- feature-capacity growth over time;
- implementing `Weaponmaster` or any Trait effect beyond `MASTERY`;
- a weapon-switch global cooldown or an exception to it;
- new Forms/Schools beyond the Phase 0 seeded initial catalog;
- balance redesign of existing abilities, other than the approved `MASTERY`
  damage modifier;
- Form-specific animation controllers, sets, clips, recipes, or overrides;
- rewriting existing spell gameplay merely because its loadout classification
  changes; or
- restoring spellbook/known-spell authorization removed by the v1 cutover.

## 17. Definition of done

Combat Build v2 is complete only when:

- one to three Specializations, including repeated parent Disciplines, are the
  sole top-level build selection;
- every retained selectable player ability has one reviewed `loadout_kind`
  and legal ownership, and every removed ability has an explicit deletion
  disposition;
- the four removed Staff melee player abilities grant no selectable,
  intrinsic, bar, snapshot, or runtime authorization;
- Techniques, Spells, Perks, and Traits use the correct capacity
  and authorization predicates;
- the global feature cap is 18, the Trait cap is 3, and no independent Spell-
  or Technique-bar cap exists;
- empty selected Specializations cannot be saved;
- same-Discipline Forms share one weapon configuration and Technique bar;
- all Spells use one always-present global bar and work under any equipped
  Discipline;
- every selected active feature appears on its applicable dynamic bar and is
  reachable through the reviewed input/access scheme;
- all Techniques require their non-Staff parent weapon to be equipped, and no
  School/Staff Technique path or Technique bar exists;
- `MASTERY` is selectable under the three-point Trait cap and applies its
  reviewed damage bonus exactly when the frozen build derives one distinct
  parent Discipline;
- selected Perks remain active independent of equipped weapon while their
  source Specialization remains selected;
- dormant Specializations restore their feature choices without granting
  dormant effects, and ordering collisions reflow deterministically;
- switching resets weapon-specific state and cancels active casts through the
  authoritative interrupt path;
- the existing global spell animation plus equipped-Discipline override
  architecture remains intact;
- Hub, ticket, provisioner, match, client, and local probes consume the same
  versioned contract;
- the combat-build-only Hub reset and v2 default seeding are explicit and
  verified without changing unrelated Hub data;
- no v1 compatibility writer, authorization fallback, or stale generated
  contract remains; and
- the full automated and live evidence gate passes.

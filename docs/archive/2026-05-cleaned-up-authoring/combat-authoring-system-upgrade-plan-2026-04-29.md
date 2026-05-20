# Combat Authoring System Upgrade Plan

This document is historical. Use `docs/combat-authoring-contract.md` for the current combat authoring contract. Do not copy fixed-charge guidance from this archive; charge-like movement abilities are now selectable `MOVEMENT` abilities.

Date: 2026-04-29
Updated: 2026-04-30

## Purpose

This plan upgrades ability, spell, melee, fixed-action, loadout, and presentation authoring into a system that is easy for new developers to understand and hard for automated agents to misuse.

The current architecture is functional, and the melee/spell divide is mostly intentional:

- spells are behavior-first runtime definitions
- melee attacks are animation-first authored actions
- progression exposes player-facing abilities and loadouts
- fixed actions are UI/input actions that resolve through subclass-owned abilities

The problem is not that these layers differ. The problem is that the contracts are spread across JSON, Unity assets, exported manifests, generated bindings, tests, and tribal knowledge. The upgrade should make those contracts explicit, validated, and discoverable.

The improved plan is compressed around one central implementation idea: resolve the combat action graph once, then use that graph for validation and, later, for generated views only when a real consumer exists.

## Primary Deliverables

The upgrade should leave two current core artifacts:

- `docs/combat-authoring-contract.md`: the human and LLM entry point.
- a `cargo test`-covered internal resolved action graph validator in `server/src/progression.rs`.

The contract doc should be what a new developer or automated agent reads first. The validator is the current machine check. A generated manifest and manifest schema should be added only after a resolver, dispatch path, editor, or audit tool consumes them directly.

Deferred potential artifacts:

- `server/src/combat_action_manifest.generated.json`
- `server/src/combat_action_manifest.schema.json`

Optional artifact:

- `server/src/progression_catalog.schema.json`, but only if it has a named consumer such as editor autocomplete, inline JSON diagnostics, or non-Rust tooling. If Rust serde plus the graph validator are the only consumers, skip this schema to avoid a synchronization tax.

## End State

The end state has one obvious authoring path per action type:

- melee attack: authored in a combat animation set, exposed through progression if selectable
- spell: authored in `spells[]`, exposed through progression if selectable
- fixed action: authored as a fixed-action wrapper that resolves to a concrete subclass ability
- auto-attack: authored as intrinsic combat-profile behavior, not as a selectable ability
- presentation: authored in the combat animation set or VFX registry, never inferred from stale naming conventions

The current validation command is `cargo test` from `server/`. The graph-backed validator answers the first-pass authoring questions that currently catch real mistakes:

- what actions exist?
- where are they authored?
- which subclass and combat profile can use them?
- which loadout slots expose them?
- whether player-facing melee, spell, presentation, and default loadout identity is coherent.

## Design Principles

- Do not flatten melee and spells into one giant schema. They differ for good reasons.
- Do not add a second source of truth for existing data.
- Do not build separate audit, validator, and manifest walkers. Build one resolved graph and emit multiple views.
- Do not leave half-renamed systems behind.
- Prefer generated manifests, schemas, and validation reports over comments that can drift, but only after they have named consumers.
- Treat raw action strings as boundary data only.
- Make invalid authoring fail early with concrete file/field/action ids.
- Every phase must end in a project state that is less confusing than before.

## Current Source Ownership

### Progression Catalog

File: `server/src/progression_catalog.shared.json`

Owns:

- combat profiles
- subclasses
- resources and combat rules
- player-facing abilities
- melee ability gameplay tuning
- spell rows
- auto-attack gameplay tuning
- action presentations
- default loadout assignments
- fixed-action bindings
- loadout slots

Does not own:

- melee clip timing
- melee phased presentation clips
- VFX implementation
- server behavior code for new behavior kinds

### Combat Animation Sets

Files: `Assets/Arena/Resources/CombatAnimationSets/*.asset`

Own:

- combat-profile-specific animation set identity
- authored melee strike ids
- melee runtime slot ids
- melee hit windows
- targeted melee recovery timing
- orbit projectile active/contact windows
- combo links
- phased melee clips
- spell animation entries
- weapon presentation data

Do not own:

- selectable melee damage/range/cooldown tuning
- subclass exposure
- loadout slot placement
- spell behavior payloads

Important current detail:

- `the removed contact-resolution field` is orbit-projectile-only. Targeted melee strikes export `the removed contact-resolution field: 0`; their hit timing comes from hit windows and recovery timing.

### Melee Manifest

File: `server/src/melee_manifest.shared.json`

Owns no original authoring intent. It is an exported bridge from combat animation sets to the server.

It should remain generated or rebuilt from Unity authoring. It should not be hand-edited to fix identity drift.

### Saved Loadout Assignments

`SavedSpecSlotAssignment` uses ActionRef placement:

- `slot_id`: the action bar slot.
- `action_kind`: `ABILITY` or `FIXED`.
- `action_id`: the assigned action ref id.
  - For `ABILITY`, this is an `ability_id`.
  - Historical note: at the time this was written, `CHARGE` was still being discussed as a fixed action. Current authoring only uses fixed action ids such as `DODGE` or `PARRY`; charge-like movement abilities are selectable `ABILITY` rows.

The legacy `ability_id` column still exists as a compatibility mirror and should not be used as the primary placement identity for new work. The current rule lives in `docs/combat-authoring-contract.md`; the older ActionRef migration note is retained only as a historical pointer.

### Server Runtime

Owns:

- authoritative validation
- cooldowns and global cooldowns
- resource payment
- damage/effect application
- defense resolution
- movement and cast lifecycle behavior
- synchronized public tables

Does not own:

- player-facing action naming, except for stable engine-level categories
- hardcoded subclass defaults that belong in progression data

### Client Runtime

Owns:

- input collection
- local prediction
- HUD rendering
- animation and VFX presentation

Does not own:

- authoritative action legality
- gameplay tuning constants that can be synchronized from server catalogs
- independent action identity policy

## Phase 0: Contract, Consumer Decisions, And Glossary

Goal: make the current architecture readable before changing behavior.

Actions:

- Create `docs/combat-authoring-contract.md` as the canonical current-state authoring doc.
- Decide whether `server/src/progression_catalog.schema.json` has a real consumer:
  - create it if editors or non-Rust tools will use it for autocomplete, inline errors, or external validation
  - skip it if Rust serde and the graph validator are the only consumers
- Add a glossary for:
  - `ability_id`
  - authored strike id
  - runtime slot id
  - spell id
  - fixed action id
  - ActionRef
  - loadout slot id
  - combat profile id
  - subclass id
- Add short "How to add an action" checklists for:
  - selectable melee
  - selectable spell
  - fixed action
  - auto-attack
  - self-buff
- Keep these checklists abstract: they should describe the ordered systems to touch and the identity rules, not full filled-in examples.
- Fold in the ActionRef loadout migration note and the orbit-only `the removed contact-resolution field` rule.

Validation:

- Docs explicitly name every source-of-truth file.
- Docs include examples from current Warrior and Paladin rows.
- If a progression catalog schema is created, its consumer is named in the contract.
- No behavior changes.

Do not stop with:

- vague prose that says "action id" without saying which layer owns it.
- schemas that only validate JSON syntax but have no named consumer.
- a new doc that contradicts `progression_catalog.shared.json`.

## Phase 1: Resolved Action Graph Generator

Status: complete for the internal-validator scope.

Goal: build one graph pass that validates the current authoring contract, with manifest/audit serialization deferred until a real consumer exists.

This phase replaces the older separate audit, validator, and manifest phases. The same resolver should walk the graph once. In the first implementation, keep that graph internal to the server validator; only serialize generated artifacts when a named consumer exists.

Current checkpoint:

- The first-pass graph validator exists in `server/src/progression.rs` as the `combat_authoring_graph_validates_first_pass_contract` test.
- The validator runs under `cargo test` and reports stable rule codes.
- Ability subclass/default-profile validation now runs through the graph, including subclass existence and derived combat profile resolution.
- Melee and spell action identity validation now runs through the graph, including authored melee strike ids, runtime-slot misuse, spell row resolution, and selectable spell animation entries.
- Default loadout assignment validation now runs through the graph, including subclass/slot existence, ability ownership, slot compatibility, duplicate slot placement, fixed-action support, fixed-action subclass bindings, and fixed-action presentation.
- Fixed action binding validation now runs through the graph, including subclass existence, ability ownership, fixed-action support, duplicate subclass/action pairs, and ability `fixed_action_id` parity.
- Action presentation target validation now runs through the graph, including ability, spell, fixed-action, and presentation-kind resolution.
- Remaining progression tests are intentionally focused behavior/catalog tests, not duplicate graph walkers.
- No generated manifest, manifest schema, or audit report is checked in.

Canonical implementation:

- Build the resolver as Rust server-side code so it runs under `cargo test` and can gate normal server CI.
- Unity remains responsible for exporting `server/src/melee_manifest.shared.json`; the graph builder should consume that exported bridge, not parse Unity assets directly in the first version.
- Optional Unity editor commands can call into or mirror the validator later, but they are not the canonical source of truth.

Inputs:

- `server/src/progression_catalog.shared.json`
- `server/src/melee_manifest.shared.json`
- spell catalog rows compiled from `spells[]`
- combat animation set identity and spell/strike data
- Rust table structs and catalog publish paths for synchronization checks

First-pass output:

- a fail-on-error server test or `cargo test`-covered validation module

Potential generated outputs after Phase 2 has a real consumer:

- `server/src/combat_action_manifest.generated.json`
- `server/src/combat_action_manifest.schema.json`
- `docs/generated/combat_authoring_audit.md` or `combat_authoring_audit.json`

Deferred synchronization checks:

- The validator should compare expected synchronized table destinations against Rust table structs and the server catalog publishing paths.
- Generated C# bindings are downstream output and should not be treated as an authoring source of truth.

Future schema note:

- Do not add a manifest schema until a serialized manifest has a named consumer.
- If a draft manifest schema is created later, treat it as provisional and finalize it against real graph output.

For every player-facing ability, the graph should resolve:

- `ability_id`
- `ability_kind`
- `subclass_id`
- derived `combat_profile_id`
- authored `action_id`
- resolved runtime action id, if melee
- source row path
- gameplay tuning source
- presentation source
- default loadout slot, if any
- fixed action id, if any
- validation status

For every melee authored strike, the graph should resolve:

- combat profile
- authored strike id
- runtime slot id
- combo parent
- delivery mode
- hit window count
- targeted vs orbit projectile timing ownership
- whether a progression ability exposes it
- whether it is intrinsic only

For every spell, the graph should resolve:

- spell id
- behavior kind
- targeting
- resource cost/gain
- cooldown/GCD
- which abilities expose it
- which combat profiles have animation entries for exposed use
- whether VFX is implemented or intentionally none

For every fixed action, the graph should resolve:

- fixed action id
- default loadout slots
- subclass binding rows
- bound ability id, if any
- bound spell/action id, if any
- current dispatch model
- visibility/enabled facts that are currently hardcoded

First-pass hard errors:

- melee ability `action_id` does not match an authored strike id in the subclass combat profile
- melee ability points at a runtime slot id
- spell ability `action_id` has no `spells[]` row
- selectable spell ability has no combat animation set spell entry for its subclass profile
- default loadout assignment points at an unknown slot or wrong-subclass ability
- player-facing action has no presentation row

Add later when they would have caught a real bug:

- authored melee strike has no progression exposure and is not marked/documented as intrinsic
- spell has no exposing ability and is not marked/documented as prototype/system-only
- fixed-action binding points at an unknown ability or wrong subclass
- fixed-action ability does not declare the matching `fixed_action_id`
- charge fixed-action ability does not resolve to charge spell behavior
- default loadout assignment uses an unsupported ActionRef kind
- auto-attack catalog is missing for a combat profile
- melee manifest contains gameplay tuning fields that belong in progression
- targeted melee strike serializes a positive `the removed contact-resolution field`
- orbit projectile strike has no positive active/contact window
- VFX fallback would be used for a player-facing spell
- combat animation set contains duplicate authored ids after normalization
- fixed action has hardcoded client policy not represented in the manifest
- duplicate display names exist without an intentional alias note

Do not implement the full candidate list up front. Start with validators that catch bugs already seen in this project or mistakes that are highly likely during the next content pass. Promote candidate rules only when they would have prevented real churn.

Manifest shape:

Each compiled action should contain:

- canonical content id
- action category: `MELEE`, `SPELL`, `FIXED`, `AUTO_ATTACK`
- source ids:
  - `ability_id`
  - spell id
  - authored strike id
  - runtime slot id
  - fixed action id
- owner scope:
  - subclass id
  - combat profile id
- gameplay source:
  - progression melee tuning
  - spell behavior row
  - auto-attack row
  - fixed-action wrapper
- presentation source:
  - combat animation set melee entry
  - combat animation set spell entry
  - VFX behavior
- authorization model:
  - selectable
  - fixed
  - intrinsic
  - combo follow-up

Validation:

- Existing runtime behavior remains unchanged.
- Existing tests still pass.
- Current scattered server validation tests are either reused by, or migrated behind, the graph-backed validator.
- The internal graph can explain every current loadout action.
- Any human audit generated later is readable without opening Unity YAML by hand.
- At least one Phase 2 resolver or dispatch path is committed to consume the manifest; otherwise keep the resolved graph internal to the validator and do not check in a generated JSON artifact.

Do not stop with:

- a manifest that duplicates data but no runtime/editor code trusts.
- separate audit and validator implementations that can drift from the manifest.
- a report that only validates JSON syntax.
- a manifest that invents a new action taxonomy inconsistent with current tables.

## Phase 2: Use The Graph For Action Resolution

Status: complete for the structured ActionRef/resolved-loadout scope.

Goal: reduce resolver and dispatch ambiguity where it actually pays off.

Server already has useful `AuthoredActionId` and `RuntimeActionId` boundaries. This phase expands that discipline and mirrors it in client code where practical.

Current checkpoint:

- Server saved-spec assignment validation resolves through `ActionRef`, `ActionKind`, and `FixedActionId` instead of raw action strings.
- Superseded: server charge behavior no longer resolves through the old charge wrapper; selectable movement abilities dispatch through `CastRequest`.
- Client loadout resolution returns `ActiveSelectableLoadoutAction` with `action_kind`, `action_ref_id`, `ability_kind`, authored action id, and runtime action id.
- Client action-bar dispatch branches on resolved fixed/melee/spell metadata instead of guessing by probing unrelated tables.
- C# editor contract tests guard the resolved action-bar dispatch shape and fixed-action dispatcher entry point.

Actions:

- Keep raw strings at reducer/network/table boundaries.
- Use typed or structured action refs inside resolver and dispatch code.
- Ship at least one real graph/ActionRef consumer, such as server action resolution or client action-bar/loadout dispatch.
- Let action-bar/loadout resolution consume structured action refs or resolved loadout metadata instead of probing unrelated tables.
- Replace broad "accept any string and normalize it" helpers with narrowly named functions:
  - resolve spell id
  - resolve authored melee id
  - resolve runtime melee slot id
  - resolve fixed action id
  - resolve loadout slot id
- Remove permissive broad lookups once all call sites use structured action refs or resolved loadout metadata.

Client dispatch target shape:

1. input key resolves to loadout slot
2. loadout slot resolves to structured action ref and ability/fixed metadata
3. action ref dispatches by category/behavior
4. category-specific executors handle prediction and reducer calls

Validation:

- Runtime slot ids no longer appear in player-facing progression rows.
- Client dispatch does not guess whether an id is melee or spell by probing unrelated tables.
- Error messages use both the raw input and the expected id kind.
- Existing keybind behavior is unchanged.

Do not stop with:

- wrappers that exist but most call sites ignore.
- a broad "ActionId" type that hides the exact same ambiguity.
- a checked-in manifest that only the validator reads.
- a client rename that leaves `SpellInputHandler` and a new action dispatcher both owning action-bar input.

## Phase 3: Final Authoring Contract And Worked Examples

Status: complete for the current authoring-contract scope.

Goal: leave one canonical source for authoring guidance.

Current checkpoint:

- `docs/combat-authoring-contract.md` is the current authoring entry point.
- The contract includes source ownership, glossary, checklists, validator command/rules, consumer decisions, and worked examples for selectable melee, selectable spell, and fixed action.
- Auto-attack and self-buff are covered as concise contract sections instead of full walkthroughs.
- `docs/ability-implementation-prompt-template-2026-04-22.md` points at the contract before giving task instructions.
- Superseded combat-authoring planning notes live in `docs/archive/2026-04-superseded-plans/`.

Actions:

- Promote `docs/combat-authoring-contract.md` from phase-0 draft to final current-state contract.
- Add worked examples for:
  - selectable melee
  - selectable spell
  - fixed action
- Each example must say which files are touched and what each file contributes.
- These examples are concrete walkthroughs against current Warrior/Paladin-style rows, distinct from the abstract Phase 0 checklists.
- Cover auto-attack and self-buff in concise contract paragraphs unless their workflows become non-obvious enough to justify full walkthroughs.
- Add common validator failure examples with their fixes.
- Update prompt templates to point at the final contract.
- Move superseded planning docs into `docs/archive/`.

Final docs should include:

- current authoring workflows
- source ownership table
- glossary
- add-action checklists
- generated manifest location, if a real consumer exists by then
- schema locations, if a real consumer exists by then
- validator command
- common failure messages
- examples from current catalog rows

Validation:

- Searching docs for stale field names returns only archived files.
- New prompt templates do not mention old fields or legacy workflows.
- Current docs do not disagree about range, cooldown, auto-attack, fixed charge ownership, or ActionRef loadout ownership.

Do not stop with:

- multiple current docs that each define the same contract differently.
- examples that skip file paths or hide required generated outputs.

## Deferred Work

These are useful, but they are not blocking the production-grade authoring foundation.

### Client Dispatch Rename

The current issue is real: `SpellInputHandler` owns action-bar dispatch and branches into melee, fixed, and spell paths. The name teaches the wrong model.

Defer broad renaming until the compiled action graph exists and dispatch can move to an action-oriented entry point without duplicating ownership.

Target follow-up:

- introduce `ActionInputDispatcher` or `ActionBarInputDispatcher`
- keep spell-specific logic in a smaller spell executor
- keep melee-specific prediction in `MeleeInputHandler` or a renamed melee executor
- keep fixed-action dispatch in a fixed-action executor

Do not do a cosmetic rename before the structural graph-backed dispatch model exists.

### Fixed-Action Behavior Data

Superseded context: this section was written while `DODGE` and a charge wrapper were explicitly branched in client dispatch. Current charge-like actions are selectable movement abilities.

The manifest should describe current fixed-action facts now. Full data-driven fixed-action routing can wait until there are more fixed actions or fixed-action policy starts blocking content.

Suggested trigger:

- introduce fixed-action behavior data when adding a third or fourth true fixed action

Future fixed-action behavior fields:

- fixed action id
- display behavior
- dispatch kind:
  - movement reducer
  - cast request through bound ability
  - defense reducer
- requires target
- uses global cooldown
- cooldown key source
- charge count model, if any
- resource effect model, if any

### Authoring UI Improvements

The editor already has some combat animation set validation/export support. Deeper UI should follow real authoring friction; manifest-backed UI should wait until a serialized manifest exists and has a consumer.

Useful future actions:

- show whether a strike is exposed by progression
- show which ability ids reference a strike
- show which spell ids are required by exposed spell abilities for that combat profile
- add buttons for:
  - validate this animation set
  - export melee manifest
  - open progression row reference
  - open generated audit report

Do not add hidden auto-fixes that mutate identity fields without showing the author.

## Suggested Sequence

Recommended order:

1. Phase 0: contract, consumer decisions, and glossary
2. Phase 1: internal resolved action graph validator
3. Phase 2: structured action refs and resolved loadout metadata in resolver/dispatch
4. Phase 3: final authoring contract and worked examples

Then revisit deferred work based on real content-authoring friction.

## Non-Goals

- Do not replace SpacetimeDB table generation.
- Do not turn every spell into data-only scripting.
- Do not force melee into the spell behavior model.
- Do not move melee timing out of Unity.
- Do not remove local prediction.
- Do not block content work on broad client renames or editor polish once the graph-backed validator is catching authoring mistakes.
- Do not treat authoring graph validation as runtime conformance testing. Synthetic cast/attack integration tests are useful, but they are a separate layer from this authoring foundation.

## Final Acceptance Criteria

The full foundation is done when:

- a new developer can add a simple selectable melee action by following one worked example
- a new developer can add a simple selectable spell by following one worked example
- the internal graph validator explains every current player-facing action it checks
- the validator catches missing animation, missing gameplay, missing presentation, and wrong id layer errors
- loadout placement is described through ActionRef fields, not legacy `ability_id` placement
- runtime slot ids are internal details, not player-facing action ids
- targeted melee no longer depends on the stale contact-resolution semantics
- current docs agree with the code and any generated manifests that exist
- automated agents can be told "follow the combat authoring contract" without needing hidden context

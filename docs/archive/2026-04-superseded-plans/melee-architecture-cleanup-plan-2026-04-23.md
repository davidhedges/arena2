# Melee Architecture Cleanup Plan

This plan turns the recent melee/auto-attack/debugging pain into a concrete cleanup sequence.

## Status

As of April 24, 2026, the main cleanup work described in this document is complete enough to stop and return to feature work.

For current combat action authoring rules, use `docs/combat-authoring-contract.md`.

Completed:

- authored strike ids are now the canonical player/content-facing melee ids
- progression `action_id` parity with authored strike ids is enforced
- runtime slot ids are narrower internal plumbing, with stronger validation
- auto-attack has a dedicated authored identity and is separated from selectable melee by contract
- explicit melee event sources exist (`player_input`, `queued_followup`, `auto_attack`)
- typed `AuthoredActionId` / `RuntimeActionId` boundaries now cover the highest-risk progression/melee/spell seams
- inline melee phased presentation replaced the detached staged-action workflow
- legacy fixed `strike1..16` authoring has been removed
- legacy hidden `stagedActions` authoring has been removed
- new melee attacks can be authored in the weapon-set editor and exposed through progression without reusing legacy slots

Remaining work is optional polish or future feature-driven refinement, not a blocking architecture cleanup:

- broaden typed ids further only if a real bug points at another seam
- refine selectable multi-hit distribution authoring only if upcoming attacks need more control than the current even-split damage path
- prune old backup/doc clutter if repo hygiene becomes annoying enough to justify it

Recommended stopping point:

- treat the current system as the new baseline
- add real attacks/features
- only reopen this document when concrete feature work exposes repeated friction again

It is intentionally opinionated:

- authored action id should be canonical
- selectable melee damage authority should be explicit
- intrinsic melee should stay separate from selectable melee
- legacy compatibility paths should be temporary, not permanent architecture

## Terms

This plan uses three different ids. They are not interchangeable.

- `ability_id`
  - progression-facing catalog identity
  - example: `WARRIOR_SKYFALL_1`
  - lives in `server/src/progression_catalog.shared.json`
- authored strike id
  - canonical melee action identity
  - example target shape: `SKYFALL_1`
  - authored in the weapon-set asset and exported into `server/src/melee_manifest.shared.json`
  - referenced by progression `action_id`
- runtime slot id
  - internal execution lane / plumbing identity
  - example: `utility_1` or `skyfall_1`
  - authored in the weapon-set asset and exported into `server/src/melee_manifest.shared.json`

The current repo is not fully clean yet. For example, `WARRIOR_SKYFALL_1` currently points at mixed-case `Skyfall`. This plan exists to remove that ambiguity.

## Naming Rule

The intended naming rule after Phase 1 is:

- `ability_id`
  - subclass-prefixed, progression-facing, uppercase snake
  - example: `WARRIOR_SKYFALL_1`
- authored strike id
  - canonical melee action identity, uppercase snake
  - example: `SKYFALL_1`
- runtime slot id
  - lowercase snake
  - example: `skyfall_1`

This means the progression row should look like:

- `ability_id = WARRIOR_SKYFALL_1`
- `action_id = SKYFALL_1`

The authored strike id is minted in the weapon-set asset. Export then carries it into the melee manifest. Progression never invents a different action id for the same melee attack.

## Current Problems

The recent `Skyfall` and auto-attack work exposed the same structural issues repeatedly:

- one attack can be identified by too many ids
  - progression `action_id`
  - weapon-set authored strike id
  - runtime slot id
  - presentation lookup id
- selectable melee tuning is duplicated across two layers
  - `server/src/progression_catalog.shared.json`
  - weapon-set melee authoring / `server/src/melee_manifest.shared.json`
- runtime still accepts too many compatibility paths
  - authored id lookup with normalization
  - runtime slot id lookup
  - legacy field fallbacks
- legacy authoring systems still exist in the data model even when hidden from the editor
- event payloads do not carry enough explicit provenance

This creates three bad outcomes:

1. bugs are easy to introduce
2. logs are harder to trust than they should be
3. authoring feels indirect and confusing

## Desired End State

### Identity

Use one canonical player/content-facing action id: the authored strike id.

Examples:

- `SKYFALL_1`
- `HEW_1`
- `CLEAVE_1`

Runtime slot ids may still exist, but only as narrow internal plumbing.

Examples:

- `utility_1`
- `skyfall_1`
- `alt_light_1`

The key rule is:

- progression, loadouts, authorization, presentation ownership, and most events should speak in authored ids
- runtime slot ids should not leak into design-facing or player-facing layers

Intrinsic melee should also have authored ids.

Example:

- `AUTO_ATTACK_1`

Intrinsic attacks do not need progression `ability_id` rows, but they should still have authored strike ids so identity stays uniform across melee systems.

### Authority

Split authority cleanly:

- `progression_catalog.shared.json`
  - player-facing selectable ability metadata
  - loadout exposure
  - resource cost
  - selectable melee tuning values such as base damage / stagger / knockback
- weapon-set melee authoring
  - executable melee attack definition
  - timing
  - hit windows / hit count
  - range
  - combo links
  - phased presentation
  - delivery mode
  - aerial environment
- intrinsic melee
  - authored only in weapon-set/melee authoring
  - not represented as selectable progression abilities by default

### Presentation

Each melee attack card should own its own presentation.

For example, `SKYFALL_1` should own:

- combat tuning
- presentation mode
- phased start / loop / end clips

There should be no detached staged-action workflow for normal melee authoring.

## Recommended Cleanup Order

## Phase 1: Canonical Authored ID Enforcement

Goal: stop letting multiple ids "sort of work."

### Rules

- progression `action_id` must equal the authored strike id exactly
- phased melee presentation must be keyed by the same authored strike id
- runtime slot id stays internal
- new authored ids must be deliberate and stable

### Implementation

- enforce authored-id lookup first in server melee resolution
- keep any runtime-slot fallback behind narrow internal helpers only
- add editor validation for authored ids and runtime slot ids
- reject placeholder runtime slot ids such as `this_id_doesnt_matter`

### Exit Criteria

- a new melee ability can be added with one authored id and one runtime slot id
- no player-facing workflow requires knowing the runtime slot id
- progression/loadout rows never point at runtime slot ids

## Phase 2: Selectable Melee Damage Authority

Goal: stop having two believable sources of truth for selectable melee damage.

### Recommendation

For selectable melee:

- progression owns absolute tuning values
  - `base_damage`
  - `applies_stagger`
  - `range`
  - `cooldown_ms`
  - `uses_global_cooldown`
  - `parry_behavior`
  - `block_behavior`
  - `airborne_targeting_mode`
  - orbit hit limits/cooldowns
- melee authoring owns execution structure
  - hit count
  - hit timing
  - recovery
  - combo behavior

For intrinsic melee:

- auto-attack gameplay tuning currently lives in `auto_attacks[]` in `progression_catalog.shared.json`; melee authoring owns execution timing and presentation

### Multi-Hit Requirement

Multi-hit selectable melee cannot be deferred past Phase 2. It is the only part of this authority split that can cause a real regression if left vague.

Current implementation evenly splits progression `base_damage` across hit windows. That is acceptable until a concrete attack needs authored per-hit weights.

### Distribution Design

If selectable melee attacks need non-even multi-hit weighting, the weapon-set side should express distribution rather than absolute damage.

Example:

- hit 1 = `0.4`
- hit 2 = `0.6`

Then progression `base_damage = 30` becomes:

- hit 1 damage = `12`
- hit 2 damage = `18`

If distribution authoring is added later, export or validation should reject incomplete distributions rather than silently duplicating full base damage onto every hit.

### Exit Criteria

- a selectable melee attack cannot say `30` in progression and hit for `25` because of a silent fallback
- intrinsic auto-attack tuning stays in the `auto_attacks[]` catalog rather than in melee manifest damage fields
- selectable multi-hit melee attacks either use the current even split or an explicitly authored future distribution

## Phase 3: Explicit Event Source Semantics

Goal: stop making the client infer melee provenance from heuristics.

### Required Sources

- `player_input`
- `queued_followup`
- `auto_attack`

Potential later source:

- `ai`

### Source Definitions

- `player_input`
  - a melee action started directly from a player-issued command in the current moment
- `queued_followup`
  - a melee action that executes because a previously buffered combo continuation or committed follow-up window resolved
  - it is player-originated historically, but not a fresh input at the exact execution moment
- `auto_attack`
  - an intrinsic timed melee action started by the server-owned auto-attack controller

### Implementation

- include source on authoritative melee cast events
- include source on impact events
- include authored strike id on those events by default

### Why

This removes ambiguity in:

- local replay suppression
- auto-attack presentation
- debugging logs
- future AI / proc / scripted melee additions

### Exit Criteria

- client animation routing never has to guess whether a local melee event was predicted or server-started
- logs make source and strike identity obvious on both cast and impact

## Phase 4: Typed Boundaries for Dangerous Seams

Goal: stop wrong-layer string usage from compiling.

This is the intermediate hardening step if a full canonical-id migration is not done yet.

### Suggested Types

```rust
pub struct AuthoredActionId(String);
pub struct RuntimeActionId(String);
```

### Convert First

- progression authorization
- melee dispatch
- cooldown lookup/stamping
- event payload construction
- client/server id translation helpers

These newtypes should cover intrinsic authored ids too. Intrinsic melee is not a third identity class; it is still authored melee, just not selectable progression content.

### Exit Criteria

- wrong-layer calls between authored ids and runtime ids fail at compile time in the most error-prone server paths

## Phase 5: Delete Legacy Compatibility Paths

Goal: stop carrying old architecture forever.

### Candidates For Removal

- permissive lookup paths that accept both authored ids and runtime slot ids in broad APIs
- old fallback presentation resolution paths
- obsolete backup directories once migration is complete and replacement recovery paths are agreed

### Deletion Gates

Nothing in this phase should be deleted on intuition alone. The legacy `strike1..16`, `strike1Combat..16Combat`, and detached `stagedActions` workflows were completed/removed on 2026-04-24.

- permissive authored-id/runtime-slot-id broad lookup paths
  - safe to delete after Phase 1 parity tests exist and all progression rows resolve through authored ids only
- old fallback presentation resolution
  - safe to delete after inline phased presentation has replaced detached staging on all active combat animation sets
- `Backups/melee-authoring/` and related weapon-set backup folders
  - do not delete during active migration
  - once migration is complete, either archive them outside the repo or delete them in a dedicated cleanup change so they stop polluting searches and false-positive matches

### Exit Criteria

- one active authoring workflow for melee attacks
- one active presentation workflow for phased melee
- no need to remember which hidden legacy structure still matters

## Tests And Validation

This cleanup should be enforced by tests and validation, not only by convention.

### Server Tests

- progression `action_id` resolves only through authored strike ids for melee abilities
- progression rows do not resolve against runtime slot ids
- intrinsic authored strikes such as `AUTO_ATTACK_1` resolve correctly without progression rows
- selectable melee damage uses progression authority
- selectable multi-hit melee preserves progression total damage across hit windows
- event payloads include explicit source semantics for melee cast and impact

### Editor / Export Validation

- authored strike ids must be uppercase snake
- runtime slot ids must be lowercase snake
- placeholder runtime slot ids are rejected
- phased melee exports fail if no valid timing source exists

## Rollback Strategy

Each phase should be landable and reversible on its own.

- Phase 1 rollback
  - keep authored-id enforcement behind tests first, then validation, then hard rejection
- Phase 2 rollback
  - preserve current melee-manifest absolute damage fields during the transition
  - do not delete old selectable-damage read paths until progression-authoritative damage has been verified against representative attacks
- Phase 3 rollback
  - event source tagging is additive and low-risk; old clients can ignore new fields if wire compatibility is handled correctly
- Phase 4 rollback
  - newtypes should begin at boundary helpers first; if they cause churn, the wrappers can be limited to the hottest seams without reverting the whole migration
- Phase 5 rollback
  - never combine deletion and behavior rewrite in one change
  - delete legacy structures only after their replacements have been live and verified

## Immediate Practical Tasks

If this cleanup is executed incrementally, the next concrete tasks should be:

1. Write and adopt a naming rule.
   - authored ids: uppercase snake, e.g. `SKYFALL_1`
   - runtime slot ids: lowercase snake, e.g. `skyfall_1`
2. Add explicit melee event source fields so client replay/presentation stops relying on inference.
3. Add editor validation that rejects placeholder or empty runtime slot ids.
4. Add server tests that enforce progression `action_id` -> authored strike id parity.
5. Normalize the current `Skyfall` attack to that rule.
6. Remove any remaining case-insensitive "make it work" crutches that are only compensating for inconsistent authoring.
7. Write a short contract note declaring progression as the authority for selectable melee damage values.

## Practical Guidance For New Work

Until the cleanup is complete, use these rules:

- when adding a new melee ability, create a unique authored strike id first
- expose that attack to the subclass by editing `server/src/progression_catalog.shared.json`
- do not reuse an old combo-named authored id for a new ability
- do not use runtime slot ids in progression rows
- treat auto-attack as intrinsic combat-profile behavior, not a selectable ability
- do not reintroduce detached staged-action authoring for new melee work

## Recommendation

The highest-value next cleanup is still:

1. enforce authored id as canonical
2. stop duplicated selectable melee damage authority
3. make event source semantics explicit

That sequence will remove more confusion than any additional symptom patching.

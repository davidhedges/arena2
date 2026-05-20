# Progression And Loadout Next Step Plan

Date: April 21, 2026

Status: Proposed

Depends on:

- `docs/progression-loadout-foundation-plan-2026-04-21.md`

## Objective

Implement the first safe slice of the progression system by adding the server-owned schema and validation surface for saved specs and insight-based slot unlocks, without changing combat execution behavior yet.

This step should establish the long-lived data model before any spell, melee, HUD, or input refactors consume it.

## Scope

### In Scope

- canonical progression/loadout terminology
- server tables for saved specs and slot assignments
- static catalogs for selectable abilities, slots, and insight rewards
- saved spec count limit
- insight-based unlocked-slot calculation
- reducer validation for editing and activating specs
- default active-spec bootstrap behavior

### Out Of Scope

- derived combat stat scaling
- resource regen or max-resource scaling
- ability execution changes
- client HUD and input integration
- keybind ownership and slot-to-keybind mapping
- `proficiency`, `expertise`, or `mastery` gameplay behavior
- combo progression rules
- loadout-scene-only activation or edit gating until the server has a world or scene concept that can represent the loadout screen

## Step Boundary

At the end of this step, the codebase should be able to answer:

- what subclass the player is
- what saved specs the player owns
- what stat allocation each saved spec has
- how many selectable slots that spec unlocks from insight
- what abilities are assigned to those slots
- whether a requested edit or activation is legal

It does not need to answer:

- how those stats scale damage or cast speed
- how the HUD renders the loadout
- how combat execution changes at runtime

## Proposed Server Model

### 1. Character Progression State

Add a persistent per-player row that owns:

- `owner`
- `subclass_id`
- `combat_profile_id`
- `active_spec_id`

Purpose:

- make subclass identity explicit and server-owned
- stop overloading the current `player` row for progression ownership over time

### 2. Saved Spec

Add one row per saved spec:

- `spec_id`
- `owner`
- `name`
- `subclass_id`
- `version`
- `created_at`
- `updated_at`

Purpose:

- persist multiple named loadouts
- support future migration and versioning

### 3. Saved Spec Stat Allocation

Add one row per stat allocation entry:

- `key` derived from `(spec_id, stat_kind)`, for example `"{spec_id}:{stat_kind}"`
- `spec_id`
- `stat_kind`
- `allocated_points`

V1 stat kinds:

- `MIGHT`
- `INSIGHT`
- `FINESSE`
- `FORTITUDE`

### 4. Saved Spec Slot Assignment

Add one row per slot assignment:

- `key` derived from `(spec_id, slot_id)`, for example `"{spec_id}:{slot_id}"`
- `spec_id`
- `slot_id`
- `ability_id`

Purpose:

- persist the active choices made from the subclass ability pool

### 5. Ability Catalog

Add a static catalog row per selectable loadout ability:

- `ability_id`
- `subclass_id`
- `action_id`
- `display_name`
- `ability_tags`
- `sort_order`

Purpose:

- provide stable server-owned identity
- map selectable loadout identity to current execution identity

Note:

- `action_id` may still point into existing hardcoded spell ids or melee action ids
- execution behavior remains code-driven
- this catalog is only for selectable loadout abilities
- intrinsic combat actions such as baseline combo steps and dodge should remain outside this catalog in v1

### 6. Loadout Slot Catalog

Add a static catalog row per selectable slot:

- `slot_id`
- `ui_row`
- `ui_col`
- `slot_group`
- `required_insight`
- `accepts_tags`
- `sort_order`

Purpose:

- make the slot grid explicit and server-known
- encode rightmost-slot trimming rules by stable ordering

Note:

- `required_insight = 0` means the slot is always available

### 7. Insight Reward Catalog

Add a static catalog row per threshold reward:

- `reward_id`
- `required_insight`
- `reward_kind`
- `reward_value`
- `sort_order`

V1 reward kinds:

- `SLOT_UNLOCK`
- `PROFICIENCY`
- `EXPERTISE`
- `MASTERY`

For this step, only `SLOT_UNLOCK` needs gameplay validation behavior. The other reward kinds can be persisted and published as data only.

## Catalog Source Of Truth

These catalogs should be seeded from shared JSON files checked into the repo, following the same pattern already used by the existing shared manifests.

The intended flow is:

1. author shared JSON in the repo
2. include and parse it on the server
3. synchronize the public catalog tables from that JSON

Reducers should not become a second authoring path for static catalog content.

## Bootstrap Rule

When a character is first assigned a subclass:

- auto-create a default saved spec
- initialize its stat allocation to the subclass baseline
- make it the active spec immediately

This step should not rely on a nullable `active_spec_id` in normal gameplay.

## Required Validation Rules

### Saved Spec Limits

- enforce a server-side max saved spec count per player
- reject creation beyond the configured limit

### Ownership

- a spec may only be edited, renamed, deleted, or activated by its owner

### Subclass Consistency

- a spec subclass must match the owning character's subclass

### Ability Eligibility

- assigned abilities must belong to the owning subclass
- assigned abilities must satisfy `ability_tags` and `accepts_tags` compatibility

### Insight Slot Unlocks

- compute unlocked selectable slots from the saved spec's insight allocation
- reject assignments to locked slots

### Rightmost Slot Trimming

When a saved spec's insight allocation drops below a threshold during editing:

- keep persisted assignments in locked slots rather than deleting them eagerly
- expose only the currently unlocked subset as active
- if a projection or export step needs a deterministic active subset, prefer the furthest slot to the right on the bottom row as the first slot to deactivate

This preserves saved builds when the player experiments with stat allocation and then restores insight later.

The ordering rule should come from the slot catalog rather than ad hoc UI logic.

### Activation And Editing Context

The long-term product rule is:

- spec editing and activation should only be allowed while the player is in the loadout scene

The current server limitation is:

- the world model only represents `OPEN` and `INSTANCE`
- this step should not pretend that loadout-scene gating is enforceable until a server-visible loadout world or scene state exists

For this step:

- document the rule
- do not build hard validation against a scene concept that is not yet in the schema

## Concrete Tasks

1. Define canonical enums and strings for `subclass_id`, `resource_kind`, `stat_kind`, `reward_kind`, stable `slot_id` conventions, and shared JSON file shapes.
2. Add the new SpacetimeDB tables for progression state, saved specs, stat allocation, slot assignment, and the static catalogs.
3. Add catalog sync and bootstrap paths so the ability, slot, and reward rows are authored from shared JSON and synchronized authoritatively into tables.
4. Add helper functions that compute:
   - total allocated insight for a spec
   - unlocked selectable slots
   - active-slot projection from persisted assignments
   - rightmost deactivation ordering
5. Add reducers for:
   - create spec
   - rename spec
   - delete spec
   - set stat allocation
   - assign ability to slot
   - clear slot
   - activate spec
6. Add server-side validation for ownership, subclass compatibility, slot compatibility, and active-spec ownership.
7. Publish the new tables so the client can observe them later, even if no UI consumes them yet.
8. Add tests around insight thresholds, active-slot projection, illegal assignments, default-spec bootstrap, and saved spec limits.

## Recommended Implementation Order

### Part A: Schema And Catalogs

Land the tables and sync logic first. Do not mix in reducer behavior until the schema is stable.

### Part B: Pure Validation Helpers

Implement helper functions for:

- slot ordering
- insight threshold resolution
- allowed-slot resolution
- ability compatibility

These should be testable without UI involvement.

Add helper tests in this phase, alongside the helper logic.

### Part C: Saved Spec Reducers

Add the reducers only after the helper layer exists.

### Part D: Reducer Tests

Add reducer tests before any client consumption starts.

## Suggested Success Criteria

This next step is complete when:

1. a player can own a bounded set of saved specs on the server
2. each saved spec can store stat allocations and slot assignments
3. insight thresholds authoritatively determine which selectable slots are legal
4. illegal slot assignments are rejected on the server
5. a default saved spec is created and activated at subclass bootstrap
6. the plans no longer claim loadout-scene-only gating is already enforced

## Follow-On Step

After this step lands, the next step should be:

- client consumption of active spec and slot assignments
- replacement of the hardcoded selectable spell bar with server-backed loadout data
- explicit server-visible representation of the loadout scene so spec edit and activation gating can become enforceable

Combat scaling should remain deferred until after that integration layer exists.

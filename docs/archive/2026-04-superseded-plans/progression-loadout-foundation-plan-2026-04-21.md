# Progression And Loadout Foundation Plan

Date: April 21, 2026

Status: Historical

2026-05-02 note: stamina was removed as a runtime resource. The generic `PlayerResource` system remains for subclass primary resources such as Rage and Vengeance.

## Executive Summary

The game is ready to begin building progression and saved loadouts, but it is not ready to ship a full stats-driven combat system directly into the current spell and melee runtime.

The correct first move is to add a server-owned progression and loadout foundation that cleanly separates:

- subclass identity
- combat profile identity
- saved spec identity
- stat allocation
- insight-based slot unlocks
- server-authoritative slot assignment
- static ability and slot catalogs

The main architectural boundary is:

- ability identity, slot compatibility, loadout assignment, resource type, subclass ownership, and unlock thresholds should become data-driven
- ability execution behavior should remain code-driven for now

This plan keeps the current combat runtime stable while creating the data model needed for future stat scaling, saved specs, and class-specific buildcraft.

## Locked Decisions

The current design assumptions are:

- players do not have multiple characters per account
- subclasses grant starting abilities, a default combat profile, and a primary resource type
- saved specs are per-character saved builds
- a spec contains stat allocation and saved slot assignments
- spec switching is free, instant, and unlimited, but the long-term product rule is that it should only be possible inside the loadout scene
- the server is authoritative for saved specs and active loadout selection
- characters know the full ability pool for their subclass
- insight unlocks additional selectable ability slots
- combo followups are not progression-gated in v1
- no stat changes global cooldown
- no stat changes combo timing windows
- insight will later also grant progression rewards such as `proficiency`, `expertise`, and `mastery`

## Terminology

These names should become canonical before further implementation:

- `archetype`: broad fantasy family
- `subclass`: the actual gameplay class choice used for ability pool and resource identity
- `combat_profile`: weapon and moveset presentation package used by melee definitions and client presentation
- `spec`: a saved build for a single subclass, including stat allocation and slot assignments
- `ability_id`: stable identity for a selectable loadout ability
- `slot_id`: stable identity for a loadout slot in the UI grid

Avoid mixing `class` and `subclass` in code once this work starts.

Additional semantics:

- the ability catalog is for selectable loadout abilities only
- intrinsic combat actions such as baseline melee combo steps, dodge, and other always-available actions stay outside the selectable loadout catalog in v1

## Goals

1. Make progression and saved specs first-class server state.
2. Move loadout eligibility and slot unlock rules out of hardcoded client-only assumptions.
3. Preserve the current combat runtime while introducing future-proof ability and slot identities.
4. Create a clean path for later stat-derived scaling without rewriting the progression model.

## Non-Goals

This foundation pass should not attempt to do all of the following at once:

- rewrite spell execution to be fully data-driven
- progression-gate melee combo followups
- implement final `proficiency`, `expertise`, or `mastery` gameplay effects
- redesign the entire HUD or loadout screen
- add class switching

## Target Architecture

### Static Catalogs

These should become public or otherwise queryable server-owned catalogs:

- subclass catalog
- combat profile catalog
- ability catalog
- loadout slot catalog
- insight reward catalog

These catalogs should answer:

- which subclass owns an ability
- which primary resource type a subclass uses
- which ability tags an ability exposes
- which tags a slot accepts
- which slots require insight thresholds
- which insight thresholds grant slots or future rewards

The source of truth for these catalogs should be shared data files checked into the repo, following the same pattern already used for `*.shared.json` manifests.

Catalog rows should be synchronized from those shared JSON files rather than authored manually through reducers.

### Persistent Player-Owned State

These should become persistent server-owned rows:

- character progression state
- saved spec rows
- saved spec stat allocations
- saved spec slot assignments

These rows should answer:

- which subclass the character is
- which combat profile identity the character is currently using
- which spec is active
- how many saved specs the player has
- what stats a spec allocates
- which abilities are currently assigned

The active spec bootstrap rule should be explicit:

- subclass assignment should auto-create a default saved spec and make it active immediately

This avoids a nullable `active_spec_id` state in the common path.

### Derived Runtime State

This should be introduced later, after the progression foundation exists:

- derived combat stats for damage, cast speed, resource regen, max resource, max health, and related scaling

The existing combat runtime should continue to own execution behavior until that layer is ready.

## Recommended Phases

### Phase 1: Canonical Identities And Saved Spec Foundation

Deliver:

- canonical terminology in docs and code comments
- stable `ability_id` and `slot_id` model
- subclass, slot, and reward catalogs
- shared JSON catalog seeding
- saved spec persistence
- saved spec count limit
- server validation for slot assignment and active spec ownership
- explicit bootstrap rules for the default spec

Do not yet:

- make casting or melee consume the new data model
- alter combat tuning
- enforce loadout-scene-only spec editing or activation unless the server first gains a world or scene concept that can represent the loadout screen

### Phase 2: Active Loadout Integration

Deliver:

- client reads active spec and slot assignments from the server
- HUD and input stop relying on hardcoded spell bars for selectable loadout slots
- loadout scene can create, save, activate, and rename specs
- server gains a concrete concept for "player is in the loadout scene" and begins enforcing spec-edit and spec-activation gating against that state

Do not yet:

- implement full derived stat scaling

### Phase 3: Derived Stats And Resource Generalization

Deliver:

- stat allocation feeds a derived runtime layer
- resource system supports subclass primary resources
- insight scaling affects max resource and regen
- finesse, might, and fortitude affect the agreed v1 combat outputs

Do not yet:

- implement advanced mastery mechanics

### Phase 4: Reward Expansion

Deliver:

- `proficiency`, `expertise`, and `mastery` become first-class rewards
- ability improvements can reference those rewards
- future subclass-specific progression rules can be added without reworking saved specs

## Design Rules

1. Loadout legality must be server-authoritative.
2. Insight should unlock slots through catalog rules, not by hardcoded UI assumptions.
3. Core combo sequencing should remain intrinsic to the combat profile in v1.
4. Selectable loadout abilities should be a different category from intrinsic combat actions.
5. A saved spec should remain valid even if future gameplay effects evolve.
6. Static identity should be data-driven before numeric scaling becomes data-driven.
7. If a spec temporarily falls below a slot threshold, the saved assignment may remain persisted but inactive until the slot becomes available again.

## Key Risks

### Naming Drift

If `class`, `subclass`, and `combat_profile` remain blurry, ownership will drift across systems and the tables will become misleading quickly.

### Client Hardcoding

The current client input and HUD are still heavily hardcoded around fixed spell ids and slot positions. The server model should be introduced before trying to fully untangle the client.

Keybind ownership should remain a separate concern from slot identity. `slot_id` should identify a position in the loadout grid, not implicitly encode keyboard binding semantics.

### Premature Stat Integration

If stats are wired directly into spell and melee behavior before the progression schema exists, the result will be a brittle mix of runtime assumptions and partial persistence.

### Scene-Gating Drift

The design requires loadout-scene-only spec switching, but the current server world model only represents `OPEN` and `INSTANCE`. Do not claim the rule is enforced until the world or scene schema can represent that state.

### Save Evolution

Saved specs will almost certainly need a `version` field so later redesigns can migrate old builds safely.

## Exit Criteria

This foundation plan is complete when:

1. the server owns subclass, saved spec, stat allocation, and slot assignment state
2. slot unlocks can be derived from insight without client authority
3. active loadout selection is persisted and validated by the server
4. the combat runtime can later query a stable progression model instead of hardcoded assumptions
5. the docs no longer claim loadout-scene-only gating already exists before the server can represent that state

## Recommended Immediate Next Step

The next implementation step should focus on the non-combat schema and validation layer:

- define canonical ids and catalogs
- add saved spec persistence
- add shared JSON catalog seeding
- add slot-unlock evaluation from insight
- add reducer-level validation for saved spec editing, assignment legality, and active-spec ownership
- defer loadout-scene-only gating until the server can represent the loadout scene explicitly

That work is broken out in:

- `docs/progression-loadout-next-step-plan-2026-04-21.md`

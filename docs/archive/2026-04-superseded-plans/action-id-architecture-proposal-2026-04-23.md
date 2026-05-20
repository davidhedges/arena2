# Action ID Architecture Proposal

Status: historical architecture proposal. Use `docs/combat-authoring-contract.md` as the current combat authoring contract.

## Recommendation

Use one canonical player/content-facing action id: the authored strike id.

Do not use runtime slot ids such as `utility_1`, `light_combo_1`, or `heavy_gapclose` as general-purpose action ids outside narrow internal runtime concerns.

Where runtime grouping is genuinely needed, model it explicitly with dedicated fields instead of overloading a second general-purpose id.

## Why

The current dual-id model mixes two different concerns:

- content identity
  - example: `COMBO_ATTACK_3_1_LOW_TO_HIGH`
- runtime lane identity
  - example: `utility_1`

That split is only safe if the boundary is enforced consistently. In practice, both concepts are currently plain strings, which makes it easy for runtime ids to leak into progression, authorization, or other player-facing layers.

Recent auto-attack/melee authorization bugs showed the failure mode clearly:

- progression catalog correctly referenced the authored melee strike id
- melee authorization drifted to the internal runtime slot/root id
- the system then rejected a valid equipped ability because it compared against the wrong identifier layer

This is exactly the class of bug we should design away.

## Proposed Direction

### Canonical identity

Make the authored strike id the canonical action id everywhere that is:

- player-facing
- content-facing
- progression-facing
- authorization-facing
- event-facing by default

Examples:

- progression ability `action_id`
- active-spec/loadout resolution
- unlocked/equipped ability authorization
- combat-profile/weapon-set authored references
- server/client combat event ids unless a runtime-only field is specifically needed

### Explicit runtime grouping

Replace the hidden jobs currently bundled into runtime slot ids with explicit fields where needed.

Examples:

- `cooldown_group_id`
- `animation_slot`
- `binding_slot`
- `combo_family_id`

Not every one of these fields is required immediately. The point is that each runtime concern should have its own explicit representation instead of piggybacking on a second overloaded action id.

## What Not To Do

- Do not use runtime slot ids as general-purpose action ids in progression data.
- Do not let authorization depend on normalized runtime slot ids.
- Do not rely on implicit string normalization to cross content/runtime layers.
- Do not keep adding compatibility exceptions around raw string ids.

## Short-Term vs Long-Term

### Short-term safety

Keep the current system functional, but stop introducing new player-facing uses of runtime slot ids.

At minimum:

- preserve the authored-id contract in progression and melee authorization
- keep runtime slot ids internal to the places that truly need them
- add regression tests whenever authored ids are translated into runtime lanes

### Long-term architecture

Move toward:

- one canonical authored action id
- explicit runtime grouping fields
- fewer APIs that expose runtime slot ids directly

## Migration Strategy

### Phase 1: Boundary discipline

- treat authored strike ids as canonical in progression and melee authorization
- keep existing runtime slot machinery working internally
- prevent further drift

### Phase 2: Separate runtime concerns

- identify every remaining place where runtime slot ids are used
- classify each usage by purpose:
  - cooldown grouping
  - animation routing
  - input grouping
  - combo/root lineage
- replace those usages with explicit fields or helpers

### Phase 3: Reduce runtime-id surface area

- stop passing runtime slot ids through general-purpose APIs
- prefer authored ids in client/server events and resolver entry points
- isolate runtime slot ids to the narrow execution/presentation code that still needs them

### Phase 4: Optional hardening

If the repo is not ready to collapse to one canonical id immediately, introduce distinct Rust types for authored ids and runtime ids so wrong-layer calls fail at compile time instead of at runtime.

Example direction:

```rust
pub struct AuthoredActionId(String);
pub struct RuntimeActionId(String);
```

This is a valid intermediate step, but it is not the preferred end state. The preferred end state is still authored-id canonical with explicit runtime grouping.

## Final Recommendation

For `arena2`, the best long-term design is:

1. authored strike id is the single canonical action identity
2. runtime grouping concerns are modeled explicitly
3. runtime slot ids stop being treated as first-class general-purpose action ids

If there is not time to do the full migration immediately, the minimum acceptable direction is:

1. keep both ids temporarily
2. enforce the boundary much more strictly
3. do not allow new player-facing or authorization-facing uses of runtime slot ids

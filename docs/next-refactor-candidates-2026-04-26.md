# Next refactor candidates — 2026-04-26

Three architectural improvements plus pre-emptive performance cleanups identified after the spell catalog work landed. Listed in recommended order.

---

## 1. Status payload schema single-source cleanup

**Status: Completed 2026-05-12.** The stale version of this item predated the current `StatusPayload` helpers and understated the number of status variants. The useful remaining cleanup was narrower: preserve the flat replicated `StatusEffect` row and `StatusEffectKind` wire/index discriminator, but centralize authored status payload construction and validation.

### What changed

- `StatusPayload` remains the runtime source of truth for behavior-shaped status data.
- `StatusPayload::kind()`, sparse-column projection, decode, invalid-payload checks, and strength comparison remain in `combat.rs`.
- Authored catalog fields now flow through one shared authored-payload adapter before becoming a `StatusPayload`.
- Spell `APPLY_STATUS`, spell impact effects, movement impact effects, and melee impact effects use the same `kind + fields -> StatusPayload` construction rules.
- Payload-field validation for `slow_pct`, tick fields, and `modifier_scalar` is shared instead of duplicated between spell and progression catalog validation.

### What intentionally stayed

- `StatusEffectKind` still exists because wire strings, indexes, lookup, and authored validation need a compact discriminator.
- The replicated `StatusEffect` table shape stayed flat.
- Status behavior variants stayed in Rust, not JSON.
- Delivery systems stayed separate; only status semantics are shared.

### Residual note

Adding a brand-new status kind still requires adding a `StatusEffectKind` variant, a `StatusPayload` variant, and behavior handling where that status actually affects simulation. That is acceptable for now because status behavior is code-coupled. Revisit only if new status kinds become frequent enough that the discriminator/payload mapping itself becomes a recurring source of defects.

---

## 2. Widen typed action IDs across server (longer initiative)

**Status: Completed 2026-04-26.** Landed as a single-pass refactor across `action_ids.rs`, `melee.rs`, `auto_attack.rs`, `practice.rs`, and `progression.rs`. Boundary discipline preserved: only reducer entry points and `resolve_melee_action_reference` accept `&str`; everything internal carries typed IDs or `ResolvedMeleeStrike`. 8 new tests including authored-vs-runtime collision rule. 93 server tests passing.

### The smell

[`server/src/action_ids.rs`](../server/src/action_ids.rs) already defines:

```rust
pub(crate) struct AuthoredActionId(String);
pub(crate) struct RuntimeActionId(String);
```

These typed wrappers would have made the *"Momentum got lowercased through melee runtime normalization"* bug a compile error instead of a runtime cast fizzle. The whole class of *"is this string an authored ID, a runtime ID, a slot ID, or a wire ID?"* confusion goes away.

But adoption is partial: **28 references in `melee.rs` only**, no use in spells, casting, progression, or the resolver. It's a half-done refactor.

### The fix shape

Widen typed IDs across the server:

- Replace `&str` / `String` parameters in resolver, dispatch, progression, and casting with `&AuthoredActionId` / `&RuntimeActionId` where the role is known.
- Push string normalization into the wrapper constructors so it happens once at the boundary.
- Make conversion explicit (`authored.to_runtime_via(...)`) — no more silent string-to-string transforms hiding behavior changes.

### Why this is a separate initiative, not the next bite

- Surface area is much larger than the status effect cleanup. Touches every action-aware code path.
- Original design note archived at [`docs/archive/2026-04-superseded-plans/action-id-architecture-proposal-2026-04-23.md`](archive/2026-04-superseded-plans/action-id-architecture-proposal-2026-04-23.md).
- Higher coordination cost: wider diff, more reviewer load, more places to get conversions wrong during migration.

### Why it still belongs on the list

- Recurring bug pattern. The Momentum cast bug was one symptom. The runtime resolver fix was a band-aid; this is the systemic fix.
- The longer this stays partial, the more new code reaches for raw strings because that's what the surrounding code uses.
- Naturally pairs with onboarding new developers — typed IDs make the action-handling code self-documenting.

---

## 3. Spell dispatch guardrails and bespoke-tier containment

Status update, 2026-05-13: the broad version of this item is mostly resolved. Spell gameplay now derives from ability `gameplay.delivery`, normal spell payloads are behavior-shaped, `SELF_BUFF` became `APPLY_STATUS`, spell Charge moved out of spell dispatch, and spell/movement/melee status applications share status semantics. The remaining candidate is not a rewrite; it is guardrails around the deliberately small bespoke spell tier.

### Current shape

- Normal `PROJECTILE`, `AREA`, `APPLY_STATUS`, and `SELF_RESOURCE` spells are catalog-authored and mostly dispatched by `SpellBehavior`.
- Projectile sub-shapes such as linear, orbit-caster, and boomerang-caster are data-driven through projectile motion tunables.
- Shared impact/status effects now flow through `StatusApplication`; ordinary damage/status combinations should not need per-spell Rust.
- The explicit bespoke runtime tier is currently `INSTANT_BEAM`, `ELECTROCUTE`, `METEOR`, and `NEGATE`, with a test-enforced budget of four.
- Client input/presentation reads replicated `SpellDefinition` behavior/targeting/cast-time fields; combat VFX is routed from combat events plus authored action ids.

### Remaining smell

The main risk is future growth of `BespokeRuntimeSpell`. A new spell should not become a new Rust branch just because it needs a slightly different projectile, area, status, resource, or presentation behavior. Add a delivery shape or tunable when the behavior is reusable; use the bespoke tier only for genuinely unique simulation loops.

There are also a few identity-keyed validation rules that are acceptable today but worth watching:

- `METEOR` owns sky-origin area travel and its required stun impact.
- `INSTANT_BEAM` owns charged-release beam logic.
- `ELECTROCUTE` owns channel cadence/target maintenance.
- `NEGATE` owns active-spell/projectile cancellation.

### Refactor shape

Keep this as a containment/refinement item:

- Keep `SpellId` as the reducer/wire lookup id.
- Keep `SpellBehavior` as the dispatch primitive.
- Keep the replicated `SpellDefinition` table shape unchanged unless client needs force it.
- Keep the four bespoke spells explicit and budgeted.
- Move any future ordinary spell behavior into `gameplay.delivery`, `ProjectileMotionTunables`, shared `ImpactEffect`/`StatusApplication`, or a new reusable behavior shape.
- If another spell wants “Meteor-like” sky-origin area travel, promote that from a `METEOR` identity rule into an authored area-delivery option.
- If another spell wants channel ticking or charged-release beam behavior, either generalize `CHANNEL`/`INSTANT_BEAM` as reusable shapes or keep the new spell out until the abstraction is clear.

### Priority

Not recommended next unless new unusual spells are being authored soon. For normal projectile, area, status, and resource spells, the current stack is already in decent shape. The higher near-term payoff is likely performance cleanup such as per-tick snapshot caching.

---

## 4. Phase-scoped player snapshot sets

**Status: Completed 2026-05-13.** The stale version of this item overstated the duplicated work and pointed at the old helper path. The useful cleanup was narrower: avoid rebuilding the same player snapshot vector and identity index across adjacent simulation phases, without introducing a global tick context.

### What changed

- Added `PlayerSnapshotSet` next to `PlayerSnapshot` in [`server/src/combat/player_snapshot.rs`](../server/src/combat/player_snapshot.rs).
- `PlayerSnapshotSet` owns the snapshot slice plus the `Identity -> snapshot index` map, so callers no longer rebuild the same index by hand.
- `tick_spells_with_snapshots` and `tick_combat_projectiles_with_snapshots` now accept the shared set.
- `game_loop` builds one set for the spell/projectile simulation window and reuses it across both phases before queued effects are resolved.
- Existing `tick_spells`, `tick_combat_projectiles`, and `collect_player_snapshots` wrappers remain for call sites that are not naturally part of this shared phase.

### What intentionally stayed

- No global tick context or cross-phase cache.
- No snapshot lifetime across ticks.
- Active-cast completion, instant cast helpers, and melee area impacts still collect locally where their state boundaries are different.
- Player snapshots remain read-only phase inputs; queued combat effects still resolve after spell/projectile simulation.

### Residual note

This is enough until profiling shows player-table reads or projectile/player collision indexing are material. The next performance step would be spatial partitioning for collision, not a broader snapshot cache.

---

## Watch list (out of scope today, profile before acting)

These are not refactors I'd schedule pre-emptively, but they're the patterns most likely to surface if performance ever becomes a concern:

- **`tick_negate_spell` is O(spells²).** Walks all active spells per Negate to check intersection. Cheap at typical loads, degrades during spell-flurry scenarios. Profile if Negate becomes meta.
- **World raycast cost per projectile.** [`raycast_world_with_layout`](../server/src/spells/scene_query.rs) is called by every projectile every tick. Cost depends on map geometry complexity — unknown until profiled. Worth measuring early when you can stress-test 50 active casters.
- **Spatial partitioning for projectile-vs-player collision.** Currently O(projectiles × players) per tick. Fine at 50 players. Becomes a real ceiling beyond ~100. Don't build until scale targets change.
- **SpacetimeDB replication overhead at 50 players.** Mostly out of code control. Profile bandwidth and serialization at target scale; the server simulation being cheap doesn't help if replication dominates.
- **Unity client tick at 50 visible players.** Animation, VFX, prediction reconciliation all scale with visible player count. Server budget headroom doesn't translate to client headroom — measure separately.

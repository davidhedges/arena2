# Combat Projectile Replication Architecture Plan - 2026-05-15

Status: Archived. Phases 1-3 were implemented; Phase 4 was split into
`docs/combat-projectile-interest-load-controls-plan-2026-05-15.md`.

## Purpose

This note documents a production target for combat projectile ownership and
replication.

The current system is already close to the right gameplay model: the server is
authoritative for projectile travel, collision, block, parry, damage, status
effects, and terminal outcomes. The concern is not server authority. The concern
is that live projectile simulation state is also exposed as a high-frequency
client presentation stream.

The goal is to keep accurate server-side projectile gameplay while preventing
each projectile from becoming a 30.3Hz / 33ms replicated object for every
subscribed client.

## Current Context

Projectile-related runtime state currently has three separate responsibilities:

- `ActiveCombatProjectile`
  - Live projectile state used by the server to advance position, lifetime,
    range, homing/orbit/boomerang behavior, collision, and defense resolution.
  - Declared as a public SpacetimeDB table in `server/src/combat.rs`.
  - Updated every server projectile tick in `server/src/combat/projectiles.rs`.
  - Currently included in scoped gameplay subscriptions by caster world context.
- `CombatEvent`
  - Public combat presentation and outcome stream.
  - Carries cast/release/update/contact/impact/block/parry/fizzle facts.
  - Consumed by Unity for combat animation, VFX routing, projectile visual
    updates, hit reactions, and terminal effects.
- `ActiveCombatProjectileTargetState`
  - Internal per-projectile/per-target state for multi-contact projectiles such
    as orbit and boomerang behavior.
  - Tracks hit cooldown, hit count, and overlap state.

These are not all redundant. The server needs live projectile state, and the
client needs combat facts. The overlap is specifically that `ActiveCombatProjectile`
is both authoritative simulation state and a public client-facing replication
surface.

## Problem

`ActiveCombatProjectile` is updated as part of normal server simulation. Because
the table is public and scoped gameplay subscriptions include it, those row
updates can replicate to clients at the server tick rate.

For spell projectiles, that also creates two client-visible projectile motion
channels:

```text
ActiveCombatProjectile public row updates
  -> client starts/updates/fizzles table-driven projectile visuals

CombatEvent rows
  -> client starts/updates/impacts/blocks/parries/fizzles combat visuals
```

The render-side duplication is narrower than the replication problem:

- Spell projectiles currently pass `CombatVFXDispatcher.IsTableDrivenProjectile`
  and can receive table-driven visual starts, updates, and fizzles.
- Weapon projectiles are ignored by that table-driven Unity path, so their
  current cost is wire replication and SDK/table change handling, not duplicate
  render routing.

`CombatEvent` already has the right shape for throttled presentation facts. For
example, projectile update events are emitted through an update accumulator and
`update_interval_seconds`. However, the public `ActiveCombatProjectile` row can
still update every simulation tick, defeating that throttle.

This is likely to become a production bottleneck as projectile counts rise:

- Server writes scale with active projectile count every tick.
- Network replication scales with active projectile count and subscriber scope.
- Unity/SDK table change handling can occur at simulation rate.
- Spell projectile visuals may receive both row-driven updates and event-driven
  corrections.
- Interest management is harder because the live state table is public behavior,
  not just server-private simulation storage.

## Design Principle

Simulate at gameplay rate. Replicate at presentation rate.

The server should advance projectile physics/collision every authoritative tick.
Clients should receive only the facts needed to render plausible visuals and react
to authoritative outcomes.

Projectile dodge, block, and parry do not require full-rate replicated projectile
rows. They require accurate server simulation. The client visual is not the source
of truth.

## Target Ownership Model

Production ownership should look like this:

```text
Server-private projectile state
  ActiveCombatProjectile or replacement runtime table
  ticked at fixed server rate
  resolves world collision, player capsule collision, block, parry, damage,
  status effects, max range, lifetime, and terminal behavior

Public projectile presentation stream
  CombatEvent COMBAT_RELEASE
  CombatEvent COMBAT_UPDATE, throttled or threshold-based
  CombatEvent COMBAT_CONTACT
  CombatEvent COMBAT_IMPACT
  CombatEvent COMBAT_BLOCK
  CombatEvent COMBAT_PARRY
  CombatEvent COMBAT_FIZZLE

Unity presentation
  starts visuals from release/spawn facts
  simulates normal projectile motion locally
  applies occasional authoritative corrections
  resolves visuals from terminal events
```

`CombatEvent` should be the public contract for projectile presentation and
outcomes. `ActiveCombatProjectile` should be a gameplay implementation detail.

## Gameplay Semantics

Projectile defense and evasion remain server-authoritative.

### Block And Parry

Block and parry are projectile interactions. When the server finds a valid player
collision, it resolves defense state against projectile behavior:

```text
projectile swept segment intersects player capsule
  -> if block/parry succeeds
       emit COMBAT_BLOCK or COMBAT_PARRY
       apply projectile-specific terminal/deflect/continue behavior
  -> otherwise
       emit COMBAT_IMPACT
       queue effects
       apply projectile-specific terminal behavior
```

### Dodge

Dodge should not be modeled as projectile resolution or blanket immunity.

In the intended realistic model, dodge is authoritative movement. If the dodge
moves the player's capsule out of the projectile swept segment, the projectile
misses. If the capsule is still in the swept segment, the projectile hits and
then block/parry/normal impact rules apply.

This means reducing projectile replication does not weaken dodge correctness.
The server still evaluates projectile path against authoritative player capsule
positions.

## Proposed Contract

### Server

- Keep authoritative projectile simulation state server-side.
- Emit a public `COMBAT_RELEASE` or equivalent spawn fact when the projectile
  leaves the caster.
- Emit public `COMBAT_UPDATE` only when a client correction is useful:
  - fixed visual interval, such as 5-10Hz for homing/orbit/boomerang projectiles;
  - threshold-based correction when direction/position diverges materially;
  - optional no updates for simple linear projectiles after release.
- Emit exactly one terminal public event for terminal projectile outcomes:
  - `COMBAT_IMPACT`
  - `COMBAT_BLOCK`
  - `COMBAT_PARRY`
  - `COMBAT_FIZZLE`
- Continue to emit non-terminal contact events for projectile types that can hit
  multiple targets without ending, such as boomerang or orbit behavior.
- Do not rely on public table row updates for normal projectile presentation.
- Do not expose gameplay-only projectile fields through the public presentation
  event contract unless a visual actually needs them.

### Client

- Treat `CombatEvent` as the presentation source of truth.
- Start projectile visuals from release/spawn facts.
- Locally integrate simple projectile visuals between authoritative facts.
- Apply `COMBAT_UPDATE` as a correction, not as the only motion source.
- Resolve terminal visuals from terminal events.
- Stop using `ActiveCombatProjectile.OnUpdate` for routine projectile movement.
- Optionally keep `ActiveCombatProjectile.OnInsert` as a migration bridge only
  until every projectile release path emits a complete spawn fact.
- Keep the current table-driven visual path for orbit/boomerang spell
  projectiles until their event payloads and client integrators are explicit.

## Migration Plan

### Phase 1: Remove Duplicate Client Motion Routing

Goal: stop normal visual motion from using full-rate `ActiveCombatProjectile`
updates.

- Audit current Unity projectile visual routing in `CombatVFXDispatcher`.
- Keep `CombatEvent` routing for update/impact/block/parry/fizzle.
- Disable or sharply narrow `ActiveCombatProjectile.OnUpdate` visual updates for
  spell projectiles that already have complete event-driven presentation facts.
- Do not remove table-driven updates from orbit/boomerang spell projectiles until
  Phase 2 gives those visuals either complete event payloads or an explicit
  correction-only path.
- Allow table-driven updates only for projectiles that do not yet have complete
  event-driven presentation facts.
- Add debug logging or counters to verify whether projectile visuals are updated
  by table rows, combat events, or both.
- Verify `CombatVFXDispatcher.IsProjectileEvent` does not silently reject
  non-linear projectile events. It currently gates projectile events on positive
  `Speed`, positive `MaxDistance`, and non-empty `ProjectileId`; orbit-style
  motion may need a different predicate.

This phase can be done before changing server table visibility.

### Phase 2: Make Event Payloads Complete

Goal: ensure clients can start and run projectile visuals without the public live
projectile row.

This is a schema migration, not just a population task. The current `CombatEvent`
schema does not carry every field below. Either extend `CombatEvent` with a
small presentation-only subset, or introduce a narrowly named sibling projectile
presentation event/table.

Each projectile release/spawn fact should carry enough data for presentation:

- `action_instance_id`
- `projectile_instance_id`
- `ability_id`
- `action_kind`
- `source_kind`
- `projectile_id` or resolved projectile body VFX id
- caster
- intended target when applicable
- origin
- direction
- speed
- max distance
- radius if needed by visuals
- motion kind
- update/correction policy when applicable

Non-linear projectile presentation also needs an explicit choice:

- integrate client-side from a complete authored payload; or
- remain correction-only at a controlled event rate.

If integrated client-side, orbit/boomerang payloads need the presentation subset
of fields that currently live only on `ActiveCombatProjectile`, such as:

- `orbit_initial_yaw`
- `orbit_radius`
- `orbit_height`
- `orbit_angular_speed_deg_per_sec`
- `orbit_phase_offset_deg`
- `boomerang_outbound_distance`
- `boomerang_return_speed`
- `boomerang_returning` on correction/update facts

Do not copy the whole `ActiveCombatProjectile` row into public events. The
presentation contract should exclude gameplay-only fields such as damage,
parry/block behavior, primary-resource grants, lifetime bookkeeping, update
accumulators, and any fields used only for server-side hit resolution.

For normal linear projectiles, release plus terminal event should be enough.
For homing, orbit, boomerang, and other non-linear projectiles, correction events
should remain throttled and data-complete.

### Phase 3: Stop Public Replication Of Live Projectile State

Goal: make the authoritative live projectile table private or replace it with
server-private runtime state.

Preferred end state:

- `ActiveCombatProjectile` is no longer public.
- Only `CombatEvent` and final effect/state tables are public presentation data.
- Client subscriptions no longer include live projectile state.

SpacetimeDB already supports private tables in this codebase; for example,
`ActiveCombatProjectileTargetState` is declared without `public`. This phase is
therefore tracked implementation work, not a fundamental feasibility question.

Known affected client-side surfaces include:

- `GameplaySubscriptionPlanner.BuildScopedActiveCombatProjectileQuery`
- `CombatVFXDispatcher` active-projectile subscription and callbacks
- `CombatProjectileVisualController` overloads that accept `ActiveCombatProjectile`
- `WeaponProjectileVFX(ActiveCombatProjectile ...)`

If generated bindings or migration risk make table privacy impractical
immediately, use an intermediate split:

- private/full-rate server projectile state;
- optional public `projectile_visual_snapshot` table or event stream updated at
  presentation rate only.

The important boundary is that full-rate simulation state must not be a public
subscription contract.

### Phase 4: Add Interest And Load Controls

Goal: make high projectile counts predictable under production load.

- Scope projectile presentation events by world/instance/open-world scene.
- Avoid sending projectile facts to clients that cannot possibly see or interact
  with them.
- Add server profiling counters for active projectile count, projectile contacts
  scanned, projectile events emitted, and projectile row/event writes per tick.
- Add client counters for projectile visual starts, updates, corrections,
  terminal events, and pooled instance reuse.

## Acceptance Criteria

The production target is met when:

- Server projectile collision still runs at the fixed authoritative tick rate.
- Simple projectile visuals do not require public per-tick table row updates.
- Block, parry, impact, contact, and fizzle still appear correctly on clients.
- Dodge correctness is based on authoritative movement/capsule position, not
  client projectile visuals or blanket immunity.
- `CombatEvent` is the only routine public projectile presentation stream.
- High active projectile counts increase server simulation cost, but do not create
  matching full-rate client replication cost.
- Spell projectiles do not receive duplicate visual update paths for the same
  projectile.

## Non-Goals

- Do not make clients authoritative for projectile hits.
- Do not add dodge invulnerability as part of this migration.
- Do not remove server-side projectile simulation.
- Do not collapse gameplay, animation, VFX, and projectile presentation into one
  monolithic data object.
- Do not require a full combat rewrite before reducing projectile replication
  pressure.

## Open Questions

- Should projectile release facts be represented only as `CombatEvent`, or should
  there be a more narrowly named public projectile presentation event/table?
- Which projectile motion kinds require correction events, and at what default
  interval?
- Should projectile visual correction be interval-based, error-threshold-based,
  or both?
- How should projectile interest management be expressed in current subscription
  scopes?

## Implementation Notes

- The first implementation folds projectile presentation fields into
  `CombatEvent` rather than adding a sibling event/table. This keeps the public
  contract count low, but it widens every combat event row with zero-valued
  projectile fields. Revisit a narrower projectile-presentation stream if
  measured combat-event bandwidth becomes a problem.
- `CombatEvent` is a public SpacetimeDB table and this migration adds columns.
  Live deployments need an explicit schema migration or a drop/redeploy plan.
- Client projectile visuals now treat server terminal events as authoritative
  for lifetime. Local max-distance expiry is only a presentation fallback for
  legacy/non-authoritative construction paths.

## Original Recommended First Implementation Step

This was the pre-implementation stepping stone. The current implementation has
already moved through the private-table phase and should not reintroduce the
public `ActiveCombatProjectile` subscription path.

Start by removing duplicate client motion routing.

Keep the server simulation unchanged. Keep `ActiveCombatProjectile` public during
the first pass. Change Unity so routine linear spell projectile movement is
driven by `CombatEvent` release/update/terminal facts, and use
`ActiveCombatProjectile` callbacks only as a temporary compatibility bridge.
Keep orbit/boomerang spell projectiles on the existing table-driven path until
Phase 2 explicitly covers their payload and routing.

That gives an immediate proof that projectile presentation can run without
depending on full-rate public row updates. Once that is stable, making live
projectile state private becomes a much smaller and safer change.

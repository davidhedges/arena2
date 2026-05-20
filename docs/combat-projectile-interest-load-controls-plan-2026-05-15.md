# Combat Projectile Interest And Load Controls Plan - 2026-05-15

Status: In progress.

## Purpose

This plan covers the remaining Phase 4 work split out from the combat projectile
replication architecture migration.

The completed migration made live projectile runtime state server-private and
moved routine projectile presentation onto `CombatEvent`. This plan focuses on
making high projectile counts measurable and predictable under production load.

## Current State

- Server projectile simulation remains authoritative and runs at the fixed
  gameplay tick rate.
- `ActiveCombatProjectile` is server-private.
- Unity no longer subscribes to live projectile rows.
- `CombatEvent` is the routine public projectile presentation stream.
- Projectile presentation events are scoped through the existing combat-event
  world/instance subscription shape.
- There are not yet dedicated counters for projectile event volume, correction
  rate, visual lifecycle churn, or subscriber interest efficiency.

## Goals

- Make projectile load visible before it becomes a production incident.
- Keep projectile presentation traffic proportional to what clients can use.
- Preserve server-authoritative collision, block, parry, dodge-by-movement, and
  terminal outcomes.
- Avoid premature broad architecture changes until counters show a real need.
- Use a separate synthetic load harness plan to create large projectile counts
  before real large open-world traffic exists.

## Non-Goals

- Do not make clients authoritative for projectile hit resolution.
- Do not reintroduce public full-rate projectile simulation rows.
- Do not build a new global telemetry platform as part of this task.
- Do not split `CombatEvent` into a new projectile event stream unless measured
  event bandwidth justifies it.

## Phase 1: Baseline Instrumentation

Add lightweight counters around the current event-driven implementation.

Server counters:

- active projectile count per tick
- projectile rows updated per tick
- projectile update events emitted per tick
- projectile release/contact/impact/block/parry/fizzle events emitted per tick
- projectile collision candidate scans per tick
- projectile target contacts resolved per tick
- per-motion-kind projectile counts: linear, homing, orbit, boomerang

Client counters:

- projectile visuals started
- projectile visuals updated
- projectile visuals corrected/snapped
- projectile visuals terminally resolved
- projectile visuals disposed without terminal event
- missing projectile prefab/template count by projectile id

Implementation guidance:

- Prefer existing logging/profiling surfaces if available.
- Keep counters cheap and allocation-light.
- Gate verbose output behind debug/development configuration.
- Aggregate per window instead of logging every projectile event.

## Phase 2: Synthetic Projectile Load Harness

Build the focused harness described in:

- `docs/combat-projectile-load-harness-plan-2026-05-15.md`

The broader interest/load-control work depends on this harness because the game
does not yet have enough real large-world traffic to produce representative
projectile pressure.

The harness must stay siloed. Treat it as development/test infrastructure, not
as load-bearing combat architecture. Normal projectile, spell, melee,
progression, and VFX code should not depend on harness types or scenario ids.

## Phase 3: Define Production Budgets

Use the counters to define initial budgets.

Track at minimum:

- expected active projectile count in normal 1v1, small group, and stress-test
  scenarios
- maximum acceptable projectile presentation events per second per client
- maximum acceptable server projectile collision scans per tick
- maximum acceptable Unity projectile visual churn per second
- acceptable correction/snap rate for non-linear projectile visuals

The first budgets can be conservative placeholders. The important outcome is a
visible threshold that can fail loudly in stress tests.

## Phase 4: Tighten Interest Management

Review whether current `CombatEvent` subscription scoping is enough.

Candidate improvements:

- Keep world/instance/open-world-scene scoping as the base filter.
- Avoid sending projectile presentation events to clients outside relevant
  visibility or interaction range when the subscription model can express it.
- Consider separate projectile presentation scoping only if `CombatEvent`
  bandwidth becomes a measured bottleneck.
- Preserve terminal outcome visibility for players who need hit reactions,
  damage feedback, or combat log effects.

Design constraint:

- Do not hide server-authoritative effects from affected clients just because the
  projectile visual itself was out of view.

## Phase 5: Add Regression Tests

Add focused verification for load and lifecycle behavior.

Useful checks:

- high projectile counts do not recreate public full-rate table replication
- simple linear projectile release plus terminal event renders without update
  spam
- orbit/boomerang projectiles use throttled correction events and smooth local
  presentation
- client visuals do not leak when projectiles fizzle, block, parry, impact, or
  are cleaned up by abnormal server paths
- debug counters remain stable under repeated spawn/resolve cycles

## Acceptance Criteria

- Projectile load is visible through server and client counters.
- Stress scenarios can report active projectile count, emitted event rate, and
  visual lifecycle churn.
- Current world/instance scoping is either confirmed sufficient for the next
  production milestone or replaced with a measured narrower scope.
- No client subscribes to public full-rate `ActiveCombatProjectile` state.
- Server projectile collision remains authoritative and fixed-rate.
- Any future decision to split `CombatEvent` has measured bandwidth data behind
  it.

## Archive Link

This plan supersedes Phase 4 of:

- `docs/archive/2026-05-superseded-plans/combat-projectile-replication-architecture-plan-2026-05-15.md`

## Implementation Notes

Initial load-control slice:

- added a coarse `PlayerSnapshotSet` spatial index for projectile broad-phase
  candidate lookup
- linear projectile collision now queries nearby player candidates before the
  authoritative ray/capsule hit test
- boomerang enemy contact scans now query nearby player candidates before the
  authoritative ray/capsule hit test
- orbit projectile overlap scans now query nearby player candidates around the
  orbit position and union them with existing per-projectile target state so
  overlap exits/cooldowns are still processed correctly
- world collision remains authoritative and unchanged

Expected effect:

- `server_collision_candidate_scans` should drop substantially for linear and
  orbit/boomerang-heavy scenarios, especially dense target layouts
- `server_world_collision_queries`, terminal events, and hit outcomes should
  remain behaviorally consistent

Follow-on melee/AoE load-control slice:

- pending melee impacts now collect one `PlayerSnapshotSet` per impact
  resolution and reuse it for targetless hit volumes plus impact-area effects
- targetless melee hit volumes query nearby snapshot candidates around the
  caster before running the existing range/cone checks
- melee impact-area effects query nearby snapshot candidates around the impact
  point before running the existing AoE overlap checks
- pending melee rows still resolve in the same order, and effects may still be
  resolved between pending rows as before

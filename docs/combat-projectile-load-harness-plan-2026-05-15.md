# Combat Projectile Load Harness Plan - 2026-05-15

Status: Implemented.

## Purpose

Build a deterministic way to create high projectile counts before the game has a
large enough open world population to produce that load naturally.

The harness should exercise the same server projectile simulation and
`CombatEvent` presentation paths used by normal gameplay. It exists to reveal
costs, leaks, and event-volume problems early, not to become gameplay code.

## Goals

- Stress projectile simulation without needing a populated large open world.
- Test each projectile motion class alone before testing mixed scenarios.
- Produce repeatable measurements for future optimization work.
- Validate that client visuals do not leak under high projectile churn.
- Provide enough load data to decide whether spatial broadphase or narrower
  event interest management is actually needed.

## Non-Goals

- Do not add player-facing abilities or balance data.
- Do not bypass normal projectile tick, collision, defense, or event emission
  code paths.
- Do not make synthetic projectile results authoritative gameplay.
- Do not implement spatial partitioning as part of the harness unless the
  harness data shows the current scan path is hot.
- Do not let normal combat code depend on harness types, scenario ids, or helper
  modules.
- Do not mix harness setup/cleanup logic into ordinary spell, melee, projectile,
  progression, or VFX code paths.

## Siloing Requirements

The harness must be obvious non-production infrastructure.

Implementation rules:

- Put harness code in a clearly named module, such as
  `server/src/combat/projectile_load_harness.rs`.
- Keep the module behind a test/development gate where practical.
- Expose one narrow entry point for running a scenario.
- Keep scenario configuration in harness-owned structs/enums.
- Do not add harness-specific fields to production tables.
- Do not add harness branches to normal projectile ticking, collision,
  block/parry, spell, melee, or effect code.
- Do not make production code import harness modules.
- Reuse existing production functions from the harness direction only:
  harness -> production, never production -> harness.
- Keep Unity/client support optional and dev-only. If added, it should call the
  server harness entry point and display counters; it should not own projectile
  simulation.

Allowed production touch points:

- a dev-only reducer or test helper that invokes the harness
- insertion of temporary server runtime rows needed to exercise normal projectile
  simulation
- existing projectile tick/collision/effect/event code
- existing playground/fake target facilities if they already provide suitable
  test actors
- existing counter/logging surfaces

Forbidden couplings:

- no scenario ids in normal combat resolution
- no harness conditionals in `tick_combat_projectiles`
- no harness-specific VFX registry entries required by normal gameplay
- no changes to progression catalog authoring just to support the harness
- no production subscription changes solely for harness convenience

## Harness Shape

Recommended first version:

- dev-only server reducer or test helper
- configurable projectile count
- configurable target count and target spacing
- configurable spawn spread radius
- configurable duration or tick count
- configurable scenario id
- deterministic seed for reproducible runs
- automatic cleanup of spawned projectiles, target state, and temporary actors

If a reducer is added, keep it unavailable in production builds or guarded by a
development/admin capability.

Suggested responsibility split:

- scenario builder: creates deterministic projectile and target layouts
- scenario runner: starts, advances, and cleans up a run
- metrics adapter: reads normal counters/log output for the scenario window
- optional dev reducer: validates access and delegates to the runner

The scenario builder should be pure or close to pure where possible. The runner
is the only piece that should touch database state.

## Projectile Buckets

Run isolated scenarios first. Mixed scenarios come after the isolated costs are
known.

### Linear Weapon Projectile

Example: standard arrow.

Purpose:

- baseline fast straight-line swept collision
- weapon projectile event path
- world collision and player capsule terminal impact cost
- normal block/parry interaction

### Linear Spell Projectile

Example: fireball or icicle-style projectile.

Purpose:

- spell projectile release/update/terminal payloads
- spell impact effect queueing
- spell projectile VFX id resolution
- block/parry behavior for spell projectiles

### Homing Spell Projectile

Purpose:

- retargeting/turn-rate cost
- correction event rate
- local visual correction behavior
- target movement sensitivity

### Orbit Projectile

Purpose:

- repeated overlap/contact checks over time
- per-projectile/per-target cooldown state
- non-terminal impact/contact event volume
- client local angular presentation under throttled correction events

### Boomerang Projectile

Purpose:

- outbound and return phase behavior
- repeated non-terminal contacts
- phase-scoped target state
- return direction updates
- terminal return/impact/fizzle behavior

## Scenario Matrix

Start with these named scenarios:

- `baseline_linear_arrows`
- `linear_spell_projectiles`
- `homing_projectiles`
- `orbit_projectiles`
- `boomerang_projectiles`
- `mixed_realistic`
- `mixed_worst_case_dense_targets`
- `moving_lane_homing_boomerang`

Recommended initial mix for `mixed_realistic`:

- 60% linear weapon
- 20% linear spell
- 10% homing
- 5% orbit
- 5% boomerang

Recommended initial mix for `mixed_worst_case_dense_targets`:

- 35% linear weapon
- 20% linear spell
- 15% homing
- 15% orbit
- 15% boomerang

## Target Layouts

Use multiple deterministic layouts:

- sparse line: validates long-range sweeps with few candidates
- dense cluster: stresses repeated candidate checks and contact state
- ring around casters: stresses orbit projectiles
- moving lane: validates homing and boomerang behavior against changing target
  positions if movement simulation is included

The first pass can use stationary playground/fake targets. Moving targets are a
follow-up if the stationary harness already exposes useful load data.

## Measurements

The harness should record the normal counters from the interest/load-controls
plan:

- active projectile count per tick
- projectile rows updated per tick
- projectile collision candidate scans per tick
- projectile target contacts resolved per tick
- projectile events emitted by type
- per-motion-kind projectile counts
- client projectile visuals started/updated/corrected/terminated
- client visuals disposed without terminal event
- missing projectile prefab/template counts

Avoid a separate measurement path. The harness is useful only if it exercises the
same counters as normal gameplay.

## Implementation Order

1. Add server-side deterministic scenario construction.
2. Add isolated projectile bucket scenarios.
3. Add automatic cleanup.
4. Add mixed scenarios.
5. Add optional Unity/dev command to trigger a scenario while connected.
6. Capture and compare counter windows for each scenario.

## Acceptance Criteria

- A developer can run at least one high-count projectile scenario without a
  populated open world.
- Linear weapon, linear spell, homing, orbit, and boomerang projectiles can each
  be stressed independently.
- Mixed scenarios produce repeatable counter output with a deterministic seed.
- Harness projectiles use normal server projectile simulation and normal
  `CombatEvent` emission.
- Harness cleanup leaves no active projectile rows or target-state rows behind.
- The results are sufficient to decide whether to build server-side spatial
  broadphase next.

## Related Plan

This plan is a focused slice of:

- `docs/combat-projectile-interest-load-controls-plan-2026-05-15.md`

## Implementation Notes

Initial implementation:

- server module: `server/src/combat/projectile_load_harness.rs`
- reducer: `run_projectile_load_harness(scenario, projectile_count, target_count, seed)`
- reducer: `cleanup_projectile_load_harness()`
- private tracking tables:
  - `projectile_load_harness_run`
  - `projectile_load_harness_actor`
- generated C# reducer bindings:
  - `RunProjectileLoadHarness`
  - `CleanupProjectileLoadHarness`

The implementation seeds normal `ActiveCombatProjectile` rows and `CombatEvent`
release rows, then relies on existing projectile tick/collision/defense/event
logic. Normal projectile code does not import harness types or branch on harness
scenario ids.

Unity readout:

- dev-only overlay: `Assets/Arena/Runtime/Debug/ProjectileLoadHarnessOverlay.cs`
- toggle: `=`
- client presentation modes:
  - `server_sim`: suppresses harness combat presentation/VFX on the Unity client
    while still counting replicated combat events
  - `full_client`: allows normal harness VFX/presentation for client pressure
    testing
- displays harness event rate, update/contact rate, active/peak observed
  projectiles, event totals, client projectile visual counts, missing prefab
  count, auto-disposed visual count, frame timing, allocated memory, and latest
  server projectile tick metrics
- run-level server peaks and totals are read from server-owned cumulative fields,
  not accumulated by Unity, so client hitches do not cause missed server samples

Server diagnostics:

- public diagnostics row: `combat_projectile_tick_metrics`
- records active projectiles, projectile rows updated, collision candidate
  scans, world collision queries, contacts resolved, emitted update/contact/
  terminal/block-parry events, and motion-kind mix
- records server-side run peaks and cumulative totals; the harness resets these
  counters at the start of each run
- wall-clock projectile tick timing is not measured inside the reducer because
  `std::time::Instant::now()` panics in the SpacetimeDB WASM runtime; use host
  logs/profiling for elapsed reducer time
- this is generic projectile instrumentation, not scenario logic; the load
  harness still only seeds and cleans up normal runtime rows

Remaining measurement gap:

- no known first-pass gaps for this harness slice

Supported initial scenarios:

- `baseline_linear_arrows`
- `linear_spell_projectiles`
- `homing_projectiles`
- `orbit_projectiles`
- `boomerang_projectiles`
- `mixed_realistic`
- `mixed_worst_case_dense_targets`
- `moving_lane_homing_boomerang`

Example reducer calls:

- `run_projectile_load_harness("baseline_linear_arrows", 500, 32, 1)`
- `run_projectile_load_harness("mixed_worst_case_dense_targets", 1000, 96, 42)`
- `run_projectile_load_harness("moving_lane_homing_boomerang", 300, 48, 7)`
- `cleanup_projectile_load_harness()`

Completed first-pass scope:

- deterministic stationary and moving target layouts
- isolated and mixed projectile scenarios
- manual cleanup reducer
- Unity dev overlay with bounded duration, auto capture/cleanup, and copied
  summary output across captured runs

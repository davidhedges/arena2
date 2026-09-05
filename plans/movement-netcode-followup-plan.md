# Movement Netcode Follow-Up Plan (Historical)

> **Status: historical follow-up; the main diagnoses below are superseded.**
> Source review on 2026-09-04 verified tick-indexed commands/acks and replay,
> movement-context history in prediction, and separate local presentation.
> See the [current movement runtime map](../docs/project-structure.md#movement-runtime)
> for the implemented boundaries, deliberate fallbacks and verification limits.
>
> “Current,” “remaining,” and the phase ordering below refer to the migration
> state when this plan was written. In particular, the tick-versus-sequence
> mismatch, always-default prediction context and shared simulation/visual-root
> diagnoses are not descriptions of today's ordinary movement path. The old
> ownership-cleanup and collision-parity proposals require fresh evidence;
> this review neither declares every success criterion proven nor turns those
> proposals into an approved backlog.

## Historical Proposal

## Why This Exists

The current movement/netcode migration fixed many real issues, but it still contains one protocol-level shortcut that prevents it from being a fully professional implementation:

- the client replays per pending command
- the server simulates once per authoritative tick using the latest intent row

That means command versions and simulation ticks are still conflated.

This document captures the remaining work so it is not lost.

## Primary Root Cause

The main remaining correctness issue is:

- `seq` is being used like a simulation tick on the client
- `seq` is only a latest-intent version on the server

Current behavior:

- the client may replay one full `50 ms` movement step per pending command
- the server still performs exactly one movement step per authoritative tick
- the server then sets `last_processed_seq` to the latest intent it saw for that tick

That creates periodic over-prediction / under-prediction cycles and explains the alternating smooth / vibrating windows under jitter.

## Required Direction

The movement protocol needs to become explicitly tick-based.

The correct model is:

1. client samples input for movement tick `N`
2. client builds one movement command for tick `N`
3. client predicts exactly one movement step for tick `N`
4. server buffers commands by tick
5. server simulates exactly one movement step per server tick
6. server acknowledges `last_processed_tick`
7. client rewinds to authoritative tick/state and replays later ticks

## Do Not Keep Patching Around This

Avoid these shortcuts:

- treating pending command count as replay step count
- inferring tick count indirectly from command arrival patterns
- adding more visual correction layers to hide a protocol mismatch
- tuning more smoothing knobs before fixing the tick contract

## Remaining Shortcuts To Remove

### 1. Tick vs Seq Mismatch

This is the highest priority issue.

Replace:

- `last_processed_seq`
- latest-intent-per-tick semantics
- client replay per command

With:

- explicit movement tick ids
- buffered server-side commands by tick
- one authoritative step per tick
- client replay by tick

### 2. Missing Prediction Context

Client prediction still hardcodes movement context values in the local driver:

- `isRooted: false`
- `moveSpeedMultiplier: 1.0f`

Prediction must use the same authoritative movement modifiers as the server.

### 3. Local Presentation Coupling

The local player still uses the same transform stack for:

- simulation root
- visible character
- camera-relevant presentation

That makes tiny corrections show up visually more than they should.

The target structure should be:

- hidden simulation root
- visible model child/root
- camera follow target tied to presentation, not raw corrected simulation

### 4. Transitional Local Orchestration

The current local runtime is improved but still transitional.

Responsibilities are still spread across:

- `LocalPlayerMotor`
- `MovementNetDriver`
- `LocalMovementPredictionDriver`

That is acceptable temporarily, but the final system should have a clearer per-tick ownership model.

### 5. Collision Parity Still Needs Proof

Client environments were implemented pragmatically, but parity is not yet proven under all gameplay cases.

This still needs targeted validation, especially around:

- edge transitions
- slopes / snap-down
- obstacle resolution
- arena-specific geometry cases

## Recommended Execution Order

### Phase 1: Fix The Protocol Properly

Deliverables:

- introduce explicit movement tick ids
- send one movement command per local movement tick
- buffer commands on the server by tick
- simulate one authoritative step per server tick
- acknowledge `last_processed_tick`
- replay client prediction by tick, not by command count

Important implementation note:

- the current reducer behavior that drops later commands while `intent.jump` is latched must not survive this redesign
- in the tick-based model, each tick's command stands on its own
- jump is just one field on tick `N`, not a special latched state that blocks later tick commands from existing

This is the most important remaining change.

### Phase 2: Feed Real Movement Modifiers Into Prediction

Deliverables:

- root state reaches client prediction
- move speed modifiers reach client prediction
- any other movement-affecting state reaches client prediction

Client prediction must stop assuming default movement context.

This is not polish. It is required for correctness under combat.

If root / slow / movement modifiers exist on the server but not in client prediction:

- the client will predict movement that the server does not allow
- reconciliation churn will spike during combat
- movement will feel stable out of combat and bad in combat, which is a strong sign this phase is incomplete

### Phase 3: Separate Simulation Root From Visual Root

Deliverables:

- hidden local simulation root
- visual model root that smooths toward simulation
- camera target that follows presentation rather than raw corrected root

This is the correct place for presentation smoothing, not in the protocol layer.

### Phase 4: Simplify Local Runtime Ownership

Deliverables:

- cleaner tick-owned local movement loop
- clearer ownership of input sampling, command building, prediction, and networking
- reduced transitional scaffolding

Do this after the protocol is correct.

### Phase 5: Validate Collision / Modifier Parity

Deliverables:

- targeted parity tests
- debug counters / instrumentation used during real network testing
- concrete reconciliation drift checks

Do not assume parity because it “feels mostly okay.”

## Success Criteria

The remaining work is complete when:

- local replay is keyed by authoritative ticks, not command count
- no-air-control movement stays stable under jitter
- straight-line movement no longer exhibits alternating smooth / vibrating windows
- airborne facing changes do not introduce visible vibration
- small correction noise does not leak through presentation
- prediction uses the same movement-affecting context as the server

## Bottom Line

The main unfinished problem is not “need more smoothing.”

The main unfinished problem is that the protocol still does not enforce:

- one command tick
- one simulation tick
- one replay tick

Fix that first. Everything else is secondary.

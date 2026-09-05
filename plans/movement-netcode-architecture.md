# Movement And Netcode Plan (Historical)

> **Status: historical migration proposal; not the current runtime contract
> or an executable backlog.** Source review on 2026-09-04 confirms that fixed
> input ticks, authoritative acknowledgments and local rewind/replay are
> implemented. Read the [current movement runtime map](../docs/project-structure.md#movement-runtime)
> for ownership, source links and verification limits.
>
> The text below preserves the original diagnosis and proposed migration.
> Its `seq` / `last_processed_seq` examples, tick-rate suggestions, old
> `Assets/Scripts` paths and instructions to replace the local runtime describe
> earlier work. They do not specify today's schema or authorize another rewrite.
> Goals not covered by the current runtime map remain historical proposals,
> not newly verified defects or approved next steps.

## Historical Proposal

## Goal

Replace the current hybrid movement stack with a production-grade architecture that is:

- authoritative on the server
- responsive for the local player
- scalable on live multiplayer servers
- bandwidth-conscious
- simple enough to reason about under load

This plan is intentionally biased toward operational reliability and server performance, not toward preserving the current Unity-side movement code.

## Hard Decisions

1. The server owns authoritative movement state.
2. The client predicts only its own player.
3. Remote players are never fully predicted by the client.
4. Movement simulation is kinematic and fixed-tick on the server.
5. The client and server use the same movement contract, the same fixed timestep, and the same collision inputs.
6. Unity `CharacterController` is not part of authoritative local movement.
7. Camera, targeting, and spell aim can influence facing, but they do not own movement state.
8. Movement is a pure simulation step over explicit state.
9. Corrections happen by authoritative rewind plus replay, not by directly correcting position.

## What Is Wrong With The Current Design

The current design mixes:

- client-owned `CharacterController.Move`
- delayed input transmission
- server-authoritative snapshots
- snap reconciliation without replay
- split input ownership
- global yaw state shared across unrelated systems

That combination does not scale cleanly and does not produce stable feel under network jitter or server load. It also forces the client and server to simulate movement differently, which guarantees visible corrections.

## Target Architecture

### 1. One Authoritative Movement Model

The server movement loop becomes the canonical model for:

- fixed tick rate
- grounded and airborne state transitions
- yaw updates
- jump edge consumption
- gravity
- horizontal collision resolution
- landing and snap-down behavior

The client does not invent a second locomotion model. It mirrors the server model only for local prediction.

The movement core must be expressible as:

- `new_state = f(old_state, command, dt)`

That function must be pure for prediction purposes:

- no hidden timers
- no implicit state outside the snapshot
- no Unity-owned movement state
- no side effects during simulation

Every piece of state required to continue simulation must live in the movement state itself or in immutable shared config.

Required movement state should include at minimum:

- position
- velocity
- grounded
- facing yaw
- any jump or grounding state needed by the rules
- any movement-mode state that affects simulation

If a rule matters to the next tick, it belongs in state.

### 2. One Command Format

Movement commands should be the only thing the client sends for locomotion.

Recommended command fields:

- `seq`
- `forward`
- `strafe`
- `facing_yaw`
- `jump_pressed`
- `client_time` or `client_tick` if useful for diagnostics

The important part is not the exact field names. The important part is that both the client predictor and the server simulation interpret the command the same way.

Commands must be processed in order.

Do not collapse movement to "latest command wins" semantics. For replay to be stable:

- client buffers every sent command until acknowledged
- server processes commands in sequence order
- client replays every unacknowledged command after rewind

If commands are dropped or collapsed, prediction will diverge from authority.

### 3. Server Snapshot Ack

Each authoritative movement snapshot for the local player must include:

- authoritative position
- authoritative velocity
- authoritative grounded state
- authoritative facing yaw
- `last_processed_seq`

And it must include any other state the simulation function depends on. The snapshot must be sufficient to restart simulation deterministically from that point.

Without `last_processed_seq`, the client cannot do proper replay of unacknowledged commands.

### 4. Local Prediction Only

The local client should:

- sample input every frame
- convert input into a movement command
- enqueue the command
- send the command immediately
- predict movement locally using the same movement contract as the server
- receive authoritative snapshot
- rewind to authoritative state
- replay unacknowledged commands

This eliminates the current snap-reconcile-without-replay problem.

Core rule:

- do not correct position directly

The correct pipeline is always:

- authoritative state
- rewind
- replay buffered inputs

Direct position correction reintroduces visible jitter and defeats the point of prediction.

### 5. Remote Players Stay Cheap

Remote players use a strict non-predictive policy.

Remote players are never simulated with full client prediction or replay.

Remote player rendering is driven by authoritative server snapshots using:

- interpolation as the default path
- a small interpolation buffer, target roughly 100 ms
- only bounded short-window extrapolation when snapshots are temporarily delayed
- capped extrapolation using only the last known authoritative velocity

Remote player extrapolation is visual only.

Remote players do not use:

- command buffering
- rewind
- replay
- collision-aware extrapolation
- invented client-side movement logic beyond interpolation and bounded extrapolation

Do not spend client CPU on predicting other players. That does not help scalability and increases complexity for little gain.

Server authority is final for remote players.

## Server Design For Scale

### Fixed Tick

Keep movement simulation on a fixed tick.

Recommended starting point:

- 20 Hz if you want MMO-like server cost discipline
- 30 Hz if movement feel is more important and server budget allows it

Do not jump to 60 Hz server movement by default. That increases CPU cost, bandwidth pressure, and reducer frequency before the architecture is even correct.

Client prediction and replay must use the same tick size as the server.

That means:

- fixed-tick server simulation
- fixed-tick local prediction
- replay on the same tick quantum

Variable `deltaTime` cannot be the prediction authority. Rendering may remain variable-rate, but movement simulation cannot.

### Kinematic Simulation

Use a simple kinematic movement model on the server:

- position
- velocity
- grounded flag
- facing yaw

Do not depend on heavyweight real-time physics for player movement on live servers. The current Rust-side kinematic structure is the right direction.

### Collision Data

Server and client prediction must use the same collision representation and the same collision rules.

That means:

- shared gameplay collision data
- shared terrain sampling rules
- shared player capsule dimensions
- shared grounding rules
- shared slide and sweep rules
- shared slope and step-down behavior

Approximate parity is not sufficient. Any mismatch in collision shapes or resolution rules will show up as replay drift and correction churn.

Do not use Unity scene physics on the client for predicted authority while the server uses a different collision system.

### Input Handling

Movement commands should be cheap to process:

- commands are applied in-order by `seq`
- stale duplicate commands are ignored
- jump remains edge-triggered
- all unacknowledged commands remain replayable on the client

This keeps server-side movement O(players) per tick and avoids expensive reconciliation logic on the server.

### Broadcast Strategy

Do not broadcast every movement update to every player.

Use:

- interest management by arena, shard, or visibility bucket
- per-client relevance filtering
- lower snapshot priority for distant actors
- coalesced movement updates where possible

The local player needs fast authoritative ack for replay. Distant actors do not.

## Client Design

### Replace Local Movement Ownership

Retire the current local authority path built around:

- `LocalPlayerMotor`
- `MovementNetDriver`
- `ServerReconciliationBridge`

Replace it with:

- `LocalInputCollector`
- `LocalMovementCommandBuilder`
- `LocalMovementPredictor`
- `LocalMovementNetClient`
- `LocalPlayerStateProvider`

These names are illustrative, not mandatory. The key point is responsibility separation.

The new predictor must not depend on Unity `CharacterController` or scene physics for authority.

It needs explicit code for:

- capsule sweep
- grounding
- horizontal collision resolution
- slide handling

That logic must match the server's movement rules closely enough that replay stays stable.

### One Input Sampling Path

Movement, RMB strafe mode, aim mode, jump, targeting mode, and spell-facing intent should be sampled into one local input state each frame.

Do not let movement, spells, and targeting each poll their own independent truth from different systems.

That means:

- no split between `StarterAssetsInputs` and raw `UnityEngine.Input` as movement authority
- no global yaw mutation by spell systems
- no hidden state transitions based on cursor lock alone

Movement owns yaw.

Other systems may suggest desired facing or aim direction, but the movement system is the sole authority that decides the simulated facing written into commands and state.

### Prediction State Ownership

The predicted local state should be instance-scoped, not static.

Other systems should read from a local player state provider for:

- current predicted position
- current predicted facing
- current predicted grounded state

Camera and spells can request that information. They should not mutate global movement state directly.

### Rendering

Render from predicted local state for the local player.

Render remote players from authoritative snapshots with a strict policy:

- interpolation is the default
- keep a small interpolation buffer, target roughly 100 ms
- if snapshots are briefly delayed, allow short bounded extrapolation using only last known authoritative velocity
- cap remote extrapolation to roughly 100 ms max
- do not perform collision-aware extrapolation for remote players
- do not invent remote client-side movement simulation
- when a new authoritative snapshot arrives with small error, smooth to it
- when a new authoritative snapshot arrives with large error, hard snap to it
- hard snap if the error is large

Only the local player uses prediction and replay.

Do not overload one simulation state object to serve both roles.

Small residual drift should be handled visually, not by bypassing replay.

Use:

- rewind plus replay for authoritative correction
- small visual smoothing for tiny residual error
- hard snap only for large discontinuities such as teleports, respawns, or severe desync

## Migration Plan

### Phase 1: Stabilize The Contract

Deliverables:

- server snapshot includes `last_processed_seq`
- movement command schema is finalized
- player dimensions and collision inputs are centralized
- fixed movement tick is defined as a shared constant
- authoritative movement state is made explicit and complete

This phase does not improve feel yet. It creates the contract that the rest of the system depends on.

### Phase 2: Build The New Client Path In Parallel

Deliverables:

- new local input collector
- new command builder
- new local movement predictor
- new local movement net client
- bounded replay ring buffer for unacknowledged commands
- fixed-tick local simulation loop decoupled from render frame rate

Keep the old path alive behind a feature flag until the new path is complete.

### Phase 3: Switch Local Player Authority To Prediction

Deliverables:

- local transform is driven by predicted state
- authoritative snapshots rewind and replay local commands
- `CharacterController` is removed from local movement authority
- direct position correction paths are removed from the normal movement loop

At this point the local player should stop rubberbanding under normal conditions.

### Phase 4: Remove Global And Duplicate State

Deliverables:

- delete static yaw bridge
- move spell aim to local player state provider
- move local aim origin off stale simulation render state
- remove mixed ownership of cursor and movement mode transitions

This phase is about making the system maintainable.

### Phase 5: Optimize For Live Servers

Deliverables:

- tune tick rate based on server budget
- add snapshot relevance filtering
- add command send throttling only where it does not hurt replay quality
- add lightweight runtime counters for command rate, snapshot rate, correction size, and replay depth

This phase is where the architecture becomes operationally useful at scale.

## Performance Principles

### Server CPU

Prioritize:

- O(players) movement simulation
- cheap collision queries
- no per-player heavy physics engine work
- no server-side replay

The server should only simulate authoritative state forward.

### Bandwidth

Prioritize:

- compact input commands
- authoritative acks for the local player
- relevance-filtered snapshot fan-out
- lower-frequency updates for irrelevant or distant actors

The client replay buffer should be bounded.

Recommended starting point:

- enough commands for roughly 100 to 200 ms of unacknowledged input

Use a ring buffer. Do not allow unbounded command history growth.

The bandwidth budget should be spent on the local player's correctness first, then on visible nearby actors.

### Client CPU

Prioritize:

- prediction for one player only
- interpolation for everyone else
- minimal per-frame allocation
- ring buffers for pending commands and predicted states

Avoid expensive scene-physics queries in the main local movement path.

## Explicit Non-Goals

This plan does not aim to:

- preserve Unity `CharacterController` as movement authority
- make remote players fully predicted
- make every subsystem own its own input interpretation
- optimize for offline single-player convenience over live multiplayer behavior

## Concrete Code Direction

Server-side files likely involved:

- `/Users/davidhedges/Projects/arena2/server/src/game_loop.rs`
- `/Users/davidhedges/Projects/arena2/server/src/movement.rs`
- `/Users/davidhedges/Projects/arena2/server/src/player_intent.rs`
- `/Users/davidhedges/Projects/arena2/server/src/player_physics.rs`
- `/Users/davidhedges/Projects/arena2/server/src/world_collision.rs`

Client-side files likely to be replaced or heavily refactored:

- `/Users/davidhedges/Projects/arena2/Assets/Scripts/Input/LocalPlayerMotor.cs`
- `/Users/davidhedges/Projects/arena2/Assets/Scripts/Input/MovementNetDriver.cs`
- `/Users/davidhedges/Projects/arena2/Assets/Scripts/Input/ServerReconciliationBridge.cs`
- `/Users/davidhedges/Projects/arena2/Assets/Scripts/Input/SpellInputHandler.cs`
- `/Users/davidhedges/Projects/arena2/Assets/Scripts/Simulation/ClientSimulationState.cs`
- `/Users/davidhedges/Projects/arena2/Assets/Scripts/Presentation/PlayerView.cs`
- `/Users/davidhedges/Projects/arena2/Assets/Scripts/Entity/EntityRegistry.cs`

## Bottom Line

The right move is not to keep tuning the current hybrid. The right move is to replace it with:

- authoritative fixed-tick server movement
- local client prediction with replay
- one command format
- one pure movement function over explicit state
- ordered command processing with bounded replay buffering
- one facing owner
- one input sampling path
- exact client/server collision parity
- cheap remote interpolation
- relevance-aware snapshot fan-out

That is the version that can feel responsive and still survive real multiplayer server load.

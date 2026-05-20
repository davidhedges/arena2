# Spellcasting Responsive Cancel Production Plan - 2026-05-15

Status: Phase 1 implemented with an owner-scoped cast result table. Phase 2 is
partially implemented for local cast-bar prediction/reconciliation and immediate
local spell-hold animation cancellation; broader VFX identity cleanup remains.

## Purpose

Make cast start, cast progress, cast release, and cast cancellation feel
immediate to the local player while remaining server-authoritative and robust
under remote latency, input buffering, reducer reordering, reconnects, and
future spell variety.

The current tactical fix sends an explicit `cancel_active_cast_request` on local
movement intent. That improves the visible symptom, but it does not fully solve
the architecture problem: spell interruption is still partly coupled to a
movement command stream that intentionally runs ahead of the server.

This plan defines the production target.

## Current Facts

- Authoritative movement ticks run every `33ms`.
- Remote movement currently targets an 8-tick input lead.
- 8 movement ticks at `33ms` is about `264ms`.
- Local movement prediction needs some lead to hide latency and packet jitter.
- Cast start and cancel reducers are wall-clock reducer inputs.
- Movement interruption is also discovered by `tick_active_casts()` using
  `PlayerIntent`, which is derived from the authoritative movement tick stream.
- `ActiveCast.started_at` and `ActiveCast.ends_at` are currently server
  timestamps from `begin_active_cast(...)`.
- The current explicit cancel reducer no-ops if the cancel arrives before the
  server has inserted the active cast.
- `ActiveCast` is currently public and primary-keyed by `caster`. That is the
  desired gameplay model: one active cast per caster.

The important conclusion is that a cast cancel must not depend on the 8-tick
movement lead. Movement lead is a movement prediction concern. Cast lifecycle is
a combat action concern.

## Goals

- Show local cast bars immediately when the player starts casting.
- Hide local cast bars immediately when the player cancels or is locally
  interrupted.
- Prevent "I canceled, but the cast still went off" except when the server had
  already completed the cast before the cancel could possibly arrive.
- Preserve server authority for acceptance, resource cost, cooldown, GCD,
  damage, projectile release, channel ticks, impact, block, parry, and fizzle.
- Make reducer ordering safe. Cancel-before-start, duplicate cancel, duplicate
  start, and late release must be deterministic.
- Keep movement prediction lead for remote movement without letting that lead
  delay spell interruption.
- Give combat/VFX/animation systems one durable cast lifecycle identity.
- Add instrumentation so latency and races are visible in development and
  production diagnostics.

## Non-Goals

- Do not make the client authoritative for damage, projectile hits, resources,
  cooldowns, or combat outcome.
- Do not remove movement prediction to solve spellcasting.
- Do not raise the global game tick rate as the first solution.
- Do not couple spell cancellation to Unity animation events.
- Do not add spell-id-specific cancellation branches.
- Do not accept hidden state where the client and server cannot explain why a
  cast completed or canceled.
- Do not support multiple concurrent `ActiveCast` rows per caster.

## Design Principles

- Cast lifecycle is an explicit combat action protocol, not a side effect of
  movement simulation.
- Every accepted or predicted cast has a stable instance identity.
- At most one active cast may exist per caster. Keyed cancellation protects
  against old-input/new-input crossover races; it does not imply concurrent
  server-side casts.
- Client prediction is presentation and intent. Server state is authority.
- Reducers are idempotent where practical.
- Cancellation is durable. A cancel that arrives before the cast row exists
  should still suppress that cast if it refers to the same predicted action.
- Movement can validate or force cancellation, but it is not the only interrupt
  transport.
- Combat timing should be timestamp-based where it matters, not limited to
  33ms phase granularity unless the mechanic explicitly needs fixed-step logic.

## Target Architecture

Introduce a first-class combat action input model for spellcasting.

```text
Client button down
  -> create predicted_cast_id / client_action_seq
  -> show predicted cast bar immediately
  -> send cast_start_request(predicted_cast_id, client_action_seq, snapshot)

Server cast start reducer
  -> validate ability, target, resource, cooldown, mobility, snapshot
  -> reject if a newer/equal cancel exists for that predicted_cast_id/seq
  -> create the caster's single ActiveCast with action_instance_id
  -> record private prediction correlation
  -> emit authoritative COMBAT_CAST / owner ack

Client movement/jump/cancel
  -> hide predicted cast bar immediately
  -> stop local anticipation VFX immediately
  -> send cast_cancel_request(predicted_cast_id, client_action_seq, cancel_reason)

Server cancel reducer
  -> record durable cancel token
  -> if the caster's ActiveCast matches, fizzle and clear it now
  -> if no matching ActiveCast exists, keep the token briefly

Server cast completion
  -> before finishing, check matching cancel token and interrupt facts
  -> revalidate release-time facts that can change during the cast, such as live
     facing for targeted projectiles
  -> release only if the cast is still valid and not canceled
  -> if completion validation fails after COMBAT_CAST, emit COMBAT_FIZZLE before
     clearing ActiveCast so release-bound animation/VFX receive a terminal signal
```

This makes movement lead irrelevant to the explicit cancel path. The 8-tick
buffer can continue to exist for remote locomotion while cast cancel travels as
an immediate combat action input.

## Identity Model

Use distinct names everywhere.

- `client_action_seq`: monotonically increasing per local player combat input.
- `predicted_cast_id`: client-generated id for one locally predicted cast.
- `action_instance_id`: server-generated canonical id for one accepted cast.
- `ability_id`: authored ability row, such as `MAGE_ICICLE`.
- `action_kind` or `spell_kind`: runtime spell kind, such as `ICICLE`.
- `cancel_seq`: optional monotonic counter for cancel inputs if separate from
  `client_action_seq`.

`ActiveCast` remains primary-keyed by `caster`. The production model is one
active cast per caster, not one row per cast id. This keeps the core gameplay
state simple and preserves the existing "already casting" gate.

`ActiveCast` should carry only public-safe authoritative state:

- `caster`
- `action_instance_id`
- `ability_id`
- `kind`
- `started_at`
- `ends_at`
- target/aim data
- charge/channel metadata where needed

Prediction correlation should live outside public `ActiveCast` unless the
project deliberately accepts broadcasting client-private ids. Preferred shape:

- `ActiveCast`: public authoritative state for observers
- `CastPredictionCorrelation`: server-private row keyed by
  `(caster, action_instance_id)` with `predicted_cast_id` and
  `client_action_seq`
- owner-scoped cast ack/reject/cancel events or reducer callbacks that return
  the prediction correlation only to the casting client

The local client should be able to match an authoritative accepted cast back to
its predicted cast without guessing by spell kind. Other subscribers do not need
the client's prediction ids.

## Server State

Add a short-lived server-private table for combat action input ordering.

Suggested table:

```text
PendingCastCancel
  caster
  predicted_cast_id
  client_action_seq
  reason
  received_at
  expires_at
```

Purpose:

- make cancel-before-start safe
- dedupe repeated cancel requests
- provide diagnostics for why a cast was suppressed
- keep reducer ordering deterministic

Suggested cleanup:

- expire after a small window, such as 2 to 5 seconds
- remove only the row keyed by `(caster, predicted_cast_id)` after it cancels or
  suppresses a matching cast

Do not expose this table publicly unless debug tooling needs a gated view.

Suggested correlation table:

```text
CastPredictionCorrelation
  caster
  action_instance_id
  predicted_cast_id
  client_action_seq
  received_at
  expires_at
```

This table is server-private. It exists only to reconcile owner prediction and
to protect against stale cancels/releases. It is not gameplay state.

## Event And Ack Schema

Use separate vocabularies for gameplay events and VFX cue triggers:

- `COMBAT_CAST`, `COMBAT_RELEASE`, `COMBAT_FIZZLE`, and related values are
  authoritative combat event types.
- `SPELL_CAST`, `SPELL_RELEASE`, `SPELL_FIZZLE`, and related values are VFX cue
  trigger names resolved from authoritative combat facts.

Public `CombatEvent` should continue to carry only observer-safe authoritative
fields, such as `action_instance_id`, `action_kind`, `ability_id`, caster,
target, origin, direction, and event type. It should not broadcast
`predicted_cast_id` or `client_action_seq`.

The casting client still needs prediction reconciliation. Add one of these:

- an owner-scoped cast lifecycle event table
- an owner-only reducer callback/ack path if the networking layer supports it
- a private subscription-gated table keyed by owner

That owner-only surface carries `action_instance_id`, `predicted_cast_id`,
`client_action_seq`, and a coarse result. This is the main schema touch point
for downstream UI, VFX, and animation reconciliation.

Current implementation uses `CastActionResult`, a public table subscribed through
the owner-filtered local query plan. This matches the project's existing local
subscription model; it is an owner-scoped reconciliation channel, not a hard
server-enforced read-security boundary. Because of that limitation, the public
row intentionally omits rejection reasons, action kind, and ability id; detailed
failure reasons stay in server logs until a true private/RLS-backed channel
exists.

## Reducer Contract

Replace the current unkeyed cancel reducer with a keyed protocol.

### `cast_start_request`

Inputs:

- `ability_or_spell_id`
- `target_id`
- aim point
- `cast_input_tick`
- authored position/yaw snapshot
- `predicted_cast_id`
- `client_action_seq`

Rules:

- Validate the same gameplay gates as today.
- If a matching `PendingCastCancel` already exists, reject/suppress the cast.
- If the player is already casting a different active cast, reject/suppress.
- If accepted, insert the caster's single `ActiveCast` row with the canonical
  `action_instance_id`.
- Insert private `CastPredictionCorrelation` for the accepted cast.
- Emit public `COMBAT_CAST` with public-safe authoritative fields.
- Send or expose an owner-only cast ack that includes `action_instance_id`,
  `predicted_cast_id`, `client_action_seq`, and coarse result. The implemented
  surface is `CastActionResult`.

`cast_input_tick` is for movement snapshot validation only. It is not the combat
ordering key. `client_action_seq` is the combat ordering/crossover key.

### `cast_cancel_request`

Inputs:

- `predicted_cast_id`
- `client_action_seq`
- `reason`
- `cancel_observed_remaining_ms`

Rules:

- Insert or update `PendingCastCancel`.
- If the caster's single `ActiveCast` matches the cancel correlation, emit
  fizzle/interrupt cleanup and clear it.
- If the matching cancel is received after `ends_at`, accept it only when the
  client observed remaining cast time and the reducer is processed no later than
  `ends_at + 100ms`.
- If no matching active cast exists, keep the pending cancel until expiration.
- If the caster's active cast belongs to a newer or different client action,
  ignore the cancel and keep diagnostics.
- Duplicate cancels are no-ops.

The 100ms window is a pre-end cancel acceptance window, not a post-end completion
delay. Successful casts still complete at `ends_at`; the window only allows a
late-arriving matching cancel to apply when the cancel was authored while the
client still showed remaining cast time.

### `release_cast_request`

Inputs:

- `predicted_cast_id`
- `client_action_seq`
- `spell_id`

Rules:

- Release only the caster's active cast when the private correlation matches.
- If a matching cancel exists, cancel wins.
- If the server has no matching active cast, no-op with diagnostics.
- Do not release a newer cast by relying only on spell kind. The hazard here is
  old release/cancel input crossing over with a later cast, not two active cast
  rows coexisting.

## Cast Completion Contract

Before `tick_active_casts()` finishes any cast:

- check for a matching pending cancel
- check disabling statuses
- check dead/missing caster
- check movement fallback validation
- check channel-specific termination rules

If any terminal interruption is true, emit one authoritative terminal cleanup
event and clear the cast exactly once.

The explicit cancel path should normally win before movement fallback is needed.
Movement fallback remains important for:

- packet loss
- malicious clients
- alternate interrupt sources
- server-authored movement changes
- airborne/grounded authority

## Local Client Contract

On local cast start:

- generate `predicted_cast_id`
- increment `client_action_seq`
- show cast bar immediately
- start local anticipation VFX immediately
- send `cast_start_request`

On local movement/jump/manual cancel while a predicted or authoritative local
cast is visible:

- hide cast bar immediately
- stop local anticipation VFX immediately
- send keyed `cast_cancel_request`
- mark the predicted cast id as locally suppressed until authoritative cleanup
  arrives or the suppression expires

On authoritative cast ack or accept:

- reconcile predicted cast by `predicted_cast_id`
- update start/end times to server values if needed
- do not re-show a locally suppressed cast

On authoritative fizzle/delete:

- clear predicted and authoritative UI state
- stop all cast-bound VFX and animation holds

On authoritative release/impact:

- only play release/impact if the local predicted cast was not already
  authoritatively canceled
- match by `action_instance_id`, not spell kind alone

## Cast Bar Timing

Use two timelines:

- predicted timeline for immediate local feel
- authoritative timeline for final reconciliation

The cast bar should appear on the input frame. It should not wait for:

- reducer acknowledgement
- `ActiveCast` table insert
- server tick
- movement command acknowledgement

When the authoritative cast ack or `ActiveCast` arrives, the client can correct
the bar with a small smoothing policy:

- if the displayed progress differs by `30ms` or less, snap silently
- if the difference is greater than `30ms` and less than or equal to `150ms`,
  blend fill progress over `100ms`
- if the difference is greater than `150ms`, snap to authority and log a
  diagnostic sample
- if the server rejects the cast, hide immediately and show rejection feedback
  only where appropriate

Do not let the local player see the bar reappear after they canceled unless the
server explicitly reports that the cancel was too late and the spell completed.

## Movement Lead Policy

Keep these concerns separate:

- movement lead: how far ahead the client authors locomotion commands
- combat action input: immediate semantic events such as cast start/cancel/release
- action snapshot validation: server sanity check for position/yaw at cast start

The 8-tick remote movement lead should not be the production cancellation path.
It is acceptable for movement-derived interruption to lag behind local input
because it is only a fallback. Explicit cancel should be immediate.

Future tuning options:

- keep remote movement lead at 8 for now
- collect metrics for actual pending command depth and correction size
- consider adaptive remote lead based on RTT/jitter instead of one fixed value
- reduce remote lead only if movement quality remains acceptable

## Tick Rate Policy

Do not raise the global game tick rate as the first fix.

Raising the global tick from 30Hz to 60Hz would reduce movement tick granularity
from `33ms` to `16.7ms`, but it would also increase the cost of every fixed
simulation phase. It still would not solve cancel-before-start reducer ordering
by itself.

Preferred production model:

- keep movement fixed tick for physics
- process cast start/cancel/release reducers immediately
- use timestamps for cast ends
- accept matching late-arriving pre-end cancels for up to `100ms` after `ends_at`
- let `game_tick` resolve due combat work, but allow reducer-time cancellation
  and cleanup where safe

Without per-cast scheduled wakeups, normal cast completion still lands on the
first `game_tick` after `ends_at`. Timestamped cast ends improve correctness and
progress display, but they do not remove fixed-tick completion latency by
themselves. If that latency becomes visible, add per-cast scheduled completion
or a combat-specific wakeup before raising the global movement tick rate.

Only consider a higher tick rate after profiling shows that combat feel still
needs it and server budget can support it.

## Channel And Movement Delivery Policy

Channels use the same keyed start/cancel identity, but cancellation composes
with channel-specific teardown. For Electrocute-style channels, an explicit
matching cancel should call the channel stop path, emit the channel's terminal
cleanup event, clear the active cast, and apply the channel's existing resource,
cooldown, and tick semantics. Cancel-mid-channel is not the same as
cancel-before-completion for a normal cast-time spell.

Movement delivery casts are not canceled by the normal spell cancel protocol in
Phase 1. The client should not send `cast_cancel_request` for movement delivery
unless that movement ability explicitly opts into the spell cast lifecycle. The
existing movement delivery fizzle/cleanup path remains authoritative for those
actions.

## Animation And VFX Contract

Cast-bound animation and VFX must bind to the cast instance identity.

Required behavior:

- cast hand glow starts from local prediction for the local player
- authoritative cast start confirms or replaces the predicted visual
- cancel/reject/fizzle stops the hand glow exactly once
- animation cast hold exits immediately on local cancel for the local player
- remote players follow authoritative events
- release-bound projectile VFX never spawns from a canceled cast

This requires VFX/animation state to track:

- `predicted_cast_id`
- `action_instance_id` once known
- `ability_id`
- `spell_kind`
- terminal state

Spell kind alone is not sufficient.

## Diagnostics

Add counters and structured logs for the full lifecycle.

Server counters:

- cast start requests received
- cast starts accepted
- cast starts rejected by reason
- cancel requests received
- cancel-before-start suppressions
- cancel-after-active interruptions
- cancel-too-late completions
- duplicate cancels ignored
- active casts completed
- active casts fizzled by movement fallback
- active casts fizzled by explicit cancel

Client counters:

- predicted cast bars shown
- predicted cast bars suppressed locally
- authoritative accepts matched to prediction
- authoritative accepts without prediction
- authoritative rejects after prediction
- cast bars re-shown after local suppression
- local cancel to authoritative fizzle latency
- local cast start to authoritative accept latency

Debug overlay additions:

- current predicted cast id
- authoritative action instance id
- local cast bar state
- local suppression state
- last cancel reason
- cancel round-trip timing

## Edge Cases To Handle

- cancel arrives before cast start reducer
- cancel arrives after cast already completed
- cast accept arrives after local cancel
- release arrives after cancel
- duplicate cast start request
- duplicate cancel request
- movement cancel for old cast after a newer cast started
- player dies while casting
- player is stunned/silenced while casting
- player disconnects/reconnects while casting
- cast is rejected after local prediction
- channel is canceled
- release-cast spell is canceled
- movement delivery active casts are not canceled by normal spell cancel logic
- VFX starts locally but authoritative cast never appears

Every case should have a deterministic server outcome and an explicit client UI
cleanup path.

Reconnect policy:

- `client_action_seq` is scoped by client session.
- reconnect creates a new client combat session id and resets local prediction
  state
- server-private pending cancel/correlation rows expire normally and may also be
  purged on disconnect if the networking layer exposes a reliable lifecycle hook
- public `ActiveCast` remains authoritative after reconnect; the client
  reconciles from server state without reusing old predicted ids

## Phase 1: Harden The Tactical Fix

Purpose: remove the worst race without changing every spell path.

Tasks:

- Add `predicted_cast_id` and `client_action_seq` to client cast requests.
- Add private cast prediction correlation rather than broadcasting those fields
  on public `ActiveCast`.
- Replace unkeyed `cancel_active_cast_request()` with keyed cancel.
- Add `PendingCastCancel` for cancel-before-start.
- Update release-cast requests to include the same identity.
- Add owner-only cast ack/reject/cancel correlation, or an equivalent private
  reconciliation path.
- Keep movement fallback cancellation in `tick_active_casts()`.
- Add focused diagnostics for cancel-before-start and cancel-too-late.

Acceptance:

- If local movement sends cancel before the server inserts `ActiveCast`, that
  cast does not later complete.
- Cancel does not accidentally cancel a newer cast of the same spell.
- Duplicate cancels are harmless.

## Phase 2: Full Local Prediction Reconciliation

Purpose: make local cast UI/VFX smooth without hiding authoritative truth.

Tasks:

- Centralize local predicted cast state in `LocalCombatState`.
- Track prediction by `predicted_cast_id`, not spell kind.
- Reconcile authoritative cast acks and `ActiveCast` rows to predicted casts.
- Add local suppression expiry for cases where no authoritative response arrives.
- Add cast start rejection handling.
- Bind local anticipation VFX to predicted cast identity.
- Bind authoritative VFX cleanup to action instance identity.

Current implementation note:

- `SpellCastPresentationController` receives the same `CastActionToken` used by
  the local cast bar.
- Local movement/jump cancellation sends that token to the spell presentation
  controller on the same frame that the cast bar is suppressed.
- The local spell hold is canceled immediately and late `ActiveCast` inserts for
  the locally suppressed spell kind do not restart the hold before server
  cleanup arrives.
- Owner-scoped `CastActionResult` rows are also routed into local spell
  presentation. Accepted rows map `action_instance_id` to the local prediction
  token without putting prediction ids on public `ActiveCast`, so an old
  authoritative cast row cannot consume a newer same-kind prediction.
- Locally suppressed action instance ids expire after `10s` in the presentation
  controller so callback ordering differences between result rows and
  `ActiveCast` deletes cannot leak suppression memory.
- `stale_token` results clear matching prediction bookkeeping but do not locally
  play a cancel animation; the server is explicitly saying that token did not
  cancel the authoritative cast.

Corrective architecture status:

- Implemented `LocalSpellPresentationStateMachine` as a pure transition engine
  with no Unity, animator, `MonoBehaviour`, SpacetimeDB row, or network
  dependencies. It accepts lifecycle inputs and returns presentation commands
  such as `StartHold`, `RequestCancel`, `RequestRelease`, and `None`.
- `SpellCastPresentationController` is now a thin adapter: it translates Unity
  and SpacetimeDB callbacks into state-machine inputs, keeps the short-lived
  action-id suppression TTL filter at the adapter boundary, resolves facing
  targets, and dispatches returned commands to `PlayerEntity.RequestCombatAnimation`.
- Current states are `Idle`, `PredictedHold`, `PendingCorrelation`,
  `AuthoritativeHold`, `Released`, and `Terminal`. `PendingCorrelation` covers
  the case where an `ActiveCast` row arrives before its owner-scoped
  `CastActionResult`.
- Short-lived action-id suppression is intentionally not a state. It remains a
  small TTL event filter on the adapter.
- `_locallySuppressedSpellActionId` remains only as a narrow fallback for the
  pre-result race where the local player has canceled, but the old `ActiveCast`
  insert arrives before the owner-scoped result row can identify its
  `action_instance_id`. Once any terminal/reject result arrives, the adapter
  clears the kind fallback and relies on action-instance suppression.
- Target inputs: local predict, local cancel, `CastActionResult`,
  `ActiveCast` insert/update/delete, combat release, scheduled release, and
  timeout.
- The transition engine is the single place that decides whether to start hold
  animation, bind prediction to authority, play cancel, play release, or clear
  lifecycle state.
- Phase 5 now has focused edit-mode coverage for the pure transition function:
  same-kind cancel/recast crossover, predicted happy path release/delete,
  `cancel_too_late`, and `stale_token`.
- Server policy coverage protects the live-facing projectile completion rule
  and the active-cast interrupt terminal policy.
- A feature-gated reducer-level harness,
  `run_spellcasting_terminal_harness`, now exercises the shared terminal path
  in a real SpacetimeDB reducer context: Icicle completion rejected by
  live-facing validation must fizzle exactly once and spawn no projectile,
  normal interrupt must fizzle exactly once, and Electrocute interrupt must use
  channel-stop cleanup. The reducer is compiled only with the
  `spellcasting_terminal_harness` Cargo feature so normal deployments do not
  expose a callable test reducer.

Acceptance:

- Cast bar appears on the input frame.
- Cast bar disappears on the cancel frame.
- Authoritative accept does not resurrect a locally canceled bar.
- Hand glow never persists after cancel, reject, fizzle, release, or timeout.

## Phase 3: Server Lifecycle Cleanup

Purpose: make all spell categories share the same cancellation semantics.

Tasks:

- Audit normal cast-time spells, channels, release-cast spells, instant beams,
  projectile spells, area spells, and movement delivery casts.
- Keep cast-time projectile completion on live-facing validation; start-time
  validation may use the accepted input snapshot, but release-time validation
  must reflect where the caster is actually facing when the spell completes.
- Route status/disabling interrupts through the same fizzle/channel-stop helper
  when the server still has enough caster state to author a terminal event.
- Treat any failure after `begin_active_cast()` / `COMBAT_CAST` as a terminal
  presentation event: emit fizzle or channel stop before clearing the active row.
- Move cast lifecycle checks into shared helpers.
- Ensure terminal events are emitted exactly once.
- Ensure resource/cooldown/GCD timing is intentional for canceled casts.
- Add tests around finish-vs-cancel ordering.
- Keep terminal cleanup centralized through `ActiveCastTerminalOutcome` and
  `apply_active_cast_terminal_outcome`; direct `clear_active_cast` calls are
  reserved for silent invalid/stale state cleanup or successful completion.

Acceptance:

- All castable spells use the same start/cancel/release identity protocol unless
  explicitly documented otherwise.
- Server logs can explain every active cast terminal state.

## Phase 4: Adaptive Movement Lead Review

Purpose: tune movement prediction after combat cancellation is decoupled.

Tasks:

- Measure remote RTT, jitter, pending command depth, correction distance, and
  resend/drop behavior.
- Replace fixed `RemoteDesiredServerInputLeadTicks = 8` with adaptive lead if
  metrics justify it.
- Test lead values under local, LAN, normal remote, high-latency, and jittery
  conditions.
- Keep combat cancellation on the combat action protocol regardless of lead.

Acceptance:

- Movement lead is selected for movement quality, not spell cancellation.
- Reducing lead is a tuning choice, not a correctness requirement.

## Phase 5: Test Harness And Regression Suite

Purpose: prevent responsiveness regressions.

Tests:

- cast start accepted and echoed with predicted id
- cancel-before-start suppresses the future cast
- cancel-after-active fizzles active cast
- cancel-too-late allows completed cast and is visible in coarse reconciliation
  plus server diagnostics
- duplicate cancel no-ops
- old cancel does not cancel newer cast
- release after cancel no-ops
- movement fallback still cancels if explicit cancel is missing
- local UI suppresses immediately and reconciles correctly
- VFX cleanup runs on reject, cancel, fizzle, release, and timeout
- public `ActiveCast` does not expose prediction ids
- old cancel/release input cannot affect a later single active cast for the
  same caster
- reducer-level terminal harness covers live-facing completion failure,
  non-channel interrupt fizzle, channel interrupt stop, and no projectile on
  failed completion
- CI compiles the harness feature in
  `.github/workflows/server-spellcasting-validation.yml` with
  `cargo test --manifest-path server/Cargo.toml --features spellcasting_terminal_harness spells::casting`
  and a harness-enabled WASM build. Any live SpacetimeDB smoke-test job should
  publish a harness-enabled module before invoking
  `run_spellcasting_terminal_harness`.
- Local live smoke command:
  `ops/run-spellcasting-terminal-harness.sh`. It builds the server WASM with
  `spellcasting_terminal_harness`, publishes a temporary
  `arena-spellcasting-terminal-harness` database to the configured
  SpacetimeDB server, invokes the harness reducer, then deletes the temporary
  database unless `ARENA_HARNESS_KEEP_DATABASE=1`.

TODO:

- Finish live harness execution. Current local SpacetimeDB CLI `2.1.0` panics
  during `publish`/`server ping` on this macOS environment before the reducer
  can run (`system-configuration` / `Attempted to create a NULL object`). Upgrade
  or reinstall the CLI, or run the script on a working Linux/CI SpacetimeDB
  environment, then record the first successful
  `ops/run-spellcasting-terminal-harness.sh` invocation here.

Manual scenarios:

- low latency local server
- remote endpoint with normal latency
- simulated 150ms RTT
- simulated jitter/loss
- spam movement at the end of Icicle cast
- cancel and immediately cast again
- channel cancel
- release-cast cancel

## Rollout Strategy

Recommended order:

1. Add ids and pending cancel table without changing user-facing behavior.
2. Send keyed cancel from the client while keeping old fallback temporarily.
3. Reconcile UI/VFX by predicted id.
4. Convert release-cast and channel paths.
5. Remove unkeyed cancel reducer once all callers are migrated.
6. Tune movement lead only after metrics show the combat protocol is stable.

This keeps the game playable while replacing the weak lifecycle contract.

## Production Acceptance Criteria

- Local cast bar appears within one rendered frame of input.
- Local cast bar disappears within one rendered frame of cancel input.
- Explicit cancel does not wait for movement command lead.
- Cancel-before-start cannot later complete the same cast.
- Cancels are keyed and cannot accidentally cancel a newer cast.
- Server can report whether a cancel was applied, suppressed a pending cast, was
  duplicate, was stale, or was too late.
- Cast-bound VFX and animation holds clean up through the same identity model.
- Movement fallback still protects authority if explicit cancel is missing.
- Remote movement prediction quality can be tuned independently from
  spellcasting responsiveness.
- At `150ms` simulated RTT with normal jitter, cancel-too-late outcomes are
  explainable by server completion ordering and tracked as a metric. Initial
  target budget: less than `1%` of cast attempts canceled before the local bar
  reaches `95%` progress.

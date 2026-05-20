# Cast Mobility Race Diagnosis & Production Fix Proposals - 2026-05-16

## Archive Status

Archived after implementation and playtest on 2026-05-16. The normal cast-time spell path is resolved for the reported `ICICLE` issues:

- Premature movement cancel no longer leaves stale movement able to fizzle the next stationary cast.
- The cast bar no longer rewinds when the delayed authoritative `ActiveCast` arrives.
- Pending cast validation is tick-aligned, but accepted pending casts keep the original request receipt timestamp as `ActiveCast.started_at`, so server confirmation does not extend the predicted cast-bar duration.
- `ActiveCast` carries `predicted_cast_id` and `client_action_seq`, allowing local HUD and spell presentation to reconcile by exact action token rather than spell kind or ordering.

This document remains as historical diagnosis and rationale. The implemented contract for normal cast-time spells is: predict immediately, validate on the authoritative movement tick, preserve the original accepted action start time, and reconcile authoritative state by exact prediction token.

## Historical TL;DR

A stationary spell (e.g. `ICICLE`) cancelled by movement, then immediately recast with **no further input**, could server-fizzle ~200ms after the recast began. The fizzle was logged as `terminal=mobility_fizzle ... forward=1.0000` even though the player's fingers were off the keyboard.

Root cause was **architectural**, not a one-line bug: the cast state machine read the shared `player_intent` row at non-deterministic moments (reducer scheduling order was not aligned to `game_tick`), and re-evaluated raw intent every tick during the cast lifetime. Stale movement inputs - in-flight commands, fallback-intent inheritance, or commands that beat the `cast_request` to the server queue - could flip `intent.forward` back to 1.0 after acceptance and fizzle the cast.

This document captures evidence, surveys candidate fixes (including a tempting "epoch snapshot" approach that turned out to miss the same race), and records the production path that was implemented: compare authored input ticks, not process-time state.

## Implementation Status - 2026-05-16

Phase 1 shipped and playtested successfully. Phase 2, tick-aligned cast acceptance for normal non-release spell requests, is now implemented.

Implemented:

- `ActiveCast` now stores `cast_authored_input_tick`.
- `PlayerState` now tracks `last_voluntary_move_input_tick`.
- `tick_player` updates `last_voluntary_move_input_tick` only when a real client-authored movement command is popped and accepted as voluntary movement. Fallback inherited intent does not advance it.
- `tick_active_casts` no longer uses raw `player_intent.forward/strafe/jump` for the lifetime mobility check of normal active casts. It uses:

```rust
state.last_voluntary_move_input_tick > active_cast.cast_authored_input_tick
```

- Grounding remains a separate invariant: becoming airborne during a grounded-stationary cast still fizzles.
- `ELECTROCUTE` channel handling and movement-delivery active casts remain outside this new lifetime path.
- `SpellInputHandler.SendCastRequest` stamps casts with the newest locally-authored pending movement tick when available.
- The client locally rejects normal cast-time, non-release spells while move or jump input is active, preventing predicted cast-bar flicker while the player is actively moving.
- Normal non-release `cast_request` reducers now enqueue `PendingCastRequest` instead of evaluating movement synchronously.
- `game_tick` resolves pending casts after `tick_player` has committed movement, and only after the caster's authoritative `last_processed_tick >= cast_input_tick`.
- Accepted pending casts use the original pending request receipt timestamp as `ActiveCast.started_at`; tick-aligned validation must not extend the cast duration from the player's predicted bar.
- Requests made while already casting, on GCD, on spell cooldown, dead, or disabled are still rejected at request time so pending casts cannot be used as an input buffer to bypass those gates.
- `ActiveCast` echoes `predicted_cast_id` and `client_action_seq`, giving the local client an exact correlation key for the authoritative cast row.
- Local cast-bar and spell-presentation reconciliation preserve the predicted local visual start when the matching authoritative `ActiveCast` arrives later, while still adopting the authoritative server end time.
- Release/channel starts remain immediate because `INSTANT_BEAM` and `ELECTROCUTE` have key-up/release semantics rather than normal cast-time acceptance semantics.

Validation:

- Manual playtest: canceling the first `ICICLE` by movement, releasing movement, and immediately recasting no longer causes the second cast to be server-fizzled by stale movement.
- Manual playtest: moving after the recast still cancels the cast.
- Server tests cover the authored-tick boundary, pre-cast movement processed late, post-cast movement, fallback inherited intent, and airborne fizzle behavior.
- Client tests cover exact-token `ActiveCast` confirmation for both HUD cast-bar timing and spell-presentation timing, including the delayed-authoritative-start case that caused the visible rewind.

---

## Repro & Evidence

### Player Action Sequence

1. Cast a `GROUNDED_STATIONARY` cast-time spell (`ICICLE`, 1.0s cast).
2. Press `W` to deliberately cancel the in-flight cast.
3. Release **all** keys (fingers off keyboard).
4. Press `C` to recast.

**Observed**: second cast is accepted by the server, then fizzled ~200ms later.

### Server Log (Filtered To Relevant Events)

```text
05:16:16.819167  cast_request   [SPELL_CAST] caster=c20009d2 spell=ICICLE
                                cleared_preaccepted_movement_commands count=2
05:16:17.487184  cast_request   [SPELL_CAST] caster=c20009d2 spell=ICICLE
                                cleared_preaccepted_movement_commands count=1
05:16:17.694241  game_tick      [SPELL_CAST] caster=c20009d2 spell=ICICLE
                                terminal=mobility_fizzle
                                cast_id=...:1778908577484871
                                started_at_micros=1778908577484871
                                grounded=true cast_tick=0
                                snapshot_tick=964 intent_tick=964
                                forward=1.0000 strafe=0.0000 jump=false
```

The cast at `started_at_micros=1778908577484871` matches the second `cast_request` (17.487s). It fizzles 207ms later (~6 game ticks @ 30Hz). At fizzle time `intent.forward = 1.0` even though the player has released all keys.

### Client Trace (Cast 2 Only)

```text
12:16:17  C -> slot_1_8 ability=WARRIOR_ICICLE
12:16:17  spell dispatch sending cast request for ICICLE
12:16:17  sending CastRequest for ICICLE
12:16:17  spell presentation command hold kind=ICICLE authority=Predicted
12:16:17  predicted local cast hold for ICICLE
12:16:17  cast action result result=accepted
12:16:17  active cast insert kind=ICICLE
12:16:17  authoritative COMBAT_CAST received for local caster: ICICLE source=SPELL
12:16:17  active cast delete kind=ICICLE
12:16:17  spell presentation active cast delete kind=ICICLE
12:16:17  spell presentation command cancel kind=ICICLE
12:16:17  authoritative COMBAT_FIZZLE received
```

Note the absence of any `local movement cancel dispatch` line for cast 2 - the client did not initiate the cancel. The server fizzled it.

---

## Why This Happens - Anatomy Of The Race

### Relevant Code Paths

**Mobility gate at accept** ([casting.rs](../server/src/spells/casting.rs)):

```rust
let movement_intent = ctx.db.player_intent().identity().find(caster);
if violates_cast_mobility_requirement(spell_kind, caster_state.grounded, movement_intent.as_ref()) {
    // record REJECTED + log "mobility_requirement"
    return Ok(());
}
```

**Mobility gate during cast** re-runs the same predicate every game tick in `tick_active_casts`.

**Predicate**:

```rust
fn violates_cast_mobility_requirement(spell_kind, grounded, intent) -> bool {
    // GROUNDED_STATIONARY + (!grounded OR has_movement_intent(intent))
}

fn has_movement_intent(intent: &PlayerIntent) -> bool {
    intent.jump || intent.forward.abs() > 0.0001 || intent.strafe.abs() > 0.0001
}
```

**Preaccepted-command sweep** - at accept, deletes `player_command` rows with `received_at <= accepted_at`. Does **not** reset `player_intent`. Does **not** affect commands received after `accepted_at`.

**Intent fallback** - when `tick_player` finds no `player_command` for the next input tick, it inherits the previous tick's `intent.forward/strafe/yaw`. So a single stale movement value can persist for many ticks.

**Tick phase order** - `tick_active_casts` runs at the **start** of `game_tick`, **before** `tick_player`. So the mobility gate during cast reads `player_intent` written by the previous tick's `tick_player`. Cast acceptance, on the other hand, runs as a `cast_request` reducer at arbitrary wall-clock time, reading whichever `player_intent` value was last committed by the most recent `game_tick`.

### Three Concrete Sources Of Stale `forward=1.0`

1. **Prediction lead delivers W=1.0 commands the server hasn't processed yet.**

   `DesiredServerInputLeadTicks` is greater than zero. When the player was holding W during cast 1's cancel, the client had already sampled and transmitted W=1.0 commands tagged with future input ticks. Some land on the server before `cast_request` and get cleared; others land after, slip past the preaccepted sweep, and get popped by `tick_player` later, after cast 2 has been accepted.

2. **Fallback inheritance.**

   Once `intent.forward = 1.0` is written by any tick, the fallback at later ticks (when no command arrives for that tick) inherits 1.0 indefinitely. The cast lifetime check sees forward=1.0 on a fallback tick and fizzles, even though no W command exists for that tick.

3. **Reducer ordering vs. tick boundary.**

   `cast_request` reads `player_intent` at an arbitrary point between game ticks. `send_movement_intent` writes to `player_command` (not `player_intent`) but the queue ordering determines what `tick_player` will commit at the next `game_tick`. Acceptance and the lifetime check operate against different versions of the same state.

### Why `count=1` In The Cast 2 Log

By cast 2 acceptance, only one `player_command` remained with `received_at <= accepted_at`. The bulk of the W=1.0 commands either:

- Had already been popped by `tick_player` and committed into `player_intent` (so they no longer block acceptance - `intent.forward` happens to read 0 because the latest applied command was W=0)
- Or arrived on the server **after** `cast_request` ran, escaping the sweep entirely

The cast is accepted because the snapshot says 0; it fizzles because a few ticks later a stale W=1.0 command (or fallback inheritance) flips intent back to 1.0.

---

## Why The Existing Helpers Don't Cover This

The `clear_preaccepted_movement_commands_for_stationary_cast` helper is the closest thing to mitigation. It:

- Drops queued movement commands at accept time.
- Does not reset the `player_intent` fallback (still inherits prior frame).
- Does not advance the input cursor (later-arriving stale commands not blocked).
- Does not catch commands received after `accepted_at`.
- Does not isolate the cast lifetime check from raw intent.

It papers over the race in the common case but leaves the architectural sharing of `player_intent` between movement and casts intact.

---

## Candidate Fixes

Each section: what it does, complexity, what it fixes, what it leaves broken.

### Option A - Intent Snapshot ("Fast Lane")

Cache the player's `intent.forward / strafe / yaw / jump` on the `active_cast` row at `begin_active_cast`. In `tick_active_casts`, compare current intent to the snapshot; only fizzle if it changed in a movement-implying direction.

- **Complexity**: low (one new column, one comparison)
- **Fixes**: in-flight stale commands flipping intent immediately after accept
- **Leaves broken**:
  - Fallback inheritance can keep forward=1.0 across ticks without "changing"
  - Does not fix the accept-time race
  - Does not generalize to other invariants (airborne, hard-CC, dodge)
  - Adds a denormalized field that has to stay in sync with whatever the movement loop does

### Option B - Grace Ticks

In `tick_active_casts`, ignore the mobility check for the first N (e.g. 2) ticks after `started_at`.

- **Complexity**: trivial
- **Fixes**: this specific 207ms race window
- **Leaves broken**: everything else; players will find inputs that exploit the grace window; does not address the architecture.

### Option C - Voluntary-Movement Epoch Snapshot (Insufficient)

First instinct: use the existing `player_state.voluntary_move_epoch` ([game_loop.rs:850-869](../server/src/game_loop.rs#L850-L869)) - a monotonic counter that increments once per accepted voluntary movement input. Snapshot it on `active_cast` at `begin_active_cast`, fizzle in `tick_active_casts` iff `state.voluntary_move_epoch > active_cast.voluntary_move_epoch_at_start`.

**This does not actually fix the bug.** A W=1.0 command authored *before* the recast but whose `send_movement_intent` reducer runs on the server *after* `cast_request` will:

- Slip past `clear_preaccepted_movement_commands_for_stationary_cast` (because `received_at > accepted_at`)
- Get popped by `tick_player` a few ticks later
- Increment `voluntary_move_epoch`
- Trip the lifetime check and fizzle the cast

The epoch is incremented at *process time*, not *authoring time*. It correctly distinguishes "fallback inheritance" from "real movement input applied," but it cannot distinguish "pre-cast movement processed late" from "post-cast movement." For a PvP game with prediction lead, that distinction is exactly what matters.

Keep this option in the document for context, but it is not the right fix on its own.

### Option C' - Authored-Input-Tick Comparison (Implemented)

The right invariant is **authoring-time**, not process-time: a stationary cast should be interrupted only by voluntary movement input the client *authored after* it authored the cast.

The client already authors movement commands tagged with monotonic `input_tick` values (the buffer's `_nextInputTick`). The cast already carries a tick in its request payload. Plumb a matching reference through and compare:

1. **Client**: in [SpellInputHandler.SendCastRequest](../Assets/Arena/Runtime/Input/SpellInputHandler.cs), set the cast's authoring reference to `_commandHistory.NewestPendingTick` (the latest input_tick the client has authored at the moment of `SendCastRequest`). Today the client uses `stateProvider.LastProcessedTick` (the *server's* last-acked tick) which is the wrong reference - it's older than the in-flight movement commands the client has already authored.
2. **Server**: store the authored tick on `active_cast` (e.g. `cast_authored_input_tick: u32`). Track `last_voluntary_move_input_tick: u32` per player, updated inside `tick_player` whenever an accepted command increments the epoch (same predicate as `sync_player_voluntary_move_epoch`).
3. **Lifetime check** in `tick_active_casts`:

```rust
if state.last_voluntary_move_input_tick > active_cast.cast_authored_input_tick {
    // mobility_fizzle
}
```

Why this works where epoch alone does not:

- A stale W=1.0 command authored before the recast has `input_tick <= cast_authored_input_tick`. When `tick_player` later pops it, `last_voluntary_move_input_tick` advances to that command's tick - but it is still `<= cast_authored_input_tick`, so the check does not fire.
- A genuinely new W=1.0 command authored after the cast has `input_tick > cast_authored_input_tick`. It trips the check. Correct fizzle.
- Fallback inheritance does not advance `last_voluntary_move_input_tick` (no input was processed). No spurious fizzle.
- Reducer ordering does not matter - the comparison is between two values that were authored by the client, not by the server's scheduler.

Keep the `grounded` check separate - it is a distinct invariant.

- **Complexity**: medium (one new column on `active_cast`, one new column or field on `player_state` for the latest voluntary-movement input tick, a tiny client plumbing change, a `tick_player` write, a reducer change)
- **Fixes**: lifetime fizzle race entirely, including the in-flight-stale-command case Option C misses
- **Generalizes**: any future "should X interrupt this cast?" question can use the same authored-tick boundary
- **Leaves**: accept-time race (smaller, may still want Option D - see below)

### Option D - Tick-Aligned Cast Acceptance (Implemented For Normal Spell Starts)

Restructure `cast_request` so it does **not** evaluate state synchronously. Instead:

1. `cast_request` reducer enqueues a `pending_cast` row tagged with the client's `cast_input_tick`, token, and authoring snapshot.
2. Inside `game_tick`, add a new phase `resolve_pending_casts` that runs **after** `tick_player` has committed this tick's movement. It evaluates each pending cast against deterministic state, then calls `begin_active_cast` or records a rejection.

- **Complexity**: medium-high (new table, new phase, reducer semantics change, client must wait one game tick for acceptance ack instead of best-effort same-frame)
- **Fixes**: accept-time race entirely; eliminates need for `clear_preaccepted_movement_commands_for_stationary_cast`
- **Generalizes**: anything else that wants tick-deterministic input evaluation (cancel, release, charge release) plugs into the same pattern
- **Cost**: at least one tick of authoritative accept latency; if the client is ahead by prediction lead, the pending row waits until the server has processed through `cast_input_tick`

---

## Recommendation Outcome: Option C' Shipped; Option D Added For Normal Spell Starts

### Why Option C' Is The Load-Bearing Fix

The bug class is not "intent flips spuriously" - it is "the cast state machine asks a process-time question (`is intent.forward currently 1.0?`) when it should be asking an authoring-time question (`did the client author voluntary movement after authoring this cast?`)." Process-time is poisoned by reducer ordering, prediction lead, and fallback inheritance. Authoring-time is owned end-to-end by the client's monotonic input_tick stream and is immune to all three.

Option C' answers the authoring-time question directly:

- In-flight stale commands cannot trip it - their input_tick is `<= cast_authored_input_tick` by construction
- Fallback intent inheritance cannot trip it - no input was processed, so `last_voluntary_move_input_tick` does not advance
- Reducer ordering cannot trip it - both sides of the comparison are client-authored values that the server only ever observes through ordered input_tick advances

Option A (intent snapshot) and Option C (epoch snapshot) both snapshot a *process-time* value and compare against another *process-time* value. They reduce the bug surface but cannot eliminate it as long as in-flight stale commands can advance the compared value after `begin_active_cast`. Option B (grace ticks) is symptom suppression. Option D alone fixes accept but leaves the lifetime check fragile.

### Why Option D Was Added After Phase 1

Option D (tick-aligned acceptance) is a genuine improvement - it eliminates the accept-time race and lets us avoid relying on preaccepted-command sweeps. It was not load-bearing after Option C', but it closes the remaining deterministic acceptance gap:

- The accept-time check is one reducer call; the lifetime check runs every tick for as long as the cast is alive.
- The accept-time check protects against the relatively rare case "player is actively moving at the moment of cast." The lifetime check protects against the much more common "stale movement reached intent late."
- A client-side movement gate in `CanAttemptCast` mostly closes the accept race in practice (the request never gets sent), without requiring a server refactor.

The implemented version resolves normal non-release spell starts through `PendingCastRequest`. Release/channel starts remain immediate until they get a paired pending-release design, because delaying `INSTANT_BEAM` / `ELECTROCUTE` starts without queuing early releases would drop quick key-up input.

### Why Also A Client Gate

Added `_input.Move.sqrMagnitude > 0.0001` and jump input gating to `CanAttemptCast` for normal cast-time, non-release spells. This is not load-bearing for correctness - the server remains authoritative - but it:

- Kills predicted-cast-bar flicker when the player is moving
- Gives instant local feedback
- Keeps the prediction model and server model in agreement, reducing correction snaps

### Defense Against Likely Objections

> "The fast lane (Option A) is one column and one if-statement. Why not just ship that?"

It fixes the specific symptom of this report and creates a maintenance trap. Every new mobility-adjacent feature (sliding, knockback-during-cast, midair charge, special-movement handoffs) will have to update the snapshot semantics or risk drifting. The authored-input-tick primitive in C' is end-to-end client-driven and answers the right question for free.

> "Why not just snapshot the epoch (Option C) - it's already in the codebase?"

Epoch is incremented at process time, not authoring time. A stale W=1.0 command authored before the recast but whose `send_movement_intent` reducer arrives after `cast_request` slips past `clear_preaccepted_movement_commands_for_stationary_cast` (received_at > accepted_at), gets popped by `tick_player` after the cast started, and increments the epoch - tripping the lifetime check on a movement input the player authored *before* casting. Option C is strictly better than today and strictly worse than C'. See the Option C section above for the worked case.

> "Option D adds ~33ms of accept latency. This is a PvP game."

In practice the added latency is bounded by the next game tick boundary, and is fully predictable. The current "best effort same-frame" acceptance is already non-deterministic from the player's perspective - sometimes it lands in the same tick, sometimes in the next, depending on reducer ordering. Tick-aligned acceptance trades a tiny worst-case latency increase for **deterministic feel**. PvP players notice inconsistent timing more than slightly-later-but-uniform timing. Pair it with a confident client-side predicted cast bar to mask the round-trip entirely. That said, Option C' already fixes the reported bug without D, so D becomes a follow-up rather than a blocker.

> "Migrating `active_cast` requires schema changes. Risky."

The field is additive with a default. Existing rows treat the missing `cast_authored_input_tick` as 0 (or `u32::MAX` depending on direction) - we should pick the direction that errs on the side of "more permissive mobility for in-flight casts at deploy time," which is benign for any cast already in progress when the new server boots.

> "Why not just gate the client harder and skip the server work?"

Server-side authority is non-negotiable for PvP correctness - cheating, network desync, replay determinism all assume the server is the rule. The client gate is a UX layer; the server is the law. Both should agree, which is why we recommend adding both, not picking one.

---

## Ship Order Status

1. **Option C'**: implemented. `cast_authored_input_tick` is plumbed from the newest pending client movement tick at `SendCastRequest` time through to `active_cast`; `last_voluntary_move_input_tick` is tracked per player in `tick_player`; the lifetime mobility check in `tick_active_casts` uses the input-tick comparison.
2. **Client-side movement gate** in `CanAttemptCast`: implemented for normal cast-time, non-release spells.
3. **Option D**: implemented for normal non-release spell starts. `cast_request` now enqueues `PendingCastRequest`; `game_tick` resolves it after movement has advanced through the cast's authored input tick.
4. **Exact visual reconciliation**: implemented. `ActiveCast` carries the prediction token, and local HUD/presentation confirmation requires an exact token match before preserving predicted timing.

The normal cast-time spell path no longer depends on process-time movement intent or kind-only visual matching. Release/channel starts keep their separate key-up semantics and are intentionally outside this normal-cast path.

---

## Open Questions For Reviewers

1. **Jump-only inputs as voluntary movement.** `sync_player_voluntary_move_epoch` ([game_loop.rs:850-869](../server/src/game_loop.rs#L850-L869), tests at [game_loop.rs:1445-1475](../server/src/game_loop.rs#L1445-L1475)) currently treats grounded jump input as voluntary movement. Should a stationary cast fizzle on jump press during cast? Probably yes - matches design intent - but worth confirming with combat design before locking in the predicate that updates `last_voluntary_move_input_tick`.
2. **Movement-delivery spells.** Movement-delivery actions are excluded from the mobility check today ([casting.rs:1578-1588](../server/src/spells/casting.rs#L1578-L1588)). Confirm the input-tick comparison preserves that exclusion when we migrate.
3. **Special movement runtimes.** `reset_player_intent_after_special_movement` ([game_loop.rs:983-1000](../server/src/game_loop.rs#L983-L1000)) zeroes intent on exit from special movement. Should the exit increment `last_voluntary_move_input_tick`? If not, a cast started during special movement might never fizzle on the player's exit movement. Probably yes for consistency, but design-level decision.
4. **Client `cast_authored_input_tick` source.** Resolved for Phase 1: use `MovementNetDriver.NewestPendingTick` at `SendCastRequest` time when available, falling back to the predicted state provider tick.
5. **Migration direction.** Existing in-flight `active_cast` rows missing the new column should be considered deployment-only risk. Current implementation initializes new rows explicitly; revisit migration defaults only if persistent active casts survive module replacement in production.

# Combat Action Identity Audit - 2026-05-16

## Status

Active audit for taking the casting/prediction work to the long-term action identity model.

This document is about correlation quality. It is not proposing another timing patch. The standard is:

- every locally predicted gameplay action gets a client-authored token;
- the server echoes that token on the authoritative accept/reject/cancel result and any long-lived authoritative row for that action;
- client reconciliation preserves local presentation time only after exact token match;
- VFX, animation, cooldown, terminal, and cleanup paths route by server action instance id once the action is accepted.

## Verdict

Normal cast-time spell casting is already close to the target architecture. The server accepts a `predicted_cast_id` plus `client_action_seq`, stores those fields on `ActiveCast`, emits them through `CastActionResult`, and the client cast bar plus spell presentation state machine reconcile by exact token.

The remaining distance from the ideal solution is not in ICICLE's normal cast path. It is that other action families still do not share the same identity contract:

- immediate spell replay is still kind + timeout based;
- melee local replay suppression is runtime action id + short time window based;
- movement actions and movement-delivery state do not carry client prediction tokens;
- defense prediction has no action token and reconciles by owner/kind/timing;
- `CombatEvent` and `ProjectilePresentationEvent` carry strong server action instance ids, but not client prediction ids.

That means accepted server events are well identified after the server creates an action instance, but not every client-predicted action can prove "this authoritative row is my exact input" without inference.

## Current Identity Surface

### Spell Cast-Time Actions

Server:

- `cast_request` accepts `predicted_cast_id` and `client_action_seq`.
- Normal non-release cast requests queue as `PendingCastRequest`, preserving the token and reducer receipt timestamp.
- `ActiveCast` stores `cast_id`, `cast_authored_input_tick`, `predicted_cast_id`, and `client_action_seq`.
- `CastActionResult` stores `action_instance_id`, `predicted_cast_id`, `client_action_seq`, and terminal result.
- Premature self-cancel GCD refund is protected by matching the `GlobalCooldown.started_at` timestamp against the canceled `ActiveCast.started_at`.

Client:

- `LocalCombatState.CreateCastActionToken` creates the client token.
- `LocalCombatState.ConfirmPredictedCastBar` preserves predicted local start after exact token match and adopts authoritative end.
- `LocalSpellPresentationStateMachine.ActiveCastInserted` suppresses authoritative restart after exact token match.
- Local movement cancel dispatch sends `CancelActiveCastRequest(predicted_cast_id, client_action_seq, reason, observed_remaining_ms)`.

Assessment: strong. This is the model to generalize.

Remaining spell-specific gaps:

- `CombatEvent` itself does not carry `predicted_cast_id`/`client_action_seq`; spell prediction correlation comes through `ActiveCast`/`CastActionResult`.
- Instant spell visual replay uses `LocalCombatState.ConsumePredictedSpellVisual(kind, nowMs)`, which is kind + timeout based.
- `GlobalCooldown` has no action instance field. The current started-at match is acceptable for normal cast-time refund, but it is not a general action correlation field.

### Release / Channel Spells

Server:

- `cast_request` and `release_cast_request` accept the same cast token.
- `ActiveCast` stores the token for channels and release-cast/charge spells as well.
- Channels and release-cast actions intentionally do not refund GCD on cancel.

Client:

- Release dispatch retrieves the current token with `CurrentCastTokenForRelease`.
- Cast-time hold presentation only predicts normal cast-time spells; release/channel handling remains mostly server-authored after the initial active row.

Assessment: good enough for current behavior, but not yet fully uniform because release terminal events are still consumed primarily through server action ids and event kind, not a generic action-token layer.

### Movement Actions and Movement Delivery

Server:

- `start_dodge` has no client action token.
- `MovementActionState` carries `action_id`, `kind`, `ability_id`, `resolved_action_id`, `started_at`, and input-tick windows.
- Movement-delivery actions enter through spell `cast_request`, but `start_movement_delivery_request` currently drops `predicted_cast_id` and `client_action_seq`.
- `SpecialMovementRuntime` carries `runtime_id`, `kind`, path data, and timing, but no prediction token or linked movement action id.

Client:

- `LocalCombatState.MovementAction` caches local authoritative movement action timing by owner.
- `EntityRegistry` applies movement action and special movement rows by owner.
- Local movement prediction itself is tick based and robust, but special movement action presentation is not correlated to a client action token.

Assessment: functionally coherent, but not at the same identity standard as cast-time spells. Owner/kind/timing are enough while each player can have only one movement action row, but they do not prove exact input correlation.

Recommended target:

- Add a generic predicted action token to `start_dodge`.
- Preserve token on `MovementActionState`.
- Pass spell cast token through movement-delivery launch and store it on `MovementActionState` and `SpecialMovementRuntime`.
- Echo accept/reject/cancel through a generic action result row or a movement-specific result row.

### Melee

Server:

- `melee_attack` has no client action token.
- Server creates a `spell_id`/action instance id internally and writes it to `CombatEvent.action_instance_id`.
- `PendingMeleeImpact` uses `spell_id` as the action instance id.
- `PendingProjectileRelease`, `ActiveCombatProjectile`, and `ProjectilePresentationEvent` carry `action_instance_id`, so projectile and terminal VFX can route by server action instance after acceptance.

Client:

- `MeleeInputHandler` predicts local melee animation immediately.
- Authoritative replay suppression uses resolved runtime action id plus a short retention window.
- There is no exact client action id to pair a predicted melee swing with the authoritative `CombatEvent`.

Assessment: good server-side action instance continuity after acceptance, weak client prediction correlation before acceptance. The 400ms replay suppression window is the clearest remaining heuristic.

Recommended target:

- Add `predicted_action_id` and `client_action_seq` to `melee_attack`.
- Add a server action result for melee accepted/rejected/queued.
- Include the token in the initial melee `CombatEvent` or in a companion accepted-action row.
- Replace melee replay suppression by runtime action id + time window with token match.

### Defense

Server:

- `start_parry`, `stop_parry`, `start_block`, and stop paths have no action token.
- `DefenseState` is one row per owner with `kind`, timing, movement restriction ticks, and facing yaw.

Client:

- `LocalDefensePrediction` predicts parry locally and reconciles by owner/kind/timing.
- Parry/block hit reactions are driven by `CombatEvent` block/parry terminal events.

Assessment: acceptable for simple one-row defensive states, but not ideal. If parry/block timing becomes more latency-sensitive or if multiple defensive action variants exist, lack of exact action identity will matter.

Recommended target:

- Add action tokens to defense start/stop reducers.
- Store token on `DefenseState`.
- Add authoritative result rows for accepted/rejected/expired defense actions.
- Keep terminal block/parry `CombatEvent` routing by server action instance or include the defense action id as metadata if needed.

### Combat Events, Projectiles, and VFX

Server:

- `CombatEvent` has a strong `action_instance_id`.
- `ProjectilePresentationEvent` has both `action_instance_id` and `projectile_instance_id`.
- `ActiveCombatProjectile` also carries `action_instance_id` and `projectile_instance_id`.

Client:

- `CombatVFXDispatcher` builds facts from `CombatEvent`/`ProjectilePresentationEvent`.
- Travel/projectile lifecycle routes by action/projectile identifiers after authoritative events arrive.

Assessment: strong after server acceptance. VFX is mostly already in the right shape. The missing piece is not VFX lifecycle identity; it is local predicted action-to-authoritative-action correlation for action families that can start locally before server acceptance.

## Ideal Architecture Delta

Introduce one action-token contract and apply it consistently:

```text
ActionPredictionToken
  predicted_action_id: string
  client_action_seq: u64
  authored_kind: string
```

Server reducers that can be locally predicted should accept the token:

- `cast_request`
- `release_cast_request`
- `cancel_active_cast_request`
- `melee_attack`
- `start_dodge`
- movement-delivery launch through `cast_request`
- `start_parry`
- `start_block`
- stop/cancel reducers when they target a predicted action

Authoritative rows should carry the token when they represent the accepted action:

- `ActiveCast`
- `MovementActionState`
- `SpecialMovementRuntime`
- `DefenseState`
- melee accepted-action row, or initial `CombatEvent` if no row exists

Terminal/result rows should carry:

- server action instance id;
- predicted action id;
- client action seq;
- result enum.

Client presentation should follow this order:

1. Predict immediately with local start time.
2. On authoritative accept with matching token, keep local start, adopt authoritative end/terminal data.
3. On authoritative reject/cancel with matching token, clear the predicted presentation.
4. On authoritative row/event without matching local token, treat it as remote/server-authored and start from authoritative time.

## Implementation Plan

### Phase 1 - Spell Model Lockdown

Status: implemented for normal cast-time spells.

Remaining work:

- Add reducer-level tests that prove all GCD refund and no-refund paths through `cancel_active_cast_request` and `tick_active_casts`.
- Add tests around cast bar reconciliation to protect "local start, authoritative end".
- Decide whether instant spell visual replay should move from kind + timeout to token matching before broadening the contract.

### Phase 2 - Generic Client Token Type

Create a client/server naming convention before adding more fields:

- Keep `predicted_cast_id` on existing cast rows for compatibility.
- New action families should use `predicted_action_id`.
- Client can expose one `ActionPredictionToken` helper and map spell casts to the existing wire fields.

This avoids a schema churn pass that renames working spell fields before the rest of the system catches up.

### Phase 3 - Melee Exact Correlation

This is the highest-value next implementation target.

Why:

- Melee already predicts local animation.
- Server already has strong `action_instance_id` continuity after acceptance.
- The replay suppressor is still a time-window heuristic.

Work:

- Add token parameters to `melee_attack`.
- Emit accepted/rejected/queued melee result with token and server action instance id.
- Store token for queued followups where applicable.
- Suppress authoritative local melee replay by token instead of runtime action id + 400ms window.

### Phase 4 - Movement Action Exact Correlation

Work:

- Add token parameters to `start_dodge`.
- Thread spell cast tokens into movement-delivery.
- Store token on `MovementActionState`.
- Store linked movement action id/token on `SpecialMovementRuntime`.
- Use token match to reconcile local special movement presentation.

### Phase 5 - Defense Exact Correlation

Work:

- Add tokens to parry/block start and stop reducers.
- Store token on `DefenseState`.
- Add result rows or explicit events for accepted/rejected defense starts/stops.
- Replace `LocalDefensePrediction` timeout reconciliation with token reconciliation.

### Phase 6 - Event Schema Consolidation

Only after Phases 3-5:

- Consider adding optional `predicted_action_id` and `client_action_seq` to `CombatEvent`.
- Consider adding `accepted_action_result` as a generic row replacing spell-specific result rows.
- Consider adding action instance id to cooldown rows only if future mechanics need exact action-origin cooldown reconciliation.

Do not start here. Event/schema consolidation before the missing action families have tokens would create churn without eliminating the remaining heuristics.

## Acceptance Criteria For "Ideal"

The implementation is at the intended standard when:

- every locally predicted action has exactly one client token;
- every authoritative accept/reject/cancel for that action echoes that token;
- local presentation replay suppression never depends on kind + timeout;
- local HUD/presentation reconciliation never swaps to a server start time for a matched predicted action;
- server terminal paths route by action instance id and do not delete or affect newer action rows;
- tests cover token mismatch, stale token, duplicate token, late cancel, and out-of-order authoritative arrival for spells, melee, movement actions, and defense.

## Recommendation

Do not rewrite the working cast-time spell path. Use it as the reference implementation.

The next real step is Phase 3: exact melee correlation. It has the largest remaining heuristic, the smallest conceptual surface, and the cleanest payoff: remove time-window replay suppression for predicted melee and replace it with token-confirmed reconciliation.

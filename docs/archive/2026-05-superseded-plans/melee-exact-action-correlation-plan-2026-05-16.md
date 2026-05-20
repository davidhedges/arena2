# Melee Exact Action Correlation Plan - 2026-05-16

Archived status: completed and archived on 2026-05-17.

## Source Audit

This plan follows [Combat Action Identity Audit - 2026-05-16](../../combat-action-identity-audit-2026-05-16.md), specifically the finding that melee is the highest-value next target for exact action correlation.

The audit conclusion was:

- normal cast-time spells now use exact client token correlation;
- melee still suppresses duplicate local authoritative animation by inferred runtime action id plus a short timeout;
- server melee events already carry strong `action_instance_id` after acceptance;
- the missing piece is exact correlation from local predicted melee input to the server-accepted melee action.

## Review Resolution

Claude's review correctly identified several issues in the first draft. This revision resolves them as follows:

- Use a typed action result enum, not result strings.
- Do not add a melee-specific result table. Add a generic predicted action result table for melee and future action families.
- Define `client_action_seq` as a session-local, monotonically increasing client sequence number.
- Require explicit rejected results for valid predicted melee attempts that fail server validation.
- Do not rely on callback ordering between the result row and `CombatEvent(Cast)`.
- Treat current combo/followup queue prediction as deprecated compatibility behavior, not a feature to harden.

## Implementation Status

The action-correlation migration covered by this plan is complete in this branch:

- added shared server-side `ActionPredictionToken`, typed `ActionResultKind`, typed `PredictedActionFamily`, and public `PredictedActionResult`;
- changed `melee_attack` to accept predicted action tokens while preserving unpredicted legacy calls for empty tokens;
- records accepted/rejected melee prediction results for valid non-queued predicted attempts;
- prunes prediction result rows during post-tick maintenance;
- regenerated SpacetimeDB C# bindings;
- subscribes the local client to its own `PredictedActionResult` rows;
- replaces local melee replay suppression with exact action-instance correlation when accepted results arrive;
- tolerates result/event callback ordering by briefly holding unmatched authoritative melee events while waiting for the result row;
- leaves deprecated queued combo/followup behavior on the legacy non-token path.

Instant spell replay correlation and local release VFX prediction are also implemented and playtest-confirmed in this branch:

- `ActionResultKind` now includes typed spell terminal states: `Canceled`, `CancelTooLate`, and `StaleToken`;
- spell cast prediction results are emitted through `PredictedActionResult { family: SpellCast }`;
- instant spell accepted rows use the generic result path as the replay-correlation source of truth;
- `ENRAGE`/`BATTLE_CRY`-style local instant spell predictions are stored by cast token instead of by a short spell-id timing window;
- targeted and point-confirmed instant spells, such as `BOOMERANG_ORB` and `ERUPTION`, now start their local cast animation immediately after dispatch/aim confirmation;
- instant spell release VFX now has a predicted presentation path for local release cues and projectile bodies, keyed by the same cast token;
- authoritative local spell release/projectile rows adopt or suppress the predicted VFX by accepted `action_instance_id`, preventing duplicate local effects;
- local authoritative instant spell `COMBAT_CAST` replay is suppressed by accepted `action_instance_id`;
- result/event callback ordering is tolerated by briefly holding unmatched local authoritative spell events while waiting for the generic result row;
- ordinary zero-cast spells, including projectile/area/self-resource spells, execute immediately on the server instead of waiting behind the cast-time pending gate;
- predicted local projectile bodies adopt the authoritative projectile instance id without replaying or hard-snapping the visual when the release row arrives;
- retired the legacy `CastActionResult` table; cast bars, cast holds, instant spell replay, and spell release VFX now consume the generic spell-cast result rows.

Movement and defense action starts now use the same generic result contract:

- fixed `DODGE` requests carry `predicted_action_id` and `client_action_seq`;
- accepted/rejected dodge starts emit `PredictedActionResult { family: Movement }`;
- `PARRY` and server-side `BLOCK` starts carry `predicted_action_id` and `client_action_seq`;
- accepted/rejected defense starts emit `PredictedActionResult { family: Defense }`;
- `DefenseState` now carries an authoritative `action_id` so accepted defense results point at a concrete action instance;
- local parry prediction clears immediately on rejected defense results and preserves the existing accepted-result handoff so the later authoritative `DefenseState` does not replay the local parry animation.
- accepted parry prediction no longer uses the short local reconciliation timeout for correctness; after an accepted defense result, it waits for authoritative `DefenseState` and only has a longer safety cleanup.
- shared server token-policy tests cover legacy unpredicted calls, valid predicted calls, and malformed partial token inputs before movement or defense consult the transient result table.

Known verification note:

- `cargo check --manifest-path server/Cargo.toml` passes.
- `cargo test --manifest-path server/Cargo.toml action_prediction` passes.
- `cargo test --manifest-path server/Cargo.toml movement_actions` passes.
- focused server tests cover the zero-cast routing rule: zero-cast spells execute immediately, normal cast-time spells still use the pending gate, and channels/release-casts keep their immediate path.
- `dotnet build Assembly-CSharp.csproj --no-restore` passes.
- `dotnet build Arena.EditModeTests.csproj` passes.
- `spacetime generate --yes --lang csharp --module-path server --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB` was run after removing `CastActionResult`, and the generator deleted the stale generated table/type files.
- focused client edit-mode tests cover the projectile adoption rule: adopted predicted boomerang/orb projectile updates smooth-correct instead of hard-snapping or respawning on the authoritative release/update handoff.
- playtesting confirmed `ENRAGE`/`BATTLE_CRY` no longer double-play, `ICICLE` cast bars no longer rewind on authoritative insert, and `BOOMERANG_ORB`/`WITHERING_ORB` local projectile VFX no longer respawn after the authoritative release row arrives.
- `ERUPTION` executes immediately; its remaining delayed damage is the authored `impact_delay_ms`, not cast-request gating or delayed local animation.
- `cargo test --manifest-path server/Cargo.toml defense` passes after adding authoritative defense action ids.
- edit-mode contract coverage verifies rejected defense results clear matching predicted parry, accepted defense results wait for authoritative state, and accepted parry uses the longer safety cleanup instead of the short local prediction timeout.
- Full `cargo test --manifest-path server/Cargo.toml` currently has three unrelated melee catalog/authoring assertion failures. They are out of scope for this plan and should not block the action-correlation migration.

## Remaining Work

No implementation work remains in this plan.

Follow-up hardening belongs in separate work:

1. add true reducer-level SpacetimeDB scenario tests once the project has a reusable reducer test harness;
2. keep replacing any remaining family-local prediction heuristics with `PredictedActionResult` as those action families are touched;
3. continue the planned melee authoring migration from deprecated combo/followup chains to one-input phased attacks with multiple hit windows.

Instant self-spells such as `ENRAGE` and `BATTLE_CRY` were the first spell target because they previously used `spellId -> 350ms` local replay suppression and could double-play if server reconciliation landed outside that window. Instant targeted and point-confirmed spells now use the same exact replay-correlation path for their cast animation and release VFX. Damage, hit confirmation, block/parry results, and terminal impact VFX remain authoritative.

## Instant Spell Replay Correlation Plan

### Problem

Instant spells previously did this:

- client sends `CastRequest` with `predicted_cast_id` and `client_action_seq`;
- client immediately plays `CombatAnimationRequest.PredictedSpell` only for self-targeted instant spells;
- client stores a short `PredictSpellVisual(spellId, nowMs, 350ms)` entry;
- server resolves the cast later through the tick-aligned pending-cast path;
- server emits authoritative `COMBAT_CAST`;
- client suppresses authoritative replay only if the `spellId` timing window is still alive.

That means a valid instant spell can play twice when the authoritative cast arrives after the short local window. This is not a gameplay-rule issue; it is the same prediction-correlation issue this plan fixed for melee.

### Goal

Instant spell prediction should reconcile by exact cast token and authoritative `action_instance_id`, not by spell id plus timeout.

The player should see:

- one immediate local animation on input;
- no duplicate authoritative replay when the server accepts the same predicted cast;
- authoritative replay only when there was no matching local prediction;
- rejected casts clear pending local prediction state without waiting for a visual timeout.

### Server Work

Spells now use the generic action-result contract. `CastActionResult` has been removed instead of being kept as a cast-bar compatibility surface.

Server implementation:

- record `PredictedActionResult { family: SpellCast, result: Accepted }` whenever a predicted spell is accepted;
- record `PredictedActionResult { family: SpellCast, result: Rejected }` whenever a predicted spell is rejected before creating an action instance;
- extend `ActionResultKind` with any terminal states that the client must distinguish for polish, such as `Canceled`, `CancelTooLate`, or `StaleToken`, instead of encoding those as strings;
- keep the authoritative spell `action_instance_id` identical between the generic result row and the emitted authoritative `COMBAT_CAST`;
- do not reintroduce a spell-specific action result table; cast-bar and cast-hold reconciliation should continue to consume the generic spell-cast rows plus `ActiveCast`.

Server requirements:

- accepted instant spells must record `PredictedActionResult(SpellCast, Accepted)` with the same `action_instance_id` later emitted on authoritative `COMBAT_CAST`;
- rejected instant spells must record a typed generic result and emit no `COMBAT_CAST`;
- canceled, stale-token, and cancel-too-late paths must preserve their current gameplay behavior while exposing typed generic terminal results where the client needs them;
- cast-time spell cast bars must keep existing behavior during migration, but the replay/correlation path should use the generic result contract;
- the migration should not add another spell-specific prediction-result table.

### Client Work

Replace instant spell visual suppression from:

```text
spellId -> expiresAtMs
```

to:

```text
predictedCastId + clientActionSeq -> pending instant spell visual
actionInstanceId -> accepted local predicted spell
```

Client implementation details:

- when `PredictImmediateInstantSpellVisual` plays a local instant spell, store the pending visual by the `CastActionToken`;
- on `PredictedActionResult { family: SpellCast, result: Accepted }`, map that token to `action_instance_id`;
- on generic non-accepted spell results, clear the pending instant visual according to the typed terminal state;
- when local authoritative `COMBAT_CAST` arrives for a spell, suppress replay if its `action_instance_id` maps to an accepted local predicted spell;
- tolerate callback ordering by briefly holding local authoritative instant spell `COMBAT_CAST` events if a matching result row has not arrived yet;
- keep cast-time spell hold/cast-bar logic stable while migrating replay correlation to the generic path.

Do not solve this by increasing the `350ms` window.

Current implementation detail:

- local instant spell cast animation is predicted for self-targeted, targeted, and point-confirmed spells after the actual `CastRequest` is dispatched;
- target-required spells do not predict locally when no target is selected, matching the expected server rejection path;
- point-targeted spells predict on aim confirmation, not when entering aim mode;
- release-cast, charge, and channel spells remain excluded.
- local instant spell release VFX is predicted only for cues that can be reconciled cleanly today: one-shot release cues and projectile body release cues;
- predicted projectile body visuals use a temporary local projectile key, then adopt the authoritative projectile instance id when the server release row arrives;
- adopted predicted projectile visuals are not restarted by the authoritative release row and do not hard-snap on the first authoritative update;
- rejected/canceled/stale generic spell results remove pending predicted projectile visuals;
- terminal impact VFX remains server-authoritative so damage, hit, block, parry, and fizzle presentation still reflects server truth.

### Instant Spell Tests

Add client edit-mode coverage for:

- `ENRAGE` local prediction stores a pending token;
- accepted generic `PredictedActionResult(SpellCast)` maps token to `action_instance_id`;
- authoritative local `COMBAT_CAST` with that action instance is suppressed;
- authoritative local `COMBAT_CAST` without a matching accepted token plays normally;
- authoritative event arriving before accepted result is held then suppressed;
- accepted result arriving before authoritative event suppresses normally;
- rejected/canceled/stale-token generic result clears pending predicted instant spell according to terminal semantics;
- cast-time spell hold/cast-bar behavior is unchanged.

Add server coverage where practical for:

- accepted instant self-spell records `PredictedActionResult(SpellCast, Accepted)` with non-empty `action_instance_id`;
- rejected instant self-spell records a non-accepted generic result and emits no `COMBAT_CAST`;
- self-cancel, stale-token, and cancel-too-late spell paths expose typed generic terminal results without changing existing cast/GCD behavior;
- accepted cast-time spell behavior remains unchanged.

## Generic Action-Correlation End State

The long-term target is one prediction/result vocabulary for every locally predicted action family:

- melee uses `PredictedActionResult`;
- spells use `PredictedActionResult` for replay/prediction correlation;
- movement actions emit accepted/rejected prediction results for local movement-action visuals;
- defense actions emit accepted/rejected prediction results for block/parry presentation.

`CastActionResult` has been removed. Do not reintroduce a parallel prediction-result model for spells.

Do not create new family-specific result tables. If a new action family needs prediction reconciliation, it uses `PredictedActionResult`.

## Goal

Replace melee replay suppression by timing inference with exact action-token matching.

After this work, when the local player presses a melee action:

1. the client creates a unique predicted action token;
2. the client predicts the swing immediately;
3. the server receives and validates the token;
4. the server echoes the token on the authoritative accepted/rejected result;
5. the client suppresses duplicate local authoritative playback only when the token matches.

## Non-Goals

- Do not rewrite the melee combat system.
- Do not change melee damage, range, cooldown, resource, combo, projectile, or gap-close gameplay rules.
- Do not alter normal cast-time spell behavior.
- Do not migrate movement actions or defense in this phase.
- Do not rename existing spell `predicted_cast_id` fields.
- Do not invest in making current combo/followup attacks more robust. Current combo-style back-to-back attacks are deprecated for this phase.
- Do not expand `PlayerAnimator`. Changes should be contained to server melee identity/result emission, generated bindings, `MeleeInputHandler`, local combat/prediction state, `EntityRegistry`, and `CombatAnimationReplayPolicy`.

## Combo Deprecation Direction

Current combo/followup melee attacks should not drive this correlation work.

The intended long-term authoring model is:

- one player input starts one melee action instance;
- multi-stage attacks are authored as one phased melee action;
- that phased action can contain multiple hit windows;
- all hit windows share the same server `action_instance_id`;
- prediction/reconciliation correlates the one input token to the one accepted phased action.

Example:

- old model: input A starts attack 1, then combo/followup attack 2 is queued or triggered as a separate action;
- target model: input A starts one phased attack with hit window 1 and hit window 2.

Because of this, combo queue/followup support should be treated as compatibility behavior only. This plan should not add new token architecture specifically to make combo chains more sophisticated.

Implementation rule:

- directly triggered non-followup melee actions receive exact prediction tokens;
- phased attacks with multiple hit windows receive one exact prediction token;
- deprecated combo/followup queue acceptance does not receive a new `Queued` prediction result;
- if existing combo queue behavior remains callable during migration, it must not become a new local-prediction correctness dependency.

## Current Behavior

Client melee prediction lives in `Assets/Arena/Runtime/Input/MeleeInputHandler.cs`.

Current flow:

- `MeleeInputHandler.TryTriggerAction` performs local prechecks.
- It calls `conn.Reducers.MeleeAttack(...)`.
- It predicts resource/GCD/cooldown state locally.
- It triggers local animation through `CombatAnimationRequest.PredictedMeleeSkill`.
- It stores a predicted visual keyed by resolved runtime action id for `400ms`.
- When the server later emits `CombatEvent(Cast)`, `CombatAnimationReplayPolicy` asks `MeleeInputHandler.ConsumePredictedStrikeVisual(...)` whether to suppress local replay.
- Suppression succeeds when the same runtime action id appears inside the retention window.

That is the inference this plan removes.

The current combo/followup queue path may still exist while this plan is implemented, but it should not receive special new correlation behavior beyond not breaking existing gameplay. New multi-hit melee should be authored as phased attacks with multiple hit windows.

Server melee already has good post-acceptance identity:

- `perform_melee_attack_for` creates an internal action instance id.
- initial melee `CombatEvent` stores it in `action_instance_id`;
- `PendingMeleeImpact` carries that id as `spell_id`;
- `PendingProjectileRelease`, `ActiveCombatProjectile`, and `ProjectilePresentationEvent` carry `action_instance_id`.

## Target Contract

Add a generic action prediction token:

```text
predicted_action_id: string
client_action_seq: u64
```

For melee, this token means:

- `predicted_action_id` identifies one local melee input attempt;
- `client_action_seq` is a local-client session sequence number;
- server `action_instance_id` remains the authoritative id after acceptance;
- the client maps `predicted_action_id + client_action_seq` to `action_instance_id` when the server accepts.

### `client_action_seq` Contract

- Scope: per local client connection/session, shared across predicted action families.
- Start value: `1`.
- Increment: every locally predicted action attempt consumes one sequence value.
- Zero: invalid.
- Reconnect: pending predicted actions are cleared; a new session may restart at `1`.
- Persistence: not persisted across client restarts.
- Server validation: validate non-zero and token shape, but do not reject solely because a sequence is lower than the last observed sequence. Reducer delivery and reconnect behavior should not make monotonic ordering a gameplay dependency.
- Duplicate policy: the same `(owner, predicted_action_id, client_action_seq)` must never map to two server action instances within the result TTL. Duplicate tokens should produce no new trusted correlation and should be rejected or treated as stale.

## Server Plan

### 1. Extend `melee_attack`

Change reducer signature:

```rust
pub fn melee_attack(
    ctx: &ReducerContext,
    strike_id: String,
    target_id: String,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
    predicted_action_id: String,
    client_action_seq: u64,
) -> Result<(), String>
```

Thread the token through `perform_melee_attack_for`.

### 2. Add Shared Token Types And Validation Helpers

Reuse the same policy as spell cast tokens:

- non-empty id;
- bounded length;
- expected character set;
- non-zero `client_action_seq`.

Prefer extracting shared helpers from spell casting instead of duplicating token validation logic.

Add a Rust-side shared token representation or helper API so spell, melee, movement, and defense do not each invent slightly different validation rules.

### 3. Add Generic Authoritative Result Row

Do not add a `MeleeActionResult` table. Add one generic public result table for predicted actions:

```rust
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ActionResultKind {
    Accepted,
    Rejected,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PredictedActionFamily {
    SpellCast,
    Melee,
    Movement,
    Defense,
}

#[table(accessor = predicted_action_result, public)]
pub struct PredictedActionResult {
    #[primary_key]
    #[auto_inc]
    pub event_id: u64,
    #[index(btree)]
    pub owner: Identity,
    pub family: PredictedActionFamily,
    pub action_instance_id: String,
    pub predicted_action_id: String,
    pub client_action_seq: u64,
    pub result: ActionResultKind,
    pub created_at: Timestamp,
    #[index(btree)]
    pub created_at_micros: i64,
    #[index(btree)]
    pub expires_at_micros: i64,
}
```

Use the exact SpacetimeDB enum derive/import style already supported by this project when implementing the enum. The key requirement is that generated C# bindings expose typed enum values, not raw strings.

Keep both `created_at` and `created_at_micros` only if both jobs are needed:

- `created_at`: readable event timestamp for clients and debugging;
- `created_at_micros`: indexed scalar for ordering/pruning.

If implementation shows `created_at_micros` is only needed for pruning, it can be omitted and `expires_at_micros` can be the only indexed cleanup field.

Do not add a `Queued` result for current combo/followup behavior in this phase. Since combo chains are being deprecated in favor of phased single-input attacks, adding queue-specific token semantics would harden a path we intend to retire.

Result table policy:

- new predicted action families must use `PredictedActionResult`;
- do not add parallel family-specific result tables;
- spells now use `PredictedActionResult { family: SpellCast }` as the replay, cast-bar, and cast-hold correlation source of truth;
- the legacy spell-specific result table has been removed and should not be reintroduced.

### 4. Emit Results

On server accept:

- emit `PredictedActionResult { family: Melee, result: Accepted }` with `action_instance_id`, `predicted_action_id`, and `client_action_seq`;
- continue emitting the existing `CombatEvent(Cast)` with the same `action_instance_id`.
- emit the accepted result only after all fallible pre-commit and hit-window scheduling work has succeeded, so `Accepted` means the server action instance really started.

On server reject:

- emit `PredictedActionResult { family: Melee, result: Rejected }` with empty `action_instance_id`;
- do not emit combat events.

Silent server no-ops should become explicit rejected results when the token is valid and the reducer represents a local predicted action. This is important because the client needs to clear prediction confidently.

The rejection audit must cover these `perform_melee_attack_for_internal` paths:

- unresolved or unauthorized melee action;
- action not assigned on active spec;
- invalid action resource setup;
- missing/invalid melee gameplay row;
- no hit windows;
- invalid projectile authoring;
- missing `PlayerState`;
- dead caster;
- disabling status;
- insufficient resource;
- deprecated combo/followup input rejected by current combo rules;
- global cooldown;
- named cooldown;
- missing `PlayerPhysics`;
- aerial execution mismatch;
- invalid target id;
- self target;
- missing/dead target;
- world-context mismatch;
- non-hostile target;
- missing target physics;
- airborne target mismatch;
- target outside facing arc;
- target out of range;
- targetless projectile action;
- missing caster/target snapshots for projectile line-of-sight;
- line-of-sight failure;
- gap-close pre-commit failure;
- resource spend failure.

The implementation should make it mechanically hard to add a new valid-token early return without emitting `Rejected`.

### 5. Prune Results

Add a server-side TTL sweep for `PredictedActionResult`.

Use an expiry long enough for normal client reconciliation and reconnect diagnostics, but short enough that the public result table cannot grow without bound. Start with `expires_at_micros = created_at + 10_000ms`.

The client subscription should filter to `owner == local_identity`; pruning is still required because public tables persist independently of any one client subscription.

### 6. Preserve Existing Combat Event Semantics

Do not change:

- melee `CombatEvent.action_instance_id`;
- pending melee impact identity;
- projectile presentation identity;
- VFX routing by authoritative action instance id.

The token is for client prediction correlation, not a replacement for server action instance id.

## Client Plan

### 1. Add Generic Action Token Helper

Add a small token type near `LocalCombatState.CastActionToken`, or a new shared simulation/input type:

```csharp
public readonly struct ActionPredictionToken
{
    public string PredictedActionId { get; }
    public ulong ClientActionSeq { get; }
    public string Kind { get; }
}
```

The existing spell cast token can remain unchanged. Do not rename working spell APIs in this phase.

### 2. Generate Token For Melee

In `MeleeInputHandler.TryTriggerAction`:

- create an action token before calling `Reducers.MeleeAttack`;
- pass `predicted_action_id` and `client_action_seq` to the reducer;
- store the pending predicted visual by token, not by runtime action id.

### 3. Replace Visual Suppression Key

Replace:

```text
runtimeActionId -> expiresAtMs
```

with:

```text
predictedActionId + clientActionSeq -> predicted melee visual state
```

The state should include:

- runtime action id;
- authored action id;
- predicted start ms;
- expiry ms;
- optional predicted resource/cooldown information if needed later.

### 4. Reconcile On `PredictedActionResult`

On `Accepted` with `family == Melee`:

- find the pending predicted melee visual by token;
- attach the server `action_instance_id`;
- mark it accepted;
- when the matching authoritative `CombatEvent(Cast)` arrives, suppress replay by `action_instance_id` or accepted token.

On `Rejected` with `family == Melee`:

- clear the pending prediction;
- reconcile optimistic resource/GCD/cooldown if the authoritative tables do not confirm it.

Subscribe to this result table only for the local owner. The table is public and transient, so server pruning and client subscription filtering are part of the feature, not optional cleanup.

### 5. Update Authoritative Replay Policy

Change local melee suppression from:

```text
same runtime action id within 400ms
```

to:

```text
server action_instance_id is mapped to an accepted local predicted melee token
```

### 6. Handle Result/Event Ordering

Do not rely on callback order between `PredictedActionResult(Accepted)` and `CombatEvent(Cast)`.

The client must tolerate either arrival order:

- result first: map token to `action_instance_id`; suppress matching local `CombatEvent(Cast)` when it arrives;
- event first: hold local-player authoritative melee `CombatEvent(Cast)` briefly while waiting for a matching result row; if the matching result arrives, suppress replay; if it does not arrive, treat it as an unmatched authoritative event and process according to replay policy.

The hold exists only for local predicted-capable melee start events. Remote players and non-predicted sources should not be delayed.

### 7. Cleanup Windows

Do not use cleanup expiry as the correctness mechanism.

Use named constants:

- `PendingMeleePredictionTtlMs = 5000`: memory safety and disconnect/reconnect cleanup for unconfirmed local predictions. This is intentionally longer than normal RTT and reducer latency; normal accept/reject should arrive well before it.
- `PendingLocalMeleeEventHoldMs = 250`: short ordering buffer for local authoritative `CombatEvent(Cast)` arriving before its result callback. This covers callback/update ordering, not network round-trip.
- accepted action-instance mappings are removed immediately after suppressing the matching authoritative replay, or after `PendingMeleePredictionTtlMs` as a safety net.

If a reject never arrives because the client disconnects or loses subscription state, reconnect clears pending local predictions and authoritative tables rehydrate the durable state.

## Tests

### Server Tests

Add focused tests in `server/src/melee.rs`:

- valid predicted melee token accepted emits `PredictedActionResult(Accepted)`;
- accepted result includes server `action_instance_id`;
- rejected melee emits `PredictedActionResult(Rejected)` with matching token;
- invalid token does not create a trusted correlation row;
- two same melee actions in quick succession produce distinct tokens and distinct action instance ids;
- phased melee with multiple hit windows keeps one action instance id across every hit window;
- projectile melee carries the accepted action instance id through `PendingProjectileRelease`;
- pending melee impact carries the accepted action instance id through impact resolution.
- duplicate token does not create a second action instance mapping;
- lower `client_action_seq` with a unique `predicted_action_id` is not rejected solely for being lower;
- every valid-token rejection path listed in this plan emits `Rejected`;
- deprecated combo/followup queue behavior does not emit `Queued` and does not gain new prediction semantics.

### Client Edit-Mode Tests

Add or extend melee prediction tests:

- local melee prediction stores pending visual by token;
- accepted result maps token to server action instance id;
- authoritative local `CombatEvent(Cast)` with mapped action instance id suppresses replay;
- authoritative local `CombatEvent(Cast)` without mapped token plays normally;
- `CombatEvent(Cast)` arriving before `PredictedActionResult(Accepted)` is held and then suppressed when the result arrives;
- `PredictedActionResult(Accepted)` arriving before `CombatEvent(Cast)` maps normally and suppresses the later event;
- two same-action presses close together do not consume each other's prediction;
- rejected result clears pending prediction.
- phased multi-hit melee reconciles as one predicted action, not as multiple combo predictions.
- reconnect clears stale pending predictions and does not leave permanent optimistic resource/cooldown state.

## Migration Notes

This changes a SpacetimeDB reducer signature. After server schema changes:

```bash
spacetime generate --yes --lang csharp --module-path server --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB
```

Do not hand-edit generated bindings.

All reducer call sites must be updated after generation.

Deploy ordering:

- server schema/reducer changes and generated client bindings must ship together for development builds;
- no old client should call the new reducer signature;
- if live compatibility is needed later, add a temporary compatibility reducer instead of overloading semantics.

## Acceptance

### Melee Phase 1

- Local melee still animates immediately on input.
- The local player does not see duplicate authoritative melee animation when the server accepts the predicted action.
- Suppression no longer depends on a fixed 400ms timing window.
- Two identical melee inputs close together reconcile independently.
- A phased multi-hit melee action uses one prediction token and one server action instance id across all hit windows.
- Rejected melee clears local predicted visual/resource/cooldown state without waiting for a timeout.
- Current combo/followup queue behavior is not hardened; new multi-hit authoring uses phased actions instead.
- Existing server melee tests pass.
- Existing Unity edit-mode tests pass.

The three unrelated melee catalog/authoring assertion failures currently seen in full `cargo test` are excluded from this acceptance gate.

### Remaining Migration

- Instant self-spells no longer double-play when authoritative `COMBAT_CAST` arrives after the old local timing window.
- Instant spell replay suppression uses exact token/action-instance correlation.
- Instant targeted and point-confirmed spell release VFX starts locally and reconciles through the same token/action-instance path.
- Projectile and one-shot release VFX no longer wait on the pending-cast server tick for local accepted casts.
- Movement and defense prediction use the same generic result vocabulary.
- No new action family introduces a family-specific prediction-result table.

## Recommended Order

### Completed Melee Phase

1. Add shared token validation and typed `PredictedActionResult`.
2. Generate C# bindings.
3. Update client reducer call site.
4. Add client pending-token storage and result callback.
5. Switch replay suppression to accepted action-instance mapping.
6. Add focused server token tests.
7. Remove runtime-action-id replay timeout as the primary correctness dependency for direct predicted melee.

### Completed Instant Spell Phase

1. Add exact pending-token storage for instant predicted spells.
2. Emit typed `PredictedActionResult { family: SpellCast }` rows for accepted and terminal spell outcomes.
3. Use generic accepted spell rows to map spell token to `action_instance_id`.
4. Suppress local authoritative instant spell `COMBAT_CAST` by accepted action instance.
5. Add ordering tolerance for result/event callback order.
6. Predict local instant spell release VFX for one-shot release cues and projectile body cues.
7. Adopt/suppress authoritative release/projectile VFX rows by accepted action instance.

### Completed Movement And Defense Phase

1. Move fixed dodge onto `PredictedActionResult { family: Movement }`.
2. Move parry/block starts onto `PredictedActionResult { family: Defense }`.
3. Preserve accepted parry prediction until authoritative `DefenseState` arrives.
4. Clear rejected parry predictions immediately from generic defense result rows.
5. Add client edit-mode contract tests for accepted/rejected defense prediction.
6. Add shared server token-policy tests for legacy, valid, and malformed action prediction inputs.

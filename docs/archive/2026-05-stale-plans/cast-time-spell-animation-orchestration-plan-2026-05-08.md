# Cast-Time Spell Animation Orchestration Plan - 2026-05-08

## Purpose

This plan defines the production-grade fix for cast-time spell animation timing.

The immediate symptom is `ICICLE`: the spell has `cast_time_ms: 250`, but the release animation plays immediately at cast acceptance. That makes the character finish or visibly release before the server actually fires the projectile.

The larger issue is architectural. Cast-time spell presentation needs a first-class orchestration layer that aligns animation, cast hold, VFX cues, and authoritative server release timing without regressing the existing combat animation system.

## Context For Reviewers And LLMs

This project is a Unity client with a SpacetimeDB server. The server is authoritative for spell acceptance, cast timing, release, projectile travel, collision, impact, damage, block, parry, fizzle, and status effects.

Spell authoring is split across three layers:

- gameplay lives in `server/src/progression_catalog.shared.json` under `abilities[].gameplay`
- animation lives in Unity `CombatAnimationSet.spells[]`, usually under `Assets/Arena/Resources/CombatAnimationSets/*.asset`
- VFX lives in `combat_vfx_cues[]` plus `Assets/Arena/Resources/CombatVFX/CombatVFXRegistry.asset`

`PlayerAnimator` is intentionally sensitive. It owns actual Animator playback, layer arbitration, masks, combat stance, banked clip overrides, lower-body unlock, visual interrupt rules, and combat animation preemption. Do not bypass it or turn it into a dumping ground for new spell-specific timing logic.

The current cast-time bug is caused by routing `COMBAT_CAST` directly into spell release animation playback. `COMBAT_CAST` happens when the cast is accepted, while server gameplay release happens later at `ActiveCast.ends_at`. `ActiveCast` already has `started_at` and `ends_at`, but `EntityRegistry` currently ignores it for animation.

The desired fix is an orchestration layer that listens to `ActiveCast`, presents cast enter/idle, computes `releaseAnimationStart = ActiveCast.ends_at - authoredReleaseOffset`, then asks `PlayerAnimator` to play the release animation through the existing request path. `COMBAT_RELEASE` remains the authoritative gameplay and VFX release fact.

`CombatAnimationSet` should own the release frame inside the release clip. Existing `groundEffectTime` and `airEffectTime` fields are the likely serialization hook, but their semantics need to be clarified or renamed because "effect time" has already caused confusion.

The Kevin Iglesias spellcasting animation package supports this model: it contains `CastingEnter01`, `CastingIdle01`, `CastingExit01`, and many `MagicAttack... - Load` / `MagicAttack... - Cast` clips. The checked `.fbx.meta` files have no animation events, so this plan deliberately does not use Unity animation events as gameplay authority.

Hard rules for implementers:

- do not add spell-id branches such as `if (spellId == "ICICLE")`
- do not use Unity animation events as authoritative release timing
- do not spawn authoritative gameplay projectiles from `ActiveCast`
- do not duplicate spell playback outside `PlayerAnimator`
- do not bind animator override clips from the scheduler
- do not let both `COMBAT_CAST` and `ActiveCast` trigger the same release animation
- do not rely on callback ordering between `COMBAT_CAST` and `ActiveCast` to infer cast-time behavior
- do not drop target/facing data when rerouting release animation off `ActiveCast`

## Resolved Design Decisions

These decisions are part of the plan, not implementation-time choices.

### Cast-Time Gate

The client gates `COMBAT_CAST` animation routing by looking up the spell action in the replicated spell definition/catalog data and reading `cast_time_ms`.

Rules:

- `cast_time_ms == 0`: `COMBAT_CAST` may request the release animation immediately.
- `cast_time_ms > 0`: `COMBAT_CAST` must not request the release animation.
- missing spell definition for a spell combat event is an authoring/subscription error and should log loudly; it must not be patched with a spell-id branch.

Rejected alternatives:

- do not rely on `ActiveCast` insert arriving before or after `COMBAT_CAST`
- do not add a V1 server schema flag unless the catalog lookup proves insufficient
- do not special-case individual spells

### `COMBAT_CAST` Role After This Change

`COMBAT_CAST` remains the authoritative cast-accepted fact.

For instant spells, it still drives immediate release animation.

For cast-time spells, it drives only acceptance-time presentation such as `SPELL_CAST` VFX cues and telemetry. It does not become a no-op, and it does not play the release animation. Cast hold and scheduled release animation are driven by `ActiveCast`.

### VFX Trigger Ownership

VFX remains event/cue driven.

- `SPELL_CAST` cues continue to trigger from `COMBAT_CAST`.
- `SPELL_RELEASE` / projectile body cues continue to trigger from `COMBAT_RELEASE`.
- `SPELL_IMPACT` cues continue to trigger from impact events.
- `ActiveCast` does not spawn authoritative VFX or gameplay projectiles.

For V1, cast-loop/hand/ground VFX that should last through a cast must use authored `DURATION` values that match the intended hold window. A later lifecycle such as `UNTIL_RELEASE` can be added as a separate VFX-scheduler slice, but it is not required for Icicle animation correctness.

If local cast-time projectile prediction is added in V1, it must be explicitly presentation-only, keyed by `cast_id`, and reconciled or discarded when `COMBAT_RELEASE` / `ActiveCombatProjectile` arrives. Do not let predicted visuals apply damage, collision, impact, or gameplay state.

### Aim And Facing Data

`ActiveCast` already carries target and aim data in this codebase:

- `target_id`
- `aim_x`
- `aim_y`
- `aim_z`

The scheduler must propagate that data into the scheduled release `CombatAnimationRequest` as the same facing target information that immediate combat-event requests use today. If `ActiveCast` has a valid aim point, the release animation should face that point; it must not silently fall back to the caster's current transform yaw.

This is a routing requirement, not a V1 schema change.

### State Ownership

Cast-hold presentation state lives in `CombatActionPlaybackController`, alongside existing active spell presentation, banked-clip, lower-body unlock, and melee phase state.

`SpellCastPresentationController` is a scheduler/router:

- it may keep a pending release timer for the current active cast
- it does not own animator-layer state
- it does not own banked clips
- it does not manipulate presentation state directly

### Time Alignment And Late Starts

The scheduler must anchor to `ActiveCast.ends_at` using `ArenaServerClock` whenever a server-time estimate is available.

Recommended V1 late-start policy:

```text
MaxReleaseCatchupMs = 200
```

If the calculated release animation start time is already in the past:

- late by `<= 200ms`: start immediately with catch-up through the existing remote timing path
- late by `> 200ms`: start immediately and clamp catch-up to `200ms`; do not delay authoritative release presentation further

If no server clock estimate is available, use this hierarchy:

1. schedule from `ActiveCast.ends_at` through `ArenaServerClock` when available
2. if the clock is missing but `ActiveCast.started_at` and `cast_time_ms` are available after the clock appears, derive the same `ends_at` schedule
3. if the clock never becomes available before release, keep the cast hold active and play release immediately from `COMBAT_RELEASE`

Do not fall back to the old "play release from `COMBAT_CAST`" behavior for cast-time spells.

### Release Frame Versus Event Arrival

There is an unavoidable latency mismatch if release animation is scheduled to the server's `ends_at` while projectile visuals are spawned only after the client receives `COMBAT_RELEASE`.

V1 must make this an explicit tradeoff instead of accidentally shipping it.

Default V1 policy:

- local cast-time spells should predict cast-enter/idle immediately after local input passes the same local checks used to send `CastRequest`
- local release animation may be scheduled from confirmed `ActiveCast.ends_at`
- authoritative projectile gameplay still waits for `COMBAT_RELEASE` / `ActiveCombatProjectile`
- if a local predicted projectile visual is needed to make the projectile visibly leave the hand at the authored release frame, implement it as a presentation-only visual keyed by `cast_id` and reconcile it on authoritative release
- remote cast-time spells should favor authoritative correctness and may visually backdate/catch up from `COMBAT_RELEASE` rather than pretending the projectile was seen leaving the hand in real time

If V1 does not implement predicted local projectile visuals, Phase 4 acceptance must not claim that the projectile visibly leaves the hand exactly at the authored frame under network latency. It may claim that the release animation reaches its authored release frame at `ends_at`.

### Single Active Cast Invariant

The server model is one active cast per caster. Presentation should keep that invariant instead of building multi-cast bookkeeping for V1.

Cancellation API should cancel the current cast hold for the entity/caster, not a random cast id. If the server later supports multiple simultaneous casts per caster, that becomes a separate contract change.

### Terminology

Use `spell action id` in new code and documentation for the key shared by:

- `progression_catalog.shared.json` spell action id
- `SpellDefinition.kind`
- `CombatAnimationSet.spells[].spellId` legacy serialized field
- combat event `action_kind`

Existing serialized/runtime names may still say `spellId`; new APIs should prefer `spellActionId` to avoid repeating the earlier action-kind/action-instance confusion.

## Critical Non-Negotiable

The combat animation system must not regress.

Do not solve cast-time spells by adding ad hoc spell branches, bypassing the existing animation arbiter, or letting a new component manipulate animator layers independently of `PlayerAnimator`. The current animation system has hard-won behavior around combat stance, melee preemption, overlay playback, left gesture playback, lower-body unlock, visual interrupt windows, ghosting, and banked clip overrides. This work must preserve those responsibilities.

The acceptable implementation is an orchestration layer above the existing spell playback path. It decides *when* to request cast enter, cast idle, and release animation. It does not replace `PlayerAnimator` as the owner of clip playback, layer arbitration, combat stance, bank slots, or preemption.

## Current Behavior

Current server flow:

1. `CastRequest` is accepted.
2. `begin_active_cast(...)` inserts `ActiveCast` with:
   - `cast_id`
   - `ability_id`
   - `kind`
   - `started_at`
   - `ends_at`
3. The server immediately emits `COMBAT_CAST`.
4. When `now >= active_cast.ends_at`, the server finishes the cast and emits `COMBAT_RELEASE` for projectile release.

Current client animation flow:

1. `EntityRegistry.OnCombatEventInsert(...)` receives `COMBAT_CAST`.
2. `CombatAnimationRequestTranslator` translates it into a spell `CombatAnimationRequest`.
3. `PlayerAnimator.RequestCombatAnimation(...)` immediately calls `PlaySpellAnimation(...)`.
4. `PlaySpellAnimation(...)` binds the spell clip and plays it immediately.

Current gap:

`ActiveCast` timing is not used for animation. In fact, `EntityRegistry` currently documents that `ActiveCast` is not the primary spell animation trigger path. That was acceptable for instant spells, but it is wrong for cast-time spells.

## Existing Responsibilities To Preserve

### `CombatAnimationSet`

Owns combat-profile-specific animation data:

- spell action id mapping
- grounded and airborne spell clips
- playback layer policy
- combat stance policy
- lower-body unlock timing
- visual interrupt timing
- release-frame metadata

This remains the correct authoring home for spell animation and release-frame timing.

### `PlayerAnimator`

Owns animation execution:

- central combat animation request entry point
- preemption decisions
- combat stance entry
- full-body vs upper-body vs left-gesture playback
- animator override controller clip binding
- spell bank slot selection
- lower-body unlock and blend-out state
- clearing interrupted presentation state

`PlayerAnimator` should receive requests such as "play this spell release animation now." It should not become the scheduler that computes delayed release starts from `ActiveCast.ends_at`.

### `CombatActionPlaybackController`

Owns reusable playback state:

- banked clip tracking
- active spell presentation state
- active melee presentation state
- lower-body unlock timing state
- phased melee state

It must own reusable spell cast-hold presentation state for this feature. It should not subscribe to network tables directly.

### `EntityRegistry`

Owns network-to-entity routing:

- receives table callbacks
- finds the caster `PlayerEntity`
- forwards presentation facts to the entity/animator layer

It should route `ActiveCast` changes to the cast-time spell presentation scheduler. It should not contain spell-specific animation timing logic.

## Desired Model

Cast-time spell presentation has two phases:

```text
Cast hold phase:
  ActiveCast inserted
  -> play CastingEnter
  -> hold CastingIdle until release animation should begin

Release phase:
  start release animation at:
    ActiveCast.ends_at - authoredReleaseOffset
  -> authored release frame aligns with server release time; visible projectile spawn alignment requires either local predicted projectile presentation or an accepted latency tradeoff
```

Instant spells remain simple:

```text
COMBAT_CAST accepted
  -> play release animation immediately
```

The server remains authoritative. The client uses authored animation timing only to schedule presentation around the server's known `ActiveCast.ends_at`.

For the local player, cast-enter prediction is part of V1. It is animation-only and may start immediately after local input passes the same checks used to submit `CastRequest`. The authoritative `ActiveCast` either confirms and schedules the release, or cancels/reconciles the predicted hold if the cast is rejected or replaced.

## Authoring Contract

### Gameplay

`server/src/progression_catalog.shared.json` remains the source of truth for gameplay timing:

```json
{
  "gameplay": {
    "kind": "SPELL",
    "cast_time_ms": 250
  }
}
```

The server does not read Unity animation assets.

### Release Animation

`CombatAnimationSet.spells[]` remains keyed by runtime spell/action id:

```text
spellActionId = ICICLE
ground = release clip
air = optional airborne release clip
groundReleaseTime = normalized or seconds-based release point
airReleaseTime = normalized or seconds-based release point
```

Current fields `groundEffectTime` and `airEffectTime` are already present. They should be renamed or explicitly re-documented as release timing inside the release clip. The current name "effect time" is too vague and has already led to confusion.

The release offset is:

```text
authoredReleaseOffsetSeconds =
  releaseNormalized * releaseClip.length
```

Then:

```text
releaseAnimationStartServerTime =
  ActiveCast.ends_at - authoredReleaseOffsetSeconds
```

If `authoredReleaseOffsetSeconds > gameplay.cast_time_ms`, the animation cannot be aligned without either:

- starting the release animation before the cast begins
- speeding up the release clip
- increasing `cast_time_ms`
- choosing a different release clip or release timing

V1 should reject this in validation rather than silently scaling clips.

### Cast Hold Animation

Cast-time spells need a reusable cast hold profile, also authored on the combat animation set:

```text
SpellCastHoldProfile
  enterClip
  idleLoopClip
  exitClip optional
  playbackLayer
  sexVariantPolicy or clip set for male/female profile assets
```

For the current Kevin Iglesias package:

- male enter: `HumanM@CastingEnter01`
- male idle: `HumanM@CastingIdle01`
- male exit: `HumanM@CastingExit01`
- female equivalents exist under the female spellcasting folder

The cast hold profile should be combat-profile data, not spell gameplay data.

V1 can use one default cast hold profile per `CombatAnimationSet`. Later, individual spells may optionally override it.

The hold profile has its own `playbackLayer`. It should not blindly inherit the release animation's playback layer because hold and release often have different movement/stance needs. A future mobile-casting profile can choose an upper-body hold layer without changing release animation semantics.

Movement policy is gameplay first, presentation second:

- if gameplay cancels movement during cast, the server must delete `ActiveCast`; presentation cancels hold from that delete
- if gameplay allows moving while casting, the hold profile must use an appropriate moving-safe layer or clip
- do not play a full-body stationary `CastingIdle01` over locomotion for a mobile cast unless that is explicitly authored for the profile

## Kevin Iglesias Package Findings

The package supports the proposed model well.

Relevant structure:

- `HumanM@CastingEnter01.fbx`
- `HumanM@CastingIdle01.fbx`
- `HumanM@CastingExit01.fbx`
- female equivalents
- `MagicAttacks/... - Load.fbx`
- `MagicAttacks/... - Cast.fbx`

Observed import facts:

- `CastingEnter01` is a non-loop enter clip.
- `CastingIdle01` is imported with `loopTime: 1`.
- `CastingExit01` is a non-loop exit clip.
- many magic attacks split load/hold from cast/release.
- checked `.fbx.meta` clips have `events: []`.

Conclusion:

Do not use Unity animation events as gameplay release authority. The package itself does not require them. Use explicit authored release timing in `CombatAnimationSet`.

The package vocabulary maps cleanly to:

```text
CastingEnter01 -> cast hold enter
CastingIdle01  -> cast hold loop
MagicAttack... - Cast -> release animation
MagicAttack... - Load -> optional spell-specific hold loop or charge loop
CastingExit01 -> cleanup/cancel/return transition
```

## Proposed Runtime Layer

Add a sibling runtime component, tentatively:

```text
SpellCastPresentationController
```

Ownership:

- receives `ActiveCast` insert/update/delete facts from `EntityRegistry` or `PlayerEntity`
- resolves the caster's `CombatAnimationSet`
- receives only cast-time spells; `COMBAT_CAST` routing performs the `cast_time_ms > 0` gate through the replicated spell definition/catalog data
- resolves facing from `ActiveCast.target_id` / `aim_x` / `aim_y` / `aim_z`
- starts cast hold presentation
- schedules release animation start using `ActiveCast.ends_at` and authored release offset
- cancels cast hold/release scheduling if `ActiveCast` is deleted before release starts
- asks `PlayerAnimator` to play release animation through the existing spell playback path

Non-ownership:

- does not bind override clips directly
- does not manipulate animator layer weights directly
- does not choose left gesture vs upper body itself
- does not resolve VFX anchors
- does not spawn projectiles
- does not decide gameplay release timing

The scheduler must use `ArenaServerClock` estimates for server-time alignment. If no clock estimate exists, it follows the explicit no-clock fallback in this document: hold until clock recovery or `COMBAT_RELEASE`, then play release immediately.

## Required `PlayerAnimator` Surface

Do not let the scheduler reach into private animator state.

Expose a narrow surface, for example:

```csharp
public bool TryBeginSpellCastHold(string spellActionId, long startedAtMs, long endsAtMs);
public bool TryPlaySpellReleaseAnimation(string spellActionId, long releaseAtMs);
public void CancelCurrentSpellCastHold();
```

Alternative:

Extend `CombatAnimationRequest` with a spell-specific phase field while keeping `Category = Spell`.

```text
CombatAnimationCategory.Spell
CombatSpellAnimationPhase:
  HoldStart
  Release
  Cancel
```

Do not add spell phases to `CombatAnimationCategory`. That enum answers "what kind of action is this?" and should remain `MeleeSkill | AutoAttack | Spell`. The phase answers "which spell presentation phase?" and should be a separate field that defaults to `Release` for existing spell requests.

This is probably cleaner long term because it keeps the one-request-entry rule intact. The scheduler would create `CombatAnimationRequest`s; `PlayerAnimator` would remain the single combat presentation arbiter.

Recommendation:

Use the request-based approach unless implementation proves it creates excessive churn. It aligns with the existing architecture plan: all combat presentation should pass through one request entry point in `PlayerAnimator`.

## Event Routing Rules

### Cast-Time Gate Signal

`EntityRegistry` or the combat animation translator must resolve `CombatEvent.action_kind` to the replicated spell definition and read `cast_time_ms`.

This is the only V1 gate for deciding whether `COMBAT_CAST` is an immediate release animation request. `ActiveCast` ordering must not be used as the gate.

Ordering rule:

- if `COMBAT_CAST` arrives before `ActiveCast`, the cast-time gate suppresses release animation and still allows `SPELL_CAST` VFX cues
- if `ActiveCast` arrives before `COMBAT_CAST`, the scheduler may start/confirm hold, but the later `COMBAT_CAST` still must not request release animation
- the scheduler tracks the current active cast per caster and treats `ActiveCast` delete as the cancellation signal

This avoids needing `if (spellActionId == "...")` branches to handle table callback order.

### Local Prediction

For local cast-time spells, V1 should predict cast-enter/idle animation after local input validation succeeds and before `ActiveCast` arrives. This prevents the new correct timing model from feeling less responsive than the old bug.

Prediction rules:

- predict hold only, not damage, collision, projectile gameplay, or impact
- key the prediction by the submitted spell action id and local send time until authoritative `ActiveCast.cast_id` arrives
- when `ActiveCast` arrives, reconcile the predicted hold to the authoritative `cast_id`, `started_at`, `ends_at`, target, and aim
- if the cast is rejected or no matching `ActiveCast` arrives within `PredictionConfirmTimeoutMs = 750`, cancel the predicted hold through the same cancel path as `ActiveCast` delete
- do not predict release animation before `ActiveCast` confirms the cast

### Instant Spells

For spells with `cast_time_ms == 0`:

- `COMBAT_CAST` continues to play the release animation immediately.
- `COMBAT_RELEASE` drives release VFX/projectile body cues.

### Cast-Time Spells

For spells with `cast_time_ms > 0`:

- `COMBAT_CAST` should not immediately play the release animation.
- `COMBAT_CAST` still triggers `SPELL_CAST` VFX cues.
- `ActiveCast` insert starts cast hold presentation.
- the scheduler starts release animation at `ends_at - authoredReleaseOffset`.
- `COMBAT_RELEASE` remains the authoritative gameplay release and VFX release fact.
- if `ActiveCast` is deleted before the scheduled release start, cancel hold and do not play release.

### Channels And Charged Release

Do not generalize beyond the needed cast-time projectile path in V1.

The separate `CombatSpellAnimationPhase` field is a narrow substrate for cast-time spell presentation, not permission to implement channels now.

V1 phases are only:

- hold start
- release
- cancel

Channels and charged-release spells can later add channel-specific states and rules using the same shape, but the initial implementation should focus on normal cast-time spells like `ICICLE`.

## Validation Rules

Update editor/server-adjacent validation to enforce the real contract.

Current validator problem:

`CombatVFXAuthoringValidator` compares `releaseSeconds` directly to `cast_time_ms`. That assumes the release animation starts at cast begin. That is the old broken model.

Do not make the strict validator flip before Phase 3 runtime routing exists. Phase 1 may relabel/rename fields and produce warnings, but the hard error for "release offset must fit inside cast time" should ship with the scheduler path so content cannot validate into still-broken runtime behavior.

New validation:

- selectable spell ability must have a `CombatAnimationSet.spells[]` entry keyed by `action_id`, not `ability_id`
- cast-time spell must have a release clip
- cast-time spell must have authored release timing
- `authoredReleaseOffsetSeconds <= cast_time_ms / 1000f`
- warn if `cast_time_ms - authoredReleaseOffsetSeconds` is too small to play `CastingEnter` cleanly
- error if no cast hold profile exists for a combat profile with any selectable cast-time spell
- error if a cast-time spell would still be routed as immediate `COMBAT_CAST` release animation

Recommended default tolerance:

```text
50ms
```

Validation should not silently accept impossible timings. Warning-level checks are acceptable during Phase 1 migration; Phase 3 promotion should surface impossible timing as editor validation errors and CI/play-mode validation failures, matching the existing validator's log/error reporting path.

## Implementation Phases

### Phase 1 - Document And Rename Timing Semantics — SUPERSEDED, WILL NOT BE IMPLEMENTED

**Status (2026-05-10):** This phase will not be implemented. Superseded by [`docs/animation-event-timing-migration-plan-2026-05-10.md`](animation-event-timing-migration-plan-2026-05-10.md), which deletes `groundEffectTime` / `airEffectTime` (and the other per-clip timing fields) entirely instead of renaming them. The renamed fields described below were a halfway step toward clarifying that these timings live "inside the release clip"; the event-migration plan goes the rest of the way by moving the timing data onto the clip itself as `OnReleaseFrame` (and related) animation events.

The original plan text is preserved below for historical context. Do not act on it.

---

Files:

- `CombatAnimationSet.cs`
- `CombatAnimationSetEditor.cs`
- `CombatVFXAuthoringValidator.cs`
- `SpellAuthoringWindow.cs`
- `IntimidateAnimationAuthoring.cs`
- existing `CombatAnimationSet` assets

Work:

- rename or re-label `groundEffectTime` / `airEffectTime` as release timing
- preserve serialized compatibility with `[FormerlySerializedAs]` if fields are renamed
- update tooltips to say this is the visible release point inside the release clip
- update validation messaging from "release time equals cast time" to the new release-offset model
- keep new strict cast-time/release-offset validation warning-only until Phase 3 lands

Acceptance:

- `FIREBALL` instant spell still validates and plays immediately
- `ICICLE` reports a clear warning if its release offset does not fit inside `250ms`
- no existing melee timing or melee animation tests change

### Phase 2 - Add Cast Hold Profile Authoring

Files:

- `CombatAnimationSet.cs`
- `CombatAnimationSetEditor.cs`
- `TwoHandedSword.asset`

Work:

- add a default spell cast hold profile to `CombatAnimationSet`
- assign Kevin Iglesias `CastingEnter01`, `CastingIdle01`, and optionally `CastingExit01`
- support male/female variants only if the existing avatar/profile system can resolve them cleanly; otherwise V1 may use combat-profile-specific clips and defer runtime sex switching
- keep clips referenceable from `Assets/Arena/Resources`-loaded animation sets; do not move animation assets in a way that breaks `Resources.Load` catalog resolution

Acceptance:

- the editor clearly shows cast hold clips
- missing cast hold clips produce validation errors for selectable cast-time spells
- instant spells do not require cast hold clips
- V1 does not require a combinatorial matrix of weapon profile x sex variants unless the existing avatar/profile system already exposes it cleanly

### Phase 3 - Add Cast-Time Scheduler

Files:

- new `SpellCastPresentationController.cs` or equivalent
- `EntityRegistry.cs`
- `PlayerEntity.cs`
- `CombatAnimationRequest.cs`
- `PlayerAnimator.cs`

Work:

- route `ActiveCast` insert/update/delete into the scheduler
- scheduler computes release animation start time
- scheduler issues request-based cast hold and release animation commands
- prevent immediate release animation playback from `COMBAT_CAST` for cast-time spells
- keep instant spells on the current immediate path
- gate `COMBAT_CAST` with replicated spell definition `cast_time_ms`, not `ActiveCast` callback ordering
- propagate `ActiveCast.target_id` / `aim_x` / `aim_y` / `aim_z` into scheduled release facing
- add local cast-enter/idle prediction for cast-time spells after local input validation
- add `CombatSpellAnimationPhase` or equivalent field; do not overload `CombatAnimationCategory`
- move strict validator errors for impossible release offsets from warning to error

Acceptance:

- no new spell-id switch
- no direct animator layer manipulation outside `PlayerAnimator`
- one active cast per caster is handled cleanly
- cancellation clears hold state without playing release
- `COMBAT_CAST` still triggers cast-time `SPELL_CAST` VFX cues
- scheduled release animation faces the authoritative ActiveCast aim point
- if no server clock estimate exists, release waits for `COMBAT_RELEASE` instead of falling back to immediate `COMBAT_CAST`
- local cast-time spell input starts cast hold without waiting for table round-trip

### Phase 4 - Icicle Content Pass

Files:

- `progression_catalog.shared.json`
- `TwoHandedSword.asset`
- `CombatVFXRegistry.asset`

Work:

- choose actual Icicle release clip
- author release timing inside the clip
- keep `cast_time_ms` honest
- ensure cast VFX cue duration fits the hold phase
- ensure release VFX/projectile body appears at authoritative release
- decide whether Icicle V1 includes a presentation-only predicted local projectile visual; if not, document that network latency can separate the release frame from visible projectile spawn

Acceptance:

- on cast start, character enters/holds casting pose
- release animation starts shortly before server release
- release animation reaches its authored release frame at the scheduled release time
- if predicted local projectile visual is implemented, the visible projectile leaves the hand at the authored release frame and reconciles with authoritative release
- if movement cancels the cast via gameplay, release animation and projectile do not play

### Phase 5 - Tests And Regression Coverage

Add focused tests around:

- `CombatAnimationSet` release offset calculation
- validator rejects `releaseOffset > castTime`
- `EntityRegistry` does not route cast-time `COMBAT_CAST` as immediate release animation
- `EntityRegistry` still routes cast-time `COMBAT_CAST` to `SPELL_CAST` VFX cues
- scheduler starts release at `ends_at - releaseOffset`
- scheduler cancels on `ActiveCast` delete before release
- scheduler clamps late catch-up to `200ms`
- scheduler preserves ActiveCast aim/facing for release requests
- request category remains `Spell`; spell phase is represented separately
- local cast-enter prediction reconciles when `ActiveCast` arrives
- local cast-enter prediction cancels after `PredictionConfirmTimeoutMs`
- no-clock fallback waits for `COMBAT_RELEASE`
- instant spell path remains unchanged
- melee animation request path remains unchanged

Play mode or integration tests should cover:

- local Icicle cast
- remote Icicle cast
- cast canceled by movement
- cast canceled by death/disable if supported by current gameplay
- Fireball still instant-casts
- melee and parry interactions still obey preemption rules

All gameplay cancel paths must delete the server `ActiveCast` row. Presentation trusts `ActiveCast` delete as the single cancellation signal instead of adding movement/death/disable hooks in multiple client components.

Hard CC, stagger, knockdown, and hit reaction presentation remains owned by `CombatStatusReactionController`. If CC cancels a cast, the server-side result must still be `ActiveCast` delete. The spell scheduler cancels hold/release scheduling only; the status reaction controller owns the stagger/CC clip that follows.

## Red Flags To Avoid

- Do not add an `if (spellId == "ICICLE")` timing branch.
- Do not use Unity animation events for authoritative gameplay release.
- Do not make `ActiveCast` itself spawn authoritative VFX or gameplay projectiles.
- Do not duplicate spell playback outside `PlayerAnimator`.
- Do not make the scheduler bind animator override clips directly.
- Do not let `COMBAT_CAST` and `ActiveCast` both trigger the same release animation.
- Do not rely on `ActiveCast` callback ordering to suppress `COMBAT_CAST` release animation.
- Do not encode cast hold animation clips in `progression_catalog.shared.json`.
- Do not solve impossible timing by silently speeding up clips in V1.
- Do not overload `CombatAnimationCategory` with spell phases.
- Do not play full-body stationary cast hold over locomotion unless gameplay prevents movement or the profile explicitly authors that behavior.

## Open Questions Before Implementation

1. Should release timing be stored normalized or in seconds?

   Current fields are normalized. Normalized is convenient for clip changes; seconds is clearer for validation. V1 can keep normalized for serialization compatibility, but editor UI should display computed seconds.

2. Should cast hold profiles support male/female variants immediately?

   The Kevin Iglesias package has both. If the current avatar system exposes sex/model reliably at runtime, support it. If not, prefer combat-profile-specific clips first and add sex variants as a separate clean slice.

3. Should `MagicAttack... - Load` clips be used instead of generic `CastingIdle01` for some spells?

   Not required for V1. The authoring model should allow spell-specific hold override later.

4. Should V1 include predicted local projectile visuals for cast-time spells?

   Cast-enter prediction is now part of V1. Predicted local projectile visuals are only required if Phase 4 wants the visible projectile body to leave the hand exactly at the authored release frame under network latency. If implemented, they must be presentation-only and reconciled against authoritative projectile state.

## Recommended Decision

Proceed with the orchestration layer, but keep it narrow:

- `ActiveCast` drives cast hold and release scheduling for cast-time spells.
- `PlayerAnimator` remains the only owner of actual animator playback and arbitration.
- `CombatAnimationSet` owns release timing and cast hold clips.
- `COMBAT_RELEASE` remains the authoritative gameplay/VFX release fact.
- `COMBAT_CAST` no longer directly plays the release animation for cast-time spells.

This is the cleanest way to support Icicle now while preserving the animation system and leaving room for area spells, beams, lasers, channels, and longer cast spells later.

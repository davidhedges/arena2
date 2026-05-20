# Combat Animation Latency-Aware Remote Playback Plan - 2026-05-05

## Status - Unfinished V1

V1 is implemented as a conservative presentation feature, not the full reducer-RTT clock described in the original ideal plan.

What V1 does:

- remote authoritative single-clip melee may start with a small catch-up offset
- local predicted attacks and local authoritative duplicate replay are not offset
- catch-up is capped globally and by the first hit window safety margin
- single-clip melee timing is now evaluated in played-clip seconds end-to-end
- phased melee remains unchanged
- the server clock is reset on reconnect

Important V1 limitation:

- production clock samples currently come from observed replicated server timestamps, not reducer round-trip samples
- `ArenaServerClock.RecordReducerSampleMs(...)` exists for future precise samples, but no production code calls it yet
- the lowest-RTT and snap-corroboration paths are therefore dormant in production until a non-generated SDK hook or explicit ping reducer is added

Pre-merge manual validation:

- visually retest `COMBO_ATTACK_1_1_HIGH_TO_LOW`
- visually retest `COMBO_ATTACK_1_3_GROUND_TO_AIR`
- confirm lower-body unlock and visual-interrupt timing still feel right after scaling authored thresholds onto the played clip timeline

This is mergeable as V1 only after that manual pass. If either tuned attack feels shifted, retune against the new played-clip timeline rather than restoring the old mixed timing math.

## Problem

Melee attacks already publish an authoritative `COMBAT_CAST` event before strike damage resolves. Remote observers therefore do not wait for `COMBAT_IMPACT` to start attack animation.

The remaining risk is timing drift: remote observers start the animation when their client receives the cast row, not at the server time recorded on the cast row. Under normal latency this can make long attacks begin a little late. Under worse latency, the visual windup can appear closer to the hit window than intended.

## Current Facts

- Server melee accepts an attack, inserts `CombatEvent` with `event_type = COMBAT_CAST`, and records `created_at = now`.
- Server schedules one or more `PendingMeleeImpact` or `PendingProjectileRelease` rows using authored hit window delays.
- Unity translates authoritative `COMBAT_CAST` rows into `CombatAnimationRequest.Authoritative`.
- `CombatAnimationRequest.StartedAtMs` carries the combat event timestamp in milliseconds.
- `PlayerAnimator` V1 uses `StartedAtMs` only for eligible remote authoritative single-clip melee catch-up.
- Local melee input already predicts visual playback immediately for ordinary non-gap-close melee.
- SpacetimeDB reducer results carry a server `Timestamp`, but V1 deliberately avoids editing generated binding files to access them.
- V1 estimates server time from replicated row timestamps observed in normal table callbacks.
- The main migration plan already establishes the anchoring rule for local prediction: predicted local melee phase timestamps anchor to the predicted start time, and authoritative duplicate replay must not re-anchor lower-body unlock or visual interruption.

## Goal

Make remote combat animation playback lightly server-time aware so observer clients can catch up a small amount when a cast event arrives late.

The rule should improve perceived alignment between:

- authoritative cast start
- authored attack windup
- authored hit windows
- observed impact VFX and hit reactions

## Non-Goals

- Do not change melee damage, hit windows, cooldowns, combo timing, auto-attack cadence, or projectile release timing.
- Do not make animation playback authoritative gameplay state.
- Do not synchronize exact animation poses across all clients.
- Do not offset local predicted attacks.
- Do not skip large portions of dramatic attack animations.
- Do not add a ping reducer until another feature needs reliable server time while the client is idle.

## Design

Latency-aware remote playback is a presentation-only catch-up.

When a remote client receives an authoritative animation-start request:

```text
event_age_ms = ArenaTime.ServerNowMs - request.StartedAtMs
catchup_ms = clamp(event_age_ms, 0, MaxRemoteCombatAnimationCatchupMs)
normalized_start = map_timing_reference_elapsed_to_played_clip_normalized_time(catchup_ms)
```

Then start the animation at `normalized_start` instead of `0`.

Use one canonical runtime timing basis once playback starts: played-clip seconds.

Authored hit windows, lower-body unlock, and visual interruption are authored against the combat animation set timing reference. V1 scales those authored seconds onto the played clip timeline once at presentation start:

```text
played_seconds = authored_seconds * (played_clip_length / timing_reference_length)
```

Remote catch-up also maps to Animator normalized time using the played clip length:

```text
normalized_start = catchup_seconds / played_clip_length
```

This intentionally fixes a pre-existing mixed-units inconsistency. It can move existing tuned thresholds when `played_clip_length != timing_reference_length`, so tuned attacks need a manual visual pass.

Initial tuning:

```text
MaxRemoteCombatAnimationCatchupMs = 200
```

Acceptable range after testing:

```text
150ms - 250ms
```

This clamp is the important safety rule. A 100ms late event can start 100ms into the clip. A 700ms late event should not start 700ms into the clip because that can hide the readable windup entirely.

The clamp must also respect authored contact timing. The effective maximum catch-up should be:

```text
min(MaxRemoteCombatAnimationCatchupMs, first_hit_window_ms - safety_margin_ms)
```

Use a small safety margin, for example `50ms`, so short attacks do not begin at or after the visual contact region. If the first hit window is too early to leave a positive margin, do not apply catch-up for that strike.

## Scope Rules

Apply catch-up only when all of these are true:

- `request.Authority == CombatAnimationAuthority.Authoritative`
- the animated entity is not the local player
- `request.StartedAtMs > 0`
- the client has a valid server clock estimate
- the category is `MeleeSkill` or `AutoAttack`

Initially do not apply catch-up to:

- local predicted melee
- authoritative local replay suppression
- spells
- charge
- dodge
- block/parry
- hit reactions
- death/stun/knockdown reactions

Spells and movement actions can adopt the same clock later if they get scheduled presentation issues, but melee is the motivating case.

## Phase 1 - Add A Project Server Clock

Create a small runtime service, for example:

```text
Assets/Arena/Runtime/Network/ArenaServerClock.cs
```

Responsibilities:

- store an estimated `server_minus_client_ms` offset
- expose `HasEstimate`
- expose `ServerNowMs`
- accept observed replicated server timestamp samples in V1
- keep a precise reducer-sample API available for future integration
- keep smoothing behavior intentionally simple

Timestamp units:

- SpacetimeDB `Timestamp` is microseconds since Unix epoch.
- `CombatAnimationRequest.StartedAtMs` is milliseconds since Unix epoch.
- `ArenaServerClock` should expose milliseconds to presentation code.
- Pick one input API and document it clearly, for example `RecordReducerSampleMicros(...)` for raw SDK samples or `RecordReducerSampleMs(...)` after caller conversion. Do not mix units at call sites.

Ideal precise estimator:

```text
client_midpoint_ms = (client_send_ms + client_recv_ms) / 2
sample_offset_ms = server_timestamp_ms - client_midpoint_ms
```

V1 production estimator:

```text
observed_offset_ms = server_timestamp_ms - client_receive_ms
server_minus_client_ms = max(previous_observed_offset_ms, observed_offset_ms)
```

This monotonic-max estimator is intentional for observed timestamps. A replicated row timestamp gives only a one-way observation: the client received a row created at server time `server_timestamp_ms`. The largest observed offset corresponds to the lowest observed one-way latency and is the least-late estimate available without a paired send/receive reducer sample.

Known V1 tradeoffs:

- the estimate does not decrease during a session
- there is no snap-back path if real clock offset shifts downward
- long always-on sessions can drift more than match-length sessions
- `LowestRttBiasedEstimate` and precise snap corroboration are dormant until precise samples are wired

This is acceptable for match-length V1 presentation catch-up. Add a precise sample source before using `ArenaServerClock` for higher-stakes scheduling.

Smoothing:

- maintain a small recent window, for example 16 to 32 samples
- prefer the offset from the lowest-RTT samples in the recent window
- ignore extreme RTT spikes for offset updates
- snap to a new estimate when the best recent low-RTT samples agree on an offset shift large enough to matter, for example more than `80ms`
- do not snap on one isolated high-RTT or outlier sample
- expose current RTT diagnostics for debugging, but do not couple animation behavior directly to raw RTT

Implementation note:

The SDK has the raw data, but the current project does not expose reducer request start timestamps or reducer-result timestamps as a clean public callback. Do not subscribe to every generated reducer event just to harvest timestamps unless reducer error forwarding is preserved. Generated reducer handlers only call `InternalOnUnhandledReducerError` when no typed handler exists, so adding clock-only handlers can accidentally swallow reducer errors.

Safer implementation options:

- Add a narrow SDK/project integration point that receives `(requestId, reducerName, clientSendUtc, serverTimestamp, clientRecvUtc)` when a reducer result is parsed.
- If that is too invasive for V1, add an explicit lightweight `ping_clock` reducer later and record send/receive around that call.
- Avoid editing generated binding files manually.

V1 choice:

- no generated binding file edits
- no generated reducer handler subscriptions
- no reducer-send queue keyed by generated reducer names
- use observed row timestamps only until a durable non-generated hook exists

Ideal acceptance:

- `ArenaServerClock.HasEstimate` becomes true after normal reducer traffic.
- `ArenaServerClock.ServerNowMs` advances monotonically enough for presentation use.
- Reducer failures still reach existing error logging/UI paths.
- Disconnect/reconnect clears stale estimates.

V1 acceptance:

- `ArenaServerClock.HasEstimate` becomes true after receiving a replicated timestamped row such as `PlayerPhysics` or animation-start `CombatEvent`
- `ArenaServerClock.ServerNowMs` advances monotonically enough for remote melee catch-up
- generated binding files remain untouched
- disconnect/reconnect clears stale estimates

## Phase 2 - Compute Remote Catch-Up On Animation Requests

Add a helper near combat animation presentation code, for example:

```text
CombatAnimationRemoteTiming
```

Responsibilities:

- decide whether a request is eligible for remote catch-up
- compute clamped catch-up milliseconds
- compute catch-up against the timing reference, then map it to the played clip's normalized time
- cap catch-up by the first authored hit window minus a safety margin
- emit debug trace fields when enabled

Suggested API:

```csharp
public static bool TryResolveStartNormalizedTime(
    in CombatAnimationRequest request,
    bool isLocalPlayer,
    float timingReferenceLengthSeconds,
    float playedClipLengthSeconds,
    float firstHitWindowSeconds,
    out float normalizedStart)
```

Rules:

- return false when the request is ineligible
- return false when timing reference length or played clip length is missing or too small
- return false when the first hit window is too early to leave the configured safety margin
- clamp normalized time to a conservative maximum derived from `MaxRemoteCombatAnimationCatchupMs`
- never return a value that starts at or after the authored contact region by default

Acceptance:

- Local predicted attacks still start at time `0`.
- Authoritative remote melee can start at a small non-zero normalized time.
- Missing clock estimate preserves existing behavior.
- Short early-hit melee does not catch up past its readable anticipation.

## Phase 3 - Apply To Single-Clip Melee

Update the single-clip melee path in `PlayerAnimator`.

Current path:

```text
RequestCombatAnimation
-> PlayMeleeAnimation
-> TriggerStrike
-> SetActiveMeleePresentation
```

Target behavior:

- resolve strike index
- resolve timing reference length, played clip length, and first hit window timing
- compute remote catch-up normalized time
- play the strike state at that normalized time when eligible
- seed active melee presentation elapsed-time accounting with the applied catch-up milliseconds
- keep current trigger-based behavior when ineligible

Implementation detail:

The current `TriggerStrike` helper uses animator triggers. Starting at a non-zero normalized time may require a separate path using `Animator.Play` or `Animator.CrossFadeInFixedTime` on the melee action layer. Keep this path local to remote catch-up so existing local prediction and combo handoff behavior remains stable.

Phase timers must be compensated along with visual playback. If a remote attack starts 180ms into the clip, the lower-body unlock and visual-interrupt bookkeeping must also treat the active melee presentation as already 180ms elapsed. Otherwise the attack looks caught up, but lower-body unlock and visual interruption fire late.

V1 implementation note:

- single-clip melee uses the Animator as the presentation clock
- remote catch-up starts the Animator at played-clip normalized time
- active melee timing reads Animator normalized time and multiplies by played clip length
- authored lower-body unlock and visual-interrupt thresholds are pre-scaled from timing-reference seconds into played-clip seconds at `SetActiveMeleePresentation`

Do not apply this compensation to local predicted melee or suppressed authoritative local duplicate replay. That follows the anchoring rule in `docs/archive/2026-05-stale-plans/combat-animation-migration-plan-2026-05-04.md`: predicted local melee phase timestamps anchor to the predicted start time, and authoritative duplicate replay does not re-anchor lower-body unlock or visual interruption.

Acceptance:

- Normal remote melee still plays when no catch-up is available.
- Remote catch-up starts a late event slightly into the strike clip.
- Visual interruption and lower-body unlock state still initialize correctly.
- Visual interruption and lower-body unlock elapsed timers include the same catch-up offset as the visible animation.
- Combo follow-up handoff behavior is unchanged unless explicitly tested and extended.

## Phase 4 - Apply To Phased Melee Carefully

Phased melee uses runtime segmented playback across start/loop/end clips. Do not force single-clip normalized-time logic onto it.

Preferred phased behavior:

- convert catch-up milliseconds into segmented elapsed time
- skip into the correct segment only if the skipped time is within the clamp
- if this is hard to implement cleanly, leave phased melee at existing behavior for V1

Acceptance:

- Phased melee never starts in an invalid segment.
- Lower-body unlock and visual interruption still evaluate against segmented elapsed time.
- If phased support is deferred, single-clip melee catch-up remains shippable.

## Phase 5 - Diagnostics And Tuning

Add trace output behind existing combat animation tracing:

```text
remoteCatchup eligible=true action=... eventAgeMs=... appliedMs=... normalized=... clockRttMs=...
```

Add a simple dev-only way to test:

- artificial client network delay if available
- or a debug setting that adds a local presentation delay before applying remote combat events

Tune `MaxRemoteCombatAnimationCatchupMs` with exaggerated attacks:

- one early-hit attack
- one long-windup attack
- one multi-hit attack
- one auto-attack

Acceptance:

- At ordinary latency, observers see windup start closer to the attacker.
- At high latency, observers do not skip the entire anticipation.
- Impact VFX and hit reactions do not appear before a readable attack start.

## Tests

Editor/unit-style tests where feasible:

- catch-up returns false without clock estimate
- catch-up returns false for local player
- catch-up clamps to configured maximum
- catch-up clamps negative event age to zero
- catch-up returns false when timing reference length or played clip length is zero or missing
- catch-up caps by first hit window minus safety margin
- local predicted replay suppression still suppresses duplicate local authoritative melee

Manual multiplayer checks:

- one attacker, one observer, same open-world scope
- selectable melee
- auto-attack
- combo follow-up
- gap-close melee, if the action waits for authoritative movement
- remote observer joins scope after combat has already started

Pre-merge tuned attack checks:

- `COMBO_ATTACK_1_1_HIGH_TO_LOW`: verify the `1.4s` lower-body unlock and `1.5s` visual-interrupt feel correct after played-clip scaling
- `COMBO_ATTACK_1_3_GROUND_TO_AIR`: verify the existing tuned interruption/unlock behavior still reads correctly
- If either feels off, retune the authored thresholds against the new played-clip timeline

## Rollout

1. Land `ArenaServerClock` with observed-timestamp samples only.
2. Land remote catch-up helper with tests.
3. Enable single-clip melee catch-up behind a constant or debug flag.
4. Test two-player session with normal latency.
5. Test with artificial latency.
6. Tune clamp.
7. Manually retest tuned local melee threshold proof points.
8. Decide whether phased melee needs V1 support or remains unchanged.
9. Add a precise clock source later if another feature needs it.

## Open Questions And Follow-Ups

- Should the first implementation use a small SDK integration point for reducer-result clock samples, or wait for an explicit ping reducer?
- Should catch-up be disabled for specific authored strikes with very short anticipation windows?
- Should the clamp be global, combat-profile-specific, or per-strike authoring later?
- Projectile release VFX is the likely second consumer of `ArenaServerClock` once combat VFX scheduling becomes more precise. Keep it out of V1 melee scope, but design the clock as a reusable service rather than a melee-only helper.
- Should long-running sessions periodically reset or decay observed timestamp offsets if no precise sample source is added?

## Recommendation

V1 is intentionally conservative and unfinished. It is suitable for merge after manual tuned-attack validation, but it should not be treated as the final server-time architecture.

When it is needed, implement the small version:

- project server clock
- observed timestamp samples only in production
- remote-only melee catch-up
- `200ms` global maximum skip, further capped by first hit window timing
- single-clip melee first
- phased melee only after manual evidence says it needs it
- precise reducer or ping samples later when a durable non-generated hook exists

This gives the main benefit without turning animation playback into a deterministic networking system.

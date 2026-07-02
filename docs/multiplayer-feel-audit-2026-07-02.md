# Multiplayer Feel Audit — 2026-07-02

Player-facing experience slice of the netcode: prediction, reconciliation, interpolation,
smoothing, combat feel under latency, and desync-diagnosis tooling. Companion to
`docs/netcode-sync-audit-2026-07-02.md` (architecture); that audit's findings are
referenced but not repeated.

## Implementation status (updated 2026-07-02)

- **F1 steps 1–3 — implemented.** `PredictedActionLedger` +
  `LocalCombatState.PredictActionStart` / `RollbackPrediction` /
  `ReleasePredictedPrimaryResource`; melee and spell press paths route their
  GCD/cooldown/resource predictions through the ledger, and
  `Rejected`/`StaleToken` results roll everything back (value-guarded so
  authoritative rows or later legitimate predictions are never clobbered).
  Editor tests: `Assets/Arena/Tests/Editor/PredictionRollbackLedgerTests.cs`
  (run via Unity Test Runner). Step 4 (denial cue) is a minimal hook only:
  static `LocalCombatState.PredictionRejected` event — no HUD surface yet.
  Update (2026-07-02, netcode audit R2): the hook now carries the server's
  machine-readable denial reason —
  `PredictionRejected(actionKind, ActionRejectReason)` fed from the new
  `reject_reason` field on `PredictedActionResult` (cooldown/GCD/resource/
  target/range/facing/LOS/etc.), rollback traces log it, and
  `NetcodeDebugOverlay` shows `lastReject=family:reason`. An HUD denial
  surface can now render honest text without any client-side validation.
- **F2 sub-slice (a) — implemented.** `NetcodeDebugOverlay` now shows remote
  hard-snap count, interp/extrap sample ratio, last/max remote position error
  (aggregated over remote players), predicted-action results by kind, and
  per-table row-receive rates. Server `MOVE_FALLBACK` count is in the
  `[TICK_PROFILE_SCAN]` window line — `ARENA_PROFILE_TICKS` is compile-time
  baked; see `docs/tick-baseline-recipe.md`.
- **F2 sub-slice (b) — implemented.** No-op `ping_clock` reducer
  (`server/src/ping.rs`, schema change — bindings regenerated) + a ~2 s
  sampler in `NetworkManager` that echoes its send time through the reducer
  arg and feeds `ArenaServerClock.RecordReducerSampleMicros` from the reducer
  event's server timestamp — activating the dormant precise midpoint
  estimator (RTT rejection, low-RTT banding, snap corroboration) and
  populating `LastRoundTripMs`. One estimator fix uncovered by wiring it: the
  sample ring now stores only precise samples — it is read exclusively for
  precise-sample statistics, and ~30 Hz observed-row timestamps were evicting
  the ~0.5 Hz pings, which would have permanently defeated the ≥2-sample snap
  corroboration. Overlay gains RTT last/p50/p95 + clock offset lines
  (precise vs observed-only tagged). Editor tests:
  `Assets/Arena/Tests/Editor/ArenaServerClockTests.cs` — corroborated precise
  samples override the monotonic-max estimate downward (through an
  observed-row flood), RTT > 1000 ms rejected, percentile stats. Gameplay
  reads nothing from RTT.
- **F2 sub-slice (c) — implemented.** `docs/latency-testing.md`: macOS
  `dnctl`+`pfctl` profiles (~100 ms/+30 ms jitter/1 % loss and
  ~200 ms/+60 ms/3 %) scoped to local port 3000, with setup/verify/teardown
  and what to expect in the overlay. Plus the optional dev-only
  `Arena.Debugging.NetworkCallbackDelay` (default off,
  `ARENA_CALLBACK_DELAY_MS`): FIFO deferral of binder-routed row callbacks by
  a configurable ms — presentation-side only; caveats in the doc. Not done
  from the F2 contract: connection-quality dot / disconnect banner
  (contract item 4).
- **F3, F4, F5 — not started** (F4/F5 gated on F2 measurements by design —
  the measurements now exist, so F4/F5 are unblocked).

## Executive Summary

The foundation is stronger than the planning docs suggest. Verified as implemented:

- **Tick-buffered movement protocol.** The "tick vs seq mismatch" that
  `plans/movement-netcode-followup-plan.md` calls the highest-priority problem is
  fixed: the server buffers one command per input tick (`server/src/player_input.rs:13-30`),
  consumes exactly one per server tick (`pop_command_for_tick`,
  `server/src/player_input.rs:64-81`), acks `last_processed_tick`
  (`server/src/game_loop.rs:1389`), and falls back to the latest intent when a tick's
  command is missing (`server/src/game_loop.rs:1345-1367`). The plan doc is stale on
  this point and should be updated.
- **Movement modifiers reach prediction** (plan Phase 2 done): per-tick
  `MovementContextSample` carries `MovementBlocked` / `MoveSpeedMultiplier` into
  replay (`Assets/Arena/Runtime/Input/MovementPrediction.cs:129-160`,
  `LocalMovementPredictionDriver.cs:617-653`).
- **Sim/visual separation** (plan Phase 3 done): `LocalPresentationDriver`
  smooths a presentation root with 60 ms position half-life and 2.0 m hard-snap
  (`Assets/Arena/Runtime/Presentation/LocalPlayerCamera.cs:95-160`), and the camera
  follows the smoothed root. Small corrections do not visibly snap; corrections
  ≥ 0.25 m log a warning (`LocalMovementPredictionDriver.cs:21,374-389`).
- **Remote players are properly interpolated**: 12-snapshot buffer, 66 ms render
  delay, 66 ms max velocity extrapolation, 2.0 m / 60° hard-snap, k=18 smoothing
  (`Assets/Arena/Runtime/Simulation/ClientSimulationState.cs:93-100,397-442,517-564`).
- **Combat input prediction is broad**: melee and spells immediately predict
  animation, GCD, per-spell cooldown, resource spend, and cast bar
  (`MeleeInputHandler.cs:265-287`, `SpellInputHandler.cs:626-665`,
  `LocalCombatState.cs:208-282,302-330,495-503`), with responsive cast-cancel and
  remote melee/spell catch-up (V1, 200 ms clamp) implemented.

The real feel gaps, in priority order:

1. **Server rejection is silent and leaves phantom state behind** — a rejected action
   locks the button on a cooldown the server never started, spins the GCD, and holds
   the resource bar down for up to 1.25 s, with zero player-facing feedback. (F1)
2. **The project is RTT-blind and has no latency test harness** — the precise clock
   path is dormant, no ping, no artificial latency/jitter/loss tooling, and several
   remote-presentation counters that already exist are not surfaced. (F2)
3. **NPCs snap with zero interpolation** — every `NpcPhysics` row teleports the
   transform. (F3)
4. **Remote interpolation is keyed to packet arrival time**, not server time, with a
   fixed 66 ms delay — jitter and websocket batching warp remote motion directly. (F4)
5. **Combat has RTT-shaped dead zones**: gap-closers do nothing on press until the
   server answers; melee/spell hit confirmation (damage numbers, hit reactions,
   impact VFX) always waits a full round trip; there is **no lag compensation**
   (hit validation is at server-present time) and that stance is nowhere documented. (F5)

## What Happens Under Adverse Conditions Today

- **Latency (steady).** Local movement and combat startup stay responsive
  (prediction lead up to 12 ticks ≈ 396 ms of headroom,
  `MovementNetcodeConfig.cs:20-28`; remote sends target an 8-tick input lead,
  `MovementNetcodeConfig.cs:15-16`). What degrades: hit confirmation, interrupts,
  gap-closers, and projectile spawns — all full-RTT. A staggered local player keeps
  "casting" until the server's `ActiveCast` delete arrives
  (`LocalCombatState.cs:637-656` region), then the bar vanishes without explanation.
- **Jitter.** Local prediction absorbs it (tick-buffered commands + fallback intent).
  Remote players wobble: the interpolation buffer indexes on `ReceivedTime`
  (`PlayerSnapshot.cs:34`, `ClientSimulationState.cs:411,545-546`), so bursty
  delivery compresses/stretches perceived motion; gaps > 66 ms hit the extrapolation
  cap, then the k=18 smoother pulls back. Counters for this exist
  (`ClientSimulationState.cs:152-159`) but are only partially surfaced.
- **Packet loss / late input.** Server advances the tick using the last intent and
  logs `[MOVE_FALLBACK]` at debug level (`game_loop.rs:1358-1363`); the client later
  replays with real inputs → correction. There is no counter for fallback frequency,
  so loss is invisible in any overlay. Emergency resync clears the command buffer at
  > 12 pending (`MovementNetDriver.cs:80-93`).
- **Reconnect.** `NetworkManager` resets clock, entity cache, match and combat state
  (`NetworkManager.cs:193-220,374-382,404-406`) but there is **no auto-reconnect and
  no player-facing disconnect UI** — the world freezes and buttons gray out. Manual
  reconnect exists only on the dev-only F8 overlay (`NetworkEnvironmentOverlay.cs:65`).
- **Module republish.** Client experiences a disconnect (above). Server-side stale
  transient rows on reconnect are covered by the architecture audit (R1); the client
  side additionally shows nothing to the player about why they dropped.

## Prediction Coverage Matrix

| Local action | Predicted immediately | Waits for server |
|---|---|---|
| Run / strafe / jump | position, velocity, yaw, grounded (full replay) | — |
| Root / slow / speed buffs | respected in replay once context row arrives | onset of the CC itself (1 RTT) |
| Gap-closer / dash / special movement | nothing (`MeleeInputHandler.cs:258-263`) | movement + animation + VFX (1 RTT) |
| Knockback / forced movement | nothing | arrives as a correction |
| Melee strike | animation, GCD, cooldown, resource (`MeleeInputHandler.cs:265-287`) | hit result, damage numbers, target reaction (1 RTT+) |
| Instant spell | animation, release VFX, GCD, cooldown, resource (`SpellInputHandler.cs:654-665`) | impact, projectile spawn row (1 RTT) |
| Cast-time spell | cast bar, cast-hold animation, resource, cooldowns (`SpellInputHandler.cs:626-637`) | release, effects; interrupt presentation (1 RTT) |
| Cast cancel | bar suppression immediate (`LocalCombatState.cs:505-529`) | cancel-too-late verdict |
| Projectile flight | not predicted; spawned from `ProjectilePresentationEvent` rows (`CombatProjectileVisualController.cs:102-152`) | everything |
| Being hit / stagger | nothing | reaction + cast interruption (1 RTT) |

---

## 1. Top Multiplayer-Feel Improvements

### F1 — Roll back *all* predicted side effects on rejection, and tell the player

**Classification: combat feedback improvement** (highest-value fix in this audit)

**Repo evidence.**
On `Rejected`/`StaleToken`, the handlers clear only the cast bar and pending-visual
token:

- `LocalCombatState.OnPredictedActionResultInsert` clears `_predictedCastBar` and
  `_currentCastToken` only (`Assets/Arena/Runtime/Simulation/LocalCombatState.cs:559-565`).
- `MeleeInputHandler.OnPredictedActionResultInsert` only does
  `_pendingPredictedMeleeByToken.Remove(tokenKey)` (`MeleeInputHandler.cs:528-531`).
- `SpellInputHandler.OnPredictedActionResultInsert` only does
  `_pendingInstantSpellByToken.Remove(tokenKey)` (`SpellInputHandler.cs:720`).

Meanwhile the press path predicted much more:

- Per-spell cooldown written into `_spellCds` (`LocalCombatState.cs:268-282`) — the
  same dictionary authoritative rows land in. A rejected cast never produces a
  server `SpellCooldown` row, so **nothing ever removes the phantom entry**; the
  action bar shows the full cooldown.
- GCD predicted (`LocalCombatState.cs:208-219`); `ClearPredictedGlobalCooldown` is
  called only from the voluntary self-cancel path (`LocalPlayerMotor.cs:226`) —
  never on rejection.
- Resource reservation held until a 1250 ms timeout
  (`PredictedResourceSpendTimeoutMs`, `LocalCombatState.cs:114,329`) because
  reconciliation only releases it when the server's resource actually drops
  (`LocalCombatState.cs:362-368`) — which never happens for a rejected action.
- No sound, flash, toast, or reason anywhere.

**Player-facing symptom.** Rejections happen precisely in the latency races that
matter: you press a spell in the same instant the server staggers/silences/roots you,
or client/server validation drifts (the commit `115393b9` bug class). The player
experiences: button pressed → swing/cast animation starts → silently fizzles → **the
ability is now unusable for its full cooldown and mana appears spent** → "this game
eats my inputs." This converts an occasional mispredict (acceptable) into a
multi-second penalty plus confusion (not acceptable).

**Better contract.** Every predicted side effect is recorded on the action token, and
`Rejected`/`StaleToken` restores all of it atomically: remove/restore the
`_spellCds` entry (restore the pre-prediction value if one existed), clear predicted
GCD if this token set it, release the predicted resource reservation immediately, and
fire one player-facing denial cue (icon shake / brief red flash + sound). Prediction
stays optimistic; rejection becomes cheap and legible.

**Why it improves feel.** Mispredicts become a ~RTT-long blip instead of a
cooldown-long punishment. Denial feedback closes the "did my input register?" loop,
which is the single strongest subjective marker of responsive netcode.

**Bounded slice for a smaller model.**
1. Add a `PredictedActionLedger` struct captured at press time (per token):
   `{ gcdSetByThisToken, cooldownKind, priorCooldownEntry?, reservedResourceCost }`.
   Store in the existing pending dictionaries in `MeleeInputHandler` /
   `SpellInputHandler` (they already key by token).
2. Add `LocalCombatState.RollbackPrediction(in PredictedActionLedger)` that restores
   `_spellCds`, clears predicted GCD (only if the authoritative GCD row hasn't since
   arrived — compare against the last authoritative `GlobalCooldown` values), and
   zeroes the matching portion of `_predictedPrimaryResourceSpend`.
3. Call it from the three rejection sites above.
4. Add one denial cue hook (a static event the HUD subscribes to; a simple icon
   flash is enough for the slice).
   Client-only; zero schema changes; no binding regen.

**Files/surfaces.** `LocalCombatState.cs`, `MeleeInputHandler.cs`,
`SpellInputHandler.cs`, one small HUD/action-bar surface for the cue.

**Tests / runtime scenarios.** Editor tests: predict → reject → assert `_spellCds`
has no phantom entry, GCD inactive, effective resource equals server value; predict →
accept → assert ledger discarded without touching authoritative rows; reject arriving
*after* an authoritative cooldown row for the same spell (later legitimate cast) must
not clear the authoritative entry. Runtime: force rejections (cast at 0 resource with
the client pre-check bypassed; cast during a server-side stagger) and verify the
button is immediately reusable and the cue fires.

**Instrumentation.** Count `PredictedActionResult` rows by result kind per session;
show `accepted/rejected/canceled` in `NetcodeDebugOverlay`. A rising rejected count
is the early-warning signal for validation drift.

**Risks / non-goals.** Do not let rollback touch authoritative rows; only
prediction-sourced state. Do not build a second deny/sync channel — this composes
with the architecture audit's R2 (reason codes on `PredictedActionResult`), which
supplies the *message*; this slice supplies the *rollback and cue* and ships first
without any schema change.

---

### F2 — Wire real RTT sampling, surface the existing counters, and stand up a latency test harness

**Classification: instrumentation before tuning** (gates F4 and all smoothing work)

**Repo evidence.**
- `ArenaServerClock.RecordReducerSampleMs` (precise midpoint estimator, RTT
  rejection, low-RTT banding, snap corroboration —
  `Assets/Arena/Runtime/Network/ArenaServerClock.cs:46-72,133-173`) is **never called
  in production**; only the one-way monotonic-max estimator runs
  (`ArenaServerClock.cs:75-87,123-130`), which by design never decreases during a
  session. The design doc flags this as the dormant path
  (`docs/combat-animation-latency-aware-remote-playback-plan-2026-05-05.md`).
- `LastRoundTripMs` exists (`ArenaServerClock.cs:31`) but is never populated.
- No artificial latency/jitter/loss tooling anywhere in the client, and SpacetimeDB
  SDK 2.0.4 exposes none.
- Counters that exist but are not (fully) shown: remote hard-snap count,
  interpolation vs extrapolation sample counts, last/max remote position error
  (`ClientSimulationState.cs:152-159`); server `[MOVE_FALLBACK]` events have no
  counter at all (`game_loop.rs:1358-1363`).
- No player-facing connection indicator; no disconnect banner
  (`NetworkManager.cs:374-382` just resets state; UI polls `IsConnected`).

**Player-facing symptom / risk.** "It feels laggy" is currently undiagnosable: you
cannot see RTT, cannot see how often remote presentation is extrapolating or
snapping, cannot see input-loss rate, and cannot reproduce any of it locally. Every
smoothing/tuning change made in this state is guesswork — the exact failure mode the
movement plan warns about ("tuning more smoothing knobs before fixing the contract").

**Better contract.**
1. A `ping_clock` reducer (no-op server-side, returns via normal reducer result);
   client records send/receive around it every ~2 s and feeds
   `RecordReducerSampleMs`, activating the already-written low-RTT/snap logic.
2. `NetcodeDebugOverlay` gains: RTT (last/p50/p95), clock offset, remote
   extrapolation ratio, remote hard-snap count, predicted-action results by kind,
   and a server-fallback-intent counter (server: count `MOVE_FALLBACK` per profile
   window in the existing `ARENA_PROFILE_TICKS` line — log-only).
3. A documented latency recipe: macOS Network Link Conditioner / `dnctl`+`pfctl`
   profiles (100 ms/30 ms jitter/1% loss, 200 ms/60 ms/3%) checked into
   `docs/` with step-by-step usage against the local SpacetimeDB endpoint. Optional
   dev-only client hook: a delay queue wrapper that defers row-callback dispatch by a
   configurable ms (presentation-side latency simulation without touching the SDK).
4. A small always-on connection-quality dot + disconnect banner with a reconnect
   button (promote the F8 overlay's reconnect action to production UI).

**Why it improves feel.** Indirectly but decisively: it converts every subsequent
feel complaint into a measurable, reproducible case, and the ping reducer improves
remote combat catch-up accuracy (its clamp math currently rests on the conservative
one-way clock).

**Bounded slice for a smaller model.** Do it in three independent sub-slices:
(a) overlay lines for existing counters (pure client, trivial);
(b) `ping_clock` reducer + client sampler (one reducer added — schema change, so
follow the regen command in `MEMORY`/checklist; server side is ~5 lines);
(c) the latency-recipe doc + optional callback-delay debug utility.

**Files/surfaces.** `ArenaServerClock.cs`, `NetcodeDebugOverlay.cs`,
`NetworkManager.cs` (sampler + banner state), `server/src/lib.rs` (or a small
`ping.rs`), regenerated bindings, one HUD element, new `docs/latency-testing.md`.

**Tests / runtime scenarios.** Clock unit tests already exist conceptually in the
plan doc's list — add: precise samples override monotonic-max estimate downward;
RTT > 1000 ms rejected. Runtime: run the conditioner profiles and confirm RTT/extrap
ratio move as expected; pull the network cable and confirm the banner appears.

**Instrumentation.** This *is* the instrumentation. Non-goal: acting on the numbers
in the same slice.

**Risks / non-goals.** Keep the ping cadence low (unmetered reducer spam is
replication churn — the architecture audit's R3 counters will show it). Do not couple
gameplay behavior to raw RTT. Do not edit generated bindings by hand.

---### F3 — Interpolate NPCs like remote players

**Classification: remote-player presentation improvement**

**Repo evidence.** `NpcEntity.ApplyPhysics` teleports the transform on every row:
`GameObject.transform.SetPositionAndRotation(nextPosition, ...)`
(`Assets/Arena/Runtime/Entity/NpcEntity.cs:116-140`). No buffer, no smoothing, no
render delay; locomotion speed is derived from raw row-to-row deltas. Remote
*players* get the full `ClientSimulationState` stack
(`ClientSimulationState.cs:397-442`).

**Player-facing symptom.** NPCs stutter-step at row-arrival cadence and freeze
between updates; under jitter or websocket batching they visibly teleport in small
hops. In melee range — where the camera is close and the player is orbiting the
target — this is the most visible motion artifact in the game, and it also makes
NPC-derived locomotion animation jumpy.

**Better contract.** NPCs use the same snapshot-buffer presentation as remote
players: push `NpcPhysics` rows into a per-NPC buffer, render at `now − delay`,
smooth toward the target, hard-snap on large error. Velocity is not replicated for
NPCs, so extrapolation is position-hold (or last-delta) rather than velocity-based.

**Why it improves feel.** Smooth, continuous enemy motion is a prerequisite for
melee combat feel: players time swings against target movement, and stuttering
targets make range/timing judgments feel random.

**Bounded slice for a smaller model.**
1. Extract the remote-presentation core of `ClientSimulationState` (snapshot ring,
   `SampleRemoteRenderTarget`, smoothing/snap in `Tick`) into a small reusable
   `RemotePresentationBuffer` class, keeping `ClientSimulationState` delegating to
   it (no behavior change for players).
2. Give `NpcEntity` one instance; `ApplyPhysics` pushes snapshots instead of setting
   the transform; a per-frame tick (from the existing NPC update path) applies the
   render pose and feeds locomotion speed from the *rendered* delta.
3. Reuse the same constants initially (66 ms delay, 2.0 m snap); expose the same
   debug counters.

**Files/surfaces.** `ClientSimulationState.cs` (extraction), new
`Assets/Arena/Runtime/Simulation/RemotePresentationBuffer.cs`, `NpcEntity.cs`,
`EntityRegistry.cs` (NPC row routing), optionally `NetcodeDebugOverlay.cs`.

**Tests / runtime scenarios.** Editor test on the extracted buffer (pure math:
interpolation between two snapshots, extrapolation cap, snap threshold — reuse
whatever covers players today, else add now). Runtime: walk an NPC patrol route with
the conditioner at 100 ms/30 ms jitter; verify no per-row hops; verify hit-reaction
and death animations still align with position (they key off state rows, not
transforms).

**Instrumentation.** Same counters as players (hard snaps, extrapolation ratio) per
NPC aggregate in the overlay.

**Risks / non-goals.** Server-side NPC hit validation uses server positions, so the
added ~66 ms render delay does not change gameplay — but melee range *pre-checks*
against NPC transforms (client advisory) will now see slightly older positions;
acceptable and consistent with remote players. Non-goals: NPC velocity replication,
navigation prediction, changing `NpcPhysics` cadence.

---

### F4 — Key remote interpolation to server time with an adaptive delay

**Classification: reconciliation/smoothing improvement — gated on F2's measurements**

**Repo evidence.** The remote timeline is client-arrival-based:
`renderTime = Time.realtimeSinceStartup - RemoteInterpolationDelaySeconds`
(`ClientSimulationState.cs:411`) sampled against `PlayerSnapshot.ReceivedTime`
(`PlayerSnapshot.cs:34`, comparisons at `ClientSimulationState.cs:541-546`), with a
fixed 66 ms delay (`ClientSimulationState.cs:96`). The rows already carry what a
server-time buffer needs — `LastProcessedTick` (33 ms grid) and `UpdatedAt` — and
`ArenaServerClock` already estimates server-now, but neither is used for
presentation timing. Under a delivery gap > 66 ms, sampling falls into
velocity-extrapolation capped at 66 ms (`ClientSimulationState.cs:554-563`), then
converges back via the k=18 smoother — i.e., jitter becomes visible speed
modulation instead of added delay.

**Player-facing symptom.** Remote players subtly speed up/slow down ("swimmy"
motion) whenever delivery cadence varies — which with SpacetimeDB transaction-batch
delivery is the normal case, not the exception. Under real WAN jitter, alternating
extrapolate/correct cycles read as micro-rubber-banding on other players, exactly
what strafing opponents in melee makes most visible.

**Better contract.** Buffer snapshots on the server-tick timeline
(`LastProcessedTick × 33 ms`, or `UpdatedAt`), render at
`ArenaServerClock.ServerNowMs − adaptiveDelay`, where `adaptiveDelay` tracks a
high percentile (e.g., p95 + half a tick) of observed arrival lateness within a
sliding window, bounded to [66 ms, 200 ms]. Jitter then costs a little more fixed
delay instead of visible motion warping.

**Why it improves feel.** Constant small delay is imperceptible; time-warped motion
is not. This is the standard snapshot-interpolation contract (source-style) and the
codebase is one field away from it — the data is already replicated.

**Bounded slice for a smaller model.**
1. In `PlayerSnapshot`, add `ServerTimeMs` (from `LastProcessedTick × 33` — prefer the
   tick: it's jitter-free) alongside `ReceivedTime`.
2. In `SampleRemoteRenderTarget`, switch comparisons to `ServerTimeMs` against a
   render time of `ArenaServerClock.ServerNowMs − delayMs`; keep the arrival-time
   path as fallback while `!ArenaServerClock.HasEstimate`.
3. Start with **fixed** 100 ms delay behind a debug toggle next to the old path;
   make the delay adaptive only after F2's overlay proves the win (extrapolation
   ratio ↓, hard snaps ↓).
   Client-only; no schema change.

**Files/surfaces.** `PlayerSnapshot.cs`, `ClientSimulationState.cs`,
`EntityRegistry.cs` (snapshot construction), `NetcodeDebugOverlay.cs` (A/B toggle +
counters). If F3 landed, the change lives once in `RemotePresentationBuffer`.

**Tests / runtime scenarios.** Editor tests: feed synthetic snapshots with bursty
arrival times but uniform server ticks → server-time path produces uniform sampled
motion, arrival-time path does not. Runtime A/B under conditioner profiles: compare
extrapolation ratio and hard-snap counts old vs new; verify remote melee catch-up
still aligns (it shares `ArenaServerClock`).

**Instrumentation.** Requires F2 first: extrapolation ratio, hard snaps, and RTT in
the overlay are the before/after evidence. Add "buffer depth in ticks" as a line.

**Risks / non-goals.** The monotonic-max clock can be off by the one-way jitter
floor — acceptable for presentation, but do this after F2's precise samples land to
avoid tuning against a biased clock. Do not change the local-player path, special
movement track sampling (`ClientSimulationState.cs:401-409`), or send rates.
Non-goal: adaptive *send*-side rates.

---

### F5 — Close the combat dead zones: predicted gap-closer startup and predicted contact cues; document the no-lag-comp stance

**Classification: combat feedback improvement** (sub-part explicitly speculative)

**Repo evidence.**
- Gap-closers deliberately skip all local presentation:
  `"melee gap close awaiting authoritative movement+animation"`
  (`MeleeInputHandler.cs:258-263`); special movement is server-sampled only
  (`LocalMovementPredictionDriver.cs:254-305`).
- Attacker hit feedback is fully authoritative: damage numbers from
  `CombatEffectEvent` (`FloatingCombatText.cs:43-66`), impact VFX/hit reactions from
  `CombatEvent` inserts (`CombatVFXDispatcher.cs:331-377`); nothing is predicted at
  the authored hit-window moment.
- No lag compensation: melee impact resolution uses present-time snapshots
  (`server/src/melee.rs:3874` onward, range check ~`4063-4072`), projectiles collide
  at present-time positions (`server/src/combat/projectiles.rs:81-299`). No doc
  states this is intentional.

**Player-facing symptom.** (a) Under 100 ms+ RTT a gap-closer button is dead for a
full round trip — the single most noticeable input-response failure in the current
design, since every other action predicts *something*. (b) Melee feels floaty: the
blade passes through the target and the thud/number arrives ~RTT+impact-delay later.
(c) Because hits validate at present time, higher-latency attackers whiff on moving
targets more than their screen suggests — an implicit design decision nobody wrote
down, so future contributors may "fix" it accidentally.

**Better contract.**
1. *Gap-closer startup prediction (implement):* on press, immediately play the
   authored windup animation and startup VFX as a predicted presentation (same
   pattern as predicted melee), while movement remains fully server-owned; when the
   authoritative `SpecialMovementRuntime` row arrives, the already-playing windup
   hands off to track sampling (suppress the duplicate authoritative start, as
   predicted melee already does via its accepted-token replay suppression).
2. *Predicted contact cues (implement, cosmetic-only):* at the authored first hit
   window of a predicted local melee, if the client-side advisory hit test passes
   against current rendered target positions, play a light contact layer — spark
   flash, weapon sound, small hitstop ≤ 50 ms. Damage numbers, health, and target
   reactions remain 100 % authoritative. On a server whiff the player sees a light
   contact cue but no number — tunable down if it reads as a lie.
3. *No-lag-comp stance (document now):* add the explicit statement to
   `docs/combat-authoring-contract.md` (or a new netcode contract doc): hit
   validation is server-present-time by design; rewind-based lag compensation is
   **speculative redesign, do not implement yet** — it requires historical position
   buffers server-side and a fairness decision, and it invalidates the
   target's-eye-view ("shot behind the wall" class tradeoffs).

**Why it improves feel.** (1) removes the only remaining dead button; (2) restores
the contact-moment feedback loop that makes melee read as connected — the
authoritative confirmation then arrives as reinforcement (number) rather than as the
only signal; (3) prevents accidental fairness regressions.

**Bounded slice for a smaller model.** Ship (1) alone first: it reuses the existing
predicted-melee request path (`CombatAnimationRequest.PredictedMeleeSkill`) and
replay-suppression bookkeeping; the only new logic is "predicted presentation without
predicted movement" plus handoff-on-row-arrival. CLAUDE.md constraint applies:
the playback change goes through `CombatAnimationSet` data / the shared playback
substrate — do not add new machinery to `PlayerAnimator`. (2) is a second slice
gated behind a debug flag and a kill-switch constant. (3) is a doc edit.

**Files/surfaces.** `MeleeInputHandler.cs` (gap-close branch),
`CombatAnimationRequest`/playback substrate, `CombatVFXDispatcher.cs` (predicted
contact cue + suppression of the duplicate authoritative cue via the existing
accepted-token maps), `docs/combat-authoring-contract.md`.

**Tests / runtime scenarios.** Editor: handoff math (predicted windup elapsed →
track sampling start offset); suppression: authoritative start after predicted start
does not double-play. Runtime under 150 ms conditioner: press gap-closer — windup is
instant, dash starts ~RTT later without a pose pop; melee vs strafing target — count
contact-cue-but-no-damage occurrences per 100 swings (tune or gate on that number).

**Instrumentation.** Log predicted-contact false-positive rate (cue fired, no
matching authoritative impact within 500 ms — the accepted-token map gives the
correlation); surface count in overlay.

**Risks / non-goals.** Contact cues can misreport during target death/immunity races
— keep them subtle and instantly killable via flag. Rejection of the gap-close must
roll back the windup (compose with F1's ledger). Non-goals: predicting dash
*movement*, lag-compensated rewind, projectile client-side flight prediction (revisit
after F2 data; classified speculative).

---

## 2. Safest First Slice

**F1, step 1-3 (rollback ledger), plus F2 sub-slice (a) (overlay lines for existing
counters).** Both are client-only, zero schema change, zero binding regen, no
gameplay-authority changes, and each is independently verifiable in the editor:

1. `PredictedActionLedger` + `RollbackPrediction` + three call sites
   (`LocalCombatState.cs`, `MeleeInputHandler.cs`, `SpellInputHandler.cs`), with the
   editor tests listed under F1. Fixes the worst live feel bug (phantom cooldown /
   held resource after rejection).
2. `NetcodeDebugOverlay` lines for: remote hard-snap count, interp/extrap sample
   ratio, last/max remote position error (already computed in
   `ClientSimulationState.cs:152-159`), and predicted-action results by kind (needed
   to observe F1 working).

Defer the denial *cue* (F1 step 4) to a follow-up if HUD surface work is considered
risky; the rollback alone is already the correctness win.

## 3. Latency/Jitter Test Plan

Environment: local SpacetimeDB (`ws://localhost:3000`) with macOS Network Link
Conditioner (or `dnctl`/`pfctl`) shaping the client. Two clients (editor + dev
build), one NPC-populated scene. Toggle overlays: `\` netcode, F8 environment.

Profiles (run every scenario at each):

| Profile | RTT | Jitter | Loss |
|---|---|---|---|
| Baseline | ~0 | 0 | 0 |
| Regional | 60 ms | 10 ms | 0 % |
| Cross-region | 120 ms | 30 ms | 1 % |
| Bad WiFi | 200 ms | 60 ms | 3 % |
| Spike | 120 ms steady + 5 s bursts of +300 ms | — | — |

Scenarios and pass criteria:

1. **Straight-line + strafe run (local).** No alternating smooth/vibrate windows;
   correction error stays < 0.25 m (no `Large correction` warnings) outside of
   loss bursts; resync count 0 at ≤ 120 ms.
2. **Combat movement (local).** Get rooted/slowed while running: correction spike at
   CC onset only (one correction ≈ RTT × speed, then stable — context rows are in
   replay, so no churn afterward).
3. **Remote observation.** Observer watches runner orbiting an NPC: no hard snaps at
   ≤ 120 ms (overlay counter), extrapolation ratio < 10 % at Regional, no visible
   speed pulsing (F4's before/after metric).
4. **Melee exchange.** Attacker swings at strafing target: measure press→animation
   (must be ~0), press→damage-number (expect ≈ RTT + authored impact delay — record
   as baseline), observer sees windup catch-up (≤ 200 ms clamp) with impact aligned.
5. **Rejection race.** Target staggers the caster exactly during cast press
   (scripted or practiced): before F1 — observe phantom cooldown + held resource;
   after F1 — button reusable immediately, cue fires, overlay rejected-count
   increments.
6. **Cast + interrupt.** Time from server-side interrupt to local cast-bar removal ≈
   one-way latency; bar never completes visually after an interrupt.
7. **Gap-closer.** Press→any visible response: currently ≈ RTT (record); after F5(1)
   ≈ 0 with dash starting ≈ RTT later, no pose pop at handoff.
8. **Loss burst.** At 3 % loss: `MOVE_FALLBACK` counter (F2) rises, movement stays
   playable, corrections stay < 2 m (no presentation hard snaps).
9. **Reconnect / republish.** Kill the server process mid-combat; republish via
   `ops/` script; client should show the disconnect state (post-F2 banner), reconnect
   cleanly, and exhibit no stale cast bar / cooldown carryover (architecture audit
   R1 verification doubles here).
10. **Spike profile.** During the +300 ms bursts: remote players add delay, not
    warp (post-F4); local prediction lead grows toward 12 ticks then recovers
    without emergency resync more than once per burst.

Record per run: RTT p50/p95, correction error last/max, replay depth max, resync
count, remote extrap ratio, hard snaps, rejected actions, `MOVE_FALLBACK` count.

## 4. Prediction/Reconciliation Review Checklist

For any PR touching prediction, presentation, or combat feedback:

- [ ] Every new predicted side effect (cooldown, GCD, resource, bar, animation, VFX)
      is recorded on the action token's ledger and restored on
      `Rejected`/`StaleToken`/`CancelTooLate` as appropriate — no phantom state can
      outlive a rejection.
- [ ] Prediction never *denies* an action the server would allow (advisory
      pre-checks only) and never mutates authoritative caches, only
      prediction-layer state alongside them (architecture audit R2 rule).
- [ ] Predicted presentation and its authoritative duplicate are correlated by
      token/action-instance id, and the duplicate is suppressed — never played twice,
      never re-anchored (the anchoring rule in the latency-aware playback plan).
- [ ] Any new server row consumed by local replay is versioned/tick-stamped and
      reachable during rewind (`GetMovementContextForTick` pattern); replay fallback
      to defaults is counted, not silent.
- [ ] Corrections route through a smoothing layer (`LocalPresentationDriver` /
      remote buffer) with an explicit hard-snap threshold; no code writes corrected
      positions directly to a visible transform.
- [ ] Remote/NPC presentation goes through the shared snapshot buffer; no direct
      `transform.SetPositionAndRotation` from a row callback.
- [ ] Presentation timing uses `ArenaServerClock` (server timeline), not
      `Time.realtimeSinceStartup` arrival times, wherever a server timestamp exists.
- [ ] Timing-sensitive constants (interp delay, catch-up clamps, snap thresholds,
      prediction TTLs) are named constants with a comment stating the tick/RTT
      assumption they encode.
- [ ] The change was exercised under at least the Cross-region conditioner profile,
      and the PR cites overlay numbers (correction error, extrap ratio, hard snaps,
      rejected count) before/after.
- [ ] Hit validation timing semantics unchanged (server-present-time; no accidental
      rewind or client-authoritative hit claims); if intentionally changed, the
      contract doc changes in the same PR.
- [ ] CLAUDE.md ownership holds: no new `PlayerAnimator` responsibilities;
      hit-reaction presentation in `CombatStatusReactionController`; combat dispatch
      through action-bar resolution.
- [ ] New per-identity transient tables added to the unified teardown (architecture
      audit R1); schema changes regenerate bindings with the canonical command in
      the same commit.

## Classification Summary

| Recommendation | Classification |
|---|---|
| F1 rejection rollback + denial cue | combat feedback improvement |
| F2 RTT/ping, counters, latency harness, disconnect banner | instrumentation before tuning |
| F3 NPC interpolation | remote-player presentation improvement |
| F4 server-time-keyed adaptive remote interpolation | reconciliation/smoothing improvement (gated on F2) |
| F5(1) predicted gap-closer startup | responsiveness improvement |
| F5(2) predicted contact cues | combat feedback improvement (flag-gated) |
| F5(3) document no-lag-comp stance | combat feedback improvement (doc-only) |
| Rewind-based lag compensation | speculative redesign, do not implement yet |
| Client-side projectile flight prediction | speculative redesign, do not implement yet |
| Local prediction of dash/special movement trajectories | speculative redesign, do not implement yet |

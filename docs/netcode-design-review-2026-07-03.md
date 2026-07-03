# Adversarial Netcode & Combat-Validation Design Review — 2026-07-03

Companion to `docs/netcode-sync-audit-2026-07-02.md` (architecture) and
`docs/multiplayer-feel-audit-2026-07-02.md` (feel). Those audits verified the
implementation against its own spec. This review does the opposite, per the
owner's directive: **critique the design against the best possible system for
this game**, treating what is in place today as evidence, not constraint.
Everything cited was verified in code this week; live observations come from
the 2026-07-03 shaped-latency runs.

Ratings: **[DEFECT]** = wrong contract, fix; **[GAP]** = missing contract,
design one; **[ACCEPTED]** = right call today, keep deliberately;
**[REVERSAL]** = this review overturns a stance a prior audit recorded.

---

## 1. Victim-side hit timing — the worst contract in the game [DEFECT]

**What exists.** All hit validation reads server-present-time positions
(`PlayerSnapshotSet::collect`, `player_snapshot.rs:75-117`); there is no
positional history anywhere server-side. NPC attacks have **zero telegraph**:
when the cadence timer fires and the target is in reach, the CAST event and
the IMPACT/damage resolve in the same tick (`npcs.rs:575-578, 828-868`).
Meanwhile every victim renders attackers 100–166 ms in the past
(66–100 ms interpolation delay + half RTT). Parry/block windows are matched
against hit delivery time with a 50 ms grace (`defense.rs:22-26`).

**Why it's wrong.** The attacker's experience got a full prediction stack
(F1/F5); the victim's experience is *structurally unfixable by the client*:
you are hit by an enemy your screen shows out of reach ("mob hits me from far
away", observed live), you have at most one tick of reaction to an NPC swing,
and you must time a 50 ms-grace parry against a world you see ~150 ms late.
Defense timing windows below the render-delay floor are noise, not skill.

**Target contract.**
1. **Telegraphs first (server + data, no netcode).** Every NPC attack and
   auto-attack gets an authored windup between CAST and damage —
   ≥ 300–400 ms for NPCs, the authored `impact_delay_ms` the melee manifest
   already models for players. The CAST event then *arrives before the hit*,
   which is the industry-standard mask for present-time validation. This is
   the cheapest, largest victim-feel win available.
2. **Favor-the-defender on defense windows.** Widen parry/block success
   grace from 50 ms toward `interpolation delay + RTT p50/2`, capped
   ~150 ms. Never resolve a defense window against state the defender could
   not yet have seen.
3. **Bounded lag compensation, designed now, shipped gated [REVERSAL].**
   The feel audit recorded "rewind lag compensation: speculative redesign, do
   not implement." Keep it out of the next slice, but stop treating it as
   forbidden: a 16-tick (~530 ms) ring of `(pos, yaw)` per combat-relevant
   entity is trivial storage at 30 Hz, and reach/facing/LOS checks for
   *attacks* validated at the attacker's view time (clamped ≤ ~250 ms) is the
   standard fairness contract for this genre. Rewind attacks, never defenses;
   never rewind through dashes/teleports/invulns. Design doc + kill-switch
   before any tuning work that depends on "present-time forever."

## 2. Line of sight is a policy accident [DEFECT]

**What exists.** A competent multi-probe 2D LOS query exists
(`scene_query.rs:68-170`). It is applied as a **per-delivery opt-in flag**:
8/8 projectile strikes require it; **0/55 non-projectile melee strikes, zero
gap-closers, and non-projectile auto-attacks never check LOS**; targeted
spells check at cast; nothing re-checks at impact. A gap-closer will
teleport/dash to a target the caster cannot see (observed live — the
behind-wall press was *accepted*).

**Why it's wrong.** LOS is currently a property of *how damage travels*, not
*whether you can act on a target* — so the player-facing rule is illegible:
the same wall blocks your bow but not your charge. The one honest signal the
server sends (`LineOfSightBlocked`) is unreachable for most of the kit.

**Target contract.** LOS is a **targeting** rule, not a delivery rule. One
authored flag per action, `requires_target_los`, default **true** for every
hostile targeted action (melee, gap-close, spell, auto-attack), opt-out only
by explicit design (e.g., a homing ultimate). Gap-close validates LOS *and*
path, with distinct reject reasons. No impact-time re-check (document: dodging
behind cover after launch is legitimate counterplay). Client side, add an
*advisory* pre-check using the already-bundled shared collision data (the
client ships `gameplay_query_collision.shared.json` — the same geometry the
server raycasts), so illegal presses gray out or deny instantly with the same
reason text; server stays authoritative.

## 3. Rejection presentation lies to the player [DEFECT]

**What exists.** On a server rejection: the predicted melee swing **plays to
completion** (no visual interrupt anywhere in
`MeleeInputHandler.OnPredictedActionResultInsert`), a rejected gap-close
plays the **end segment** — a completed-looking swing
(`RequestSpecialMovementDrivenPhasedMeleeEnd`), and a rejected cast's
animation is never cut. The primitives to do better already exist unused:
`CombatActionPlaybackController.CancelPhasedMelee` (416-434),
`PlayerAnimator.CancelPhasedMeleePlayback` (layer snap-to-empty, 2365-2378),
and the `DecideVisualInterrupt` policy tree. The denial toast works but the
`PredictionRejected` event carries no slot identity, so the action bar cannot
flash the offending button.

**Target contract.** **Reject = interrupt, never completion.** Within
~100 ms of a rejection: cut the predicted animation via the existing
interrupt/empty-state primitives (composed in `CombatActionPlaybackController`
— no new `PlayerAnimator` machinery, per repo standard), flash the specific
action-bar slot (extend `PredictionRejected` payload with slot/action id),
keep the toast. A denied action must *read* as denied: wind-down or flinch,
not a follow-through swing. (The gap-close "jump-press shows a full swing
then a toast" observed live is this defect plus §5's disputed rule stacked.)

## 4. Latency adaptation is binary, and its clock is the wrong one [DEFECT]

**What exists.** Movement input lead is keyed to endpoint **kind** — 8 ticks
remote, 2 ticks local/custom — with no RTT measurement anywhere in the loop
(`MovementNetDriver.ResolveDesiredServerInputLeadTicks`). The server-tick
estimate is anchored to row *arrival* (`EstimateAuthoritativeTick`), lagging
truth by the downstream one-way delay, while a precise midpoint clock
(`ArenaServerClock`, F2b) sits unused by movement. The fallback path eats
jumps (`step_intent.jump = false`). The F4 server-time timeline engages the
moment any clock estimate exists — observed live starting a session at
**−26 ticks buffer depth** (extrapolation storm) while the estimate
converged. Remote combat animations fast-forward at most 200 ms
(`CombatAnimationRemoteTiming.MaxRemoteCatchupSeconds`) and are permanently
late beyond that.

**Consequences.** Local play tolerates ~0 added RTT before command
starvation (verified live: unplayable at +40/+40 ms); real players above
~200–230 ms RTT hit the identical cliff with no degradation curve; every
remote player pays the full 264 ms server-sim lag regardless of actual RTT.

**Target contract.**
- **RTT-adaptive input lead**, bounded [2..10] ticks, driven by measured ack
  lag percentile (p95 + 1 tick), sticky (adapt on sustained change, not
  jitter). Everyone gets the smallest lead their connection affords; nobody
  falls off a cliff. Update the pinned constants test
  (`MovementRegressionTests` #33) deliberately when this lands.
- **One clock.** Movement tick estimation re-anchors on the precise
  `ArenaServerClock` estimate when available (arrival-anchored as fallback).
- **Warmup gating.** The server-time presentation timeline (and any adaptive
  delay) engages only after `HasPreciseSample` and a non-negative measured
  buffer depth — never during clock convergence.
- **Don't eat jumps:** buffer an unconsumed jump flag one extra tick
  server-side instead of clearing it on fallback.
- **Dev lead override** (env var) so shaped-local testing can exercise the
  remote path — removes the harness blind spot recorded 2026-07-03.

## 5. Aerial gating — disputed rule, badly served by its own presentation [GAP]

`GROUNDED_ONLY` is authored on **every** strike since the initial import;
the owner has ruled the restriction (at minimum on gap-closers) disputed, and
live testing showed its edge is timing-dependent (the grounded flag at
validation time — a short hop often lands before validation). Decision needed
per archetype, not a global default: gap-closers plausibly
`GROUNDED_OR_AIRBORNE` (dash math already server-owned), most strikes
whichever the movement fantasy demands. Whatever the ruling, §3's contract
applies: a rejected mid-air press must read as *denied*, not as a swing.

## 6. Auto-attacks are second-class citizens [GAP]

Auto-attacks are fully server-driven: the client only arms a preference; the
swing animation arrives with the CAST event ~RTT late; nothing predicts, no
contact cues fire (confirmed live — the falsePos test silently measured
nothing). But the client *knows the schedule*: `AutoAttackState.next_swing_at`
is a replicated row, and `ArenaServerClock` can convert it to client time.

**Target contract.** Schedule the swing presentation locally at
`next_swing_at` (server time), consume the authoritative CAST as a duplicate
(the exact suppression pattern F5 slice 1 built), and route the swing through
the same advisory contact-cue path as predicted melee. Auto-attacks then feel
identical to skills at any RTT, for near-zero new machinery.

## 7. Replication has no idle semantics, so the instrumentation lies [DEFECT]

`NpcPhysics` rows stop when an NPC is stationary (`npcs.rs:766, 790-797`);
there is no heartbeat. The presentation buffer counts every starved frame as
"extrapolation" forever and its depth metric dives (observed live: −241
ticks on an idle kobold), which **confounded the F4 A/B** — the counters
cannot distinguish "target stopped" from "delivery is late."

**Target contract.** Give the buffer a third state: **settled** (last row
older than the extrapolation cap *and* last velocity ≈ 0 ⇒ the entity is
authoritatively at rest, not being extrapolated). Count fresh / interpolated /
extrapolating-starved / settled separately in the counters, overlay, and CSV
logger. Optionally add a low-rate (~1 Hz) NPC heartbeat so "settled" has a
bounded staleness proof. This is the hard prerequisite for rerunning the F4
A/B and for any adaptive-delay tuning.

## 8. Deliberate exposures to keep (for now, on the record) [ACCEPTED]

- **Interest management is bandwidth, not security.** Subscriptions filter by
  world/instance scope only — no distance or visibility filtering
  (`GameplaySubscriptionPlanner`), so a modified client can read every
  in-scope position through walls. Acceptable pre-launch; unacceptable for
  competitive integrity later. Direction when it matters: server-side
  distance/relevance filtering per subscriber; note SpacetimeDB's two-table
  semijoin limits will shape the design.
- **TCP transport (SpacetimeDB websocket).** Loss = stalls, not drops; the
  resync backstop is the right shape. Not worth fighting until the platform
  offers alternatives.
- **Instant cast fizzle on stagger** (no interrupt grace): fine *once*
  telegraphs exist and rejection presentation (§3) makes the fizzle legible.
- **No impact-time LOS re-check**: legitimate counterplay; document it in
  the combat authoring contract.

---

## Migration slices, in order

Each slice is bounded, independently shippable, and lands with editor tests
plus the evidence instrumentation to judge it. Order chosen so measurement
precedes tuning and feel wins precede fairness work.

| # | Slice | Surface | Unblocks |
|---|-------|---------|----------|
| S1 | Idle-aware sample taxonomy (settled vs starved) in `RemotePresentationBuffer`, overlay, CSV logger | client | honest F4 A/B rerun |
| S2 | Rejection presentation: cut-on-reject via existing interrupt primitives + slot flash (extend `PredictionRejected` payload) | client | legible denials (also fixes §5's worst symptom) |
| S3 | NPC/auto-attack telegraphs: authored windup between CAST and damage | server + data | victim reaction time; masks present-time validation |
| S4 | LOS unification: `requires_target_los` targeting flag (default on, per-action opt-out), gap-close = LOS + path, client advisory pre-check from bundled collision | server + data + client | legible targeting rules |
| S5 | Clock unification + warmup gating + RTT-adaptive input lead + jump-preserving fallback + dev lead override | client + server touch | high-RTT playability; shaped-local testability |
| S6 | Auto-attack local swing scheduling off `next_swing_at` + contact-cue parity | client | auto-attack feel at RTT |
| S7 | F4 adaptive delay [66..200 ms] from arrival-lateness p95 | client | needs S1 + clean A/B |
| S8 | Bounded lag-compensation ring for attack reach/facing + favor-the-defender grace — design doc first, kill-switched | server | end-state hit fairness |

Owner decisions needed before their slices: aerial gating ruling per
archetype (§5, gates part of S2's test matrix), telegraph durations (S3,
authored data), LOS opt-out list (S4).

## Stances this review reverses or reframes

1. Feel-audit F5: "lag compensation — do not implement" → **design now, ship
   gated later** (S8); present-time-forever was never re-examined after the
   victim-side evidence arrived.
2. Feel-audit F3 non-goal "no `NpcPhysics` cadence change" → idle semantics
   (S1) and optional heartbeat are now in scope; the non-goal protected
   bandwidth, not correctness of measurement.
3. The netcode audits treated per-delivery LOS and `GROUNDED_ONLY` defaults
   as design intent; the owner has ruled them unexamined defaults —
   recorded here as defects/gaps, not spec.

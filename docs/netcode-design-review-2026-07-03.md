# Adversarial Netcode & Combat-Validation Design Review — 2026-07-03

> **📌 CURRENT STATUS (2026-07-05) → see [`docs/netcode-open-items.md`](netcode-open-items.md).**
> S1–S9 delivered + accepted. **S10 (sweep rewind) is implemented, server-half
> verified, and ships default ON** (owner call 2026-07-05 on the strength of the
> live probe PASS; the shaped +40/+40 A/B is **deferred as an optional
> spot-check**, not a gate — revert via `set_lag_comp_config true 250 true false`).
> Next slice S11 (deferred defense resolution) is gated on S9's `[DEFENSE_LATE]`
> telemetry. Aerial gating (§5), projectile-impact rewind, per-victim sweep
> delay, RTT estimation, and interest management remain deferred. The open-items
> doc is the live board.

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

**Delivered (2026-07-03, S3 — target contract item 1, NPC scope).** Owner
scope ruling first: item 1 as written covered player auto-attacks too; the
owner rescoped S3 to **NPC attacks only** — player attacks (auto-attacks
included) are untouched, and their same-tick CAST→damage stays open (natural
home: alongside S6's auto-attack scheduling work). Every NPC attack — the
cadence swing in `npcs.rs` is the only NPC attack path; `auto_attack.rs` has
zero NPC references — now telegraphs: CAST is emitted at swing start, and
damage resolves through a private `npc_pending_swing` row `attack_windup_ms`
later. Durations are authored per template and owner-signed (scaled-with-
threat table): Thief 350 ms, Warrior 450 ms, Spearman 500 ms (2.40 m reach —
biggest threat bubble), Knight 600 ms. The cadence anchor stays at swing
start, so swing rhythm and DPS are unchanged. Resolution re-validates
against present-time state (validation semantics untouched, as scoped): the
swing **cancels** when the NPC is despawned, dead, or disabled at impact
time (hard CC mid-windup interrupts the hit) and **whiffs silently** —
player-melee parity — when the target is dead, unharmable, in another world
context, or outside authored reach (owner-signed strict re-check: stepping
out during the windup is the dodge counterplay; a retarget mid-windup
replaces, i.e. cancels, the in-flight swing). Defense is now judged at
impact time instead of cast time, so the 50 ms parry/block grace is timed
against a hit the victim watched wind up — widening that grace stays
item 2. The CAST carries the authored windup in the existing
`MELEE_RELEASE_DELAY_SECONDS` scalar (the same contract player-melee CASTs
use); the only schema change is the private table (bindings regenerated —
one new type stub, `NpcPendingSwing.g.cs`). Client: zero changes, path
verified — `EntityRegistry.OnCombatEventInsert` plays the NPC swing on CAST
arrival and the victim flinch/damage keys off IMPACT, so the server-side gap
reads as on-screen windup automatically. Measurement flags compose:
`ARENA_NPC_NO_ATTACK` still means zero swings (gates before CAST),
`ARENA_NPC_HARMLESS` still means real swings with damage-0 IMPACTs,
`ARENA_NPC_AGGRO_RADIUS` untouched. Evidence: CAST and IMPACT now carry
genuinely distinct `created_at_micros` under one `action_instance_id`, and
`ops/npc-telegraph-separation.py` prints per-swing CAST→damage separation
(plus whiff/cancel counts) from the live 20 s combat-event window.
Verified live 2026-07-04: a headless websocket player probe measured
462.9–479.2 ms separation over 11 warrior swings (authored 450 ms plus tick
rounding), and the owner confirmed the on-screen checklist — windup visibly
precedes every hit at baseline and under downstream-only shaping, and
stepping out during the windup whiffs. (The probe run also caught and fixed
an evidence-script bug: it had filtered `event_type` on the Rust constant
names instead of the `COMBAT_*` wire values and reported empty during live
combat.)

**Delivered (2026-07-04, S8 — target contract items 2 + 3; owner acceptance
recorded same day, slice closed, default ON).** Design doc first per the
slice contract:
`docs/lag-compensation-design-2026-07-04.md`, rulings D1–D5 owner-signed
same day (all-targeted press scope; impact re-check rewinds by the frozen
press view delay; 250 ms cap; grace widening only — deferred resolution
declined and recorded as accepted exposure; v1 exclusions confirmed).
Principle as shipped: *the rewind decides whether an attack connects; the
present decides everything about how it resolves* — health, status, defense
state, damage, and event positions never rewind. Server: a 16-slot private
`combat_position_history` ring per entity (written from the
`commit_player_physics` choke point and the `NpcPhysics` commit sites;
unchanged poses skip, so idle entities cost nothing), rewind barriers
stamped by non-Normal physics commits (dash/teleport/respawn — rewinds
clamp to them, and an *active* special movement forces present-time), and a
runtime kill switch (`combat_lag_comp_config` + `set_lag_comp_config`,
default OFF, history and audit always on). The press's claimed
`view_server_time_ms` (new arg on `melee_attack`/`cast_request`, 0 = no
report) is clamped to ≤ 250 ms and stamped in a per-caster row keyed by the
press transaction's timestamp — so exactly the checks in the press
transaction rewind, and completions, sustains, queued combo releases, and
server-initiated auto swings stay present-time by construction. The whole
melee positional gate (facing, range, minimum range, LOS endpoints) was
extracted to one pose-parameterized evaluation; with a report present it
runs twice (present + rewound) and logs a `[LAG_COMP]` dual-verdict line —
the flip rate is the audit's money metric and works with the switch OFF.
Spell/charge press sites overlay the rewound pose on their resolved target
(`overlay_press_rewound_target_pose`, hostile-only). The melee impact
re-check rewinds by the press-frozen delay stored on the pending impact row
(D2); sweeps and projectiles stay present-time (D5). Client: presses report
`ServerNowMs − target.PresentationEffectiveDelayMs` (new
`ICombatTargetEntity` member; the S7 budget on the server-time timeline,
interp delay otherwise) via `AttackerViewTime`, 0 without a precise clock.
Defense: `PARRY_SUCCESS_GRACE_MS`/`BLOCK_SUCCESS_GRACE_MS` 50 → 150 ms
(§1 item 2, D4). Evidence: `ops/s8-lag-comp-probe.py` (self-verifying,
two measurement builds) + `ops/analyze-s8-lag-comp.py` ([LAG_COMP] audit
summary); server half verified live 2026-07-04 on a throwaway DB — see the
slice table row for numbers.

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

**Delivered (2026-07-04, S4).** Owner decision first: the opt-out list is
**empty** — every hostile targeted action requires target LOS. The flag is
authored at gameplay level in the progression catalog (absent = true; no
`false` is authored anywhere, so the shared JSONs and their contract hashes
are unchanged) and lands as one new column on `MeleeAbilityCatalog`,
`AutoAttackCatalog`, `AutoAttackReplacementCatalog`, and `SpellDefinition`
(bindings regenerated, canonical bin-path mode). Server: the check lives in
the melee targeted-validation chain (`melee.rs`, after range/minimum-range,
before gap-close resolution) so it covers all target-requiring strikes,
combo follow-ups, gap-closers, and intrinsic/replacement auto-attacks with
one gate — a behind-wall gap-close press now reads `LineOfSightBlocked`
while a clear-sight blocked dash stays `GapCloseBlocked` (both reasons
already existed on the wire; zero reject-enum changes). The auto-attack tick
holds a due swing against a no-LOS target exactly like out-of-range (silent,
retries, resumes when LOS returns). The projectile-delivery
`requires_initial_line_of_sight` flag is superseded — still parsed so
manifests stay valid, never consulted. Spells already checked LOS
unconditionally at validation; those sites (tracking projectiles,
InstantBeam, Electrocute press + channel sustain, targeted
apply/remove/consume-status, movement deliveries) now gate on the authored
flag, default true — behavior identical until a spell opts out by design.
Caster cone/radius sweeps require no target and are untouched, as are NPC
swings (S3 scope). Client: the debug guide's server-collision mirror was
extracted to `Arena.Combat.ServerLosCollisionData` (production, all builds)
and `AdvisoryTargetLineOfSight` replays the exact server probe layout
(caster 85 % height → target 75 %/60 % center + ±side points) against the
bundled geometry. Strictness direction is enforced by construction: missing
collision data reads as clear, and a probe only counts blocked when its hit
stops ≥ 0.25 m short of the target point — the advisory may false-allow,
never false-block, and every press it allows still gets the authoritative
check. Denied presses ride the S2 presentation untouched
(`LocalCombatState.NotifyLocalAdvisoryDenial` → same toast + slot flash;
nothing was predicted, so nothing to cut), and action-bar slots whose action
requires target LOS dim from a 0.15 s-cached verdict while the selected
target is out of sight (cooldown/GCD overlays keep precedence). Evidence:
`ops/action-reject-reasons.py` groups live `predicted_action_result` rows by
(family, result, reject_reason) — enum cells matched on the live
`(camelCaseTag = ())` sql rendering, not constant names — and
`ops/s4-los-probe.py` is a headless websocket player that walks flush
against the nearest wall (Giant_Skeleton skull; the only scene with authored
query-collision geometry today), spawns a hostile playground dummy through
it, and presses. Verified live 2026-07-04 on a throwaway DB, all checks
green: control melee press Accepted in the open; behind-wall WARRIOR_MAIM
press → `rejected/lineOfSightBlocked`; armed auto-attack at 2.50 m emitted
zero CASTs for 5 s behind cover (vs CAST+IMPACT flowing in the open); 17.8 m
WARRIOR_CHARGE press → `rejected/lineOfSightBlocked`, no dash. Tests
deferred until the contract stabilizes (churn ruling).

**S4 post-delivery fix + ruling (2026-07-04, owner-reported).** Standing
adjacent to a target with no barrier read "No line of sight". Root cause:
the client advisory raycast the wrong world — `ForSceneName` silently falls
back to the default profile for unknown scene names, so in arena/practice
scenes the advisory tested oasis terrain at local coordinates and denied
every targeted press instantly. The advisory (and the LOS debug guide) now
key off the local player's server-side `PlayerWorld` row and run only in
authored open-world scenes with an exact profile match; everywhere else
they stay silent and the server is the only LOS authority (arena walls
still reject server-side with the S2 presentation, just without the
pre-press gray-out). Ruling recorded in the authoring contract: **bodies
never block target LOS** — LOS is caster→target versus world geometry only
(already true server-side; the S4 check inherited it). Player/NPC bodies
still intercept projectile *travel* by delivery design, and never
intercept gap-close dashes (the path bake is world-collision only).
Point-blank swings against a faced target are always in policy: with
correct world data there is no geometry between touching capsules at torso
height.

**S4 near-wall fix (2026-07-04, owner-reported).** A dummy merely near a
wall read "No line of sight" from every direction. Two causes, two fixes:
(1) playground dummies spawned with zero collision resolution — always
exactly 2.5 m ahead — so a dummy near an obstacle could sit inside its
padded movement box (movement boxes are authored fatter than visuals;
trees: 0.8×1.4 m movement vs 0.2×0.2 m LOS box), burying every probe
endpoint in geometry; dummy spawns now resolve through
`resolve_world_spawn_position_with_layout_for_scene` like NPC/practice
spawns, standing them beside walls (or on top of walkable bones) the way a
real player would end up. (2) The LOS clear rule now tolerates hits within
the **target's personal space**: a probe that reaches within
`target.hit_radius` of its endpoint sees the target — geometry the target
legally stands against cannot conceal them; cover blocks only when it
interposes deeper than the target's radius (client advisory mirrors this
plus its permissive margin). Design consequences, deliberate: melee-range
(≤3 m) LOS rejects between legally-placed actors are now effectively
impossible — a wall deep enough to defeat the tolerance can't fit inside
melee reach — so LOS bites on long strikes, gap-closers, spells, and bow
range, which is the genre-standard shape; thin props (tree trunks) never
block LOS because the ±side probes clear around them. Verified live
2026-07-04 (probe rerun): dummy beside the skull wall at 2.50 m →
melee **Accepted** (previously LineOfSightBlocked); control and wire
checks green; the through-wall charge reject stays covered by the earlier
live run (blocking mechanism unchanged). **Open ruling for the owner:**
the LOS raycast still includes fat movement boxes alongside the tight
query set, so wide props can block sight beyond their visual (a tree's
movement box blocks a sight line its authored LOS box would not). The
clean contract is query-geometry-only LOS — but the playground arena has
zero authored query geometry today, so that switch would stop arena walls
from blocking LOS until arena query boxes are authored.

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

**Delivered (2026-07-03, S2).** Client-only, zero schema change. A `Rejected`
result now cuts the predicted presentation on arrival: plain and phased melee
strikes, gap-close windups (the forced end segment is gone from the reject
path — `RollbackPredictedGapCloseWindup`'s end request now serves only the
5 s no-answer timeout), and instant spells on both the full-body layer and
the moving-cast upper-body/left-gesture overlay (the overlay path previously
recorded no identity — `ActiveOverlaySpellPresentation` in the playback
substrate now carries it, self-validated against the layer's current state
hash at cut time). Cast-time holds were already cut by
`LocalSpellPresentationStateMachine`'s `RequestCancel`; verified, not
changed. Composition per repo standard: the identity gate is the pure
`CombatActionPlaybackController.ShouldCutRejectedActionPresentation`, the
denial reaction is owned by
`CombatStatusReactionController.TriggerPredictionRejected` (an authored
flinch/wind-down clip hooks in there later), and `PlayerAnimator` only
coordinates via the existing preemption/empty-state primitives
(`PreemptMeleeAnimationIfActive` / `CancelPhasedMeleePlayback` /
`ClearActiveSpellPresentation`), mirroring the stagger-clear wiring. Scoping
rules: a rejection only cuts a presentation still attributable to the
rejected action id; `StaleToken` never cuts (a newer press of the same
action owns the presentation — the same exclusion the spell cast state
machine already used); a live authoritative special movement skips the cut
entirely (its row delete owns its presentation end). Slot flash:
`PredictionRejected` now carries `(actionKind, pressedActionId, reason)` —
`pressedActionId` is what the bar shows (combo follow-ups resolve to the
follow-up strike id; the ledger records the pressed opener) — and
`HUDController` flashes every visible slot whose resolved action matches
(ability grid, spellbook row, discipline bar; 0.45 s red fade over the icon,
presentation-only). Toast unchanged. Tests deferred until the contract
stabilizes (churn ruling); note the §5 aerial ruling gates *which presses
reject* in the eventual test matrix, not this presentation contract.

## 4. Local input prediction under latency — why delayed play is unplayable [DEFECT]

This is the section that answers "why does any added delay feel horrible."
The short version: the input pipeline has **no feedback loop anywhere** — a
fixed lead guesses, a lagging clock aims the guess, the server silently
papers over every miss, and the correction presentation converts the
resulting steady error stream into continuous elastic yanking.

**Every knob in the pipeline, with a verdict**
(`MovementNetcodeConfig.cs`, `MovementNetDriver.cs`,
`LocalMovementPredictionDriver.cs`, `game_loop.rs`):

| Knob / behavior | Today | Verdict |
|---|---|---|
| `FixedTickMilliseconds` | 33 (30 Hz) | Sound. Genre-standard; not a contributor. |
| `DesiredServerInputLeadTicks` (local/custom) | 2 (~66 ms) | Wrong **in kind**, not just in value. No static number is correct — the correct lead is a function of measured delivery that changes mid-session. As a static value it's also too thin: one editor hitch + frame alignment consumes it even on loopback. |
| `RemoteDesiredServerInputLeadTicks` | 8 (~264 ms) | Doubly wrong: a permanent ~8-tick tax on every good connection (your server-side character lags your intent by ~264 ms even at 20 ms RTT — felt by everyone *else*, and by combat validation) and a hard cliff above ~230 ms RTT. The endpoint-kind switch should not exist. |
| `MaxPredictionLeadTicks` = 12 + emergency resync | clear history, re-anchor | The bound is fine; the **response** is wrong — discarding history teleports the player. Degrade before discarding. |
| `MaxTicksToSendPerFrame` / `MaxLocalPredictionTicksPerFrame` = 5 | burst caps | Fine. |
| `MaxPendingCommands` = 96 | history bound | Irrelevant in practice. |
| Missing-command fallback | hold last intent, force `jump = false` | Acceptable as a *rare* event; under any added delay it is the **steady state** (observed live: every tick). Eating jumps is a plain bug — buffer an unconsumed jump one extra tick. |
| Server-tick estimate | arrival-anchored elapsed time (`EstimateAuthoritativeTick`) | Wrong clock: biased low by the downstream one-way delay, wobbled by delivery jitter, while the precise `ArenaServerClock` midpoint estimate (F2b) sits unused by movement. |
| Correction presentation | warn ≥ 0.25 m, hard snap ≥ 2.0 m, ~60 ms position half-life | Tuned for *rare* mispredicts. Under a steady error stream, a 60 ms half-life turns every 30 Hz reconcile into a visible yank — this is the literal texture of the "horrible" shaped-play feel. |

**The failure chain, end to end** (verified live at +40/+40 ms): added delay
pushes commands past their tick (thin lead + low-biased estimate) → server
falls back to stale intent **every tick** → authoritative rows disagree with
prediction by 0.3–0.5 m at 30 Hz → every reconcile replays and drags the
presentation through the 60 ms half-life → continuous elastic rubberbanding,
plus vanished jumps. No single value is "suboptimal" — the *architecture*
(open-loop guess + silent fallback) is the defect, which is why retuning
constants cannot fix it.

**Target design: a closed feedback loop, not bigger constants.** The
best-known contract for tick-buffered input (the Overwatch model, adapted to
SpacetimeDB):

1. **Server tells the truth per tick.** The server already knows, at consume
   time, whether it popped a real command or fell back, and how many commands
   were buffered. Publish both on the existing ack surface (two small fields
   beside `last_processed_tick` — the one schema change this needs).
2. **Client holds a setpoint.** A control loop keeps server buffer occupancy
   at ~1–2 commands: starvation raises the lead immediately; surplus lowers
   it slowly (asymmetric on purpose). Equivalently implementable as ±few-%
   input-clock scaling. RTT steps, jitter, and TCP stalls become smooth lead
   adjustments instead of cliffs.
3. **No endpoint-kind switch, no magic numbers.** Local and remote run the
   identical loop: loopback converges to ~2 ticks, a 250 ms connection
   converges to what it needs, and everyone pays the *minimum* server-sim lag
   their connection affords — which also shrinks the attacker-lag input to
   §1's fairness problem. The shaped-local harness becomes representative
   automatically (this deletes the 2026-07-03 caveat and the dev-override
   idea — the loop subsumes both).
4. **Degradation ladder.** Starvation → raise lead (invisible locally);
   sustained overrun of the 12-tick bound → throttle input production;
   genuine state divergence → one honest hard resync. Today the pipeline has
   only the last rung.
5. **Correction presentation spends a budget.** Reconcile errors below a
   threshold (~0.3 m) decay at a capped rate (cm/s) instead of half-life
   pulls; larger errors snap once, honestly. All presentation numbers here
   are starting points to be tuned against S1's instrumentation, not
   authored truth.

**Related clock/timeline fixes.** Movement tick estimation re-anchors on the
precise `ArenaServerClock` estimate (arrival-anchored only as fallback); the
F4 server-time timeline engages only after `HasPreciseSample` and a
non-negative measured buffer depth (observed live: it currently engages
during clock convergence — a session started at −26 ticks depth in an
extrapolation storm); remote combat animation catch-up stays clamped at
200 ms (`CombatAnimationRemoteTiming`) but should key off the same clock.

**Delivered (2026-07-04, S5 — all five parts plus the clock fixes; remote
combat animation catch-up deliberately untouched).**

*Server truth (part 1, the one schema change).* `PlayerPhysics` gains
`last_tick_consumed_command` and `buffered_command_count` beside
`last_processed_tick` — set at both consume sites (`tick_player` and the
special-movement tick) from the actual pop result and the post-consume
per-identity `player_command` count; respawn reports a clean state so its
queue wipe never reads as starvation. Bindings regenerated in canonical
bin-path mode; only the `PlayerPhysics` stubs changed.

*Client setpoint loop (part 2).* New `Arena.Input.InputLeadController`:
every ack tick's consume truth is drained per-tick (a 64-deep ack ring in
`ClientSimulationState` survives multi-row frames) and steered against a
buffer-occupancy setpoint of 1–2. A send-covered ack below the setpoint
floor raises the lead one tick immediately (rate-limited to one raise per
250 ms so a stale burst after a stall counts once); occupancy above the
ceiling must persist 5 s before lowering one tick — asymmetric on purpose.
Lead is bounded [1..10], under the 12-tick resync bound. Actuation lives in
the authoring clock (`LocalMovementPredictionDriver`): inject up to 2 extra
ticks per frame / skip one slot per 0.5 s toward
target = precise-estimate + lead, with a 1.5-tick dead band. The send gate
is deleted — authoring pace is the actuator, and every authored command is
sent immediately (burst caps unchanged).

*No endpoint switch (part 3).* `DesiredServerInputLeadTicks` /
`RemoteDesiredServerInputLeadTicks` and both
`ResolveDesiredServerInputLeadTicks` copies are gone; everyone starts at 2
ticks and converges to what their delivery needs. A load-bearing find while
deleting it: every command-numbering reset re-anchored at `ackTick + 1` —
*behind* the server's consume point by the full downstream delay — so any
special-movement end (dash, knockback) or resync at real RTT re-entered a
permanently-late regime (100 % fallback, wedged until the next reset; at
loopback it self-healed in 2 ticks, which is why nobody saw it unshaped).
All re-anchors now land *forward*: estimate + lead, above the
session-monotonic `HighestAuthoredTick` (the receive cursor never accepts a
reused tick number) and past the server's post-special-movement discard
window, with controller feedback suppressed below the anchor.

*Degradation ladder (part 4).* Rung 1: starvation raises lead (above).
Rung 2: overrunning the 12-tick bound throttles input production (the
existing authoring bound, now the *response* instead of a trigger).
Rung 3: hard resync fires only when the overrun is sustained ≥ 3 s (acks
stopped) — not instantly — and re-anchors forward. Jump preservation: a
late command whose jump missed its tick by less than one consume
(`input_tick == last_processed_tick`) is re-buffered for the next tick in
`send_movement_intent` (`[MOVE_JUMP_SLIDE]`, info-level), merged if the slot
is already buffered (upsert rule — no duplicate-tick rows) and gated by the
receive cursor so the post-special-movement discard window stays
authoritative; staler jumps still drop honestly.

*Correction budget (part 5).* `LocalPresentationDriver`'s 60 ms position
half-life is gone (it also dragged the sprint camera ~0.6 m behind the sim
root). Reconcile displacements are reported by the prediction driver:
< 0.3 m is absorbed into a presentation offset that decays at a capped
0.5 m/s; ≥ 0.3 m snaps once, honestly. Rotation smoothing unchanged. Both
numbers are starting points to tune against the CSV columns.

*Clock fixes.* `EstimateAuthoritativeTick` anchors the newest snapshot's own
server stamp against `ArenaServerClock.ServerNowMs` once a precise sample
exists (immune to downstream bias and delivery jitter); arrival-anchored
only before that or when the precise estimate is insane against the stamp.
The F4 server-time timeline (players and NPCs) now requires
`HasPreciseSample` *and* a ±1 s buffer-depth sanity window — the
session-start extrapolation storm can no longer engage it.

*Evidence.* Overlay: lead/occupancy/fallback-ratio/actuation/jump-ledger
lines under the pending-commands block. CSV: 17 `s5_*` columns on the S1
logger (now also logging solo sessions), summarized by
`ops/analyze-s5-input-loop.py` — fallback rate per ack tick, lead
trajectory/convergence, occupancy distribution, correction-budget spend,
eaten-jump count, estimate-source mix. A client-side jump-delivery ledger
pairs predicted jumps with authoritative grounded-drop edges (slide window
included) and logs each loss. Server half verified live 2026-07-04
(`ops/s5-input-loop-probe.py` on a throwaway DB, all green): idle ticks
report fallback truthfully; an estimate-gated stream consumes 16/16 sampled
acks with occupancy in band; a 6-ahead burst reads back as exactly 6; 30
boundary jumps at the estimated consume tick produced 25 `MOVE_JUMP_SLIDE`
lines with every slide landing an authoritative jump, while all 10
3-ticks-stale controls stayed dropped; no duplicate queue rows under
concurrent stream + late jumps. The client half (lead convergence at
loopback ≈ 2, fallback rare-event at +40/+40 up+down, zero eaten jumps, no
elastic rubberbanding) is the owner acceptance run in
`docs/latency-testing.md` — S5 makes full bidirectional shaping the
intended harness rather than a known artifact.

**S5 follow-up (2026-07-04, owner acceptance run 1): tick cadence was the
last open-loop knob.** The first acceptance session passed the headline
criteria (jumps 160/160 confirmed, 0 lost; fallback 1.04 %/tick; zero
resyncs, zero correction snaps, reconcile error flat zero) but failed the
setpoint: buffer occupancy sat at median 9 against the 1–2 target with the
skip actuator pegged at its 2/s rate cap (741 skips / 379 s) and lead
lowers doing nothing. Root cause, measured live: the game loop's
`ScheduleAt::Interval` row is host-scheduled **fixed-delay**, so execution
and scheduling overhead leak into the period — real cadence
**36.59 ms/tick (27.33 ticks/s)** against the authored 33 ms. That both
dilated the whole simulation ~10 % against wall clock (dt stays 33 ms) and
fed the client's 33 ms wall-clock authoring a permanent ~3 tick/s surplus
that no rate-capped skip could drain; server-side input depth pinned at the
12-tick prediction bound (~400 ms) even at loopback, hidden locally by
prediction. Two fixes, same slice:

1. *Server fixed-rate chain.* `game_tick` now re-inserts a one-shot
   `ScheduleAt::Time` row anchored on **its own scheduled time** + 33 ms
   (never "now + 33 ms"), so overhead cannot accumulate; a stall deeper
   than a 3-tick catch-up cap re-anchors to now, drops the backlog, and
   logs (dt = 33 ms stays honest; deep stalls dilate once, visibly). A 1 Hz
   host-managed Interval watchdog re-seeds the chain if a rolled-back or
   panicked tick ever takes its next link down with it; the legacy Interval
   row migrates on first fire after republish.
2. *Client target-chasing authoring.* The first cut's paced-33 ms clock
   with rate-capped inject/skip actuation is gone: the authoring clock now
   authors input tick N when estimate + lead crosses N, which paces command
   production at the server's *measured* consume cadence (the review's
   "±few-% input-clock scaling", and the shape the probe's estimate-gated
   sender had already validated at occupancy 1–5). Lead raises burst a
   tick, lead lowers pause until the target catches up, and the inject/skip
   rate constants are deleted; the 33 ms wall accumulator survives only as
   the pre-first-snapshot fallback.

Verified live 2026-07-04: cadence 36.59 → **32.98 ms/tick** on a throwaway
DB and **32.99 ms/tick** on the republished dev DB (30.3 ticks/s, exact);
`ops/s5-input-loop-probe.py` all green against the fixed-rate module (22
slides, all landing); chain + watchdog rows confirmed live, and the
catch-up guard observed re-anchoring through module-startup stalls instead
of fast-forwarding.

**Owner acceptance recorded (2026-07-04, run 2, one session per leg — S5
CLOSED).** Baseline loopback (183 s moving + jumping): ack rate 30.7/s,
occupancy median 2 (min 0 / max 4), lead converged 3 (range 2–5 — editor
hitches bite the thin-margin regime occasionally and the loop absorbs
them), fallback 0.82 %/tick, 105/105 jumps confirmed 0 lost, 0 resyncs,
3 honest correction snaps (max reconcile sample 0.48 m), no continuous
yanking. Shaped +40/+40 ms up+down (186 s): ack rate 30.31/s exact,
fallback **0.39 %/tick** (pre-S5: every tick), lead converged 5 and held
(range 5–7), occupancy median 3, reconcile error flat zero with **zero**
snaps, 173/173 jumps confirmed 0 lost, 0 resyncs, precise estimate
184/186 samples. Every §4 acceptance criterion met; the shaped leg is
cleaner than baseline because the converged lead buys real margin. The
2026-07-03 "shaped-local is not representative" caveat is formally dead.

## 5. Aerial gating — disputed rule, badly served by its own presentation [GAP]

`GROUNDED_ONLY` is authored on **every** strike since the initial import;
the owner has ruled the restriction (at minimum on gap-closers) disputed, and
live testing showed its edge is timing-dependent (the grounded flag at
validation time — a short hop often lands before validation). Decision needed
per archetype, not a global default: gap-closers plausibly
`GROUNDED_OR_AIRBORNE` (dash math already server-owned), most strikes
whichever the movement fantasy demands. Whatever the ruling, §3's contract
applies: a rejected mid-air press must read as *denied*, not as a swing.
(S2 delivered that presentation half on 2026-07-03 — an `AerialMismatch`
reject now cuts the windup and flashes the slot. The ruling itself stays
open; it decides which presses reject at all, and with it part of S2's
eventual test matrix.)

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

**Delivered (2026-07-04, S6 — implementation + server-half live evidence;
owner acceptance run pending).** Scope ruling first: S6 stays client-only
presentation as the slice table says — the player-attack same-tick
CAST→damage left open by S3 (§1) is victim-side telegraph work and does not
join this slice. One premise correction: this section claimed
`AutoAttackState.next_swing_at` "is a replicated row" — it was not. The
table was private (no `public` attribute, no client subscription, type stub
only). The minimal enabler shipped with the slice: `auto_attack_state` is
now `public` with an owner-filtered subscription (the exact `GlobalCooldown`
shape in `GameplaySubscriptionPlanner`), so only the local player's own
schedule row replicates. No rows, columns, reducers, or gameplay changed;
bindings regenerated canonically (one new table-handle stub,
`Tables/AutoAttackState.g.cs`).

Client: `AutoAttackSwingScheduler` (self-bootstrapped, presentation-only)
polls the local row each frame and starts the swing at `next_swing_at`
converted through `ArenaServerClock`, gated on `HasPreciseSample` — without
a precise clock (or with the runtime kill switch off: `[` while the netcode
overlay is up) playback stays CAST-driven exactly as before. A swing fires
locally only when a client mirror of every server hold passes: live harmable
target, range vs the replicated `AutoAttackCatalog` row, advisory LOS (S4
mirror), hard-CC status, active defense (`DefenseState`), blocking cast,
dodge, voluntary-move cadence reset epoch, and `pending_due == false`. A
held or `pending_due` schedule is never predicted — the probe measured a
held release firing 17.3 s off-schedule, which is exactly why. The fired
swing plays as a Predicted AutoAttack-category request through the standard
playback gate (autos still yield to higher-priority presentations), and the
authoritative CAST is consumed as a duplicate via a
`CombatAnimationReplayPolicy` route (400 ms window, post-CAST lockout
against clock-error double-presents, 150 ms late-fire cutoff). An armed
auto-attack replacement (private pending row) suppresses prediction until
that swing's CAST arrives; a strike-identity mismatch lets the authoritative
animation play and is counted. Contact-cue parity: the scheduled swing takes
the same advisory contact path as predicted melee with auto-tagged ledger
entries; the consumed CAST supplies the `action_instance_id` mapping that
presses get from `PredictedActionResult`, so matched/falsePos are finally
meaningful for autos (feel audit F5 falsePos redo). Evidence surface:
overlay "S6 auto swing sched" line (fired/suppCast/unpred/held/late/expired/
mismatch + startErr and castAlign last/max) plus an auto share line under
the contact-cue section, `aa_*` columns in `remote-presentation-ab.csv`,
`[AA_SCHED]` action-bar traces, and `ops/analyze-s6-auto-swing.py` printing
per-session acceptance verdicts.

Server-half verified live 2026-07-04 (`ops/s6-auto-swing-probe.py`, all
pass, headless websocket player): the owner row replicates over a real
subscription (including tick-driven `TransactionUpdateLight` updates —
identity/timestamp cells arrive as single-element arrays in v1.json); every
on-time CAST landed 19–32 ms after the advertised `next_swing_at` (pure
33 ms tick rounding, so a client firing at `next_swing_at` presents the
truth within one tick); out-of-range hold flipped `pending_due=true` with
zero CASTs; walking back released the held swing 17.3 s off-schedule.
Client-half acceptance (0 ms + shaped +40/+40 per `docs/latency-testing.md`:
start error ≲ 1 tick, every CAST suppressed, falsePos ≈ 0, zero
double-swings, zero local swings under hold) ran as the owner checklist
handed off with the slice.

**Owner acceptance recorded 2026-07-04 — slice closed.** Two live legs
(analyzer sessions 08:27:28Z baseline 0 ms, 114 s; 08:30:51Z shaped
+40/+40 ms, 176 s), all verdicts PASS in both: every locally fired swing's
CAST consumed as a duplicate (11/11 baseline, 22/22 shaped, zero
expired-no-CAST, zero mismatches — no double-swing events); local fire led
the authoritative CAST by 21–31 ms at baseline (pure tick rounding — the
swing starts the moment the schedule says, the CAST confirms a tick later)
and stayed within [−31, +2] ms shaped, with schedule-conversion error ≤
10 ms; auto contact cues 33 fired / 33 matched / 0 falsePos / 33
suppressedAuth across both legs — the feel-audit F5 falsePos redo is
closed for autos; the mirror-held schedule in each leg produced zero local
swings. Unpredicted CASTs (2 baseline / 9 shaped) are the designed
CAST-driven remainder: pre-precise-clock swings, the in-session `[`
toggle-OFF A/B (swings visibly revert to RTT-late and snap back on-beat
when re-enabled), and held releases.

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

**Delivered (2026-07-03, S1).** Every non-seeding sample now classifies
interpolated / extrapolating (within cap) / starved (past cap, delivery late)
/ settled (past cap, entity at rest) — `RemotePresentationBuffer.ClassifySample`,
pure and reflection-tested. Discriminator, with the cadence facts verified in
code: `PlayerPhysics` commits every tick for every live connected player
(`game_loop.rs` `tick_player` → unconditional `commit_player_physics`), so a
remote player past the cap is always starved; `NpcPhysics` legitimately stops
when idle (`npcs.rs` — chase writes per chase tick, facing only on yaw
change), so an NPC past the cap is settled while global row delivery is fresh
(`NetcodeReceiveCounters.RowDeliveryFresh`: `TotalRows` changed within 250 ms,
the same signal ConnectionStatusHud classifies staleness from) and starved
once it stalls. A settled entity's reported buffer depth pins at the cap
boundary (−2 ticks, `ReportableBufferAheadTicks`) instead of diving. Four-way
counts surfaced in the ClientSimulationState/NpcEntity passthroughs, the
overlay's Remote Presentation section, and new CSV columns
(`p_starved`/`p_settled`/`n_starved`/`n_settled`); the A/B metric going
forward is (extrapolating + starved) / (all non-settled samples). Rendered
pose is unchanged — classification and reporting only. Heartbeat stays
deferred; known limits of the global-flow discriminator: playground/practice
dummy players commit only on change and read as starved while parked (debug
fixtures — exclude from A/B legs), and a *moving* NPC whose own rows gap past
the cap while global delivery stays healthy reads settled until the next row.

**F4 A/B rerun 1 (2026-07-03, post-S1, session 07:50:21Z): inconclusive —
S7 stays blocked.** The taxonomy held up (settled no longer poisons depth;
legs compare on the (extrap + starved) / non-settled ratio), but the run
missed protocol: 26 s / 24 s legs against a planned 75 s each, and the
kobold sat settled 82–84 % of samples (hovering, not chasing). Late ratio
ON 26.2 % vs OFF 24.2 % (z ≈ 0.8 — noise), hard snaps 0–0, error columns
nearly all zero on a settled target. No timeline verdict — and note ON pays
100 ms presentation delay vs OFF's 66 ms by design, so a tie is a loss for
ON. Rerun spec: measurement build (`ARENA_NPC_NO_ATTACK=1
ARENA_NPC_AGGRO_RADIUS=100 ./ops/republish-local-clear.sh` — compile-time
flags in `server/src/npcs.rs`: NPC melee disabled outright, 100 m aggro
radius) so the kobold does nothing but chase, anywhere in the playground —
the stock 8 m leash, the post-swing 1800 ms cadence freeze, and tester
death made sustained chase unachievable by hand; run continuous laps at
full speed; 60 s warmup as a discardable timeline-OFF leg 0, then 75 s
legs interleaved ON/OFF/ON/OFF with settled < ~40 % per leg; start legs
only after depth reads sane (session 07:23:12Z logged 57 s of ON at 100 %
starved / depth −1739 ticks — session-start clock convergence). Full
numbers: feel audit F4 A/B entry.

**F4 A/B rerun 2 (2026-07-03, session 13:25:52Z): conclusive — the
server-time timeline as shipped loses.** The measurement flags
(`ARENA_NPC_NO_ATTACK=1 ARENA_NPC_AGGRO_RADIUS=100`) held the kobold in
continuous chase (settled ≤ 1.5 % per leg); five legs (OFF warmup
discarded, then ON/OFF/ON/OFF, 83–108 s, ≥ 11 k non-settled samples
each). Late ratio pooled: ON 11.6 % vs OFF 9.0 % — every ON leg worse
than every OFF leg, nominal z ≈ 10 — while ON holds a 100 ms delay
budget against OFF's 66 ms. Hard snaps 0–0; error mean ON slightly
worse, error p95 ON better (0.42–0.47 m vs 0.49–0.50 m). Reading:
server-time keying does smooth the error tail, but its fixed 100 ms
budget is effectively under-delayed — absolute lateness vs the estimated
server clock has a wider tail than inter-arrival gaps — so as shipped it
pays +34 ms of presentation delay and still extrapolates 2.6 pp more.
S7's gate failed as specced; see the S7 row for the decision.

**S7 rescope delivered (2026-07-04): adaptive delay budget + fully
automated rerun protocol; shaped session decides the gate.** Owner ruling:
rescope S7 to adapt the server-time delay budget from measured server-time
arrival-lateness p95 (rerun 2's failure signature is under-delay, which is
exactly what adaptivity cures), built gate-first with the drop path
pre-wired. Client-only, zero schema change: `ServerTimeDelayBudget` records
lateness (precise serverNow − quantized row stamp) at
`RemotePresentationBuffer.Push` for every stamped row on the connection,
and budgets p95 + one tick, clamped [66..200] ms, slow-slewed (raise
≤60 ms/s, lower ≤8 ms/s, targets recomputed at 1 Hz), one shared budget per
connection; precise-clock gated, holding the pre-S7 fixed 100 ms until
samples exist, so no-precise-clock behavior is byte-for-byte the shipped
F4. Rendered-pose machinery untouched — only when samples present changed.
Instrumentation: overlay S7 budget line, `s7_*` CSV columns (budget,
target, lateness p95, window n, NPCs-on-timeline), scripted A/B leg driver
in the overlay (period: 60 s OFF warmup + ON/OFF/ON/OFF 80 s legs;
`ARENA_S7_AB_AUTORUN=1` self-starts for batchmode), `ops/s7-lap-probe.py`
(headless chase target — 42 laps/0 skips live, kobold moving 100 % of
samples at 0.1–2.4 m; kobold spawned AT the circuit because nearest-wins
aggro otherwise latches the spawn-point observer), and the analyzer's gate
verdict (regression-validated: reproduces rerun 2's recorded FAIL and
numbers exactly). Baseline A/B (2026-07-04, session 09:24:51Z, unshaped,
autonomous headless observer): protocol OK, late ratio ON 0.04 % vs OFF
0.05 % at an identical 66 ms paid average — the budget floors at 66 (p95
5–7 ms + 33 margin), erasing fixed-100's +34 ms baseline tax; the
session-start convergence window (budget 163 ms, paid 83 ms) proves the
adaptive path engages. Gate decision comes from the owner's shaped session
(+40/+40 + jitter branch), scored by
`ops/analyze-remote-presentation-ab.py`: PASS → S7 delivered; FAIL → park
`ServerTimeTimelineEnabled` default-OFF and record S7 resolved-dropped
(the decision is already made — the shaped run just picks the branch).

**S7 shaped acceptance (2026-07-04, session 10:28:08Z): gate PASS — slice
closed.** Owner shaped session per rerun-2 spec (+40/+40 dnctl with the
30 % / 65 ms jitter branch), full automated protocol: 60 s OFF warmup
discarded, ON/OFF/ON/OFF 80 s legs, protocol OK, settled 0 % in every
scored leg. Pooled late ratio ON 1.1 % (190/17,936) vs OFF 8.0 %
(1,441/18,058) — every ON leg (1.0/1.1 %) beat every OFF leg (7.7–8.2 %),
nominal z ≈ 32 — and ON kept the err-p95 win (0.432 m vs 0.580 m). Hard
snaps tied 2–2. The adaptive budget did what rerun 2 proved a fixed one
could not: it read the shaped lateness tail (in-leg p95 ~110 ms) and paid
144 ms average against OFF's 66 ms arrival delay, inverting fixed-100's
under-delayed loss (11.6 % vs 9.0 %) into a 6.9 pp win. Players on the
same connection budget mirrored it (late ratio ON 0.1 % vs OFF 5.1–6.1 %).
`ServerTimeTimelineEnabled` ships default-ON; the parked drop path is
dead. The slice's promise is now measured fact: jitter costs a stable,
budgeted delay instead of motion warp.

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
- **No impact-time LOS re-check**: legitimate counterplay; recorded as the
  combat authoring contract with S4 — `requires_target_los` is validated at
  press/targeting time only, so dodging behind cover after a projectile or
  swing is launched is the dodge working as designed. (Electrocute's
  channel-sustain LOS check is a channel rule, not an impact re-check, and
  follows the same authored flag.)

---

## Migration slices, in order

Each slice is bounded, independently shippable, and lands with the evidence
instrumentation to judge it. On tests: the existing suite is churn-era noise,
not spec — a pinned test is never an argument against a design change, and
new tests are written only once a slice's contract has stabilized, not
during churn. Order chosen so measurement precedes tuning and feel wins
precede fairness work.

| # | Slice | Surface | Unblocks |
|---|-------|---------|----------|
| S1 | ✅ Delivered 2026-07-03 — idle-aware sample taxonomy (settled vs starved) in `RemotePresentationBuffer`, overlay, CSV logger (see §7) | client | honest F4 A/B rerun |
| S2 | ✅ Delivered 2026-07-03 — cut-on-reject via existing interrupt primitives + slot flash, `PredictionRejected` payload extended with the pressed action id (see §3) | client | legible denials (also fixes §5's worst symptom) |
| S3 | ✅ Delivered 2026-07-03 — NPC telegraphs: authored per-template windup between CAST and damage via `npc_pending_swing`, present-time re-validation at impact (see §1; player attacks rescoped out of S3 by owner) | server + data | victim reaction time; masks present-time validation |
| S4 | ✅ Delivered 2026-07-04 — LOS unification: `requires_target_los` targeting flag (default on, opt-out list signed empty), gap-close = LOS + path with distinct reasons, auto-attack holds behind cover, client advisory pre-check + slot dim from bundled collision (see §2) | server + data + client | legible targeting rules |
| S5 | ✅ Delivered 2026-07-04 — closed-loop input buffering: per-tick consume truth on the ack surface (`last_tick_consumed_command`/`buffered_command_count`), client lead control loop (setpoint 1–2, asymmetric raise/lower, no endpoint-kind switch), degradation ladder (throttle before resync, forward re-anchors), one-tick jump slide, precise-clock tick estimate + F4 warmup gating, correction-decay presentation budget. Same-day follow-up from acceptance run 1: server tick cadence was fixed-delay-scheduled at a real 36.6 ms — replaced by a fixed-rate Time chain (33.0 ms measured) with watchdog, and client authoring is target-chasing (paces to measured cadence; inject/skip rate caps deleted). See §4; server half verified live via `ops/s5-input-loop-probe.py` + cadence measurements; owner acceptance recorded 2026-07-04 (baseline + shaped +40/+40 legs, all criteria met — slice closed) | client + server | playable at any RTT; shaped-local testing representative |
| S6 | ✅ Delivered 2026-07-04 — local swing scheduling off the (newly replicated, owner-only) `auto_attack_state` row via `AutoAttackSwingScheduler`: precise-clock-gated fire at `next_swing_at`, full server-hold mirror (never swings a lie), authoritative CAST consumed as duplicate, contact-cue parity with an auto counter split. Server-half verified live (`ops/s6-auto-swing-probe.py`: replication, CAST 19–32 ms after `next_swing_at`, hold ⇒ pending_due + zero CASTs); §6 records the corrected premise (the row was private until S6 — visibility-only server diff). Owner acceptance recorded 2026-07-04 (baseline + shaped +40/+40 legs via `ops/analyze-s6-auto-swing.py`, all verdicts PASS: 33/33 CASTs suppressed, fire within one tick of the CAST, cue falsePos 0, zero double-swings, holds fire nothing — slice closed) | client (+ table visibility) | auto-attack feel at RTT |
| S7 | ✅ Delivered 2026-07-04 — adaptive server-time delay budget (owner ruling after rerun 2's fixed-100 FAIL: rescope to adapt from measured server-time lateness, gate-first): `ServerTimeDelayBudget` (lateness p95 + 1 tick, clamp [66..200] ms, slow slew, precise-clock gated, connection-shared), S7 overlay/CSV instrumentation, fully automated A/B protocol (scripted leg driver / `ARENA_S7_AB_AUTORUN`, `S7HeadlessAbRunner` batchmode observer, `ops/s7-lap-probe.py` chase target, analyzer gate verdict). Baseline A/B: budget floors at 66 ms — fixed-100's +34 ms baseline tax gone at an equal late ratio. Shaped acceptance recorded 2026-07-04 (+40/+40 + jitter, session 10:28:08Z, all-automated protocol OK): gate PASS — pooled late ratio ON 1.1 % vs OFF 8.0 % (z ≈ 32), err p95 0.432 vs 0.580 m, paid 144 vs 66 ms; `ServerTimeTimelineEnabled` ships default-ON — slice closed (see §7) | client | jitter costs fixed delay, not motion warp |
| S8 | ✅ Delivered 2026-07-04, owner acceptance recorded same day — slice closed, switch ships **default ON** (shaped +40/+40 OFF/ON legs: no feel regression; analyzer verified the real client's reports end-to-end — 51 gate + 35 impact evaluations, history-sourced poses, zero flips at the owner's play pattern) — bounded attacker-view rewind + favor-the-defender grace, design doc first as contracted (`docs/lag-compensation-design-2026-07-04.md`, rulings D1–D5 owner-signed): 16-slot private position-history ring + rewind barriers at the physics choke points, press-transaction `view_server_time_ms` reports (≤ 250 ms arrival-anchored clamp), one pose-parameterized melee positional gate + spell/charge press overlays, frozen-delay impact re-check (D2), dual-verdict `[LAG_COMP]` audit in both switch states, runtime kill switch, parry/block success grace 50→150 ms (D4). Server half verified live 2026-07-04 (`ops/s8-lag-comp-probe.py`, all-green record runs on fresh throwaway publishes; since ported to frozen-build setup): flip-geometry charge presses (kobold inside the 5.45 m minimum ring now, outside it 250 ms ago) rejected present-time 4/4 with the switch OFF and accepted at identical geometry with it ON — cumulative across the day's honest-probe runs 16/16 OFF rejects vs 6/6 ON accepts, every audit line `present=out_of_range → rewound=accept, source=history` at rewound_ms ≈ 250–280; history ring 16 slots spanning ~494 ms on a chasing kobold; accepted dash stamped the attacker's rewind barrier; defense leg: live cooldown row reads exactly `recovery − active = 9 850 ms` (the 150 ms grace baked in), 21 parries / 240 s under three harmless warriors. Owner leg: shaped +40/+40 melee/charge pressure on a chasing kobold, scored by `ops/analyze-s8-lag-comp.py`; PASS flips the config default ON in the acceptance commit (S7 precedent) | server + data + client | end-state hit fairness |
| S9 | 📝 Design ready 2026-07-04 (`docs/auto-attack-rewind-design-2026-07-04.md`, rulings E1–E5 pending) — standing view-delay signal (slow-cadence report piggybacked on `ping_clock`, per the S8 §7 note) + auto-attack tick rewind: the tick stamps the S8 press-context row in its own transaction, so the existing melee chain and frozen-delay impact re-check rewind server-initiated swings with zero new rewind code; both tick hold gates (reach/LOS) read the same rewound pose. Rider: `[DEFENSE_LATE]` telemetry at the defense choke point — the parry-loss dataset the D4b (deferred defense resolution) decline was parked on. Own flag under the S8 master switch, default OFF, shaped A/B gate per precedent. Sequenced first of the S8 §7 gaps because the standing signal is the shared substrate: S10 (per-victim sweep rewind) reuses S8 machinery only; S11 (deferred defense resolution) is decided on S9's telemetry. Aerial items stay deferred | server + data + client | closes the last present-time player attack path; D4b decision data |
| S10 | ✅ Implemented + default **ON** 2026-07-05 (owner call — shipped live on the server-half probe PASS; the shaped +40/+40 A/B is deferred as an optional spot-check, not a gate — revert with `set_lag_comp_config true 250 true false`) — per-victim rewind for cone/radius sweeps (spell `AREA` `resolve_area_impact` + melee caster-cone/radius `resolve_pending_melee_hit_volume`) via one shared `sweep_rewind_membership` helper; `sweep_rewind_enabled` flag (4th `set_lag_comp_config` arg); projectile impacts stay present-time (G1); no-target casts report the shared S7 budget (G2). Server-half live-verified by `ops/s10-sweep-rewind-probe.py` (PASS, ICE_SPIKES vehicle); the probe is now ported to frozen-build setup. Design `docs/sweep-projectile-rewind-design-2026-07-05.md`. Remaining/deferred items: `docs/netcode-open-items.md` | server + data + client | end-state hit fairness for area sweeps |

Owner decisions needed before their slices: S9 rulings E1–E5 (report
policy, staleness TTL, one-timeline rule, config shape, telemetry rider —
recommendations in the design doc); aerial gating ruling per
archetype (§5, gates part of S2's test matrix — still deferred by owner
direction 2026-07-04).
Decided 2026-07-04 (after a live playground session): LOS raycast geometry
set — **query-only**. Query raycasts (LOS + projectile impact) test
authored query geometry plus terrain and arena layout; movement boxes
never block sight (they are authored oversized for capsule keep-out).
Contract recorded in CLAUDE.md ("Combat Geometry Contract") and at the
choke point in `server/src/world_collision.rs`; client mirror
(`ServerLosCollisionData`) matches. Seeded arenas have no authored query
geometry yet, so only their layout raycast blocks sight there until some
is authored.
Decided 2026-07-03: telegraph durations (S3 — scaled per template
350/450/500/600 ms, strict impact-time reach re-check, player attacks
rescoped out of the slice).
Decided 2026-07-04: LOS opt-out list (S4) — **empty**; every hostile
targeted action requires target LOS, including ARCHER_RAIN_SHOT, all eight
gap-closers, and melee auto-attacks.

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
4. Prior work treated the pinned regression tests (netcode constants,
   animation timings) as constraints to update carefully. Owner ruling: the
   current suite is churn-era noise with little signal — contracts in this
   review override tests, and tests are rewritten after a contract
   stabilizes, never cited against a redesign.

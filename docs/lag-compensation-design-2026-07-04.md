# S8 — Bounded Lag Compensation + Favor-the-Defender Grace (Design, 2026-07-04)

Slice S8 of `docs/netcode-design-review-2026-07-03.md` (§1 target contract items
2–3): a bounded positional-history rewind so player attacks validate against
what the attacker actually saw, plus a defender-grace widening. Per the slice
table: **design doc first, kill-switched**. This document is the design; the
owner decisions in §6 gate implementation. Everything in §1 was verified in
code today.

**Status 2026-07-04 — DELIVERED, acceptance recorded, slice closed.**
Rulings D1–D5 signed (§6) and the design implemented the same day; server
half verified live by `retired pre-cutover S8 lag-compensation harness` (all-green record runs).
Owner shaped acceptance same day (+40/+40, OFF/ON legs): no feel regression
with ON, and the analyzer confirmed the real client's wiring — 51 press-gate
+ 35 impact-recheck evaluations carried view reports with history-sourced
poses; zero verdict flips in either leg (the owner's presses never sat on a
verdict boundary — lag comp changed nothing for that play pattern and cost
nothing). Gate PASS per the S7 precedent → **the switch ships default ON**;
`set_lag_comp_config false 250` is the kill switch. Note on the audit
metric: `rewound_ms` reports the *age of the history sample used* — on an
entity at rest it can exceed the cap while the pose is exactly the present
pose; the effective rewind is always clamped ≤ `max_rewind_ms`.

**Principle (one sentence):** *the rewind decides whether an attack connects;
the present decides everything about how it resolves.* Rewind applies to
positional accept/reject checks only — never to health, status, defense
state, damage, or anything the defender owns.

---

## 1. What exists today (verified)

### 1.1 Every positional validation site and its timeline

| Site | Checks | Positions read | Timeline |
|---|---|---|---|
| Melee press, targeted chain (`melee.rs:2837-2917`) | facing arc, range + hit_radius, minimum range, LOS (S4 gate) | `caster_phys` (present `PlayerPhysics`), `target_snapshot` via `player_snapshot_for` (present) | present |
| Melee impact resolve (`melee.rs:4136-4195`) | reach re-check (whiff), miss/dodge check, defense arc vector | present `caster_phys`, present `target_snapshot` | present |
| Gap-close resolution (after the S4 LOS gate) | LOS + path, destination bake | present positions, world collision | present |
| Auto-attack tick | reach, LOS hold (S4) | present | present |
| Projectile impact (`combat/projectiles.rs:1240-1346`) | hit test, defense window (1 ms) | projectile row + tick-start snapshot set | present |
| Targeted spell cast (`spells/casting.rs`) | range, LOS (S4 flag) | present | present |
| Cone/radius sweeps | area membership | present snapshot set | present |
| NPC pending swing resolve (`npcs.rs:978-1063`, S3) | alive/disabled/reach re-check, defense | present | present (owner-ruled: stepping out during windup is the dodge counterplay) |

There is **no positional history anywhere server-side** (confirmed; the review's
claim holds). There is no global tick counter — only per-player
`last_processed_tick` on `PlayerPhysics`; the 33 ms tick chain
(`game_loop.rs`, S5) is timestamp-anchored, so history is naturally
**timestamp-keyed**, not tick-keyed.

### 1.2 What the server knows about client time

Almost nothing, deliberately:

- `ping_clock` (`ping.rs:10`) is a no-op echo; the server stores no RTT state.
- Movement and parry/spell presses carry `input_tick` (S5 lead), validated
  only as a drift window (±12 ticks) in
  `action_snapshot.rs:57-129` — it says nothing about *view* delay.
- **The melee press carries no timing signal at all**
  (`melee.rs:3368-3378`: strike, target, claimed pos/yaw, prediction token).

### 1.3 What the client knows about its own view time — exactly enough

The client can state precisely which server-time it was rendering for any
remote entity at press time:

```
view_server_time_ms = ArenaServerClock.ServerNowMs − paid_presentation_delay
```

- `ArenaServerClock.ServerNowMs` (`ArenaServerClock.cs:39`) — precise F2b
  midpoint estimate, ±~12 ms band, gated by `HasPreciseSample`.
- `paid_presentation_delay` — per-entity honest value:
  `RemotePresentationBuffer.LastEffectiveDelayMs` (`RemotePresentationBuffer.cs:156`),
  which is the S7 adaptive budget (66–200 ms) on the server-time timeline or
  the legacy 66 ms interpolation delay on the arrival timeline. S7's
  acceptance proved this value tracks real delivery (shaped: ~144 ms).

The uplink half of the delay does not need to be reported: the server computes
`rewind = ctx.timestamp − view_server_time_ms`, and the press's transit time
is automatically inside that difference.

### 1.4 The defense model as actually implemented

The review's shorthand ("50 ms-grace parry") undersold the code. Verified
model (`defense.rs`):

- Parry and block are **hold-armed**: `active_until = now + 3,600,000 ms`
  (`defense.rs:20-21`). Any press that *arrives before a hit resolves*
  defends it — the overlap check (`defense.rs:497-502`) is against the hit's
  `[active_from, active_until]` window.
- The 50 ms is the **post-success grace**: a successful parry/block flips to
  cooldown with `active_until = now + 50 ms`
  (`PARRY_SUCCESS_GRACE_MS`/`BLOCK_SUCCESS_GRACE_MS`, `defense.rs:22-24`),
  then a 10 s cooldown. Hits landing ≤ 50 ms after your parry are also
  parried; hits 51 ms–10 s after it land clean.
- The defense **arc** is judged from the defender's press-time `facing_yaw`
  and present positions (`defense.rs:506-513`) — already defender-owned state.

So the two real render-delay costs to the defender are:

1. **The simultaneity window is too thin.** Two hits the defender's screen
   showed as one moment (they render attackers 100–166 ms in the past) can
   straddle the 50 ms success grace: the reacted-to hit parries, its twin
   lands clean during the 10 s cooldown.
2. **The last-moment press loses whole.** A parry pressed in honest reaction
   to a windup seen ~150 ms late can arrive after the hit resolved. The
   armed-window model cannot help — the row didn't exist at resolve time.

---

## 2. Target contract — attacker-side bounded rewind

### 2.1 View-time report (client → server)

Targeted attack reducers gain one argument: `view_server_time_ms: u64`
(0 = no report → validation stays present-time, byte-for-byte today's
behavior). The client fills it at press time from §1.3, using the **target
entity's** buffer (`LastEffectiveDelayMs` of that entity's
`RemotePresentationBuffer`), falling back to the shared budget, then to 0
when no precise clock exists.

Server-side, per press:

```
rewind_ms = clamp(now_ms − view_server_time_ms, 0, MAX_REWIND_MS)
rewind_ms = min(rewind_ms, now_ms − target_rewind_barrier_ms)   // §2.4
```

`MAX_REWIND_MS = 250` (D3): covers the S7 budget cap (200 ms) plus uplink
and clock error, and is the genre-standard bound. Note the clamp is
**arrival-anchored**: a claim older than the cap is not rejected — it
validates at `now − 250 ms`, the oldest allowed moment. A real client's
total claim age (presentation delay + uplink) sits well under the cap, so
its claimed moment is honored exactly; only claims that exceed the cap get
silently advanced (observed live while building the probe: a press path
with ~250 ms of SQL latency had its intended moment pushed past the
boundary crossing — real presses don't have that problem). **Trust analysis:** the
report is client-claimed, so a modified client can always claim maximum
lateness — the cap means the worst cheat is "validate against a 250 ms-old
world," identical in kind to playing at 250 ms ping, and the claim is
sanity-clamped to `≤ now`. This is the same bounded trust every
rewind-based shooter/MMO accepts; the kill switch and the audit counters
(§4) keep it observable.

### 2.2 Position history ring

New **private** table `combat_position_history`:

```
(entity_key: String, slot: u8)  ← primary key (entity_key indexed)
stamped_at_micros: i64
pos_x, pos_y, pos_z: f32
yaw: f32
```

- **16 slots** per entity, circular (`slot = write_counter % 16`); at 33 ms
  that is ~528 ms of history — comfortably above the 250 ms cap plus jitter.
- **Players:** one slot updated per tick from the same site as
  `commit_player_physics` (players commit every tick, S5). **NPCs:** one
  slot updated wherever `NpcPhysics` commits (chase/yaw writes only —
  see lookup rule for why gaps are correct).
- **Write cost:** one small row-update per moving entity per tick — the same
  order as the existing physics commits it shadows. This roughly doubles
  per-entity per-tick row writes, which matters for local commitlog growth
  (known issue: tens of GB at 30 Hz); the cleanup recipe already in use
  covers it, and NPC history writes pause whenever the NPC is idle.
- **Lookup rule:** the rewound pose is the newest sample with
  `stamped_at_micros ≤ view_time`. If the newest sample overall is older
  than `view_time`, the entity has not moved since — use present pose. If
  *no* sample ≤ `view_time` exists (history too shallow — deep rewind right
  after spawn), clamp to the oldest sample. Nearest-≤ is exact for gap
  semantics (an NPC that stopped writing sat still at its last written
  pose) and within one tick (≤ 33 ms × speed ≈ 0.2 m) for players — below
  the existing action-snapshot tolerance; no interpolation in v1.
- **Pruning:** rows delete on despawn/disconnect alongside the entity's
  physics row.

Rejected storage alternatives: an in-memory static ring (rollback- and
restart-unsafe: a rolled-back tick would leave phantom samples; module
restart silently empties it) and one-row-per-entity packed blobs (rewrites
16× the bytes per tick for no query benefit).

### 2.3 What rewinds — per-site ruling

One rule everywhere: resolve **one rewound target pose** per validated press
and feed the *entire* positional chain from it. Mixed-timeline checks
(range at view time, LOS at present) are incoherent and produce unexplainable
rejects.

| Site | S8 behavior |
|---|---|
| Melee press targeted chain (facing, range, min-range, LOS endpoints) | **Rewound target pose** (`melee.rs:2837-2917` reads the resolved pose instead of `target_snapshot` positions). Caster pose stays the press-claimed/validated snapshot, as today. |
| Combo follow-ups, intrinsic/replacement auto-attack *presses* | Same chain, same rewind (they dispatch through it). |
| Gap-close **validation** (LOS + range) | Rewound target pose. |
| Gap-close **path bake + destination** | **Present-time.** You cannot rewind the world you move through; the dash must land somewhere real. Validation may pass on the rewound pose while the dash still bakes against the live one — the distance/path clamps stay present-time, so no reach exploit. |
| Melee **impact** reach re-check + whiff (`melee.rs:4136-4145`) | **D2 (owner ruling):** recommended — re-check against the target pose at `impact_time − view_delay`, where `view_delay` is frozen at press (`now − view_server_time_ms`, clamped). This is what makes "the swing I saw connect, connects" true; without it, most of the benefit evaporates during `impact_delay_ms` against a strafing target. |
| Targeted spell cast validation (range, S4 LOS) | Rewound target pose, same clamp (cast press already carries `input_tick`; it additionally gets `view_server_time_ms`). Implementation rule: the press context lives in a per-caster row stamped with the press transaction's timestamp, so *only checks in the press transaction* rewind — cast-time completion re-validation, channel sustains, movement-delivery arrival resolution, and queued combo releases run in later transactions and stay present-time by construction. |
| Auto-attack **tick** (server-initiated swing: reach/LOS hold) | **Present-time in v1.** There is no press to carry a view time. Recorded gap + future extension in §7. |
| Projectile impact | **Present-time, deliberate.** The projectile is visible in flight; dodging it is counterplay — same philosophy as the recorded no-impact-LOS-re-check ruling. Launch validation (a targeted-spell press) does rewind. |
| Cone/radius sweeps (no target) | **Present-time in v1.** Per-victim rewind for area hits is the full-fat model; deferred (§7). |
| NPC attacks | **Never rewind** — NPCs have zero latency. S3's present-time contract is untouched. |

LOS note: geometry is static, so rewinding LOS means moving the *endpoints*
only — the same multi-probe query (`scene_query.rs`) runs against the
authored query set (geometry ruling untouched) with the target endpoint at
the rewound pose. A pleasant coherence effect: the S4 client advisory
already tests against the *rendered* (delayed) target pose — with rewind ON,
the server finally validates the same world the advisory approved, so
advisory/authoritative agreement improves for free.

### 2.4 Never-rewind list (hard rules)

1. **Never rewind defenses** — defense state, its arc, and its windows are
   judged on present state exactly as today (`defense.rs` untouched by the
   rewind; §3 widens a constant only).
2. **Never rewind vitality or status** — alive, harmable, disabled,
   world-context, dodge/miss checks (`hostile_targeted_ability_misses`),
   and the aerial gate all read present state. A target that is dead,
   invulnerable, or already parrying *now* resolves on that truth; rewind
   never resurrects a target into hittability — it only moves a hittable
   target's checked position.
3. **Never rewind through discontinuities.** Each entity carries a
   `rewind_barrier_micros`, stamped at: special-movement start *and* end
   (dash, gap-close, knockback), teleport, respawn, and world-context
   change. The rewind clamps to `max(view_time, barrier)` — if the target
   dashed between what the attacker saw and now, the attacker validates
   against the post-discontinuity truth, by design. (Storage: one timestamp
   on the history row-set or a tiny per-entity row; stamped from the
   existing `SpecialMovementRuntime` insert/delete sites and respawn.)
4. **Attacker pose never comes from history** — it is the press-claimed
   snapshot validated by the existing tolerance machinery
   (`action_snapshot.rs`), unchanged.
5. **Emitted events stay present-time** — impact positions, VFX anchors,
   and the defense arc vector are built from present poses, so nothing a
   *victim* sees is rewound (principle: the present decides how it resolves).

### 2.5 Failure containment

Missing history (fresh spawn, NPC never moved, table pruned) degrades to
present-time validation — never a reject. A zero/absent report degrades to
present-time. The kill switch (§5) restores today's behavior wholesale. In
every degraded path the behavior is exactly the shipped S1–S7 game.

---

## 3. Target contract — favor-the-defender grace

**Ship now (cheap, code-true):** widen `PARRY_SUCCESS_GRACE_MS` /
`BLOCK_SUCCESS_GRACE_MS` from **50 → 150 ms** (`defense.rs:22-24`). This
covers §1.4 cost 1: everything that looked simultaneous on a screen
rendering ≤ ~150 ms in the past resolves under the defense you reacted with,
instead of landing clean during your cooldown. It applies automatically to
NPC swings (S3) and players — the resolve path is shared. The deliberate
trade: an attacker staggering two hits ~100 ms apart to "punish the parry
cooldown" loses that line — that is the point of favor-the-defender.

Static 150 (the review's cap), not per-defender-derived: the server keeps no
RTT state (§1.2), and deriving it would mean new per-client timing state for
at most ±60 ms of precision on a grace window. Not worth the machinery; the
constant is honest and legible. (Adversarial note against our own spec: the
review said "toward interp + RTT p50/2, capped 150" — at the cap, the
formula and the constant are the same number for every realistic connection;
the formula only matters below ~100 ms total delay, where the 150 ms
constant is *more* generous to the defender, which is the stated direction.)

**Explicitly decided, not silently skipped (D4):** §1.4 cost 2 — the parry
press that arrives just after the hit resolved. The only true fix is
**deferred resolution**: hold defensible hits open ~150 ms before applying
damage so a late-arriving press can still defend ("never resolve a defense
window against state the defender could not yet have seen", literally).
Recommendation: **decline for now.** It taxes every defensible hit's damage
timing by up to 150 ms (felt by attackers as impact mush), complicates the
S3 pending-swing and melee-impact resolve paths, and S3's authored windups
(350–600 ms) already restored the reaction budget that made this case rare.
Record as an accepted exposure; re-evaluate with live parry telemetry after
S8's attacker rewind ships.

---

## 4. Evidence & automated acceptance (no hand-recorded numbers)

**Dual-verdict audit (always computed while a report is present):** every
rewound validation ALSO runs the present-time verdict and logs
`[LAG_COMP] action=… rewind_ms=… clamped=… verdict=accept|reject
present_verdict=… flip=bool reason=…`. The **verdict-flip rate** is the
money metric — it is exactly "hits that connect only because of lag comp."
When the switch is OFF, reports still arrive and the would-be verdict is
still logged (flip counting works in both switch states), so an A/B needs
no client changes between legs.

**Analyzer:** `ops/analyze-s8-lag-comp.py` — parses the `[LAG_COMP]` audit
lines out of `spacetime logs` for any session (probe or owner leg): press
counts and flip rate by check and switch state, rewind_ms distribution,
pose-source mix (history / barrier_clamp / active_sm / oldest_clamp),
spell-overlay pose deltas.

**Headless probe leg (server truth):** `retired pre-cutover S8 lag-compensation harness`,
self-verifying (prints PASS/FAIL per check), two identities + NPCs on a
throwaway measurement-build DB (`ARENA_NPC_HARMLESS=1
ARENA_NPC_AGGRO_RADIUS=100`):

- *Runner probe* shuttles along a line, towing a chasing kobold; *attacker
  probe* parks 4 m off the line (far enough that the runner always stays
  the kobold's nearest player) and watches the kobold's distance sweep
  through WARRIOR_CHARGE's minimum-range ring (5 m + hit radius).
- Flip moment: present distance inside the ring while the pose 250 ms ago
  (an honest ~250 ms-delayed view) was outside it. Switch OFF → every such
  press must reject OutOfRange; switch ON → the same geometry must Accept.
- Sanity: config defaults (disabled / 250 ms); a view report on a
  stationary in-reach dummy stays Accepted in both states (rewound pose ==
  present when the target hasn't moved).
- History: the moving kobold's ring holds ≤ 16 rows spanning ≲ 0.6 s.
- Barrier: the first accepted charge dash stamps `combat_rewind_barrier`
  for the attacker (non-Normal physics commits are the stamp site).
- Grace check: two harmless warriors swing at a parry-cycling defender for
  ~150 s; a parry landing 50–150 ms after a successful parry is impossible
  under the old 50 ms constant, and any landed IMPACT in that band fails
  the check.

**Owner client leg (feel + client wiring):** shaped +40/+40 ms per
`docs/latency-testing.md`, melee/gap-close pressure on a strafing target;
scored by the analyzer plus the existing contact-cue ledger — predicted
melee falsePos under shaping should drop with ON (the cue fires on what the
attacker saw; ON makes the server agree). Handed off as literal keystrokes,
per standing practice.

**Gate (S7 precedent):** the switch ships **default OFF**; the shaped A/B
decides. PASS → flip default ON in the acceptance commit and close S8.
FAIL → park default OFF, record resolved-dropped with the numbers.

---

## 5. Kill switch & config

New private single-row table `combat_lag_comp_config`
(`enabled: bool`, `max_rewind_ms: u32`) + an owner/debug reducer to set it —
**runtime**, so A/B legs flip without republish (the S7 lesson; today's
server has only compile-time `option_env!` flags, which force a rebuild per
leg). Defaults seeded `enabled=false, max_rewind_ms=250`. History writing
stays on regardless of the switch (so flipping ON mid-session has warm
history and the OFF legs still log would-be verdicts); the switch gates only
whether rewound verdicts are *used*.

---

## 6. Owner decisions — SIGNED 2026-07-04

| # | Decision | Ruling |
|---|---|---|
| D1 | Press-site scope | **All hostile targeted actions** — melee strikes (incl. combo follow-ups), gap-close validation, targeted spell casts. One resolved pose feeds existing chains; melee-only would recreate the illegibility S4 killed. |
| D2 | Melee impact re-check timeline | **Rewind it** — re-check reach at impact against the target pose at `impact_time − frozen press view-delay`. The dodge-during-windup weakening vs lagged attackers (≤ 250 ms) is accepted; S3's NPC ruling untouched. |
| D3 | `MAX_REWIND_MS` | **250 ms** (S7 budget cap 200 + uplink + clock error; stated default, unobjected) |
| D4 | Defender grace | **(a) widen success grace 50 → 150 ms** (constant, parry+block, shared by NPC and player hits). Deferred resolution declined; the late-press case is recorded as accepted exposure (§3, §7). |
| D5 | v1 exclusions | **Confirmed** — server-tick auto swings, projectile impacts, and cone/radius sweeps stay present-time, recorded as deliberate with §7 extensions. |

## 7. Recorded gaps / future extensions (not this slice)

- **Auto-attack tick rewind**: needs a standing view-delay signal (slow-
  cadence client report, ~2 s, piggybacked on `ping_clock` or the ack
  surface) — revisit if shaped autos feel unfair after S8.
  **→ Taken up as S9** (`docs/auto-attack-rewind-design-2026-07-04.md`),
  which also ships the `[DEFENSE_LATE]` telemetry D4b is parked on.
- **Per-victim rewind for sweeps/projectile impacts**: the full model;
  only if targeted-action fairness proves insufficient. (S10 candidate,
  sequenced after S9 — reuses S8 machinery only.)
- **Deferred defense resolution** (D4b) — revisit with parry telemetry.
  (S11 candidate: decide on S9's `[DEFENSE_LATE]` numbers; if taken,
  design the hold around the defender's standing delay, not a flat
  150 ms.)
- **Server-side RTT/view-delay estimation** as a cross-check on client
  reports (anti-cheat hardening, post-launch concern per §8 of the review).

## 8. Churn notes

- Schema: two private tables (+ barrier stamp), new args on `melee_attack`
  and `cast_request` — bindings regen (canonical bin-path), no public-row
  changes, no client subscription changes.
- Existing headless probes that press attacks (`retired pre-cutover S4 LOS harness`,
  `ops/s6-auto-swing-probe.py`) must add the new reducer args (0 is a valid
  no-report value).
- Tests: deferred until the contract stabilizes (standing churn ruling).

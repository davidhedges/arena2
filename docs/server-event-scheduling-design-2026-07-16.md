# Server Event Scheduling & Execution Architecture

Date: 2026-07-16 (rewritten same day — v1 renamed phases inside the
monolithic tick without answering the architecture question; this version
compares real execution architectures)
Status: PROPOSED (design only — no implementation)
Companions: `docs/netcode-design-review-2026-07-03.md`, `docs/lag-compensation-design-2026-07-04.md`,
`docs/server-tick-compute-audit-2026-07-02.md`, `docs/perf-opportunities-2026-07-11.md`

## 1. The actual question

Today almost all gameplay work executes inside one 33 ms scheduled
transaction. A melee impact authored to land at `t` resolves at the next
tick ≥ `t` — mean +16.5 ms, worst +33 ms — behind whatever unrelated work
that tick carries (NPC decisions, projectile integration, regen,
maintenance). The question this design must answer: **which latency-sensitive
PvP events should get their own scheduled execution path, and what is the
smallest architecture that provides it?**

Scope: scheduling and execution architecture only — no combat-rule or
data-model changes. §4's semantic rules (ordering, timestamp domains,
determinism, recurrence) are architecture-independent and required under
*every* option below.

## 2. Substrate facts: what SpacetimeDB actually provides

Four concepts that v1 of this doc conflated, now separated:

| Concept | What it is | Who controls it |
|---|---|---|
| **Semantic ordering** | Which of two competing events wins (cancel vs. completion) | Consumer code, via timestamp domains (§4.3) |
| **Intra-transaction phase order** | Function call order inside one reducer transaction | Module code; atomic — nothing interleaves inside it |
| **Scheduling** | *When* a transaction is enqueued: on client-message arrival, on a chain of `ScheduleAt` rows, or at an exact `ScheduleAt::Time` | Module (rows) + host (timers) |
| **Runtime transaction priority** | Preempting or reordering enqueued transactions | **Does not exist.** All reducers serialize into one committed order; nothing jumps the queue |

Consequences that constrain every option:

- A transaction in flight blocks everything behind it for its full duration.
  The **only** lever against head-of-line blocking is transaction *size* —
  no architecture on this substrate can prioritize a press over a running
  tick.
- Independently scheduled execution paths available: **(P-A)** client-message
  arrival, **(P-T)** fixed-rate chains of one-shot `ScheduleAt::Time` rows
  (the game loop, S5), **(P-X)** exact-time one-shot `ScheduleAt::Time` rows
  per event, **(P-W)** host-managed `ScheduleAt::Interval` (the watchdog).
  Nothing else is a "path" — sub-rate clocks checked inside the tick are
  intra-transaction gating, and are not called lanes/paths in this doc.
- Host scheduling overhead is real but small: the S5 measurement clocked the
  old Interval-driven loop at 36.6 ms against an authored 33 ms (~3.6 ms
  per-fire overhead). Exact one-shot fire latency has **never been measured
  in this deployment** — Phase 0 measures it, and the recommendation is
  gated on the result.
- Reducers roll back atomically on panic/Err; a one-shot row consumed by a
  failed transaction is gone. Any P-X design needs a sweep backstop.

## 3. As-built survey

### 3.1 What executes where today

- **P-A (arrival):** press reducers validate and commit intent immediately —
  cooldowns, GCD, reach/facing/LOS with S8 rewind (`melee_attack`
  `melee.rs:3949`; `cast_request` executes instant/channel/zero-cast spells
  inline, `spells/mod.rs:492`). They then author deadline rows: melee
  impacts at press + authored `impact_delay_ms` (`melee.rs:2235`), timed
  casts as `pending_cast_request` with start backdated to receipt
  (`casting.rs:582-586`). Movement commands buffer per input tick.
- **P-T (33 ms tick chain, `game_loop.rs:2042`):** everything else, in fixed
  phase order (`game_loop.rs:895-1148`): status view collect → charge
  states/practice → movement actions → gap-close → special movement → cast
  **completion** latch (`tick_active_casts`, early) → combo followups →
  auto-attack due swings → NPC combat (resolves its own due windups first,
  `npcs.rs:1283`) → combat **Pass A** (due melee impacts → projectile
  releases → area impacts → pending effects → expiries) → projectile/bespoke
  integration → periodic DoT/HoT → **Pass B** → passives/auras/emanations →
  player kinematics + regen → `resolve_pending_casts` (queued-cast *intake*,
  gated on `last_processed_tick ≥ cast_input_tick` — must follow player sim;
  starts casts, never resolves damage) → maintenance (prune on 500 ms
  boundaries, corpse despawn, countdowns, invites).
- **P-W:** 1 Hz watchdog re-seeds a dead tick chain. Chain catch-up capped
  at 3 ticks, then re-anchor (backlog time dropped).

### 3.2 Ordering mechanics already in place

- Deadline rows carry btree-indexed `*_at_micros`; resolution is a range
  scan + explicit sort. All HP/status mutations flow through one choke
  point: `resolve_pending_effects` (`combat.rs:3768`), applied in a global
  monotonic `queued_order` from the single-row `pending_effect_sequence`
  table — **monotonic across transactions**, not just within one.
- The resolve functions are standalone-callable and idempotent
  (existence-check before resolve, delete after; `resolve_pending_melee_impacts(ctx, now)`
  is already invoked from two contexts today). This is what makes P-X
  feasible without restructuring.
- NPC decisions run on per-NPC authored cadence (`decision_interval_ms`,
  `npcs.rs:1661`), not every tick.

### 3.3 Audit findings (independent of architecture choice)

- **A1 — Unsorted actor iteration.** `tick_auto_attacks` (`auto_attack.rs:359`),
  `tick_npc_combat` (`npcs.rs:1285`), `run_player_simulation_phase`
  (`game_loop.rs:1128`), and `tick_special_movement_runtimes` iterate raw
  table order; same-tick cross-actor races can depend on storage order.
- **A2 — Resolve-time anchoring drift.** The next auto-swing anchors on the
  tick's `now`, not the authored `next_swing_at` (`auto_attack.rs:771`) —
  every period stretches by the alignment remainder (mean ~16.5 ms).
- **A3 — No overload policy.** All due work always runs; sustained overrun
  dilates the whole simulation.
- **A4 — Tiebreak gaps.** `resolve_pending_casts` sorts by
  `received_at_micros` only; player melee impacts (`melee.rs:4031`) and
  projectile releases (`melee.rs:4158`) tiebreak on per-attack `hit_index`,
  which different actors share — and same-tick dispatches share `resolve_at`
  exactly. The NPC variant already uses `(resolve_at_micros, impact_id)`
  (`melee.rs:1106`). Fix: unique row ID as final key everywhere.
- **A5 — In-memory state is per-wasm-instance.** Instances are pooled
  (`game_loop.rs:370-375`); scheduling state that affects gameplay must live
  in tables.
- **A6 — Missed periodic intervals are silently dropped.**
  `process_periodic_status_ticks` emits one packet then skips `next_tick_at`
  past every stalled interval (`combat.rs:5832`).
- **A7 — Wall-clock is diagnostics-only in the deployed module.**
  `ScopeTimer` reads zero on wasm (`tick_metrics.rs:82-93`); production
  scheduling decisions must derive from transaction data (scheduled-time
  lateness, work counts), never measured wall time.

## 4. The semantic layer (holds under every architecture)

### 4.1 Event categories

| # | Category | Examples | Path | May be shed? |
|---|----------|----------|------|--------------|
| C1 | Ability input & cancels | melee press, cast/release/cancel, dodge, block/parry | P-A | never |
| C2 | Movement input intake | `player_command` buffering | P-A | never |
| C3 | Control-flow advance & interrupts | cast completion/cancel latch, movement-action cancel, gap-close, combo followups | P-T (§6: completion optionally P-X) | never |
| C4 | Server-initiated attacks | auto-attack due swings, NPC swings/casts | P-T, due-gated | soft (cadence stretch) |
| C5 | Damage & status resolution | melee impacts, projectile releases, area impacts, pending effects | P-T Pass A/B (§6: player-combat subset optionally P-X) | never |
| C6 | Continuous simulation | projectile integration, kinematics, regen | P-T every tick | never |
| C7 | Periodic effects | DoT/HoT, equipment periodic, auras, emanations, passives, expiries | P-T at authored interval | never (per-interval packets, §7) |
| C8a | Semantic timers | countdown → phase flip, corpse despawn (loot anchor), invite expiry | P-T, own clocks | never (gameplay deadlines) |
| C8b | Replication-history maintenance | event/prediction prune, backfills | P-T, 0.5–1 s clocks | deferrable, bounded |

### 4.2 Ordering rules

- **R1** — Arrival order is a determinism fact, not a fairness rule; it must
  never *decide* a race between competing combat events (see R9).
- **R2** — Within a tick, interrupt/completion latches (C3) run before new
  initiations (C4); completion vs. cancellation itself is decided by R9.
- **R3** — Due windups resolve before the same actor's next cadence fire.
- **R4** — All HP/status mutations apply only inside
  `resolve_pending_effects`, in global `queued_order`; everything else
  enqueues. This is what keeps damage serialized **regardless of which
  transaction calls the resolve function** — the choke point is a function +
  a global sequence, not a phase.
- **R5** — Effects enqueued by C4–C6 and periodic DoT/HoT resolve in the
  same tick's Pass B; Lane-arrival effects resolve next Pass A. Exception:
  ambient producers after Pass B (passives/auras/emanations,
  `game_loop.rs:1056`) resolve next tick (+1, accepted).
- **R6** — One movement command consumed per player per tick; queued casts
  execute only once `last_processed_tick ≥ cast_input_tick`.
- **R7** — Every due batch applies in `(deadline_micros, unique_row_id)`
  order; every actor sweep iterates a sorted stable key (closes A1/A4).
- **R8** — C8b may defer/reorder freely (replication footprint only); C8a
  may not.
- **R9** — Deadline-vs-arrival races arbitrate on **semantic timestamps**
  while still pending; **committed outcomes are never reversed**. Existing
  pattern: the 100 ms pre-end cancel grace (`casting.rs:1764`) vs. a cancel
  arriving post-commit recording only a pending cancel (`casting.rs:669`).
  New race pairs declare their clock in §4.3.

### 4.3 Timestamp domains

| Event | Clock |
|---|---|
| Press validation (reach/facing/LOS) | Rewound attacker-view time, ≤250 ms (S8) |
| Cast start / cooldown / GCD anchor | Press receipt time |
| Cast completion vs. cancel | Authored `ends_at` vs. client-observed remaining (grace arbitration) |
| Impact/effect resolution | Present time at resolve — rewind decides *connect*, present decides *resolve* |
| Deadline firing | Authored `*_at_micros`; semantics anchored on it however late execution runs |
| Periodic amounts | Authored interval count, independent of resolve time |

### 4.4 Recurrence rule (missed anchored deadlines)

- **One-shot committed deadlines** (impacts, releases, completions): always
  resolve, however late — the outcome is owed.
- **Committed-effect recurrences** (DoT/HoT): missed occurrences are owed —
  emit the missed per-interval packets (A6 fix), N = missed authored
  deadlines strictly before `expires_at` (half-open expiry preserved: a
  6 s/1 s DoT like `WARRIOR_CARVE_BLEED` ticks five times, `combat.rs:5786`).
  Per-interval packets, never one N × amount aggregate — crit/absorb/death
  are per-`PendingHit` (`combat.rs:4346`).
- **Initiation cadences** (auto-attack, NPC decisions, pulses): skipped,
  never replayed — re-anchor to the next authored grid point strictly in the
  future. Intrinsic auto-attacks bypass shared cooldowns (`melee.rs:323`); a
  10 s stall must not produce 10 catch-up swings.

### 4.5 Determinism

C1–C7 outcomes must be a function of table state + the transaction schedule,
independent of table iteration order, instance identity, or profiling.
Options that add execution paths (§5 A/B/C) make the *transaction schedule*
itself timing-dependent — an honest cost accounted per-option below. R7
sorts, table-resident state (A5), and RNG confined to the R4 resolve pass
are required everywhere; the arbiter is a fixture comparing canonicalized
event streams across insertion-order-permuted runs.

## 5. Architecture comparison

The candidates, each scored on: real execution path or function order;
tick-alignment latency; transaction size/contention; ordering & determinism;
damage serialization; failure & overload; complexity.

### Option D — Monolithic tick (baseline: today + §4 hardening)

- **Path?** No new path. Everything except press validation shares the 33 ms
  transaction; "priorities" are function order.
- **Latency:** committed deadlines resolve at next tick ≥ `t`: uniform
  0–33 ms, mean **16.5 ms**, on top of press→validation (already immediate)
  and replication (per-commit push, identical in all options). Presses wait
  behind an in-flight tick (p95 target < 20 ms).
- **Transactions:** ~30/s + presses. One big transaction; its duration *is*
  the press head-of-line worst case.
- **Ordering/determinism:** strongest — fixed interleaving, one schedule.
- **Damage serialization:** R4 choke point inside the tick.
- **Failure/overload:** watchdog re-seeds; overrun dilates uniformly.
- **Complexity:** zero new.

### Option A — Second higher-frequency combat tick (e.g. 120 Hz resolve chain)

A second fixed-rate chain running only the combat cycle (due impacts,
releases, area impacts, effects).

- **Path?** Yes — genuinely independent schedule.
- **Latency:** quantization drops to the combat period: at 8.33 ms, mean
  **~4.2 ms**, max 8.3 ms.
- **Transactions:** +120/s **regardless of activity** (~90/s of them empty
  scans at arena scale), each a small serialized transaction that can still
  delay a press by its duration. Idle suppression (kill the chain when no
  pending rows exist, re-arm on insert) is possible but adds lifecycle
  complexity.
- **Ordering/determinism:** periodic interleaving with the main tick is
  host-timing dependent; reproducibility requires recording the schedule.
  Status views split across two transaction families (the T1 shared-view
  optimization no longer covers combat resolution).
- **Damage serialization:** intact — R4 function + global `queued_order`.
- **Failure/overload:** a second chain needs its own watchdog and re-anchor
  policy; two chains compete during overload.
- **Complexity:** chain + watchdog + idle suppression + view rework. The
  polling shape buys *less* precision than Option B at *more* standing cost.

### Option B — Exact-time one-shot reducers for all committed deadlines

Every deadline row (impact, release, area impact, cast completion, DoT tick)
also inserts a one-shot `ScheduleAt::Time` row at its authored time,
targeting a thin reducer that calls the existing resolve function.

- **Path?** Yes — per-event exact scheduling; fires at authored time ±host
  jitter.
- **Latency:** quantization eliminated at the scheduling layer. Residual =
  host one-shot jitter (**unmeasured**; S5's data suggests low single-digit
  ms — Phase 0 measures) + occasionally waiting out an in-flight tick.
- **Transactions:** proportional to combat activity. Arena scale (~20
  actors): tens/s for impacts+releases, but **hundreds/s if DoT ticks and
  every periodic occurrence are included** — this is where B overreaches.
  Simultaneous deadlines coalesce naturally: the resolve functions batch all
  due rows, and later fires no-op (idempotent existence checks already in
  place).
- **Ordering/determinism:** per-event interleaving with the tick is
  timing-dependent. Mitigated by §4: each transaction is internally
  deterministic (R7), amounts/next-deadlines are anchored (§4.4) so ±ms of
  interleave cannot change *values*, and genuine races are governed by R9
  semantic time, not by which transaction ran first.
- **Damage serialization:** intact — same choke point, same global sequence.
- **Failure/overload:** a panicked one-shot is lost; the tick's Pass A sweep
  (unchanged) is the backstop → graceful degradation to Option D latency for
  that event. Under overload, exact fires keep combat resolution on time
  even while the tick dilates — but add transactions exactly when the host
  is struggling (bounded by committed actions, which are bounded by actors ×
  attack rates).
- **Complexity:** one scheduled table + one thin reducer + inserts at every
  scheduling site + prune-on-fire. Unscoped, it schedules rows for event
  classes (periodic ticks, ambient pulses) whose 16 ms alignment nobody can
  perceive.

### Option C — Hybrid: scoped exact-fire + tick as spine and backstop (recommended)

Option B restricted to the event classes where sub-tick timing is
player-perceivable and PvP-relevant, with the monolithic tick keeping
everything else and sweeping stragglers:

- **P-X classes:** player melee impacts + melee projectile releases (one
  table family, the highest-frequency PvP deadlines), then cast completions
  if the pilot measures well. **Not** P-X: DoT/HoT ticks, auras, pulses,
  NPC-source impacts (NPC cadence is already coarse), maintenance.
- The tick's Pass A continues to sweep all due rows unconditionally — the
  exact-fire row is an accelerator, never a correctness requirement. Lost
  one-shot ⇒ the event resolves at most one tick late (= today's behavior).
- **Latency:** mean deadline-resolve latency drops from ~16.5 ms to host
  jitter (~1–5 ms expected, Phase 0 to confirm) for the scoped classes.
  Cast-completion inclusion also tightens death-interrupt timing (a kill
  cancels the victim's in-flight action sooner).
- **Transactions:** tens/s at arena scale, activity-proportional, no idle
  cost.
- **Ordering/determinism/serialization:** as Option B; the scoped surface
  keeps the timing-dependent interleaving small and entirely within event
  classes whose semantics are already anchored + R9-arbitrated.
- **Failure/overload:** degrades to D per-event via the existing sweep; no
  new watchdog; overload adds only committed-action transactions.
- **Complexity:** smallest architecture that actually changes the latency
  class of PvP combat: one scheduled table, one ~20-line reducer calling
  `resolve_pending_melee_impacts` / `resolve_pending_projectile_releases`,
  one insert beside each existing deadline-row insert, S8-style config row
  as kill switch.

### What no option fixes

- **Press head-of-line blocking.** No priority exists; a press arriving
  mid-tick waits out the tick in every option. The lever is tick transaction
  size (existing p95 < 20 ms target) — and Option C helps indirectly by
  removing work from worst-case ticks, but no option removes the wait.
- **Client presentation delay**, typically the dominant term end-to-end.
  Server-side de-quantization is still worth having: it compounds with every
  authored windup, cadence, and interrupt race rather than sitting behind a
  fixed buffer.

## 6. Recommendation

**Option C**, adopted in this order and gated by measurement:

1. **Phase 0 measures the substrate** (below). If one-shot fire jitter
   turns out ≥ ~half a tick, exact-fire buys little — stop at Option D +
   §4 hardening and revisit only with a feel complaint in hand.
2. Pilot P-X on **player melee impacts + projectile releases** behind a
   config-row kill switch (the S8 pattern: flip without republish).
3. Extend to **cast completions** if the pilot's measured latency and the
   owner's feel check both pass.
4. Options A and B-unscoped are rejected: A pays standing cost for less
   precision than per-event exact fire; B-unscoped schedules transactions
   for event classes with no perceptual payoff.

The §4 semantic layer plus A1/A2/A4/A6 fixes proceed regardless — they are
prerequisites for C (anchored semantics and R9 arbitration are what make
timing-dependent interleaving safe) and pure wins under D.

## 7. Overload behavior (applies to the surviving architecture)

Governing rule: **never discard a gameplay outcome; coalesce overdue work
where authored-equivalent.** Per-interval packets for stalled DoTs (§4.4);
post-stall bursts resolve as one R7-ordered pass; continuous sim degrades
only by coherent time dilation (the existing 3-tick re-anchor). No
server-side projectile admission cap exists today (`casting.rs:3549`) and
adding one is a combat-visible change, out of scope.

The overload signal is **scheduled lateness** (`ctx.timestamp −
fired_row.scheduled_at` — transaction data, A7-safe), never wall time.
Ladder, implemented only on observed need (re-anchor warnings in normal
play): L1 defers C8b (500 ms staleness trigger, oldest-first bounded
per-tick quota, 1 s ceiling); L2 stretches NPC decision intervals; L3 is the
existing re-anchor. P-X one-shots are unaffected by the ladder — they carry
committed outcomes.

## 8. Phased plan

**Phase 0 — Measure the substrate (new, cheap, gates Phase 3).**
An ops probe (existing headless-probe pattern) that (a) schedules one-shot
rows at known times across load levels and logs fire deltas, and (b) samples
press-arrival wait behind tick transactions. Deliverable: measured one-shot
jitter distribution; go/no-go for exact-fire.

**Phase 1 — Determinism hardening (no behavior change).**
A1 sorted iteration; A4 unique final sort keys. Tests: sort-key units; the
§4.5 canonicalized + insertion-order-permuted fixture.

**Phase 2 — Anchors, recurrence, A6.**
A2 via skip-to-grid; A6 per-interval packet emission; sweep all deadline
consumers to §4.3/§4.4. Tests: cadence-drift probe (±1 ms over 2 min);
stall test (zero replayed swings, next swing on grid); DoT stall test (5
packets for the 6 s/1 s shape, per-packet amounts).

**Phase 3 — Exact-fire pilot (Option C, gated on Phase 0).**
Scheduled table + thin resolve reducer + inserts beside melee-impact /
projectile-release scheduling + config kill switch. Tests: measured mean
deadline-resolve latency for piloted classes vs. Phase 0 baseline; kill the
one-shot mid-flight (panic injection) and assert the tick sweep resolves the
event ≤ one tick late; determinism fixture re-run with exact-fire on.

**Phase 4 — Overload ladder (on observed need).**
Ledger + `tick_overload_state` + lateness-derived L1/L2. Tests: load-harness
ladder mechanics (signal trips/clears on real wasm, C8b defers/C8a doesn't,
quota honors trigger/ceiling, recovery to L0).

## 9. Success criteria

1. Determinism: canonicalized event streams identical across
   insertion-order-permuted runs, under both D and C configurations.
2. No drift, no loss, no replay: auto-attack period ±1 ms of authored; zero
   post-stall catch-up swings; DoT per-interval packet counts/amounts exact
   across stalls with half-open expiry preserved.
3. Latency (the point of the redesign): measured mean committed-deadline
   resolve latency for piloted classes drops from ~16.5 ms to the Phase-0
   jitter floor, verified live; with exact-fire killed, every event still
   resolves ≤ one tick late.
4. Ordering: R1–R9 hold under the probe suite, including the
   cancel-vs-completion race on both sides of `ends_at`.
5. Serialization: all HP/status mutations still flow through
   `resolve_pending_effects` in global `queued_order`, from every calling
   transaction.
6. Disruption: no public gameplay table schema changes except the additive
   scheduled table (+ `tick_overload_state` if Phase 4 runs); no client
   changes.

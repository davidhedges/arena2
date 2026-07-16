# Server Event Scheduling & Processing Lanes

Date: 2026-07-16
Status: PROPOSED (design only — no implementation)
Companions: `docs/netcode-design-review-2026-07-03.md`, `docs/lag-compensation-design-2026-07-04.md`,
`docs/server-tick-compute-audit-2026-07-02.md`, `docs/perf-opportunities-2026-07-11.md`

## 1. Purpose and scope

Classify all server gameplay work into named processing lanes with explicit
priorities, ordering guarantees, determinism requirements, and overload
behavior. Scope is **scheduling and execution order only** — no combat rule,
netcode, or data-model changes. The design mostly *names and hardens* the
structure `game_tick` already has, then closes the specific gaps found in the
audit (§2.4).

## 2. Current architecture (as-built survey)

### 2.1 Execution substrate

SpacetimeDB serializes every reducer as one transaction. Two entry classes
exist today:

- **Client-invoked reducers** run at arrival time in their own transaction
  (`melee_attack` at `melee.rs:3949`, `cast_request`/`release_cast_request`/
  `cancel_active_cast_request` in `spells/mod.rs`, `start_dodge`,
  `start_block`/`start_parry`, `send_movement_intent`, inventory/party/admin).
- **`game_tick`** (`game_loop.rs:2042`) — a 33 ms fixed-**rate** chain of
  one-shot `ScheduleAt::Time` rows, re-armed first inside each tick and
  anchored on the fired row's scheduled time (S5). Catch-up is capped at 3
  ticks, then the chain re-anchors and drops the backlog. A 1 Hz host-managed
  Interval watchdog re-seeds a dead chain.

### 2.2 What runs where today

Press-time reducers **validate and commit intent immediately** (cooldowns,
GCD, range/facing/LOS with S8 rewind, resource state), then either execute
inline or write a deadline row:

- `melee_attack`: fully executes at press; the hit lands later via a
  `pending_melee_impact` row (`resolve_at_micros` = press + authored
  `impact_delay_ms`, `melee.rs:2235`).
- `cast_request`: instant/channel/zero-cast spells execute at press
  (`cast_request_executes_immediately`, `spells/mod.rs:492`); timed casts
  press-validate then queue a `pending_cast_request`, executed by the tick
  only after the caster's movement stream has advanced through
  `cast_input_tick` — with the cast's effective start **backdated to receipt
  time** so the cast bar never rewinds (`casting.rs:582-586`).
- Movement: commands buffer per input tick (`player_command`); the tick
  consumes exactly one per player per tick, with fallback-to-latest-intent.

`game_tick` phase order (the de-facto priority list, `game_loop.rs:895-1148`):

1. Status view A collect → progression/resource sync, charge states, practice
2. Movement actions cancel → melee timed movements (gap-close) → special movement
3. Active cast **completion** latch (`tick_active_casts` — a due cast fires
   here, *early*, before any attack initiation) → movement-action mirror
   cleanup
4. Queued melee combo followups → auto-attack due swings → NPC combat
   (NPC lane internally resolves its own due windups first, `npcs.rs:1283`)
5. Combat cycle **Pass A** (if due work): melee impacts → projectile releases
   → area impacts → pending effects → dead-cleanup/expiry/defense prune
6. Projectile + bespoke-spell integration (gated on non-empty tables)
7. Periodic DoT/HoT + equipment periodic → combat cycle **Pass B** (if queued)
8. Stacking passives, auras, emanations → engagement/status expiry
9. Status view B collect → per-player kinematics + resource regen
10. `resolve_pending_casts` → post-tick maintenance (corpse despawn; event
    prune on 500 ms boundaries) → match/world maintenance (countdowns, invites)

Note the two distinct cast stages, easily conflated: `tick_active_casts`
(step 3) **completes** due casts near the top of the tick; `resolve_pending_casts`
(step 10) is queued-request **intake** — press-validated timed casts waiting
on the movement gate (`last_processed_tick ≥ cast_input_tick`). Intake *must*
sit after player simulation or the gate could not pass until the following
tick; its late position costs nothing because an accepted cast's effective
start is backdated to receipt, and intake only *starts* casts — it never
resolves damage.

### 2.3 Deadline and ordering mechanics already in place

- Deadline rows carry btree-indexed `*_at_micros` columns; resolution is a
  range scan `filter(..=now_micros)` + an explicit sort:
  `(resolve_at_micros, hit_index)` for melee impacts, `(resolve_at_micros,
  impact_id)` for area impacts, `(release_at_micros, hit_index)` for
  projectile releases.
- **All HP/status mutations flow through one choke point**:
  `resolve_pending_effects` (`combat.rs:3768`) applies hits/status
  apply/remove in a single pass sorted by a global monotonic `queued_order`
  (`pending_effect_sequence`). "Spells enqueue only" is an enforced lock.
- NPC decisions run on per-NPC authored cadence (`decision_interval_ms` with a
  per-identity jitter scalar, `npcs.rs:1661`), not every tick.
- Tick profiling measures phase p50/p95/p99/max against
  `TICK_BUDGET_MICROS` = 33 ms and counts over-budget streaks — measurement
  exists, but **no behavioral response** to overload exists beyond chain
  re-anchor.

### 2.4 Audit findings this design must close

- **A1 — Unsorted actor iteration.** `tick_auto_attacks` (`auto_attack.rs:359`),
  `tick_npc_combat` (`npcs.rs:1285`), `run_player_simulation_phase`
  (`game_loop.rs:1128`), and `tick_special_movement_runtimes` iterate raw
  table order. Cross-actor outcomes that race within one tick (two lethal
  swings the same tick, contested resource) can depend on storage order.
- **A2 — Resolve-time anchoring drift.** After a due auto-swing fires, the
  next swing anchors on the tick's `now`, not the authored `next_swing_at`
  (`auto_attack.rs:771`). Every period stretches by the tick-alignment
  remainder (mean ~16.5 ms — ~1.6 % at 1 s cadence). Same defect class the S5
  fixed-rate chain fixed for the tick itself. Other deadline consumers need
  the same audit (status `next_tick_at`, sequence steps, cooldown starts).
- **A3 — No overload policy.** All due work always runs; sustained overrun
  dilates the whole simulation (chain re-anchor). Maintenance work competes
  with combat work at equal priority.
- **A4 — Tiebreak gaps.** Three due batches lack a unique final sort key and
  fall back to raw scan order on equal deadlines: `resolve_pending_casts`
  sorts by `received_at_micros` only; player melee impacts sort
  `(resolve_at_micros, hit_index)` (`melee.rs:4031`) and projectile releases
  `(release_at_micros, hit_index)` (`melee.rs:4158`) — but `hit_index` is
  per-attack, so *different actors' first hits share index 0*, and impacts
  dispatched in the same tick share `resolve_at` exactly. The NPC-source
  variant already does it right: `(resolve_at_micros, impact_id)`
  (`melee.rs:1106`). Fix: unique row ID as the final key everywhere
  (arrival-assigned is fine here — it decides only exact-equal-deadline
  application order, which R9 semantic arbitration does not claim).
- **A5 — In-memory state is per-wasm-instance.** SpacetimeDB pools module
  instances; statics like the tick-profile window are explicitly
  instance-local (`game_loop.rs:370-375`). Any scheduling state that affects
  gameplay must live in tables, never statics.
- **A6 — Missed periodic intervals are silently dropped.**
  `process_periodic_status_ticks` queues one tick's amount, then skips
  `next_tick_at` past every interval that elapsed during a stall
  (`combat.rs:5832`). The anchor is correct (no drift), but a stall deletes
  DoT/HoT ticks instead of emitting the missed packets.
- **A7 — Wall-clock is diagnostics-only in the deployed module.** The wasm
  target has no `Instant`; `ScopeTimer` returns zero there
  (`tick_metrics.rs:82-93`) and real timings exist only as host-side console
  log lines. Phase timings and "over-budget" counts therefore **cannot drive
  any production scheduling decision** — a gameplay-affecting overload signal
  must be derived from transaction data (scheduled-time lateness, work-unit
  counts), never from measured wall time.

## 3. Design: lanes and categories

### 3.1 Lanes

- **Lane I — Immediate (press lane).** Client reducers, at arrival, own
  transaction. Validation, intent commit, deadline-row authorship. Never does
  bulk simulation. This is where responsiveness comes from; unchanged.
  **Caveat — logical lane, not runtime priority:** SpacetimeDB serializes all
  reducers on one transaction stream; a press arriving mid-`game_tick` waits
  for that transaction to commit. "Immediate" means *not quantized to the
  next tick boundary*, with worst-case delay equal to the in-flight
  transaction's remainder — which is why the tick budget targets (p95 < 20 ms)
  are also a Lane I latency guarantee, and why Lane I bodies must stay cheap.
- **Lane T — Simulation tick (33 ms).** The single `game_tick` pipeline.
  Within-tick phase order **is** the priority order; it becomes a normative
  contract (§3.3) instead of an implementation accident.
- **Lane S — Sub-rate.** Work gated below tick frequency: per-NPC decision
  cadence, 500 ms event prune, match/world maintenance, 1 Hz watchdog. Runs
  *inside* Lane T transactions (except the watchdog) but on its own clocks.

No new parallel state machine, no new scheduler process: lanes are a
classification and a set of rules over the existing substrate.

### 3.2 Event categories

| # | Category | Examples | Lane | Frequency | May be shed? |
|---|----------|----------|------|-----------|--------------|
| C1 | Player ability input & cancels | melee press, cast/release/cancel request, dodge, block/parry, target arm | I | on arrival | never |
| C2 | Movement input intake | `player_command` buffering, cursor pruning | I | on arrival | never |
| C3 | Control-flow advance & interrupts | cast completion/cancel latch, movement-action cancel, gap-close starts, special movement, combo followups | T | every tick | never |
| C4 | Server-initiated attacks | auto-attack due swings, NPC swings/casts | T (S cadence for NPC decisions) | every tick, due-gated | soft (cadence stretch) |
| C5 | Damage & status resolution | pending melee impacts, projectile releases, area impacts, pending hit/apply/remove | T | every tick, due-gated, ≤2 passes | never |
| C6 | Continuous simulation | projectile/bespoke integration, player kinematics, resource regen | T | every tick | never (governed by interest/load controls) |
| C7 | Periodic effects | DoT/HoT ticks, equipment periodic, auras, emanations, stacking passives, expiries | T | authored interval, resolved at tick | never (amounts anchored, §5) |
| C8a | Semantic timers | match countdown → phase flip, corpse despawn (deletes the loot anchor), party invite expiry | S | own clocks, due-based | never (gameplay deadlines) |
| C8b | Replication-history maintenance | combat/player event prune, prediction-result prune, backfills | S | 0.5–1 s clocks | deferrable, bounded |

C8a looks like maintenance but is **gameplay**: deferring a countdown delays
match start; deferring corpse despawn extends the loot window. Both are cheap
due-row scans — they stay on their clocks and are never load-shed. Only C8b
satisfies R8.

Watchdog is outside the taxonomy: immortal, untouched by any policy here.

### 3.3 Ordering guarantees (normative rules)

- **R1 — Arrival serialization is a determinism rule, not a fairness rule.**
  Lane I transactions apply in arrival order; no same-transaction races exist
  by construction. Arrival order alone must never *decide* a combat outcome
  between competing events (that would encode latency advantage as a combat
  rule) — see R9.
- **R2 — Interrupts before initiations.** Within a tick, C3 (completion/
  cancel/interrupt latches) runs before C4 (new server-initiated attacks); a
  cast cancelled this tick cannot also fire this tick. Completion vs.
  cancellation itself is decided by R9, not by phase order.
- **R9 — Semantic-time arbitration, for races still pending at observation.**
  When an arrival (cancel, interrupt, dodge, parry) races a due deadline
  (cast completion, impact), the consumer arbitrates on **semantic
  timestamps**, not on which transaction observed the other first — but
  **committed outcomes are never reversed**. The existing pattern to
  preserve: the 100 ms pre-end cancel grace (`casting.rs:1764`) accepts a
  cancel the client observably pressed before the bar completed, while a
  cancel arriving after the completing tick finds the outcome committed and
  records only a pending cancel (`casting.rs:669`). Grace mechanisms widen
  the pending window; they never roll back commits. (Grace-delayed
  finalization would arbitrate more races but delays every completion — a
  combat-rule change, out of scope.) New race pairs declare their clock in
  §3.4 rather than inheriting phase order.
- **R3 — Old damage before new swings per actor.** Due windups resolve before
  the same actor's next cadence fire is considered (already explicit in the
  NPC lane; for players it holds because a swing scheduled this tick is never
  due this tick).
- **R4 — Single mutation choke point.** All HP/status mutations apply only
  inside `resolve_pending_effects`, in global `queued_order`. Simulation and
  press lanes enqueue only. (Existing lock; now a stated invariant.)
- **R5 — Same-tick effect closure, with one documented exception.** Effects
  enqueued by C4–C6 and by periodic DoT/HoT resolve in that tick's Pass B.
  Effects enqueued by Lane I between ticks resolve in the next tick's Pass A.
  **Exception:** the ambient C7 producers that run *after* Pass B — stacking
  passives, auras, emanations (`game_loop.rs:1056`) — enqueue effects that
  resolve at the *next* tick's Pass A (+1 tick, accepted for ambient pulses;
  reordering them ahead of Pass B is a possible follow-up but changes
  observable ordering and needs its own sign-off). Consequence:
  press-to-damage adds at most one tick of alignment, ambient pulses at most
  two.
- **R6 — Movement/ability coherence.** One input command consumed per player
  per tick; queued timed casts execute only once `last_processed_tick ≥
  cast_input_tick` (existing rule, kept).
- **R7 — Total order inside batches.** Every due batch applies in
  `(deadline_micros, stable_id)` order; every actor sweep iterates in a
  sorted, stable key order (closes A1/A4).
- **R8 — Maintenance invisibility.** C8b may reorder or defer freely; it must
  never change a gameplay outcome, only replication-history footprint. C8a
  (semantic timers) is explicitly outside R8: it carries gameplay deadlines
  and follows C7 rules at sub-rate frequency.

### 3.4 Timestamp domains

Each event class arbitrates on one declared clock. The scheduler never
substitutes another; consumers own the rule, this table is the index:

| Event | Clock | Owner |
|---|---|---|
| Press validation (reach/facing/LOS) | Rewound attacker-view time, bounded 250 ms (S8) | `lag-compensation-design-2026-07-04.md` |
| Cast start / cooldown / GCD anchor | Press receipt time | `casting.rs` (backdating rule) |
| Cast completion vs. cancel | Authored `ends_at` vs. client-observed remaining, grace-window arbitration | `casting.rs:1764` |
| Impact/effect *resolution* (damage, status, defense state) | Present time at resolve — rewind decides *connect*, present decides *resolve* | `position_history.rs` header |
| Deadline firing | Authored `*_at_micros` (execution may lag ≤1 tick; semantics anchored, §4) | this doc |
| Periodic amounts | Authored interval count, independent of resolve time | §4 |

### 3.5 Determinism requirements

**Deterministic (C1–C7):** given identical table state, identical reducer
arrival order, and identical timestamps, outcomes must be identical —
independent of table iteration order, wasm instance identity, or profiling
state. Concretely:

- R7 sorted iteration everywhere an outcome can race across rows.
- RNG (crit rolls) drawn only inside the R4 resolve pass, hence in
  `queued_order` — draw order is deterministic even if the seed is not.
- No gameplay decision may read instance-local statics (A5). Statics remain
  legal for pure caches of immutable authored data (collision preload) and
  diagnostics.

Sorted iteration is **necessary, not sufficient**: byte-identical outcomes
also depend on floating-point evaluation order, `auto_inc` ID assignment,
RNG state, and any unordered collection inside individual systems. The
Phase-1 two-run fixture (§8) is the arbiter of determinism; A1/A4 fixes are
just the known offenders.

**Bounded-staleness only (C8b):** outcomes need not be reproducible, only
bounded (§6). C8a semantic timers follow the deterministic rules above.

## 4. Deadline handling without quantizing combat to the tick

**Principle: quantize execution, never semantics.** Deadline rows keep their
authored microsecond times; the tick resolves whatever is due (`≤ now`), and
every *derived* quantity is computed from the **authored deadline**, not the
resolve timestamp:

- next cadence/sequence anchor = previous authored deadline + authored period
  (fixes A2 exactly the way S5 fixed the tick chain);
- periodic `next_tick_at` += authored interval (never `now` + interval);
- cooldown/GCD starts = press receipt time (already the rule for cast starts);
- damage amounts for periodic effects computed per authored interval count,
  so a late resolve never changes totals, only observation time.

With anchoring in place, tick alignment costs only 0–33 ms of *observation*
latency (mean ~16.5 ms) on top of client presentation delay — no drift, no
accumulated error, no gameplay-visible quantization. Client-side prediction
(predicted swings, cast bars, S8 press-time rewind validation) already covers
the perceptual window.

**Recurrence rule — what happens when an anchored deadline is missed.**
Anchoring alone is ambiguous after a stall: the next authored deadline may
now be in the past. Three classes, three behaviors:

- **One-shot committed deadlines** (melee impacts, projectile releases, area
  impacts, cast completions): always resolve, however late, in R7 order.
  The action was already committed; the outcome is owed.
- **Committed-effect recurrences** (DoT/HoT, equipment periodic): missed
  occurrences are *owed* — the status application was a committed outcome
  whose total value includes its ticks. Emit the missed per-interval packets
  (§6, A6 fix), bounded by the effect's authored expiry.
- **Initiation cadences** (auto-attack `next_swing_at`, NPC decision timers,
  aura/emanation pulses): missed occurrences are **skipped, never replayed**
  — re-anchor to the next authored grid point strictly in the future
  (`anchor + k × period > now`, phase-preserving). Each initiation requires
  fresh validation at fire time; nothing is owed. Replaying would conflict
  with L3 coherent dilation and, because intrinsic auto-attacks bypass shared
  cooldowns (`melee.rs:323`), a 10 s stall would otherwise hammer a target
  with ~10 unthrottled catch-up swings across consecutive ticks.

The A2 fix must implement skip-to-grid, not naive previous-deadline
anchoring.

**Escape hatch (design for, don't build):** every resolve function already
takes `(ctx, now)` and is callable from any reducer. If a specific event
class is ever *measured* to need sub-tick firing, promote it by inserting a
one-shot `ScheduleAt::Time` row targeting a thin scheduled reducer that calls
the same resolve function — the pattern the game-loop chain already proves.
Rejected as a default: per-event scheduled rows multiply transactions and
interleave with the tick's status views for no measured benefit.

**Rejected: half-tick combat resolver.** A second 33 ms chain offset 16.5 ms
running only the combat cycle would halve mean observation latency but double
combat transactions and split the status-view invariants (T1 audit) for a
below-perception gain.

**Effect-generation policy (proc chains).** Resolve passes snapshot their due
batch up front; rows enqueued *during* a pass are not in that batch. The
resulting bounded ladder, stated as policy: effects enqueued by generation-1
resolution land in the same tick's Pass B (melee impact resolution
additionally drains its own per-impact fanout inline, `melee.rs:4057`);
effects enqueued during Pass B roll to the next tick's Pass A. No pass loops
to quiescence — chain depth per tick is bounded by construction, recursion is
impossible, and the worst added latency per extra generation is one tick.
Consumers must not assume same-tick visibility beyond one generation.

## 5. Data structures

- **Durable deadline queue (existing idiom, now canonical):** btree-indexed
  `*_at_micros` column + range scan + R7 sort. This *is* the priority queue —
  transactional, republish-safe, instance-safe. In-memory heaps are
  prohibited as authority (A5).
- **`TickWorkLedger` (new, in-tick struct):** per-category unit counters
  (rows resolved per C-class). Work units are transaction data — available in
  wasm, deterministic — and serve two roles: overload *attribution* and the
  L-ladder's secondary input. Phase micros stay in the profile sample as
  diagnostics only (A7).
- **`tick_overload_state` (new, single-row table):** `shed_level: u8`,
  `consecutive_late_ticks: u32`, `last_lateness_micros: i64`,
  `deferred_maintenance_since_micros: i64`. Table, not static, so pooled
  instances agree and decisions are replayable from the DB.

## 6. Overload behavior, starvation prevention, work limits

The governing rule: **never discard a gameplay outcome; coalesce overdue work
where the coalesced result is authored-equivalent.** Dropping combat events
breaks the game worse than a late tick does — but "process every intermediate
step" is not the only correct execution. Sanctioned coalescing (all already
implied by §4 anchoring, now explicit):

- Overdue periodic intervals resolve as **N ordered per-interval packets
  emitted from one row visit** (sequential `queued_order`) — not today's
  drop-all-but-one behavior (A6), and not a single N × amount packet: crit,
  absorbs, death ordering, and combat events are all per-`PendingHit`
  (`combat.rs:4346`), so aggregation would change combat semantics. Expiry
  stays half-open (an occurrence due at `now ≥ expires_at` is skipped,
  `combat.rs:5786` — a 6 s / 1 s DoT like `WARRIOR_CARVE_BLEED` ticks five
  times, not six), so N = missed authored deadlines strictly before
  `expires_at`, never `⌊duration/interval⌋`.
- A post-stall deadline burst resolves as a single R7-ordered pass; anchored
  semantics keep every amount and next-deadline correct regardless of how
  late the pass runs.
- Continuous simulation (C6) degrades only by **coherent time dilation**
  (L3): every actor and projectile advances by the same simulated dt.
  Explicitly rejected: analytic wall-clock catch-up for projectiles (it would
  desync them from the dilated players they're chasing) and any reduction of
  collision/sweep fidelity under load. **There is no server-side projectile
  admission cap today** — normal insertion is ungated
  (`casting.rs:3549`); only the load harness clamps its own input. Population
  is bounded organically (actors × cooldowns × lifetimes), and the interest
  controls govern *replication*, not simulation. Adding an admission cap
  would be a combat-visible rule change and is out of scope; if organic
  bounds ever prove insufficient, that becomes its own design.

**The signal is scheduled lateness, not wall time (A7):** `lateness =
ctx.timestamp − fired_row.scheduled_at` — pure transaction data, already
computed for re-anchor detection. A host that cannot sustain the 33 ms rate
fires late; that *is* overload. The ledger's work-unit counts attribute it;
wall-clock timers stay diagnostics-only. Load-shedding is inherently
host-coupled — the design keeps it replayable (decisions derive only from
data recorded in `tick_overload_state`) and confined to the sanctioned
elasticities below.

The ladder (evaluated once per tick from lateness + the ledger):

- **L0 (normal):** everything runs. Maintenance runs on its clocks.
- **L1 (lateness ≥ 1 tick period on ≥2 consecutive fires):** defer C8b to
  the next on-time tick. Starvation bound: a C8b job that reaches **500 ms**
  of staleness (half the 1 s ceiling) starts oldest-first catch-up with a
  bounded per-tick quota, regardless of lateness — never one unconditional
  flush. Quota is sized in Phase 3 from measured generation rates (both are
  ledger counters; a logged underrun is the resize signal).
- **L2 (lateness ≥ 2 tick periods sustained):** stretch NPC decision
  intervals by a load scalar (the per-NPC cadence function already composes
  scalars). Log one line per transition.
- **L3 (chain re-anchor, existing: backlog > 3 ticks):** backlog time is
  dropped — coherent dilation; deadline rows are *not* dropped and resolve
  per the burst rule above.

Together these bound the burst spiral: single-visit packet emission caps a
burst's cost, L2 sheds the largest elastic producer, and L3 caps backlog
depth at 3 ticks.

Per-tick work limits are **counters, not caps**, for C1–C7 (the ledger makes
overload attributable); caps apply only to elastic work: NPC decisions per
tick (via cadence stretch) and prune/catch-up batch sizes. Lane I intake is
bounded by player count × client send rate; per-connection rate limiting is
a transport concern, out of scope here.

## 7. Fit with existing architecture

Minimal disruption is the point:

- No new processes, threads, schedulers, or parallel state machines.
- Lane I / Lane T split, the phase order, deadline tables, the effect choke
  point, S5 chain, watchdog, S8–S10 rewind: all unchanged.
- The work is: (a) declare the rules (§3), (b) fix the audit gaps A1/A2/A4/A6
  inside existing functions, (c) add the ledger + one small table + the
  ladder gate around already-separable phases (maintenance is already a
  distinct phase; NPC cadence already accepts scalars).

## 8. Phased implementation plan

**Phase 1 — Determinism hardening (no behavior change intended).**
Sorted iteration for `tick_auto_attacks`, `tick_npc_combat`, player sim,
special movement (stable actor key); unique final sort keys per A4 (`caster`
tiebreak in `resolve_pending_casts`; `impact_id` on player melee impacts;
the release row's unique ID on projectile releases — matching the NPC
variant's existing `(resolve_at_micros, impact_id)` key). Phase-order
contract comment in `game_loop.rs` pointing at this doc.
*Tests:* unit tests on the new sort keys; a fixture determinism check via the
headless probe harness comparing **canonicalized** event streams (auto-inc
IDs stripped, timestamps normalized to tick-relative) across a run pair whose
equivalent-row **insertion order is deliberately permuted** — identical runs
would recreate the same table order and prove nothing.

**Phase 2 — Anchored-deadline + coalescing audit.**
Fix A2 with **skip-to-grid** (§4 recurrence rule — next swing on the next
authored grid point strictly in the future, never naive previous-deadline
anchoring); fix A6 (missed intervals emit N ordered per-interval packets, §6
— *not* one aggregate amount, N = missed deadlines strictly before expiry);
sweep all deadline consumers (status `next_tick_at`, auto-attack sequence
steps, melee followup windows, cooldown starts) to the §4 anchor + recurrence
rules; document each class decision inline.
*Tests:* headless cadence-drift probe (N auto-swings over ≥2 min: mean
period within ±1 ms of authored); cadence stall test (multi-second stall ⇒
zero replayed swings, next swing on the authored grid); DoT stall test
asserting per-interval **packet count and amounts** — not totals — with 5
packets for the 6 s / 1 s shape (half-open expiry preserved; fails today per
A6).

**Phase 3 — Ledger + overload ladder (on observed need).**
Trigger: `[GAME_LOOP] tick chain fell behind` re-anchor warnings appearing in
normal play — that log line already exists, so the "do we need this yet"
signal is free. Work: `TickWorkLedger`, `tick_overload_state`,
lateness-derived L1/L2 gates (A7), starvation bound.
*Tests (ladder mechanics, not a universal p95 guarantee):* drive sustained
load with `run_projectile_load_harness`; assert the lateness signal trips
and clears in a real wasm deployment, C8b defers under L1 while C8a stays on
schedule, catch-up honors the 500 ms trigger / 1 s ceiling / per-tick quota,
no C1–C7 row is left unresolved past its due tick, L2 stretches NPC cadence,
and the ladder returns to L0 when load stops.

**Phase 4 — conditional, needs a measured trigger.**
Exact-fire one-shot lane for one named event class, only if a feel/latency
measurement (not intuition) shows the ≤33 ms alignment matters.

## 9. Tradeoffs

- **Tick-resolved deadlines vs. exact-fire events:** we keep tick resolution
  and buy back correctness with anchoring. Cost: ≤33 ms observation latency.
  Gain: one transaction per tick, coherent status views, no interleaving
  hazards. The escape hatch preserves the option cheaply.
- **Never shedding combat vs. bounded ticks:** we accept overrun + dilation
  (L3) over dropping events. An arena game with tens of actors is compute-
  bounded by authored content, not open-ended load; correctness wins.
- **Tables over in-memory queues:** slower per-op, but transactional,
  republish-safe, and pooled-instance-safe. Non-negotiable given A5.

## 10. Success criteria

1. Determinism: the Phase-1 fixture produces identical **canonicalized**
   gameplay event streams across an insertion-order-**permuted** run pair; no
   gameplay outcome depends on table iteration order.
2. No drift, no loss, no aggregation, no replay: auto-attack mean period
   within ±1 ms of authored over 2 min; after an injected stall, zero
   catch-up swings and the next swing on the authored grid; DoT/HoT
   **per-interval packet counts and amounts** exact for any tick phase and
   across stalls, with **half-open expiry semantics preserved** (5 packets
   for the 6 s / 1 s shape; A6 fixed without changing per-packet
   crit/absorb/death semantics).
3. Ordering: R1–R9 hold under the probe suite, including a
   cancel-vs-completion race probe exercising the R9 grace-window arbitration
   on both sides of `ends_at` (and confirming a committed completion is never
   reversed).
4. Overload (ladder mechanics, at authored content scale): the lateness
   signal trips and clears in a real wasm deployment; C8b catch-up starts at
   500 ms staleness, stays incremental (no single-tick flush spike), and
   holds the 1 s ceiling; C8a semantic timers are never deferred; zero C1–C7
   *outcomes* discarded (§6 per-interval packets count as delivered);
   watchdog never fires during the test.
5. Disruption: no schema change to any public gameplay table in Phases 1–3
   except the additive `tick_overload_state`; no client change required.

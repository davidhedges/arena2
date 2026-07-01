# Server Tick Compute Audit — 2026-07-02

Server-side runtime cost slice: `game_tick` compute, table scans, row writes, NPC/
projectile/status loops, and write-driven replication pressure. Companion to
`docs/netcode-sync-audit-2026-07-02.md` (architecture/lifecycle/subscriptions) and
`docs/multiplayer-feel-audit-2026-07-02.md` (client feel). Findings from those audits
are referenced, not repeated — in particular netcode-R3 (per-table row-write
counters) is assumed as the write-side counterpart of this audit's instrumentation.

## Measurement status (read this first)

**Measured infrastructure exists; measured numbers do not.** The repo has two good
instruments and no recorded baselines:

- Tick-time profiler: 5s windows of p50/p95/p99/max plus 4 coarse phases, env-gated
  behind `ARENA_PROFILE_TICKS` (`server/src/game_loop.rs:124-345`), targets
  p95 < 20 ms / p99 < 28 ms on a 33 ms budget (`game_loop.rs:484-487`).
- Projectile tick metrics: per-tick row with rows-updated, broadphase/narrowphase
  counts, event counts, worst-tick micros (`server/src/combat.rs:536-595`,
  `server/src/combat/projectiles.rs:1844+`), surfaced by
  `Assets/Arena/Runtime/Debug/ProjectileLoadHarnessOverlay.cs`.

Everything below about *cost* is therefore **inferred from code shape** (call counts,
scan shapes, write sites are verified by reading; wall-clock impact is not measured).
Every risk lists the instrumentation to run before changing behavior. The one
finding that is arithmetic rather than inference — the per-tick call multiplicity of
the status-view rebuild — is exact: the call sites are enumerable.

**Context that bounds everything:** the entire server frame is a single scheduled
reducer, `game_tick`, at 30.3 Hz (`game_loop.rs:451-457,1437`). There are no other
scheduled reducers (verified: only `scheduled(game_tick)` exists). One transaction
per tick means every scan and write below serializes into the same 33 ms budget and
the same commit — a tick that runs long delays movement acks
(`PlayerPhysics.last_processed_tick`) for *every* client, which is exactly the
"server tick strain → multiplayer responsiveness" coupling this audit targets.

---

## 1. Top server tick strain risks

### T1 — `StatusRuntimeView::collect` is rebuilt O(players) times per tick

**The single biggest inferred cost in the tick path.** Every collect is a full
`status_effect` table scan plus a full alive-set build over players *and* NPCs, plus
per-row `String` materialization:

- `StatusRuntimeView::collect` (`server/src/combat.rs:5144-5148`) iterates the whole
  `status_effect` table, clones `stack_group` per row, and calls
  `alive_status_target_identities` (`combat.rs:5573-5589`) which builds a `HashSet`
  from indexed scans of `player_state` and `npc_state`.

**Verified call multiplicity per tick** (exact, from call sites):

- **6 × per alive non-dummy player** via the resource path:
  - Pre-tick, `sync_progression_runtime_rows` (`game_loop.rs:525,580-592`) →
    `sync_primary_resource_for_player` → `sync_resources_for_player` → one
    `resolve_resource_spec_for_owner_and_kind` per resource kind (STAMINA, MANA),
    each collecting a fresh view at `server/src/resources.rs:526-527` → **2**.
  - Player sim, `tick_player` → `tick_primary_resource_for_player`
    (`game_loop.rs:1397`, `resources.rs:238-257`) → `sync_resources_for_player`
    (**2** more) → then the per-row loop re-resolves the spec per resource
    (`resources.rs:250-251`) → **2** more.
- **1 × global** for movement modifiers (`game_loop.rs:577` → `combat.rs:5569-5571`).
- **1 × global** inside `tick_npc_combat` (`server/src/npcs.rs:453`), a second
  independent collect of the same data.
- **1 × per active cast** via `has_active_disabling_status`
  (`server/src/spells/casting.rs:1884` → `combat.rs:6921-6923`).
- **1 × per active movement-action row, twice per tick** — `tick_movement_actions`
  runs at `game_loop.rs:529` *and* `game_loop.rs:537`, and each active row calls
  `has_active_disabling_status` (`server/src/movement_actions.rs:889`).
- **1 × per auto-attack (re)arm** (`server/src/auto_attack.rs:275,789`).
- **1 × per bloodlust-eligible player** — `nearby_bleeding_hostile_target_count`
  (`combat.rs:1113-1144`) is its own full `status_effect` scan with per-row dispel
  string decode plus `can_harm`/alive/position lookups per row.
- Event-driven: `resolve_pending_effects` keeps a cached view but invalidates it on
  every applied status change or lethal damage (`combat.rs:3427-3451,3470-3511`), so
  an AoE burst can re-collect once per pending effect within one tick.

**Suspected cost / scaling shape.** O(P × (S + P + N)) row materializations per tick
— P players, S status rows, N NPCs — with HashMap/HashSet churn and String clones on
each. Illustrative arithmetic (not a measurement): at 32 players, 150 status rows,
40 NPCs → ≈ 200 collects × ≈ 220 rows ≈ 44k row materializations per tick ≈ 1.3M/s
at 30.3 Hz, before any combat happens. This grows *quadratically-ish* with
population because both the number of collects and the size of each collect scale
with P.

**Better contract.** Status state is read through at most **two tick-scoped views**:

- View A, collected once at the top of pre-tick housekeeping — used by
  `sync_progression_runtime_rows`, `tick_movement_actions` (both calls),
  `tick_active_casts`, `tick_auto_attacks`, and `tick_npc_combat`. All of these run
  *before* the status-mutating phases (Pass A `resolve_combat_cycle` at
  `game_loop.rs:546`, periodic/aura/passive phases at 560-575), so a start-of-tick
  view is semantically what they read today.
- View B, collected once where `movement_modifiers(ctx, now)` already collects
  (`game_loop.rs:577`, after `expire_status_effects`) — extended to also retain the
  `StatusRuntimeView` / `TemporaryCombatModifiers`, and threaded through
  `run_player_simulation_phase` into `tick_primary_resource_for_player` /
  `resolve_resource_spec_for_owner_and_kind`. No statuses are mutated during the
  player-sim phase (`tick_player` writes physics/intent/resources only), so one view
  is valid for the whole phase.
- `resolve_pending_effects` keeps its existing invalidating cache (correct as-is).

Per-tick collects drop from ~6P+extras to **2 + event-driven**, independent of P.

**Why it improves multiplayer responsiveness.** It removes the dominant
population-scaling term from the pre-tick and player-sim phases — the phases that
sit between "tick starts" and "physics + ack rows commit." Lower and flatter tick
time = acks and authoritative state reach clients on cadence at higher player
counts.

**Bounded implementation slices (smaller-model friendly, in order):**

1. *Instrument only* (see §2): count collects and status rows per profile window.
2. *Resource path only* (biggest win, mechanical): add a
   `&TemporaryCombatModifiers` (or `&StatusRuntimeView`) parameter through
   `tick_primary_resource_for_player` → `sync_resources_for_player` →
   `sync_resource_for_player` → `resolve_resource_spec_for_owner_and_kind`
   (`resources.rs:191-257,510-559`); also hoist the duplicate spec resolution out of
   the `tick_primary_resource_for_player` per-row loop (it re-resolves what
   `sync_resources_for_player` just resolved). Callers pass view A (pre-tick sync)
   and view B (player sim).
3. *Global callers*: replace the collects in `tick_npc_combat`,
   `tick_movement_actions`, `tick_active_casts`, `tick_auto_attacks` with view A
   passed as a parameter; add `StatusRuntimeView::has_disabling_status`-style
   accessors where the free functions (`combat.rs:6895-6923`) were used.

**Files/surfaces.** `server/src/resources.rs`, `game_loop.rs`, `combat.rs` (view
API), `npcs.rs`, `movement_actions.rs`, `spells/casting.rs`, `auto_attack.rs`.
Server-only; **zero schema change, zero binding regen, zero client change.**

**Instrumentation before change.** §2 counters (collect count + collect micros +
status row count per window). Record a baseline with 2 clients + practice actors.

**Verification / load test.** `cargo test` in `server/`; run
`ARENA_PROFILE_TICKS=1` before/after under the same scenario (see §2 load recipe)
and compare `pre`/`player` phase p95 plus the new collect counter (expect: collects
per tick ≈ 2, phase times flat as dummies/practice actors scale). Gameplay parity
checks: root/slow still applies to movement the same tick it lands (view B is
collected after status resolution, same as today's `movement_modifiers`); regen
bonuses from statuses still apply within the same tick.

**Risks / non-goals.** View A is marginally staler than a mid-phase collect for
early housekeeping consumers — but those consumers run before status mutations
today, so semantics are preserved; state changes mid-tick were already invisible to
earlier phases. Do not cache views *across* ticks. Do not touch
`resolve_pending_effects`' invalidation. Non-goal: changing what any status does.

---

### T2 — Per-player equipment/derived-stat recompute, 5×+ per player per tick

**Repo evidence.** `equipment_modifier_totals_for_owner`
(`server/src/inventory.rs:1883-1963`) walks the equipment loadout, resolves each
item definition, filters + sorts `item_affix_instance` per item, and validates each
affix. It runs per alive player per tick:

1. `tick_player` movement-speed calc (`game_loop.rs:1372`);
2. 3 × via the resource path — every MANA spec resolution calls it
   (`resources.rs:528-532`): pre-tick sync, player-sim sync, and the per-row loop;
3. `tick_equipment_periodic_effects` (`inventory.rs:1988`).

`derived_combat_stats_for_owner` is similarly computed twice per player per tick
(`game_loop.rs:1369` and `sync_player_state_derived_stats` at `combat.rs:294` via
`sync_progression_runtime_rows`). Today it is cheap **only because
`active_stat_totals_for_owner` is a stub returning defaults**
(`server/src/progression.rs:2183-2190`) — the moment stat allocation ships, this
silently becomes 2 × P × (whatever that query costs) per tick.

**Suspected cost / scaling shape.** O(P × items × affixes) indexed lookups plus
sorts and allocations, ×5 redundancy. Not the biggest term, but pure waste: within
one tick the loadout cannot change (equip reducers run outside `game_tick`).

**Better contract.** A per-tick, per-player memo: compute equipment totals (and
derived stats) at most once per player per tick and pass by reference to the speed
calc, resource specs, and periodic-effects loop. Natural composition: the same
threading pass as T1 slices 2-3 (the signatures being touched are the same).

**Why it improves responsiveness.** Directly shrinks the player-sim phase, which
runs between input consumption and the physics/ack commit.

**Bounded slice.** Server-only. Add
`struct PlayerTickContext { equipment: EquipmentModifierTotals, derived: DerivedCombatStats }`
built once per player at the top of `tick_player`, passed down; have
`tick_equipment_periodic_effects` accept a prebuilt map or fold it into the same
per-player loop. No schema change.

**Files/surfaces.** `game_loop.rs`, `resources.rs`, `inventory.rs`,
`derived_stats.rs`.

**Instrumentation before change.** Add an equipment-scan counter next to the T1
collect counter (one `AtomicU64` incremented in
`equipment_modifier_totals_for_owner`, reported per profile window). Expect exactly
5 × alive players per tick before, 1 × after.

**Verification.** `cargo test`; equip/unequip mid-combat and confirm move speed,
mana regen, and health-regen ticking behave identically (all consumers read the
same-tick memo).

**Risks / non-goals.** None material — all consumers already read within one tick.
Non-goal: caching across ticks (equip reducers can run between ticks).

---

### T3 — Unconditional per-tick row writes (compute + commit-log + replication churn)

**Repo evidence — write sites that fire every tick regardless of change:**

- **Stationary training dummies & playground targets rewrite `PlayerPhysics` every
  tick.** `settle_stationary_dummy` / `settle_stationary_playground_target` stamp
  `updated_at = now` (`game_loop.rs:710-725`) and commit unconditionally
  (`commit_player_physics` always updates — `server/src/player_physics.rs:129`).
  `player_physics` is public and inside the scoped subscription
  (`GameplaySubscriptionPlanner.BuildScopedPlayerPhysicsQuery`), so every dummy
  produces a 30.3 Hz replicated row update to every client in scope, forever, while
  standing still. Unlike live players, dummies have no ack semantics
  (`last_processed_tick` never advances), so a skip-when-unchanged is safe here.
- **`fixed_action_charge_state` is upserted every tick per dodge row** even at full
  charges and not recharging (`movement_actions.rs:679-688` →
  `upsert_fixed_action_charge_state` at 759-771, unconditional `update`). Public
  table, owner-filtered subscription (`GameplaySubscriptionPlanner.cs` local group)
  → per-player per-tick replicated update to its owner (pending verification of
  whether SpacetimeDB elides byte-identical updates — instrument first).
- **`PlayerIntent` is updated unconditionally per player per tick**
  (`game_loop.rs:1390-1396`), including the fallback path where values are
  unchanged except `input_tick`/`updated_at`. The table is public
  (`player_intent.rs:22`) but **no client query subscribes to it** (verified:
  absent from `GameplaySubscriptionPlanner`), so today this is commit-log/index
  cost only — but it is also the second-largest write family after physics.
- **`npc_combat_runtime` is upserted every tick per hostile NPC with a target**,
  including the pure "waiting for next attack" branch (`npcs.rs:505-509,913-925`).
  Private table → commit-log cost only.
- **`combat_stacking_passive_runtime` is upserted every tick per eligible in-combat
  player** even when nothing is due (`combat.rs:1457-1465`). Private table.
- **`equipment_periodic_runtime` is written every tick per player with health-regen
  gear** (`inventory.rs:2028-2039`), accumulator design makes the row genuinely
  change each tick — acceptable, but it belongs in the write counters.

**Suspected cost / scaling shape.** O(P + D + N) writes per tick (D dummies). Each
write is index maintenance + commit-log entry; the public ones are candidate
subscription deltas. At 32 players + 10 dummies: ≈ 4-6 writes/player/tick ≈
150-200 writes/tick ≈ 5-6k/s — consistent with (and extending) the netcode audit's
~117-150 rows/tick estimate, which counted physics/intent/resource only.

**Better contract.** Hot-path writes are **change-gated**: compare against the
current row (already fetched in every one of these paths) and skip the update when
nothing but `updated_at` would change. Explicit exception, documented at the write
site: the local player's `PlayerPhysics` commit is never skipped — it carries the
`last_processed_tick` ack (netcode audit §3 design caution).

**Why it improves responsiveness.** Fewer writes per tick shrinks commit/serialize
time inside the same 33 ms budget, and the replicated ones (dummy physics, charge
state) directly reduce per-client bandwidth and client-side row-callback work.

**Bounded slices.**
1. Dummy/playground physics: in `try_tick_dummy_player` /
   `try_tick_playground_target`, skip `commit_player_physics` when position,
   velocity, yaw, and grounded are unchanged (don't stamp `updated_at` on no-ops).
2. `tick_fixed_action_charge_states`: skip upsert when the synced row equals the
   stored row.
3. `upsert_npc_combat_runtime` / stacking-passive runtime: same value-compare skip.

Each is a ~10-line server-only diff, independently shippable.

**Files/surfaces.** `game_loop.rs`, `movement_actions.rs`, `npcs.rs`, `combat.rs`.
No schema change.

**Instrumentation before change.** Netcode-R3's per-table write counters are the
gate: implement those first (they were already recommended) and record which tables
dominate. For the replication question specifically, also watch the client side:
`EntityRegistry` per-table callback counters (netcode-R3 client half) will show
whether dummy physics and charge-state updates arrive at 30 Hz.

**Verification / load test.** Training scene with ≥ 8 dummies + 2 clients: write
counters for `player_physics` drop from `P+D` to `P` per tick; dummies still render
correctly on a fresh client join (initial subscription snapshot covers them — their
row still exists, it just stops churning); dodge charges still replicate on
consume/recharge; NPC attack cadence unchanged (`cargo test` + manual aggro check).

**Risks / non-goals.** Client interpolation of a dummy that stops receiving updates:
remote presentation buffers hold last position (velocity zero), so a stationary row
going quiet renders identically — verify once with a client. Do NOT change-gate the
live-player physics commit or anything carrying ack/tick counters. Non-goal:
changing `PlayerIntent`'s contract (see §3).

---

### T4 — NPC combat loop: O(NPCs × players) targeting with per-pair context resolution

**Repo evidence.** `tick_npc_combat` (`npcs.rs:452-539`) runs every tick and, per
hostile NPC, calls `acquire_npc_attack_target` (`npcs.rs:554-607`) which loops over
**every** `player_state` row and per pair calls `players_share_world_context`
(`server/src/arena.rs:1025-1042` → `resolve_player_world_context` at 978-1009: 1-2
indexed finds plus a `String` scene-name clone per call) and `can_harm`
(`relations.rs:143` → party/faction lookups), then a physics find. The loop also
does its own `movement_modifiers` collect (counted in T1) and per-NPC
template/state/physics finds.

**Suspected cost / scaling shape.** O(N × P) pairs per tick, each pair costing
several indexed lookups plus string allocation — the classic quadratic-ish shape
that is invisible at 3 NPCs + 2 players and dominant at 50 NPCs + 30 players in one
open-world scene. No spatial or context pre-filtering, despite an existing spatial
index pattern to copy (`PlayerSnapshotSet` buckets,
`server/src/combat/player_snapshot.rs:163-283`).

**Better contract.** Once per tick, build the world-context grouping: resolve each
alive player's context once (O(P)), bucket players by context (and optionally into
the existing 8 m spatial cells); per NPC, resolve its context once and iterate only
the matching bucket, using squared-distance pre-check before `can_harm`. Semantics
identical to `players_share_world_context` by construction.

**Why it improves responsiveness.** Caps the NPC phase's growth as zones get
populated; NPC pursuit/attack runs pre-tick, ahead of player simulation, so its
overruns delay every ack.

**Bounded slice.** Server-only: a `NpcTargetingContext` built at the top of
`tick_npc_combat` (players grouped by resolved context with positions/hit data),
`acquire_npc_attack_target` rewritten to consume it. Reuse view A from T1 for the
movement-modifiers input.

**Files/surfaces.** `npcs.rs`, small helper in `arena.rs` (batch context resolve).

**Instrumentation before change.** Add `npc_count` and `npc_target_pairs_scanned`
to the §2 profile line (two counters in the loop). Baseline with a spawned NPC pack.

**Verification / load test.** Spawn N NPCs (existing spawn reducers) with 2+
players split across two scenes: NPCs must only ever target same-scene players
(existing behavior), aggro radius honored, pair-scan counter drops from N×P to
N×P_same_scene. `cargo test`.

**Risks / non-goals.** Keep target *selection* order/tie-breaking identical
(nearest-wins, `npcs.rs:581-586`). Non-goal: NPC AI changes, aggro tables, spatial
AoI for replication.

---

### T5 — Aura / bloodlust ticking scans the status table with string filters every tick

**Repo evidence.** Every tick, unconditionally:

- `tick_auras` (`combat.rs:1188-1325`) does **two** full `status_effect` scans with
  per-row `stack_group.starts_with(AURA_STACK_GROUP_PREFIX)` string tests (owner
  collection at 1190-1196, stale sweep at 1304-1321), builds a candidate list of
  all alive players, and per aura-owner × candidate pair calls
  `players_share_world_context` + `target_audience_allows` + a physics find, plus
  `should_refresh_aura_status` (per-target indexed filter) per effect.
- `tick_combat_stacking_passives` (`combat.rs:886-943`) builds owner sets from
  **two full `player_state` scans** (restless + bloodlust) every tick and runs
  per-owner eligibility for every player, whether or not they run the TwoHandedSword
  profile; bloodlust-eligible owners then pay the full status scan in
  `nearby_bleeding_hostile_target_count` (already counted under T1's tail).

**Suspected cost / scaling shape.** O(S) string-filter scans ×3 per tick plus
O(aura_owners × P) pair work and O(P) eligibility probes ×2. Small today (auras and
the passives are niche), but it is the same unbounded shape as T1/T4 and sits in the
same pre-tick phase.

**Better contract.** (a) Drive aura/passive owner discovery from the tables that
already key them (`active_aura`, `combat_stacking_passive_runtime`, plus an
eligibility check only for players whose active discipline matches the spec's
profile — an indexed `active_combat_discipline` filter) instead of full player
scans. (b) Reuse view A (T1) for the status-side owner discovery instead of raw
scans with string prefixes. (c) The stale-aura sweep keys on data already inside
view A.

**Why it improves responsiveness.** Removes three more per-tick full scans and the
last per-tick O(P) loops that run even when the feature is unused.

**Bounded slice.** Server-only refactor of the two owner-collection blocks; keep
per-owner tick logic untouched.

**Files/surfaces.** `combat.rs` only.

**Instrumentation before change.** The §2 collect/scan counters cover it (attribute
scans by call site label).

**Verification.** Existing aura/passive unit tests in `combat.rs` tests module;
runtime: aura buff application radius/refresh unchanged; restless/bloodlust
stack gain/decay cadence unchanged with a TwoHandedSword character.

**Risks / non-goals.** Owner discovery must still find statuses whose backing
runtime row was deleted (the current status-scan arm exists for cleanup) — view A
provides that. Non-goal: redesigning aura stacking.

---

## 2. Safest first instrumentation slice

All log-only, behind the existing `ARENA_PROFILE_TICKS` gate, zero schema change,
zero client change, no behavior change. This extends the existing profiler rather
than adding a new system, and deliberately does not duplicate netcode-R3 (per-table
row-write counters) — implement that alongside.

1. **Sub-phase timing inside pre-tick.** The pre-tick phase aggregates ~15
   subsystems (`game_loop.rs:520-578`) into one number, so a spike is
   unattributable. Extend `TickProfileSample` with per-subsystem micros for:
   progression/resource sync, movement actions, special movement, active casts,
   melee followups, auto attacks, NPC combat, combat-cycle resolution, spell/
   projectile sim, periodic statuses, passives+auras, and expiries. Same
   percentile/window machinery (`TickProfileWindowState`), one wider
   `[TICK_PROFILE]` line or a second `[TICK_PROFILE_PRE]` line.
2. **Scan/collect counters.** Static `AtomicU64`s incremented in
   `StatusRuntimeView::collect` (count + summed micros + status-row count) and
   `equipment_modifier_totals_for_owner` (count), reset and reported per profile
   window. These are the direct before/after gates for T1/T2/T5.
3. **Population + pair counters.** Per window: alive players, dummies, NPCs, active
   projectiles (already in projectile metrics), active statuses, active casts,
   `npc_target_pairs_scanned` (T4 gate), and `MOVE_FALLBACK` count
   (`game_loop.rs:1358-1363` — also wanted by feel-audit F2).
4. **Repeatable load recipe (no extra tooling needed):** one training instance with
   ≥ 8 playground targets/dummies (`playground_targets.rs` reducers), 2-4 practice
   actors as fireball turrets / melee trainers (`practice.rs:95`), plus
   `run_projectile_load_harness` (`server/src/combat/projectile_load_harness.rs:81`)
   for projectile pressure, with `ARENA_PROFILE_TICKS=1`. Record the profile lines
   as the committed baseline (paste into the PR description). Scale one axis at a
   time (dummies, NPCs, projectiles) to confirm the predicted scaling shapes before
   optimizing.

Estimated effort: ~a day. Everything in §1 cites these counters as its gate.

## 3. Optimizations that should wait for measurement

- **`PlayerIntent` write-contract change.** The row churns every tick by design
  (`input_tick` advances even on fallback). Since no client subscribes, the win is
  commit-log-only and the fix would change a contract (`input_tick` mirrors
  `physics.last_processed_tick` — dropping or gating it needs a sweep of intent
  readers). Wait for netcode-R3 write counters to show whether it matters.
- **`player_resource` regen write-rate reduction** (e.g., quantized updates while
  regenerating). Touches prediction reconciliation on the client
  (`LocalCombatState` releases predicted spend when the server value drops —
  feel-audit F1); do not change cadence until RTT/overlay tooling (feel-F2) can
  prove it doesn't break resource-bar feel.
- **`PlayerSnapshotSet` reuse across combat phases.** Today it is collected once
  per tick when spells/projectiles are active (`game_loop.rs:551-557`) — correct
  and cheap. Extending reuse into melee/area resolution risks stale-position hits;
  needs the §2 timings to show snapshot collection matters at all.
- **Skipping `resolve_pending_casts`' unindexed `pending_cast_cancel` /
  `cast_prediction_correlation` prune scans** (`spells/casting.rs:1435-1458`, run
  twice per tick). Tables are near-empty in practice; only worth touching if
  counters say otherwise.
- **`expire_party_invites` / `tick_countdowns` every-tick scans**
  (`party.rs:182-193`, `game_loop.rs:1480-1501`) — unindexed but tiny; candidates
  for a slower cadence (e.g., the 500 ms prune boundary) only if profiling ever
  shows them.
- **Status hot/cold split, spatial AoI, subscription re-planning** — already
  deferred by the netcode audit; still deferred, same reason (no data).

## 4. Smaller-model review checklist (tick-path PRs)

For any PR touching `game_tick` or code it calls:

- [ ] `cargo test` passes in `server/` and the module builds (`spacetime build`).
- [ ] No new `StatusRuntimeView::collect`, `movement_modifiers`, or
      `temporary_combat_modifiers` call inside a per-player, per-NPC, per-cast, or
      per-projectile loop — take a view/modifiers parameter instead (T1 contract).
- [ ] No new full-table `.iter()` scan in the tick path when an indexed accessor
      (`.filter(..=now_micros)` btree range, per-owner filter) exists; new
      due-time tables get a `_micros` btree column like `pending_melee_impact`
      (`melee.rs:3365-3372` is the reference pattern).
- [ ] Every hot-path row write is change-gated (compare before update) **except**
      rows that carry ack/tick counters — the local player's `PlayerPhysics`
      commit must never be skipped (`last_processed_tick` is the prediction ack).
- [ ] Nothing stamps `updated_at = now` as the *only* change on a row every tick.
- [ ] New per-tick work over "all players" or "all NPCs" states its scaling shape
      in the PR description and cites §2 counter output before/after (no
      optimization or new load without a measurement).
- [ ] New public tables justify their subscription scope; per-tick-churning rows
      do not go into public tables without a stated replication budget
      (`combat_projectile_tick_metrics` — one keyed row — is the acceptable
      pattern; a per-player per-tick public row is not).
- [ ] Event tables (`combat_event`, `combat_effect_event`, `player_event`,
      `projectile_presentation_event`) remain insert-only with a btree-indexed
      `created_at_micros` and are covered by `prune_combat_events`
      (`combat.rs:7137-7189`); retention stays ≤ `PLAYER_EVENT_RETENTION` (20 s,
      `combat.rs:114`).
- [ ] Per-tick metrics/counters go into the existing `TickProfileWindowState` /
      `[TICK_PROFILE]` window (log-only, `ARENA_PROFILE_TICKS`-gated) — no ad-hoc
      per-tick `log::info!` in loops, no replicated churn added to measure churn.
- [ ] Authoritative correctness is never traded for tick time: validation stays in
      reducers, hit/status resolution order (`resolve_combat_cycle`) is unchanged,
      and any view/caching added is tick-scoped, never cross-tick.
- [ ] Transient per-identity tables added by the PR are wired into the unified
      teardown (netcode-R1) — dead rows in hot tables are a tick-cost bug too.

## Classification summary

| Recommendation | Classification |
|---|---|
| §2 profiler sub-phases + scan counters + load recipe | instrumentation before optimization |
| T1 tick-scoped status views (resource path first) | tick-compute optimization, behavior-preserving |
| T2 per-tick per-player equipment/derived memo | tick-compute optimization, behavior-preserving |
| T3 change-gated writes (dummies, charge state, NPC runtime) | write/replication churn reduction |
| T4 NPC targeting context grouping | scaling-shape fix, gated on §2 counters |
| T5 aura/passive owner discovery via keyed tables | scaling-shape fix, low urgency today |
| PlayerIntent contract, resource write cadence, snapshot reuse | wait for measurement (§3) |
| Status hot/cold split, spatial AoI | deferred (netcode audit), still blocked on data |

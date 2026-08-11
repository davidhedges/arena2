# PvP Match-Start Latency Optimization Plan

Date: 2026-08-11

Status: **APPROVED — Steps 1–6 complete**

## 1. Scope

Reduce the player-visible delay between pressing the Hub's unranked 2v2 button
and entering the provisioned PvP match. This plan applies to disposable 2v2,
3v3, and 10v10 match databases only. It does not add or optimize survival,
random-dungeon, training-ground, or open-world gameplay.

The one-Hub-database plus one-disposable-database-per-match topology remains
the target architecture. The purpose of this plan is to make that topology
fast enough for players, not to move combat simulation back into the Hub.

## 2. Baseline and leading diagnosis

The first interactive Phase 4 run reached Hub `READY` about 12 seconds after
the ticket was created. That excludes the subsequent Unity WebSocket,
subscription, and scene-load work.

The current match artifact is approximately 263 MB. It is an unoptimized build
of the existing all-gameplay module and embeds approximately 151 MB of world
data, including multiple open-world heightfields and dungeon collision files.
The provisioner sends that entire artifact through the management publish API
for every new match. The provisioner also polls the Hub every two seconds, and
Unity applies broad static and local subscription plans sequentially after the
assignment becomes ready.

These observations establish likely bottlenecks, but the first implementation
step must measure each boundary before changing the architecture.

## 3. Initial performance budgets

Treat these as engineering targets to refine with measurements, not release
guarantees:

| Interval | Initial local target |
|---|---:|
| Request sent to ticket claimed | 0.5 s |
| Claim through database bootstrap and Hub `READY` | 2.0 s |
| Hub `READY` through match connection and initial state | 1.0 s |
| Match initial state through loaded `Arena_Map_01` scene | 1.0 s |
| End to end | 4.0 s |

Record individual samples now. Add p50 and p95 reporting when repeatable load
probes or production telemetry exist.

## 4. Implementation steps

### Step 1 — Instrument the complete startup timeline

Add correlated, structured timing events without changing provisioning or
handoff behavior.

Provisioner measurements:

1. ticket creation to claim;
2. Hub claim reducer;
3. Hub transition to `PROVISIONING`;
4. existing-database lookup;
5. match WASM publish;
6. published-database identity verification;
7. bootstrap-config lookup;
8. bootstrap reducer;
9. lease renewal;
10. Hub transition to `READY`;
11. total provisioner time and ticket-create-to-ready time.

Unity measurements:

1. match button/request dispatch;
2. receipt of each distinct Hub status;
3. assignment validation and match-connect start;
4. WebSocket authentication;
5. static subscription applied;
6. local-player subscription applied;
7. match initial state accepted by the handoff coordinator;
8. `Arena_Map_01` scene requested and loaded.

All Unity events use one monotonic trace and retain the ticket and match IDs as
soon as they become available. Hub timestamps report server-owned status time;
client elapsed time never relies on client/server clock synchronization.

Exit gate:

- provisioner tests cover the timing record shape;
- the Unity runtime and EditMode-test assemblies compile;
- one manual local match emits a complete trace that identifies the dominant
  interval.

Step 1 completion record (2026-08-11):

- Match `match-7730675dac7ea6395fee6713` emitted a complete provisioner and
  Unity trace from request through the map scene then named `ArenaMatch`
  (renamed to `Arena_Map_01` on 2026-08-12).
- End-to-end player-visible time was 17,009.4 ms.
- Hub ticket creation to `READY` was 12,166.9 ms in the provisioner trace and
  12,197.4 ms as observed by Unity.
- Ticket creation to claim was 747.9 ms.
- Publishing the 275,477,205-byte match WASM took 11,121.5 ms: 97.2% of the
  provisioner's 11,436.1 ms and 65.4% of the complete player-visible wait.
- Database lookup and post-publish verification together took 20.4 ms.
- Bootstrap-config lookup took 233.7 ms; the bootstrap reducer took 2.1 ms.
- Hub `READY` to accepted match initial state took 1,974.1 ms. Within that
  interval, transport connection took 453.2 ms, the static subscription took
  197.3 ms, shared-contract validation between static/local subscriptions took
  856.8 ms, and the local subscription took 300.6 ms.
- The requested map scene took 2,812.9 ms to report loaded.
- The measurements confirm the oversized all-gameplay WASM publish as the
  primary bottleneck. Scene loading and contract validation are meaningful
  secondary targets; polling and reducer execution are not the main problem.

### Step 2 — Extract a lean PvP match module

Create a dedicated PvP SpacetimeDB module containing only the match lifecycle,
arena movement/physics, combat, PvP bots, required arena collision, and the
catalog rows needed by match clients. Share reusable gameplay code through a
Rust library rather than duplicating it.

Exclude open-world heightfields and scenes, random-dungeon data, survival,
playground/training systems, and unrelated persistence from the match artifact.
Generate match bindings from this dedicated schema.

Exit gate:

- the provisioner publishes the PvP-specific artifact;
- the existing unranked 2v2 flow behaves identically;
- irrelevant mode tables and large world-data blobs are absent;
- the optimized artifact size and publish timing are recorded.

Step 2 completion record (2026-08-12):

- Added `match-server/`, a dedicated disposable PvP crate that compiles the
  authoritative movement, physics, combat, catalog, arena, and bot-match source
  through the `pvp_match` feature instead of copying gameplay implementations.
- The PvP artifact excludes embedded open-world heightfields, scene collision,
  and query-collision blobs. Survival, practice/training, playground targets,
  Hub parties/invites, random-dungeon doors/interactions/traps, the per-player
  open-world scene table, and dice mode tables/reducers are absent from its
  generated schema.
- `ops/build-match-spacetimedb.sh` builds the artifact once and regenerates the
  separate `Arena.MatchDb` C# bindings. The provisioner defaults now point only
  to `match-server/target/wasm32-unknown-unknown/release/arena_match.wasm`.
- Provisioned gameplay connections retain the existing subscription sequence
  but filter out tables that do not exist in the PvP schema. The generic
  gameplay/open-world connection keeps its original all-mode query plans; the
  one-round-trip/minimum-payload optimization remains Step 5.
- The raw dedicated artifact is 6,004,060 bytes, down 97.82% from the measured
  275,477,205-byte baseline. A local size-oriented Binaryen measurement produced
  a 4,584,769-byte artifact, down 98.34%; installing and enforcing that optimizer
  remains Step 3.
- A distinct-player local probe reached Hub `READY`, published the 6,004,060-byte
  artifact in 485.5 ms, completed provisioner work in 544.1 ms, and went from
  ticket creation to `READY` in 1,340.8 ms. Its bootstrapped database contained
  one human reservation plus the expected ally and two enemy bots in the fixed
  2v2 roster.
- Verification: the dedicated WASM build and binding generation pass; both
  Unity C# projects build with zero errors; provisioner tests pass 14/14; 615
  shared Rust tests pass. Eighteen full-server/source-layout tests remain
  inapplicable or pre-existing failures when invoked through the lean wrapper;
  they inspect excluded world/survival data, fixed full-server paths, or the
  already-known authored combat catalog failures rather than the PvP bootstrap.

### Step 3 — Optimize and enforce the release artifact

Install Binaryen in the local build environment so `spacetime build` can run
`wasm-opt`. Add compatible size-oriented release settings, then add a build
guard that rejects an unexpectedly large PvP artifact. Choose the final size
ceiling from the first legitimate optimized PvP build rather than guessing it
in advance.

Exit gate:

- local and CI builds use the same optimization path;
- the size guard catches accidental inclusion of large unrelated assets;
- module tests and generated bindings still pass.

Step 3 completion record (2026-08-12):

- Added size-oriented Rust release settings (`opt-level = "z"`, LTO, one codegen
  unit, aborting panics, and no incremental release output). The resulting raw
  PvP WASM is 3,928,819 bytes.
- `ops/build-match-spacetimedb.sh` now requires Binaryen, discovers either a
  `wasm-opt` on `PATH`, an explicit `WASM_OPT`, or the current macOS Unity
  editor's bundled Binaryen. It always applies the same explicit
  `-Oz --strip-debug --strip-producers` pass after `spacetime build` and
  generates bindings from that canonical optimized artifact.
- The canonical artifact is 2,978,841 bytes: 50.39% smaller than Step 2's
  6,004,060-byte raw artifact and 98.92% smaller than the original
  275,477,205-byte all-gameplay baseline.
- Added a measured 3,500,000-byte default ceiling with 17.5% growth headroom.
  The guard accepts the canonical artifact and its forced one-byte-ceiling test
  proves that the rejection path fails the build.
- Added the PvP match validation workflow. CI installs Binaryen and invokes the
  repository build script, so local and CI release artifacts follow the same
  optimizer flags, size guard, and binding-generation path. No deployment is
  performed by this workflow.
- A fresh local disposable-match proof published and bootstrapped the canonical
  2,978,841-byte artifact and reached Hub `READY`. This first new-build sample
  took 1,601.2 ms to publish, 1,654.5 ms of provisioner work, and 2,805.8 ms
  from ticket creation to `READY`; repeated latency benchmarking remains Step 6.
- Verification passed: the build and generated-schema inspection, the 3 bot
  lifecycle tests, 6 provisioned-contract tests, the Arena Map 01 layout test,
  all 14 provisioner tests, and both Unity C# project builds. Unity batch mode
  was not used.

### Step 4 — Remove polling from the latency-critical path

Replace the two-second idle polling delay with an event-driven provisioning
wakeup. Retain a slower reconciliation sweep for restarts and missed events.
A short 250–500 ms development poll may be used as an intermediate benchmark,
but it is not the intended final scheduler.

Exit gate:

- a newly committed ticket wakes the provisioner promptly;
- restart recovery and lease reconciliation still pass;
- idle management-query traffic is materially lower.

Step 4 completion record (2026-08-12):

- Added one private Hub wakeup singleton projected through a public view that
  returns data only to the configured provisioner identity. The projection
  contains one monotonic sequence number and exposes no ticket, player,
  assignment, or database information.
- A successfully inserted, non-idempotent match ticket advances the sequence.
  Repeated client request IDs and rejected duplicate requests do not generate
  more provisioning work.
- The local provisioner keeps one authenticated `spacetime subscribe` process
  on that view. Updates are coalesced through a process-local event, so a burst
  produces one authoritative private Hub snapshot rather than one snapshot per
  notification. A failed subscription restarts after five seconds.
- Replaced the two-second idle poll with a 30-second reconciliation sweep. The
  configured sweep is required to remain shorter than the ticket lease, and it
  still owns restart recovery, retry/lease work, terminal-match detection, and
  cleanup. With no active work, base Hub snapshot traffic falls from 30 to 2
  snapshots per minute (120 to 8 SQL requests per minute), a 15x reduction.
- The additive local Hub migration used `delete-data=never`. A disposable live
  proof was claimed 27.1 ms after ticket creation and reached Hub `READY` in
  522.4 ms. Its optimized WASM publish took 451.5 ms. Subsequent idle cycles
  occurred at the expected 30-second cadence and the fallback sweep deleted
  the expired test database.
- Verification passed: 9 Hub tests, 16 provisioner tests (including restart,
  leases, timed fallback/coalescing, exact cleanup, and failure recovery), Hub
  schema build and binding generation, and both Unity C# project builds. CI now
  runs the Hub and provisioner suites with the existing PvP validation. Unity
  batch mode and remote deployment were not used.

### Step 5 — Add a PvP-specific initial subscription

Replace the generic all-mode static/local startup subscriptions with the
minimum tables and catalog rows needed for the assigned PvP match. Prefer one
initial subscription round trip when contract validation and cache safety
allow it. Preserve scoped runtime subscriptions for later visibility changes.

Exit gate:

- Hub `READY` to local match readiness meets its budget;
- no required combat definition or local state is missing;
- survival, open-world, playground, and unrelated inventory state is not part
  of the PvP entry payload.

Step 5 completion record (2026-08-12):

- Provisioned PvP connections now apply one explicit 44-query initial
  subscription instead of the former 25-query static subscription followed by
  a 26-query local subscription. Generic/open-world connections retain their
  original two-stage plans.
- The PvP entry plan includes required combat catalogs, three prediction
  contract rows, the assigned arena row, local action/cooldown/discipline and
  equipment state, and the local player's item/spell/affix aggregate. It
  excludes survival, open-world, playground, party/invite, dice, recall,
  projectile-metrics, NPC-catalog, inventory-container/slot, and unowned
  world-loot queries. PvP scoped visibility also excludes loot containers and
  slots while retaining its actor, physics, combat, match, and event queries.
- PlayerWorld snapshot callbacks record the requested instance scope, but the
  separate visibility subscription now waits until the one-round-trip initial
  subscription and contract gate have passed. Later scope transitions remain
  unchanged.
- PvP contract validation directly loads the three arena prediction contracts
  (`arena_layout`, `gameplay_collision`, and `gameplay_query_collision`),
  totaling 11,179 bytes, rather than enumerating roughly 179 MB of bundled
  open-world and dungeon resources. The live validation verified all three
  stamps in 2.9 ms, down from the 545–857 ms samples that verified 38 files.
- The interactive match `match-26d58b0493acd8a8a11f0dbd` reached Hub
  `READY` at 780.0 ms and match initial-state readiness at 1,666.8 ms: an
  886.8 ms interval, below the 1.0-second Step 5 budget and 55.1% below the
  1,974.1 ms baseline. The initial subscription itself took 338.1 ms, its
  scoped instance subscription applied successfully, and there were no
  subscription or contract errors.
- The same run reached loaded `Arena_Map_01` at 3,381.7 ms, below the
  four-second end-to-end target. The user confirmed the map, action bar,
  movement, targeting, and an ability worked. Scene loading still consumed
  1,692.0 ms after the request and remains above its separate one-second
  interval target.
- Verification passed: both Unity C# projects build with zero errors, the
  regression assembly covers the one-subscription boundary and exact payload
  exclusions, and the interactive trace proved required runtime state. Unity
  batch mode, server republishing, and remote deployment were not used.

### Step 6 — Benchmark and decide whether a warm pool is justified

Measure repeated local runs and, later, remote p50/p95 results. If on-demand
publishing still dominates, maintain a small bounded pool of inert,
already-published databases keyed by region and match-module build hash.
Atomically reserve and bootstrap one on demand, delete it after the match, and
replenish the pool asynchronously.

Do not add pooling if the preceding steps already meet the budget. A pool adds
idle resource use, version draining, and reservation-safety requirements.

Exit gate:

- the measured data explicitly supports either no pool or a documented pool
  size;
- an unassigned warm database rejects gameplay clients;
- build changes drain old entries safely;
- every completed or abandoned match database remains disposable.

Step 6 completion record (2026-08-12):

- Added `ops/benchmark-local-match-start.py`, a serial local probe that reuses
  one anonymous player identity, requests matches through the public Hub
  reducer, preserves that identity across the assigned match connection,
  applies the production 44-query PvP initial subscription, cancels each
  ticket, and waits for exact-identity cleanup in the provisioner ledger. It
  emits correlated JSON samples and nearest-rank p50/p95 summaries without
  adding server telemetry or persistent match history.
- A 20-sample run used the 2,978,841-byte match artifact with build ID
  `sha256-7fe3ad16ff5f2b18440f`. All 20 samples reached initial state and all 20
  disposable databases reached the ledger's `CLEANED` state. A preceding
  decoder-validation sample and one intentionally failed decoder-development
  sample were also cleaned; the live ledger ended with zero active rows.
- Provisioner stage results were:

  | Local interval | p50 | p95 |
  |---|---:|---:|
  | Ticket creation to claim | 64.8 ms | 73.3 ms |
  | Claim through Hub `READY` | 387.1 ms | 479.7 ms |
  | WASM publish within that interval | 346.7 ms | 432.1 ms |
  | Ticket creation through Hub `READY` | 447.0 ms | 544.0 ms |

- Headless player-path results were:

  | Local interval | p50 | p95 |
  |---|---:|---:|
  | Request sent through observed Hub `READY` | 436.3 ms | 537.3 ms |
  | Hub `READY` through authenticated match transport | 22.0 ms | 34.1 ms |
  | Match transport through 44-query initial state | 126.6 ms | 143.3 ms |
  | Hub `READY` through match initial state | 150.7 ms | 165.3 ms |
  | Request sent through match initial state | 582.8 ms | 691.4 ms |

- The approved local warm-pool size is **zero**. Publishing remains the largest
  provisioner stage, but its 432.1 ms p95 does not justify keeping idle
  databases: claim-to-`READY` is already 76.0% below its two-second budget,
  `READY`-to-initial-state is 83.5% below its one-second budget, and the prior
  interactive Step 5 run already met the four-second end-to-end budget. A pool
  could remove only part of an already sub-budget interval while introducing
  idle cost, atomic reservation, replenishment, and version-draining failure
  modes.
- With pool size zero there are no unassigned warm databases to admit clients
  or stale warm entries to drain after a build change. Defense in depth remains
  in the match module: an unconfigured database admits only its owner/service,
  and a configured database admits only the reserved identity during a live
  allocation. Its six contract tests pass. The provisioner's 16 tests still
  cover terminal exact-identity deletion, deletion retry, restart recovery,
  and refusal to delete ownership/identity mismatches.
- This is a local decision, not a permanent prohibition. Remote p50/p95 data
  remains deferred; reconsider a nonzero pool only if that future data misses
  the budgets and again identifies on-demand publishing as the dominant
  actionable interval.

## 5. Deferred work

- remote deployment and regional capacity planning;
- ranked matchmaking, rewards, ratings, or persistent match history;
- 3v3 and 10v10 feature implementation;
- optimization of non-PvP modes;
- automatic scaling beyond the bounded provisioning concurrency already
  configured.

# Dungeon generator: current status

Start here when returning to dungeon work. Keep this page short and update it at the end of every dungeon-generation session.

Last updated: 2026-07-21

Active milestone: Phase 3 complete and verified; Phase 4 is ready but has not started

Production mode: the processional-spine route-first planner is the sole reachable layout builder

Recipe authoring UI: design only; not implemented

## Read next

1. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules.
2. This file — current evidence and exact handoff.
3. [`COHERENT_FLOORPLAN_PLAN.md`](COHERENT_FLOORPLAN_PLAN.md) — architecture, phases, and exit gates.
4. [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — required context for the Phase 4 throne-hall schema probe; do not generalize it into a platform yet.
5. [`stair_forge_design.md`](stair_forge_design.md) — exact stair contracts reused by the coupled twin-stair episode.

## Locked context

- Everything rises from the common abyss. The resulting vertical supports are intentional and acceptable.
- The 4u plan grid, explicit dimensions, traversal contracts, headroom rules, and prefab contracts remain authoritative. Do not infer semantics from names, renderer bounds, or screenshots.
- The renderer does not repair plans. Planning and validation own connectivity, clearance, landings, transitions, overlap, and sightline reservations.
- Every accepted dungeon is connected from the bottom arrival to the top culmination.
- Structural stairs, bridges, paired stairwells, and their landings are planned anchors, not decorations fitted into leftover cells.
- Traversal and visibility are separate graphs. Phase 3 proved one final source-to-target vista, not merely an adjacent elevation delta; later phases must preserve it.
- `DungeonLayout` and `TieredLevelPlan` remain the canonical downstream plans. Do not create parallel intent/placed/compiled DTO families or a legacy adapter.
- The ephemeral `RouteIntent` is the sole pre-coordinate graph. Named topology variants must eventually compose shared graph operations rather than become separate planner implementations.
- Recipes complement generic generation; they do not replace the floor with a large prefab pool. Recipe assets, catalogs, and authoring are Phase 4+.
- Locks, ability gates, and one-way traversal remain out of scope.
- Each phase has a deletion ledger. A phase is incomplete if its scheduled temporary or superseded code remains reachable.
- Phase 3 now proves explicit route-relative elevation, typed structural transitions, and one final named vista. Phase 4 must consume that foundation rather than introduce another elevation, stair, or visibility path.

## Foundation already established

Phase 0 established deterministic characterization, hard checks, real renderer probes, stable reason codes, and six visual sentinels. Its historical 200-seed baseline generated 200/200 plans and found 197/200 post-plan hard-valid; the three headroom failures characterize the deleted room-first path and are not permission for renderer repair.

Phase 1 established the processional-spine route-first slice:

- intent before coordinates: a nine-node main route, four-node purposeful branch, and one branch/rejoin loop;
- bounded deterministic skeleton embedding, room-envelope reservation, room inflation, and declared-edge corridor routing;
- direct construction of the existing `DungeonLayout`;
- opposed `vista-source`/`vista-target` facing with reserved intervening void at the 2D handoff;
- unchanged tier planning, validators, boundary construction, `ElevationEdgeModel`, renderer, abyss support, and collision export;
- a locked reliability budget of at least 95/100 hard-valid completions, maximum two layout attempts, p95 one attempt, and stable reason codes.

## Phase 2 result

Phase 2 made the route-first planner the sole production path and closed its deletion ledger.

- `TryBuildAcceptedPlan` now calls `TryBuildProcessionalSpineDungeonLayout` directly within the existing two-attempt bound. There is no selector, comparison wrapper, old-builder branch, adapter, or fallback.
- Deleted the room-first chain: `BuildRandomDungeonLayoutData`, `BuildDungeonFloorMask`, `PlaceRoomBand`, nearest-room center routing, and its old-only room-shape/fallback helpers.
- Removed 22 settings used only by the deleted path from `DungeonGenerationProfile` and the serialized spacious profile. Shared corridor, path, grid, graph, room-threshold, loop, dais, and promontory primitives remain.
- Generate, specific-seed, batch, sentinel, and reflection-based test entry points now use the sole builder. User-facing Phase 0/Phase 1 comparison menus and report labels are gone.
- Useful deterministic diagnostics remain. At the Phase 2 boundary the report envelope was `dungeon-plan-v1` and the generator version was `processional-spine-v1`; Phase 3 advances both versions as recorded below.
- The accepted `RouteIntent`, `DungeonLayout`, and `TieredLevelPlan` projections for all 100 locked seeds are identical to the Phase 1 pilot. Only the deliberately renamed report envelope/catalog digest changes the aggregate report hash.
- No route elevation metadata, structural-transition intent, final vista enforcement, recipes, additional topology, socket-solver framework, renderer changes, or collision changes were added.

## Phase 3 result

Phase 3 moved the vertical story into the existing route/tier seam and closed its deletion ledger.

- The pre-coordinate `RouteIntent` now declares one `AscendingSpine` policy, explicit relative levels, and typed traversal edges. The main route rises `0, 0, 4, 4, 8, 12, 16, 20, 24u`; the branch uses `12, 12, 16, 20u`.
- The 13 declared route traversals require seven embedded stairs, one 8u external-span bridge, one synthesized stairwell, and four level corridors. Every structural edge records its exact rise, placement class, footprint, both landing sets, and canonical `TransitionEdge` consumer.
- One narrow ephemeral `RouteTierRequirements` companion carries the existing route intent plus the already-reserved vista cells, endpoints, and facing into `TryBuildTieredLevelPlan`. It is not a canonical plan, adapter, renderer input, or serializable DTO family.
- The existing tier planner, reviewed stair pool, forge/synthesis, stairwell service, placement ledger, headroom checks, port graph, `TieredLevelPlan`, and `ElevationEdgeModel` remain the only production path. The renderer, abyss support, boundary builder, and collision exporter were not changed.
- The `branch-overlook-to-landmark` vista is protected before structural selection and verified after final tier planning with opposed facing, at least three void cells, and source elevation at least 4u above target.
- Route-aware loop candidates now validate their actual doorway-zone levels, preventing center-level candidates from becoming off-grammar 5u/9u connections at split-room thresholds.
- Diagnostics now use `dungeon-plan-v2` / `processional-spine-v2` and include intent, route-resolution, transition-reservation, final-vista, validation, and canonical hash evidence.
- Displaced late elevation assignment was deleted: the random archetype target-field planner and its eleven policy implementations, BFS/depth repair helpers, allowed-delta builders, target snapping, deepest-room selection, and the obsolete optional-bridge constant are gone. Only the active route elevation policy remains.

## Current sole generation seam

```text
GenerateWithSeed
  -> GenerateRandomDungeonLayout
  -> TryBuildAcceptedPlan
  -> TryBuildProcessionalSpineDungeonLayout
  -> DungeonLayout + ephemeral RouteTierRequirements
  -> TryBuildTieredLevelPlan / TryBuildTieredLevelPlanAttempt
  -> TieredLevelPlan
  -> TryBuildRoomBoundaryContext
  -> ElevationEdgeModel.BuildLevelField
  -> renderer / abyss support / collision export
```

`RandomDungeonSceneBuilder.RebuildWithSeed` still marks collision and calls `GameplayCollisionExporter.ExportActiveSceneSharedCollisionData`. Renderer probes still consume the real boundary and level-field seam, require enabled non-trigger mesh colliders, and never repair output.

## Phase 2 validation evidence

- Focused route-first EditMode suite: 6/6 passed in 1.838 seconds.
- Phase 0 hard-check fixture: 6/6 passed in 1.752 seconds.
- Locked corpus `2026072100..2026072199`: 100/100 accepted and post-plan hard-valid; attempts min/p50/p95/max/mean `1/1/1/2/1.01`; histogram `1:99, 2:1`.
- Pre-acceptance tier rejection codes: `ROOM_LEVEL_DELTA:405`, `STAIR_PLACEMENT:7`, `PORT_GRAPH:4`. There were no route-builder exhaustions or post-plan validation failures.
- The locked sweep was run twice and produced the same active result hash: `59a0db9a5cc46284b3fca8d054e1236f054a3ab46748d371482ea96395c05fb4`.
- Comparison with the preserved Phase 1 report found zero mismatches across all 100 per-seed route-intent, layout, tier, acceptance, or hard-validity results.
- Six fixed visual sentinels rendered with `REJECTED 0`; their seed order and renderer summaries match the Phase 1 captures.
- Full EditMode suite compiled and ran 324 tests: 303 passed, 21 failed. No dungeon test failed. The failures are the same unrelated current-tree failures: ten `PredictedMeleeContactCueTests`, eight `RemotePresentationBufferTests`, and one each in `ProjectileVfxPoolingTests`, `SpellCueCatalogWriterTests`, and `UiInputContractTests`.
- Repository symbol/settings audit found no remaining selector, wrapper, deleted room-first builder/helper, or removed-profile-field references. `git diff --check` passed.

Active reproducible outputs are ignored under `DungeonLabReports/`:

| Purpose | Seed or range | Result | Report path |
| --- | ---: | --- | --- |
| Locked route-first corpus | `2026072100..2026072199` | 100/100 hard-valid; repeat hash matched | `DungeonLabReports/dungeon_plan_2026072100_2026072199.json` |
| Visual sentinels | six established fixed seeds | 6/6 rendered; `REJECTED 0` | `DungeonLabReports/visual_sentinels/manifest.json` |

## Phase 3 acceptance budget — locked before behavior changes

The deterministic Phase 3 corpus is the 200 inclusive seeds `2026072100..2026072299` with the active spacious profile. The Phase 2 pre-change sweep completed 200/200 accepted and hard-valid, with layout attempts min/p50/p95/max/mean `1/1/1/2/1.01`, rejection codes `ROOM_LEVEL_DELTA:752`, `PORT_GRAPH:11`, `STAIR_PLACEMENT:7`, `CELL_LEVEL_CONFLICT:1`, no post-plan validation failures, and result hash `892a1bdd3f4b1c16e282421fac47e0b92a069ddfc448063ce01b76bb75d1e0c8`.

Phase 3 passes only if all of these predeclared gates hold:

- at least 190/200 seeds are accepted and post-plan hard-valid;
- no seed uses more than two layout attempts and p95 layout attempts remain at most one;
- every accepted result passes all existing connectivity, transition-contract, port-graph, bottom-to-top, landing, headroom, boundary, renderer-input, renderer, and collision preconditions;
- every non-completion carries a stable route/tier reason code;
- every accepted route carries explicit relative elevation and transition requirements, realizes a 24u arrival-to-culmination climb, and realizes its required structural stair, bridge, and stairwell through the existing transition services;
- every required transition has reserved footprint and landing evidence before generic fill, and its resolved transition matches the declared edge and rise;
- the named `branch-overlook-to-landmark` vista retains opposed facing, at least three reserved intervening cells, a source at least 4u above its target, and an unobstructed final pre-render sight volume;
- two independent sweeps produce identical per-seed intent/layout/tier/requirement hashes and the same aggregate result hash;
- all six visual sentinels render with zero rejected placements and pass a route/vertical-story readability review without weakening any hard gate.

These thresholds were locked before the first Phase 3 production behavior edit. Missing one requires revising the embedding, reservation, or tier-consumption algorithm—not editing this budget after seeing results.

## Phase 3 validation evidence

- Focused Phase 3 vertical-intent suite: 6/6 passed in 2.143 seconds. Phase 0 and Phase 1 fixtures also passed 6/6 each.
- Locked corpus `2026072100..2026072299`: 200/200 accepted and post-plan hard-valid; route requirements 200/200; final vistas 200/200; route climb exactly 24u for all 200.
- Attempts min/p50/p95/max/mean were `1/1/1/2/1.005`; histogram `1:199, 2:1`. Pre-acceptance retry codes were `STAIR_PLACEMENT:32` and `PORT_GRAPH:18`; there were no failed seeds or post-plan validation failures.
- Two independent 200-seed sweeps produced the same result hash: `80b79cbebe991aa7bd0b65cf28486217655e8bbc63f679e6c486f396a1930229`.
- All six fixed sentinels rendered through the real graphics path with `REJECTED 0`. Direct comparison with the preserved Phase 0 captures shows a materially clearer stepped procession: repeated structural climbs, a consistent high culmination, stronger vertical separation, and readable overlook gaps across representative, weak, and edge seeds.
- Full EditMode suite compiled and ran 330 tests: 309 passed, 21 failed. No dungeon test failed. The failures are the same unrelated baseline: ten `PredictedMeleeContactCueTests`, eight `RemotePresentationBufferTests`, and one each in `ProjectileVfxPoolingTests`, `SpellCueCatalogWriterTests`, and `UiInputContractTests`.
- Producer/consumer/test and removed-symbol audits found no remaining late elevation planner, repair helper, duplicate transition system, or scheduled Phase 3 deletion. The Phase 3 deletion ledger is empty.

| Purpose | Seed or range | Result | Report path |
| --- | ---: | --- | --- |
| Locked Phase 3 corpus | `2026072100..2026072299` | 200/200 hard-valid; repeat hash matched | `DungeonLabReports/dungeon_plan_2026072100_2026072299.json` |
| Phase 3 visual review | six established fixed seeds | 6/6 rendered; `REJECTED 0`; readability improved | `DungeonLabReports/visual_sentinels/manifest.json` |

## Exact Phase 4 handoff

Phase 4 is one throne-hall episode used as a schema probe. It is not permission to build the general recipe platform. Perform these steps in order:

1. Read `PROJECT_INVARIANTS.md`, this file, `COHERENT_FLOORPLAN_PLAN.md`, `RECIPE_AUTHORING_WORKFLOW.md`, and `stair_forge_design.md`. Inventory the active `RouteIntent` landmark beat, route/tier companion, stair reservations, canonical plan seam, rendering, abyss, dressing, and collision consumers before editing.
2. Lock the Phase 4 deterministic seed corpus and episode-review acceptance thresholds before changing behavior. Preserve every Phase 3 hard gate, the 200-seed reliability floor, stable reason codes, and the six-sentinel comparison.
3. Add one processional landmark slot to the existing route intent. Do not add another topology, general socket solver, parallel plan family, legacy adapter, or renderer repair.
4. Represent only the throne-hall episode fields proven necessary for this slice: focal axis, protected dais/focal zone, coupled twin stairs, both landing sets, side galleries, typed thresholds, and explicit allowed variation. Use the smallest internal data or draft `ScriptableObject`; do not create a reviewed catalog or custom authoring UI.
5. Place or reject the whole episode atomically before generic fill. Connect generic generation only through declared ports, use existing stair contracts/forge, landing/headroom checks, `ElevationEdgeModel`, abyss support, dressing permissions, and collision export, and never leave one coupled stair or gallery behind after a failed placement.
6. Produce canonical `DungeonLayout`/`TieredLevelPlan` data directly and add isolated plus full-dungeon diagnostics. Emit a schema-usage report showing every represented field and its active production consumer.
7. Add deterministic atomicity, port-connection, coupled-stair/landing, protected-zone, render, collision, isolated, and full-dungeon tests. Run the locked corpus, six sentinels, prior hard fixtures, and full EditMode suite; record distributions, hashes, reason codes, and unrelated failures.
8. Delete unused speculative fields/helpers, record the Phase 4 go/no-go review, close the deletion ledger, and stop. Do not begin Phase 5 schema hardening, lifecycle/catalog infrastructure, authoring windows, contrast recipes, atrium-ring/twin-wing topology, runtime generation, locks, or renderer repair.

Phase 4 exits only when the entire episode places or rejects atomically, generic generation connects through declared ports without repair, isolated and full-dungeon tests pass, the episode is visibly stronger than independent late rolls, unused schema surface is deleted, and the go/no-go review approves generalization.

## Known implementation facts and blockers

- Generator code: `Assets/Arena/Editor/Dungeons/RandomDungeon/`.
- Settings: `Assets/Arena/Content/Settings/Dungeons/RandomDungeon/`.
- Editor tests: `Assets/Arena/Tests/Editor/`.
- Gold reference scene: `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity`.
- Baked destination: `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`.
- `ActiveStepFormationPlacementEnabled` remains false, and `step_library_index.json` is absent. Do not enable the parked path by flipping the flag.
- There is no dungeon blocker for Phase 4. The 21 unrelated full-suite failures remain outside this workstream.

## End-of-session handoff

```text
Milestone/phase: Phase 3 complete and verified; Phase 4 ready and not started; Phase 5 not begun.
Completed this session: explicit route-relative elevation; typed stair/bridge/stairwell intent; pre-fill footprint/landing/vista reservations; existing tier/stair/forge consumption; final vista and route proof; diagnostics/tests; deletion of displaced late elevation assignment.
Current validation result: Phase 3 EditMode 6/6; Phase 0 fixture 6/6; Phase 1 fixture 6/6; locked corpus 200/200 hard-valid with p95 1 and max 2; repeat hash 80b79cbe...30229; six sentinels REJECTED 0 with improved vertical readability; full EditMode 309/330 with the same 21 unrelated failures.
Last known-good seed and report: 2026072140; DungeonLabReports/dungeon_plan_2026072100_2026072299.json and DungeonLabReports/visual_sentinels/manifest.json.
Last diagnostic fact: all 200 accepted plans realize the 24u route climb, seven required stairs, one bridge, one stairwell, and the final named vista before rendering.
Deletion-ledger items added/closed: late archetype target-field planner, BFS/depth repair, level-delta/snapping helpers, optional-bridge late policy / all closed; current ledger empty.
Exact next action: begin only the eight-step Exact Phase 4 handoff above, starting with the required reads and a read-only throne-hall producer/consumer/seam inventory before locking thresholds.
Blocker or decision needed: none.
New chat necessary: no. A new chat is recommended because Phase 3 is a complete verified boundary and this file now contains the exact Phase 4 handoff.
```

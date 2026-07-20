# Dungeon generator: current status

Start here when returning to dungeon work. Keep this page short and update it at the end of every dungeon-generation session.

Last updated: 2026-07-21

Active milestone: Phase 2 complete and verified; Phase 3 is ready but has not started

Production mode: the processional-spine route-first planner is the sole reachable layout builder

Recipe authoring UI: design only; not implemented

## Read next

1. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules.
2. This file — current evidence and exact handoff.
3. [`COHERENT_FLOORPLAN_PLAN.md`](COHERENT_FLOORPLAN_PLAN.md) — architecture, phases, and exit gates.
4. [`stair_forge_design.md`](stair_forge_design.md) — required before Phase 3 vertical-intent work.
5. [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — post-foundation content workflow; do not implement it in Phase 3.

## Locked context

- Everything rises from the common abyss. The resulting vertical supports are intentional and acceptable.
- The 4u plan grid, explicit dimensions, traversal contracts, headroom rules, and prefab contracts remain authoritative. Do not infer semantics from names, renderer bounds, or screenshots.
- The renderer does not repair plans. Planning and validation own connectivity, clearance, landings, transitions, overlap, and sightline reservations.
- Every accepted dungeon is connected from the bottom arrival to the top culmination.
- Structural stairs, bridges, paired stairwells, and their landings are planned anchors, not decorations fitted into leftover cells.
- Traversal and visibility are separate graphs. Phase 3 must prove one final source-to-target vista, not merely an adjacent elevation delta.
- `DungeonLayout` and `TieredLevelPlan` remain the canonical downstream plans. Do not create parallel intent/placed/compiled DTO families or a legacy adapter.
- The ephemeral `RouteIntent` is the sole pre-coordinate graph. Named topology variants must eventually compose shared graph operations rather than become separate planner implementations.
- Recipes complement generic generation; they do not replace the floor with a large prefab pool. Recipe assets, catalogs, and authoring are Phase 4+.
- Locks, ability gates, and one-way traversal remain out of scope.
- Each phase has a deletion ledger. A phase is incomplete if its scheduled temporary or superseded code remains reachable.

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
- Useful deterministic diagnostics remain. The active report envelope is `dungeon-plan-v1`; the generator version remains `processional-spine-v1`.
- The accepted `RouteIntent`, `DungeonLayout`, and `TieredLevelPlan` projections for all 100 locked seeds are identical to the Phase 1 pilot. Only the deliberately renamed report envelope/catalog digest changes the aggregate report hash.
- No route elevation metadata, structural-transition intent, final vista enforcement, recipes, additional topology, socket-solver framework, renderer changes, or collision changes were added.

## Current sole generation seam

```text
GenerateWithSeed
  -> GenerateRandomDungeonLayout
  -> TryBuildAcceptedPlan
  -> TryBuildProcessionalSpineDungeonLayout
  -> DungeonLayout
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

## Exact Phase 3 handoff

Phase 3 adds the smallest vertical-intent slice through the existing tier pipeline. It does not add recipes or another topology. Perform these steps in order:

1. Read `PROJECT_INVARIANTS.md`, this file, `COHERENT_FLOORPLAN_PLAN.md`, and `stair_forge_design.md`. Inspect the existing `RouteIntent` production, `TryBuildTieredLevelPlan` consumption, stair/bridge placement, landing/headroom validation, vista diagnostics, and `ElevationEdgeModel` seam before editing.
2. Lock Phase 3 acceptance thresholds and a deterministic fixed corpus before changing planner behavior. Retain the Phase 2 hard checks and reason-coded rejection requirements; include at least 200 fixed seeds in the Phase 3 exit proof.
3. Add only the route-relative elevation and typed structural-transition requirements needed by the working processional-spine slice. Keep them on the ephemeral route intent or the smallest justified companion data consumed directly by the existing tier planner; do not create parallel plan families or an adapter layer.
4. Reserve required stairs or bridges, their landings, and headroom before generic surrounding fill. Use existing measured contracts, stair selection/forge/synthesis, bridge support, and validators. Reject infeasible plans with stable reason codes; do not repair them in rendering.
5. Make `TryBuildTieredLevelPlan` consume those requirements while continuing to produce the canonical `TieredLevelPlan`. Preserve the current `DungeonLayout`, boundary, `ElevationEdgeModel`, renderer, abyss, and collision contracts.
6. Carry the existing `vista-source`/`vista-target` reservation through final planning and prove one actual final pre-render sight volume remains unobstructed. Keep traversal and visibility evidence separate.
7. Add focused determinism, transition, landing, headroom, final-vista, and renderer/collision tests. Run the locked Phase 3 corpus, the six visual sentinels, prior hard fixtures, and the full EditMode suite. Record per-seed hashes, distributions, stable failure codes, and any unrelated failures.
8. Close the Phase 3 deletion ledger and stop. Do not begin recipe schemas/catalogs/UI, atrium-ring or twin-wing topology, general socket solving, runtime generation, locks, or renderer repair.

Phase 3 exit requires the processional-spine route to carry relative elevation intent, required transitions to reserve valid landing/headroom space before fill, one final vista to remain valid through the tier handoff, deterministic output, and at least 200 fixed seeds meeting the locked Phase 0 hard-validity budget.

## Known implementation facts and blockers

- Generator code: `Assets/Arena/Editor/Dungeons/RandomDungeon/`.
- Settings: `Assets/Arena/Content/Settings/Dungeons/RandomDungeon/`.
- Editor tests: `Assets/Arena/Tests/Editor/`.
- Gold reference scene: `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity`.
- Baked destination: `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`.
- `ActiveStepFormationPlacementEnabled` remains false, and `step_library_index.json` is absent. Do not enable the parked path by flipping the flag.
- There is no dungeon blocker for Phase 3. The 21 unrelated full-suite failures remain outside this workstream.

## End-of-session handoff

```text
Milestone/phase: Phase 2 complete and verified; Phase 3 ready and not started; Phase 4 not begun.
Completed this session: sole-builder cutover; deletion of the old room-first chain, comparison state/wrappers/labels, and 22 old-only settings; entry-point/report cleanup; complete Phase 2 validation.
Current validation result: route-first EditMode 6/6; Phase 0 hard fixture 6/6; locked corpus 100/100 hard-valid with p95 1 and max 2; repeat hash 59a0db9a...5fb4; six sentinels REJECTED 0; full EditMode 303/324 with the same 21 unrelated failures.
Last known-good seed and report: 2026072140; DungeonLabReports/visual_sentinels/manifest.json.
Last diagnostic fact: the sole route-first path preserves all 100 locked per-seed route/layout/tier projections; final vista preservation through vertical planning remains Phase 3 work.
Deletion-ledger items added/closed: Phase 2 selector/comparison wrappers, old 2D builder/helpers/settings, and comparison labels / all closed; current ledger empty.
Exact next action: begin only the eight-step Exact Phase 3 handoff above, starting with the required reads and a read-only producer/consumer/seam inventory before locking thresholds.
Blocker or decision needed: none.
New chat necessary: no. A new chat is recommended because Phase 2 is a complete verified boundary and this file now contains the exact Phase 3 handoff.
```

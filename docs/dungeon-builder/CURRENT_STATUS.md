# Dungeon generator: current status

Start here when returning to dungeon work. Keep this page short and update it at the end of every dungeon-generation session.

Last updated: 2026-07-21

Active milestone: Phase 4 throne-hall schema probe complete and verified; Phase 5 ready and not started

Production mode: the processional-spine route-first planner is the sole reachable layout builder

Recipe authoring UI: design only; not implemented

## Read next

1. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules.
2. This file — current evidence and exact handoff.
3. [`COHERENT_FLOORPLAN_PLAN.md`](COHERENT_FLOORPLAN_PLAN.md) — architecture, phases, and exit gates.
4. [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — required contract for Phase 5 lifecycle, validation, preview, and review work.
5. [`stair_forge_design.md`](stair_forge_design.md) — exact stair contracts already reused by the coupled twin-stair episode and any Phase 5 contrast recipe.

## Locked context

- Everything rises from the common abyss. The resulting vertical supports are intentional and acceptable.
- The 4u plan grid, explicit dimensions, traversal contracts, headroom rules, and prefab contracts remain authoritative. Do not infer semantics from names, renderer bounds, or screenshots.
- The renderer does not repair plans. Planning and validation own connectivity, clearance, landings, transitions, overlap, and sightline reservations.
- Every accepted dungeon is connected from the bottom arrival to the top culmination.
- Structural stairs, bridges, paired stairwells, and their landings are planned anchors, not decorations fitted into leftover cells.
- Traversal and visibility are separate graphs. Phase 3 proved one final source-to-target vista, not merely an adjacent elevation delta; later phases must preserve it.
- `DungeonLayout` and `TieredLevelPlan` remain the canonical downstream plans. Do not create parallel intent/placed/compiled DTO families or a legacy adapter.
- The ephemeral `RouteIntent` is the sole pre-coordinate graph. Named topology variants must eventually compose shared graph operations rather than become separate planner implementations.
- Recipes complement generic generation; they do not replace the floor with a large prefab pool. The general schema, lifecycle, catalog, and authoring workflow remain Phase 5 work.
- Locks, ability gates, and one-way traversal remain out of scope.
- Each phase has a deletion ledger. A phase is incomplete if its scheduled temporary or superseded code remains reachable.
- Phase 4 now proves one atomic authored throne-hall episode on the existing route/tier seam. Phase 5 may generalize only its consumed contract, must add a structurally different contrast recipe, and must not introduce another plan, stair, visibility, renderer, or collision path.

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

## Phase 4 result

Phase 4 used one throne-hall episode as a schema probe and closed its deletion ledger.

- The pre-coordinate route intent declares exactly one `episode_throne_twin_stairs_01` slot at the existing `vista-target` landmark beat. No second topology, general socket solver, parallel canonical plan, adapter, renderer repair, or recipe catalog was added.
- One narrow internal episode contract supplies a vista-bound focal axis, a `7x5` dominant room, a protected `3x5` focal zone, two symmetric `4x2` side galleries at rise 1, two coupled stairs with explicit lower/upper landings and footprints, two typed processional thresholds, and two explicit StairForge-backed focal variations.
- Placement is atomic before generic fill. The two adjacent main-route connections terminate at declared off-center threshold cells; generic loops and late features cannot consume the episode room, galleries, focal zone, stairs, landings, or showpiece.
- The existing stair prefab contract, `TransitionEdge`, placement ledger, StairForge showpiece data, `DungeonLayout`, `TieredLevelPlan`, renderer, abyss support, and collision export remain the only downstream production path.
- Final validation proves exact port endpoints and levels, gallery symmetry, complete twin-stair reservations, one backed focal showpiece, protected zones, and absence of generic promontory intrusion before publishing one canonical atomic episode resolution.
- Diagnostics use `dungeon-plan-v3` / `processional-spine-v3`. The schema-usage projection reports 17 represented field groups, all with active producers and production consumers; isolated diagnostics cover four cardinal orientations by both focal designs.
- Producer/consumer and symbol audits found no unconsumed speculative field, helper, duplicate system, or scheduled Phase 4 deletion. The Phase 4 deletion ledger is empty.

## Current sole generation seam

```text
GenerateWithSeed
  -> GenerateRandomDungeonLayout
  -> TryBuildAcceptedPlan
  -> TryBuildProcessionalSpineDungeonLayout
  -> DungeonLayout + ephemeral RouteTierRequirements (route + placed landmark episode)
  -> TryBuildTieredLevelPlan / TryBuildTieredLevelPlanAttempt
     -> atomically realize protected episode before generic structural fill
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

## Phase 4 acceptance budget — locked before behavior changes

The deterministic Phase 4 corpus remains the 200 inclusive seeds `2026072100..2026072299` with the active spacious profile. The pre-change reference is the final Phase 3 result above: 200/200 accepted and hard-valid, attempts min/p50/p95/max/mean `1/1/1/2/1.005`, and repeat-identical hash `80b79cbebe991aa7bd0b65cf28486217655e8bbc63f679e6c486f396a1930229`.

Phase 4 passes only if all of these predeclared gates hold:

- at least 190/200 seeds are accepted and post-plan hard-valid; no seed uses more than two layout attempts and p95 layout attempts remain at most one;
- every Phase 3 route, transition, landing, headroom, port-graph, final-vista, renderer-input, renderer, abyss, and collision gate remains unchanged and passing;
- every accepted route contains exactly one `episode_throne_twin_stairs_01` slot at the existing `vista-target` landmark beat, and the slot resolves to either one complete episode or one stable pre-render rejection—never a partial placement;
- every resolved episode has one consumed focal axis, one protected backed dais/focal zone, two non-empty symmetric side-gallery cell sets, exactly two coupled rise-1 stair transitions, and two explicit non-empty lower-landing plus upper-landing sets;
- the two main-route connections touching the landmark terminate at the episode's declared typed threshold cells at the declared level; neither uses the room center as a repair endpoint, moves a port, or opens an undeclared episode boundary;
- the protected focal zone, gallery cells, stair footprints, and landing cells remain free of generic dais, promontory, bridge, stair, and dressing placement; the focal showpiece is selected only from the contract's explicit stable variation set;
- isolated diagnostics prove every allowed focal variation and orientation using canonical cell/transition data; full-dungeon diagnostics prove episode atomicity, port binding, symmetry, protection, render success, and enabled non-trigger collision;
- two independent corpus sweeps produce identical per-seed intent/layout/tier/episode hashes and the same aggregate result hash;
- all six established visual sentinels render with `REJECTED 0`, and all six visibly pass focal-axis, paired-stair/gallery, declared-threshold, and uncluttered-protected-zone review against the equivalent Phase 3 landmark rooms.

These thresholds were locked after the required read-only producer/consumer inventory and before the first Phase 4 production behavior edit. Missing one requires revising placement, reservation, or canonical-plan consumption—not editing this budget after seeing results.

## Phase 4 validation evidence

- Focused Phase 4 throne-hall suite: 7/7 passed in 2.522 seconds. The Phase 0, Phase 1, and Phase 3 fixtures also passed 6/6 each.
- Locked corpus `2026072100..2026072299`: 200/200 accepted and post-plan hard-valid; route requirements 200/200; final vistas 200/200; atomic throne-hall episodes 200/200.
- Attempts min/p50/p95/max/mean were `1/1/1/2/1.005`; histogram `1:199, 2:1`. Pre-acceptance retry codes were `STAIR_PLACEMENT:32`, `PORT_GRAPH:12`, and `CELL_LEVEL_CONFLICT:1`; there were no failed seeds or post-plan validation failures.
- Two independent 200-seed sweeps produced the same result hash: `40cb04c8d8334bbaa8ace02bbb06a31551def30dbe914eae66178f40a602a08e`. Both the preserved Phase 3 budget and the locked Phase 4 budget passed.
- The isolated diagnostic passed all eight orientation/design combinations and reported all 17 schema field groups consumed. Full-dungeon tests proved atomicity, exact typed-port connection, coupled stairs/landings, symmetry, protected zones, real rendering, and enabled non-trigger collision.
- All six fixed sentinels rendered through the real graphics path with `REJECTED 0`. Direct comparison with their preserved Phase 3 images shows a materially stronger single focal room in every representative, weak, and edge seed: the backed dais, paired gallery stairs, transverse thresholds, and uncluttered protected zone read as a coupled composition rather than independent late rolls.
- Full EditMode suite compiled and ran 337 tests: 316 passed, 21 failed. No dungeon test failed. The failures are the same unrelated baseline: ten `PredictedMeleeContactCueTests`, eight `RemotePresentationBufferTests`, and one each in `ProjectileVfxPoolingTests`, `SpellCueCatalogWriterTests`, and `UiInputContractTests`.
- Schema producer/consumer/test and duplicate-system audits found no unused speculative surface. `git diff --check` passed, the deletion ledger is empty, and the Phase 4 go/no-go review approves generalizing only the proven contract in Phase 5.

| Purpose | Seed or range | Result | Report path |
| --- | ---: | --- | --- |
| Locked Phase 4 corpus | `2026072100..2026072299` | 200/200 hard-valid and atomic; repeat hash matched | `DungeonLabReports/dungeon_plan_2026072100_2026072299.json` |
| Phase 4 visual review | six established fixed seeds | 6/6 rendered; `REJECTED 0`; throne episode visibly stronger | `DungeonLabReports/visual_sentinels/manifest.json` |

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

## Exact Phase 5 handoff

Phase 5 hardens only the recipe contract demonstrated by the working throne-hall probe. Perform these steps in order:

1. Read `PROJECT_INVARIANTS.md`, this file, `COHERENT_FLOORPLAN_PLAN.md`, `RECIPE_AUTHORING_WORKFLOW.md`, and `stair_forge_design.md`. Inventory the 17 consumed Phase 4 field groups, their producers/consumers/tests, the canonical route/tier seam, existing validation services, and the temporary throne-specific boundary before editing.
2. Preserve the Phase 4 corpus, all Phase 3/4 hard gates, stable reason codes, repeat hashes, and six-sentinel comparison. Lock any Phase 5 contrast-recipe and authoring-workflow review thresholds before behavior changes.
3. Separate reusable recipe semantics from throne-specific placement facts. Generalize only fields with a current production consumer; do not copy the throne episode into a nominally generic DTO, add an unused extension point, or create a parallel canonical plan.
4. Introduce the smallest versioned recipe/motif asset contract and non-mutating schema/structure/variation/neighbor/full-dungeon validators needed by the proven episode. Validation is a computed result, not a stored lifecycle state.
5. Add one deliberately different contrast recipe, preferably a stair tower or flexible vestibule, and force both recipes through the same contract, placement seam, stair/landing/headroom rules, canonical plans, renderer, abyss, dressing, and collision consumers. No schema field may be named for either recipe.
6. Add `Draft`, `Reviewed`, and `Deprecated` lifecycle behavior plus a catalog that admits only reviewed assets whose validation digest still matches. Editing a reviewed asset must make its review stale without mutating validation state.
7. Implement only the overlays, deterministic previews, validation output, review action, and preview-gallery support required to complete `RECIPE_AUTHORING_WORKFLOW.md`; do not build speculative editor panes or future recipe kinds.
8. Add isolated, neighbor, full-dungeon, lifecycle, digest, and workflow tests; run the locked corpus, sentinels, prior fixtures, and full EditMode suite; delete superseded throne-only scaffolding and every unused extension point; close the deletion ledger and stop before Phase 6 topology or content expansion.

Phase 5 exits only when two structurally different recipes share one explicit contract without recipe-specific fields, review stales on change, invalid structure cannot be promoted, the required authoring workflow completes end to end, and no unused editor or schema extension point remains.

## Known implementation facts and blockers

- Generator code: `Assets/Arena/Editor/Dungeons/RandomDungeon/`.
- Settings: `Assets/Arena/Content/Settings/Dungeons/RandomDungeon/`.
- Editor tests: `Assets/Arena/Tests/Editor/`.
- Gold reference scene: `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity`.
- Baked destination: `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`.
- `ActiveStepFormationPlacementEnabled` remains false, and `step_library_index.json` is absent. Do not enable the parked path by flipping the flag.
- There is no dungeon blocker for Phase 5. The 21 unrelated full-suite failures remain outside this workstream.

## End-of-session handoff

```text
Milestone/phase: Phase 4 complete and verified; Phase 5 ready and not started; Phase 6 not begun.
Completed this session: one route-bound throne-hall episode; atomic footprint, focal zone, symmetric galleries, coupled stairs/landings, typed thresholds, explicit focal variations, pre-fill protection, direct canonical-plan consumption, schema-usage diagnostics, tests, corpus and visual review.
Current validation result: Phase 4 EditMode 7/7; Phase 0/1/3 fixtures 6/6 each; locked corpus 200/200 hard-valid and atomic with p95 1 and max 2; repeat hash 40cb04c8...a08e; six sentinels REJECTED 0 and visibly stronger than Phase 3; full EditMode 316/337 with the same 21 unrelated failures.
Last known-good seed and report: 2026072140; DungeonLabReports/dungeon_plan_2026072100_2026072299.json and DungeonLabReports/visual_sentinels/manifest.json.
Last diagnostic fact: all 200 accepted plans retain the Phase 3 route/vista gates and resolve exactly one protected, port-bound, symmetric, twin-stair throne-hall episode; all 17 represented schema field groups have active production consumers.
Deletion-ledger items added/closed: no speculative Phase 4 schema field or helper survived the producer/consumer audit; current ledger empty.
Exact next action: begin only the eight-step Exact Phase 5 handoff above, starting with the required reads and a read-only audit of the 17 proven field groups and the throne-specific/general boundary before locking Phase 5 review thresholds.
Blocker or decision needed: none.
New chat necessary: no. A new chat is recommended because Phase 4 is a complete verified boundary and this file now contains the exact Phase 5 handoff.
```

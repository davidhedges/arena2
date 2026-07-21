# Dungeon generator: current status

Start here when returning to dungeon work. Keep this page short and update it at the end of every dungeon-generation session.

Last updated: 2026-07-21

Active milestone: Phase 6f third reviewed corner-return recipe complete and verified; next Phase 6 increment not yet selected

Production mode: one route-first layout builder deterministically selects processional-spine, atrium-ring, or twin-wing-keep, resolves exactly three reviewed recipes plus any target-bearing named-vista promontory, and feeds the shared canonical pipeline

Recipe authoring UI: implemented for the Phase 5 contract, validation, deterministic gallery, review, and promotion workflow

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
- Recipes complement generic generation; they do not replace the floor with a large prefab pool. The reviewed schema, lifecycle, catalog, and authoring workflow are now the Phase 5 production foundation.
- Locks, ability gates, and one-way traversal remain out of scope.
- Each phase has a deletion ledger. A phase is incomplete if its scheduled temporary or superseded code remains reachable.
- Phase 5 proved two structurally different reviewed recipes on one existing route/tier seam; Phase 6f now proves a third. Further breadth must use this contract and must not introduce another plan, stair, visibility, renderer, or collision path.

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

## Phase 5 result

Phase 5 generalized the proven recipe semantics, completed the authoring workflow, and closed its deletion ledger.

- `DungeonRecipeAsset` is the single versioned recipe contract. Its current kinds are only `Connector` and `Episode`; embedded motifs are only `StairTransition` and `FocalVisual`. The schema has no field named for either shipped recipe and no future topology, lock, runtime, renderer, or collision extension point.
- The reviewed catalog contains `episode_throne_twin_stairs_01` and the structurally different `connector_flexible_vestibule_01`. The vestibule has two opposed corridor ports, one protected circulation strip, one offset raised bay, and one rise-1 stair; it has no symmetry pair or focal alternative. Both recipes use the same slot, placement, canonical-plan, stair, validation, render, abyss, dressing-protection, and collision path.
- Schema, structure, variation, neighbor, and full-dungeon validation are non-mutating. Ordinary generation admits only `Reviewed` assets whose stored review digest matches current content. A content edit makes review stale; invalid structure cannot promote; `Draft`, `Reviewed`, and `Deprecated` are the only lifecycle states.
- The authoring window implements Draft creation, catalog/current validation, grid/zone/port/landing/headroom/protected-axis overlays, deterministic orientation/mirror/alternative galleries, below-floor support views, generic-neighbor evidence, full-dungeon evidence, review metadata, and promotion. The throne gallery has 66 entries and hash `1f815e2e...b61d32e`; the vestibule gallery has 34 entries and hash `7f2b9267...dfa08e`.
- The temporary throne-only production DTO and compatibility diagnostic layer were deleted. Route-specific node/edge bindings remain outside the asset contract; `DungeonLayout` and `TieredLevelPlan` remain the only canonical downstream plans.
- Phase 0/1/3/4/5 fixtures pass 33/33. The final full EditMode suite is 324/345 with the same 21 unrelated baseline failures and no dungeon failure.
- The locked corpus passed twice at 200/200 accepted, hard-valid, route-valid, vista-valid, and complete two-recipe sets. Attempts min/p50/p95/max/mean are `1/1/1/2/1.005`; retry codes are `STAIR_PLACEMENT:32` and `PORT_GRAPH:11`; both aggregate hashes are `765ead1a87f95732fb66dfa617b33e91d1ea921cb91f0287226309d17af46155`. The independent ordered digest over every per-seed intent/layout/tier/recipe/catalog/canonical hash is `1d35718f9d9b0d31f752a3b630f64d5f2ee134cbc651a2e79759a3c0a1b01f01` in both sweeps.
- All six final real-graphics sentinels report `REJECTED 0`. Comparison with the preserved Phase 4 captures retains the throne composition and route readability; the contrast connector's asymmetric raised bay, protected route, and single stair are also present without a renderer repair.
- Producer/consumer/test, duplicate-path, recipe-named schema/validator/diagnostic, and removed-symbol audits found no surviving throne-only production scaffolding or unused extension point. The Phase 5 deletion ledger is empty.

## Current sole generation seam

```text
GenerateWithSeed
  -> GenerateRandomDungeonLayout
  -> TryBuildAcceptedPlan
  -> TryBuildProcessionalSpineDungeonLayout
  -> DungeonLayout + ephemeral RouteTierRequirements (route + two placed recipe resolutions + named-vista reservation)
  -> TryBuildTieredLevelPlan / TryBuildTieredLevelPlanAttempt
     -> atomically realize protected recipes before generic structural fill
     -> resolve any source-side named-vista promontory after structural fill
  -> TieredLevelPlan (including canonical target-bearing promontory resolution)
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

## Phase 5 acceptance budget — locked before behavior changes

The deterministic Phase 5 corpus remains the 200 inclusive seeds `2026072100..2026072299` with the active spacious profile. The preserved pre-change reference is the final Phase 4 result above: 200/200 accepted and hard-valid, attempts min/p50/p95/max/mean `1/1/1/2/1.005`, repeat-identical hash `40cb04c8d8334bbaa8ace02bbb06a31551def30dbe914eae66178f40a602a08e`, and six sentinels with `REJECTED 0`.

The required read-only boundary audit found the 17 consumed Phase 4 field groups at the temporary throne-specific seam: stable ID; route slot; focal-axis binding; dominant footprint; protected focal zone; paired side regions; relative region rise; coupled transition count; allowed focal alternatives; typed port identity/binding/kind; resolved port cell/direction/level; resolved primary axis; protected cells; resolved region cells; transition footprint/landings/climb; and selected visual alternative/origin/yaw. Their active consumers are route placement, exact corridor termination, canonical cell levels, `StairPlacementLedger`, `TransitionEdge`, port-graph and landing/headroom validation, protected generic-fill/dressing exclusions, StairForge-backed showpieces, diagnostics, renderer, abyss support, and collision export. Phase 5 may rename and group these semantics, but may not retain a field without one of those current consumers.

Phase 5 passes only if all of these predeclared gates hold:

- at least 190/200 seeds are accepted and post-plan hard-valid; no seed uses more than two layout attempts and p95 layout attempts remain at most one;
- every Phase 3/4 route, transition, landing, headroom, port-graph, final-vista, throne-episode, renderer-input, renderer, abyss, dressing-protection, and collision gate remains passing with the same stable reason-code categories;
- the throne hall and one structurally different flexible-vestibule connector are both selected from one reviewed versioned catalog and placed through one recipe seam; every accepted seed contains exactly one complete resolution of each recipe and never a partial transition, region, port, protected zone, or visual alternative;
- the contrast connector proves that focal alternatives and symmetry groups are optional contract semantics: it has two opposed typed route ports, one protected circulation strip, one offset raised region, and exactly one rise-1 stair with explicit lower/upper landings and footprint, while the throne recipe retains its two symmetric raised regions, two coupled stairs, protected focal zone, and two explicit focal alternatives;
- no serialized schema field, validator branch, placement field, diagnostic field, or editor control is named for either recipe; route-specific node and edge bindings remain outside the reusable asset contract;
- schema, structure, variation, neighbor, and full-dungeon validation are computed without mutating recipe content or lifecycle state; invalid structure cannot be promoted; editing reviewed content changes its digest, makes review stale, and excludes it from the active catalog until re-review;
- only `Draft`, `Reviewed`, and `Deprecated` lifecycle behavior exists, and ordinary generation admits only `Reviewed` assets whose recorded review digest matches the current validation digest;
- deterministic authoring previews cover every legal orientation and meaningful alternative, expose grid/zone/port/landing/headroom/protected-axis overlays, include compatible-neighbor and full-dungeon evidence plus a below-floor structural view, and reproduce the same manifest/hash for the same recipe digest and preview seeds;
- the required workflow completes end to end in tests: create Draft, edit explicit structure, validate all five layers, build deterministic previews/gallery evidence, record review metadata, promote, admit through catalog, detect a stale edit, and reject invalid promotion;
- two independent corpus sweeps produce identical per-seed intent/layout/tier/recipe/catalog hashes and the same aggregate result hash; all six established sentinels render with `REJECTED 0`, retain the Phase 4 throne composition, and show the contrast connector without weakening route readability;
- producer/consumer/test and symbol audits find no surviving throne-only DTO/scaffolding, duplicate plan/validator/render/collision path, unused editor extension point, speculative recipe kind, or open Phase 5 deletion-ledger item.

These thresholds were locked after the required Phase 4 producer/consumer inventory and before the first Phase 5 production behavior edit. Missing one requires revising the shared contract, placement, validation, lifecycle, or workflow implementation—not editing this budget after seeing results.

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

## Phase 6 entry audit — shared graph operations

The first Phase 6 increment is deliberately behavior-preserving. It extracts only the graph operations already proven by the processional-spine production slice; it does not add topology selection or another layout.

| Operation | Current production evidence | Phase 6a treatment |
| --- | --- | --- |
| Spine | `BuildProcessionalRouteIntent` declares nine ordered main-route nodes and eight typed edges. | Compose the same ordered nodes/edges through one narrow internal spine operation. |
| Branch | The same method attaches four ordered branch nodes at `choice` and declares four typed branch edges. | Compose the same branch through one narrow internal branch operation. |
| Rejoin | The final `branch-return -> rejoin` edge closes the purposeful branch. | Add the same typed rejoin edge through one narrow internal rejoin operation. |
| Loop | The one macro loop is the graph consequence of branch plus rejoin. Generic `AddLevelSafeLoopConnections` is a later canonical-plan enrichment pass, not a macro-planning operation. | Report and validate the resulting cycle; do not create a redundant loop combinator or move the late loop pass. |
| Hub, cross-link, long return | No current route-intent producer or production consumer exists. | Do not add them in Phase 6a. Introduce each only with the first topology that consumes it. |

The current processional boundary is intentionally still specialized outside graph construction:

- `RouteIntent` is the sole pre-coordinate graph and `BuildProcessionalRouteIntent` is its only production constructor. `TryBuildAcceptedPlan` directly reaches only `TryBuildProcessionalSpineDungeonLayout`.
- `TryEmbedProcessionalRoute` owns the fixed coarse lattice, bounded branch search, rotation/mirroring, room-envelope spacing, and processional node-index mapping. These are spatial-solver facts, not graph combinators, and remain unchanged in Phase 6a.
- `TryValidateProcessionalRouteIntent`, `BuildPhase1RouteIntentProjection`, `BuildRouteIntentOnlySnapshot`, `TryValidateAcceptedRouteRequirements`, recipe slot/port binding, vista reservation, room inflation, and planned-overlook appendages currently depend on exact processional counts, indices, IDs, degrees, or edge order. Phase 6a must preserve those facts and may not pretend they are generic.
- The two reviewed recipes remain bound outside the reusable assets to `threshold` and `vista-target`; their exact typed route edges, orientations, atomic placement, canonical-plan consumers, and catalog digest remain unchanged.
- `TransformCoarseCell`, stable route random streams, corridor compilation, `RouteTierRequirements`, tier planning, `DungeonLayout`, `TieredLevelPlan`, renderer, abyss support, dressing protection, and collision export are reused unchanged.
- There is no serialized macro-topology definition, profile weight, selector, hub/cross-link/long-return operation, generic embedding framework, second validator, renderer branch, or collision branch. Phase 6a adds none.

## Phase 6a acceptance budget — locked before production edits

The first increment is `processional_graph_composition_01`: replace only the hand-written processional node/edge assembly with a minimal internal composition builder supporting the already-used spine, branch, and rejoin operations. It produces the existing `RouteIntent` arrays directly. It is not a serialized definition, parallel graph DTO, public framework, topology selector, or new generation mode.

The deterministic corpus remains the 200 inclusive seeds `2026072100..2026072299` with the active spacious profile. The locked pre-change reference is Phase 5: 200/200 accepted and hard-valid, attempts min/p50/p95/max/mean `1/1/1/2/1.005`, retry codes `STAIR_PLACEMENT:32` and `PORT_GRAPH:11`, aggregate result hash `765ead1a87f95732fb66dfa617b33e91d1ea921cb91f0287226309d17af46155`, independent ordered per-seed multi-hash digest `1d35718f9d9b0d31f752a3b630f64d5f2ee134cbc651a2e79759a3c0a1b01f01`, and six sentinels with `REJECTED 0`.

Phase 6a passes only if all of these predeclared gates hold:

- the processional graph projection is exactly unchanged: pattern ID, planner/spatial random versions, node and edge order, stable IDs, roles, beats, main/branch order, relative elevations, transition kinds/rises, bottom/top nodes, recipe slots, vista endpoints, branch attach/rejoin nodes, degrees, and one-cycle count;
- focused operation tests prove that spine preserves order and connects consecutive nodes, branch attaches at its declared existing node, rejoin closes the declared branch once, and duplicate IDs, missing endpoints, self-edges, or a second rejoin are rejected without mutating a published graph;
- `BuildProcessionalRouteIntent` remains the sole production constructor and composes directly into the existing ephemeral `RouteIntent`; no placed/compiled graph DTO, adapter, serialized topology asset, profile selector, fallback, or pattern-specific downstream path is introduced;
- no unused hub, cross-link, long-return, topology-weight, recipe, vista, renderer, collision, lock, one-way edge, or runtime-generation extension point is added;
- all 33 Phase 0/1/3/4/5 focused fixtures remain passing, and any Phase 6a fixture exercises production composition rather than a diagnostic-only copy;
- both independent 200-seed sweeps are byte-identical to the locked Phase 5 per-seed intent, layout, tier, recipe, catalog, and canonical hashes; the aggregate result hash must remain exactly `765ead1a87f95732fb66dfa617b33e91d1ea921cb91f0287226309d17af46155` and the independent ordered digest exactly `1d35718f9d9b0d31f752a3b630f64d5f2ee134cbc651a2e79759a3c0a1b01f01`;
- attempt distribution and retry codes remain exactly the Phase 5 reference, every route/vista/recipe/hard validator remains passing, and all six real-renderer sentinels retain `REJECTED 0`; any drift is an implementation failure, not permission to advance a version or rewrite the budget;
- the full EditMode suite has no dungeon failure and no regression beyond the same 21 unrelated baseline failures; producer/consumer/test and symbol audits find one reachable graph-construction path and no unused combinator or compatibility scaffolding;
- the manual `AddEdge`/node-edge assembly displaced by the composition builder is deleted in the same increment, the Phase 6a deletion ledger is empty, and work stops before an atrium ring, twin-wing keep, topology selector, new recipe, generalized vista scoring, or promontory change.

Phase 6a is an identity-preserving extraction, so `dungeon-plan-v4`, `processional-spine-v4`, and `processional-spine-v1` remain the report, planner, and spatial-random versions. Advancing a version would hide an accidental output change and is forbidden in this increment.

## Exact Phase 6a handoff

1. Add the smallest internal graph composition builder beside the existing route intent. It may expose only the spine, branch, and rejoin operations consumed by `BuildProcessionalRouteIntent` in the same change.
2. Re-express the existing 13-node/13-edge processional graph through those operations without changing construction order, IDs, indices, edge order, recipes, elevation, vista, or random streams.
3. Keep processional validation and embedding specialized. Extract a shared validation helper only where the new builder and the existing production validator both consume it immediately; do not generalize index-bound spatial behavior ahead of a second topology.
4. Add focused deterministic composition tests and retain all prior fixtures. Prove the old manual assembly is gone and that no unconsumed operation or parallel graph family exists.
5. Run the locked corpus twice, compare every per-seed hash and both aggregate digests to Phase 5, run all six sentinels and the full EditMode suite, record evidence, close the deletion ledger, and stop.

Phase 6a exits only on exact identity. The next topology increment is not selected or budget-locked by this slice.

## Phase 6a implementation status

- `RouteGraphComposer` is a focused internal producer with only `TryAddSpine`, `TryAddBranch`, `TryRejoin`, and atomic `TryPublish`. It publishes the existing `RouteNodeIntent[]` and `RouteTraversalIntent[]` directly; it is not another route model.
- `BuildProcessionalRouteIntent` now composes the exact existing nine-node spine, four-node branch, and rejoin in the original node/edge order. The former local `AddEdge` helper and manual edge loops are gone. Planner/report/random versions are unchanged.
- Six focused EditMode tests assert the exact production node/edge/type/rise projection, the earned operation surface, one resulting cycle, invalid-ID/endpoint/self-edge/second-rejoin rejection, failed-operation atomicity, post-publication immutability, and fixed-seed determinism.
- Compile-only verification passed for `Assembly-CSharp-Editor.csproj` and `Arena.EditModeTests.csproj` with 0 errors and 0 warnings after temporarily refreshing their generated compile item lists; those generated project-file edits were reverted.
- The pinned Unity `6000.4.0f1` runtime gates pass: the new Phase 6a fixture is 6/6, and all Phase 0/1/3/4/5/6a focused fixtures are 39/39.
- Both independent 200-seed sweeps are 200/200 accepted and hard-valid with attempts min/p50/p95/max/mean `1/1/1/2/1.005`, retry codes exactly `STAIR_PLACEMENT:32` and `PORT_GRAPH:11`, aggregate result hash `765ead1a87f95732fb66dfa617b33e91d1ea921cb91f0287226309d17af46155`, and independent ordered per-seed multi-hash digest `1d35718f9d9b0d31f752a3b630f64d5f2ee134cbc651a2e79759a3c0a1b01f01`. The two per-seed hash projections are identical.
- All six real-renderer sentinels retain `REJECTED 0`. The full EditMode suite is 330/351: all six added tests pass, there is no dungeon failure, and the same 21 unrelated failures remain in `PredictedMeleeContactCueTests` (10), `RemotePresentationBufferTests` (8), `ProjectileVfxPoolingTests` (1), `SpellCueCatalogWriterTests` (1), and `UiInputContractTests` (1).
- Symbol and deletion audits find one reachable composition path and no hub, cross-link, long-return, atrium, twin-wing, topology-weight, selector, fallback, or local `AddEdge` production symbol in this slice. `git diff --check` passes, the deletion ledger is empty, and Phase 6a exits on exact identity.

## Phase 6b acceptance budget — locked before production edits

The next increment is `atrium_ring_topology_01`: add one atrium-ring route intent and coarse embedding composed from the already-proven spine, branch, and rejoin operations. A stable seed-parity selector makes even seeds processional-spine and odd seeds atrium-ring, giving exactly 100 seeds per pattern in `2026072100..2026072299` and three seeds per pattern in the six existing visual sentinels. This slice adds no topology weight, profile field, serialized topology asset, recipe, hub, cross-link, long-return operation, alternate canonical plan, renderer branch, or collision branch.

The atrium ring keeps the existing 13-node/13-edge budget, nine-node main route, four-node returning branch, two reviewed recipe slots, required vista, 0..24u elevation story, and stair/bridge/stairwell vocabulary. Its graph must be structurally distinct: branch attach at `ring-entry`, rejoin at `ring-rejoin`, one eight-edge cycle around a reserved central void, and `ring-overlook -> atrium-landmark` as the declared vista. Pattern identity, branch endpoints, vista endpoints, and planned-overlook relationships move into the ephemeral `RouteIntent` only where both production patterns consume them.

Phase 6b passes only if all of these predeclared gates hold:

- the selector reports exactly 100 processional-spine and 100 atrium-ring seeds in the locked corpus and is deterministic without consuming or perturbing the existing processional spatial random stream;
- all 100 processional seeds preserve their Phase 6a per-seed route-intent, layout, tier, recipe-resolution, recipe-catalog, and canonical hashes exactly;
- at least 95/100 atrium seeds and at least 195/200 overall seeds are accepted within the existing two-attempt ceiling; every accepted plan is hard-valid and passes route, transition, final-vista, recipe, renderer, abyss, and collision preconditions;
- the atrium graph projection has exactly 13 nodes, 13 traversal edges, one loop, an eight-edge cycle, two compatible reviewed recipe slots, a 0u bottom, a 24u top, and at least one stair, bridge, and stairwell requirement;
- the atrium embedding forms two traversable sides around a central reserved void; its declared vista endpoints are cardinally opposed, reserve at least three clear cells, and realize or reject before rendering;
- two independent post-change corpus sweeps have identical aggregate result hashes and identical ordered per-seed multi-hash digests; no version is advanced between those sweeps;
- all prior focused fixtures plus the new Phase 6b production tests pass; the six real-renderer sentinels retain `REJECTED 0` with three seeds from each pattern; the full EditMode suite has no dungeon failure and no regression beyond the same 21 unrelated baseline failures;
- the processional-only branch-index, vista-index, and planned-overlook assumptions displaced by the second pattern are removed in the same increment, the deletion ledger is empty, and work stops before twin-wing, hub, cross-link, long-return, new-recipe, generalized vista-scoring, promontory, or runtime-generation work.

Because production behavior and the report schema now expose a second named pattern, Phase 6b advances the report envelope to `dungeon-plan-v5`. The processional intent retains `processional-spine-v4` and `processional-spine-v1` so its locked per-seed hashes and spatial choices remain comparable; the new pattern begins at `atrium-ring-v1` and uses the same stable random service with atrium-specific stable IDs.

## Phase 6b implementation status

- `TryBuildRouteFirstDungeonLayout` is the sole reachable layout builder. Its stable parity selector chooses processional-spine for even seeds and atrium-ring for odd seeds; an unknown pattern rejects with `ROUTE_PATTERN_UNSUPPORTED` and never falls back.
- `BuildAtriumRingRouteIntent` composes a distinct 13-node/13-edge graph through the existing spine, branch, and rejoin operations. The `ring-entry -> ring-rejoin` loop has eight nodes, and `ring-overlook -> atrium-landmark` reserves the central vista across the ring.
- The second pattern earned only the narrow shared intent facts now consumed by both patterns: pattern/planner identity, branch endpoints, required cycle length, planned-overlook pairs, and generic-wing policy. Validation, recipe orientation, graph reporting, and overlook appendages consume those facts without a pattern-specific renderer, tier plan, canonical model, or collision path.
- The compact atrium embedding uses 7-cell cross-ring spacing, 9-cell stair-bearing spacing, four rotations, and mirroring inside the existing 40x40 profile. It disables optional generic wings so the reviewed landmark episode retains its declared clearance; both reviewed recipes otherwise use the unchanged placement and validation path.
- Six new production-facing tests cover selector stability, processional version retention, exact atrium nodes/edges/transitions, eight-node cycle structure, central-vista geometry, deterministic hard-valid planning, and unchanged renderer/collision consumption. All Phase 0/1/3/4/5/6a/6b focused fixtures pass 45/45.
- The locked corpus selects exactly 100 processional and 100 atrium seeds. Both final sweeps are 200/200 accepted and hard-valid, every seed succeeds on its first attempt, and all accepted route/vista/recipe gates pass. The repeat result hash is `53a07a59b9fb260c49e780f8bab8b196633abd9e89494bd06c0a1f17bd8878db`; the ordered pattern-plus-per-seed multi-hash digest is `bd1b062804002623380551df4d43d5016ca8abb18a3bc222f896784b2dcf894f`.
- All 100 processional seeds exactly preserve their Phase 6a route-intent, layout, tier, recipe-resolution, recipe-catalog, and canonical hashes. Processional canonical hashes retain the `dungeon-plan-v4` namespace while new atrium canonical hashes use `dungeon-plan-v5`; the report envelope is `dungeon-plan-v5` / `route-topologies-v5`.
- The six real-renderer sentinels split three processional and three atrium and all retain `REJECTED 0`. The full EditMode suite is 336/357 with no dungeon failure and exactly the same 21 unrelated baseline failures.
- Symbol and deletion audits find only spine, branch, rejoin, and publish graph operations; no hub, cross-link, long-return, twin-wing, topology weight, serialized topology asset, new recipe, runtime path, silent fallback, or alternate downstream pipeline exists. The displaced processional-only downstream assumptions are gone, `git diff --check` passes, and the Phase 6b deletion ledger is empty.

## Phase 6c acceptance budget — locked before production edits

The next increment is `twin_wing_topology_01`: add one twin-wing keep graph and bounded embedding through the existing spine, branch, and rejoin operations. The selector advances from parity to stable seed modulo four: residues 0/2 remain processional-spine, residue 1 remains atrium-ring, and residue 3 becomes twin-wing-keep. The locked corpus therefore contains exactly 100 processional, 50 retained atrium, and 50 twin-wing seeds; the six fixed sentinels contain three processional, two atrium, and one twin-wing seed.

The twin-wing graph has 13 nodes and 14 typed traversal edges: a seven-node main route plus two three-node wings attached at `wing-hub` and rejoined at `wing-rejoin`. It must have cycle rank two, a ten-node cycle core, degree four at both shared junctions, and structurally symmetric wing path lengths. The declared `wing-overlook -> keep-landmark` vista crosses the space between one wing and the main route, while the two existing reviewed recipes remain at the compression threshold and landmark using their existing port bindings.

The symmetric embedding requires a 51-cell maximum dimension including room envelopes, so this slice increases only the active spacious profile's width/depth ceiling from 40 to 52 cells. Existing embeddings already fit their old bounds and must not move. No topology weight, new profile selector field, serialized topology asset, recipe, graph operation, renderer branch, canonical-plan variant, collision branch, cross-link, long-return, generalized vista scoring, promontory change, or runtime path is added.

Phase 6c passes only if all of these predeclared gates hold:

- the locked corpus selector reports exactly 100 processional-spine, 50 atrium-ring, and 50 twin-wing-keep seeds using `seed-modulo4-v1`;
- all 100 processional seeds preserve their Phase 6b route-intent, layout, tier, recipe-resolution, recipe-catalog, and canonical hashes exactly, and all 50 retained atrium seeds with residue 1 preserve the same six hashes exactly;
- at least 48/50 twin-wing seeds and at least 198/200 overall seeds are accepted within the existing two-attempt ceiling; every accepted plan is hard-valid and passes route, transition, final-vista, recipe, renderer, abyss, and collision preconditions;
- the twin-wing projection has exactly 13 nodes, 14 edges, seven main nodes, six wing nodes, cycle rank two, a ten-node cycle core, two degree-four shared junctions, equal four-edge hub-to-rejoin wing paths, two compatible reviewed recipe slots, a 0u bottom, a 24u top, and stair/bridge/stairwell requirements;
- the twin-wing embedding keeps both wings disjoint outside their shared hub/rejoin, keeps every edge cardinal, preserves the authored room envelopes, and realizes or rejects its cardinally opposed vista with at least three clear reserved cells before rendering;
- the active 52x52 profile admits the twin-wing footprint without changing any preserved processional or retained-atrium hash;
- two independent post-change corpus sweeps have identical aggregate result hashes and identical ordered pattern-plus-per-seed multi-hash digests;
- all prior focused fixtures plus the new Phase 6c production tests pass; all six real-renderer sentinels retain `REJECTED 0` with all three patterns represented; the full EditMode suite has no dungeon failure and no regression beyond the same 21 unrelated baseline failures;
- the single-cycle validator facts displaced by the third pattern are replaced by consumed cycle-rank, cycle-core, and junction-degree facts, no redundant hub combinator or fallback is introduced, the deletion ledger is empty, and work stops before additional Phase 6 content or topology work.

Phase 6c advances the report envelope to `dungeon-plan-v6` and the active generator envelope to `route-topologies-v6`. Processional intent/canonical versions remain `processional-spine-v4` / `dungeon-plan-v4`; retained atrium intent/canonical versions remain `atrium-ring-v1` / `dungeon-plan-v5`; twin-wing begins at `twin-wing-keep-v1` / `dungeon-plan-v6`. All patterns continue to use the unchanged `processional-spine-v1` stable spatial-random service with pattern-specific stable IDs.

## Phase 6c implementation status

- The stable modulo-four selector retains processional-spine for residues 0/2 and atrium-ring for residue 1, and selects twin-wing-keep for residue 3. Unknown pattern identities reject explicitly; there is no compatibility fallback.
- `BuildTwinWingRouteIntent` composes its seven-node spine and two three-node returning wings only through the existing spine, branch, rejoin, and publish operations. The result has 13 nodes, 14 typed edges, cycle rank two, a ten-node cycle core, degree-four `wing-hub`/`wing-rejoin` junctions, and equal four-edge wing paths.
- The twin-wing embedding is cardinal, bounded to 51 cells including room envelopes, and realizes the declared `wing-overlook -> keep-landmark` vista with ten-cell center separation. The active spacious profile ceiling is 52x52; neither older embedding moved.
- Cycle validation now consumes explicit rank, cycle-core-node-count, and junction-degree facts for all three patterns. Transition diagnostics consume the selected graph's actual edge count. Canonical namespaces remain v4 for processional, v5 for retained atrium, and v6 for twin-wing under the v6 report/generator envelope.
- Six new production-facing tests cover selector/version stability, exact graph order and typed edges, twin-cycle structure, bounded vista geometry, deterministic hard-valid production, and unchanged renderer/collision consumption. All Phase 0/1/3/4/5/6a/6b/6c focused fixtures pass 51/51.
- Both final locked-corpus sweeps are 200/200 accepted and hard-valid with exactly 100 processional, 50 atrium, and 50 twin-wing seeds. Every seed succeeds on attempt 1; the only internal candidate rejections are the retained `PORT_GRAPH:3`. Result hash `f7462647e9f079ef8a72b3c8f9f88f2ce939978ffa7125eb0b9081f4e1ab76f8` and ordered pattern-plus-per-seed multi-hash digest `62f1d8e9915cb26db8ba5ef0952462992b6999b91d1abe1ea61cc096a59ac383` repeat exactly.
- All six locked hashes match Phase 6b for every one of the 100 processional seeds and all 50 retained atrium seeds. Canonical-version counts are exactly 100 v4, 50 v5, and 50 v6.
- The six real-renderer sentinels split three processional, two atrium, and one twin-wing and all retain `REJECTED 0`. The full EditMode suite is 342/363 with no dungeon failure and exactly the same 21 unrelated baseline failures.
- Symbol and deletion audits find one reachable layout builder, no redundant hub combinator, no cross-link or long-return operation, no new recipe or serialized topology, no renderer/collision branch, no unused compatibility scaffold, and no silent fallback. `git diff --check` passes and the Phase 6c deletion ledger is empty.

## Phase 6d entry audit — route rhythm before new content

The next increment is `route_rhythm_policy_01`, a behavior-preserving validation slice over semantic facts already produced by every `RouteIntent`. Main-route order, role, beat, and recipe-bearing nodes are present and reported, but no production consumer currently prevents long semantic repetition or recipe crowding before embedding.

| Remaining Phase 6 need | Current producer/consumer state | Phase 6d treatment |
| --- | --- | --- |
| Route-level spacing and repetition | Every topology already produces ordered main-route roles, beats, and recipe bindings; production validation does not consume them as a rhythm policy. | Select now. Add one shared pre-embedding validator and rejection diagnostics. |
| Next reviewed recipe | The catalog and route validator still deliberately require exactly two reviewed recipes and two bound slots. | Defer until one new eligible node/role and its full isolated/neighbor/full-dungeon review matrix are budgeted together. |
| Coarse vista scoring | Each pattern produces exactly one required `RouteVistaIntent`; there is no candidate set to score. | Defer until a topology or recipe produces multiple legitimate vista candidates. |
| Named-target promontories | Generic promontory cells exist, but no canonical target identity or target-aware selector exists. | Defer as its own canonical-plan and renderer-consumer slice. |
| Explicit step motifs | The global pass remains parked and `step_library_index.json` is absent. | Defer until one reviewed measured motif can replace a specific late responsibility without enabling the parked pass. |

No new schema field, profile field, serialized asset, recipe, topology, graph operation, embedding, canonical-plan field, renderer branch, collision branch, or random stream is earned by this slice.

## Phase 6d acceptance budget — locked before production edits

The shared `route-rhythm-v1` policy applies to the ordered main route before coordinates are assigned. It requires contiguous unique main-route orders, forbids adjacent nodes with the same non-empty role or beat, permits no more than two occurrences of one role on the main route, and requires at least two intervening main-route nodes between recipe-bearing nodes. Branch order and topology structure remain governed by the existing graph validator; disconnected branch arrays are not treated as one false linear sequence.

Phase 6d passes only if all of these predeclared gates hold:

- the production validator consumes the policy for processional-spine, atrium-ring, and twin-wing-keep before embedding, and all three current semantic sequences pass;
- focused probes independently reject a duplicate/gapped main-route order, adjacent repeated role, adjacent repeated beat, a third separated occurrence of one role, and recipe-bearing nodes with fewer than two intervening main-route nodes;
- every rejection is deterministic, reason-coded through the existing `ROUTE_INTENT_INVALID` boundary, and cannot fall through to embedding or another topology;
- all 200 locked seeds preserve their Phase 6c route-intent, layout, tier, recipe-resolution, recipe-catalog, and canonical hashes exactly; `dungeon-plan-v6`, the three pattern planner/canonical versions, and every spatial random stream remain unchanged;
- two independent corpus sweeps remain 200/200 accepted and hard-valid on attempt 1 with the exact 100/50/50 split, repeat the Phase 6c result hash `f7462647e9f079ef8a72b3c8f9f88f2ce939978ffa7125eb0b9081f4e1ab76f8`, and repeat ordered multi-hash digest `62f1d8e9915cb26db8ba5ef0952462992b6999b91d1abe1ea61cc096a59ac383`;
- all prior focused fixtures plus the new Phase 6d production-policy tests pass; all six real-renderer sentinels retain their Phase 6c canonical hashes and `REJECTED 0`; the full EditMode suite has no dungeon failure and no regression beyond the same 21 unrelated baseline failures;
- the policy has one production definition and one production consumer, diagnostics do not participate in generation, the deletion ledger is empty, and work stops before a third recipe, multiple-vista scoring, named-target promontory, step motif, topology, renderer, collision, or runtime-generation change.

Because every currently valid production plan must remain byte-identical, Phase 6d does not advance the report, generator, pattern, recipe, or canonical versions. A changed locked hash is a regression, not a reason to advance a version.

## Phase 6d implementation status

- `TryValidateRouteIntent` now invokes one shared `route-rhythm-v1` validator before recipe compatibility, graph traversal, or embedding. The policy sorts only declared main-route nodes, verifies contiguous unique order, enforces role/beat repetition limits, and checks recipe spacing; branch arrays remain governed by graph structure rather than being misread as one linear route.
- Production invalid intents leave through the existing `ROUTE_INTENT_INVALID` boundary. There is no repair, alternate pattern, embedding attempt, or fallback after a rhythm rejection.
- Six new focused tests prove all three production patterns pass and independently prove rejection of gapped/duplicate order, adjacent role repetition, adjacent beat repetition, a third separated role occurrence, and recipe crowding through the full route validator. All dungeon-builder fixtures pass 57/57.
- Both final locked-corpus sweeps are 200/200 accepted and hard-valid with the exact 100/50/50 topology split, every seed on attempt 1, and only the retained `PORT_GRAPH:3` internal candidate rejections. The executable `phase6dBudgetResult` passes and pins the unchanged result hash `f7462647e9f079ef8a72b3c8f9f88f2ce939978ffa7125eb0b9081f4e1ab76f8`.
- All 200 seeds preserve all six Phase 6c hashes exactly. The ordered pattern-plus-per-seed digest remains `62f1d8e9915cb26db8ba5ef0952462992b6999b91d1abe1ea61cc096a59ac383`; report/generator, pattern/canonical, recipe, catalog, and spatial-random versions remain unchanged.
- All six real-renderer sentinels preserve their Phase 6c canonical hashes and retain `REJECTED 0`. The full EditMode suite is 348/369 with no dungeon failure and exactly the same 21 unrelated baseline failures.
- Symbol and deletion audits find one policy definition and one production call site, with diagnostics remaining read-only. No schema/profile/asset/topology/embedding/canonical-plan/renderer/collision/random-stream change or compatibility scaffold was added; the Phase 6d deletion ledger is empty.

## Phase 6e entry audit — named targets for promontories

The next increment is `named_vista_promontory_01`. Every production pattern already declares one stable `RouteVistaIntent`, resolves its exact source/target cells and facing, and protects at least three intervening void cells through final tier validation. The canonical tier plan also already carries generic promontory cells to the unchanged renderer, but the late selector has no target identity and no relationship to a planned sightline.

The locked Phase 6d report exposes the gap directly: all 200 seeds contain zero promontory cells. The active profile requires a 36-cell generic room, while every room large enough to approach that threshold is either smaller or protected by the reviewed recipe/vista reservations. The old random-room pass is reachable but produces nothing in the locked corpus, so preserving its nominal chance/length controls would preserve dead policy rather than reviewed behavior.

The other remaining Phase 6 candidates are larger or currently lack an immediate producer:

- a third reviewed recipe requires one newly eligible role/node plus the complete isolated, variation, neighbor, full-dungeon, lifecycle, gallery, and human-review matrix;
- multiple-vista scoring has no route pattern with more than one legitimate candidate vista;
- an explicit step motif remains blocked by the absent reviewed `step_library_index.json` and cannot be enabled by reviving the parked global pass.

Phase 6e therefore replaces only the inert generic late selector. A named promontory may occupy the source-side prefix of the already-resolved vista line only when at least three cells remain reserved as void. Its canonical resolution names the vista and target node, records exact source/target/facing/cells, and projects only those cells to the existing renderer and abyss-support path. It adds no new route schema, profile control, random draw, topology, recipe, visibility-candidate scorer, renderer branch, or collision branch.

## Phase 6e acceptance budget — locked before production edits

The deterministic corpus remains `2026072100..2026072299`. Its Phase 6d reference is 200/200 accepted and hard-valid on attempt 1 with the exact 100/50/50 topology split, result hash `f7462647e9f079ef8a72b3c8f9f88f2ce939978ffa7125eb0b9081f4e1ab76f8`, ordered multi-hash digest `62f1d8e9915cb26db8ba5ef0952462992b6999b91d1abe1ea61cc096a59ac383`, zero promontory cells, and reserved-vista counts of processional `3:78, 4:22`, atrium `7:9, 8:41`, and twin-wing `3:8, 4:42`.

Phase 6e passes only if all of these predeclared gates hold:

- the named selector consumes the one existing resolved vista for all three patterns, occupies at most four source-side cells, and leaves at least the declared three-cell void reservation; a vista with no surplus cell produces no promontory rather than weakening the vista or moving either endpoint;
- the locked corpus produces exactly 114 named promontories: 22 processional, 50 atrium, and 42 twin-wing; every resolution has non-empty contiguous cells beginning beside the declared source, faces the declared target, names both the vista and target node, remains at the source level, and overlooks a target at least 4u lower;
- malformed reservations deterministically reject missing target identity, non-cardinal/opposed facing, non-contiguous or off-axis cells, occupied planned cells, fewer than three remaining void cells, or a target that is not lower; no invalid named promontory reaches rendering;
- all 200 seeds preserve their Phase 6d route-intent, layout, recipe-resolution, and recipe-catalog hashes exactly; pattern planner versions, recipe/schema versions, topology selection, and every random stream remain unchanged;
- both independent sweeps remain 200/200 accepted and hard-valid on attempt 1 with the exact 100/50/50 split and produce identical new aggregate result hashes and ordered pattern-plus-per-seed multi-hash digests;
- the report/generator envelope and canonical plan advance to `dungeon-plan-v7` / `route-topologies-v7` because the tier plan now records target identity; diagnostics report named resolutions rather than inferring semantics from rendered cells;
- all prior focused fixtures plus the new Phase 6e production tests pass; all six real-renderer sentinels retain `REJECTED 0`, with the two atrium and one twin-wing sentinels visibly carrying their named promontories while the three processional sentinels preserve the minimum three-cell vista void;
- the old random large-room `ChoosePromontorySpurs`/`VoidRunFits` path, maximum-count constant, and its four now-unused profile/settings fields are deleted in the same increment; producer/consumer/test and symbol audits find one target-aware production path, the deletion ledger is empty, and work stops before a third recipe, multiple-vista scoring, step motifs, new topology, renderer, collision, or runtime-generation work.

This is an intentional canonical-plan behavior change, so Phase 6d tier/canonical and aggregate hashes are not preservation targets. Route intent, layout, recipes, catalog, selection, and spatial randomness remain the locked identity boundary.

## Phase 6e implementation status

- `named-vista-promontory-v1` consumes only the already-declared route vista. The route handoff carries an ordered source-side prefix of at most four cells; the tier planner validates identity, opposed cardinal facing, contiguous clear cells, source level, lower target, and the remaining void budget before mutating the level field.
- `TieredLevelPlan` now owns one canonical `NamedVistaPromontoryResolution` when a vista has surplus space. Reports name its vista and target node and record exact source, target, facing, level, and cells. Rendering, abyss supports, boundary construction, and collision still receive only the flattened cells through their existing shared path.
- The old generic `ChoosePromontorySpurs` / `VoidRunFits` room scan, maximum-count constant, random roll, and four profile/settings fields were deleted. The active profile asset was migrated, and the symbol/deletion audit finds no surviving reference.
- The new Phase 6e fixture passes 6/6; all Dungeon Lab focused fixtures pass 63/63. Both editor projects compile with zero errors (`Arena.EditModeTests` has zero warnings; the editor assembly retains its existing warnings).
- Two independent corpus sweeps are byte-identical at the seed-record level: 200/200 accepted and hard-valid on attempt 1, exact 100/50/50 topology split, only `PORT_GRAPH:3` internal candidate rejections, and exact named-promontory distribution 114 = 22 processional + 50 atrium + 42 twin-wing. The new result hash is `ce00305a6b3d4c7043ca7e725689ed514ed99c99776668867675adfe8a9b0410`; the ordered seed/pattern/six-hash digest is `30ff994aed66b58a846ca68354f887e9fdcf72e79a31c7c2868daab27a5d6dc0`.
- All 200 seeds exactly preserve their Phase 6d route-intent, layout, recipe-resolution, and recipe-catalog hashes. The report/generator versions are now `dungeon-plan-v7` / `route-topologies-v7`, and the executable `phase6eBudgetResult` passes every gate.
- All six real-graphics sentinels retain `REJECTED 0`. The two atrium and one twin-wing captures render the named deck/support geometry cleanly; the three processional sentinels have no surplus and preserve their three-cell void. The full EditMode suite is 354/375 with no dungeon failure and exactly the same 21 unrelated baseline failures.
- `git diff --check` passes. One target-aware planner/resolver path remains, diagnostics are read-only, and the Phase 6e deletion ledger is empty.

## Phase 6f entry audit — third reviewed corner-return connector

The remaining Phase 6 candidates were re-audited against current production data. Multiple-vista scoring still has no pattern that produces two independently valid source/target reservations, so adding a scorer would create policy without a real choice. The parked step-formation pass still depends on the absent reviewed `step_library_index.json`; reviving it or treating unreviewed prefab measurements as a motif contract remains forbidden.

The third-recipe seam now has one exact common producer. Node index 12 is a `connector` / `return` beat in processional-spine, atrium-ring, and twin-wing-keep. In every accepted Phase 6e layout it has exactly two perpendicular route neighbors: the branch reward on entry and the existing rejoin edge on exit. It is outside the main-route recipe-spacing rule, has a 9x9 reserved envelope, and already reaches the shared recipe placement, tier, validation, renderer, abyss, and collision consumers.

Phase 6f therefore added only `connector_corner_return_01`: a reviewed 5x5 corner connector with perpendicular mandatory corridor ports, protected L-shaped circulation, one offset rise-1 reward bay, and one explicit `seam-rise-1` transition. The `RouteForward` binding derives its axis from the named `exit` edge rather than array position, so terminal branch nodes use the same recipe contract without a pattern-specific placement path. No schema kind, optional-port behavior, topology edge, vista, renderer branch, collision branch, or step-library dependency was added.

## Phase 6f acceptance budget — locked before production edits

The deterministic corpus remains `2026072100..2026072299`. Its Phase 6e reference is 200/200 accepted and hard-valid on attempt 1 with exact 100/50/50 topology selection, result hash `ce00305a6b3d4c7043ca7e725689ed514ed99c99776668867675adfe8a9b0410`, ordered multi-hash digest `30ff994aed66b58a846ca68354f887e9fdcf72e79a31c7c2868daab27a5d6dc0`, catalog digest `ffb857de9265a73b1d5357bdb90caf9a376b02d2433e72b4f43c38c2966f6f5e`, and 114 valid named promontories.

Phase 6f passes only if all of these predeclared gates hold:

- the reviewed catalog contains exactly three current schema-v1 recipes, including `connector_corner_return_01` eligible only for `connector` / `return`; every production intent binds it at node 12 with `entry` on the branch reward edge and `exit` on the pattern's existing rejoin edge;
- the new recipe has one exact 5x5 walkable footprint, two perpendicular mandatory ports, protected L-shaped circulation, one offset 2x3 rise-1 bay, and one reviewed `seam-rise-1` transition with complete footprint, landing, lane, rise, and headroom contracts; it introduces no new recipe field or enum value;
- all four rotations and both mirror states pass isolated structure/port/transition checks; generic-neighbor, full-dungeon, lifecycle, stale-review, digest, gallery, renderer, abyss-support, and collision evidence pass through the existing Phase 5 workflow before promotion to `Reviewed`;
- every accepted seed resolves exactly three atomic recipes and names the new connector once; the two existing recipes retain their content digests and named random streams, while recipe slot IDs, catalog digest, route/tier/canonical hashes, and aggregate hashes may change intentionally;
- all 200 seeds remain accepted, post-plan hard-valid, route/vista/recipe/named-promontory valid, p95 layout attempt 1, maximum attempt at most 2, and exact 100/50/50 topology selection; two independent sweeps produce identical per-seed records, aggregate hashes, and ordered multi-hash digests;
- the report/generator envelope advances to `dungeon-plan-v8` / `route-topologies-v8`; processional, atrium, and twin-wing planner versions advance because each route intent gains the third slot, while `processional-spine-v1` spatial randomness, recipe schema v1, graph edges, elevations, topology selection, and vista/promontory policy remain unchanged;
- all prior focused fixtures plus new Phase 6f production/workflow tests pass; all six real-renderer sentinels retain `REJECTED 0` and visibly preserve route, throne, vestibule, vista, and promontory readability around the new corner-return composition; the full EditMode suite has no dungeon failure or regression beyond the same 21 unrelated baseline failures;
- producer/consumer/test and symbol audits find one three-recipe production path and one generalized exit-edge orientation rule. No diagnostic-only contract copy, pattern-specific recipe renderer/validator, compatibility fallback, multiple-vista scorer, active late step pass, runtime path, or unused extension point remains; the Phase 6f deletion ledger is empty.

These gates are locked before the recipe asset, catalog, route-slot, placement, or version changes. Missing a gate requires revising the contract or placement algorithm, not weakening this budget after observing results.

## Phase 6f implementation status

- `connector_corner_return_01` is a current reviewed schema-v1 asset with review digest `f6844e4202cb7b04e666657756e264701d2ea9f0589c906bea482441551ffa22`. Its exact 5x5 footprint, perpendicular ports, five-cell protected L path, offset 2x3 rise-1 bay, and complete `seam-rise-1` transition pass the existing contract validator without a new field or enum value.
- The real authoring workflow generated 34 deterministic isolated/neighbor/full-dungeon/renderer evidence entries across all legal rotations and mirrors, gallery hash `d9f650dc86f9d8f7f90ad75ef5173e9641f54ea4e9ef221ff4e65ec0d4c32402`, then promoted the asset to `Reviewed`. The three-recipe catalog loads current with recipe-catalog digest `f907a758c49c25a84d5931004a81f673904b86db6bd4109e130d8631528cdaf4`.
- All three route intents bind node 12 once: processional uses `branch-11-12` / `rejoin-12-7`, atrium uses `branch-11-12` / `rejoin-12-6`, and twin-wing uses `wing-b-11-12` / `wing-b-rejoin-12-5`. One shared `RouteForward` rule resolves orientation from the named `exit` edge and rejects missing or unrelated exit identity before placement.
- Two independent 200-seed sweeps are seed-record identical: 200/200 accepted and hard-valid on attempt 1, exact 100/50/50 topology split, 600 total recipe resolutions, exactly 200 corner-return resolutions, 114 valid named promontories, no post-plan failures, and only `CELL_LEVEL_CONFLICT:1` plus `PORT_GRAPH:9` internal candidate rejections. Both result hashes are `98f56a4ef0e8dd33912342b99799b3d4954d71b2aa4ccf3be9f366a296e02dba`; the seed-array digest is `1ff32dd0b3d543fa29d025719a91f3a4bede82d83bfe2028b1b845e490fcaba4`, and the ordered seed/pattern/six-hash digest is `0852a30c1014d732c11e8e4fcda798b6c4aa3c5b9e24eeb107f6efe5bbdb122f`.
- The intentional new slot changes recipe/catalog/route/tier/canonical identities. The unchanged graph, edge, elevation, vista, and node-placement projection preserves Phase 6e exactly at digest `db51e5e74a3741a54242a0c522f2e0c385942d06b34e959fd32b9cbd5d7b4cca`; named promontories plus the two pre-existing recipe resolutions preserve exactly at digest `fc4c60c1b55914e525c680ef3dd630418f3a8f7b564df7bfa8fcc2801b1ddb94`.
- The new Phase 6f fixture passes 8/8, the updated Phase 5 workflow fixture passes 8/8, and all Dungeon Lab fixtures pass 71/71. `Arena.EditModeTests` compiles with zero errors and warnings; the editor assembly compiles with zero errors and its existing warnings.
- All six real-graphics sentinels use `dungeon-plan-v8` / `route-topologies-v8` and report `REJECTED 0`. Representative processional, atrium, and twin-wing captures were inspected at original resolution with continuous routes, clean raised returns, and no visible seams, floating pieces, or blocked circulation. The full EditMode suite is 362/383: exactly the same 21 unrelated baseline failures and zero Dungeon Lab failures.
- Producer/consumer/test and symbol audits find one generalized exit-edge orientation definition with two production consumers. The temporary promotion entry point is deleted, no pattern-specific recipe renderer or validator exists, multiple-vista scoring and the parked late step pass remain absent, and the Phase 6f deletion ledger is empty.

## Known implementation facts and blockers

- Generator code: `Assets/Arena/Editor/Dungeons/RandomDungeon/`.
- Settings: `Assets/Arena/Content/Settings/Dungeons/RandomDungeon/`.
- Editor tests: `Assets/Arena/Tests/Editor/`.
- Gold reference scene: `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity`.
- Baked destination: `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`.
- `ActiveStepFormationPlacementEnabled` remains false, and `step_library_index.json` is absent. Do not enable the parked path by flipping the flag.
- There is no current dungeon design, code, or validation blocker. The 21 unrelated full-suite failures remain outside this workstream.

## End-of-session handoff

```text
Milestone/phase: Phase 6f third reviewed corner-return connector complete and verified; every production topology now resolves exactly three recipes through the shared contract and canonical consumers.
Completed this session: audited the remaining Phase 6 needs; selected and budget-locked `connector_corner_return_01`; authored, validated, gallery-tested, reviewed, and cataloged the exact raised-return contract; bound the common node-12 producer in all three route intents; generalized `RouteForward` orientation to the named exit edge; advanced the report/planner envelopes; added executable corpus evidence and eight focused tests; and completed every locked preservation, corpus, determinism, renderer, regression, symbol, and deletion gate.
Current validation result: Phase 6f 8/8, updated Phase 5 workflow 8/8, and all Dungeon Lab 71/71; two final sweeps each 200/200 accepted and hard-valid on attempt 1 with exact 100/50/50 selection, 600 recipe resolutions, 200 corner returns, 114 named promontories, result hash 98f56a4e...e02dba, seed-array digest 1ff32dd0...fcaba4, and ordered six-hash digest 0852a30c...db122f repeated exactly; all six sentinels report REJECTED 0; full EditMode 362/383 with the same 21 unrelated baseline failures and no dungeon failure.
Last known-good seeds and reports: processional 2026072100; atrium 2026072101; twin-wing 2026072103; DungeonLabReports/dungeon_plan_2026072100_2026072299.json, DungeonLabReports/Recipes/connector_corner_return_01/gallery_manifest.json, and DungeonLabReports/visual_sentinels/manifest.json.
Last diagnostic fact: the shared `RouteForward` resolver accepts a cardinal named `exit` edge regardless of route-array position and rejects missing or unrelated exit identity before inflation or placement; all three pattern-specific bindings reach the same recipe validator, canonical plan, renderer, abyss, and collision consumers.
Deletion-ledger items added/closed: the temporary Phase 6f promotion entry point was removed after the real workflow completed. No compatibility adapter, alternate contract, pattern-specific renderer/validator, diagnostic production path, multiple-vista scorer, active late step pass, or unused extension point remains. Phase 6f ledger is empty and closed.
Exact next action: begin the next Phase 6 entry audit from current production evidence. Multiple-vista scoring remains blocked until a pattern owns two legitimate candidate vistas; explicit step formation remains blocked until `step_library_index.json` supplies a reviewed measured motif. Select only a slice with an immediate producer and consumer and lock its budget before production edits.
Blocker or decision needed: none for the completed implementation. The next Phase 6 slice is not yet selected.
New chat necessary: no.
```

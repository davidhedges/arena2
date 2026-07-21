# Dungeon generator: current status

Start here when returning to dungeon work. Keep this page short and update it at the end of every dungeon-generation session.

Last updated: 2026-07-21

Active milestone: Phase 6b atrium-ring topology complete and verified; Phase 6 expansion continues with the next increment not yet selected

Production mode: the processional-spine route-first planner is the sole reachable layout builder

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
- Phase 5 now proves two structurally different reviewed recipes on one existing route/tier seam. Phase 6 may add breadth only through this contract and must not introduce another plan, stair, visibility, renderer, or collision path.

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
  -> DungeonLayout + ephemeral RouteTierRequirements (route + two placed recipe resolutions)
  -> TryBuildTieredLevelPlan / TryBuildTieredLevelPlanAttempt
     -> atomically realize protected recipes before generic structural fill
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
Milestone/phase: Phase 6b atrium-ring topology complete and verified; two named production topologies now share one canonical pipeline.
Completed this session: closed the pending Phase 6a Unity gates; audited and budget-locked `atrium_ring_topology_01`; added deterministic processional/atrium selection, the atrium graph and coarse embedding, only the shared intent facts earned by the second pattern, six focused tests, mixed-pattern diagnostics, and explicit unsupported-pattern rejection; completed every locked runtime, corpus, preservation, renderer, regression, symbol, and diff gate.
Current validation result: focused fixtures 45/45; two final mixed corpus sweeps each 200/200 accepted and hard-valid with exactly 100 seeds per pattern, all attempts equal to 1, and only `PORT_GRAPH:3` internal candidate rejections; result hash 53a07a59...8878db and ordered multi-hash digest bd1b0628...cf894f repeat exactly; all 100 processional seeds preserve all six Phase 6a hashes; six sentinels split 3/3 and report REJECTED 0; full EditMode 336/357 with exactly the same 21 unrelated baseline failures and no dungeon failure; `git diff --check` passes.
Last known-good seeds and reports: processional 2026072140; atrium 2026072101; DungeonLabReports/dungeon_plan_2026072100_2026072299.json and DungeonLabReports/visual_sentinels/manifest.json.
Last diagnostic fact: even seeds select the unchanged 10-cycle processional graph; odd seeds select the new eight-cycle atrium ring. Both have 13 nodes/13 edges, two reviewed recipe slots, one required vista, a 0..24u route, and direct reuse of the existing tier, renderer, abyss, and collision consumers.
Deletion-ledger items added/closed: processional-only downstream branch/vista/overlook assumptions and the single-pattern builder name were displaced; all were generalized or deleted. No compatibility layer, unused graph operation, profile weight, serialized topology, fallback, or temporary production symbol remains. Phase 6b ledger is empty and closed.
Exact next action: begin the Phase 6c entry audit. Compare the remaining production needs—twin-wing topology, the next reviewed recipe, coarse vista scoring, named-target promontories, spacing/repetition rules, or explicit step motifs—select the smallest one with an immediate producer and consumer, and lock its acceptance budget before edits. Do not re-open Phase 6b unless one of its locked hashes or gates changes.
Blocker or decision needed: none. Phase 6c scope and budget are the next decision.
New chat necessary: no.
```

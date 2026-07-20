# Dungeon generator: current status

Start here when returning to dungeon work. Keep this page short and update it at the end of every dungeon-generation session.

Last updated: 2026-07-21
Active milestone: Phase 1 complete and verified; Phase 2 is ready but has not started
Production mode: current room-first generator remains the default during the temporary Phase 1 comparison
Route-first planner: one selectable processional-spine 2D pilot; not yet the sole builder
Recipe authoring UI: design only; not implemented

## Read next

1. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules.
2. [`COHERENT_FLOORPLAN_PLAN.md`](COHERENT_FLOORPLAN_PLAN.md) — architecture, phases, and exit gates.
3. [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — the post-foundation content workflow.
4. [`stair_forge_design.md`](stair_forge_design.md) — detailed vertical-traversal decisions and history.

## Locked context

- Everything rises from the common abyss. The resulting vertical supports are intentional and acceptable.
- The current generator is route-aware but not strict route-first: it places rooms/corridors before deriving the final route/elevation structure.
- Current seeds create many layouts and there are multiple named elevation archetypes, but there is no named semantic macro-topology catalog yet.
- Traversal and visibility will be planned separately so overlooks can reveal meaningful distant rooms.
- Vista realization is not deferred architecturally: Phase 1's skeleton must represent facing, reserved void, and an unobstructed source-to-target sight volume; Phase 3 proves one required vista.
- Recipes complement generic generation; they do not replace the whole floor with a large pool of fixed room prefabs.
- Active late-pass step-formation placement remains parked. Normal transition stairs, stair forging/synthesis, bridges, seam steps, daises, showpieces, and promontories remain active capabilities.
- Current doorway expression is generally a deliberate wall gap. Rich gateway geometry is not a planner prerequisite.
- `DungeonLayout` and `TieredLevelPlan` remain the canonical downstream plans. Do not create parallel intent/placed/compiled DTO families or a legacy adapter.
- Migration gets one temporary selector at layout-builder selection. Remove it with the old 2D builder in Phase 2, before vertical-intent and recipe work.
- Each phase has a deletion ledger. A phase is incomplete if its scheduled temporary or superseded code remains reachable.
- The first spatial algorithm is skeleton-first: embed a coarse self-avoiding route, attach a bounded branch/rejoin, reserve room envelopes, then inflate rooms and connect boundary anchors.
- Named topology variants compose shared graph operations rather than becoming separate planner implementations.
- Locks, ability gates, and one-way traversal are out of scope, but the traversal-edge design must not foreclose those future consumers.

## Phase 0 result

Phase 0 added diagnostics and tests only. It did not add a route-first builder, selector, intent/placed/compiled DTO, adapter, or intentional generation-output change.

- Fixed corpus: 200 inclusive seeds, `2026072100..2026072299`, profile `spacious`.
- Catalog digest: `59022466d5f1882e8f7992700c7265ff0b91f8ffbb8b3ba1f25885f563e3ba53`.
- Stable result hash: `5d487223b4cd39240c2b9c7978c175c8013eaa6cb3b53b124a929440a2b3b515`. Two independent sweeps produced the same hash. The hash is SHA-256 over the ordered seed-report array and excludes `generatedAtUtc`.
- Generation completion: 200/200. Post-plan hard validity: 197/200.
- Layout attempts: min 1, p50 1, p95 1, max 2, mean 1.04; histogram `1:192, 2:8`.
- Existing-layout graph proxies: longest root route rooms min/p50/p95/max/mean `3/4/6/7/4.575`; branch nodes `2/4/7/8/4.185`; loop edges `1/3/4/4/2.395`.
- Elevation span in 1u levels: `8/16/22/24/16.235`; transitions `9/21/37/56/21.81`.
- Visible-distant-room proxy: `0/13/23/35/12.845`. This is the current adjacent-cell elevation-delta count (`>=4u`), not a line-of-sight graph.
- Internal rejected-attempt codes: `ROOM_LEVEL_DELTA:508`, `STAIR_PLACEMENT:252`, `PORT_GRAPH:92`, `NO_LOOP_EDGE:29`, `CELL_LEVEL_CONFLICT:11`, `HEADROOM_CLEARANCE:1`.
- Post-plan validation code: `POST_PLAN_HEADROOM_CLEARANCE:3`, on seeds `2026072111` (`-2u` at cell `(4,21)`), `2026072178` (`0u` at `(24,28)`), and `2026072218` (`2u` at `(11,5)`), against the existing 3u minimum. These characterize a late-plan seam; Phase 0 deliberately does not repair or alter it.
- Deletion ledger: empty. No production behavior was scheduled for or removed in Phase 0.

The batch report is `DungeonLabReports/phase0_baseline_2026072100_2026072299.json`. It contains stable canonical projections and hashes for the existing `DungeonLayout` and `TieredLevelPlan`, validation checks, reason-code histograms, distributions, all seed reports, and the empty deletion ledger. `DungeonLabReports/` is intentionally ignored because the outputs are reproducible.

## Phase 1 result

Phase 1 added only the processional-spine 2D pilot and comparison diagnostics. It did not begin vertical intent, recipes, additional topologies, a socket-solver framework, or Phase 2 deletion.

- The ephemeral `RouteIntent` is built before spatial coordinates. It has a nine-node main route from arrival through culmination, a four-node purposeful branch, 13 traversal edges over 13 nodes, and exactly one branch/rejoin loop.
- The solver embeds the self-avoiding coarse skeleton first, attaches the branch/rejoin through bounded deterministic search, reserves 9x9 room envelopes and approach directions, inflates generic room footprints through at most six alternatives per node, and connects only declared route edges before constructing the existing `DungeonLayout` directly.
- Independent random streams are derived from seed, `processional-spine-v1`, layout attempt, stable node/edge identity, and purpose. Placement evidence is recorded, while the ordered route intent, `DungeonLayout`, and `TieredLevelPlan` are separately projected and hashed.
- The single production fork is at the candidate-layout assignment in `TryBuildAcceptedPlan`. Both builders continue through the unchanged `TryBuildTieredLevelPlan`, validators, boundary construction, `ElevationEdgeModel`, renderer, abyss support, and collision path. There is no fallback from a rejected pilot attempt to the old builder.
- The pilot represents one mutually facing vista from `vista-source` to `vista-target`, reserves at least three intervening void cells, and proves the candidate sight volume unobstructed at the `DungeonLayout` handoff. Reports separately record whether the unchanged tier planner's later loop addition occupies any reserved cell; final pre-render preservation and sightline realization remain Phase 3 work.
- Stable route-builder failure categories are `ROUTE_INTENT_INVALID`, `ROUTE_MAIN_EMBEDDING_INVALID`, `ROUTE_MAIN_EMBEDDING_EXHAUSTED`, `ROUTE_BRANCH_EMBEDDING_INVALID`, `ROUTE_BRANCH_EMBEDDING_EXHAUSTED`, `ROUTE_ROOM_INFLATION_EXHAUSTED`, `ROUTE_VISTA_RESERVATION_BLOCKED`, `ROUTE_CORRIDOR_EMBEDDING_EXHAUSTED`, `ROUTE_FLOOR_DISCONNECTED`, and `ROUTE_DENSITY_PRECONDITION`.
- The open deletion-ledger item is the Phase 1 comparison state and candidate-layout branch, `BuildRandomDungeonLayoutData` and its old-only helpers/settings, and Phase 1-only comparison labels. Its removal phase is exactly Phase 2; nothing is scheduled for Phase 1 deletion.

The report is `DungeonLabReports/phase1_pilot_2026072100_2026072199.json`; `summaryVersion` is `phase1-v1` and `generatorVersion` is `processional-spine-v1`.

### Locked-budget result

- Fixed corpus: 100 inclusive seeds, `2026072100..2026072199`, profile `spacious`.
- Accepted and post-plan hard-valid: 100/100; failures: 0.
- Layout attempts: min 1, p50 1, p95 1, max 2, mean 1.01; histogram `1:99, 2:1`.
- Every locked threshold passed: hard-valid completion `100 >= 95`, maximum attempt `2 <= 2`, p95 attempt `1 <= 1`, every accepted plan hard-valid, and every possible non-completion required to carry a stable reason code.
- Two independent sweeps produced the identical result hash `63ad28e579e6b5bd248b5d472315df706284ba0719aa56088825c3daa7421b5e`.
- Internal rejected tier attempts before eventual acceptance were `ROOM_LEVEL_DELTA:405`, `STAIR_PLACEMENT:7`, and `PORT_GRAPH:4`. No route-builder attempt exhausted its bound on the locked corpus, and there were no post-plan validation failures.
- Vista evidence: 100/100 accepted pilot layouts have opposed source/target facing and an unobstructed reserved volume at the 2D handoff. The unchanged tier planner's later loop addition leaves all reserved cells empty in 35/100 and adds floor to at least one reserved cell in 65/100; that diagnostic is intentionally not repaired in Phase 1 and is the exact preservation seam for Phase 3.
- Pilot graph/vertical proxies, min/p50/p95/max/mean: longest root route rooms `6/7/9/9/6.76`; branch nodes `2/6/8/8/5.84`; loop edges `1/4/5/5/3.91`; elevation span `16/22/25/25/21.89`; transitions `8/23/33/35/23.02`; visible-distant-room proxy `0/7/10/13/7.07`.
- Compared with Phase 0 medians, the rooted route rises from 4 to 7 rooms, branch nodes from 4 to 6, loop edges from 3 to 4, elevation span from 16u to 22u, and transitions from 21 to 23. The old adjacent-cliff visibility proxy falls from 13 to 7; it is not line-of-sight. All pilot layouts additionally carry the explicit unobstructed 2D vista reservation described above, and the six rendered comparisons did not show a serious visibility or usability regression.

## Current comparison seam

The temporary Phase 1 fork is confined to layout-builder selection:

```text
GenerateWithSeed
  -> GenerateRandomDungeonLayout
  -> TryBuildAcceptedPlan
  -> [default] BuildRandomDungeonLayoutData
     OR
     [Phase 1 menu/batch/test scope] TryBuildProcessionalSpineDungeonLayout
  -> DungeonLayout
  -> TryBuildTieredLevelPlan / TryBuildTieredLevelPlanAttempt
  -> TieredLevelPlan
  -> TryBuildRoomBoundaryContext
  -> ElevationEdgeModel.BuildLevelField
```

`RandomDungeonSceneBuilder.RebuildWithSeed` then marks collision and calls `GameplayCollisionExporter.ExportActiveSceneSharedCollisionData`. The Phase 0 and Phase 1 renderer probes use the real boundary and `BuildLevelField` seam, check the build report plus enabled non-trigger mesh colliders, and destroy their probe root; they do not repair output or write a scene.

## Known implementation facts

- Generator code: `Assets/Arena/Editor/Dungeons/RandomDungeon/`.
- Content settings: `Assets/Arena/Content/Settings/Dungeons/RandomDungeon/`.
- Authored dungeon prefabs: `Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/`.
- Gold-standard reference scene: `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity`.
- Baked destination: `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`.
- Existing editor-test area: `Assets/Arena/Tests/Editor/`.
- `ActiveStepFormationPlacementEnabled` is false. `step_library_index.json` is absent, so the parked path is not ready to enable by flipping that flag.
- `DungeonLabGenerator.cs`, `ElevationEdgeModel.cs`, and `StairForge.cs` total roughly 27,000 lines; do not pre-emptively refactor them.

## Six visual sentinels

Run `Tools > Dungeon Lab > Capture Phase 0 Visual Sentinels` for the baseline or `Tools > Dungeon Lab > Phase 1 > Capture Pilot Visual Sentinels` for the pilot. Each recreates 1600x900 PNGs and a manifest under its corresponding `DungeonLabReports/phase*_visual_sentinels/` directory.

| Category | Seed | Light annotation |
| --- | ---: | --- |
| Representative A | `2026072140` | Median-like route, branch, loop, elevation, transition, and distant-room proxy counts. |
| Representative B | `2026072186` | Median-like alternate archetype with comparable graph and elevation measures. |
| Weak A | `2026072169` | No adjacent-cell distant-room proxy despite a multi-tier layout. |
| Weak B | `2026072245` | Short rooted route and near-minimum distant-room proxy. |
| Edge A | `2026072262` | Maximum transition count in the fixed range. |
| Edge B | `2026072223` | Maximum elevation span with a sparse branch/loop graph and high transition count. |

Both sets use the same seeds and camera process. All six Phase 1 captures rendered with zero rejected placements and valid stair/partition checks. Side-by-side review found no serious regression in usability, spatial variety, verticality, or distant-room readability: the pilot views are generally denser and more continuously processional while preserving open exterior separations. This is a light sentinel review, not Phase 3 line-of-sight proof. Each manifest records canonical hashes and renderer summaries; the focused renderer test separately proves real collision preconditions.

## Phase 1 reliability budget — locked before pilot

Evaluate the first 100 fixed seeds, `2026072100..2026072199`:

- completion floor: at least 95/100 hard-valid completions;
- attempt ceiling: at most 2 layout attempts per seed;
- p95 attempt target: at most 1 layout attempt;
- every non-completion must have an explicit stable route-builder reason code;
- every result counted as accepted must pass all Phase 0 hard checks, including the post-plan headroom check.

These values are now fixed. If the Phase 1 pilot misses one, stop and revise its embedding algorithm rather than relaxing the budget or building later-phase infrastructure.

Phase 1 passed all five locked checks at 100/100 hard-valid completions, maximum two attempts, and p95 one attempt. The budget remains recorded here as historical evidence and must not be rewritten after the result.

## Last verified commands and seeds

Phase 1 focused EditMode suite — 6/6 passed, 0 failed, 1.847 seconds:

```bash
/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics \
  -projectPath /Users/davidhedges/Projects/arena2 \
  -runTests -testPlatform EditMode \
  -testFilter Arena.Tests.Editor.DungeonLabPhase1RouteFirstPilotTests \
  -testResults /private/tmp/arena2-phase1-main-tests.xml \
  -logFile /private/tmp/arena2-phase1-main-tests.log
```

The six tests prove intent-before-coordinates, repeated route/layout/tier hash determinism, direct `DungeonLayout` production, opposed vista facing and reserved candidate volume, all downstream hard checks, and real renderer collision preconditions without repair.

Locked Phase 1 sweep — run twice with identical result hash:

```bash
/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics \
  -projectPath /Users/davidhedges/Projects/arena2 \
  -executeMethod DungeonLab.Editor.DungeonLabGenerator.BatchValidatePhase1Pilot100Seeds \
  -logFile /private/tmp/arena2-phase1-main-sweep-1.log \
  -quit
```

The repeat used `/private/tmp/arena2-phase1-main-sweep-2.log`; both produced `63ad28e579e6b5bd248b5d472315df706284ba0719aa56088825c3daa7421b5e`.

Phase 1 visual capture — six PNGs, all renderer summaries with `REJECTED 0`:

```bash
/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/davidhedges/Projects/arena2 \
  -executeMethod DungeonLab.Editor.DungeonLabGenerator.CapturePhase1VisualSentinels \
  -logFile /private/tmp/arena2-phase1-main-sentinels.log \
  -quit
```

Phase 0 compatibility — focused suite 6/6 passed in 6.936 seconds, then the regenerated 200-seed report retained the exact Phase 0 hash:

```bash
/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics \
  -projectPath /Users/davidhedges/Projects/arena2 \
  -runTests -testPlatform EditMode \
  -testFilter Arena.Tests.Editor.DungeonLabPhase0CharacterizationTests \
  -testResults /private/tmp/arena2-phase0-regression-tests.xml \
  -logFile /private/tmp/arena2-phase0-regression-tests.log

/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics \
  -projectPath /Users/davidhedges/Projects/arena2 \
  -executeMethod DungeonLab.Editor.DungeonLabGenerator.BatchValidate200Seeds \
  -logFile /private/tmp/arena2-phase0-regression-sweep.log \
  -quit
```

The complete EditMode suite compiled and ran all 324 tests: 303 passed and 21 unrelated current-tree tests failed in 171.249 seconds. None involves a touched dungeon file: ten `PredictedMeleeContactCueTests` report reflection parameter-count errors, eight `RemotePresentationBufferTests` report missing `PlayerSnapshot` constructors, and one failure each concerns projectile VFX source-contract text, spell-cue slot identity, and a missing Nova VFX prefab `.meta`. Do not modify those systems during Phase 2.

| Purpose | Seed or range | Result | Report path |
| --- | ---: | --- | --- |
| Locked Phase 1 pilot | `2026072100..2026072199` | 100/100 accepted and hard-valid; p95 1, max 2; repeat hash matched | `DungeonLabReports/phase1_pilot_2026072100_2026072199.json` |
| Phase 0 compatibility | `2026072100..2026072299` | 200/200 generated; 197/200 hard-valid; exact locked hash retained | `DungeonLabReports/phase0_baseline_2026072100_2026072299.json` |
| Phase 1 visual sentinels | six fixed seeds listed above | 6/6 rendered; `REJECTED 0`; comparison accepted | `DungeonLabReports/phase1_visual_sentinels/manifest.json` |
| Known Phase 0 diagnostic | `2026072111` | `POST_PLAN_HEADROOM_CLEARANCE`, `-2u` at `(4,21)` | Phase 0 baseline report |

## Exact Phase 2 handoff

Phase 2 is an early 2D cutover and deletion phase only. Start in `Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.cs`, in `TryBuildAcceptedPlan`, at the temporary candidate-layout selector. Make `TryBuildProcessionalSpineDungeonLayout` the sole reachable layout builder, then remove the comparison state and old branch rather than retaining a permanent planner mode.

Perform these steps, in this order:

1. Prove references before deletion. Identify `BuildRandomDungeonLayoutData`, `PlaceRoomBand`, its nearest-room center-routing helpers, and settings used only by that path. Do not remove room-shape or corridor primitives called by the route-first builder.
2. Remove `phase1RouteFirstPilotSelected`, both builder-selection branches, the scoped comparison wrappers, and old-builder fallback reachability. Call `TryBuildProcessionalSpineDungeonLayout` directly with the same two-attempt bound and stable reason-coded rejection.
3. Delete `BuildRandomDungeonLayoutData`, `PlaceRoomBand`, nearest-room center-routing code, and proven old-only settings. Do not retain an adapter, a hidden fallback, or `Legacy`/`Coherent` generator labels.
4. Update the existing generate, specific-seed, batch, sentinel, and test entry points to use the sole builder. Remove or rename Phase 1-only comparison menu/report labels; keep useful route/layout diagnostics without adding a planner-mode framework.
5. Leave the ephemeral `RouteIntent`, processional-spine embedding, existing `DungeonLayout` handoff, `TryBuildTieredLevelPlan`, validators, boundary construction, `ElevationEdgeModel`, abyss support, renderer, collision export, and `TieredLevelPlan` contracts as the single path.
6. Prove repository-wide that the selector and superseded symbols are gone. Rerun the Phase 1 focused tests, locked 100-seed corpus, same six sentinels, Phase 0 hard-check fixture as applicable, and compile/run the full EditMode suite. Accepted locked seeds must retain their per-seed layout/tier validity and deterministic hashes apart from intentionally renamed report envelopes.
7. Close the Phase 2 deletion ledger completely. Stop before adding route elevation metadata, structural transition intent, final vista enforcement, recipes, another topology, or any other Phase 3+ infrastructure.

Do not introduce new plan DTOs, adapters, modes, recipe assets/catalogs, vertical systems, render/collision systems, atrium-ring/twin-wing topology, or general socket solving in Phase 2.

## Active blockers or pending decisions

No dungeon blocker prevents Phase 2. The 21 unrelated full-suite failures above remain outside this workstream. The three Phase 0 post-plan headroom diagnostics remain characterized old-path behavior; they are not permission for renderer repair. Phase 1's handoff-stage vista reservation is not permission to begin Phase 3 final sightline enforcement during cutover.

## End-of-session handoff

```text
Milestone/phase: Phase 1 complete and verified; Phase 2 ready and not started; Phase 3 not begun.
Completed this session: minimal RouteIntent, one processional-spine skeleton-first builder, direct DungeonLayout compilation, handoff-stage vista reservation, one temporary comparison fork, stable reports/reason codes, focused tests, locked 100-seed validation, six sentinel comparison, and Phase 0 regression proof.
Current validation result: Phase 1 EditMode 6/6; locked corpus 100/100 accepted and hard-valid, p95 1, max 2; repeat hash 63ad28e5...7421b5e. Phase 0 EditMode 6/6 and exact baseline hash retained. Full project EditMode 303/324 with 21 unrelated failures documented above.
Last known-good seed and report: 2026072140; DungeonLabReports/phase1_visual_sentinels/manifest.json.
Last diagnostic fact: the vista volume is guaranteed at the 2D DungeonLayout handoff; reports separately expose any later unchanged tier-loop occupation for Phase 3.
Uncommitted/generated files to preserve: all Phase 0 changes plus DungeonLabGenerator.RouteFirstPilot.cs/.meta, Phase 1 batch/generator changes, DungeonLabPhase1RouteFirstPilotTests.cs/.meta, this status, and reproducible ignored DungeonLabReports outputs.
Deletion-ledger items added/closed: Phase 1 comparison state/selector, old 2D builder and old-only helpers/settings, and Phase 1-only labels scheduled for Phase 2 / none.
Exact next action: read PROJECT_INVARIANTS.md first, then this file and COHERENT_FLOORPLAN_PLAN.md; execute only the seven-step Exact Phase 2 handoff above, beginning at TryBuildAcceptedPlan.
Blocker or decision needed: none.
```

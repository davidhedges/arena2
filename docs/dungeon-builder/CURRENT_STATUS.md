# Dungeon generator: current status

Last updated: 2026-07-25

No phased plan is in progress. This page describes what the generator is and where the work stands. Keep it short — if it starts growing per-phase evidence sections again, that evidence belongs in `DungeonLabReports/` or `docs/archive/`, not here.

## What it does

One integer seed produces one deterministic Unity scene (`Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`) plus matching client/server collision payloads.

Generation is editor-time only, on purpose: a client-only runtime layout would disagree with the authoritative server collision.

Pipeline, in execution order:

```text
seed + generation profile + recipe catalog
  -> RouteIntent            semantic graph, no coordinates
  -> embedding              node centers
  -> room footprints        + recipe placements
  -> corridors              -> DungeonLayout
  -> elevation + transitions-> TieredLevelPlan
  -> ElevationEdgeModel     -> GameObjects
  -> collision export       -> Resources + server/src/world_data
```

## How to run it

| Goal | Action |
|---|---|
| Rebuild the playtest scene | **Arena > Dungeons > Rebuild Random Dungeon** |
| Reproduce a specific layout | **Arena > Dungeons > Rebuild Random Dungeon (Specific Seed)** |
| Switch density | **Arena > Dungeons > Generation Profile > Spacious / Dense** (per-user pref; `ARENA_DUNGEON_GENERATION_PROFILE` overrides; batch mode defaults to `spacious`) |
| Plan only, no scene | **Tools > Dungeon Lab > Generate** |
| Batch evidence | **Tools > Dungeon Lab > Batch Validate (50 / 200 / 100 Locked Seeds)** |
| Command line | `-executeMethod DungeonLab.Editor.RandomDungeonSceneBuilder.RebuildRandomDungeonBatch`, with `ARENA_RANDOM_DUNGEON_SEED` set |

Always publish/restart the server module after regenerating, so server movement and spawning use the same geometry as the client scene.

## Current shape

- Three route patterns selected deterministically from the seed: processional spine, atrium ring, twin-wing keep.
- Three required recipe slots (`required-compression`, `required-landmark`, `required-return`) filled from an explicit catalog; four enabled recipes currently.
- Vertical traversal from reviewed stair contracts, forged contracts, online synthesis, stairwell towers, and bridges — in that fallback order.
- One planned vista with a reserved sight corridor, plus 1–4 external connector promontories.
- Everything rises from a shared abyss datum.

## Where the work stands

All previously planned phases are closed and their evidence machinery has been
removed. The per-phase log lives in
[`docs/archive/2026-07-dungeon-phase-log/`](../archive/2026-07-dungeon-phase-log/)
if you ever need to reconstruct why a decision was made. Treat it as history,
not as current constraints.

### Landed 2026-07-25 (see [`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md) §12)

- **Closed-phase archive deleted.** `Phase7`, `Phase7Collision`, `Phase7Gallery`,
  the corrective batch/collision-floor harness, the density/adjacency slice
  snapshots, and the per-phase acceptance-budget report blocks are gone.
  ~5,400 lines out of the generator, ~1,100 out of the tests.
- **59 diagnostic stage timers removed** from the production planning path.
- **One validation gate.** The twelve hard checks moved out of the batch report
  into `DungeonLabGenerator.Validation.cs` and now run inside the tier-attempt
  loop, where a failure is an ordinary retry reason. The batch report projects
  the same result instead of recomputing it. A plan that fails validation can no
  longer be rendered, saved, or exported.
- **Renderer skips are fatal.** The four sites that logged an error and continued
  (two of which did not even increment `stats.rejected`) now throw.
- **No more double generation.** The accepted plan carries the room boundary
  context that was validated with it, so the sentinel/render path no longer
  regenerates the whole dungeon to recover it.
- **Phase vocabulary gone** from type, method, and test names. Stale hardcoded
  plan hashes and planner-version equality assertions deleted from tests.
- **`ops/dungeon-compile-gate.sh`** compile-checks both assemblies without Unity,
  so work can continue while the editor holds `Temp/UnityLockfile`.

### Measured 2026-07-25 — `dense` profile, seeds 2026072100..2026072299

```text
accepted 198/200   hardValid 198/198   validationFailureCodes none
layoutAttempts   mean 1.07  p95 2  max 2   histogram {1: 186, 2: 14}
tierAttempts     mean 1.02  p95 1  max 2   histogram {1: 194, 2: 4}
failed seeds     2026072231, 2026072295 — both 64x ROUTE_TRANSITION_RESERVATION
                 "edge 'main-4-5' could not reserve its required Stairwell for rise 8u"
```

Two conclusions:

- **The validation gate rejects nothing.** Every accepted plan is hard-valid, so
  the gate is a pure safety net rather than a behaviour change. The "~1.5% of
  seeds are invalid" figure that circulated from the old phase log is dead.
- **`TierPlacementAttempts = 32` is 16x the observed maximum of 2.** It should be
  4, but lowering it changes output today, because failed tier attempts advance
  the shared draw stream and 14 seeds take a second layout attempt. Do the
  derived-RNG work first and the reduction becomes free.

### Derived RNG — landed 2026-07-25 (review §12, 2.2 + 2.3)

The shared sequential `System.Random` is gone. Every random decision now draws
from a stream keyed by `(seed, layout attempt, tier attempt, purpose, subject)`,
via `DungeonRandomScope` in `DungeonLabGenerator.Validation.cs`:

| Stream | Keyed per | Was |
| --- | --- | --- |
| `loop-corridor` | room pair | shared stream, order-dependent on rejected candidates |
| `stair-choice` | connection | shared stream, order-dependent on neighbouring corridors |
| `aerial-bridges` | whole plan | shared stream |
| `enclosed-rooms` | whole plan | shared stream, depended on how many attempts had failed |

Every RNG construction site in the generator is now hash-derived. Nothing is
threaded sequentially between stages, so a change to one decision cannot perturb
another — which is what made stored hashes rot and the suite run red.

Also removed: the room-dimension draw-**reuse** hack, which existed only to keep
the spacious profile byte-compatible with an older hash and made the number of
random draws depend on configuration. The spacious baseline itself is retained —
it is load-bearing as the stair-clearance cap.

`TierPlacementAttempts` dropped 32 -> 4 (2x the measured maximum). With
attempt-keyed streams this is output-neutral: an accepted plan no longer depends
on how many attempts preceded it.

**This is a deliberate one-time rebaseline. Every seed's output changes once.**

## Open: variation regression from the route-first cutover

Owner reported 2026-07-25 that dungeons all read the same. Traced to
**`6657465f floorplan refactor phase 2` (2026-07-21)**, which made route-first
the sole path. Not a subjective impression — measured over 200 seeds:

| Symptom | Evidence | Cause |
| --- | --- | --- |
| One topography | `elevationSpan` and `routeClimbLevels` are 24 for 199/199 seeds; `archetypes = AscendingSpine: 199` | `ElevationArchetype` had **11** members (Basin, Mesa, Ridge, Canyon, AscendingSpine, Descent, SplitPlateau, Crater, Helix, Terraces, Atrium) chosen per seed by `ElevationArchetypePlanner.Choose(random)`. It is now `enum RouteElevationPolicy { AscendingSpine }` — one member |
| Same connectivity | 3 fixed graphs on `seed % 4` (processional 50%, atrium 25%, twin-wing 25%) | All three `Build*RouteIntent` factories hardcode every node id, role, beat, edge **and elevation** as literals |
| Always 13 rooms | `rooms` min 13, p50 13, max 13 | All three route graphs happen to have 13 nodes |
| Identical room shapes | — | The same commit cut `DungeonGenerationProfile` 68 -> 24 settings, deleting the room size-class vocabulary (`largeRoom/midRoom/smallRoom` area ranges *and* counts, `nonRectChanceGrand`, `nonRectChanceMid`, `wingMinDimCells`, `wingMaxDepthCells`, `roomMaxSideCells`, `roomMaxAspectRatio`, `floorBudgetCells`) |

The archived plan listed "multiple elevation archetypes" under *pieces worth
preserving*, so this was preservation that did not happen, not a considered trade.

**Item 1 — room size ranges (done, unverified).** Both profiles widened.
`dense` was pinned at exactly 7x7 for every role because 7 is its vertical
ceiling; it now spreads 6-8 x 5-7. `spacious` now spreads 4-7 x 4-8.

Hard constraints when tuning these: room size <= `pitch - 1` on each axis (the
gap between adjacent rooms is `pitch - size`), and <= `roomEnvelopeRadiusCells
* 2 + 1`. Spacious pitch 9/9 -> ceiling 8x8. Dense pitch 9/8 -> ceiling 8x7.

**Items 2 and 3 merged — owner ruling 2026-07-25: an archetype is a different
graph entirely**, not a different elevation profile over the same graph. That
makes "turn node elevations into data" not worth doing on its own: elevations
are fields of a graph, so they become data when graphs do.

The slice is therefore **make a topology cheap to author**. It is not cheap now.
Adding one costs roughly 180-230 lines of hand-written C#:

| Per topology, today | Size |
| --- | --- |
| `Build*RouteIntent` — nodes, roles, beats, elevations, edge ids, transition kinds, all literals | ~130 lines |
| `TryEmbed*Route` — including a **hand-drawn coarse coordinate array**, one `Vector2Int` per node | 45-98 lines |
| branches in `TryEmbedRoute`, `ResolvePatternSpatialSettings`, `SelectRoutePattern` | 3 switches |

The layout is not procedural. Each pattern is one hand-drawn diagram — the
processional main route is literally `(0,0)(1,0)(2,0)(3,0)(3,1)(3,2)(2,2)(1,2)
(1,3)`, an S on a 4x4 grid — fed to `TryTransformCoarseEmbedding`, which tries
at most 4 quarter-turns against one mirror choice. **So each topology has <= 8
spatial arrangements, ever.** That, more than room size, is why plans read alike.

`SelectRoutePattern` is `seed % 4` mapped to 3 patterns, so it also needs
redesigning to scale past four.

The open question is now narrower: **author more diagrams as data, or replace
the hand-drawn diagram with a general graph embedder?** Authoring keeps
authorial control and suits "designed places"; a general embedder unlocks more
variety but risks legibility and is real work. Per the archived plan's decision
11 ("abstractions are earned by a working slice"), authoring two or three new
topologies first — and only then extracting — is the lower-risk order.

Encouragingly, the vocabulary for the owner's eventual goal already exists:
`RouteTransitionKind` covers `LevelCorridor`, `Stair`, `Bridge`, `Stairwell`,
and `RouteVistaIntent` / `RouteOverlookIntent` cover sightlines. Bridges,
balconies and overlooks are mostly a matter of *authoring* those edge kinds in
new graphs rather than building new systems — which is a further argument for
doing the graph-as-data work as the vehicle.

Room count also needs to vary: all three graphs have 13 nodes, so every dungeon
has exactly 13 rooms.

### Next, in order

1. **Confirm the split preserved identity** — run Batch Validate and expect the
   unchanged hash (see below).
2. **Seed 2026072295 is a real defect, not noise.** Its `main-4-5` edge needs a
   `Stairwell` for an 8u rise; a stairwell tower needs void cells beside the
   corridor and the `dense` profile leaves fewer, so the reservation is
   structurally impossible and the planner retries it until the ceiling. Worth
   understanding before adding route content. It sits outside
   `2026072100..2026072149`, the 50-seed window the last catalog change was
   validated against — that window was too narrow to have caught it.
3. **Remaining review items**, none urgent: unify the two floor representations
   (§12, 2.5), move the headroom gate after the late passes and delete the
   duplicated deck formula (2.4), carry `RouteIntent` into the plan to remove the
   `lastRouteIntent` static (2.7), and take the display strings out of
   `TieredLevelPlan` (2.9). The typed test API (2.6) is blocked on an asmdef
   migration of `Assets/Arena/Editor` as a whole — see the review's H4 note.

### Post-rebaseline measurement, same 200 seeds, `dense`

```text
                        before          after
accepted                198/200         199/200
hardValid               198/198         199/199
validationFailureCodes  none            none
layoutAttempts          mean 1.07       mean 1.06   max 2
tierAttempts            max 2           max 2       histogram {1: 197, 2: 2}
wasted rejections       512 + 4         52 + 2
failed seeds            ...231, ...295  ...295
```

Seed 2026072231 now succeeds: with independently keyed streams its stairwell
found a placement the old order-dependent draw never offered. Wasted work fell
10x, which is the `TierPlacementAttempts` 32 -> 4 reduction showing up directly.
`tierAttempts` still maxes at 2, so the new ceiling of 4 keeps 2x headroom.

**Determinism verified.** Two independent 200-seed runs produced the byte-identical
`resultHash` `3092863af94919fa2f77705014ec62b37e6bf13f8ef4e6cc1db23d0845a1bef6`
(catalog `8c0f30b2`, profile `dense`), 199/199 both times, and the scene plus
collision payloads rebuilt end to end.

That hash is a **transient comparison value, not a lock.** Do not assert it in a
test or re-baseline it per change — that ritual is what left three tests
permanently red. Any intentional change to generation is expected to move it.

### `TryBuildCellLevelField` split — landed 2026-07-25 (review §12, 1.2)

660 -> 268 lines of orchestration. Two blocks became named steps:

- `TryResolveConnectionTransition` — the 385-line per-connection body: level the
  corridor, then reserve its transition via reviewed stair contract, then online
  synthesis, then a stairwell tower.
- `AddZoneSeamStepStrips` — the zone-seam rise-1 strips.

Both were verified statement-for-statement identical to the code they replaced
before compiling, so this is an identity-preserving refactor.

**Verification: run Batch Validate (200 Fixed Seeds) and expect the SAME hash,
`3092863a…`.** Unlike the RNG rebaseline, a pure extraction must not move it. A
different hash means the extraction changed behaviour and should be reverted.

Also worth doing at some point: **Arena > Dungeons > Rebuild Random Dungeon** and
look at the result. No hash can tell you whether the dungeon still reads well.

## Read next

1. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules. Read before changing generator, measurement, contract, or placement code.
2. [`GLOSSARY.md`](GLOSSARY.md) — authoritative vocabulary (role vs. beat, room vs. recipe, zone, port, transition, reservation).
3. [`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md) — current system model and recommended work.
4. [`ROOM_AUTHORING_GUIDE_CURRENT.md`](ROOM_AUTHORING_GUIDE_CURRENT.md) and [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — adding content.
5. [`stair_forge_design.md`](stair_forge_design.md) — vertical traversal decision history.

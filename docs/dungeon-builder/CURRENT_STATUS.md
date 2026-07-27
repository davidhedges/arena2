# Dungeon generator: current status

Last updated: 2026-07-27

No phased plan is in progress. This page describes what the generator is and where the work stands. Keep it short — if it starts growing per-phase evidence sections again, that evidence belongs in `DungeonLabReports/` or `docs/archive/`, not here.

## What it does

One integer seed produces one deterministic Unity scene (`Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`) plus matching client/server collision payloads.

Generation is editor-time only, on purpose: a client-only runtime layout would disagree with the authoritative server collision.

Pipeline, in execution order:

```text
seed + density + generation profile + recipe catalog
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
| Switch density | **Arena > Dungeons > Density > 0..5** (per-user pref; `ARENA_DUNGEON_DENSITY` overrides; with neither, the profile asset's own `densityLevel`) |
| Plan only, no scene | **Tools > Dungeon Lab > Generate** |
| Batch evidence | **Tools > Dungeon Lab > Batch Validate (50 / 200 / 100 Locked Seeds)** |
| Command line | `-executeMethod DungeonLab.Editor.RandomDungeonSceneBuilder.RebuildRandomDungeonBatch`, with `ARENA_RANDOM_DUNGEON_SEED` set |

Always publish/restart the server module after regenerating, so server movement and spawning use the same geometry as the client scene.

## Current shape

- Seven route topologies, one drawn per seed by weight: processional spine, atrium ring, twin-wing keep, cataract shaft (descending), sunken basin, terraced cascade, ridge and ravine. Each is one JSON file under `Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`; adding one costs no C#.
- Three required recipe slots (`required-compression`, `required-landmark`, `required-return`) filled from an explicit catalog; four enabled recipes currently.
- Vertical traversal from reviewed stair contracts, forged contracts, online synthesis, stairwell towers, and bridges — in that fallback order.
- One planned vista with a reserved sight corridor, plus 1–4 external connector
  promontories. Each external promontory is a straight eight-cell (32u) run,
  with at most one per cardinal direction. Its first added cell crosses the
  core dungeon's global outer face; exterior-connected concavities do not count.
- Everything rises from a shared abyss datum.

## Where the work stands

All previously planned phases are closed and their evidence machinery has been
removed. The per-phase log lives in
[`docs/archive/2026-07-dungeon-phase-log/`](../archive/2026-07-dungeon-phase-log/)
if you ever need to reconstruct why a decision was made. Treat it as history,
not as current constraints.

### Landed 2026-07-27 — density scale, phase 2 (M1), partial

Two of phase 2's three items. Design flux, so no hash gate: hard validity every
build, and the numbers below are the evidence.

- **The measured transition reservation replaces the room-size axis cap.**
  `BaselineRoomSizeRangeForRole` capped a room to a fixed 4–5 cells on any axis
  carrying a Stair/Stairwell/Bridge edge, to leave room for a stair chosen later.
  `DungeonLabGenerator.TransitionReservation.cs` now measures that requirement
  from the shipped contract set — shortest compatible run plus a landing at each
  end — and caps the room against the actual lane distance instead, splitting
  the reservation evenly between the two endpoint rooms so it is independent of
  inflation order. Contract *data* is read early; stair *selection* still happens
  where it always did.
- **The vista lane is reserved before inflation, not after it.** It used to be
  derived from the two rooms' faces after they had grown, which worked only
  because inflation happened to leave void lying around. The lane is now claimed
  from node centres first: rooms outside the vista pair may not enter it at all,
  and the pair is capped against the lane's own required clear run.
  `TryReserveProcessionalVista` still derives the exact reservation — same rule,
  guaranteed rather than lucky. **No observable change at density 0**, which is
  the point: there is plenty of void there.

Measured over `2026072100..2026072299` at density 0:

| | before | after |
|---|---|---|
| accepted | 184/200 | **196/200** |
| `ROUTE_TRANSITION_RESERVATION` | 12 | **0** |
| `ROUTE_DENSITY_PRECONDITION` | 38 | 16 |
| `latticeEnvelopeFillPercent` p50 | 25% | 26% |
| EditMode `DungeonLab*` | 94 pass / 31 fail | **100 pass / 25 fail** |

The three known stairwell-retry seeds (`2026072219`, `2026072257`, `2026072135`)
no longer retry for a transition reservation at all. Six previously-red tests
went green, none went red — including both `atrium-ring` density tests, which is
the §3 prediction landing.

**Then the fill gate moved onto the new metric** (§3 + §5), which is the other
half of the same change: `denseFloorplanMinFillPercent` measured floor over the
FLOOR bounding box, so it rejected layouts for exactly the two things this work
wants — a promontory reaching outward, and rooms growing. It is now
`minLatticeEnvelopeFillPercent` over the lattice envelope, the box the embedder
itself measures against, carried onto `RouteTierRequirements` so both gate sites
(layout stage and post-loop) use the same denominator.

| | baseline | after M1 | after the gate swap |
|---|---|---|---|
| accepted | 184/200 | 196/200 | **199/200** |
| `ROUTE_TRANSITION_RESERVATION` | 12 | 0 | 0 |
| `ROUTE_DENSITY_PRECONDITION` | 38 | 16 | **0** |
| mean layout attempts | 1.13 | 1.08 | **1.01** |

The threshold is a flat **0.20**, two points under the observed density-0 minimum
of 22%, so it currently rejects nothing. That is deliberate: §3's finding is that
this gate can reject sparse output but cannot create floor, so it is a backstop
against a degenerate layout and `densityLevel` is the thing that makes a dungeon
dense. It needs retuning when phase 3 makes fill actually move.

**One seed still fails, and it is not the density work:** `2026072187` wants
exactly four external connectors on distinct cardinals and the grown core no
longer offers four anchors that fit. `TryResolveExternalConnectorPromontories`
is atomic on an exact per-seed count, which is pre-existing brittleness —
`2026072198` hits it too and recovers on a second layout attempt. Relaxing that
count is a change to a shipped contract ("1–4 external promontories"), so it was
left alone rather than folded into a density change.

**Still open in phase 2: the explicit stairwell shaft.** See "Next, in order".

### Measured 2026-07-27 — the corridor reroute spike passed (§3.1 candidate 3)

§9 named this the one unproven step in the whole density design: *"if a
laterally offset corridor does not survive `TryResolveConnectionTransition`,
fall back to doorway-or-straight-only and accept a higher inflation-retry rate at
densities 4–5."* It does survive. **The fallback is not needed.**

The spike forced EVERY generic corridor off its centre line — a Z path that jogs
laterally inside the source room, runs the axis, and jogs back inside the target
room, so both rooms still contain their node centre and only the derived
threshold moves. Recipe-port edges were excluded, because their approach depth is
authored and candidate 3 never proposed moving them. (Forcing those too fails
immediately with `RECIPE_PORT_APPROACH` — worth knowing, and not a finding about
candidate 3.) 200 seeds at density 0:

| offset | accepted | hardValid | routeRequirements | finalVistas | recipeSets |
|---|---|---|---|---|---|
| none (baseline) | 199/200 | 199 | 199 | 199 | 199 |
| **±1, every edge** | **199/200** | **199** | **199** | **199** | **199** |
| ±2, every edge | 186/200 | 186 | 186 | 186 | 186 |

At ±1 the corpus is indistinguishable from straight corridors — zero validation
failures, the transition contracts resolve, and the boundary builder derives the
right threshold. At ±2 the only new code is `ROUTE_CORRIDOR_EMBEDDING_EXHAUSTED`
(64×): the offset path *collides* with other geometry, which is a fit problem,
not a rejection by the transition or boundary machinery — every seed that fits is
fully hard-valid.

And this was the worst case by construction: as the ladder's third rung it is
only reached when the straight path already failed, on a small minority of edges,
so its real fit rate will be far better than the ±2 row suggests.

The spike code was reverted — it was a spike. The path shape it proved: from the
source centre, step `k` cells laterally; run the route axis until aligned with
the target centre; step `k` cells back. All unit steps, so
`ValidatePathCardinality` holds.

### Landed 2026-07-27 — density scale, phases 0 and 1

Phases 0 and 1 of
[`density-scale-design-2026-07-27.md`](density-scale-design-2026-07-27.md). Both
are geometry-neutral by design and both were gated on it.

- **The A/B tool was redefined first (§7.1).** `ops/dungeon-port-ab.sh` compared
  `resultHash`, which is SHA-256 over the whole seed-report array — and every
  seed report embeds the reflected `settings` struct. Adding a report field or a
  settings field moved it with no geometry change, so a whole-report identity
  gate was unachievable by construction for exactly the two phases that needed
  one. It now compares the per-seed **`hashes.canonical`** vector plus `accepted`
  plus the failure codes, and narrows a mismatch to `routeIntent` / `layout` /
  `tieredLevelPlan`. `resultHash` stays as a cheap change-detector and is not a
  gate. Both phases then passed that gate on all 200 seeds while `resultHash`
  moved — which is the defect demonstrating itself.
- **The density metric replaced the fill metric.** `latticeEnvelopeFillPercent`
  (floor over the lattice envelope — the box the embedder measures against, so a
  promontory reaching outward no longer lowers the score) and `voidComponents`
  (4-connected non-floor components inside that envelope, minus authored void,
  as a histogram with a max). Both per seed under `measurements.density`, rolled
  up per topology and per corpus in the batch report.
  `DungeonLabGenerator.DensityMetrics.cs` owns them.
- **The floorplan is readable without Unity.** An ASCII projection per seed in
  the batch report, and a square top-down orthographic PNG per visual sentinel
  alongside the existing three-quarter shot. The previous density attempt's
  failure mode was optimising a scalar nobody could see.
- **Baseline written** to `DungeonLabReports/void_baseline_2026-07-27.md` and the
  two 200-seed reports beside it. Density 0 today: **25% fill p50, max void
  component 749 cells p50**, 184/200 accepted.
- **The dial replaced the flag.** `densityLevel` 0–5 on the one profile asset,
  **Arena > Dungeons > Density > 0..5**, `ARENA_DUNGEON_DENSITY`.
  `generation_profile_dense.asset`, the Spacious/Dense menu pair and
  `ARENA_DUNGEON_GENERATION_PROFILE` are gone.
- **Topology spatial overrides are pitch-relative** (`columnGapDeltaCells`,
  `rowGapDeltaCells`, `roomSizeDeltaCells`; `latticeSlackMaxCells` now clamps the
  profile's value rather than replacing it). The retired absolute names are
  rejected by the loader with a migration message rather than reinterpreted,
  because a bare number is legal in both vocabularies and means something
  different in each. `Validate Topologies` runs every topology at all six
  densities.
- **What phase 1 deliberately did NOT do:** move geometry. Every level resolves
  through `DungeonGenerationProfile.ResolveDensitySpatialSettings`, which is
  currently the identity — so **levels 1–5 produce density 0's geometry today**
  and log a warning saying so. Pitch, room size, slack, envelope radius and
  enclosure chance become functions of the dial in phase 3 (M2); that method is
  the single seam to edit.

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
| Always 13 rooms *(closed by step 3)* | `rooms` min 13, p50 13, max 13 | All three route graphs happened to have 13 nodes. Step 2 removed the 13-node *lock* (the range is 9–20); step 3 added graphs that differ, and the same 200 seeds now measure 12/13/14/16 rooms |
| Identical room shapes | — | The same commit cut `DungeonGenerationProfile` 68 -> 24 settings, deleting the room size-class vocabulary (`largeRoom/midRoom/smallRoom` area ranges *and* counts, `nonRectChanceGrand`, `nonRectChanceMid`, `wingMinDimCells`, `wingMaxDepthCells`, `roomMaxSideCells`, `roomMaxAspectRatio`, `floorBudgetCells`) |

The archived plan listed "multiple elevation archetypes" under *pieces worth
preserving*, so this was preservation that did not happen, not a considered trade.

**Item 1 — room size ranges (done, measured 2026-07-25, and it cost a seed).**
Both profiles widened. `dense` was pinned at exactly 7x7 for every role because 7
is its vertical ceiling; it now spreads 6-8 x 5-7. `spacious` now spreads
4-7 x 4-8.

Measured over `2026072100..2026072299` at `dense`: **198/200 accepted**, down from
199/200 before the widening. The new failure is **2026072246**, a
`processional-spine` seed whose `branch-2-9` Bridge cannot reserve its 8u rise —
the same void-starvation failure mode as 2026072295's stairwell, and exactly what
larger rooms cause. Consistent with Finding A: only `processional-spine` reads the
widened profile, and only a processional seed regressed. `atrium-ring` is 50/50
with zero retries because it still uses `BaselinePatternSpatialSettings`.

**Resolved by step 2, 2026-07-25.** Closing Finding A gave `atrium-ring` the
widened profile sizes too, and `2026072246` generates again — so the widened
`dense` range is kept as authored and no narrowing was needed. `twin-wing-keep`
is the exception: seven lattice columns at a 9-cell pitch do not fit the 52-cell
envelope, so its lanes stay tight and it declares its own narrower room sizes as
a per-topology override. That override is visible in its topology file, with the
reason, rather than hidden in a settings table.

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
*(Step 2's rubber-sheet lattice is the fix: per-lane gaps drawn per seed, so a
topology now has hundreds of lattices x 8 orientations rather than 8 in total.)*

`SelectRoutePattern` is `seed % 4` mapped to 3 patterns, so it also needs
redesigning to scale past four. *(Done in step 2: a weighted draw over the
registry, keyed on the seed alone.)*

**Answered 2026-07-25 — the authoring model is agreed:**
[`route-topology-authoring-2026-07-25.md`](route-topology-authoring-2026-07-25.md).
JSON topology files (ASCII lattice map + node/edge/slot tables), derived graph
metrics, a rubber-sheet lattice, weighted selection, and four hand-verified new
topologies (descent 13, basin 14, terraces 16, ridge/ravine 12 rooms). All three
forks ruled by the owner in its §8.

**Step 1 landed 2026-07-25 — data cutover, output-neutral.** The three existing
topologies are now files under
`Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`; see
[`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md) for the format and
the rule list. Deleted: the three `Build*RouteIntent` factories, the three
`TryEmbed*Route` embedders, `TryFindBoundedCoarsePath`, `RouteGraphComposer`, the
switch arms in `TryEmbedRoute`/`ResolvePatternSpatialSettings`/`SelectRoutePattern`,
the `Recipes.cs` pattern ternaries, and every self-declared graph metric
(`requiredCycleRank`, `requiredCycleCoreNodeCount`, `requiredJunctionDegree`,
`branchAttach`/`RejoinNode`, per-edge `requiredRiseLevels`, edge ids) — all now
derived. New: **Tools > Dungeon Lab > Validate Topologies**, which checks a draft
against the whole rule list, redraws its map with edges, and computes the vista
lane's clear-cell count.

The win is per topology, not in total lines. Adding a topology cost 180–230 lines
of hand-written C#; it now costs one data file of ~55 significant lines. Paying for
that: 1,285 lines of generator C# deleted, 350 added back as the generic builder
and embedder, 1,070 added as the loader, 1,080 added as the validator — so the tree
is ~1,220 lines of editor C# heavier. Roughly 1,080 of that is the validator, which
is new capability rather than a port; the port itself is about line-neutral and
buys the per-topology cost.

Step 1 was verified output-neutral **before** the batch run: node centres are
byte-identical to the old embedders across all 8 orientations and both profiles,
and the ported graph tables match the old C# literals field for field. The
processional's four BFS-placed branch nodes are the constant `(2,1) (1,1) (0,1)
(0,2)` at 7 search expansions, confirmed by running the pre-port BFS rather than
by assumption.

**Gate: PASSED, verified 2026-07-25.** `ops/dungeon-port-ab.sh` ran the 200-seed
`dense` batch twice on the same tree — once with the port stashed, once with it
restored — and both legs produced the byte-identical
`resultHash e3fb0480892978f107b31b50b0535e8feccb1f8ee83438e8f689dd3350143db2`,
198/200, with the same two failed seeds. Each leg was proved to be the leg it
claimed: the pre-port log mentions neither `Topologies/` nor
`DungeonRouteTopology`, and the post-port assembly
(`Library/ScriptAssemblies/Assembly-CSharp-Editor.dll`, rebuilt 31s before its
report) contains `DungeonRouteTopology` / `RouteTopologyNode` /
`BuildTopologyRouteIntent` and no longer contains `RouteGraphComposer`.

The `3092863a…` hash the plan named cannot be reproduced by any run after commit
`3123a06d` — see the box further down. Both failures are accounted for: 2026072295
is the known stairwell defect, 2026072246 is item 1's widening.

**Step 2 landed 2026-07-25 — the deliberate rebaseline. Every seed moved once.**

Shipped in one commit: weighted selection over the registry, the rubber-sheet
lattice, per-topology spatial overrides (which close Finding A by deleting the
second settings table), the ±4/±8 rise sign, a node-count range in place of the
13-node lock, a role→size-class map in the profile, and the deletion of the
step 1 `legacy` blocks and pinned edge ids. The full deviation list is in the
commit message; the format is in
[`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md).

**Gate: PASSED.** `ops/dungeon-step2-verify.sh` ran the 200-seed `dense` batch
twice on the same tree — two independent runs, byte-identical `resultHash`
`09b04b4e32aa4e74c3cf6cebcded94582b9394dee5e918749f7d7e2095795856`, **199/200
accepted**, `hardValid 199/199`, `validationFailureCodes none`. The one failure
is `2026072228`, a `twin-wing-keep` seed whose `E-F` Stairwell cannot reserve its
8u rise: the same structural defect as the old `2026072295`, at a different seed
because the topology mix moved. Item 1's casualty `2026072246` and the old
`2026072295` both generate again.

That hash is a **transient comparison value, not a lock.** Do not assert it in a
test; the very next content edit moves it.

```text
                        before (step 1)   after (step 2)
accepted                198/200           199/200
topology mix            100/50/50         70/55/75   (processional/atrium/twin-wing)
                        seed % 4          weighted draw, equal weights
room counts             13 x 200          13 x 200   (the three graphs still have 13 nodes)
layoutAttempts          mean 1.07 max 2   mean 1.03 max 2
lattice slack spent     n/a               16 processional, 15 atrium, 8 twin-wing (cells/plan)
floor fill, median      42.1%             30.7%
floor fill, minimum     33.3%             27.1%   against a 26% floor
```

**The measured cost of the rubber sheet is floor fill, exactly as §4 predicted.**
Wider lane gaps grow the floor bounding box without growing rooms, so fill fell
~11 points and the margin over `denseFloorplanMinFillPercent` (0.26) fell from
7.3 points to 1.1. Nothing was rejected for density — `rejectionCodes` has no
`ROUTE_DENSITY_PRECONDITION` at all — but that margin is thin, so the cap was
measured rather than guessed:

| `latticeSlackMaxCells` | accepted | floor fill min / median | `ROUTE_DENSITY_PRECONDITION` |
| --- | --- | --- | --- |
| 8 (shipped) | 199/200 | 27.1% / 30.7% | 0 |
| 14 | 154/200 | 26.0% / 27.4% | 117 |

That is the whole trade: the cap is a per-profile knob rather than a lowered fill
floor, and 8 is most of the room the floor allows. **Step 3 must re-measure
fill** — a sparser new topology is the thing most likely to cross it.

Also verified rather than assumed: **Tools > Dungeon Lab > Validate Topologies**
passes all three at both profiles (report in
`DungeonLabReports/route_topology_validation.txt`), and six seeds covering all
three topologies rebuild end to end — plan, renderer, collision export — into a
throwaway scene.

**Looked at, 2026-07-25: too much void between tiers.** Owner verdict on the
rebaselined output. Not "the corridors are long" — the complaint is the empty
space between platforms at different elevations.

The measurement behind it is an axis ratio, and none of its terms is topology
data:

| | constant | value |
| --- | --- | --- |
| horizontal | `CellSize` | 4 units per grid cell |
| vertical | `StairForge.LevelHeight` | **1 unit per level** |
| rise per edge | `MajorRiseLevels` / `DoubleMajorRiseLevels` | 4 or 8 levels, so 4 or 8 units |
| total climb | `MaxGeneratedLevel` | 24 units |
| abyss skirt | `AbyssDepthLevels` | 20 units below the lowest floor |

A tier change is 4–8 units of height across a lane gap of 36–52 units. Two
adjacent platforms that used to have roughly 8 units of void between them now
have up to ~24, while the drop between them is unchanged — and that gap is open
air down to the shared abyss base, not floor. The rubber sheet widened the
horizontal term only, so it made the ratio worse; the fill drop from 42% to 31%
is the same fact counted differently.

**Deliberately not addressed here — owner ruling: it is part of a larger
problem.** Two things follow:

- **Do not spot-fix it by lowering `latticeSlackMaxCells`.** That knob only
  trades variety against density along the horizontal axis; it cannot change the
  ratio, and turning it down just gives back step 2's variety.
- **Step 3 neither fixes it nor is blocked by it.** New topologies redistribute
  elevation — `terraced-cascade` steps 4u across eleven nodes, `descent-shaft`
  runs 24 -> 0 — but every graph still lands 4–8 unit rises across 36+ unit gaps,
  because the ratio lives in the constants above, not in a topology file.

**Step 3 landed 2026-07-25 — the four drafted topologies. Every seed moves again.**

Seven topologies now, drawn with equal weight. Four new files, no generator C#;
the only code-side change is three new role strings in both profile assets'
`roleSizeClasses`.

| topology | rooms | lattice | shape |
| --- | --- | --- | --- |
| `descent-shaft` | 13 | 5×5 | arrive on a rim, turn down a shaft, end in a flooded vault at the abyss datum — levels run **24 → 0** |
| `sunken-basin` | 14 | 5×4 | two rims at 24, the island shrine at 0 on the basin floor, a bridge across the north lip, two loops |
| `terraced-cascade` | 16 | 5×5 | eleven main-route nodes stepping 4u across a terrace field, plus a cascade spur that falls back to the arrival |
| `ridge-ravine` | 12 | 5×4 | a ridge climbing to 24 over a ravine floor at 0; the overlook is a deliberate degree-1 dead end |

**Gate: PASSED.** `ops/dungeon-step2-verify.sh dense` (it is generic) ran the
200-seed batch twice on the same tree: byte-identical `resultHash`
`eb3ffd0c0df09586a2f50c0f20dab9ad7d652433f7c47b9dd9c786e126e78af8`, **200/200
accepted**, `hardValid 200/200`, `validationFailureCodes none`. That hash is a
**transient comparison value, not a lock** — do not assert it in a test.
`Tools > Dungeon Lab > Validate Topologies` passes all seven at both profiles.

```text
                        step 2            step 3
accepted (dense)        199/200           200/200
topologies              3                 7
room counts             13 × 200          12 × 33, 13 × 106, 14 × 33, 16 × 28
floor fill, median      30.7%             30.6%
floor fill, minimum     27.1%             27.0%   against a 26% floor
layoutAttempts          mean 1.03 max 2   mean 1.015 max 2
```

Per topology, `dense`, floor fill min / median: `processional-spine` 30.1/31.4 ·
`sunken-basin` 29.5/32.5 · `terraced-cascade` 29.6/31.8 · `atrium-ring`
28.4/30.1 · `descent-shaft` 27.0/29.3 · `ridge-ravine` 27.1/28.3 ·
`twin-wing-keep` 27.1/30.3.

Four things worth carrying forward:

- **200/200 does not mean the stairwell defect is fixed.** Step 2's failure
  `2026072228` now draws `terraced-cascade` instead of `twin-wing-keep`, so no
  seed in this window lands on the broken combination. The defect is still
  visible as recovered `ROUTE_TRANSITION_RESERVATION` retries on `2026072219`
  and `2026072257` (both twin-wing) and `2026072135` (descent-shaft).
- **The rubber sheet always spends its whole budget**, so a topology's floor
  bounding box — and therefore its floor fill — is effectively a constant, and a
  topology that misses the 26% floor misses it on *every* seed rather than a few.
  Two of the four needed their lane minimums authored one cell under the profile
  pitch, and two needed a `roomSizes` or `latticeSlackMaxCells` override, all
  measured rather than guessed. Details and the reason live in each file.
- **The `±4/±8` rise sign is now exercised**: 20 descending edges across the four
  new graphs, on 119 of 200 seeds. `descent-shaft` descends for eight of its
  thirteen edges.
- **Recipe port geometry is an authoring rule that nothing checks.** The
  compression slot's node must be straight through, the landmark's must be
  straight through *and* perpendicular to the vista, and the return's must be a
  corner — see the new section in
  [`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md). Two of the four
  §6 drafts broke this and were redrawn. Teaching `Validate Topologies` to check
  it is the first follow-up.

**Spacious got better, and what is left there is not new.** Measured over the
same 200 seeds at `spacious`: **184/200**, and every one of the 16 failures is
`atrium-ring` (3/19 accepted) rejected for `ROUTE_DENSITY_PRECONDITION`. The four
new topologies accept all 119 of their spacious seeds. For comparison, `main`
before step 3 accepted 40/50 spacious seeds with `atrium-ring` at 6/16 — so
`atrium-ring`'s spacious density failure is a **pre-existing step 2 defect**, not
something step 3 introduced. The same one-line fix the new topologies use would
very likely close it (`"columnGapCells": { "min": 8, … }`, `rowGapCells` the
same), but that changes a shipped topology's dense output too, so it is left for
an owner call rather than folded in here.

**The editor suite came out one test better than it went in**: `main` runs the
`DungeonLab*` filter at 78 passed / 29 failed, this tree at 79 / 28. Two tests
were updated because step 3 invalidated what they pinned — the registry weight
list in `Selector_DrawsEveryTopologyByWeightRatherThanBySeedResidue`, and an
absolute `baseLevel` in `TwinStairs_Landings…`, whose seed now draws
`sunken-basin` and so hands the episode a landmark at level 0 (that assertion now
pins the *coupling*, `elevatedLevel == baseLevel + 1`, instead of the absolute).
The one test that is red here and green on `main`,
`HallwayEndRegression_RendererAndCollisionConsumeOnlyTheValidatedPlan`, fails on
`collision.missingMeshes=2` with the plan and renderer both passing — the same
signature nine tests already carry on `main`, on a seed that moved into it while
two others moved out.

The question below is settled in favour of authoring-as-data; kept for the
reasoning: **author more diagrams as data, or replace the hand-drawn diagram with
a general graph embedder?** Authoring keeps
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

### Next, in order

0. **The density scale — phases 0 and 1 landed 2026-07-27, phase 2 is next.**
   Design in
   [`density-scale-design-2026-07-27.md`](density-scale-design-2026-07-27.md).
   Items 3, 4 and 5 below are folded into it (the stairwell shaft becomes an
   explicit reservation in phase 2, `atrium-ring`'s density failure dies with the
   bounding-box fill gate, and the tier void is what the whole dial exists to
   remove). Phases 0, 1 and two thirds of phase 2 have landed — see the blocks
   below. What is left, in order:

   a. **The explicit stairwell shaft** (the rest of phase 2). A stairwell tower
      stands on void beside the path, and that requirement is still implicit:
      the placer searches for void at tier time rather than the plan reserving
      it. M1 dropped the symptom to zero (no `ROUTE_TRANSITION_RESERVATION`
      retries in 200 seeds), so this is about surviving packing, not a live
      defect.

      **The obvious implementation was tried on 2026-07-27 and measured as
      wrong; do not repeat it.** Reserving a fixed window — the widest measured
      footprint (2 cells along the path × 3 lateral, from 254 towers: 2×2:129,
      2×3:54, 3×2:43, 1×2:12, 2×1:16) at the lane midpoint, on a side drawn per
      edge — and closing it to rooms and corridors took density 0 from
      **199/200 to 138/200**: 53 `ROUTE_ROOM_INFLATION_EXHAUSTED` and 82
      `ROUTE_CORRIDOR_EMBEDDING_EXHAUSTED`. The two causes are structural, not
      tuning:

      - **Authored recipe footprints cannot move.** `keep-landmark` and
        `hanging-shrine` overlapped the reserved window on 27 seeds, and a
        recipe room has no re-roll (design §9 residual risk 2).
      - **Other edges' corridors legitimately cross it.** `E-F` and `B-C` paths
        ran through shafts reserved beside `F-G`.

      The lesson is about ordering: a shaft location cannot be chosen before the
      things it competes with exist. The sound design is the opposite ordering —
      choose the shaft **after** rooms and corridors are compiled, from the
      sides and positions that are actually free, and protect that choice
      through the tier stage. Note also that the reservation does not need to
      dictate where the tower goes: `AddValidStairwellTransitionCandidates`
      searching every position and both sides is fine, because all the
      reservation has to guarantee is that its candidate list is not empty.
   b. **Phase 3 (M2 — pack).** The corridor reroute spike (§3.1 candidate 3,
      and §9's only unproven step) was **run on 2026-07-27 and it passed** — see
      below — so the fallback §9 reserved is not needed. What is left is the
      corridor-ownership rewrite (`claimedCorridorCells` + the per-edge candidate
      ladder replacing the attempt-abort), then pitch / room size / slack /
      envelope radius / enclosure as functions of the dial in
      `DungeonGenerationProfile.ResolveDensitySpatialSettings`.
   c. **Retune `minLatticeEnvelopeFillPercent` when the dial moves.** It is a
      flat 0.20 backstop today, two points under the observed density-0 minimum,
      so it rejects nothing. §4.3 targets 28% at density 0 rising to 95% at
      density 5, so once phase 3 makes fill move this should become
      density-relative or it stops meaning anything.
1. **Look at the dungeons.** **Arena > Dungeons > Rebuild Random Dungeon** on a few
   seeds. No hash tells you whether a dungeon reads well. One rendered shot per
   new topology is in `DungeonLabReports/step3_topology_shots/` (dense, seeds
   2026072104 / 2026072100 / 2026072101 / 2026072105); the four also rebuild end
   to end — plan, renderer, collision export, scene save — into throwaway scenes.
2. **Teach `Validate Topologies` the slot-geometry rule.** It is the one
   authoring rule with real teeth that nothing checks, and it cost two of the
   four step 3 drafts a redraw. See "Slot geometry" in
   [`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md).
3. **The stairwell reservation defect is still open**, and step 3's 200/200 hides
   it: a stairwell tower needs void cells beside its corridor and `dense` leaves
   fewer, so an interior stairwell in a dense cluster is structurally
   impossible. It now shows up as recovered retries (`2026072219`, `2026072257`,
   `2026072135`) rather than as a failed seed.
4. **`atrium-ring` fails 16 of its 19 spacious seeds for density** — pre-existing,
   see the step 3 block above for the one-line fix and why it was not folded in.
5. **The tier-void ratio** the owner called out is still untouched and still its
   own slice. New topologies redistribute elevation; they cannot change 4u of
   rise across a 36u+ gap.
6. **Remaining review items**, none urgent: unify the two floor representations
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

> **`3092863a…` is dead. Do not use it as a gate — it belongs to commit `90feceb3`
> only.** The very next commit, `3123a06d` (widen room sizes), edited
> `generation_profile_dense.asset`, and `ActiveContentDigest()` hashes that file's
> bytes into every seed report's `catalogDigest`. Recomputing that digest from git
> proves it: `8c0f30b2` at `90feceb3`, `3389fba8` at `3123a06d` and every commit
> since. So from `3123a06d` onward no run could reproduce `3092863a`, whatever the
> generator did.
>
> This is the trap in comparing against a *recorded* hash: unrelated content drift
> is indistinguishable from a regression. Compare against **the current commit**
> instead — `ops/dungeon-port-ab.sh` runs the same batch with the working tree
> stashed and restored and diffs the two reports seed by seed. Current value, for
> reference only: `e3fb0480…` (catalog `3389fba8`, profile `dense`, 198/200).

### `TryBuildCellLevelField` split — landed 2026-07-25 (review §12, 1.2)

660 -> 268 lines of orchestration. Two blocks became named steps:

- `TryResolveConnectionTransition` — the 385-line per-connection body: level the
  corridor, then reserve its transition via reviewed stair contract, then online
  synthesis, then a stairwell tower.
- `AddZoneSeamStepStrips` — the zone-seam rise-1 strips.

Both were verified statement-for-statement identical to the code they replaced
before compiling, so this is an identity-preserving refactor.

**Verification: this was gated on `3092863a…`, which is now unreachable — see the
box above.** The split was verified statement-for-statement against the code it
replaced, so treat it as confirmed; re-check identity-preserving work with
`ops/dungeon-port-ab.sh`, which compares against the current commit instead of a
recorded value.

Also worth doing at some point: **Arena > Dungeons > Rebuild Random Dungeon** and
look at the result. No hash can tell you whether the dungeon still reads well.

## Read next

1. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules. Read before changing generator, measurement, contract, or placement code.
2. [`GLOSSARY.md`](GLOSSARY.md) — authoritative vocabulary (role vs. beat, room vs. recipe, zone, port, transition, reservation).
3. [`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md) — current system model and recommended work.
4. [`ROOM_AUTHORING_GUIDE_CURRENT.md`](ROOM_AUTHORING_GUIDE_CURRENT.md) and [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — adding content.
5. [`stair_forge_design.md`](stair_forge_design.md) — vertical traversal decision history.

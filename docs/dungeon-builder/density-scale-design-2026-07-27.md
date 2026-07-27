# Density scale design — a 0–5 dial replacing the spacious/dense flag

Status: **finalized**, 2026-07-27. Forks resolved in §8 under the owner's
"defer to your judgement" ruling of 2026-07-27. Revised the same day after
review: retry transaction named (§3.2), M4's boundary-room state corrected
(§4.2), the A/B gate redefined (§7.1), the enclosure clamp accounted for (§4.3).

**Phases 0 and 1 landed 2026-07-27**, both gated on the redefined §7.1 A/B.
**Phase 2 is two thirds landed** the same day: the measured transition
reservation and the vista ordering fix are in (density 0 went 184/200 to 196/200
accepted, with `ROUTE_TRANSITION_RESERVATION` at zero); the explicit stairwell
shaft is still open and is specified in `CURRENT_STATUS.md`. Live status: `CURRENT_STATUS.md`; baseline numbers:
`DungeonLabReports/void_baseline_2026-07-27.md`. Note that the dial is wired but
inert — `DungeonGenerationProfile.ResolveDensitySpatialSettings` is the identity
until phase 3 (M2) edits it, so levels 1-5 currently produce density 0.
Supersedes: `docs/archive/2026-07-dungeon-phase-log/DENSITY_ADJACENCY_PLAN.md` (closed 2026-07-23)

## 1. What this delivers

- A **0–5 density dial** chosen at generation time; `spacious`/`dense` deleted.
- **0** = today's look, large voids and all. Sparse stays a first-class setting,
  not a legacy mode.
- **5** = voids minimal — max void component ≤4 cells, at most two components
  larger than one cell.
- Tiers, stairwells, external promontories and **all seven route topologies**
  survive at every setting.

## 2. What the void actually is (measured)

Measured over `DungeonLabReports/dungeon_plan_2026072100_2026072299.json`
(200 accepted seeds, profile `dense`, all seven topologies):

| | median |
|---|---|
| floor cells per dungeon | **537** |
| `floorFillPercent` (floor ÷ floor bounding box) | **30.6%** |
| floor ÷ union of the 9x9 room envelopes | **52%** |
| floor ÷ lattice bounding box | **27.5%** |

Two roughly equal halves, and this is the finding that drives the design:

- **~48% of the space inside occupied lattice cells is empty.** Rooms are ~6x6
  in a 9x9 envelope at a 9–11 cell lane pitch, so every room is ringed by a 2–5
  cell channel of open air.
- **~45% of the lattice bounding box is not inside any envelope** — partly
  rubber-sheet slack, mostly **vacant lattice cells**, the `.` tokens in the
  topology maps:

  | topology | lattice | nodes | vacant |
  |---|---|---|---|
  | `processional-spine` | 4x4 | 13 | 3 (19%) |
  | `sunken-basin` | 5x4 | 14 | 6 (30%) |
  | `atrium-ring` | 5x4 | 13 | 7 (35%) |
  | `terraced-cascade` | 5x5 | 16 | 9 (36%) |
  | `twin-wing-keep` | 7x3 | 13 | 8 (38%) |
  | `ridge-ravine` | 5x4 | 12 | 8 (40%) |
  | `descent-shaft` | 5x5 | 13 | 12 (48%) |

  A vacant lattice cell is a ~9x9 hole — 36x the stated tolerance.

Closing the channels alone tops out near 55–65% fill and leaves a field of 9x9
craters. **Both halves must be attacked.**

## 3. Rules and invariants audit

The owner's question was whether stale rules are making this harder. They are —
but fewer than expected, and the two most damaging ones are not the one that was
suspected. Every rule that touches density, and its verdict:

| Rule | Site | What it actually protects | Verdict |
|---|---|---|---|
| **`BaselineRoomSizeRangeForRole` axis cap** | `RouteFirstPilot.cs:1678-1701` | leaves room for a stair whose prefab is chosen later | **STALE — the single biggest brake.** Clamps a room to 4–5 cells on any axis carrying a Stair/Stairwell/Bridge edge. Actual reviewed footprints: `rise=4 run=1 → 1 cell`, `rise=8 run=2 → 2 cells`. It gives up 4–5 cells to reserve 2, on 9 of 13 processional edges. **Replace** with a reservation measured from the static contract set. |
| **`denseFloorplanMinFillPercent` over the floor bbox** | `cs:1253`, `RouteFirstPilot.cs:468` | rejects sparse layouts | **STALE METRIC.** The bounding box grows when a promontory reaches outward, so the gate penalizes a feature we want. It is also a gate, not a driver — it can reject sparse output but cannot create floor. **Replace** with §5. |
| **Vista reserved *after* room inflation** | `RouteFirstPilot.cs:387` then `:400` | 3 clear void cells for the planned sightline | **STALE ORDERING.** Works only because inflation happens to leave void lying around. At any real density there is none. **Reserve the lane first**, from node centres, then inflate around it. |
| **`PathTouchesExistingFloorOutsideEndpointRooms`** | `RouteFirstPilot.cs:2011`, `cs:1534` | *(narrower than it looks — see below)* | **NOT STALE, WRONG SHAPE.** Keep the invariant, replace the mechanism. |
| **`PathCrossesThirdRoom`** | `cs:1555` | a corridor must not punch through an unrelated room, creating an undeclared doorway and an unowned threshold | **KEEP.** True at every density. Downgrade from attempt-abort to reroute reason. |
| **`ValidatePathCardinality`** | `cs:3897` | path steps are 4-neighbours | **KEEP.** Cheap and correct. |
| **Edge endpoints must be cardinally aligned** | `RouteFirstPilot.cs:1975` | the lattice contract | **KEEP.** The archived slice-2 spike proved alignment is the *first* thing that breaks when anchors move; not worth reopening. |
| **`RoomFitsEnvelope` / `roomEnvelopeRadiusCells = 4`** | `RouteFirstPilot.cs:1353` | rooms stay inside their lattice cell | **KEEP the rule, make the radius density-driven.** This is the packer's main lever. |
| **`EnclosedRoomChance = 0.5`** | `cs:110` | some rooms walled, some open | **PARAMETERIZE.** `IsPartitionWallEdge` returns false when neither room is enclosed, so at high density two same-level abutting rooms would merge into one open field. Reaches 1.0 at density 5. |
| **Stairwell towers stand on void beside the path** | `cs:4792-4795` | the tower's folded footprint | **REAL REQUIREMENT, CURRENTLY IMPLICIT** — and already the open defect at today's `dense` (recovered `ROUTE_TRANSITION_RESERVATION` retries on `2026072219`, `2026072257`, `2026072135`). Must become an explicit shaft reservation taken from the route intent's declared `Stairwell` edge kind. |
| **The 12 hard checks** | `Validation.cs:131-222` | connectivity, vertical traversal, transition contracts, headroom, renderer inputs | **ALL KEEP.** Not one is a density constraint. |

### 3.1 The corridor rule, specifically

`PathTouchesExistingFloorOutsideEndpointRooms` rejects a corridor if any cell is
already floor and outside both endpoint rooms. At its call site, third-room floor
is already caught by `PathCrossesThirdRoom` and endpoint rooms are excluded — so
the unique work it does is **reject overlap with previously placed corridors**.

That underlying invariant is real and survives at every density:

> A corridor cell is owned by exactly one connection. Two connections sharing a
> cell would both try to level it, and one of them climbs.

What is wrong is the mechanism, in two ways:

1. **No reroute granularity.** One grazing corridor aborts the entire layout
   attempt. At density 0 that is rare enough not to matter; from density 2 it is
   the dominant failure mode, because short corridors in a packed lattice collide
   constantly.
2. **It enforces ownership by rejection instead of by construction.** The path is
   generated once, centre-to-centre, and either fits or kills the attempt.

Replacement — **corridor reservation with per-edge reroute.** A `claimedCorridorCells`
set, distinct from `floorCells`, and a deterministic candidate ladder per edge:

1. **Abutting rooms → a doorway, not a corridor.** Zero exterior cells; the
   connection is the two facing cells. Already proven end to end by the archived
   slice 2 (twin-wing seed `2026072103`: boundary builder, renderer and collision
   export all clean, one doorway emitted).
2. The straight centre-to-centre path (today's behaviour, tried first).
3. The same path offset laterally ±1, ±2 within the edge's reserved corridor
   band. Both rooms still contain their node centre, so the slice-2 anchor rule
   holds; only the derived threshold moves, and thresholds are already derived
   (`ThresholdCell`), not stored.
4. Exhausted → the whole **route-shape attempt** re-rolls (§3.2).

Candidate 3 was the one piece needing a spike before it could be trusted: does
`TryResolveConnectionTransition` accept a laterally offset corridor, and does the
boundary builder derive the right threshold? **Both yes, measured 2026-07-27** —
see §9.1 and `CURRENT_STATUS.md`. Recipe-port edges are excluded from the rung,
because their approach depth is authored.

### 3.2 The retry transaction

There is no per-edge retry boundary today, and inventing one would be wrong.
`TryInflateProcessionalRooms` (`RouteFirstPilot.cs:387`) completes **before**
vista reservation (`:400`), promontory planning (`:415`), recipe placement
(`:432`) and corridor construction (`:446`). Re-rolling one endpoint's inflation
after a corridor failure would invalidate the reserved vista lane, the recipe
port placements, and every corridor an earlier edge already claimed. Its only
internal retry is per-node shape re-rolls *within* an already-placed set, which
cannot help an edge that fails later.

So the design names a real transaction: the **route-shape attempt** — inflation,
vista, promontory, recipes and corridors retried **as one unit**, bounded, keyed
by attempt index.

- Every one of those five stages is already a pure function of
  `(intent, spatial, nodeCenters)` plus its own RNG stream, so re-running the
  suffix is a re-call, not a rollback. Nothing outside it is mutated: the layout
  is only published at `RouteFirstPilot.cs:499`.
- The RNG pattern already exists — `DungeonRandomScope` keys streams by
  `(seed, layout attempt, tier attempt, purpose, subject)`. The shape attempt
  index joins that key exactly as the tier attempt did, so attempt *k* cannot be
  perturbed by how many attempts preceded it.
- It nests cleanly: the corridor candidate ladder (§3.1) is the inner retry, the
  route-shape attempt is the middle one, and today's layout attempt — which
  re-runs from embedding with a fresh orientation, max 2 — stays the outer one.
- **Size the ceiling from measurement, not a guess**, per the habit that took
  `TierPlacementAttempts` from 32 to 4. Start at 4, record the observed maximum
  in phase 3, and set it to twice that.

The two rejected alternatives, for the record. *Keep rooms fixed and retry paths
only* is what §3.1 alone gives; it is strictly weaker, because at densities 4–5
the thing that has to move is often the room, not the path. *Move corridor
feasibility into inflation* is circular — corridor feasibility depends on recipe
port placement and the vista reservation, both of which run after inflation, so
it would mean hoisting those too.

## 4. Design

### 4.1 The principle

> Density removes **incidental** void. **Authored** void survives at every
> setting: the vista sight lane, bridge spans, stairwell shafts, and everything
> outside the dungeon's mass — including the external promontories, which reach
> beyond it by construction.

Tiers survive for free: two rooms at different levels that abut produce a
retaining wall, not a hole. `AddPlannedOverlookAppendages` already does exactly
this in production today, which is the existence proof that the boundary,
renderer, abyss and collision paths digest abutment across an elevation seam.

### 4.2 Four mechanisms on one dial

`densityLevel` (int 0–5) resolves into `DungeonPatternSpatialSettings` and into
four passes, each monotone in the dial:

| # | Mechanism | What it removes |
|---|---|---|
| **M1** | **Measured transition reservation** replaces the size cap: reserve the minimum run + landing footprint over all contracts compatible with that edge's rise and kind, loaded before inflation (static data — no stair *selection* moves earlier). Includes the explicit stairwell shaft. | the 4–5 cell dead band on every stair axis |
| **M2** | **Pack**: lane pitch shrinks toward room size, lattice slack toward 0, envelope radius and room size grow, rooms inflate to their lattice cell minus the M1 reservation, enclosure chance → 1.0 | the channels around every room |
| **M3** | **Annex**: each vacant lattice cell is claimed by an adjacent room as an extra rect part in its footprint | the 9x9 craters |
| **M4** | **Mop up**: leftover slivers ≤2 cells wide are absorbed by a neighbour; over-large annexed rooms are subdivided into chambers with a partition wall and a doorway | ragged 1xN leftovers, and the warehouse look |

**M3 deliberately does not add rooms.** Room index is 1:1 with route node index
throughout the generator (`intent.nodes[zone.roomIndex]`, `TryAssignRoomLevels`,
recipe slots, tier requirements). Annexation adds a rect part to an existing
room, which costs nothing in that coupling and inherits the room's level for
free. `RoomFootprint` is already a rect-part composition (`cs:7493`) and
`BuildProcessionalRoomParts` already emits multi-rect rooms via its wing branch.
Annexed parts must never become `parts[0]`, because `Dominant`/`Center` is the
node anchor.

**M4's chamber subdivision goes in at the boundary-context seam**
(`TryBuildRoomBoundaryContext`, `cs:3314`), not in the layout — but repartitioning
`cellRoomIds` alone **produces no walls at all**, and the design must say so.
`enclosedRooms` is sized to `layout.rooms.Count` (`cs:3335`), and `IsEnclosedRoom`
bounds-checks against that array (`ElevationEdgeModel.cs:2662`), so any chamber id
at or beyond `rooms.Count` reads as unenclosed and `IsPartitionWallEdge` returns
false. `ValidateEnclosedRoomDoorways` has the same bound on its `doorwayCounts`
array (`cs:3848`), so those chambers would also skip validation silently.

M4 therefore introduces a **boundary-room set** that is a refinement of
`layout.rooms`, and expands all three pieces of state together:

- `cellRoomIds` repartitioned to chamber granularity;
- `enclosedRooms` sized to the **chamber** count, with each chamber's flag
  derived from its parent room's;
- one `DoorwayEdge` per chamber seam, added before
  `DemoteSealedEnclosedRooms` / `ValidateEnclosedRoomDoorways` run — so the
  existing sealed-room validation covers the expanded set rather than being
  bypassed by it.

Route semantics are still untouched: `layout.rooms` keeps its 1:1 mapping to
route nodes, and the chamber set exists only inside `RoomBoundaryContext`.
Consumers that read `cellRoomIds` for other purposes — trap room/corridor
weighting in `ElevationEdgeModel.Traps.cs` — see a finer partition, which changes
trap distribution slightly and is a phase-5 measurement, not a surprise.

### 4.3 The dial

Targets to be measured and retuned in phase 6 — not asserted:

| density | lane gap beyond room | lattice slack | vacant cells | enclosure | target lattice-envelope fill | max void component |
|---|---|---|---|---|---|---|
| 0 | today (2–6) | 8 | untouched | 0.5 | ~28% | unbounded |
| 1 | ≤4 | 6 | untouched | 0.6 | ~35% | ≤30 |
| 2 | ≤3 | 4 | untouched | 0.7 | ~45% | ≤20 |
| 3 | ≤2 | 2 | half annexed | 0.8 | ~60% | ≤12 |
| 4 | ≤1 | 1 | all annexed | 0.9 | ~80% | ≤6 |
| 5 | 0 | 0 | all + mop-up | 1.0 | ≥95% | **≤4** |

Enclosure 1.0 does not currently mean what it says: `ChooseEnclosedRooms`
(`cs:3821-3824`) forces one room unenclosed whenever every draw came up true, so
1.0 yields *n-1* enclosed rooms and one that merges into its neighbours. That
clamp was a variety guarantee for a floorplan where rooms never touched. Phase 3
conditionalizes it on the dial: below density 4 it stands, at 4 and above it is
retired, because "at least one open room" and "no two rooms silently merge" are
in direct conflict once rooms abut. The paired `if (!Any(enclosed, true))` clamp
above it stays at every density — it is guarding the opposite, harmless case.

**The density-5 arithmetic**, as a projection to be checked in phase 6: pitch
falls from ~10 to ~6–7, so a 20-cell lattice envelope shrinks from ~1950 to ~840
cells; annexing ~6 vacant cells adds ~250 floor cells to the ~537 we have. That
lands **~790 floor cells in an ~840-cell envelope — ~94% fill, ~1.5x today's play
space in ~45% of today's footprint.**

Density is therefore *packing*, not scale: the dungeon gets denser and somewhat
smaller, not three times larger. Filling today's bounding box instead would take
~1900 floor cells — 3.5x — which is a different and much larger dungeon. The
mechanisms are indifferent to that choice; it lives entirely in this tuning
table, so phase 6 can move it by looking rather than by rebuilding anything.

## 5. The metric has to change

`floorFillPercent` over the floor bounding box is the metric that let the last
attempt believe it had shipped density: it optimized exterior corridor length
from 57 to 49 cells aggregate, which moved, while the void did not. Replace it:

- **`voidComponents`** — connected components of non-floor cells inside the
  lattice envelope, excluding authored void (vista lane, bridge spans, stairwell
  shafts), as a size histogram with a max.
- **`latticeEnvelopeFillPercent`** — floor ÷ lattice envelope, which unlike the
  bounding-box version does not move when a promontory reaches outward.

Density 5 acceptance is then literally the owner's sentence: **max void component
≤ 4 cells, at most two components larger than 1 cell.**

Alongside it, an **ASCII floorplan projection per seed** in the batch report and
a **top-down PNG per sentinel**. `Validate Topologies` already draws lattice maps,
so the drawing code exists. The previous attempt's failure mode was optimizing a
scalar nobody could see; make the floorplan readable without opening Unity.

## 6. Multiple topologies

Nothing here reduces topology count, but the dial and the topology files do
interact and that interaction needs a rule.

Today six of seven topologies author `spatial` overrides in **absolute cells** —
`"columnGapCells": { "min": 8, "max": 11 }`, `roomSizes`, `latticeSlackMaxCells`,
and `twin-wing-keep`'s explicit per-lane array `[6, 5, 6, 8, 8, 9]`. Absolute
values fight a dial that moves pitch.

**Rule: topology spatial overrides become density-relative.** They declare
offsets and clamps against the resolved pitch (`minDelta`/`maxDelta`,
`maxWidth: pitch-1`) rather than raw cells, so a topology's authored *character*
— twin-wing's tight lattice columns, descent-shaft's narrow lanes — is preserved
across the whole dial instead of being pinned to one density. Each file keeps the
comment explaining why it deviates, which is already the convention.

**`Tools > Dungeon Lab > Validate Topologies` runs all seven at all six
densities** and reports achieved fill and max void component per topology per
level. A topology that cannot reach density 5 is then a data problem the
validator names by file, fixable without generator C# — which is the property
that made adding four topologies cost ~55 lines of data each, and the property
worth protecting.

Expect `descent-shaft` (48% vacant) and `ridge-ravine` (40%) to lean hardest on
M3, and `twin-wing-keep` (7x3 lattice at a 9-cell pitch inside a 52-cell
envelope) to be the one most likely to need an authored exception. That is a
prediction to check in phase 4, not a plan to special-case it in advance.

## 7. Work, in dependency order

| Phase | Work | Gate | Size |
|---|---|---|---|
| **0** ✅ | **Instrumentation.** `voidComponents` + `latticeEnvelopeFillPercent`; ASCII floorplan per seed; top-down PNG per sentinel. **Redefine the A/B gate first (§7.2).** Write the `voidComponents` baseline for the current tree into `DungeonLabReports/` before anything changes, so phases 3–5 have a real before-number. | Per-seed `hashes.canonical` vector unchanged across all 200 seeds. | ~0.5 d |
| **1** ✅ | **Flag → dial.** One profile asset with `densityLevel`; `Arena > Dungeons > Density > 0..5`; `ARENA_DUNGEON_DENSITY`. Delete `generation_profile_dense.asset`, the Spacious/Dense menu pair, `ARENA_DUNGEON_GENERATION_PROFILE`. Update `ops/dungeon-port-ab.sh`, `ops/dungeon-step2-verify.sh`, `DungeonLabStairLaneContinuityTests.cs:32`. Topology overrides become density-relative. | **Identity-preserving: density 0's geometry is identical to today's `spacious`** — per-seed `hashes.canonical` vector unchanged, same accepted set, same failure codes. This is the one phase where a hash gate is the right tool. | ~1 d |
| **2** ◐ | **M1** — measured transition reservation, including the explicit stairwell shaft; retire the `BaselineRoomSizeRangeForRole` cap. Fix the vista ordering. | Corpus hard-valid at densities 0–1; transition reservation success rate ≥ today's; the three known stairwell-retry seeds resolve on attempt 1. | ~1–2 d |
| **3** ← next | **Corridor ownership + reroute spike, then M2 — pack.** Pitch, room size, slack, envelope radius, enclosure chance become functions of the dial. | Corpus hard-valid at 0–3; run-twice determinism; sentinels eyeballed at each level. | ~2–3 d |
| **4** | **M3 — annex vacant lattice cells.** | Corpus hard-valid at 0–5; `voidComponents` max falls monotonically across the dial. | ~2–4 d |
| **5** | **M4 — mop-up and chamber subdivision.** | Density 5 meets the §5 acceptance. | ~2–3 d |
| **6** | **Tune and look.** Sentinels at all six levels; retune the §4.3 table; measure collision-export size, scene object count and trap count at density 5. | Owner sentinel review. | ~1–2 d |

Roughly **9–15 focused days**. Large, but all of it sits inside the existing
route-first seam: no second planner, no new renderer concept, no change to the
route, topology, recipe or stair contracts.

### 7.1 The A/B tool must be redefined before it can gate anything

`ops/dungeon-port-ab.sh` compares `resultHash`, which is
`ComputeSha256(seedReports)` over the **entire** seed-report array
(`Batch.cs:5306`). Each seed report embeds `settings` — every public field of
`DungeonGenerationSettings`, reflected (`Batch.cs:53`) — and `settingsDigest`
over the same. So:

- phase 0 moves `resultHash` by adding report fields, with no geometry change;
- phase 1 moves it again by adding `densityLevel` to the settings struct, also
  with no geometry change.

A whole-report identity gate is therefore **unachievable by construction** for
both phases that need it. That is a defect in the tool, not in the plan.

**Fix it in phase 0, before it is relied on.** The gate compares the per-seed
**`hashes.canonical`** vector — plus `accepted` and the failure codes — which is
what "the same dungeon came out" actually means. `hashes.layout` and
`hashes.tieredLevelPlan` are already there for narrowing a mismatch to a stage.
`resultHash` stays in the report as a cheap change-detector; it stops being a
gate.

### 7.2 How this is gated, and why not with hashes

Phase 1 is an identity-preserving refactor, so it gets the geometry-identity A/B
gate of §7.1 — that is exactly what `ops/dungeon-port-ab.sh` exists for, and it
compares against the current commit rather than a recorded value.

**Phases 2–6 are design flux, and get no hash ceremony.** Per the owner ruling
of 2026-07-22 carried in `CURRENT_STATUS.md`: hard validity gates every build,
run-twice determinism on any seed being inspected, sentinel eyeballing with no
gate attached, and the batch sweep as a smoke test rather than a gate. Every seed
moves at every density ≥1 and that is the intended outcome, not a regression.

Density 0 stays *recognizably* today's dungeon — same topologies, same pitch,
same room sizes — and after phase 1 it is not hash-locked. Sparse is a supported
setting on the same code path, not a preserved legacy branch: there is no
density-0 special case anywhere in the design, only parameter values at one end
of a table.

## 8. Resolved forks

**F1 — pack, not enlarge.** Density 5 lands ~1.5x floor in ~45% of the footprint.
Recorded in §4.3 as a tuning table so phase 6 can move it by looking.

**F2 — bridge spans keep their void.** A bridge over filled floor is a walkway.
This means density 5 has a few deliberate holes larger than 2x2; they are
excluded from the `voidComponents` metric as authored void.

**F3 — yes, subdivide over-large annexed rooms into chambers** (M4). Without it
density 5 reads as a warehouse. It is the one part of the plan that is polish
rather than structure, so it is the correct thing to cut if the schedule bites.

**F4 — filler is walkable floor, not solid rock.** Rejected the rock option:
it hides holes without adding play space and needs a genuinely new render concept
for mass between plateaus at different levels, where growing rooms reuses the
footprint, wall, doorway, level and collision machinery that already exists.

## 9. Residual risks

1. ~~**The corridor reroute spike (§3.1 candidate 3) is the one unproven step.**~~
   **RESOLVED 2026-07-27: the spike passed and the fallback is not needed.**
   Forcing a ±1 lateral offset on every generic corridor left the 200-seed corpus
   at 199/200 accepted and 199 hard-valid — indistinguishable from straight
   corridors, with the transition contracts resolving and the boundary builder
   deriving the right threshold. ±2 fits less often (186/200) but everything that
   fits is equally valid, and as the ladder's third rung it is only reached when
   the straight path already failed. Recipe-port edges are excluded: their
   approach depth is authored and candidate 3 never proposed moving them.
   Evidence in `CURRENT_STATUS.md`.
2. **Recipe rooms cannot grow.** Authored footprints are fixed and their ports
   declare approach depth; at high density they end up ringed by annexed
   neighbour space and their port approach must stay reserved.
3. **Cost at density 5.** ~1.5x floor cells, and a larger increase in wall
   segments — every packed seam is now a partition or retaining wall where it was
   a cliff face. Collision export size, scene object count and trap count
   (`trapFloorCellsPerTrap: 25` scales automatically, 18 → ~28) get measured in
   phase 6, not assumed.
4. **`descent-shaft` and `ridge-ravine` lean hardest on M3**, and
   `twin-wing-keep` is the likeliest to need an authored exception. Checked in
   phase 4 by the per-topology validator run, not pre-empted.

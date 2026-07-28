# Density scale — the per-phase log, 2026-07-27/28

History, not current constraints. Phases 0 through 6 of
[`density-scale-design-2026-07-27.md`](../../dungeon-builder/density-scale-design-2026-07-27.md),
moved out of `CURRENT_STATUS.md` when the work closed, per that page's own
rule about per-phase evidence. The design doc carries the conclusions; this
carries how each phase got there and what was measured as wrong on the way.
Reproducible evidence lives in `DungeonLabReports/phase4/README.md`.

Blocks are newest first, as they were written.

### Landed 2026-07-28 — density scale, phase 6 (tune and look). The dial is done.

**The dial steps evenly now.** §4.3's fill column and its mechanism column
disagreed with each other, and the fill column is the one anybody can see:
packing alone tops out near 34% (measured), so densities 1 and 2 could never
reach 35% and 45% with "vacant cells untouched", and density 4 could not reach
80% with mop-up off. The mechanism columns moved to serve the fill column.

| density | fill min/p50 | §4.3 target | annex | mop-up | accepted | hardValid | renders |
|---|---|---|---|---|---|---|---|
| 0 | 21 / **26%** | 28 | 0.00 | 0.00 | 199/200 | 199 | 199/200 |
| 1 | 26 / **33%** | 35 | 0.25 | 0.10 | 200/200 | 200 | 200/200 |
| 2 | 30 / **47%** | 45 | 1.00 | 0.15 | 200/200 | 200 | 200/200 |
| 3 | 42 / **65%** | 60 | 1.00 | 0.40 | 200/200 | 200 | 200/200 |
| 4 | 62 / **80%** | 80 | 1.00 | 0.60 | 200/200 | 200 | 200/200 |
| 5 | 92 / **93%** | 95 | 1.00 | 1.00 | 200/200 | 200 | 200/200 |

Steps of +7/+14/+18/+15/+13 points, against the shipped-yesterday
+3/+3/+9/+33/+19 — the cliff between 3 and 4 is gone. Density 0 is unchanged on
all 200 canonical hashes, density 3 is byte-identical across two independent
runs, and density 5 still meets §5 on 200/200.

**`minLatticeEnvelopeFillPercent` is density-relative.** One flat 0.20 could not
mean anything across a dial spanning 26% to 93%, so it is a column now — set a
few points under each level's observed minimum, where it stays a backstop
against a degenerate layout rather than a shaper of room size. The profile
asset's field is gone; the table is the only source.

**What density 5 costs (§9 residual risk 3, measured not assumed).** Twelve
seeds per level, medians:

| | density 0 | density 3 | density 5 |
|---|---|---|---|
| floor cells | 527 | 1108 | **1308** (2.5x) |
| scene objects | 10615 | 13437 | **12645** (1.2x) |
| colliders | 9640 | 11690 | **10650** (1.1x) |
| partition walls | 27 | 92 | 204 |
| cliff edges | 398 | 420 | **297** |
| railings | 46 | 6 | 2 |
| doorways | 26 | 44 | 52 |
| gateways | 12 | 4 | **2** |
| traps | 21 | 44 | 53 (2.5x) |

Three things worth knowing:

- **The cost does not scale with the floor.** 2.5x the play space costs 1.2x the
  objects and 1.1x the colliders, because a packed seam becomes ONE partition
  wall where an open cliff face was a wall plus a railing plus corner kits. §9
  expected the opposite. Collision payload is one entry per collision source, so
  the collider count is the payload size to within a constant.
- **The peak is in the middle, not at the packed end.** Density 3 renders more
  objects than density 5: a half-packed floorplan has the most boundary, and
  boundary is what costs.
- **Doors nearly vanish as density rises** — 12 gateways at density 0, 2 at
  density 5, while doorways double. The gateway rules require a real wall on
  both flanks and leave chamfer-framed entrances bare on purpose
  (`docs`, 2026-07-26), and a packed floorplan produces fewer qualifying
  frames. That is a look question for the owner rather than a defect: a packed
  keep with two doors may or may not be what you want.

### Landed 2026-07-28 — density scale, phase 5 (M4 — mop up, and chambers)

**Density 5 meets §5's acceptance on all 200 seeds**: max void component ≤4
cells and at most two components larger than one cell. Measured: max component
0 p50 / 2 p95 / 2 max, components larger than one cell 0 p50 / 1 max, at **93%
lattice-envelope fill**.

| density | accepted | hardValid | fill p50 | max void p50/p95/max | components >1 cell p50/max | boundary chambers p50 |
|---|---|---|---|---|---|---|
| 0 | 199/200 | 199 | 26% | 742 / 1106 / 1229 | 6 / 10 | 13 |
| 1 | 200/200 | 200 | 29% | 698 / 993 / 1097 | 6 / 9 | 13 |
| 2 | 200/200 | 200 | 32% | 583 / 872 / 1066 | 6 / 13 | 13 |
| 3 | 200/200 | 200 | 41% | 332 / 657 / 826 | 10 / 20 | 16 |
| 4 | 200/200 | 200 | 74% | 84 / 225 / 456 | 12 / 20 | 25 |
| 5 | 200/200 | 200 | **93%** | **0 / 2 / 2** | **0 / 1** | 30 |

Density 5 is byte-identical across two independent runs, the EditMode
`DungeonLab*` filter is 100 pass / 25 fail on this tree and on HEAD (same 25),
and **the whole 200-seed corpus now RENDERS at every density measured** — see
the tier-corner fix below.

**M4a — mop-up is M3 with two parameters moved.** It sweeps every lattice band
rather than only the vacant ones, and takes rects down to a single cell. What
M3 leaves is the channel around each room — ragged, wrapping a room on two or
three sides — so the sweep repeats until a pass claims nothing: a hole two bands
from any room is out of reach on the first pass and adjacent to a grown room on
the second. One pass left a 128-cell corner on `ridge-ravine`.

**M4b — chambers.** A room over 64 cells (8x8, the largest a generic room
reaches at density 0) is cut by recursive straight guillotine cuts until every
chamber fits, each cut checked for both sides staying connected and for a seam
long enough to hold a flanked doorway. Density 5 runs ~30 chambers over 13
rooms. It is a boundary-stage refinement: `layout.rooms` keeps its 1:1 mapping
to route nodes, and §4.2's trap is respected — `cellRoomIds`, `enclosedRooms`
**and** one `DoorwayEdge` per seam expand together, before the two sealed-room
passes rather than after them. Gated on the annex dial, so densities 0–2 are
untouched by construction.

**Phase 2's last item is closed: the stairwell shaft is explicit now.** Phase 4
bought the tower its void with a blanket two-cell band around every transition
corridor. That cost ~200 cells a seed and was most of the void left at density
5. It is now ONE 3x3 window per transition corridor, on whichever side is
actually free — which is only knowable after rooms, recipes and corridors are
compiled, and is the opposite ordering to the pre-inflation reservation that
failed on 2026-07-27. The window is carried on the layout, loop corridors route
around it, and it counts as authored void because that is what §4.1 says a
stairwell shaft is.

**Two things the metric was calling holes that the design calls authored void.**
Both were found by measurement, not by reading:

- **A dais showpiece's backdrop.** `TryValidateAcceptedRecipes` requires the
  cells behind a dais to stay empty so it reads as backed against an exterior
  wall. It was the LAST hole at density 5 — one 7-cell strip in all 200 seeds,
  the width of the reviewed landmark's dais.
- **The gap an aerial span crosses**, not just the piece sitting on it. Fork F2
  keeps that void on purpose, and the deck is added by the tier stage, so the
  layout floor mask the metric reads is empty underneath it by construction.

**And a renderer defect that phase 4 flagged is fixed.** `STAIR_BOUNDARY_CONFLICT`
— a rounded tier corner sweeping into a stairwell tower's footprint — was 2/200
at density 5 in phase 4 and packing took it to **27/200**. The cause was that
the corner SELECTOR and its VALIDATOR disagreed about what a stair owns: the
selector skipped `reservedCells` (a stair's floor-blocked cells) while the
validator threw on the wider footprint and landing-port set. They share one set
now, the selector keeps those corners square — the same decision it already
makes for a reserved cell or a diagonally touching mass — and the validator
stays as the assertion that it worked. **Render sweep: 200/200 at densities 4
and 5, 199/200 at density 0** (the one failure is the known plan failure
`2026072187`). One test needed updating: `GatewayPlanning_DoesNotSuppressAnEligibleAngledCorner`
invokes `FindRoundTierCorners` reflectively and pins its signature.

**Still open for phase 6.** Densities 3 and 4 are where the dial now reads
unevenly — 41% and 74% fill against §4.3's 60% and 80% — because mop-up is off
at 3 and half on at 4. That is a table value, not a mechanism, and phase 6 is
where the table gets retuned by looking. §4.3's other columns (`lane gap beyond
room`, `lattice slack`) are also worth revisiting now that fill actually spans
26–93%.

### Landed 2026-07-28 — density scale, phase 4 (M3 — annex the craters)

**The dial now removes both halves of the void §2 measured.** M2 closed the
channel around each room; M3 claims the vacant lattice cells — the map positions
no node occupies, which at a 9-cell pitch are 9x9 craters outside every room's
placement envelope, and which packing cannot reach by construction.

| density | accepted | hardValid | fill p50 | max void component p50 | channel void p50 | vacant void p50 |
|---|---|---|---|---|---|---|
| 0 | 199/200 | 199 | 26% | 763 | 552 | 849 |
| 1 | 200/200 | 200 | 28% | 717 | 507 | 831 |
| 2 | 200/200 | 200 | 31% | 591 | 469 | 683 |
| 3 | 200/200 | 200 | **39%** | **377** | 462 | 474 |
| 4 | 200/200 | 200 | **54%** | **189** | 413 | 200 |
| 5 | 200/200 | 200 | **62%** | **156** | 395 | 143 |

**Phase 4's gate is met: hard-valid at every level 0–5, and the max void
component falls monotonically across the dial.** Density 5 is byte-identical
across two independent runs (`ops/dungeon-step2-verify.sh 5`), and the EditMode
`DungeonLab*` filter is 100 pass / 25 fail on both this tree and HEAD — the same
25 tests, none moved either way.

Per-topology fill, p50, densities 0→5:

```text
atrium-ring         23.9  27.6  32.6  38.1  56.9  63.3
descent-shaft       26.3  27.8  29.1  42.4  71.6  75.1
processional-spine  25.8  29.5  33.4  36.0  46.7  56.0
ridge-ravine        27.2  28.8  30.8  40.9  56.6  67.5
sunken-basin        26.0  29.4  31.8  39.0  50.1  49.7
terraced-cascade    27.5  31.6  38.2  46.6  67.6  68.8
twin-wing-keep      24.2  27.7  31.1  36.4  51.8  57.1
```

§6 predicted `descent-shaft` would lean hardest on M3 (48% of its lattice is
vacant). It does: 26% → 75%, the largest gain in the set.

**How M3 works, and the one thing it is not.** `DungeonLabGenerator.Annex.cs`
runs *after* rooms, recipes and corridors are compiled, finds the largest free
rect inside each vacant lattice cell's band, and hands it to the adjacent room
that owns the longest face on it as an extra rect part. It adds no rooms — room
index stays 1:1 with route node index — and an annexed part is never `parts[0]`,
because `Dominant`/`Center` is the node anchor. It cannot fail an attempt: worst
case it annexes nothing and the seed is exactly what M2 produced, which is what
densities 0–2 ask for. That ordering is the whole design, and it is the lesson
from the blind pre-inflation stairwell reservation that cost 61 density-0 seeds.

**Three prerequisites had to be fixed first, and two of them were not on the
plan. All three are behaviour changes:**

1. **The room clamp is per node now, not per axis.** `ResolveTopologySpatialSettings`
   clamped every room to the *tightest lane anywhere on its axis*, so
   `twin-wing-keep` (lanes 6,5,6,8,8,9) pinned every room to five cells at every
   density. The clamp moved to inflation time (`ResolveAdjacentLaneGaps`), where
   the node's embedded position is known and the binding gap is its own.
   **This moves density 0**: 17 of 200 seeds, all `twin-wing-keep`, same accepted
   set and same failure codes (`ops/dungeon-port-ab.sh 0`, 199/200 both legs).
   That is the fix working — those rooms sit beside 8- and 9-cell lanes and were
   being held to 5 — but it is the first density-0 geometry change since phase 1.
2. **A topology's room-size override was deaf to the dial.** `roomSizeDeltaCells`
   resolves against the pitch, and phase 3 measured §4.3's expected pitch drop as
   wrong and holds the pitch fixed — so the override is numerically constant, and
   the three topologies that declare one (`twin-wing-keep`, `descent-shaft`,
   `ridge-ravine`) stopped packing at all. They were the three lowest-fill
   topologies at density 5 by a wide margin, with `channelVoidCells` flat across
   the entire dial. The override is now read as the topology's density-0 size and
   packed by the same rule as the profile's own (`PackAuthoredRoomSize`), which
   is identity at density 0. **This was not in the phase-4 brief**; it was found
   by the sentinel evidence the brief asked to check, and phase 4's gate cannot
   be read on a corpus where three of seven topologies ignore the dial.
3. **A generic room is clamped against already-placed recipe rooms.** A centred
   rect and an authored recipe footprint do not meet the same way: two generic
   rooms one lane apart abut exactly, but a recipe reaches symmetrically from its
   anchor and a lane-width neighbour lands one cell inside it.
   `ridge-ravine`'s `ridge-walk` sits one 8-cell lane from its landmark recipe
   and lost every seed at densities 4 and 5 to `ROUTE_ROOM_INFLATION_EXHAUSTED`
   without this.

**Two things M3 must not take, both found by measurement:**

- **A showpiece's backdrop is authored void.** `TryValidateAcceptedRecipes`
  requires the cells behind a dais to stay empty so it reads as backed against an
  exterior wall. Annexing them cost 96 `RECIPE_SHOWPIECE_FIT` rejections and 14
  seeds across densities 4 and 5.
- **The band beside a stair, bridge or stairwell corridor.** A stairwell tower
  stands on void beside its path and that requirement is still implicit — the
  placer searches at tier time rather than the plan reserving it. Declining a
  2-cell band around those corridors is worth two points of fill:

  | | clearance 2 (shipped) | clearance 0 |
  |---|---|---|
  | density 4 | 200/200, 0 `ROUTE_TRANSITION_RESERVATION` | 198/200, 52 |
  | density 5 | 200/200, 0 | 197/200, 48 |

**Open, and found by this phase: a hard-valid plan is not the same claim as a
plan that renders.** Batch Validate never builds a GameObject, so nothing in the
corpus evidence has ever covered the renderer. `Tools > Dungeon Lab > Render
Sweep (200 Fixed Seeds)` now does, and it says:

| density | rendered | failure |
|---|---|---|
| 0 | 199/200 | the known plan failure `2026072187`; no renderer failure |
| 3 | 200/200 | — |
| 4 | 197/200 | 3 × `STAIR_BOUNDARY_CONFLICT` |
| 5 | 198/200 | 2 × `STAIR_BOUNDARY_CONFLICT` |

Every one is a **tier corner kit placed on a cell inside a stairwell tower's
footprint** (`2026072223`, `2026072298`, `2026072161`). This is the same defect
class as the two `DungeonLabStairBoundaryCompatibilityTests.CurvedBridgeRegression_*`
tests that are red on `main` and have been for some time; packing produces more
tier corners, so it hits more often. It is **not** a phase-4 mechanism failing —
M3 adds no transitions and no corners — and fixing it belongs with the tier
corner / stair boundary work, not inside the density dial. Worth doing before
density 4–5 is shipped to a player, because `RandomDungeonSceneBuilder` will
throw on those seeds.

**§4.3's ≥95% target is arithmetically reachable, and the worry that it was not
is resolved — but not by M3.** Measured per seed, the ceiling if every remaining
non-authored void cell became floor is **99% at every density**: the lattice
envelope's 4-cell margin is covered by the rooms on its rim, not wasted. M3's own
ceiling — every vacant cell claimed, channel untouched — is **~72%**, and it
achieves 62%. The remaining ten points of vacant are the declined bands above,
remnants under 2x2, and craters with no adjacent room face. **The 27 points
between 72% and 99% are channel, and that is M4's mop-up in phase 5.**

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

### Landed 2026-07-27 — density scale, phase 3: corridor ownership and M2

**The dial moves geometry now.** `DungeonGenerationProfile.ResolveDensitySpatialSettings`
is no longer the identity: it reads a six-row table and packs lattice slack, room
size and enclosure chance. Density 0 is still the identity *by construction* —
every column is a delta or a scale against the profile's authored values and row
0 is (0, authored, 1.0) — so sparse is what the profile says, not a special case.

| density | accepted | hardValid | fill p50 | max void component p50 |
|---|---|---|---|---|
| 0 | 199/200 | 199 | 26% | 763 |
| 1 | **200/200** | 200 | 28% | 729 |
| 2 | **200/200** | 200 | 31% | 609 |
| 3 | **200/200** | 200 | 32% | 566 |
| 4 | **200/200** | 200 | 34% | 498 |
| 5 | **200/200** | 200 | 34% | 389 |

**Phase 3's gate asked for hard-valid at densities 0–3; every level 0–5 is
hard-valid.** Fill rises and the max void component falls monotonically across
the whole dial. Density 0 is unchanged from the committed baseline on every
number, and density 3 is byte-identical across two independent runs
(`ops/dungeon-step2-verify.sh 3`). Sentinels for densities 0, 3 and 5 are in
`DungeonLabReports/sentinels_d{0,3,5}/`, three-quarter and top-down.

Getting 4 and 5 there took two more things beyond the table:

- **The route-shape attempt (§3.2).** Inflation, vista, promontory, recipes and
  corridors retry as one unit, keyed by attempt index into the RNG streams so
  attempt *k* draws a genuinely different shape rather than re-running an
  impossible one. Attempt 0 keeps the unsuffixed stream, so a retry that never
  happens does not re-phase the generator. Density 4 went 187 → 200 on this
  alone. The ceiling was sized the way §3.2 asks — started at 4, probed at 16,
  observed maximum 13, set to **26**.
- **Room sizes are clamped to a topology's tightest lane.** `sunken-basin` and
  `terraced-cascade` were losing *every* density-5 seed: both author lanes one
  cell under the pitch, so once rooms grew to the pitch they overlapped their
  neighbours. The authoring guide has always told authors "a lane gap must be at
  least the largest room extent on that axis"; resolving it in
  `ResolveTopologySpatialSettings` makes it true by construction instead of by
  vigilance, and it is what lets a topology keep narrow authored lanes without
  re-declaring its room sizes at every density. No clamping happens at density 0,
  so it is identity there.

**The gap to §4.3's fill targets (45/60/80/95%) is M3, and that is phase 4.**
34% at density 5 is rooms filling their own lattice cells and nothing more; §2
already measured that ~45% of the lattice box is not inside any envelope at all.
The sentinels show it plainly — the mass is denser and the *craters* are
untouched.

**§4.3's assumption that lane pitch falls to ~6–7 is measurably wrong, and the
table now holds pitch at the profile's own value.** Shrinking it was tried first
and cost most of the corpus: at pitch 7 density 3 fell to 117/200 and density 5 to
0/200. The binding constraint is authored recipe footprints, which cannot shrink
(design §9 residual risk 2) and reach four cells from their anchor — the same
reach that floors `roomEnvelopeRadiusCells` at 4. Below a 9-cell pitch they
overlap their neighbours (19 seeds at density 5 failed with two recipe rooms
overlapping each other), and where a recipe room is a vista endpoint it eats the
sight lane (`vista reserved 0/1/2 void cells; required 3`). So density comes from
**closing the channel around generic rooms**, not from moving the lattice — and
the rest has to come from M3 annexing vacant lattice cells, which is phase 4 and
is where the gap to §4.3's 80/95% fill targets lives. §2 already said so: ~45% of
the lattice box is not inside any envelope.

`roomEnvelopeRadiusCells` deliberately does not move either. It is floored at 4
by the same recipe footprint, and with the pitch fixed the 9x9 envelope is exactly
one lattice cell, so making it density-driven would only inflate the fill
denominator — the opposite of what §3 wanted from it.

The corridor ladder earns its keep as soon as rooms touch: the doorway rung is
taken 14 times at density 2, 41 at density 3 and 101 at density 4. The lateral
offset rung has still never been needed, which is the right order of events — it
is the rung below a re-roll, not above it.

The enclosure clamp is retired from density 4 up, as §4.3 requires: "at least one
open room" was a variety guarantee for a floorplan where rooms never touched, and
once they abut it conflicts with "no two rooms silently merge", because
`IsPartitionWallEdge` returns false when neither room is enclosed.

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

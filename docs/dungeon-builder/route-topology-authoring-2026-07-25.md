# Route topology authoring — proposal

Date: 2026-07-25. Status: **authoring model agreed; §8 forks all ruled. §5 step 1
(the output-neutral data cutover) is implemented — see
[`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md) for the shipped
format and `CURRENT_STATUS.md` for what landed. Steps 2 and 3 are not started.**

Two deliberate departures from the §3 sketch below, both because step 1 must not
move output: lane gaps are fixed scalars or fixed per-lane arrays rather than
`[min, max]` ranges (the rubber sheet is step 2), and `weight` is not parsed at
all (weighted selection is step 2, so the field would be dead data). The shipped
schema also carries `vista.id`, `allowGenericRoomWings`, `spatial.settings`, and a
quarantined `legacy` block of hash-compatibility values; §7's table did not
anticipate those.

Context: `CURRENT_STATUS.md` "Open: variation regression". Owner ruling 2026-07-25:
**an archetype is a different route graph entirely.** This page proposes how a
route topology should be authored so that adding one is cheap, then drafts new
ones — including descending and basin shapes the current ascending-spine
structure cannot express.

---

## 1. Why plans read alike: the skeleton space is 24

Counting the actual degrees of freedom before the room-shape jitter:

| Axis | Today |
| --- | --- |
| Topologies | 3, chosen by `seed % 4` (processional 50%, atrium 25%, twin-wing 25%) |
| Spatial arrangements per topology | `TryTransformCoarseEmbedding` picks 1 of 4 quarter-turns and 1 of 2 mirrors from a derived stream — the full dihedral group D4, so **8 and no more** |
| Node positions | fixed literal coordinates; uniform pitch; no jitter |
| **Distinct skeletons, ever** | **3 × 8 = 24** |

Everything else — room dimensions ±1–2 cells, a 40% chance of one 2×2 wing, loop
corridors, stair candidate choice, aerial bridges, 1u zone splits — perturbs a
skeleton drawn from a set of 24. That is the whole regression. Room size (item 1)
was a real but second-order contributor.

### Finding A — item 1's room-size widening reaches only half the seeds

`ResolvePatternSpatialSettings` returns `settings.processionalSpatial` (the
profile asset, widened on 2026-07-25) **only for `processional-spine`**. Atrium-ring
and twin-wing get `BaselinePatternSpatialSettings(...)`, which hardcodes
`BaselineRoomSizeRangeForRole` — the *old* spacious sizes (5×5 / 5×5–6 / 4–5×5) —
and `neighborBiasStrengthCells: 0`, ignoring dense's 1. So 50% of seeds still
render pre-widening room sizes. Item 1 should be recorded as half-landed.

Fix falls out of this proposal: spatial settings become one profile default plus
per-topology overrides, so there is no second code path to forget.

---

## 2. What is actually pinned, and by what

An authoring model is only cheap if the author knows the rules up front. These
are the constraints a topology must satisfy, read out of the code:

### Hard, enforced today

| Rule | Enforced at |
| --- | --- |
| Exactly `9 + 4 = 13` nodes | `TryValidateRouteIntent` — **this is the "always 13 rooms" lock** |
| Exactly 3 recipe slots: `connector`/`compression`, `landmark`/`landmark`, `connector`/`return` | `TryValidateRouteIntent` + recipe `eligibleRoles`/`eligibleBeats` |
| Every node level in `[0, 24]` and `% 4 == 0` | `TryValidateRouteIntent`, `TryAssignRoomLevels` |
| Edge rise ∈ `{+4, +8}` for Stair/Bridge/Stairwell; exactly `0` for LevelCorridor | `TryValidateRouteIntent` (**positive only — the descent blocker**) |
| Graph declares ≥1 Stair, ≥1 Bridge, ≥1 Stairwell | `TryValidateRouteIntent` |
| `bottomNode` level `== 0`, `topNode` level `== 24` | `TryValidateRouteIntent` and again on final anchors at `DungeonLabGenerator.cs:2568` |
| Main route: contiguous orders, no adjacent duplicate role, no adjacent duplicate beat, **≤2 main-route nodes per role**, ≥2 intervening main nodes between slot-bearing nodes | `TryValidateRouteRhythm` |
| Vista source ≥4u above target | `TryValidateRouteIntent` |
| **Every edge's endpoints cardinally aligned** in the embedding | `TryConnectProcessionalRooms` |
| A corridor may not cross a third room, or touch floor outside its endpoint rooms | `PathCrossesThirdRoom`, `PathTouchesExistingFloorOutsideEndpointRooms` |
| Vista lane: cardinal, ≥3 clear cells, no room and no corridor in it | `TryReserveProcessionalVista`, `TryConnectProcessionalRooms` |
| Plan fits `mapWidthMaxCells` × `mapDepthMaxCells` (52×52 in both profiles) **in every orientation** | `TryTransformCoarseEmbedding` |
| Room ≤ `pitch − 1` per axis, and ≤ `roomEnvelopeRadiusCells * 2 + 1` (9) | inflation + envelope checks |

### Self-declared bookkeeping that should not be authored at all

`requiredCycleRank`, `requiredCycleCoreNodeCount`, `requiredJunctionDegree`,
`branchAttachNode`, `branchRejoinNode`, and every edge's `requiredRiseLevels` are
all *declared* per topology and then checked against the literal graph they were
written next to. For literal data they cannot disagree except by editing one and
not the other. **Derive them.** That removes ~12 authored numbers per topology and
one class of self-inflicted failure.

`branchAttachNode`/`branchRejoinNode` are also not well-defined for a general
graph (twin-wing already has two branches off one node). Replace with "report all
nodes of degree ≥3".

### Findings that shape the drafts

**Finding B — descent needs no rise-sign change.** Edge direction is used only to
compute `requiredRiseLevels`. Every other consumer is symmetric
(`TryGetTransition`, `TryValidateConnectedRoomLevelDeltas` use `Abs`, corridor
building handles either order, `TryResolveRouteForwardRecipeAxis` is
direction-agnostic). So a descending route is authorable **today** by writing each
edge low-node-first. Recommendation: still allow `±4/±8` in the validator so
authors write edges in travel order and stop thinking about it.

**Finding C — a vista pair must be ≥2 lattice steps apart.** At pitch 9 a 1-step
vista yields `9 − sourceHalf − targetHalf − 1` clear cells. With a hall up to 8
wide (half 4) and the landmark recipe reaching 4, that is `0`, against a required
minimum of 3. The processional's 1-step vista only works because its source node
is in `plannedOverlooks`, which force-shrinks that room to 4×5 (half 2), landing
on **exactly 3** — the minimum, with zero margin. Every new topology should use a
2+ step vista.

**Finding D — the 4/8 rise grammar prices deep excursions in nodes.** A spur that
drops from a 24u rim to 0 and returns needs ~3 nodes down and ~3 back up. A
"crater" whose whole rim sits at 20–24 therefore cannot close inside an 11-node
budget; it needs ~15. This killed a crater draft below and is worth knowing before
sketching shapes.

**Finding E — twin-wing's embedding is already a non-uniform lattice.** Its
coordinates are absolute cells with gaps `6,5,6,8,8,9` (x) and `9,10` (y), which
is why its pitch is `(1,1)`. The rubber-sheet lattice in §4 is a generalisation of
something the codebase already does by hand.

---

## 3. Proposed authoring model

**One JSON file per topology**, under `Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`
(`docs/project-structure.md`: `Editor/` is "editor-only authoring inputs"; note the
existing recipe/profile assets under `Content/Settings/` are a pre-existing
deviation — not relitigating it here). Newtonsoft is already a dependency
(`DungeonLabGenerator.Batch.cs`), so no new parser.

JSON rather than a ScriptableObject because the payload is a *diagram*: it wants to
be read, diffed and hand-edited, not clicked through an inspector, and it should
not mint a GUID and a `.meta` per topology.

### Schema

```jsonc
{
  "id": "descent-shaft",
  "displayName": "Cataract Shaft",
  "plannerVersion": "descent-shaft-v1",
  "weight": 2,                       // selection weight; 0 disables

  // One char per lattice cell. '.' is empty. First row is the TOP row (highest +y).
  "map": [
    "J . C B A",
    ". . D . .",
    "K . E . .",
    "L M F . .",
    "I H G . ."
  ],

  "spatial": {                       // every field optional; falls back to the profile
    "columnGapCells": [9, 12],       // [min, max] — see §4
    "rowGapCells":    [9, 12],
    "tierSeamCount":  0
  },

  "nodes": {
    // key: id                role              beat          level
    "A": ["rim-arrival",      "arrival",        "arrival",      24, { "main": 0 }],
    "B": ["rim-gate",         "connector",      "compression",  24, { "main": 1 }],
    "C": ["shaft-head",       "junction",       "choice",       20, { "main": 2 }],
    "D": ["upper-gallery",    "processional-hall","approach",   16, { "main": 3 }],
    "E": ["hanging-shrine",   "landmark",       "landmark",     12, { "main": 4 }],
    "F": ["descent-well",     "connector",      "descent",       4, { "main": 5 }],
    "G": ["lower-hall",       "grand-room",     "reveal",        4, { "main": 6 }],
    "H": ["vault-approach",   "return-hall",    "rejoin",        4, { "main": 7 }],
    "I": ["flooded-vault",    "culmination",    "culmination",   0, { "main": 8 }],
    "J": ["rim-ledge",        "connector",      "branch",       24, {}],
    "K": ["shaft-overlook",   "overlook",       "reveal",       20, {}],
    "L": ["cistern",          "optional-room",  "reward",       12, {}],
    "M": ["vault-return",     "connector",      "return",        4, {}]
  },

  // kind only. Rise is DERIVED from the two node levels; id is DERIVED as "{from}-{to}".
  "edges": [
    ["A","B","LevelCorridor"], ["B","C","Stair"],   ["C","D","Stair"],
    ["D","E","Stair"],         ["E","F","Stairwell"],["F","G","LevelCorridor"],
    ["G","H","LevelCorridor"], ["H","I","Stair"],
    ["C","J","Bridge"],        ["J","K","Stair"],   ["K","L","Stairwell"],
    ["L","M","Stair"],         ["M","H","LevelCorridor"]
  ],

  "slots": [
    { "id": "required-compression", "at": "B", "entry": "A-B", "exit": "B-C" },
    { "id": "required-landmark",    "at": "E", "entry": "D-E", "exit": "E-F",
      "orientation": "vista-source-to-target" },
    { "id": "required-return",      "at": "M", "entry": "L-M", "exit": "M-H" }
  ],

  "vista":     { "from": "K", "to": "E", "minVoidCells": 3 },
  "overlooks": [],
  "anchors":   { "bottom": "I", "top": "A" }
}
```

### What this buys

| Per topology | Today | Proposed |
| --- | --- | --- |
| `Build*RouteIntent` | ~130 lines of C# literals | the `nodes` + `edges` tables |
| `TryEmbed*Route` | 45–98 lines incl. a hand-drawn `Vector2Int[]` | the `map` block |
| Switch arms in `TryEmbedRoute`, `ResolvePatternSpatialSettings`, `SelectRoutePattern` | 3 | 0 |
| Recipe port-binding ternaries in `Recipes.cs` | grows per topology | the `slots` block |
| Declared graph metrics | 5 numbers + 8–13 edge ids + per-edge rises | derived |
| **Total** | **180–230 lines of C#** | **~40 lines of data, no code** |

### Derived, never authored

Edge ids (`{from}-{to}`), per-edge rise, cycle rank, cycle-core size, junction
degrees, branch attach/rejoin nodes, node count, `mainRouteOrder` contiguity.

### The authoring model is the validator

Hand-verifying a draft against §2 is the expensive part — I hit real dead ends
doing it for the drafts below. So the format ships with
**`Tools > Dungeon Lab > Validate Topologies`**, which loads every file and reports,
per topology:

- every §2 rule, by node/edge key, with the offending value;
- the map re-rendered with edges drawn, so a misalignment is visible;
- the vista lane, with its computed clear-cell count at the active profile;
- worst-case envelope extent across all 8 orientations and both profiles;
- derived metrics (node count, cycle rank, junction degrees, kind coverage).

This is what makes adding a topology cheap. Cheap is not "fewer rules" — it is
"the rules answer in one second instead of an afternoon".

---

## 4. The variety multiplier: a rubber-sheet lattice

Rigid transforms are exhausted at 8 (D4 is complete), so more topologies alone
multiply 8 by the topology count and no further. The cheap second axis:

**Give each lattice lane its own gap.** All nodes in lattice column *x* share one
world x-offset, so **cardinal alignment is preserved by construction** — the one
invariant that a general graph embedder would have to solve for. Corridor lengths,
compression/release rhythm and overall proportion all change.

Algorithm (deterministic, always in-envelope):

```
minGap = maxRoomExtentOnAxis + 1          // profile-derived; guarantees no overlap
base   = lanes * minGap + (2*radius + 1)
slack  = envelopeCells - base             // 52 - 45 = 7 at pitch 9, 4 lanes
distribute slack across lanes from DerivedRandom(seed, attempt, topologyId, "lattice-x")
```

Variety: ~`C(slack+lanes-1, lanes-1)` lattices per axis. At 4 lanes and slack 7
that is 120 per axis, ~14 400 per topology, × 8 orientations. Combined with 7
topologies the skeleton space goes from **24 to ~800 000**. Authored fixed gaps
stay possible (`"columnGapCells": [9, 9]`), which is how the three existing
topologies reproduce byte-identically.

**Measured risk, and the one thing to check first:** larger gaps grow the floor
bounding box without growing rooms (`roomEnvelopeRadiusCells` is pinned at 4 by
the landmark recipe's reach), so `denseFloorplanMinFillPercent` (0.26) is the
binding constraint on how much slack is spendable. Measure the rejection rate over
200 seeds before widening the gap range; if it bites, the honest fix is a
per-profile slack cap, not a lowered fill floor.

### Selection

`SelectRoutePattern` = `seed % 4` → weighted draw over the registry from
`DerivedRandom(seed, 0, "topology", "select")`. Keyed on seed only, never on
attempt, so topology choice stays attempt-independent per the derived-RNG
doctrine. Scales to any count; `weight: 0` disables a topology without
renumbering anything.

---

## 5. Cutover sequencing

The architecture review's item 3.4 says "do not do this now — three patterns is
not enough evidence to design the abstraction". The owner's ruling changes the
driver: variety now *requires* more graphs, so the question is only whether graphs
4–8 are data or code. The review's de-risking bar is adopted verbatim:

**Step 1 — data cutover, output-neutral.** Port the 3 existing topologies to
files with fixed gaps and rubber sheet disabled. Bake the processional's BFS
branch placement as literal coordinates (`TryFindBoundedCoarsePath` has no RNG, so
its output is a constant — expected `(2,1),(1,1),(0,1),(0,2)`; verify by comparing
node centres, do not assume). Delete the 3 factories, the 3 embedders, the 3
switch arms, the `Recipes.cs` ternaries, and the BFS.
**Gate: Batch Validate (200) must produce the SAME hash.** A different hash means
the port changed behaviour and should be reverted, exactly as for the
`TryBuildCellLevelField` split.

**Step 2 — deliberate rebaseline, one commit.** Enable the weighted selector, the
rubber sheet, per-topology spatial overrides (which also closes Finding A), the
`±4/±8` rise sign, and the node-count range. Every seed changes once. Verify:
two independent 200-seed runs identical, 199/200 accepted or better, then
**Rebuild Random Dungeon and look at several seeds.** No hash tells you whether a
dungeon reads well.

Per the hash-lock memory: these are the only two hash comparisons wanted. Step 1
must not move the hash; step 2 is expected to. Neither value gets asserted in a
test.

**Step 3 — add topologies.** Each is a file plus a validator pass. Room count
becomes 12–16 across the set.

Also worth knowing before step 3: **seed 2026072295's stairwell failure is a
structural constraint, not noise.** A stairwell tower needs void cells beside its
corridor, and `dense` leaves fewer. Authoring guidance: put Stairwell edges where
the plan is open — rims, shaft heads, plan edges — not in the interior of a dense
cluster. Drafts below follow this.

---

## 6. Drafted topologies

Four hand-verified against every rule in §2; three deferred with reasons. Maps use
`x` right, `y` up, one char per lattice cell.

Notation: `▣` recipe slot · `◀b` bottom anchor (level 0) · `◀t` top anchor (24) ·
`◀src`/`◀tgt` vista endpoints. Main-route order is the `#` column; `–` is off-main.
Rise is shown as the absolute value; sign follows travel.

### 6.1 `descent-shaft` — Descent, 13 nodes

Arrive on a high rim, spiral down a shaft, end in a flooded vault at the abyss
datum. Levels run **24 → 0** — the first shape the current structure cannot express.

```
        x0 x1 x2 x3 x4
  y4     J  .  C  B  A
  y3     .  .  D  .  .
  y2     K  .  E  .  .
  y1     L  M  F  .  .
  y0     I  H  G  .  .
```

| key | # | id | role | beat | lvl |
| --- | --- | --- | --- | --- | --- |
| A | 0 | rim-arrival | arrival | arrival | 24 ◀t |
| B | 1 | rim-gate | connector | compression | 24 ▣ |
| C | 2 | shaft-head | junction | choice | 20 |
| D | 3 | upper-gallery | processional-hall | approach | 16 |
| E | 4 | hanging-shrine | landmark | landmark | 12 ▣ ◀tgt |
| F | 5 | descent-well | connector | descent | 4 |
| G | 6 | lower-hall | grand-room | reveal | 4 |
| H | 7 | vault-approach | return-hall | rejoin | 4 |
| I | 8 | flooded-vault | culmination | culmination | 0 ◀b |
| J | – | rim-ledge | connector | branch | 24 |
| K | – | shaft-overlook | overlook | reveal | 20 ◀src |
| L | – | cistern | optional-room | reward | 12 |
| M | – | vault-return | connector | return | 4 ▣ |

Edges: `A-B` Level · `B-C` Stair 4 · `C-D` Stair 4 · `D-E` Stair 4 ·
`E-F` Stairwell 8 · `F-G` Level · `G-H` Level · `H-I` Stair 4 ·
`C-J` Bridge 4 *(skips (1,4))* · `J-K` Stair 4 *(skips (0,3))* ·
`K-L` Stairwell 8 · `L-M` Stair 8 · `M-H` Level.

- Vista `K(0,2) → E(2,2)`: 2 steps over empty `(1,2)`, Δ8u. K's only edges are
  vertical, and no corridor enters row y=2 between them.
- Cycle rank 1. All four transition kinds present. Slots at main orders 1 and 4.
- Stairwells at `E-F` (open shaft) and `K-L` (plan edge) per §5.

### 6.2 `sunken-basin` — Basin, 14 nodes

Two rims at 24, an island shrine at 0 on the basin floor, a bridge across the north
lip, a sump shelf below. Two loops — the basin's signature.

```
        x0 x1 x2 x3 x4
  y3     A  J  .  K  I
  y2     B  .  .  .  H
  y1     C  D  E  F  G
  y0     .  L  M  .  N
```

| key | # | id | role | beat | lvl |
| --- | --- | --- | --- | --- | --- |
| A | 0 | west-rim | arrival | arrival | 24 |
| B | 1 | rim-gate | connector | compression | 24 ▣ |
| C | 2 | west-stair-head | junction | choice | 20 |
| D | 3 | west-terrace | processional-hall | approach | 12 |
| E | 4 | basin-floor | grand-room | reveal | 4 |
| F | 5 | island-shrine | landmark | landmark | 0 ▣ ◀b ◀tgt |
| G | 6 | east-terrace | return-hall | rejoin | 8 |
| H | 7 | east-ascent | connector | ascent | 16 |
| I | 8 | east-rim | culmination | culmination | 24 ◀t |
| J | – | north-rim-walk | connector | branch | 24 |
| K | – | north-overlook | overlook | reveal | 16 ◀src |
| L | – | south-shelf | connector | branch | 12 |
| M | – | sump | optional-room | reward | 4 |
| N | – | east-return | connector | return | 8 ▣ |

Edges: `A-B` Level · `B-C` Stairwell 4 · `C-D` Stair 8 · `D-E` Stair 8 ·
`E-F` Stair 4 · `F-G` Stair 8 · `G-H` Stair 8 · `H-I` Stair 8 ·
`A-J` Level · `J-K` Bridge 8 *(skips (2,3))* · `K-I` Stair 8 ·
`D-L` Level · `L-M` Stair 8 · `M-N` Stair 4 *(skips (3,0))* · `N-G` Level.

- Vista `K(3,3) → F(3,1)`: 2 steps over empty `(3,2)`, Δ16u. K's edges both run
  along y=3; nothing routes through `(3,2)`.
- The landmark sits at the single lowest point, which is also the bottom anchor and
  the vista target — the basin's whole point.
- Cycle rank 2. Slots at main orders 1 and 5. Stairwell at `B-C` (rim, void-rich).

### 6.3 `terraced-cascade` — Terraces, 16 nodes

Broad and shallow: eleven main-route nodes stepping up in 4u increments across a
terrace field, plus two spurs. Largest room count in the set.

```
        x0 x1 x2 x3 x4
  y4     J  I  .  H  G
  y3     .  .  .  .  .
  y2     K  N  O  E  F
  y1     .  .  P  .  M
  y0     A  B  C  D  L
```

| key | # | id | role | beat | lvl |
| --- | --- | --- | --- | --- | --- |
| A | 0 | field-arrival | arrival | arrival | 0 ◀b |
| B | 1 | first-gate | connector | compression | 0 ▣ |
| C | 2 | lower-terrace | terrace-hall | approach | 4 |
| D | 3 | terrace-junction | junction | choice | 4 |
| E | 4 | mid-shrine | landmark | landmark | 8 ▣ ◀tgt |
| F | 5 | mid-terrace | terrace-hall | reveal | 12 |
| G | 6 | upper-junction | junction | rejoin | 16 |
| H | 7 | terrace-overlook | overlook | reveal | 16 ◀src |
| I | 8 | high-stair | stair-hall | ascent | 20 |
| J | 9 | crown-walk | gallery | approach | 24 |
| K | 10 | summit | culmination | culmination | 24 ◀t |
| L | – | east-spur | connector | branch | 4 |
| M | – | east-return | connector | return | 12 ▣ |
| N | – | cascade-ledge | gallery | reward | 20 |
| O | – | cascade-span | optional-room | reward | 12 |
| P | – | cascade-foot | gallery | reward | 8 |

Edges: `A-B` Level · `B-C` Stair 4 · `C-D` Level · `D-E` Stair 4 *(skips (3,1))* ·
`E-F` Stair 4 · `F-G` Stair 4 *(skips (4,3))* · `G-H` Level ·
`H-I` Stair 4 *(skips (2,4))* · `I-J` Stair 4 · `J-K` Level *(skips (0,3))* ·
`D-L` Level · `L-M` Stairwell 8 · `M-F` Level ·
`I-N` Level *(skips (1,3))* · `N-O` Bridge 8 · `O-P` Stair 4 · `P-C` Stair 4.

- Vista `H(3,4) → E(3,2)`: 2 steps over empty `(3,3)`, Δ8u.
- Main roles: terrace-hall ×2, junction ×2 — at the ≤2 cap, no adjacent repeats.
  This is the longest main route the role-occurrence rule permits without inventing
  more roles, which is why `terrace-hall`, `stair-hall` and `gallery` are new
  strings (they fall to `hallRoomSize`; see §7 on making role→size-class data).
- Cycle rank 2. Slots at main orders 1 and 4. Stairwell at `L-M` (east plan edge).

### 6.4 `ridge-ravine` — Ridge / Canyon, 12 nodes

A ridge climbing to 24 over a ravine floor at 0, with a bridge climbing out of the
ravine to an east ledge. Smallest room count in the set; the overlook is a
deliberate degree-1 dead end.

```
        x0 x1 x2 x3 x4
  y3     .  .  E  F  G
  y2     .  .  D  .  H
  y1     .  .  C  .  I
  y0     A  B  J  L  K
```

| key | # | id | role | beat | lvl |
| --- | --- | --- | --- | --- | --- |
| A | 0 | ravine-mouth | arrival | arrival | 0 ◀b |
| B | 1 | mouth-gate | connector | compression | 0 ▣ |
| J | 2 | ravine-floor | grand-room | reveal | 0 |
| C | 3 | ravine-stair | junction | choice | 8 |
| D | 4 | ridge-shrine | landmark | landmark | 16 ▣ ◀tgt |
| E | 5 | ridge-walk | processional-hall | approach | 16 |
| F | 6 | far-ridge | return-hall | rejoin | 20 |
| G | 7 | ridge-crown | culmination | culmination | 24 ◀t |
| H | – | east-ledge | overlook | reveal | 20 ◀src |
| I | – | ravine-span | connector | branch | 16 |
| K | – | sump-pool | optional-room | reward | 8 |
| L | – | mouth-return | connector | return | 4 ▣ |

Edges: `A-B` Level · `B-J` Level · `J-C` Stairwell 8 · `C-D` Stair 8 ·
`D-E` Level · `E-F` Stair 4 · `F-G` Stair 4 ·
`C-I` Bridge 8 *(skips (3,1))* · `I-H` Stair 4 · `I-K` Stairwell 8 ·
`K-L` Stair 4 · `L-J` Stair 4.

- Vista `H(4,2) → D(2,2)`: 2 steps over empty `(3,2)`, Δ4u. H's only edge is
  vertical.
- Node `I` carries three edges — a fork. **`RouteGraphComposer.TryAddBranch` cannot
  express this**, since it only appends chains. Another reason the data model should
  be a flat `edges: [...]` list rather than a sequence of composer ops.
- Cycle rank 1. Slots at main orders 1 and 4. Stairwells at `J-C` (ravine floor,
  void either side) and `I-K` (east plan edge).

### 6.5 Deferred, with reasons

| Shape | Why not yet |
| --- | --- |
| `crater-rim` | Finding D. A rim entirely at 20–24 cannot host a pit reaching 0 and rejoin within the 4/8 grammar under ~15 nodes; three drafts dead-ended on the return edge. Viable at 15, worth doing after the validator exists so the closure is machine-checked rather than hand-traced. |
| `split-plateau` | Two independent 24u plateaus need two full ascents ≈ 18 nodes; wants the node-count range settled first. |
| `mesa-tabletop` | Needs a large flat 24u region, which interacts with `denseFloorplanMinFillPercent` — measure the rubber sheet's fill impact first. |

Room counts across the proposed set: 12, 13, 13, 13, 14, 16 — "always 13 rooms"
gone, and the range is driven by shape rather than by a dial.

---

## 7. Code the cutover touches

Listed so the scope is explicit; nothing here is done.

| Site | Change |
| --- | --- |
| new `DungeonRouteTopology.cs` | load, validate, build `RouteIntent` from data |
| new `Topologies/*.json` + registry | the topology set with weights |
| `Build{Processional,AtriumRing,TwinWing}RouteIntent` | deleted (~390 lines) |
| `TryEmbed{Processional,AtriumRing,TwinWing}Route`, `TryFindBoundedCoarsePath` | replaced by one generic embedder (~200 lines net deletion) |
| `TryTransformCoarseEmbedding` | + rubber-sheet lane gaps |
| `ResolvePatternSpatialSettings`, `BaselinePatternSpatialSettings` | profile default + per-topology override; closes Finding A |
| `RoomSizeRangeForRole` | role→size-class becomes a map in the profile, so a new role name is not silently a hall (`terrace-hall`, `stair-hall`, `gallery` above) |
| `SelectRoutePattern` / `SelectedRoutePatternId` | weighted registry draw |
| `DeclaredTierSeamCandidates` | from data |
| `Recipes.cs` slot port bindings | from data; delete the pattern ternaries |
| `TryValidateRouteIntent` | node-count range; derive cycle/degree metrics; accept `±4/±8` |
| `DungeonLabGenerator.Batch.cs` | `nodes[8]` hardcode → declared anchors |
| tests | route-graph composition tests retarget the data loader |

---

## 8. Owner rulings — 2026-07-25

All three forks are decided. This section is settled decisions, not open questions.

1. **Spawn placement is out of scope.** `CenterDungeonSpawn` spawns on *the floor
   nearest the plan origin*, not the `arrival` node, so nobody spawns at the arrival
   today and in a descending dungeon the player may begin in the flooded vault.
   Owner: *"I don't really care where a player spawns today — much more concerned
   with dungeon appearance right now."* Leave it incidental. Do **not** bundle the
   `RouteIntent`-into-the-plan work (review item 2.7) into this slice. Recorded here
   only so it is not rediscovered as a bug.

2. **The vista target need not be the landmark.** Owner ruling. Consequences:
   - `RecipeOrientationBinding` becomes a per-slot authored field in the topology
     data, so a landmark slot may orient off route-forward
     (`TryResolveRouteForwardRecipeAxis`) instead of `vista-source-to-target`.
   - The `legalQuarterTurns` concern is **resolved, not merely deferred**:
     `episode_throne_twin_stairs_01.asset` declares `legalQuarterTurns` `[0,1,2,3]`
     and `allowMirror: 1`, so route-forward orientation cannot fail that gate for
     any axis. Verify by generating, not by assertion.
   - Unlocks a source→any-lower-node vista, which removes one of the two obstacles
     to `crater-rim` (Finding D's node cost is the other, and still stands) and
     allows a summit-looking-back variant of `terraced-cascade`.
   - The four drafts in §6 still point their vista at the landmark; they do not need
     revising, they simply no longer *have* to.

3. **Rubber sheet lands in step 2.** Owner ruling. Step 1 stays output-neutral and
   must hold the hash; variety lands one commit later.

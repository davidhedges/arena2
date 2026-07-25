# Authoring a route topology

A route topology is one JSON file under
`Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`. It is the whole graph:
nodes, edges, recipe slots, the vista, the anchors, and an ASCII lattice map that
places every node. Adding one costs no C#.

Check a draft with **Tools > Dungeon Lab > Validate Topologies**. It reports every
rule below by node/edge key with the offending value, re-renders the map with its
edges drawn, computes the vista lane's clear-cell count at both profiles, and says
how much the rubber sheet can move each axis. A report is also written to
`DungeonLabReports/route_topology_validation.txt`.

Design background and the drafted topology set:
[`route-topology-authoring-2026-07-25.md`](route-topology-authoring-2026-07-25.md).

## The file

```jsonc
{
  "id": "descent-shaft",              // must equal the file name
  "displayName": "Cataract Shaft",
  "plannerVersion": "descent-shaft-v1",

  // Selection weight. The generator draws one topology per seed, weighted, from
  // DerivedRandom(seed, 0, "topology", "select"). Omit for 1; 0 disables the
  // topology without renumbering anything.
  "weight": 1,

  // One whitespace-separated token per lattice cell; '.' is empty.
  // The FIRST row is the TOP row (highest lattice y).
  "map": [
    "J  .  C  B  A",
    ".  .  D  .  .",
    "K  .  E  .  ."
  ],

  // Every field is optional. Anything absent comes from the generation
  // profile's `processionalSpatial`. There is no second settings table.
  "spatial": {
    // A lane gap is a number (fixed), an object { min, max } (a rubber-sheet
    // range), or an array with one such entry per gap between adjacent lanes
    // (so length == lanes - 1). Either bound may be omitted and falls back to
    // the profile's pitch on that axis.
    "columnGapCells": { "max": 13 },                 // [profile pitch, 13]
    "rowGapCells": [9, { "min": 10, "max": 15 }],    // first lane fixed at 9

    // Overrides of the profile's spatial settings, for a topology whose shape
    // genuinely cannot take the profile's defaults. State the reason in a
    // comment - an override that is not explained is a bug waiting to happen.
    "roomEnvelopeRadiusCells": 4,
    "neighborBiasStrengthCells": 1,
    "latticeSlackMaxCells": 8,
    "tierSeamCount": 0,
    "tierSeamMaxRiseLevels": 8,
    "roomSizes": {                                   // [minW, maxW, minD, maxD]
      "terminal":  [5, 5, 7, 7],
      "hall":      [5, 5, 5, 6],
      "connector": [4, 5, 5, 5]
    }
  },

  // key: [ id, role, beat, level, order ]. Level is absolute, in 4u units.
  "nodes": {
    "A": ["rim-arrival", "arrival",   "arrival",     24, { "main": 0 }],
    "B": ["rim-gate",    "connector", "compression", 24, { "main": 1 }],
    "J": ["rim-ledge",   "connector", "branch",      24, { "branch": 0 }]
  },

  // [ from, to, kind ] and nothing else. The id derives as "{from}-{to}"; the
  // rise derives from the two levels, signed in travel order.
  // Kind is LevelCorridor | Stair | Bridge | Stairwell.
  "edges": [["A", "B", "LevelCorridor"], ["B", "C", "Stair"]],

  "slots": [
    { "id": "required-compression", "at": "B", "entry": "A-B", "exit": "B-C" },
    { "id": "required-landmark",    "at": "E", "entry": "D-E", "exit": "E-F",
      "orientation": "vista-source-to-target" },   // default is "route-forward"
    { "id": "required-return",      "at": "M", "entry": "L-M", "exit": "M-H" }
  ],

  "vista":     { "id": "overlook-to-shrine", "from": "K", "to": "E", "minVoidCells": 3 },
  "overlooks": [],                    // non-traversal pairs that get tier seams
  "anchors":   { "bottom": "I", "top": "A" },
  "allowGenericRoomWings": false
}
```

### Derived, never authored

Edge ids, per-edge rise, node index order, cycle rank, cycle-core size, junction
degrees, node count, main-route contiguity. There is no field for any of them, so
a file cannot disagree with itself. An edge that carries a fourth element is
rejected, and so is a `legacy` block or a `spatial.settings` token — both were
step 1 scaffolding.

Node index order — the order the graph is numbered in reports and in the plan — is
**main-route nodes by `main`, then off-main nodes by `branch`**. Reformatting or
reordering the `nodes` object cannot renumber the graph.

## The rules

Hard rules, all enforced by the generator and all reported by the validator:

| Rule | Why |
| --- | --- |
| 9 to 20 nodes | the sanity rails on room count; the profile's `denseFloorplanMinRooms` is the binding floor in practice |
| Exactly 3 slots: `required-compression`, `required-landmark`, `required-return` | the recipe catalog's `eligibleRoles`/`eligibleBeats` |
| Every level in `[0, 24]` and `% 4 == 0` | the level grammar |
| Every slot node has degree 2 | a two-port recipe room |
| Edge rise `±4` or `±8` for Stair/Bridge/Stairwell, exactly `0` for LevelCorridor | write an edge in travel order in either direction; a descending edge is a rise of `-4` |
| At least one Stair, one Bridge, one Stairwell | the transition-kind coverage check |
| `anchors.bottom` at level 0, `anchors.top` at level 24 | the abyss datum and the ceiling |
| Connected graph, cycle rank ≥ 1, at least two degree-≥3 nodes | a route loops |
| Main route: contiguous orders, no adjacent duplicate role or beat, ≤2 nodes per role, ≥2 intervening nodes between slot-bearing nodes | `TryValidateRouteRhythm` |
| Vista source ≥4u above its target, cardinally aligned, nothing between | `TryValidateRouteIntent` + `TryReserveProcessionalVista` |
| Every edge cardinally aligned in the lattice, with no third node on its lane | `TryConnectProcessionalRooms`, `PathCrossesThirdRoom` |
| No edge inside the vista lane | the vista reservation |
| Plan fits 52×52 **in every orientation, at the widest lattice** | `TryTransformCoarseEmbedding` tries only 4 quarter-turns against one mirror choice |
| Every role appears in the profile's `roleSizeClasses` map | a new role name is an authoring error, not silently a hall |
| Role size range ≤ 9 cells per axis | `roomEnvelopeRadiusCells` is pinned at 4 by the landmark recipe's reach |

## Slot geometry: the rule the validator cannot see

**Read this before drawing a slot.** A recipe's ports are fixed in its own
frame, and `TryResolveActiveRecipePortBindings` demands that each port's
transformed outward direction *equals* the direction of the route neighbour it
is bound to. So the catalog decides the shape of every slot node's corner:

| Slot | Node must be | Because |
| --- | --- | --- |
| `required-compression` | **straight through** — its two edges leave in opposite directions | `connector_example_01` and `connector_flexible_vestibule_01` both put their two mandatory ports on opposite faces (`-x` / `+x`), and `route-forward` binds `+x` to the exit edge |
| `required-landmark` | **straight through, and perpendicular to the vista** | `episode_throne_twin_stairs_01` puts its ports on the *transverse* axis (`-y` / `+y`), while `vista-source-to-target` binds the *primary* axis to the vista line |
| `required-return` | **a corner** — its two edges leave at 90° | `connector_corner_return_01` puts its ports on adjacent faces (`-y` / `+x`) |

Mirroring is not an escape: `allowMirror` flips the transverse axis, so it
chooses which *side* a port faces, never whether the pair is opposite or
adjacent.

Getting this wrong is expensive and silent:

- **Validate Topologies does not check it.** It reports the graph, the lattice
  and the envelope; recipe port geometry is resolved at placement time.
- **`TryValidateRecipeCandidate` does not check it either**, so the wrong-shaped
  recipe still enters the candidate pool for that slot.
- **The retry cannot save it.** `RecipeSelectionRandom` is keyed on
  `(seed, topologyId, nodeId)` with no layout attempt in the key, so every
  layout attempt reselects the same recipe and fails the same way. A
  straight-through `required-return` node does not cost a retry — it loses
  roughly half that topology's seeds outright to `RECIPE_PLACEMENT`.

Two of the four §6 drafts in
[`route-topology-authoring-2026-07-25.md`](route-topology-authoring-2026-07-25.md)
were hand-verified against the rule table above and still broke this, because
the rule was not written down anywhere. It is now.

## The rubber sheet

Rigid transforms are exhausted at 8 — D4 is complete — so a topology with fixed
lane gaps has exactly 8 spatial arrangements and no more. The rubber sheet is the
second axis: **every lane gets its own gap, drawn per seed.** All nodes in one
lattice lane share a world offset, so cardinal alignment survives by construction.

Per axis, per layout attempt, from `DerivedRandom(seed, attempt, topologyId,
"lattice-x" | "lattice-y")`:

```
minimum span = sum of every lane's authored minimum
slack        = min( sum of authored headroom,
                    envelope room: min(mapWidth, mapDepth) - (minimum span + 2*radius + 1),
                    the profile's latticeSlackMaxCells )
```

then the slack is handed out one cell at a time to a uniformly chosen lane that
still has headroom. A topology whose gaps are all fixed draws no random number at
all, so an authored lattice stays exactly as drawn.

Things worth knowing:

- **The envelope term is a cap, not a rejection.** More lanes means a longer
  minimum span means less slack. A seven-column lattice at a 9-cell pitch spans
  54 cells before rooms, does not fit 52, and cannot be widened into fitting —
  that is an authoring problem, and the validator says so.
- **Bigger gaps grow the floor bounding box without growing rooms**, so
  `denseFloorplanMinFillPercent` (0.26) is the thing that bites, and it bites
  suddenly. Measured over `2026072100..2026072299` at `dense`:

  | `latticeSlackMaxCells` | accepted | floor fill min / median | `ROUTE_DENSITY_PRECONDITION` |
  | --- | --- | --- | --- |
  | 8 (shipped) | 199/200 | 27.1% / 30.7% | 0 |
  | 14 | 154/200 | 26.0% / 27.4% | 117 |

  So 8 is not an arbitrary constant — it is most of the available room, with
  about one point of fill to spare. Raise it and re-measure, or don't raise it.
- The minimum lattice is the worst case for **every other rule** — a shorter
  vista lane, tighter rooms — so that is what the validator checks against. The
  widest lattice is the worst case for the envelope only.
- **The sheet always spends its whole budget.** `ResolveLatticeLaneOffsets`
  hands out every available cell; only *which lane* gets each one is drawn. So
  the total span, and therefore the floor bounding box, is effectively a
  constant per topology per profile — and so is floor fill, to within the ±2
  points that room-size jitter contributes. A topology that misses the fill
  floor misses it on **every** seed, not on an unlucky few.
- **A wide lattice is the thing that misses it.** Five lanes at the 9-cell pitch
  span 36 cells before rooms; add the 8-cell sheet and the box is ~50 cells wide
  with the same rooms to fill it. The knob is the lane *minimum*: authoring
  `{ "min": 8 }` is safe on either axis in either profile — 8 is ≥ the largest
  room extent anywhere in the two profiles, so adjacent rooms touch at worst and
  never overlap — and it buys about four points of fill per axis. Reach for that
  before giving up rubber sheet with `latticeSlackMaxCells`.

## Things that bite

- **A vista pair needs 2+ lattice steps.** At pitch 9 a 1-step vista yields
  `9 − sourceReach − targetReach − 1` clear cells, against a required minimum of 3.
  Both `processional-spine` and `twin-wing-keep` clear exactly 3, with zero
  margin, because their source is a small room. Use 2+ steps.
- **Deep excursions cost nodes.** The 4/8 rise grammar prices a spur that drops
  from 24 to 0 and returns at ~6 nodes. A shape whose whole rim sits at 20–24
  cannot close inside 13.
- **Put Stairwell edges where the plan is open** — rims, shaft heads, plan edges.
  A stairwell tower needs void cells beside its corridor, and the `dense` profile
  leaves fewer; seed 2026072295's failure is exactly this, in the interior of a
  dense cluster.
- **A lane gap must be at least the largest room extent on that axis.** Two
  adjacent centred rooms of width `w` need `w ≤ gap` not to overlap. Under-sizing
  a lane does not fail loudly — it burns the six room-inflation retries and then
  fails the layout attempt.
- **The profile's `tierSeamCount` is a request, and `BuildPlannedOverlooks`
  throws when a topology cannot meet it.** A topology with no `overlooks` pairs
  must override `tierSeamCount` to 0.
- **New role strings must be added to the profile's `roleSizeClasses`** — in both
  `generation_profile.asset` and `generation_profile_dense.asset`.

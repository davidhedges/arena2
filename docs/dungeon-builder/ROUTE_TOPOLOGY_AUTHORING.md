# Authoring a route topology

A route topology is one JSON file under
`Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`. It is the whole graph:
nodes, edges, recipe slots, the vista, the anchors, and an ASCII lattice map that
places every node. Adding one costs no C#.

Check a draft with **Tools > Dungeon Lab > Validate Topologies**. It reports every
rule below by node/edge key with the offending value, re-renders the map with its
edges drawn, computes the vista lane's clear-cell count at every density, and says
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
  //
  // SPATIAL OVERRIDES ARE OFFSETS FROM THE RESOLVED PITCH, not absolute cells.
  // Density is a dial (0-5) that moves the pitch, and an absolute value would
  // pin a topology to one setting: "8 to 11 cells" is a sparse lattice forever.
  // "one under the pitch, up to two over" is the same lattice at every density.
  // The retired names `columnGapCells`, `rowGapCells` and `roomSizes` are
  // rejected by the loader with a message rather than reinterpreted, because a
  // bare number is legal in both vocabularies and means something different in
  // each.
  "spatial": {
    // A lane gap is a number (a fixed lane at pitch + that offset), an object
    // { minDelta, maxDelta } (a rubber-sheet range around the pitch), or an
    // array with one such entry per gap between adjacent lanes (so
    // length == lanes - 1). Either bound may be omitted and falls back to the
    // pitch itself.
    "columnGapDeltaCells": { "maxDelta": 4 },        // [pitch, pitch + 4]
    "rowGapDeltaCells": [0, { "minDelta": 1, "maxDelta": 6 }],  // first lane fixed at the pitch

    // Overrides of the profile's spatial settings, for a topology whose shape
    // genuinely cannot take the profile's defaults. State the reason in a
    // comment - an override that is not explained is a bug waiting to happen.
    "roomEnvelopeRadiusCells": 4,                    // absolute; see the note below
    "neighborBiasStrengthCells": 1,                  // absolute; see the note below
    "latticeSlackMaxCells": 8,                       // a CLAMP on the profile's, not a replacement
    "tierSeamCount": 0,                              // a graph property, so absolute
    "tierSeamMaxRiseLevels": 8,                      // a graph property, so absolute
    // [minWidthDelta, maxWidthDelta, minDepthDelta, maxDepthDelta] from the
    // pitch: width against the horizontal pitch, depth against the vertical.
    // These state the topology's DENSITY-0 sizes; the dial then packs them by
    // the same rule as the profile's own, so the channel they leave closes as
    // density rises. You do NOT need to declare narrower rooms to fit a tight
    // lane - every room is clamped to its own adjacent lanes at inflation time,
    // per node, so a tight lane costs the two rooms beside it and nothing else.
    "roomSizeDeltaCells": {
      "terminal":  [-4, -4, -2, -2],
      "hall":      [-4, -4, -4, -3],
      "connector": [-5, -4, -4, -4]
    }
  },

  // key: [ id, role, beat, level, order, layers? ]. Level is absolute, in 4u
  // units. The 6th element is OPTIONAL and declares additional storeys as
  // offsets from this node's own level — see "Layers" below.
  "nodes": {
    "A": ["rim-arrival", "arrival",   "arrival",     24, { "main": 0 }],
    "B": ["rim-gate",    "connector", "compression", 24, { "main": 1 }],
    "E": ["great-hall",  "landmark",  "aperture",     8, { "main": 4 },
          { "layers": { "gallery": 4 } }],
    "J": ["rim-ledge",   "connector", "branch",      24, { "branch": 0 }]
  },

  // [ from, to, kind ] plus an OPTIONAL 4th element binding either end to a
  // declared layer. The id derives as "{from}-{to}"; the rise derives from the
  // two BOUND elevations, signed in travel order.
  // Kind is LevelCorridor | Stair | Bridge | Stairwell.
  "edges": [["A", "B", "LevelCorridor"], ["B", "C", "Stair"],
            ["E", "F", "Stair", { "fromLayer": "gallery" }]],

  // "layers" is OPTIONAL and says which storey of the RECIPE each of the
  // node's declared storeys is — see "Slot layer mapping" below.
  "slots": [
    { "id": "required-compression", "at": "B", "entry": "A-B", "exit": "B-C" },
    { "id": "required-landmark",    "at": "E", "entry": "D-E", "exit": "E-F",
      "orientation": "vista-source-to-target",     // default is "route-forward"
      "layers": { "gallery": "upper" } },
    { "id": "required-return",      "at": "M", "entry": "L-M", "exit": "M-H" }
  ],

  "vista":     { "id": "overlook-to-shrine", "from": "K", "to": "E", "minVoidCells": 3 },
  "overlooks": [],                    // non-traversal pairs that get tier seams
  "anchors":   { "bottom": "I", "top": "A" },
  "allowGenericRoomWings": false
}
```

### Layers — a node's additional storeys

**Added 2026-08-01 (layered-topology D1). No shipped topology declares one**, and
that is deliberate: every rule the layered direction relaxes is gated on a
binding, so a graph that declares nothing behaves exactly as it did before.

A node's `level` stays its **base**. A layer is an offset from it, so nothing
acquires a global storey number: one node's `gallery` is at absolute 12 here and
28 there. An edge end binds a layer by name, and the edge's rise then derives
from `node.level + layer offset` at each end.

```jsonc
"E": ["great-hall", "landmark", "aperture", 8, { "main": 4 },
      { "layers": { "gallery": 4 } }],          // absolute 12
"edges": [["E", "F", "Stair", { "fromLayer": "gallery" }]]
```

Rejected by the **loader**, so the file will not load at all:

- a layer id that is empty or repeated on one node;
- an offset that is not a multiple of 4, or that puts the layer outside `[0, 24]`;
- two layers of one node at the same offset — one elevation may have one id;
- more than one layer at offset 0 (an offset-0 layer *names* the base, and an
  unbound edge end already means the base);
- an empty `layers` table, or a 6th element that is not `{ "layers": … }`;
- `fromLayer`/`toLayer` naming a layer its own endpoint does not declare.

Reported by **Validate Topologies**:

- a declared layer no edge binds — a storey no route reaches generates as
  nothing, which is the same silent-absence class as a beat typo;
- a node that declares layers but carries no recipe slot. Only a recipe's
  non-base storey or an aerial span's deck can build a stacked surface today, so
  a generic room's layers would have no producer. This relaxes when one exists.

A bound edge **resolves at its layer's elevation** — its corridor is leveled
there and its rise is measured from there — rather than at its node's own level
(added 2026-08-01, D3). An unbound end keeps meeting its room where it always
did.

### Slot layer mapping — which storey of the recipe is which storey of the node

**Added 2026-08-01 (layered-topology D3). No shipped topology declares one.**

A topology names the elevations its **routes** bind to. A recipe names the
storeys its own geometry is built on, and it does so without knowing which graph
will place it. The slot is the only place that knows both vocabularies, so it is
the only place they can be equated:

```jsonc
"nodes": {
  "E": ["great-hall", "landmark", "aperture", 8, { "main": 4 },
        { "layers": { "gallery": 4 } }]            // the GRAPH calls it "gallery"
},
"edges": [["E", "F", "Stair", { "fromLayer": "gallery" }]],
"slots": [
  { "id": "required-landmark", "at": "E", "entry": "D-E", "exit": "E-F",
    "layers": { "gallery": "upper" } }            // the RECIPE calls it "upper"
]
```

Equating the two by *name* is what the mapping exists to avoid: it would make
every recipe's layer ids part of every topology's, and a layer id is room-local
by design.

Rejected by the **loader**:

- a mapping naming a layer its own slot node does not declare;
- a missing, empty or non-string recipe layer id;
- two topology layers mapped onto one recipe layer — that would collapse two
  elevations into one place;
- an empty `layers` mapping, or one that is not an object.

Checked per **recipe candidate**, before it can be selected. The codes appear in
that slot's `rejectedCandidates` in the seed report:

| Code | Meaning |
| --- | --- |
| `LAYER_BINDING_UNDECLARED` | the candidate has no storey by that name |
| `LAYER_BINDING_LEVEL_MISMATCH` | the recipe's storey and the node's sit at different relative levels. **The agreement rule** — every elevation inside the room derives from the recipe's offset and every elevation on the route from the topology's, so proving them equal is what lets both derivations stand unchanged |
| `PORT_LAYER_UNMAPPED` | an edge binds a storey this slot never mapped, so its port has nowhere to arrive |
| `PORT_LAYER_MISMATCH` | a bound port is not on the storey its own edge arrives at — or an incident **socket** is off the base layer, which sockets must be: a socket binds by direction, and nothing in the route can say which storey it is on |

An edge that binds nothing arrives on the node's base, which means the recipe's
base — which is every port of every recipe in the catalog today. A recipe you
want routed to on its upper storey therefore needs a **port authored there**;
`episode_layered_gallery_01` has none yet.

`Validate Topologies` also runs a **loader self-check** over in-memory probes
(26 checks), because the schema has no site in the corpus and would otherwise be
first exercised by the first topology to use it. The generator's side has its own
— `Tools > Dungeon Lab > Print Layer Entry Fixture`.

### Derived, never authored

Edge ids, per-edge rise, node index order, cycle rank, cycle-core size, junction
degrees, node count, main-route contiguity. There is no field for any of them, so
a file cannot disagree with itself. An edge that carries a **fifth** element is
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
| Edge rise `±4` or `±8` for Stair/Bridge/Stairwell, exactly `0` for LevelCorridor | write an edge in travel order in either direction; a descending edge is a rise of `-4`. Measured between the **bound** elevations, which are the node levels for an unbound edge |
| A declared layer is bound by an edge, and its node carries a recipe slot | a storey nothing routes to, or that nothing can build, generates as nothing |
| A slot maps only layers its node declares, one recipe storey each; the mapped recipe storey exists, sits at the same relative level, and carries the port every bound edge arrives on | the graph and the room must agree about where a storey is, or the room is built at one height and routed to at another |
| Two rooms may share plan cells only when **both** their nodes declare storeys and the absolute bands those storeys imply do not meet | room inflation's overlap test is volumetric from 2026-08-01 (D4). A node declaring one elevation authorizes nothing, however far its level is from its neighbour's — otherwise generic rooms would start stacking wherever the corpus already spreads levels, which is a variety regression rather than a feature |
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
adjacent. And **`allowMirror` on an asymmetric recipe is a slot-losing bug**:
room inflation builds the footprint with `mirrored: false` while placement may
choose `true`, and the two must be `SetEquals`. A recipe whose footprint is not
mirror-symmetric across its primary axis must set `allowMirror: 0`.

**Which recipe fills a slot is decided by the node's `role` AND `beat`, never by
the slot id.** That is the lever for an authored graph: give the node a beat only
your recipe declares and it becomes the unique candidate there, while staying
ineligible on every other topology. `aperture-gallery` does this with beat
`aperture` (measured 2026-08-01: the episode is rejected at all 600 slots of the
other seven topologies, and their candidate lists are byte-identical with it in
the catalog).

**Two things the validator reports in a misleading shape:**

- **`worst-case reach` falls back to `roomEnvelopeRadiusCells` when NO recipe is
  eligible for a slot**, so a role/beat typo surfaces as a *vista clearance*
  violation rather than "no recipe matches". Check eligibility first when a slot
  node's reach reads as exactly the envelope radius.
- **A `route-forward` slot pays for BOTH of its recipe's extents**, because its
  primary axis follows the exit edge and may point along either world axis. Only
  a `vista-source-to-target` slot is charged the primary extent alone. A 7x7
  authored room therefore reaches 3 into a vista lane no matter how it is turned.

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
- **Bigger gaps spread the same rooms over a bigger lattice**, so the fill gate
  is what bites, and it bites suddenly rather than gradually. It used to be
  `denseFloorplanMinFillPercent` (0.26) measured over the FLOOR bounding box,
  and on that metric a wider rubber sheet was catastrophic — measured over
  `2026072100..2026072299` on the `dense` profile the density dial replaced:

  | `latticeSlackMaxCells` | accepted | floor fill min / median | `ROUTE_DENSITY_PRECONDITION` |
  | --- | --- | --- | --- |
  | 8 (shipped) | 199/200 | 27.1% / 30.7% | 0 |
  | 14 | 154/200 | 26.0% / 27.4% | 117 |

  Since 2026-07-27 the gate is `minLatticeEnvelopeFillPercent` (0.20) over the
  lattice envelope, which is the box the embedder measures against, and it is
  deliberately a backstop rather than a shaper: how packed a dungeon is, is
  `densityLevel`. So slack no longer trades against acceptance the way that
  table shows — but it still trades against fill, and the fill number is what
  the density dial is steered on. Raise it and re-measure.
- The minimum lattice is the worst case for **every other rule** — a shorter
  vista lane, tighter rooms — so that is what the validator checks against. The
  widest lattice is the worst case for the envelope only.
- **The sheet always spends its whole budget.** `ResolveLatticeLaneOffsets`
  hands out every available cell; only *which lane* gets each one is drawn. So
  the total span, and therefore the floor bounding box, is effectively a
  constant per topology per density — and so is floor fill, to within the ±2
  points that room-size jitter contributes. A topology that misses the fill
  floor misses it on **every** seed, not on an unlucky few.
- **A wide lattice is the thing that misses it.** Five lanes at the 9-cell pitch
  span 36 cells before rooms; add the 8-cell sheet and the box is ~50 cells wide
  with the same rooms to fill it. The knob is the lane *minimum*: authoring
  `{ "minDelta": -1 }` is safe on either axis — one under the pitch is still ≥
  the largest room extent, so adjacent rooms touch at worst and never overlap —
  and it buys about four points of fill per axis. Reach for that before clamping
  the rubber sheet with `latticeSlackMaxCells`.

## Things that bite

- **A vista pair needs 2+ lattice steps.** At pitch 9 a 1-step vista yields
  `9 − sourceReach − targetReach − 1` clear cells, against a required minimum of 3.
  Both `processional-spine` and `twin-wing-keep` clear exactly 3, with zero
  margin, because their source is a small room. Use 2+ steps.
- **Deep excursions cost nodes.** The 4/8 rise grammar prices a spur that drops
  from 24 to 0 and returns at ~6 nodes. A shape whose whole rim sits at 20–24
  cannot close inside 13.
- **Put Stairwell edges where the plan is open** — rims, shaft heads, plan edges.
  A stairwell tower needs void cells beside its corridor, and a packed density
  leaves fewer; seed 2026072295's failure is exactly this, in the interior of a
  tight cluster. Phase 2 of the density-scale design makes the shaft an explicit
  reservation, which is what stops this being an authoring hazard.
- **A lane gap must be at least the largest room extent on that axis.** Two
  adjacent centred rooms of width `w` need `w ≤ gap` not to overlap. Under-sizing
  a lane does not fail loudly — it burns the six room-inflation retries and then
  fails the layout attempt.
- **The profile's `tierSeamCount` is a request, and `BuildPlannedOverlooks`
  throws when a topology cannot meet it.** A topology with no `overlooks` pairs
  must override `tierSeamCount` to 0.
- **New role strings must be added to the profile's `roleSizeClasses`** in
  `generation_profile.asset`. There is one profile asset; how packed the dungeon
  is, is the `densityLevel` dial, not a second file.

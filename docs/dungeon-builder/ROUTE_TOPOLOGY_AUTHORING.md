# Authoring a route topology

A route topology is one JSON file under
`Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`. It is the whole graph:
nodes, edges, recipe slots, the vista, the anchors, and an ASCII lattice map that
places every node. Adding one costs no C#.

Check a draft with **Tools > Dungeon Lab > Validate Topologies**. It reports every
rule below by node/edge key with the offending value, re-renders the map with its
edges drawn, and computes the vista lane's clear-cell count at both profiles. A
report is also written to `DungeonLabReports/route_topology_validation.txt`.

Design background and the drafted topology set:
[`route-topology-authoring-2026-07-25.md`](route-topology-authoring-2026-07-25.md).

## The file

```jsonc
{
  "id": "descent-shaft",              // must equal the file name
  "displayName": "Cataract Shaft",
  "plannerVersion": "descent-shaft-v1",

  // One whitespace-separated token per lattice cell; '.' is empty.
  // The FIRST row is the TOP row (highest lattice y).
  "map": [
    "J  .  C  B  A",
    ".  .  D  .  .",
    "K  .  E  .  ."
  ],

  "spatial": {
    // "profile": take the generation profile's processionalSpatial verbatim,
    //            including its pitch, which becomes the uniform lane gap.
    // "baseline": the fixed baseline settings (envelope radius 4, no neighbour
    //            bias, no tier seams, spacious role sizes) with the gaps below.
    "settings": "baseline",
    "columnGapCells": 9,              // a number is a uniform lane pitch
    "rowGapCells": [9, 12]            // an array is one gap per lane pair
  },

  // key: [ id, role, beat, level, order ]. Level is absolute, in 4u units.
  "nodes": {
    "A": ["rim-arrival", "arrival",   "arrival",     24, { "main": 0 }],
    "B": ["rim-gate",    "connector", "compression", 24, { "main": 1 }],
    "J": ["rim-ledge",   "connector", "branch",      24, { "branch": 0 }]
  },

  // [ from, to, kind ]. Rise is derived from the two levels; the id derives as
  // "{from}-{to}". Kind is LevelCorridor | Stair | Bridge | Stairwell.
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
degrees, branch attach/rejoin nodes, node count, main-route contiguity. There is
no field for any of them, so a file cannot disagree with itself.

Node index order — the order the graph is numbered in reports and in the plan — is
**main-route nodes by `main`, then off-main nodes by `branch`**. Reformatting or
reordering the `nodes` object cannot renumber the graph.

## The rules

Hard rules, all enforced by the generator and all reported by the validator:

| Rule | Why |
| --- | --- |
| Exactly 13 nodes | the current room-count lock; becomes a range in step 2 |
| Exactly 3 slots: `required-compression`, `required-landmark`, `required-return` | the recipe catalog's `eligibleRoles`/`eligibleBeats` |
| Every level in `[0, 24]` and `% 4 == 0` | the level grammar |
| Every slot node has degree 2 | a two-port recipe room |
| Edge rise `+4` or `+8` for Stair/Bridge/Stairwell, exactly `0` for LevelCorridor | **positive only** — write descending edges low-node-first until step 2 |
| At least one Stair, one Bridge, one Stairwell | the transition-kind coverage check |
| `anchors.bottom` at level 0, `anchors.top` at level 24 | the abyss datum and the ceiling |
| Connected graph, cycle rank ≥ 1, at least two degree-≥3 nodes | a route loops |
| Main route: contiguous orders, no adjacent duplicate role or beat, ≤2 nodes per role, ≥2 intervening nodes between slot-bearing nodes | `TryValidateRouteRhythm` |
| Vista source ≥4u above its target, cardinally aligned, nothing between | `TryValidateRouteIntent` + `TryReserveProcessionalVista` |
| Every edge cardinally aligned in the lattice, with no third node on its lane | `TryConnectProcessionalRooms`, `PathCrossesThirdRoom` |
| No edge inside the vista lane | the vista reservation |
| Plan fits 52×52 **in every orientation** | `TryTransformCoarseEmbedding` tries only 4 quarter-turns against one mirror choice |
| Role size range ≤ 9 cells per axis | `roomEnvelopeRadiusCells` is pinned at 4 by the landmark recipe's reach |

### Things that bite

- **A vista pair needs 2+ lattice steps.** At pitch 9 a 1-step vista yields
  `9 − sourceReach − targetReach − 1` clear cells, against a required minimum of 3.
  The processional's 1-step vista only clears 3 because its source is a tier-seam
  node, which force-shrinks that room to 4×5. Zero margin. Use 2+ steps.
- **Deep excursions cost nodes.** The 4/8 rise grammar prices a spur that drops
  from 24 to 0 and returns at ~6 nodes. A shape whose whole rim sits at 20–24
  cannot close inside 13.
- **Put Stairwell edges where the plan is open** — rims, shaft heads, plan edges.
  A stairwell tower needs void cells beside its corridor, and the `dense` profile
  leaves fewer; seed 2026072295's failure is exactly this, in the interior of a
  dense cluster.
- **New role strings fall through to `hallRoomSize`.** `RoomSizeRangeForRole` maps
  only `arrival`/`culmination` and `connector` explicitly; anything else is a hall.
- **`weight` and gap ranges do not exist yet.** Selection is still `seed % 4` over
  a fixed table, and lane gaps are fixed. Both land in step 2.

## The `legacy` block

The three ported topologies carry a `legacy` object and pinned per-edge ids. Those
exist only so the data cutover held the batch hash: they reproduce values the
pre-port per-pattern C# embedders reported (`orientationStreamId`,
`embeddingFailureCode`, `branchSearchExpansions`). **Do not add them to a new
topology.** Step 2 unifies them and deletes the block.

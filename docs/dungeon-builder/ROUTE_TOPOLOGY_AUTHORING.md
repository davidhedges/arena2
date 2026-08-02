# Authoring procedural route families

Status: current through procedural 3-D topology Slice 6
Last updated: 2026-08-02

A production route file is a compact family contract under
`Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`. It does not contain a
node map, node elevations, an edge list, recipe slots, anchors, or a vista
diagram. The generator composes those concrete details for the selected seed.

Literal graph JSON remains readable only under `Topologies/Deprecated/` and in
in-memory evidence probes. Deprecated graphs have weight zero and cannot be
forced through a player-facing generation entry point.

## Production schema

```jsonc
{
  "id": "layered-cascade",          // must match the file name
  "displayName": "Layered Cascade Family",
  "plannerVersion": "procedural-route-family-v1",
  "weight": 1,                       // 0 keeps a draft out of selection

  "family": {
    "criticalPathNodes": [9, 9],     // inclusive [minimum, maximum]
    "loopCount": [1, 2],
    "branchNodes": [4, 4],
    "recipeOpportunities": [0, 3]
  },

  "goals": {
    "ceilingLevels": 32,             // 4u lattice, global maximum 40u
    "minimumCycleRank": 1,
    "minimumStructuralLayers": 1,
    "minimumVistaVoidCells": 3,
    "allowGenericRoomWings": true
  },

  "spatial": {
    // Optional pitch-relative rubber-sheet constraints. These retain the
    // existing density semantics; they do not place nodes.
    "columnGapDeltaCells": -1,
    "rowGapDeltaCells": { "minDelta": -1, "maxDelta": 2 },
    "tierSeamCount": 0
  }
}
```

The current grammar supports a nine-node critical spine, four nodes per branch,
and one or two rejoining loops. Those limits are validated by the loader rather
than silently clamped. Range decisions use named deterministic random scopes,
so the same seed and family produce the same route intent.

Production files are rejected if they contain any retired exact-diagram field:
`map`, `nodes`, `edges`, `slots`, `vista`, `overlooks`, `anchors`, or top-level
`ceiling`.

## What the composer owns

The producer runs in this order:

1. Select the family by the existing weighted, seed-only topology stream.
2. Choose the bounded loop and opportunity counts, node roles/beats, 4u base
   levels, room-local structural layers, transition kinds, and layer bindings.
3. Add and publish the critical spine, branches, rejoins, and loops.
4. Use bounded coarse-path search to place branch nodes on the generated
   lattice and publish the concrete recipe-opportunity bindings.
5. Resolve selected recipes from the active catalog.
6. Pass the concrete intent to the existing orientation, rubber sheet, room
   envelopes, inflation, vista reservation, port placement, and corridor
   candidate ladder.

Recipe resolution therefore happens before spatial embedding. A selected
recipe's footprint, ports, layers, transitions, and reservations remain hard
structural constraints during inflation; unselected layered nodes use the
generic structural-layer producer.

## Generated recipe opportunities

`recipeOpportunities` is an inclusive count range, not a quota for three named
rooms. For each seed the composer chooses a deterministic subset of compatible
degree-two semantic nodes. The generated slot ID is diagnostic identity only;
compatibility still comes from the existing catalog seam:

- exact eligible role and beat;
- incident traversal degree;
- exact named ports or incident cardinal sockets;
- orientation support;
- topology-layer to recipe-layer agreement;
- port elevation, transition, landing, and headroom contracts; and
- current recipe validation and catalog membership.

A family may publish zero opportunities. Each published opportunity also makes
an independent deterministic authored-versus-generic decision before spatial
embedding. No compatible candidate, or a generic decision, leaves that node to
the generic room producer. The catalog still has a deterministic digest and an
all-generic result carries empty `RecipePlacement` and `RecipeResolution`
collections. Authoring preview still requires a compatible opportunity and
forces the preview recipe there without changing ordinary selection state.

## Structural layers and bindings

Layers are generated room-local offsets, never global storey numbers. The
composer currently names its generated upper storey `upper` at `+4u`; an edge
binding that layer meets the node at `node base + 4u`. Transition rise is always
derived between the two bound endpoint elevations.

If a compatible selected recipe maps `upper` to its own `upper` storey, the
candidate validator proves the offsets agree before placement. If no recipe is
selected at that node, Slice 3 realizes the bound storey generically as a full
storey, partial gallery, perimeter ring, or balcony and records it in the shared
`SurfaceField` and `PrismLedger`.

## Spatial overrides

Spatial values keep the previous density-relative vocabulary:

- a number is a fixed offset from resolved pitch;
- `{ "minDelta": n, "maxDelta": m }` is a rubber-sheet range around pitch;
- room-size deltas, envelope radius, neighbor bias, lattice slack clamp, and
  tier-seam settings remain optional;
- retired absolute names `columnGapCells`, `rowGapCells`, and `roomSizes` are
  rejected.

The generated coarse lattice goes through the same orientation and rubber-sheet
transform as before. Vacant lattice cells continue into the annex/crater path;
the family format changes who produces them, not who consumes them.

## Validation

Use **Tools > Dungeon Lab > Validate Topologies**. For each production family it
composes a deterministic sample and checks:

- compact-family schema and range bounds;
- connected graph, cycle rank, junctions, route rhythm, and node-count rails;
- 4u levels/layers and the family ceiling;
- bound edge rises and transition-kind coverage;
- generated recipe opportunity count and degree-two port contracts;
- cardinal, non-crossing coarse embedding and vista clearance;
- room-size/profile rules at every density; and
- achieved density samples through the existing generation pipeline.

The focused code fixture `BuildProceduralRouteCompositionSnapshot` additionally
scans production-family seeds and proves deterministic composition, bounded-path
use, one- and two-loop variants, zero-through-three recipe opportunities, all
transition kinds, resolved layer bindings, and the absence of exact graph fields
from production JSON.

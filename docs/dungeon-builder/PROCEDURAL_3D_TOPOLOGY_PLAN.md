# Procedural 3-D dungeon topology plan

Date recorded: 2026-08-02

Status: recorded implementation plan. This document records the architectural
destination and implementation order. It does not authorize a slice by itself;
implementation still begins only from an explicit owner-approved item.

This is deliberately not a status log. Per-run evidence belongs in
`DungeonLabReports/`, and closed implementation evidence belongs in
`docs/archive/`.

## Outcome

The production generator becomes **3-D topology first, with a guaranteed route
through that topology**.

It is not room first. It is also not "route first" in the current sense of an
exact authored 2-D route diagram. The generator first composes structural
layers, connectivity, shared voids, and typed relationships. It then embeds
rooms and corridors that realize those relationships.

```text
topology-family rules + seed
             -> generated 3-D RouteIntent
             -> existing embedding, rooms, ports and corridor routing
             -> existing SurfaceField, PrismLedger, transitions and openings
             -> existing renderer, navigation and collision export
```

Rooms and parts are generated from constraints. Recipes remain optional
landmarks; ordinary multi-level architecture must not require authors to create
dozens of room assets.

## Elevation invariant

The existing numerical system remains authoritative:

- one internal level is one world unit;
- structural topology uses the existing `MajorRiseLevels` 4u lattice;
- route nodes, structural room layers, external ports, corridors, bridges, and
  inter-room landings must resolve to structural levels on that lattice;
- a contracted local offset may exist inside one room or part for a dais,
  approach, or similar internal composition;
- a local offset may not own an external port, cross a room/part boundary,
  become a route-node elevation, or reconcile two structural regions.

This is a boundary rule, not a second elevation system. Existing measured local
transition contracts remain the only source of permitted local geometry.

## Why the current generator misses the target

Production currently:

1. selects an exact JSON topology containing a 2-D map, every node elevation,
   every edge, and three recipe slots;
2. copies it into `RouteIntent`;
3. embeds that 2-D lattice;
4. inflates every node into one 2-D room footprint;
5. inserts three mandatory authored layered recipes;
6. connects the footprints with 2-D paths; and
7. turns the result into column floors plus recipe-owned stacked overlays.

The downstream system is already multi-surface. The upstream ownership is not:
the topology validator explicitly requires a recipe slot for any generic node
that declares a real storey. Production layering is then accepted through a
quota of three named layered recipes and at least 48 stacked surfaces. Those
checks prove authored-content presence, not generated 3-D architecture.

The accurate description of the current pipeline is:

```text
exact 2-D topology template
  -> 2-D room inflation
  -> authored vertical episodes
  -> heightfield plus overlays
```

## Existing solutions that remain

This plan does not replace the landed downstream architecture:

- `SurfaceKey(cell, level)` and `SurfaceField` remain canonical after elevation.
- `AddSurface` and `AddCorridorSurface` remain the realization APIs.
- `PlanShadow` remains a derived pre-elevation projection, never vertical
  identity.
- `PrismLedger`, `LevelBand`, `OwnerKey`, headroom, and `OpenVolume` remain the
  volumetric occupancy and clearance system.
- `RouteNodeIntent.layers`, edge layer bindings, and typed
  `LevelCorridor`/`Stair`/`Stairwell`/`Bridge` edges remain the topology
  vocabulary.
- Reviewed stair contracts, Stair Forge, landings, footprints, mouths, and
  supports remain the only structural transition realization path.
- The existing abutting-doorway resolver and corridor candidate ladder remain.
- Recipe layers, exact ports, transitions, openings, and atomic validation
  remain for optional landmarks.
- Suspended slabs, rims, bridge decks, fall navigation, exact collision
  witnesses, and runtime landing remain unchanged consumers.
- Existing deterministic random scopes remain the only RNG mechanism.

Historical implementation is also reusable. Before commit `f5d8d4b3`, the
repository contained `TryAddSpine`, `TryAddBranch`, `TryRejoin`, `TryPublish`,
and a bounded coarse-path branch embedder. Recover and adapt that code at the
current `RouteIntent` seam; do not design a second graph family.

## Single-path rules

- `BuildSelectedRouteIntent` remains the only production topology entry point.
- The composer publishes the current `RouteNodeIntent[]` and
  `RouteTraversalIntent[]`; there is no parallel topology DTO.
- There is no `legacy/generated` switch, compatibility generator, renderer
  branch, or fallback to an exact topology after composition fails.
- Production topology assets change schema in one cutover. The old and new
  schemas are never selectable simultaneously.
- Generic and recipe producers write the same surfaces, prisms, transitions,
  and openings. They do not own parallel collections.
- A planned connection has one identity and one realization. Late passes may
  decorate it but may not invent another topology.
- An abstraction is extracted only from an existing implementation or after two
  real consumers demonstrate the same operation.

## Implementation slices

### 1. Structural/local elevation enforcement and intent ownership

Boundary: validation and ownership only; no intentional geometry change.

- Centralize the existing structural-level divisibility rule instead of
  repeating it in topology, recipe, connection, and transition validators.
- Enforce it on node bases, structural layers, external ports, corridor ends,
  bridge landings, and inter-room transitions.
- Prove that every local offset and transition remains inside one room/part and
  cannot own an external threshold.
- Remove the existing 2u connected-region allowance and the 2u sloped
  post-hoc-bridge allowance.
- Pass the already-carried `RouteTierRequirements.intent` directly into plan
  acceptance and reporting.
- Remove all semantic validation reads from the static `lastRouteIntent`.

Exit: existing production remains deterministic and every structural/local
boundary violation has a focused rejection test.

### 2. Generalize existing plan ownership

Boundary: generalize landed owners in place; add no generic side channel.

- Generalize `RecipeOpeningPlacement` into the plan's one opening record.
- Store planned openings once on `TieredLevelPlan`; recipe and generated
  topology producers both publish there.
- Make renderer rims, fall navigation, collision reporting, and validation read
  that one list instead of walking recipe resolutions.
- Generalize the existing vista/open-volume producer so a void can belong to
  generated topology.
- Make chamber subdivision consult the existing `OpenVolume` ledger so it
  cannot partition a generated atrium.

Exit: recipe output remains equivalent, while no downstream opening or void
consumer depends on recipe identity.

### 3. Generic structural room layers

Boundary: use current route layers, room footprints, thresholds,
`SurfaceField`, and `PrismLedger`; do not introduce another room-plan hierarchy.

- Add the missing generic layer producer to the existing surface realization
  stage.
- Derive connected structural surfaces from a room's generated footprint and
  its required layer-bound ports.
- Produce full storeys, partial galleries, perimeter rings, and balcony surfaces
  from footprint geometry and connectivity requirements.
- Register structural occupancy, support, clearance, and shared voids in the
  existing prism ledger.
- Keep `RoomZonePlan` and other small offsets as local finishing after structural
  surfaces exist.
- Remove the validator rule that only recipe rooms may declare storeys in the
  same change that lands the generic producer.

Exit: a focused fixture with no recipe produces a navigable multi-storey generic
room whose stacked surfaces are owned by generated topology.

### 4. Compose 3-D RouteIntent at the existing seam

Boundary: replace the current exact-diagram producer; do not add a selectable
alternative.

- Recover the historical spine/branch/rejoin/publish operations and bounded
  coarse-path embedding logic.
- Compose a connected critical spine, branches, rejoins, and loops using current
  deterministic random scopes.
- Assign structural node levels, room layers, edge kinds, and edge layer bindings
  on the 4u lattice before spatial embedding.
- Feed the result through the existing orientation, rubber sheet, room
  envelopes, inflation, vista reservation, ports, and corridor candidate ladder.
- Migrate the current topology assets from literal maps/nodes/edges to compact
  family constraints and semantic goals.
- Change the loader and all production assets together; retain old exact data
  only in Git history or isolated evidence fixtures.

Exit: production reads no authored node map, node elevations, or exact edge list.

### 5. Realize connection kinds and shared space

Boundary: generated topology owns architecture; realization reuses current
machinery.

- Same structural level plus abutting footprints becomes a direct doorway.
- Same level but separated becomes a routed corridor.
- A 4u/8u delta becomes an existing stair or stairwell contract.
- A declared bridge across a reserved void becomes an existing deck transition.
- An upper surface adjacent to a shared `OpenVolume` becomes a balcony through
  the existing rim renderer.
- Multiple structural surfaces around that volume become an atrium.
- A planned aperture with a valid lower catch becomes an existing directed fall
  edge; topology must also supply a fall-free return route.
- Delete the topology-inventing role of `AddAerialBridges`; planned bridges and
  a second random bridge scanner may not coexist.

Exit: every topology relationship resolves exactly once, with no architectural
connection invented after planning.

### 6. Make recipes optional and replace author quotas

Boundary: retain recipe capability but remove its responsibility for ordinary
vertical architecture.

- Generated topology supplies the required multi-level structure.
- Recipe selection becomes zero-or-more compatible landmark substitutions.
- Keep the current layered recipes and their exact validation as optional
  content and regression fixtures.
- Remove the exactly-three layered recipe requirement and the 48 stacked-surface
  quota only after the generic structural gate is live.
- Replace those checks with generated topology and traversal properties.

Exit: a dungeon with no layered recipe satisfies every structural, traversal,
rendering, navigation, and collision requirement.

## Final acceptance

Every accepted production dungeon must prove:

- all structural datums and external connections are on the structural lattice;
- every non-structural offset is contained by one room/part;
- at least two structural layers exist;
- generic, non-recipe surfaces contribute real stacked architecture;
- the bottom-to-top route is connected without relying on a fall;
- every planned edge resolves exactly once;
- every aperture has a valid catch and a non-fall return route;
- no surface or fill penetrates an `OpenVolume`;
- headroom, footprints, mouths, landings, and supports remain prism-valid;
- navigation edges have exact physical witnesses and remain connected;
- repeat generation is deterministic; and
- a fixed seed corpus demonstrates direct adjacency, corridors, stairs,
  stairwells, bridges, atriums, balconies, and pits without counting recipe IDs.

Automated plan, rendering, navigation, and collision evidence is followed by
normal-Editor visual review. Unity batch mode is not used.

## Deletion ledger

The migration is incomplete while any obsolete owner remains:

- exact production topology maps and literal node/edge tables;
- the rule that a generic storey requires a recipe slot;
- mandatory three-recipe production selection;
- the 48 recipe-created stacked-surface quota;
- semantic reads from `lastRouteIntent`;
- recipe-only opening storage;
- topology-inventing aerial bridges;
- documentation calling the current exact-diagram system generated 3-D
  topology; or
- current authoring guidance that makes authored layered rooms the ordinary
  route to vertical architecture.

## Explicit next item

None until the owner approves an implementation slice. When approved, the
first bounded item is slice 1 only: structural/local elevation enforcement and
`RouteIntent` ownership cleanup, with no intentional geometry change.

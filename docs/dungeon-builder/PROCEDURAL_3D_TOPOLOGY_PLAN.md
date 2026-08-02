# Procedural 3-D dungeon topology plan

Date recorded: 2026-08-02

Status: all six owner-approved implementation slices landed 2026-08-02.
Non-Unity gates pass; normal-Editor acceptance and a post-cutover rebuild/export
remain. See the
[implementation closeout](../archive/2026-08-procedural-3d-topology/CURRENT_STATUS.md).

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

Rooms and parts can be generated from constraints or supplied through the
existing recipe workflow. Authored ordinary rooms, connectors, episodes, and
landmarks remain first-class production content; ordinary multi-level
architecture must simply not require authors to create enough assets to cover
every generated node and edge.

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
- `DungeonRecipeAsset`, its current `Connector` and `Episode` kinds, catalog
  membership, enable/disable policy, authoring window, deterministic preview,
  content digest, exact placement, and atomic validation remain the sole
  authored-room and authored-connector workflow.
- Recipe layers, zones, exact footprints, ports, transitions, openings,
  reservations, and visual implementations remain structural inputs when an
  authored module is selected.
- Suspended slabs, rims, bridge decks, fall navigation, exact collision
  witnesses, and runtime landing remain unchanged consumers.
- Existing deterministic random scopes remain the only RNG mechanism.

Historical implementation is also reusable. Before commit `f5d8d4b3`, the
repository contained `TryAddSpine`, `TryAddBranch`, `TryRejoin`, `TryPublish`,
and a bounded coarse-path branch embedder. Recover and adapt that code at the
current `RouteIntent` seam; do not design a second graph family.

## Authored rooms and connectors

Generated topology supplies **opportunities**, not a prohibition on authored
content. The current recipe workflow is generalized in place:

1. The composed `RouteIntent` declares semantic room and connection
   opportunities: role, beat, degree, structural layers, incident edge kinds,
   and required port levels.
2. The existing catalog and validator find zero or more compatible
   `DungeonRecipeAsset` modules.
3. Selection occurs before spatial embedding, as it does today, so a selected
   recipe's exact footprint, ports, layers, transitions, and reservations are
   structural anchors for room placement and corridor routing. It is never
   fitted into finished geometry or repaired afterward.
4. The generic room/connector producer realizes every unclaimed opportunity.
5. Both authored and generic producers publish into the same `SurfaceField`,
   `PrismLedger`, transition, opening, renderer, navigation, and collision path.

Compatibility continues to be derived from explicit contracts: catalog
availability, role, beat, connection degree and kind, layer requirements, port
orientation/elevation, landing and headroom requirements, footprint fit, and
current validation. A flat authored room, multi-layer room, connector,
vestibule, junction, approach, episode, or landmark may therefore participate
where its existing contract fits.

"Optional" means the generator remains complete with zero selected recipes. It
does not mean recipes are rare, decorative, landmark-only, or selected after
generation. This migration does not introduce a second prefab registry, room
asset type, connector schema, catalog, preview tool, or placement pipeline. The
current `Connector` and `Episode` kinds remain unless a later owner-approved
item demonstrates a capability they genuinely cannot express.

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
- The current recipe catalog, validator, authoring window, preview, selection,
  placement, and resolution pipeline remains the one authored-content path.
- Authored modules are selected before embedding and constrain it; there is no
  post-embedding recipe substitution path.
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
- Publish compatible authored-room and authored-connector opportunities through
  the existing recipe-slot intent and catalog-selection seam, generalized from
  three fixed required slots to zero or more generated opportunities.
- Resolve selected recipes before embedding so their exact footprints, ports,
  layers, and reservations remain structural constraints.
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

### 6. Generalize the existing recipe workflow and replace author quotas

Boundary: retain the current recipe workflow as the sole authored-content path,
while removing its responsibility to cover ordinary generated architecture.

- Generated topology supplies the required multi-level structure.
- Generalize the current fixed required-slot selection to zero or more
  compatible authored room, connector, episode, or landmark opportunities.
- Keep `DungeonRecipeAsset`, the `Connector` and `Episode` kinds, catalog
  service, enable/disable controls, authoring window, deterministic preview,
  validator, content digest, `RecipePlacement`, and `RecipeResolution`.
- Keep selection before embedding; selected exact footprints and ports constrain
  embedding and corridor routing rather than replacing completed geometry.
- Preserve the current layered recipes and their exact validation as selectable
  production content and regression fixtures.
- Let the generic producer fill every opportunity for which no recipe is
  selected or compatible.
- Remove the exactly-three layered recipe requirement and the 48 stacked-surface
  quota only after the generic structural gate is live.
- Replace those checks with generated topology and traversal properties.

Exit: current authored rooms and connectors still validate, preview, select,
place, render, navigate, and export through their existing workflow; a dungeon
with no selected recipe also satisfies every structural, traversal, rendering,
navigation, and collision requirement.

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
- focused production evidence proves both sides of the same path: compatible
  authored rooms/connectors can be selected before embedding, and the same
  opportunities resolve generically when no recipe is selected.

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
- fixed-slot authoring guidance that mistakes three mandatory layered episodes
  for the recipe workflow itself or makes authored layered rooms the only route
  to vertical architecture.

## Explicit next item

None. Slices 1–6 are implemented, and this plan defines no follow-on slice.
Further feature work requires a new owner-approved plan. The remaining closeout
is verification rather than implementation: normal-Editor acceptance followed
by a post-Slice-6 rebuild/export and intentional disposition of its generated
payloads. Unity batch mode is not used.

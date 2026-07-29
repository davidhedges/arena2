# Layered 3D topology for the random dungeon generator

Status: design of record for evolving the generator from a single-surface
heightfield to multiple independently traversable surfaces that may overlap in
plan space. Not yet implemented; no phase is in progress.

Date: 2026-07-29

Scope of the investigation behind it: `Assets/Arena/Editor/Dungeons/RandomDungeon`
(28 files, ~51k lines), the seven topology JSON files, the recipe schema and
validator, the renderer, the collision exporter, and the server-side
movement/collision path that consumes the export. No code was modified.

Labels used throughout: **[Fact]** verified in the repository with a citation ·
**[Inference]** my reading, not directly stated · **[Proposed]** new design ·
**[Deferred]** deliberately out of scope.

Read [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) and
[`GLOSSARY.md`](GLOSSARY.md) first; this document extends that vocabulary rather
than replacing it.

---

## 1. What exists today

### 1.1 Units — confirmed

| Quantity | Value | Source |
|---|---|---|
| Horizontal cell | **4 world units** | `CellSize = 4f`, `ElevationEdgeModel.cs:40` |
| Elevation level | **1 world unit** | `LevelHeight = 1f`, `StairForge.cs:37` |
| Major elevation change | **4 levels = 4u** | `MajorRiseLevels = 4`, `DungeonLabGenerator.cs:51` |
| Double-major | 8 levels = 8u | `DoubleMajorRiseLevels = 8`, `DungeonLabGenerator.cs:52` |
| Current vertical ceiling | **24 levels = 24u = 6 major changes** | `MaxGeneratedLevel = 24`, `DungeonLabGenerator.cs:45` |
| Minimum stacked clearance | 3 levels = 3u | `MinHeadroomLevels = 3`, `DungeonLabGenerator.cs:58` |
| Abyss skirt | 20 levels below the lowest floor | `AbyssDepthLevels = 20`, `ElevationEdgeModel.cs:55` |
| Wall masonry course | 2u — walls are composed of whole courses | `ElevationEdgeModel.cs:47` |
| Player capsule | radius 0.45u, height 1.8u | `server/src/world_collision.rs:5034` |

**Ten full major elevation changes = 40 levels = 40 world units** — a
`MaxGeneratedLevel` of 40, up from 24.

**[Proposed]** Confirm this reading: if "major" meant the double-major step the
envelope is 80u, which changes the abyss skirt and the wall-course budget
materially. This document is designed against 40.

Two consequences worth stating up front, because they bound everything else:

- A 4u major rise gives ~2.2u of true clearance over a 1.8u player. **One major
  change is the minimum viable stacking pitch, and it is tight.** 8u is the
  comfortable one. So a 40u envelope supports at most ~10 stacked surfaces in
  the pathological case and realistically 5–7.
- Topology node levels are validated to `[0, MaxGeneratedLevel]` and `% 4 == 0`,
  and **`anchors.top` must equal `MaxGeneratedLevel` exactly**
  (`DungeonRouteTopologyValidator.cs:289`, `:366`). Raising the constant to 40
  therefore **invalidates all seven shipped topology files at once.** That is a
  migration item, not a constant edit (§10).

### 1.2 The canonical model

**[Fact]** The pipeline is `RouteIntent → embedding → footprints → corridors →
DungeonLayout → elevation/transitions → TieredLevelPlan → ElevationEdgeModel →
GameObjects → collision export`.

The two canonical structures:

```csharp
DungeonLayout    { HashSet<Vector2Int> floorCells; List<RoomFootprint> rooms; ... }   // cs:7700
TieredLevelPlan  { Dictionary<Vector2Int,int> cellLevels; List<TransitionEdge> transitions; ... } // cs:8343
```

`Dictionary<Vector2Int, int>` is the load-bearing sentence in this whole design.
**The canonical elevation model is a heightfield: one plan coordinate, one
elevation, one floor.** Everything derives from it:

| Derived thing | Keyed on | Site |
|---|---|---|
| Floor prefab placement | `Vector2Int` | `ElevationEdgeModel.cs:371` |
| Walls / cliffs / railings | `Vector2Int` + 4 cardinals, from `level` vs `neighbourLevel` | `ElevationEdgeModel.cs:2287` |
| Abyss base | `MinFloorLevel(levels) - 20`, one global value | `ElevationEdgeModel.cs:2310` |
| Room ownership | `IReadOnlyDictionary<Vector2Int,int> cellRoomIds` | `ElevationEdgeModel.cs:10351` |
| Reservations | five `HashSet<Vector2Int>` | `DungeonLabGenerator.cs:6404` |
| Recipe zones | `Vector2Int offset/size` + one `relativeLevel` | `DungeonRecipeAsset.cs:38` |
| Canonical hash | sorted `cell → level` pairs | `DungeonLabGenerator.Batch.cs:4937` |
| Traps, gateways, promontories, corners | `Vector2Int` | throughout |

### 1.3 The one place multi-surface already exists — and it is more encouraging than it looks

**[Fact]** Aerial bridges are the sole existing overlapping-traversal case, and
they work by *not being surfaces*:

- A bridge is a `TransitionEdge` carrying a synthesized deck set piece
  (`DungeonLabGenerator.cs:4666`). Its span cells are `footprintCells` on the
  edge, never keys in `cellLevels`.
- Its deck height lives in a **side table** `spanDeckLevels`, local to
  `TryBuildCellLevelField` and *not carried in `TieredLevelPlan`* — the batch
  report recomputes it from transitions (`Batch.cs:4435`).
- Clearance is one gate over that side table: `deckLevel - floorLevel >= 3`
  (`DungeonLabGenerator.cs:2992`).
- The renderer carries three narrow accommodations: `aerialDeckCellLevels`,
  `bridgeSpanEdges`, `bridgeFloorBlockedCells` — the last existing precisely so
  "bridge decks float above their cells, so the terrain floor beneath them must
  still render" (`ElevationEdgeModel.cs:377`).
- Bridges over **room interiors are explicitly rejected**
  (`DungeonLabGenerator.cs:4550`); the design note says full room overflight
  "would require moving `ChooseEnclosedRooms` into the plan — deliberately
  deferred" ([`stair_forge_design.md`](stair_forge_design.md) decision 30).
- Caps: `MaxAerialBridgesPerDungeon = 2`, `MinAerialBridgeLevel = 3`, span 2–8
  cells, endpoint delta ≤ 2u (`DungeonLabGenerator.cs:4358`).

**Three findings make this a much shorter road than the heightfield suggests.**

**Finding 1 — the traversal graph is already multi-surface.**
`PortGraphNode.Floor(cell, level)` keys on `F:{x},{y},L{level}`
(`DungeonLabGenerator.cs:8060`). Two surfaces at one plan coordinate are already
two distinct nodes in `FloorStairPortGraph`. The graph is not the bottleneck;
its *source* is, because it is built by iterating `cellLevels`
(`DungeonLabGenerator.cs:6849`) which can only offer one level per cell.

**Finding 2 — the runtime already supports standing on stacked surfaces and
falling between them.** The server resolves ground as *the highest walkable top
at or below `current_y + step-up`* (`server/src/world_collision.rs:1464`);
walking off a ledge transitions to airborne and lands on whatever surface it
crosses (`server/src/game_loop.rs:1480-1530`). The dungeon exports boxes + mesh
instances, **not** a heightfield (no `random_dungeon.heightfield.shared.json`
exists). **Collision and runtime movement need no structural change.** Falling
into a pit and landing on a lower layer is a geometry problem, not a physics
problem.

**Finding 3 — an overlapping-traversal proof fixture already exists.**
`BuildStackedCrossingFixture` builds a bridge at level 4 over a corridor at
level 0, then probes the built GameObjects for a lower collider, an upper
collider, and open clearance between them at the same plan coordinate
(`DungeonLabGenerator.StackedCrossingFixture.cs`), asserted by
`Assets/Arena/Tests/Editor/DungeonLabStackedCrossingTests.cs`. This is the
evidence shape the first proof should extend, not reinvent.

---

## 2. Limitations of the current canonical model

Ordered by how hard they block the goal.

| # | Limitation | Evidence | Blocks |
|---|---|---|---|
| **L1** | **One elevation per plan coordinate.** `cellLevels` is `Dictionary<Vector2Int,int>`. | cs:8345 | Everything. A plan coordinate *cannot* express two surfaces. |
| **L2** | **Reservations are 2-D sets.** `StairPlacementLedger` is five `HashSet<Vector2Int>`. | cs:6404 | Two stairs at different elevations over one coordinate conflict spuriously. No clearance volume can be reserved. |
| **L3** | **Room ownership is per plan cell.** `cellRoomIds` maps `Vector2Int → int`. | ElevationEdgeModel.cs:10351 | A room cannot own two layers; a bridge over a room has no owner for its walls/railings. |
| **L4** | **Wall grammar is a 4-neighbour heightfield difference, with one global abyss base.** | ElevationEdgeModel.cs:2287, :2310 | A gallery's cliff face drops 20u past the chamber below it. There is no notion of "what is under this edge". |
| **L5** | **No underside.** Nothing emits a soffit; the design notes deck undersides "may read open from below". | `stair_forge_design.md` decision 31 | Standing under a bridge inside a room shows a hole in the sky. |
| **L6** | **Recipe zones are a heightfield too.** A recipe cell's level is `max` over overlapping zones; `Elevated` requires `relativeLevel > 0`; base level is derived from the *first* port. | DungeonRecipeValidation.cs:363, :633; Recipes.cs:1791 | Multi-layer authored rooms. No sunken zone is authorable at all. Ports at multiple elevations exist in schema but all resolve against one base. |
| **L7** | **A route node has exactly one elevation.** `RouteTopologyNode.level` is one `int`. | DungeonRouteTopology.cs:166 | Two routes cannot enter the same room at different heights. |
| **L8** | **"Void" is undifferentiated.** Void = absence from `cellLevels`. Every floor edge facing absence drops to the abyss base. There is no pit, hole, aperture, or opening object. | ElevationEdgeModel.cs:2430 | No way to distinguish an opening to a lower layer from the lethal exterior. |
| **L9** | **Bridge decks are not surfaces.** They exist as transition footprints + a side table dropped before the plan is published. | cs:4805, cs:8343 | A bridge cannot host traps, encounters, nav nodes, its own railings-by-room, or a second bridge below it. |
| **L10** | **Bridges over rooms are rejected by rule.** | cs:4550 | Scenario 2 and 5 as literally stated (§12). |
| **L11** | **Connectivity checks are 2-D.** `IsConnected(layout.floorCells)` operates on a plan-cell set. | Validation.cs:138 | Two disjoint stacked surfaces read as connected. The check becomes a lie rather than a check. |
| **L12** | **Fill/density metrics count plan cells.** `floorFillPercent` = floor ÷ bounding box. | [`density-scale-design-2026-07-27.md`](density-scale-design-2026-07-27.md) §2 | A cell with two surfaces counts once; the density dial's meaning drifts silently. |
| **L13** | **The canonical hash is `cell → level`.** | Batch.cs:4937 | Cannot express a second surface; hashing must rebaseline. |
| **L14** | **Directed traversal is not representable.** Every port-graph edge is added symmetrically. | cs:8130 | A fall is not expressible; reversibility cannot be checked because irreversibility cannot be stated. |

**[Inference]** L1–L4 are one problem wearing four hats: the system models *plan
coordinate* as the identity of a place. Everything else follows from replacing
that identity.

---

## 3. The minimum conceptual change

> **Replace "cell" with "surface" as the identity of a walkable place.**
> A surface is `(plan cell, level)`. Everything that today keys on `Vector2Int`
> and means *a walkable place* keys on a surface instead. Everything that keys
> on `Vector2Int` and means *a column of space* stays as it is.

That single substitution is the whole conceptual change. It is small because
**the port graph already uses exactly this key** (§1.3 Finding 1) — this
promotes an existing internal identity to a canonical one rather than inventing
one.

### 3.1 The data model

```csharp
// [Proposed]
readonly struct SurfaceKey { Vector2Int cell; int level; }        // canonical identity

sealed class Surface {
    SurfaceKey key;
    int roomId;                 // which architectural room owns this surface
    int layerOrdinal;           // 0-based rank among surfaces at this cell, ascending level.
                                // A LOCAL disambiguator for reports and hashing. NEVER a storey.
    SurfaceKind kind;           // Floor | Deck | Landing | Ledge | Stair (occupied)
    int supportBase;            // the level this surface's cliff faces drop to (§7)
}

sealed class SurfaceField {                   // replaces Dictionary<Vector2Int,int>
    IReadOnlyList<Surface> surfaces;                        // canonical, sorted (x, y, level)
    IReadOnlyDictionary<Vector2Int, int[]> byCell;          // ascending level
    bool IsSingleLayer { get; }                             // every cell has exactly one surface
    IReadOnlyDictionary<Vector2Int,int> AsHeightField();    // valid iff IsSingleLayer
}
```

`AsHeightField()` is the migration lever: while `IsSingleLayer`, every existing
consumer keeps working byte-identically, and the canonical hash keeps its
current shape. That makes the model change output-neutral, which is what makes
it safe to land before anything interesting is built on it (§9 Phase A).

**One floor truth.** `DungeonLayout.floorCells` becomes
`SurfaceField.PlanCells()` — derived, not stored. This deliberately closes the
architecture review's **H2** ("two divergent representations of where the floor
is") rather than adding a third.

### 3.2 The traversal graph

```csharp
// [Proposed]
enum TraversalKind { Lateral, Step, Stair, Bridge, Fall, Drop }

readonly struct TraversalEdge {
    SurfaceKey from, to;
    TraversalKind kind;
    bool directed;              // true only for Fall (and Drop, if adopted)
    int riseLevels;
    string transitionId;        // back-reference to the TransitionEdge that realizes it
}
```

- `Lateral` — same level, cardinally adjacent, same-room or through an opening.
  Bidirectional.
- `Step` — the existing rise-1 seam strip. Bidirectional.
- `Stair` / `Bridge` — an existing `TransitionEdge`. Bidirectional.
- **`Fall` — directed, downward only.** From a surface with an `Aperture`
  opening (§5) to the catch surface beneath it.
- **`Drop`** — an optional one-way step-down over a ledge the player can walk
  off. **[Deferred]** — the runtime already permits it
  (`server/src/game_loop.rs:1486`), but modelling every ledge as an edge
  explodes the graph. Start by modelling only *declared* falls.

Plus one non-traversal relation, because seeing must be distinguishable from
reaching:

```csharp
readonly struct VisibilityRelation { SurfaceKey from, to; }   // derived, never authored
```

**`Sees` is derived from openings and geometry, not planned.** Sightlines
between layers should emerge from openings and overlapping geometry; the
existing `RouteVistaIntent` remains the only *authored* sightline. The relation
exists in the model solely so validation can say "these layers see each other
but do not connect", which is a legitimate design outcome and must not be
confused with a connectivity failure.

### 3.3 The one new invariant that carries the pit design

> **The traversable surface graph must be strongly connected.**

Treat `Lateral`/`Step`/`Stair`/`Bridge` as bidirectional and `Fall` as one-way,
then require that every surface reaches every other. That single condition
subsumes:

- "every reachable lower pit area must provide a route back up",
- "a fall may be directed while the branch remains reversible",
- today's `IsGloballyConnected` check (`DungeonLabGenerator.cs:8150`), which
  becomes the degenerate single-layer case,
- and it makes a one-way trap a *rejection* rather than a playtest discovery.

Lethal void is **not** an edge. It is a sink, outside the traversal graph
entirely, so it can neither satisfy nor violate connectivity.

---

## 4. Room ownership across layers

**[Proposed]** A room owns *surfaces*, not cells.

```csharp
sealed class RoomFootprint {
    IReadOnlyList<RoomLayer> layers;      // ordered by relative elevation, LOCAL to the room
    HashSet<Vector2Int> planCells;        // derived union — the room's plan shadow
    RoomVolume volume;                    // optional; see §6
}
sealed class RoomLayer {
    string layerId;                       // authored, room-local ("lower", "gallery", "catwalk")
    int relativeLevel;                    // from the room's base
    IReadOnlyList<RectInt> parts;         // today's multi-rect footprint, per layer
    IReadOnlyList<Opening> openings;      // §5
}
```

- `cellRoomIds : Vector2Int → int` becomes `surfaceRoomIds : SurfaceKey → int`.
  That is the change that lets partitions, gateways, railings and enclosure
  decisions belong to the right room when two rooms occupy one coordinate.
- **A room is architectural, not navigational.** A lower chamber, upper gallery,
  bridge, openings and internal vertical connections are one room. Two routes
  entering an atrium at different heights enter *the same room* through
  *different layers*.
- `RoomFootprint.Overlaps` (`DungeonLabGenerator.cs:7568`) currently rejects any
  plan-rect intersection during inflation. **[Proposed]** it becomes a
  *volumetric* test: two rooms may share plan cells iff their occupied vertical
  bands are separated by at least `MinHeadroomLevels`, **and** at least one of
  them declares the shared column as part of a reserved open volume or a bridge
  span. Without that second clause generic rooms would start silently stacking,
  which is a variety regression, not a feature.
- **Layer ordinals are room-local and never global.** There is no `GetFloor(n)`.
  A room's "gallery" is +8 from *its own* base; that gallery is at absolute
  level 12 in one seed and 28 in another. Reject any API that names a global
  storey.

---

## 5. Pit openings versus exterior void

Today the two are literally the same thing: an absent key in `cellLevels`.

**[Proposed]** Introduce an explicit `Opening` owned by a surface layer:

```csharp
enum OpeningKind {
    Aperture,   // passable downward; a catch surface is DECLARED and PROVEN
    Void,       // lethal exterior; nothing may exist in its fall column
    Window      // visible, not passable (railing, grille, or clearance too small)
}
sealed class Opening {
    OpeningKind kind;
    Vector2Int[] cells;         // removed from the owning layer's walkable set
    SurfaceKey? catchSurface;   // required for Aperture, forbidden for Void
    string returnRouteId;       // required for Aperture — the edge that climbs back
}
```

### The rules

| | `Aperture` | `Void` |
|---|---|---|
| Catch surface | **required**, and must underlie *every* cell of the opening | **forbidden** — no surface anywhere in the fall column |
| Graph effect | one `Fall` edge per opening → catch surface | none; a sink outside the graph |
| Reversibility | strong connectivity must hold with the fall directed — the catch surface's component must reach the opening's component by non-fall edges | n/a |
| Fall height | ≥ `MinHeadroomLevels`, ≤ a `MaxSurvivableFallLevels` cap | unbounded |
| Renderer | interior cliff faces drop to `catchSurface.level`; soffit under the opening's rim | cliff faces drop to `abyssBase`, as today |
| Rejection codes | `APERTURE_NO_CATCH_SURFACE`, `APERTURE_FALL_TOO_SHALLOW`, `APERTURE_NO_RETURN_ROUTE` | `VOID_OPENING_OBSTRUCTED` |

### Backwards compatibility, stated precisely

**[Fact + Proposed]** Today, an absent cell adjacent to floor produces an abyss
cliff. Under the new model the *default for an absent, undeclared cell is
unchanged*: exterior void. An `Aperture` is a **new, explicitly declared
object** — a hole authored into a layer, not an emergent gap. This keeps every
existing seed's void semantics intact and makes the distinction a matter of
declaration rather than of inference from geometry.

The existing `AbyssDepthLevels = 20` skirt keeps its meaning: `Void` still drops
20u below the lowest floor.

**[Deferred]** Void death is a runtime gameplay rule — the server currently
falls forever (`ground_at` returns `None` and gravity integrates unbounded,
`server/src/game_loop.rs:1493`). The generator's job in phase one is only to
*declare* which openings are lethal so the runtime has something to key on
later. Deliberate lethal-void floorplan generation is also deferred.

### Optional reversible branch — the concrete shape

The canonical pit scenario becomes, in the model:

```
upper-surface  --Lateral-->  aperture rim
aperture       --Fall---->   lower-chamber-surface        (directed, one-way)
lower-chamber  --Lateral-->  lower-corridor  --Stair-->   upper-surface
```

Strong connectivity holds. The fall is one-way. The branch is reversible.
Nothing about this requires the fall to be bidirectional, and nothing about it
requires a global floor number.

---

## 6. Overlapping footprint and clearance volumes

**[Proposed]** `StairPlacementLedger`'s five `HashSet<Vector2Int>` become one
prism ledger:

```csharp
readonly struct Prism { Vector2Int cell; int minLevel; int maxLevel; PrismKind kind; }
enum PrismKind { Footprint, Landing, Mouth, Clearance, OpenVolume, Support }
```

Two prisms conflict iff they share a cell **and** their level bands intersect.
Today's semantics are the special case where every band is `[-∞, +∞]`.

Payoffs, in order of importance:

1. **Two transitions at one coordinate stop conflicting.** This is the single
   change that makes overlapping traversal *plannable* rather than merely
   renderable. Today `ConflictsWith` rejects a stair whose footprint touches a
   coordinate any other stair claimed, at any height.
2. **`OpenVolume` becomes expressible.** This is the reservation an atrium
   needs: "nothing may emit floor, wall, room fill, annex, mop-up, dressing, or
   a set piece inside this prism." It is a *reservation*, not geometry —
   consistent with the glossary definitions of `Reservation` and
   `ProtectedCirculation`.
3. **Headroom generalizes.** `TryValidateSpanHeadroom` + the `spanDeckLevels`
   side table (and its duplicate formula in `Batch.cs`, review finding **M4**)
   both delete. The rule becomes one sentence over the ledger: *for every
   surface S, the prism `(S.cell, S.level, S.level + MinHeadroomLevels)` must
   not intersect any `Support` or `Footprint` prism belonging to another
   surface.* One rule, one call site, no side table dropped before the plan is
   published.
4. **The existing authored-void vocabulary extends cleanly.** The density design
   already names "authored void" that survives every density setting — vista
   lane, bridge spans, stairwell shafts
   ([`density-scale-design-2026-07-27.md`](density-scale-design-2026-07-27.md)
   §4.1). `OpenVolume` is the same idea given a vertical extent, and it must be
   added to the density metric's exclusion list at the same time, or density 5
   will try to fill the atrium.

**Invariant:** the density dial's fill passes (annex, mop-up, backstop) must
query the prism ledger, not the plan-cell set. Missing this is the most likely
way a first implementation silently fills an atrium.

---

## 7. Renderer and collision export

### 7.1 Renderer — the largest mechanical change, and the main risk

**[Fact]** `BuildWallEdges` iterates `levels` and, for each cell and each of
four cardinals, compares `level` to `neighbourLevel`, emitting an interior edge,
a retaining wall, or a cliff to a single global `abyssBase`
(`ElevationEdgeModel.cs:2287-2451`). Floors, corners, gateways, railings,
partitions, traps and promontories all iterate the same dictionary.

**[Proposed]** Three changes, in dependency order:

1. **Iterate surfaces, not cells.** `foreach (var item in levels)` →
   `foreach (Surface s in field.surfaces)`. A surface's lateral neighbour is
   *the surface at the neighbouring cell whose level is nearest to its own*,
   subject to a compatibility rule (same level → interior/partition; small |Δ| →
   retaining wall; otherwise → not a lateral neighbour and the edge is a cliff).
   This is a local rewrite of a well-understood loop, not a new renderer.
2. **`abyssBase` becomes `supportBase(surface, direction)`.** Instead of one
   global value: the drop target of a cliff face is *the highest surface
   strictly below it in the adjacent column*, or the abyss base if none. This is
   what stops a gallery's cliff from spearing 20u through the chamber
   underneath it. It is the single most important renderer change and it is
   ~30 lines at one site.
3. **A soffit pass.** Every surface that is not the lowest in its column needs
   an underside. **[Fact]** this is a known open item — "the pack has no flat
   under-deck cap family measured yet — deck undersides may read open from
   below" ([`stair_forge_design.md`](stair_forge_design.md) decision 31). Today
   that is cosmetic because you can only pass under a bridge in a corridor. Once
   you can stand under a gallery inside a room, it is load-bearing. **This is
   the one place the design depends on unmeasured art content**, and it should
   be measured before Phase C commits.

Edge keys (`EdgeKey`, `OpenEdgeKey`, `WallEdge` at `(x, z, direction)`) gain a
level discriminator. **[Inference]** this is mechanical but wide — these types
thread through corner selection, gateway sockets, shell placement and trap
placement. Budget accordingly; it is the bulk of Phase C.

**Compatibility lever:** while `field.IsSingleLayer`, `supportBase` returns the
global abyss base and the surface loop degenerates to today's cell loop. Every
existing seed renders byte-identically. That is the property to gate Phases A
and B on.

### 7.2 Collision export — no structural change

**[Fact]** `GameplayCollisionExporter` scrapes scene colliders into boxes + mesh
instances; the dungeon has no exported heightfield; the server resolves the
highest walkable top ≤ `current_y + step-up` and integrates gravity on a ledge
walk-off. **Stacked surfaces work at runtime today**, which the existing
stacked-crossing fixture already demonstrates by probing colliders at both
heights over one plan coordinate.

Three requirements rather than changes:

- Soffits, columns and deck undersides must **not** be marked `walkable_top`, or
  a player under a bridge will be snapped onto its underside.
- The lower surface under a deck must still emit its floor — already handled for
  aerial decks via `bridgeFloorBlockedCells` (`ElevationEdgeModel.cs:377`);
  generalize it to all stacked surfaces.
- **[Fact]** the dungeon collision bake is not byte-stable across rebuilds, so
  it can never serve as the output-neutrality diff. Neutrality must be proved on
  the plan hash and the scene, not the payload.

**[Fact]** the dungeon exports with `reuseMovementCollisionForQueries: true`, so
its query geometry *is* its (deliberately oversized) movement geometry — the
opposite of the project-wide LOS rule in `CLAUDE.md`. Stacked surfaces make this
more consequential, not less: a bridge deck will block LOS to the chamber below
it. **[Deferred]** — flagged because it interacts, not proposed for change here.

---

## 8. Integration with the existing pipeline

### 8.1 Route planning and spatial embedding

**[Proposed]** minimal, additive topology-file changes:

```jsonc
"nodes": {
  // today: [ id, role, beat, level, order ]
  // proposed: an optional 6th element — the node's additional layers, RELATIVE to `level`
  "E": ["great-atrium", "grand-room", "reveal", 8, { "main": 4 },
        { "layers": { "floor": 0, "gallery": 8, "catwalk": 16 } }]
},
"edges": [
  ["D", "E", "LevelCorridor"],                          // binds to E's default layer
  ["E", "F", "Stair",   { "fromLayer": "gallery" }],    // binds to a named layer
  ["E", "G", "Bridge",  { "fromLayer": "catwalk" }]
]
```

- A node's `level` remains its **base**; layers are relative offsets, so nothing
  acquires a global storey number.
- Edge rise derives from `(fromNode.level + fromLayer.offset)` to
  `(toNode.level + toLayer.offset)` — the existing derivation, one term wider.
  The ±4/±8 grammar is unchanged.
- The embedder is untouched: layers do not change a node's lattice position or
  its plan footprint. **This is the property that keeps the rubber sheet,
  lane-gap and fill machinery working**
  (see [`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md)).
- `RequiredTransitionCorridorCells`
  (`DungeonLabGenerator.TransitionReservation.cs:50`) already keys on
  `(kind, rise)` and prices larger rises without change — but the reviewed
  contract set may have no entry beyond rise 8, in which case it returns *the
  widest measured requirement* and the seed fails by name later. That is correct
  behaviour, and it means **an envelope raise to 40u does not by itself require
  bigger single rises** — it requires more of them.

### 8.2 Recipes — multi-layer authored rooms

**[Proposed]** three schema additions, all additive and defaulting to today's
behaviour:

```csharp
class DungeonRecipeZone {
    string layerId = "";            // NEW — empty means the base layer (today's behaviour)
    // relativeLevel becomes relative to the LAYER, not the recipe base
}
class DungeonRecipePort {
    string layerId = "";            // NEW — which layer this entrance belongs to
    // relativeLevel keeps its meaning within that layer
}
class DungeonRecipeLayer {           // NEW
    string layerId; int relativeLevel; bool isBase;
}
enum DungeonRecipeZoneKind {
    ..., OpenVolume                 // NEW — reserved vertical volume (§6)
}
```

Consequences to design around, all of which follow from L6:

- **`baseLevel` derivation must change.** Today it is
  `firstPortLevel - firstPortContract.relativeLevel`
  (`DungeonLabGenerator.Recipes.cs:1791`) — the whole room is anchored off port
  zero. **[Proposed]** anchor off the *base layer's* first port; other layers
  derive from their declared offsets; every bound port is then validated at
  `baseLevel + layer.relativeLevel + port.relativeLevel`. This is what makes "a
  route may bind to any compatible declared entrance elevation" true rather than
  aspirational.
- **`RelativeLevelAt` must become layer-scoped.** Today it is `max` over
  overlapping zones (`DungeonRecipeValidation.cs:633`); that `max` *is* the
  heightfield assumption inside the recipe schema.
- **Elevated zones must be allowed to go negative** (or gain a `Sunken` kind) —
  `relativeLevel <= 0` is currently a validation error
  (`DungeonRecipeValidation.cs:363`). A lower chamber below a room's entry layer
  is not authorable today.
- **New validation layer: `RECIPE_LAYER_CONNECTIVITY`.** A recipe that declares
  its layers are connected must prove it, over its own declared transitions,
  before it can enter the catalog. `DungeonRecipeValidator` is already the
  best-modelled validator in the codebase — layered, typed findings with codes.
  Extend it; do not build a second one.
- **[Deferred]** authored per-layer wall heights — schema v1 already cannot
  preserve these ([`GENERIC_ROOM_FAMILY_BRIEF.md`](GENERIC_ROOM_FAMILY_BRIEF.md)).

### 8.3 Stairs, landings, gateways, walls, railings, openings — ownership

**[Proposed]** ownership rules, stated so no two systems can both claim a face:

| Element | Owner | Rule |
|---|---|---|
| Stair body | its `TransitionEdge` | unchanged; footprint prism now has a level band |
| Landing | the transition, shared per the ledger's existing rule ("landings may share other landings but never a footprint") | unchanged, prism-scoped |
| Gateway | the **surface** on the entering side | `surfaceRoomIds` replaces `cellRoomIds`. The existing both-flanks rule applies **within a layer**; do not let a wall on another layer count as a flank. Do not re-litigate the chamfer ruling |
| Partition wall | the two surfaces it separates, same layer | unchanged grammar, level-banded key |
| Cliff wall | the higher surface | drop target = `supportBase` (§7), not the global abyss |
| Railing | the surface whose edge it guards | unchanged; existing suppression rules (deck-even edges, bridge ports, stair mouths) extend per layer |
| **Aperture rim** | the layer that owns the opening | railing or bare, **authored** — a bare rim is how a fall becomes discoverable; a railed rim makes it a `Window`, not an `Aperture` |
| Soffit / underside | the surface *above* | new; the surface below never emits a ceiling |
| Bridge deck | **the room it belongs to**, if any, else the connector | new — today a deck belongs to nobody, which is why bridges over rooms are banned |

### 8.4 Future-compatible NPC navigation

**[Proposed]** export the surface graph as a data artifact beside the collision
payload — `random_dungeon.navsurfaces.shared.json` — containing nodes
`(cell, level, roomId, surfaceId, kind)` and typed edges
`(from, to, kind, directed, riseLevels, cost)`.

Three rules that keep this honest without designing the NPC system:

1. **Derived, never authored.** It is a projection of the plan, like the
   canonical layout projection.
2. **Validated against collision.** Every nav node's level must equal the
   surface height the server would sample at that cell centre. A
   `NAV_COLLISION_DISAGREEMENT` rejection is what stops the nav graph and the
   geometry drifting apart — the failure mode
   [`npc-system-design-2026-07-11.md`](../npc-system-design-2026-07-11.md)
   warns about ("do not use a Unity-only NavMesh as gameplay authority").
3. **Capability-tagged edges, no planner.** `Fall` edges carry their height so a
   future planner can decide whether a given NPC may use them. The generator
   states what is physically possible; it does not decide who may do it.

**[Deferred]** the planner, local avoidance, unreachable-target recovery, and
any NPC decision to take a fall.

### 8.5 Determinism, hashing, replay

- **Derived RNG keys must move from cell to surface.**
  `DungeonRandomScope.Stream(purpose, subject)`
  (`DungeonLabGenerator.Validation.cs:58`) is correct in shape; any subject that
  is a cell token must become a surface token, or two stacked surfaces will
  share a stream and stop being independent. This is the multi-layer version of
  exactly the defect the 2026-07-25 derived-RNG work fixed.
- **The canonical projection changes shape once.**
  `BuildCanonicalTieredLevelPlanProjection` emits sorted `cell → level`; it
  becomes sorted `(cell, level) → {layerOrdinal, roomId, kind}`. **[Proposed]**
  keep the *old* shape whenever `IsSingleLayer`, so Phases A and B are provably
  output-neutral and only the phase that introduces real overlap rebaselines.
- **Never gate on a recorded hash.** Compare against the current commit with
  `ops/dungeon-port-ab.sh` (stash/restore, seed-by-seed diff) — a recorded value
  rots the moment an unrelated profile asset moves, which is why `3092863a…` is
  unreachable ([`CURRENT_STATUS.md`](CURRENT_STATUS.md)).
- **Replay metadata:** the plan report should carry `maxLayersPerCell`, the
  surface count, and every `Aperture`/`Void`/`Fall` with its catch surface, so a
  bad seed is diagnosable without re-deriving the field.

---

## 9. Architectural options compared

### 9.1 The core representation

| | **A. Multi-value heightfield**<br/>`Dictionary<Vector2Int, List<int>>` | **B. Surface records** ★<br/>keyed `(cell, level)` | **C. Layer sheets**<br/>N independent height fields | **D. Volume model**<br/>voxels / solid-space |
|---|---|---|---|---|
| Container diff | smallest | medium | small | total rewrite |
| Stable identity for a surface | **no** — a level is not an identity | **yes**, and the port graph already uses it | per-sheet only | cell column |
| Renderer reuse | must rewrite the loop anyway | rewrite the loop, keep all grammar | **reuse `BuildLevelField` per sheet** | none |
| Room spanning layers | awkward | natural | natural | natural |
| Bridge as a first-class surface | awkward | natural | **splits a bridge across sheets** when it crosses void and floor | natural |
| Reservation model | still needs prisms | prisms | per-sheet 2-D, plus cross-sheet | native |
| Risk | ownership stays implicit everywhere | wide but mechanical | sheet assignment is a graph-colouring problem, fragile and non-obvious | very high |

**Recommend B.** The decisive argument is that `PortGraphNode.Floor(cell, level)`
already *is* option B — this canonicalizes an identity the system uses
internally rather than inventing one. C is genuinely tempting because it would
let today's renderer be called once per sheet unchanged; it fails on a concrete
case: a bridge that leaves a room over void and crosses a lower chamber is
layer-0 for part of its run and layer-1 for the rest, so a single deck straddles
two sheets and its walls and railings belong to neither.

### 9.2 Renderer strategy

| | Per-surface iteration ★ | Per-sheet re-invocation | Separate overlay renderer for upper layers |
|---|---|---|---|
| Grammar preserved | yes | yes | **no** — a second grammar to keep in sync |
| Cross-layer support base | one function | needs cross-sheet query anyway | needs it anyway |
| Cost | wide, mechanical, one pass | needs §9.1 option C | least up-front, highest long-run |

**Recommend per-surface iteration.** The overlay approach is the trap: it looks
cheap because it leaves the existing renderer alone, and it ends with two wall
grammars whose railing and corner rules drift.

### 9.3 Pit representation

| | Declared `Opening` on a layer ★ | Absent cell + inferred catch | Trap-style volume object |
|---|---|---|---|
| Distinguishes pit from void | **explicitly** | by inference — the thing to avoid | yes but disconnected from the layer |
| Backwards compatible | yes — absent cells keep today's meaning | no — reinterprets every existing void | yes |
| Provable reversibility | yes | yes | awkward — no owning layer |

---

## 10. Compatibility and migration

| Concern | Assessment |
|---|---|
| **Single-surface generation** | Preserved exactly while `IsSingleLayer`. `AsHeightField()`, the degenerate `supportBase`, and the old canonical projection shape make Phases A and B output-neutral by construction — provable with `ops/dungeon-port-ab.sh`. |
| **`MaxGeneratedLevel` 24 → 40** | **Not** output-neutral, and it breaks all seven topology files: `anchors.top` must equal `MaxGeneratedLevel` exactly (`DungeonRouteTopologyValidator.cs:366`). **[Proposed]** relax that rule to "the top anchor is at the topology's declared ceiling" and add an optional per-topology `ceiling` (default 24, capped at the global 40). Existing dungeons then keep their shape and a *new* topology opts into depth. Raising the constant alone would stretch every shipped dungeon and is the wrong lever. |
| **Density dial** | Fill passes must query the prism ledger; `OpenVolume` joins the authored-void exclusion list. `floorFillPercent` becomes ambiguous under stacking — **[Proposed]** keep it as *plan-cell* fill (unchanged meaning, unchanged tuning) and add `surfacesPerPlanCell` as a separate reported metric. Do not redefine the number the dial was tuned against. |
| **Recipes** | Additive fields, empty `layerId` = today's behaviour. The four enabled recipes need no edit. |
| **The three required slots** | Unchanged. A multi-layer room is a new recipe, not a change to the slot contract. The silent slot-geometry rule ([`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md) "Slot geometry") still applies and still is not machine-checked. |
| **Existing tests** | Per `CLAUDE.md`, do not repin. `DungeonLabStackedCrossingTests` asserts `transitionCount == 1` and `stackedCoordinateCount == 1` on a hand-built fixture — extend the *fixture*, and loosen or delete assertions that pin seed-derived counts. Report the rest; do not fix unasked. |
| **`H2` (two floor representations)** | This work should *close* it by deriving `floorCells` from `SurfaceField`, not add a third representation. Treat that as a hard constraint on Phase A. |
| **Server / collision** | No change required (§7.2). Republish with `ops/republish-local-clear.sh` after each rebuild so server geometry matches the scene. |

---

## 11. Failure modes and explicit rejection conditions

New rejection codes, each with a named owner phase:

| Code | Condition | Phase |
|---|---|---|
| `SURFACE_STACK_CLEARANCE` | two surfaces in one column closer than `MinHeadroomLevels` | A |
| `SURFACE_GRAPH_NOT_STRONGLY_CONNECTED` | some surface cannot be reached, or cannot reach the rest | A |
| `PRISM_CONFLICT` | two reservations share a cell and an intersecting level band | B |
| `OPEN_VOLUME_VIOLATION` | floor, wall, fill, annex, dressing or set piece emitted inside a reserved volume | B |
| `APERTURE_NO_CATCH_SURFACE` | declared pit with no surface under some cell of its footprint | C |
| `APERTURE_NO_RETURN_ROUTE` | catch component cannot reach the opening's component without a fall | C |
| `APERTURE_FALL_TOO_SHALLOW` | fall < `MinHeadroomLevels` — that is a ledge, not a pit | C |
| `VOID_OPENING_OBSTRUCTED` | declared lethal void with any surface in its fall column | C |
| `SUPPORT_BASE_UNDEFINED` | a cliff face whose drop target cannot be resolved | C |
| `SOFFIT_MISSING` | a non-lowest surface with no underside geometry over a reachable surface | C |
| `ROOM_LAYER_CONNECTIVITY` | a room claims to connect layers it does not | D |
| `ENTRANCE_LAYER_BINDING` | a route edge bound to an entrance elevation the route cannot supply | D |
| `NAV_COLLISION_DISAGREEMENT` | a nav node's level ≠ the sampled collision surface at that cell | E |

**Failure modes to design against, distinct from rejections:**

- **Silent atrium fill.** The density passes will claim any plan cell they can.
  If they do not read the prism ledger, density ≥3 packs an atrium and no check
  fires.
- **Wrong-layer flank.** A gateway taking a flank from a wall on a different
  layer produces a floating arch. The both-flanks rule must be layer-scoped.
- **Soffit-as-floor.** A soffit exported with `walkable_top` snaps players onto
  the underside of a bridge.
- **Ordinal drift.** If `layerOrdinal` leaks into a stable identity (a hash key,
  a nav id), adding a surface below renumbers everything above it. Ordinals are
  for reports; `(cell, level)` is the identity.
- **`FillUnassignedFloorCells` guessing on a stacked cell.** It already falls
  back to level 0 for unreachable cells with a warning, deliberately not a
  rejection (`DungeonLabGenerator.cs:1910`). Under multi-surface a level-0 guess
  at a stacked coordinate is a collision hazard, not a cosmetic one.
  **[Proposed]** promote it to a rejection *only for cells with more than one
  surface*.
- **The late-pass ordering hazard (review H3).** Three passes already run after
  the headroom gate. Adding aperture and volume resolution to that tail without
  moving the gate reproduces the defect at higher stakes. Fix the ordering as
  part of Phase B, not after.

---

## 12. Representative scenarios, mapped to the model

| # | Scenario | How it is represented |
|---|---|---|
| 1 | Upper floor with a pit; lower chamber with its own corridors; separate return stair | One room, two `RoomLayer`s. An `Aperture` on the upper layer with a `Fall` edge to the lower layer's catch surface; the return is an ordinary `Stair` edge. Strong connectivity proves the branch reversible. |
| 2 | A bridge between two elevated rooms with independently traversable rooms/corridors below | The deck's cells are `Deck` surfaces at the bridge level; the cells below carry `Floor` surfaces. A `Support` prism under the deck and a `Clearance` prism above the lower surface prove ≥3u separation. |
| 3 | One room owning a lower chamber, upper gallery, bridge, openings and internal connections as one atomic composition | One recipe with three `DungeonRecipeLayer`s, its own transitions, and `RECIPE_LAYER_CONNECTIVITY` proving the claimed connections before catalog admission. |
| 4 | Multiple local layers overlapping without being one global storey | `layerOrdinal` is per-cell and room-local; no API names a storey. Two rooms' "gallery" layers can be at different absolute levels in the same dungeon. |
| 5 | A large atrium as a vertical hub — routes entering at several elevations, balconies and bridges crossing, stairs connecting selected layers, a lower floor reachable by falling or descending | One room with an `OpenVolume` prism reserving its void, N `RoomLayer`s, per-layer ports bound by `fromLayer`/`toLayer` on topology edges, `Aperture` openings for the fall route, and derived `Sees` relations across the volume. |

---

## 13. Phased path

Bounded, not a rewrite. Each phase ends in a state that is safe to sit in.

### Phase A — Surface identity in the plan

**Capability.** The canonical plan can express more than one walkable surface at
a plan coordinate, and the traversal graph is promoted to first-class. Nothing
yet *produces* two surfaces.

**Systems.** `TieredLevelPlan`, `DungeonLayout`, `FloorStairPortGraph`,
`DungeonLabGenerator.Validation.cs`, the canonical projections in `.Batch.cs`.
`AsHeightField()` shim at every existing consumer.

**Invariants.** `IsSingleLayer` holds for every seed. `floorCells` becomes
derived from `SurfaceField`. Strong connectivity replaces `IsGloballyConnected`
and agrees with it on every seed. RNG subjects that were cell tokens become
surface tokens.

**Evidence.** `ops/dungeon-port-ab.sh` on 200 seeds: byte-identical `resultHash`,
stashed vs restored. Two independent runs identical
(`ops/dungeon-step2-verify.sh`). **Render Sweep (200)** — Batch Validate never
builds a GameObject, so it cannot prove the renderer survived the shim.

**Non-goals.** No renderer change. No second surface anywhere. No prism ledger.

**Exit.** 200/200 accepted, `hardValid 200/200`, byte-identical plan hash against
the current commit, Render Sweep 200/200.

---

### Phase B — Volumetric reservation and one clearance rule

**Capability.** Reservations and clearance are volumes. `spanDeckLevels` and the
duplicated deck formula die.

**Systems.** `StairPlacementLedger` → prism ledger; `TryValidateSpanHeadroom` →
the general rule; `TryValidateAcceptedPlanHeadroom` in `.Batch.cs` deleted in
favour of the shared one; density fill passes read the ledger; the late-pass
ordering (review 2.4) fixed so the gate guards the final state.

**Invariants.** For every surface, `[level, level + 3]` is free of another
surface's support. Every reservation carries a level band. No validation runs
before a mutation it must see.

**Evidence.** Output-neutral 200-seed A/B. A negative fixture: an artificially
lowered stacked cell must still be rejected — the existing
`negativeHeadroomRejected` probe in `BuildStackedCrossingFixture` is exactly
this and should be retargeted at the new rule rather than duplicated.

**Non-goals.** No `OpenVolume` *producer* yet — only the reservation kind and its
enforcement.

**Exit.** Byte-identical 200-seed hash; `spanDeckLevels` and the duplicated deck
formula gone; the ledger's conflict rule proven level-band-aware by fixture.

---

### Phase C — The two-layer authored episode ← the first real proof

**Capability.** Genuine overlapping traversal, end to end, in a built and
exported scene: an upper route with a bare-rim aperture, a directed fall to a
lower chamber with its own corridor, a return stair, and a bridge over the lower
playable route.

This is deliberately an **authored deterministic episode**, not a
generic-generation feature — the same order the project used for topologies
("abstractions are earned by a working slice").

**Systems.** Renderer per-surface iteration; `supportBase`; soffit pass;
level-banded `EdgeKey`/`OpenEdgeKey`/`WallEdge`; `surfaceRoomIds`; `Opening` +
`Fall` edge; one new Episode recipe with two layers; the aerial-bridge path
promoted so a deck's cells become surfaces.

**Invariants.** Strong connectivity holds with the fall directed. Every aperture
has a proven catch surface and return route. Every non-lowest surface has an
underside. No cliff drops through a surface below it. Both surfaces at the
stacked coordinate carry collision; the volume between them is clear.

**Evidence.**

1. Extend `BuildStackedCrossingFixture` to the full episode and probe colliders
   at *three* stacked coordinates (aperture rim, chamber floor under the bridge,
   deck).
2. **Render Sweep** on the fixture seed set — the episode must build, save, and
   export.
3. **Live**, not post hoc: a headless player probe (modelled on the committed
   `ops/s4-los-probe.py` … `ops/s9-auto-rewind-probe.py` family) that walks a
   player off the aperture, confirms the server lands them on the chamber
   surface and not the abyss, walks the return stair, and crosses the bridge
   over the chamber. Publish first with `ops/republish-local-clear.sh` and prove
   the change is live on the target DB before the leg runs.
4. Owner eyeball on the built scene. No hash tells you whether a two-layer room
   reads well.

**Non-goals.** No generic multi-layer rooms. No lethal void mechanic. No
envelope raise. No NPC nav export. Bridges over rooms only inside this authored
episode.

**Exit.** The episode generates deterministically across two independent runs;
the probe demonstrates fall → lower route → return stair → bridge on a live
server; Render Sweep clean; owner accepts the look.

---

### Phase D — Multi-layer rooms in generation

**Capability.** Topologies and recipes can declare layers; routes bind to
declared entrance elevations; `OpenVolume` reserves an atrium; the atrium
archetype (scenario 5) generates.

**Systems.** Topology schema (`layers` on nodes, `fromLayer`/`toLayer` on edges)
+ `Validate Topologies`; recipe schema (`layerId`, `OpenVolume`, negative
elevated zones) + `DungeonRecipeValidator`; recipe base-level derivation per
layer; `RoomFootprint.Overlaps` volumetric; `ChooseEnclosedRooms` moved into the
plan so bridges may legally cross rooms.

**Invariants.** No global storey concept anywhere. A room's declared layer
connectivity is proven before catalog admission. `OpenVolume` survives every
density level. Room stacking requires a declared reason.

**Evidence.** 200-seed batch at every density 0–5 on a new atrium topology;
Render Sweep; the fill metric split into plan-cell fill (tuning-stable) and
surfaces-per-cell (new); a per-seed report of layer counts and aperture/fall
inventory.

**Non-goals.** NPC nav. Envelope raise. Procedurally *invented* apertures — a
pit is authored by a topology or recipe in this phase.

**Exit.** Density 0–5 all ≥199/200 accepted with the atrium in the mix; no
`OPEN_VOLUME_VIOLATION`; the atrium's declared entrances bind at ≥2 distinct
elevations on ≥90% of its seeds.

---

### Phase E — Depth, navigation surface, and void declaration

**Capability.** Ten major elevation changes; the nav artifact exports; lethal
void is declarable.

**Systems.** Per-topology `ceiling` + global cap 40; abyss skirt re-derivation;
nav surface export + its collision-agreement check; `OpeningKind.Void` producer.

**Invariants.** Every nav node agrees with sampled collision. A `Void` opening
has nothing in its fall column. Existing topologies keep their 24u ceiling
unless edited.

**Evidence.** A deep new topology at ceiling 40 generating 200/200; nav/collision
agreement 100%; a headless probe walking the nav graph's edges to confirm each
is traversable.

**Non-goals.** The NPC planner. Void-death mechanics. Deliberate lethal-void
floorplan generation.

**Exit.** A 40u topology ships; the nav artifact exports and validates; `Void` is
declarable and rejected when obstructed.

---

## 14. Bottom line

### Recommended architecture

**Replace the plan cell with the surface `(cell, level)` as the identity of a
walkable place; give reservations a level band; give cliff faces a support base;
give layers explicit openings with a declared kind. Everything else stays.**

The four-line version:

1. `Dictionary<Vector2Int,int> cellLevels` → `SurfaceField` keyed on
   `(cell, level)`, with a single-layer projection for compatibility.
2. `HashSet<Vector2Int>` reservations → prisms
   `(cell, minLevel, maxLevel, kind)`, with `OpenVolume` as a first-class kind.
3. `abyssBase` (one global int) → `supportBase(surface, direction)`, plus a
   soffit pass.
4. `Opening { Aperture | Void | Window }` on a layer, `Fall` as the one directed
   traversal edge, and **strong connectivity** as the invariant that makes
   optional pit branches provably reversible.

No rewrite. `ElevationEdgeModel`'s wall/railing/corner/gateway grammar,
`StairForge`, the contract data, the recipe system, the route planner, the
canonical evidence machinery and the derived-RNG scope all survive — the same
conclusion [`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md)
reached for its own scope, for the same reason.

### Smallest credible implementation slice

**Phase A alone** — `SurfaceField` + surface graph + strong connectivity, with
`IsSingleLayer` true on every seed and a byte-identical 200-seed hash against
the current commit. It ships no new capability and is the only slice that makes
every later one safe, because it is the one that can be *proved* to have changed
nothing.

If a slice with something to look at is wanted, **Phase A + C**, skipping B's
generalization by keeping `spanDeckLevels` one phase longer. Not recommended —
the prism ledger is what stops two transitions at one coordinate from
conflicting, and without it the authored episode has to special-case its own
bridge.

### Major risks

1. **The renderer edge-key widening is wide.** `EdgeKey`/`OpenEdgeKey`/
   `(x,z,direction)` thread through corner selection, gateway sockets, shell
   placement, trap placement and railing suppression. Mechanical, but it is most
   of Phase C and it is where a subtle railing or flank regression will hide.
   Render Sweep, not Batch Validate, is the only thing that catches it.
2. **Soffit art may not exist.** The pack has no measured flat under-deck cap
   family. Measure this *before* committing Phase C; if it is missing, Phase C's
   exit criterion is unreachable and the phase should be re-scoped around a
   bridge over a corridor in an open room rather than a full gallery.
3. **Silent atrium fill.** The density passes will claim any plan cell. If
   `OpenVolume` is not wired into all four mechanisms, density 5 packs the
   atrium and no gate fires.
4. **Two clearance numbers.** `MinHeadroomLevels = 3` against a 1.8u player and a
   4u major rise leaves ~2.2u. Comfortable stacking wants 8u, which halves the
   number of layers a 40u envelope supports. Expect to re-derive this from the
   capsule rather than inherit it.
5. **Hash rebaseline discipline.** Phase C moves every seed once. Compare against
   the current commit with `ops/dungeon-port-ab.sh`; never against a recorded
   value, and never assert one in a test.
6. **LOS interaction.** The dungeon exports query collision == movement
   collision, so a bridge deck will block sight to the chamber below it using
   deliberately oversized geometry. This contradicts the project LOS rule and
   gets worse as stacking increases.

### Remaining owner decisions

1. **Envelope.** Is "ten full major elevation changes" 40u (ten × 4) or 80u
   (ten × 8)? This document is designed against 40u.
2. **Envelope mechanism.** Per-topology `ceiling` (existing dungeons unchanged,
   new ones opt in) versus raising the global constant (every seed stretches,
   all seven files edited). Per-topology is recommended.
3. **Stacking pitch.** Is one major rise (4u, ~2.2u clear) an acceptable stacking
   pitch, or is 8u the minimum? This decides how many layers 40u actually buys.
4. **Fill metric.** Keep `floorFillPercent` as plan-cell fill (density tuning
   stays valid) and report surfaces-per-cell separately — or redefine it?
   Keeping it is recommended.
5. **Ledge drops.** Model every walk-off-able ledge as a `Drop` edge, or only
   *declared* apertures as `Fall` edges? Declared-only to start is recommended;
   the runtime permits the rest regardless.
6. **Bridges over rooms** requires moving `ChooseEnclosedRooms` into the plan
   (deferred in 2026-06). Confirm that is in scope for Phase D.
7. **Aperture rim guard.** Is a bare rim (discoverable fall) the default, or
   railed-unless-declared? A look-and-feel call with real gameplay consequence.

---

## 15. Sources

**Generator core** — `Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.cs`
(`TieredLevelPlan` :8343, `DungeonLayout` :7700, `RoomFootprint` :7516,
`StairPlacementLedger` :6404, `PortGraphNode` :8049,
`TryBuildFloorStairPortGraph` :6849, `TryValidateSpanHeadroom` :2992,
`AddAerialBridges` :4390, `TryPlaceAerialBridge` :4666, constants :45–58) ·
`.Validation.cs` · `.Recipes.cs` · `.TransitionReservation.cs` ·
`.CorrectiveConnections.cs` · `.Batch.cs` (canonical projection :4937) ·
`.StackedCrossingFixture.cs`

**Renderer / geometry** — `Assets/Arena/Editor/Dungeons/RandomDungeon/ElevationEdgeModel.cs`
(`BuildLevelField` :205, `BuildWallEdges` :2287, `BuildTransitionKeys` :1714,
`RoomBoundaryContext` :10349, `TransitionEdge` :10180, constants :40–55) ·
`ElevationEdgeModel.Traps.cs` · `StairForge.cs` · `RandomDungeonSceneBuilder.cs`

**Recipes / topology** — `DungeonRecipeAsset.cs` · `DungeonRecipeValidation.cs` ·
`DungeonRouteTopology.cs` · `DungeonRouteTopologyValidator.cs` ·
`Topologies/atrium-ring.json` and the six siblings

**Export / runtime** — `Assets/Arena/Editor/GameplayCollisionExporter.cs` ·
`server/src/world_collision.rs` (`try_open_world_surface_height_at_y` :1464) ·
`server/src/game_loop.rs` (ledge/fall/land :1480–1530) ·
`server/src/player_physics.rs`

**Documentation** — [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) ·
[`GLOSSARY.md`](GLOSSARY.md) · [`CURRENT_STATUS.md`](CURRENT_STATUS.md) ·
[`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md) ·
[`stair_forge_design.md`](stair_forge_design.md) (decisions 22, 29–34) ·
[`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md) ·
[`ROOM_AUTHORING_GUIDE_CURRENT.md`](ROOM_AUTHORING_GUIDE_CURRENT.md) ·
[`density-scale-design-2026-07-27.md`](density-scale-design-2026-07-27.md) ·
[`GENERIC_ROOM_FAMILY_BRIEF.md`](GENERIC_ROOM_FAMILY_BRIEF.md) ·
[`npc-system-design-2026-07-11.md`](../npc-system-design-2026-07-11.md)

**Tooling referenced for evidence** — `ops/dungeon-port-ab.sh`,
`ops/dungeon-step2-verify.sh`, `ops/dungeon-gateway-audit.py`,
`ops/republish-local-clear.sh`, the `ops/s4-…s9-` headless probe family,
**Tools > Dungeon Lab > Render Sweep**,
**Tools > Dungeon Lab > Validate Topologies**

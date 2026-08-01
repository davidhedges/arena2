# Layered 3D topology for the random dungeon generator

Status: **DRAFT — not design of record.** A proposal for evolving the generator
from a single-surface heightfield to multiple independently traversable surfaces
that may overlap in plan space. Not yet implemented; no phase is in progress.

Date: 2026-07-29 · Revised 2026-07-29 after owner review (§0)

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

## 0. Review findings, and what changed

Owner review of the first draft raised seven material defects. All seven were
verified against the code and all seven are accepted. Five were outright errors;
two of those invalidated claims the draft stated as fact. What follows is the
record, so a later reader can see which parts were reasoned through and which
were repaired.

| # | Finding | Verdict | Where it is fixed |
|---|---|---|---|
| 1 | Deriving `floorCells` from `SurfaceField` inverts the pipeline, and `RoomConnection` loses corridor layer identity | **Correct.** `floorCells` is the *domain* the level field is computed over (`cs:1907`, `cs:2127`). Worse than reported: a connection resolves to its route edge **by room pair** (`cs:2700`), so two layer-distinct corridors between one room pair are indistinguishable at the lookup, not merely at the path | §3.1 (PlanShadow vs SurfaceField; H2 reframed as an invariant), §8.1 (`RoomConnection` gains `edgeId` + layer binding) |
| 2 | `Opening` cannot represent the pit: `TraversalEdge` needs two `SurfaceKey`s but an aperture is a hole, and one `catchSurface` cannot serve a multi-cell opening | **Correct** | §5 — falls are emitted **per opening cell**, rim surface → catch surface; catch surfaces are **derived and validated**, never authored |
| 3 | The prism ledger has no owner, its blanket conflict rule contradicts "landings may share landings", and closed bands reject valid clearance at the endpoint | **Correct on all three** | §6 — `ownerId`, half-open `[min, max)`, an explicit kind×kind compatibility matrix, and authorized penetrations for `OpenVolume` |
| 4 | Phase C needs layered-recipe schema that the draft deferred to Phase D | **Correct** | §13 — the minimum recipe layer schema moves into Phase C; `OpenVolume`, sunken zones, topology layer bindings and generic multi-layer rooms stay in D |
| 5 | The stated collision safety control does not exist: exported boxes carry no `walkable_top` | **Correct.** `GameplayCollisionBoxFile` is `{shape, center, size, rotation}` (`world_collision.rs:120`); the flag belongs to the hardcoded procedural `Collider` (`:106`). The claim "collision export — no structural change" was unproven | §7.2 rewritten around the real mechanism, and downgraded from a claim to a hypothesis that Phase C's live probe must test |
| 6 | "Nearest surface in the neighbouring column" is not a sufficient renderer algorithm, and `supportBase` is a scalar in one place and a function in another | **Correct.** Columns `{0,8}` and `{4,12}` have no unique symmetric pairing | §3.1 (scalar removed), §7.1 (directional edge-band decomposition; the "~30 lines" estimate retracted) |
| 7 | Strong connectivity does not prove the non-fall return rule: `U --Fall→ L1 --Fall→ L2 --Stair↔ U` is strongly connected but L1 cannot return without falling. A single `returnRouteId` also cannot describe a multi-edge return | **Correct** | §3.3 — the invariant becomes **the fall-free subgraph must be connected**, which is strictly stronger and implies both. `returnRouteId` is dropped as authored data and becomes a validation witness |

**On finding 7, one refinement.** The review's proposed fix — validate each `Fall`
independently after removing all `Fall`/`Drop` edges — is correct. Requiring the
**fall-free subgraph to be connected** is simpler and strictly stronger: it
implies full strong connectivity *and* per-fall reversibility, and it is the
literal statement of "pits primarily create optional branches." It is adopted in
that form, with the one case it forbids raised as an owner decision (§14).

**What survived round one unchanged:** canonical surface identity keyed on
`(cell, level)`, surface-scoped room ownership, explicit typed openings,
per-topology ceilings, and keeping plan-cell fill as a separate metric from
surface count.

### Round two

A second review of the revision found seven more, concentrated in exactly the
places round one had just rewritten. All verified, all accepted.

| # | Finding | Verdict | Where it is fixed |
|---|---|---|---|
| 8 | Phase A's "every connection resolves to exactly one route edge" would reject synthesized loop corridors | **Correct.** `AddLevelSafeLoopConnections` builds `RoomConnection`s with no route intent (`cs:1503`), and the elevation path treats the route requirement as **optional** by design (`cs:2097`) | §8.1 — `RoomConnection` gains a `source` discriminator; §13 Phase A's invariant is restated |
| 9 | `(cell, layerId)` is not a global vertical identity — `layerId` is room-local, an edge's two ends may differ, and `pathLayerOrdinal` contradicts §3.1's rule that ordinals are never identity | **Correct on all three** | §8.1 — corridor exclusivity keys on a **planned elevation band** derived from the topology's declared absolute node levels, which *is* known pre-elevation |
| 10 | The renderer's occupied band "from a surface's level down to whatever supports it" fills stacked space with solid mass, walling off the chamber under a gallery | **Correct, and it breaks C1 specifically**, which has no `OpenVolume` producer to subtract that mass | §7.1 — bands become **structural** (slab + declared supports), not implied |
| 11 | The collision explanation is still wrong: the server tests an **absolute** Y normal, so down-facing triangles pass; and the sampler takes the **highest** eligible surface, so a soffit under a deck cannot snap a player down | **Correct.** `triangle_normal_y_abs` returns `(cross[1]/length).abs()` (`world_collision.rs:2820`); selection is `max` (`:1557`) | §7.2 rewritten a second time, with a remedy derived from both code paths rather than guessed |
| 12 | The prism matrix does not reproduce the ledger: the two clearance kinds are distinct, landing–clearance and mouth–clearance do **not** conflict today, and the relation is asymmetric. A runtime `int ownerId` also cannot appear in an authored allow-list | **Correct on all counts** (`cs:6477`) | §6 — five kinds kept distinct, an asymmetric **blocks-policy**, and a typed owner key |
| 13 | Shadow agreement and Phase A's byte-identical hash gate are mutually exclusive: `floorCells` feeds the canonical hash | **Correct.** `BuildCanonicalLayoutProjection` (`Batch.cs:4864`) → `layoutHash` → `canonicalHash` (`:3437`) | §13 — Phase A splits into **A1 (detect, byte-identical)** and **A2 (repair, explicitly rebaselined)** |
| 14 | Rim guarding is per edge but `OpeningKind` is per opening — a pit railed on three sides and bare on one is unrepresentable | **Correct** | §5, §8.3 — guard and fall emission move to `(rimSurface, direction)` |
| — | §10's compatibility table still asserts the superseded H2 derivation, "no collision change", and an unchanged slot contract | **Correct** | §10 rows corrected |

### Round three

Six more, all verified, all accepted. Three were internal contradictions
introduced *by* the round-two fixes — a pattern worth noting: each repair pass
has created new inconsistencies in the sections adjacent to the ones it touched.

| # | Finding | Verdict | Where it is fixed |
|---|---|---|---|
| 15 | Phase A's corridor re-key is **not** output-neutral. Today rejection is unconditional (`RouteFirstPilot.cs:2582`); allowing disjoint `plannedBand`s to share a cell accepts embeddings currently rejected — and `atrium-ring` spans levels 0–24, so disjoint bands are common, not hypothetical | **Correct**, and it also contradicted Phase A's own "no second surface anywhere" | §8.1 and §13 — Phase A carries the **data** (`edgeId`, `connectionId`, `plannedBand`); the **relaxation** moves to Phase D behind layer binding |
| 16 | The structural-band rule and `supportBase` prescribe different geometry for the document's own `{0,8}` vs `{0}` example — bands give a slab fascia, `supportBase` walls off the chamber 8→0 | **Correct** | §7.1 — structural intervals are authoritative; `supportBase` is demoted to naming where a **plinth** band bottoms out, not an independent mechanism |
| 17 | `OpenVolume` is stated to block `Wall`, but `Wall` is not a `PrismKind`; and "same owner never conflicts" lets a room's own solids bypass its penetration allow-list | **Correct on both** | §6 — `Wall` added; the allow-list governs `OpenVolume` **even for same-owner** solids |
| 18 | A1 cannot promise a byte-identical `resultHash` while adding a diagnostic to seed reports — `resultHash = ComputeSha256(seedReports…)` (`Batch.cs:5748`) | **Correct** | §13 — A1 gates on the **canonical/plan hash**, and the disagreement report is emitted **out-of-band** |
| 19 | `Window` is still in the authored `OpeningKind` enum while the corrected rule makes it derived | **Correct** | §5 — removed from the enum |
| 20 | Recipe anchoring on "the base layer's first port" fails for a base layer with no external port, and keeps an ordering dependence | **Correct** | §8.2 — every bound port yields a candidate base; all must agree; an explicit anchor covers the no-bound-port case |

Plus four stale summaries that had not caught up with the body: §9.2's
"per-surface iteration", the smallest-slice claim, major risk 2's soffit
description, and the bottom-line `Opening` schema. All corrected.

### Round four

| # | Finding | Verdict | Where it is fixed |
|---|---|---|---|
| 21 | The plinth rule cannot reproduce an ordinary single-layer elevation change. Making it conditional on *the neighbour being void* emits nothing for two adjacent ground floors at 0 and 4 — the commonest case in the corpus — where today a retaining face 0→4 is emitted unconditionally (`ElevationEdgeModel.cs:2389`) | **Correct, and it invalidated the byte-identical claim outright** | §7.1 — the band is keyed on **`IsGroundBacked`** (`kind == Floor` **and** lowest in column), not on the neighbour |
| 22 | Layer names are room-local, yet the Phase D relaxation permitted crossings on "different declared layers". Local names cannot establish vertical separation between unrelated rooms | **Correct** | §8.1 — **layer binding authorizes; the absolute band decides.** Both corridor sharing and third-room crossing compare layer-offset-adjusted absolute bands |
| 23 | Three passages defined three different headroom blocker sets | **Correct** | §6 — one named **`BlocksHeadroom`** predicate, referenced by name at every site |
| 24 | *(owner question)* Does the design allow elevation change **between/outside rooms**? | **It must, and an earlier revision would have narrowed it.** Openings hung off `RoomLayer` and `Surface` carried an `int roomId`, both assuming walkable space belongs to a room — when **[Fact]** almost every elevation change here is on a **corridor** (`cs:2163-2200`) and `BuildCellRoomIds` (`:3568`) maps room cells only | **§4.1 (new)** — surfaces and openings are owned by a `SurfaceRegion`, which is a room layer *or* a corridor run. Also L10e, and C1's aperture moved into a corridor |

Finding 21 is the sharpest so far: it would have deleted every retaining wall in
every dungeon, and it was introduced *by* round two's fix for the opposite defect
(filling stacked space with solid mass). The corrected rule has to thread both —
ground-backed surfaces carry mass to the abyss, suspended ones carry only a slab
— and §7.1 now shows all three configurations resolving from one decomposition.

**One place the review is extended rather than accepted.** On finding 11 it
offers three remedies — exclude soffit faces from movement collision, change the
exported geometry, or make the server require a *signed* upward normal. Tracing
both sampler paths gives a cheaper and safer fourth: **emit soffits as box
colliders, not mesh colliders.** Boxes are not normal-tested at all; they use a
0.35u capture window and the same `max` selection. A soffit box under a deck 3+
levels up is outside a lower player's 0.35u window, and for a player on the deck
its top is below the deck's own, so `max` keeps the deck. Safe under both rules,
no server change, and no LOS side effect — which matters, because the dungeon
exports movement collision *as* its query collision, so excluding a soffit from
movement collision would also let sight pass straight through it (§7.2).

---

## 0.1 Measured 2026-07-29 — the deck-underside art question, settled

The one external dependency that could invalidate Phase C. Measured from the
**shipped collision payload** (`server/src/world_data/random_dungeon.collision.shared.json`),
which carries the exact triangles the server consumes — no Unity, no guessing.

**The answer: the kit already ships solid floor tiles. The generator just is not
using them.**

| Finding | Measured value |
|---|---|
| The generator's floor, `MOD_Floor_01_O_straight_med`, is a **zero-thickness one-sided plane** | 4 verts, 2 tris, both `normal_y = +1.000`, bounds **4u × 0 × 4u** |
| Its `_E_` counterpart, `MOD_Floor_01_E_straight_med`, is a **closed solid slab** | 8 verts, bounds **4u × 0.5u × 4u**, `Ymin = −0.5, Ymax = 0` |
| **The whole family pairs up.** Every `_E_` shape is its `_O_` twin's top surface plus a bottom | 13 matched shapes: straight 4↔8, convex_med 8↔16, convex_med_2 13↔26, angle_med 3↔6 … **(collision-hull counts — see the C1a correction below; the RENDER meshes are not 2:1)** |
| **The slab hangs entirely BELOW the walk surface** | `Ymax = 0` on every `_E_` piece measured |
| `_M_` walls are likewise closed boxes with a real bottom face | `MOD_Wall_01_M_straight_med`, bottom face `normal_y = −1.000` |
| Floors use a non-convex `MeshCollider` sharing the render mesh | `P_MOD_Floor_01_O_straight_med.prefab` |

> **`slabThickness = 0.5 world units = 0.5 levels`, measured, hanging below the
> walk surface.** §7.1's suspended band is therefore `[level − 0.5, level)`,
> which is exactly the convention the band model assumed — it now has a number
> instead of a placeholder.

**Consequences.**

1. **Phase C's art risk is retired, and no new art is needed.** Any surface whose
   underside is visible — deck, gallery, balcony, bridge — uses the **`_E_`
   family** instead of `_O_`. `FloorName` is currently pinned to
   `P_MOD_Floor_01_O_straight_med`, and the round-corner swap likewise uses
   `_O_convex_med` / `_O_angle_med`; those become a per-surface choice rather
   than a constant.
2. **No flipped quads, no render-only hack, no box-collider rule.** An `_E_`
   slab's bottom face is genuine geometry with a genuine collider, so it blocks
   sight and movement correctly — which matters because the dungeon exports
   movement collision *as* query collision (§7.2) and a render-only soffit would
   have let sight pass straight through a floor.
3. **Usable clearance is headroom − 0.5u.** With `MinHeadroomLevels = 3` a deck
   leaves **2.5u** of true clear space, not 3u, against a 1.8u player. Still
   ample at the 4u major rise (3.5u clear), but the 0.5u must be carried in the
   clearance derivation rather than discovered later.
4. **The `_E_` bottom face is not a ground hazard.** At any legal headroom it
   sits far outside the 1.2u capture window of the surface below it, so `max`
   selection never reaches it. The round-three absolute-normal correction stands,
   but it turns out not to bite here.

**Method note, recorded because it cost a wrong answer.** The first pass measured
the *exported collision payload*, which contains only geometry the generator
currently **uses** — so it showed nothing but `_O_` planes and concluded no solid
floor existed. The payload answers "what is shipped", never "what is available";
the pack itself is the only source for the second question. Owner correction,
2026-07-29.

#### Re-measured 2026-07-31 during C1a, from the loaded prefabs

Three things this section left open or got wrong. Measured by loading the
prefabs in the editor, which is the only source that answers "what will the
renderer actually instantiate".

| Question | Measured |
|---|---|
| **Is the pivot the same?** The folder names say `OneSided/` vs `PivotEdge/`, which reads like a pivot difference and would displace every deck on a swap | **Identical.** `_O_` and `_E_` both span local `min=(−4, ·, 0)` `max=(0, ·, 4)` — the same max-X/min-Z corner pivot and the same 4u×4u footprint. The swap is positionally a drop-in |
| Vertical extent | Confirmed: `_O_` is a plane at `y=0`; `_E_` spans `y ∈ [−0.5, 0]`, top flush with the walk surface. `slabThickness = 0.5` stands |
| The 2:1 vertex claim | **Does not hold on render meshes.** Measured straight **4→24**, convex_med **8→38**, angle_med **3→18**. The 2:1 figures above are collision-hull counts; a closed box split per face for normals is 24, not 8. The slab being genuinely closed is unaffected — only the ratio was wrong |
| **Collider kind — new, and it matters for §7.2** | `_O_` floors are **non-convex** MeshColliders; `_E_` floors are **convex** MeshColliders. **Neither is a box** |

**The collider finding puts §0.1 and §7.2 in tension, and the doc never
reconciles them.** §7.2's remedy for the soffit hazard is *"emit soffits as box
colliders"*, chosen precisely because boxes are not normal-tested and use the
narrow 0.35u window. §0.1's answer — use the `_E_` family — delivers a **convex
mesh** collider instead, which is normal-tested and uses the **1.2u** window.
§0.1's consequence 4 is the argument that this is still safe (a soffit 2.5u
above a lower player's feet is outside 1.2u), and that argument looks right, but
it is now a *sharper* prediction than either section states: Phase C's live probe
is testing a convex mesh soffit on the 1.2u window, **not** the box soffit §7.2
recommends. If the probe fails, "emit soffits as box colliders" is still the
first remedy to try and it is not what the `_E_` swap gives you.

**And the swap has no application site yet.** Every use of `FloorName` renders a
ground-backed floor: the floor loop iterates the level field
(`ElevationEdgeModel.cs:371`), and a bridge deck is not in it — the deck's
walkable surface is authored set-piece geometry from the transition prefab,
which is why `bridgeFloorBlockedCells` exists to keep the terrain floor *under*
it rendering. So §7.1 step 3's soffit pass cannot land before the first
suspended **floor** surface exists; it arrives with the gallery in C1b rather
than as separate work.

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
exists). **Collision and runtime movement are expected to need no structural
change** — a hypothesis Phase C's live probe must settle, not a finding; §7.2
has misstated the mechanism twice and states the current reasoning. Falling
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
| **L7b** | **A corridor has no layer identity, and is matched to its route edge by room pair.** `RoomConnection` is `{fromRoom, toRoom, path}`; `TryGetTransition(fromRoom, toRoom, …)` is the lookup. | cs:7899, cs:2700 | Two corridors between one room pair at different elevations are indistinguishable *before* their paths are compared. Any authored layer binding is discarded here. |
| **L8** | **"Void" is undifferentiated.** Void = absence from `cellLevels`. Every floor edge facing absence drops to the abyss base. There is no pit, hole, aperture, or opening object. | ElevationEdgeModel.cs:2430 | No way to distinguish an opening to a lower layer from the lethal exterior. |
| **L9** | **Bridge decks are not surfaces.** They exist as transition footprints + a side table dropped before the plan is published. | cs:4805, cs:8343 | A bridge cannot host traps, encounters, nav nodes, its own railings-by-room, or a second bridge below it. |
| **L10** | **Bridges over rooms are rejected by rule.** | cs:4550 | Scenario 2 and 5 as literally stated (§12). |
| **L10b** | **Corridor cells are exclusively owned.** `TryClaimCorridor` rejects any cell another connection already claimed — *"another connection already owns {cell}"*. | RouteFirstPilot.cs:2582 | **The deepest block on overlapping traversal.** Two corridors can never share a plan coordinate at any elevation, so bridge-over-corridor dies in the *layout* stage, before the bridge pass runs. Also: corridors are claimed **pre-elevation**, so the fix cannot use levels (§8.1). |
| **L10c** | **`PathCrossesThirdRoom` forbids a corridor crossing an unrelated room's footprint**, and the topology validator forbids a third node on an edge's lattice lane. | RouteFirstPilot.cs:2569; `ROUTE_TOPOLOGY_AUTHORING.md` rule table | A route edge can never be drawn over another room. Its *stated* reason — "an undeclared doorway and an unowned threshold" — is 2-D and does not apply to a crossing at a different elevation, so this is a well-founded relaxation rather than a rule to fight. |
| **L10f** | **Every route graph must be PLANAR.** Corridor exclusivity (L10b) means two edges can never share a plan cell, so no two routes may cross. **Measured 2026-07-29: all seven shipped topologies have exactly zero edge crossings** — that is the rule, not a coincidence of authoring. | RouteFirstPilot.cs:2582; crossing test over `Topologies/*.json` | The capability this whole design exists to add. A route passing over another route is currently inexpressible at the *graph* level, before any geometry is considered. |
| **L10e** | **Loop bridges connect ROOM PAIRS only.** `AddAerialBridges` iterates `roomA`/`roomB` and takes landings from room boundary edge cells. | cs:4412, cs:4450 | A bridge cannot start or end on a corridor, a ledge or a landing. *(Route edges of kind `Bridge` are unaffected — they realize as an `externalSpan` transition on the corridor path, `cs:2153`, so a **route** bridge is already corridor-based.)* |
| **L10d** | **A recipe-slot node must have degree 2** ("a two-port recipe room"), and a topology declares **exactly 3 slots**. | `ROUTE_TOPOLOGY_AUTHORING.md` rule table | A vertical hub with entrances at several elevations is degree 3–4, so **an atrium can never be a required recipe slot.** Scenario 5 has nowhere to live in the current slot vocabulary. |
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

**Two structures, not one.** The draft collapsed these and inverted the
pipeline; they are distinct and they live at different stages.

```csharp
// [Proposed] PRE-elevation. The 2-D domain the layout occupies.
// This is what room packing, corridor routing, density, fill and the
// connectivity precondition consume, and it must exist BEFORE elevation.
sealed class PlanShadow {
    HashSet<Vector2Int> cells;          // today's DungeonLayout.floorCells, renamed in role
}

// [Proposed] POST-elevation. The canonical walkable surfaces.
readonly struct SurfaceKey { Vector2Int cell; int level; }        // canonical identity

sealed class Surface {
    SurfaceKey key;
    OwnerKey owner;             // Room | Corridor | Transition — NOT an int room id.
                                // Most walkable space in this generator is CORRIDOR, and
                                // BuildCellRoomIds (cs:3568) maps room cells only, so a
                                // room-only owner would leave the majority of surfaces
                                // unattributable. See §4.1.
    int layerOrdinal;           // 0-based rank among surfaces at this cell, ascending level.
                                // A LOCAL disambiguator for reports and hashing. NEVER a storey.
    SurfaceKind kind;           // Floor | Deck | Landing | Ledge | Stair (occupied)
}

sealed class SurfaceField {                   // replaces Dictionary<Vector2Int,int>
    IReadOnlyList<Surface> surfaces;                        // canonical, sorted (x, y, level)
    IReadOnlyDictionary<Vector2Int, int[]> byCell;          // ascending level
    bool IsSingleLayer { get; }                             // every cell has exactly one surface
    IReadOnlyDictionary<Vector2Int,int> AsHeightField();    // valid iff IsSingleLayer
    HashSet<Vector2Int> PlanCells();                        // the shadow this field actually occupies
}
```

**[Fact]** `floorCells` is the *domain over which the level field is computed*,
not a product of it: `FillUnassignedFloorCells(layout.floorCells, cellLevels, …)`
(`DungeonLabGenerator.cs:1907`), `CleanPath(connection.path, layout.floorCells)`
(`:2127`), plus the density precondition at `:1154` and the fill metric at `:410`.
It cannot be derived from `SurfaceField`.

**Note the `supportBase` field is gone.** The draft stored it as a scalar on
`Surface` and then used it as `supportBase(surface, direction)` in §7. It is a
**per-(surface, direction) query over the neighbouring column**, not a stored
value, because a surface's four faces can drop to four different things.

`AsHeightField()` is the migration lever: while `IsSingleLayer`, every existing
consumer keeps working byte-identically, and the canonical hash keeps its
current shape. That makes the model change output-neutral, which is what makes
it safe to land before anything interesting is built on it (§13 Phase A).

**On the architecture review's H2, corrected.** The draft claimed deriving
`floorCells` from `SurfaceField` closes H2. It does not, and it could not — the
dependency runs the other way. H2 is specifically that
`TryResolveExternalConnectorPromontories` adds cells to `cellLevels` and never to
`floorCells` (`.CorrectiveConnections.cs:153`), so every metric computed from
`floorCells` describes a dungeon missing its piers. The right closure is an
**invariant**, not a derivation:

> **Shadow agreement.** At the end of planning, every surfaced plan cell must be
> in the shadow: `surfaceField.PlanCells() ⊆ planShadow.cells`.

Checkable, cheap, and it catches the whole class rather than the one instance.
Rejection code `PLAN_SHADOW_DISAGREEMENT`.

**Amended in A2 (2026-07-31), on measurement.** This paragraph originally
demanded equality, and demanded it "in the same step" at each producer. Both were
wrong: a shadow cell with no surface is legitimate (the gap under an external span
deck), and repairing at the producer moves `coreExtent` and re-picks external
connector anchors. The rule is one-directional and applied once at the end of
planning. Reasoning in §13, "Corrected in implementation".

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
- **`Fall` — directed, downward only.** From a **rim surface** — a real surface
  on the upper layer, cardinally adjacent to an opening cell — to the catch
  surface beneath *that* opening cell. **An aperture is not a graph node.** The
  draft treated it as one, which `TraversalEdge`'s two-`SurfaceKey` signature
  cannot express, because the opening's own cells are removed from the layer and
  therefore have no `SurfaceKey`. One `Fall` edge is emitted per
  (rim surface, opening cell) pair, so a multi-cell aperture over a stepped
  lower chamber lands correctly per cell (§5).
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

> **The fall-free subgraph must be connected.**
> That is: delete every directed edge (`Fall`, and `Drop` if adopted), treat the
> rest as undirected, and require one component.

**This replaces the draft's "the full graph must be strongly connected", which
was too weak.** Strong connectivity permits `U --Fall→ L1 --Fall→ L2 --Stair↔ U`:
the graph is strongly connected, yet `L1` cannot leave without taking a second
fall — which contradicts the aperture rule that the return route be a stair or
another **reversible** connector.

Fall-free connectivity implies both of the properties that matter:

- **it implies full strong connectivity** — adding edges to a connected
  undirected graph cannot break reachability — so it subsumes today's
  `IsGloballyConnected` check (`DungeonLabGenerator.cs:8150`) as the degenerate
  single-layer case;
- **it implies per-fall reversibility** — for every `Fall` edge `u → v`, `v`
  reaches `u` without falling, because they are in one fall-free component.

And it is the literal statement of the design intent: *pits primarily create
optional branches*. Every surface is reachable without ever taking a fall, so no
fall is ever mandatory.

**The one case it forbids** is a region whose only entrance is a fall, with a
stair back out. That case is rarer than it sounds — a *bidirectional* return
stair is also an entrance, so it only arises with a genuinely one-way upward
connector. It is raised as an owner decision (§14) rather than silently allowed.

Validation reports the **witness**: for each `Fall`, the fall-free path from its
target back to its source. That is what a diagnosis needs, and it is why a single
authored `returnRouteId` is the wrong shape (§5) — the return is a path, and it
is derived, not declared.

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
sealed class RoomLayer : SurfaceRegion {
    string layerId;                       // authored, room-local ("lower", "gallery", "catwalk")
    int relativeLevel;                    // from the room's base
    IReadOnlyList<RectInt> parts;         // today's multi-rect footprint, per layer
}
```

### 4.1 Corridors own surfaces too — openings are not a room-only feature

**This nearly became an accidental restriction, and it would have been a bad
one.** An earlier revision hung `openings` off `RoomLayer` and typed
`Surface.roomId` as an `int`. Both assume walkable space belongs to a room.

**[Fact] it mostly does not.** Almost every elevation change in this generator
happens *between* rooms, not inside them: `TryResolveConnectionTransition` levels
the corridor and places the transition on the corridor path — a rise-1 step strip
at `delta == 1`, and a reviewed stair, synthesized stair, stairwell tower or
external span at `delta > 1` (`DungeonLabGenerator.cs:2163-2200`). Intra-room
change (zone seams, 1u sweeps) is the minority case. And `BuildCellRoomIds`
(`:3568`) maps room cells **only** — corridor cells are absent from it entirely.

So the owning concept is a **surface region**, of which a room layer is one kind:

```csharp
abstract class SurfaceRegion {
    OwnerKey owner;                       // Room:great-atrium#gallery | Corridor:main-4-5 | ...
    int level;                            // absolute, resolved
    IReadOnlyList<Opening> openings;      // §5 — ANY region may have one
}
sealed class CorridorRun : SurfaceRegion {
    string connectionId;                  // §8.1
    IReadOnlyList<Vector2Int> path;
}
```

What this buys, all of which the design would otherwise have forbidden:

- **a pit in a corridor** — an `Aperture` on a `CorridorRun`, dropping to a route
  below. This is the cheapest possible version of scenario 1 and it needs no
  multi-layer room at all;
- **a ledge or balcony along a passage** rather than only inside a chamber;
- **a corridor crossing over another corridor**, which is the plainest form of
  overlapping traversal and does not involve a room in any way;
- **attributable walls and railings on corridor surfaces** — otherwise the
  boundary grammar has nothing to hang a guard on when a corridor gains an edge.

**Openings, guards and layers are properties of a surface region, never of a room
specifically.** Any rule in this document phrased in terms of rooms should be
read as applying to regions unless it is explicitly architectural.

- `cellRoomIds : Vector2Int → int` becomes `surfaceOwners : SurfaceKey → OwnerKey`
  — an owner, not a room id, because corridor surfaces need one too (§4.1).
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
// AUTHORED: declares only what lies BELOW the opening. Passability is decided
// per rim edge (below), so `Window` is NOT a member — it is a derived
// classification of an opening whose rim is fully guarded or whose drop is too
// shallow.
enum OpeningKind {
    Aperture,   // a catch surface exists under every cell
    Void        // lethal exterior; nothing may exist in any cell's fall column
}
sealed class Opening {
    OpeningKind kind;                    // AUTHORED — the only authored field besides cells
    Vector2Int[] cells;                  // AUTHORED — removed from the owning region's walkable set
    OwnerKey owningRegion;               // a room LAYER or a CORRIDOR RUN — see §4.1.
                                         // A pit in a corridor is a first-class case.

    // DERIVED at validation, never authored. Parallel to `cells`.
    // catchSurfaces[i] = the highest surface strictly below the owning layer in column cells[i],
    // or none. `kind` is then CHECKED against what was derived.
    SurfaceKey?[] catchSurfaces;
    SurfaceKey[] rimSurfaces;            // DERIVED — layer surfaces cardinally adjacent to the opening
}
```

**Two corrections to the draft, both from review finding 2.**

1. **Catch surfaces are per cell, and there are as many as there are cells.** The
   draft carried a single `SurfaceKey? catchSurface` while the rule demanded a
   catch under *every* cell — which only works for a one-cell aperture. A pit
   over a stepped lower chamber needs a different catch per column.
2. **Catch surfaces are derived, not authored.** Authoring them duplicates
   information the surface field already holds, and any authored value can
   disagree with the geometry. Derive "the highest surface strictly below" per
   column, then validate `kind` against the result. This also removes the
   draft's `returnRouteId`: a return is a *path*, often multi-edge, and is
   produced as a validation witness (§3.3) rather than declared.

### The rules

| | `Aperture` | `Void` |
|---|---|---|
| Derived catch | **every** cell must resolve one | **no** cell may resolve one |
| Graph effect | one `Fall` edge per (rim surface, opening cell) pair → that cell's catch surface | none; a sink outside the graph |
| Reversibility | the fall-free subgraph must be connected (§3.3); the witness path is reported | n/a |
| Fall height | ≥ `MinHeadroomLevels` per cell, ≤ a `MaxSurvivableFallLevels` cap | unbounded |
| Renderer | interior cliff faces drop to that column's catch level; soffit under the opening's rim | cliff faces drop to `abyssBase`, as today |
| Rejection codes | `APERTURE_NO_CATCH_SURFACE` (names the offending cell), `APERTURE_FALL_TOO_SHALLOW`, `APERTURE_UNREACHABLE_RETURN` | `VOID_OPENING_OBSTRUCTED` (names the offending cell) |

**Guarding is per rim edge, not per opening** — corrected in round two. The
draft made `Window` an opening-level kind, which cannot express a pit railed on
three sides and bare on one, and that is an entirely ordinary piece of
architecture. Guard state and fall emission both live on the **rim edge**:

```csharp
readonly struct RimEdge {
    SurfaceKey rimSurface;
    int direction;              // toward the opening cell
    Vector2Int openingCell;
    RimGuard guard;             // Bare | Railing | Wall — AUTHORED per edge
}
```

- **A `Fall` edge is emitted only from a `Bare` rim edge.** Railed and walled rim
  edges emit none.
- `OpeningKind` keeps its meaning as the **declaration of what is below**
  (`Aperture` = a catch surface, `Void` = lethal exterior). It no longer doubles
  as a passability flag.
- `Window` becomes the **derived** state of an opening whose every rim edge is
  guarded, or whose drop is below `MinHeadroomLevels`: it contributes `Sees`
  relations and no `Fall` edges. A partly-railed `Aperture` is neither — it is an
  aperture with fewer entrances, which is exactly what the architecture means.

That is the one place where "visible from another layer" and "reachable from
another layer" are decided, and it is decided per edge by declaration plus
geometry, never by inference alone.

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

Delete the `Fall` edge and the remaining graph is still connected, so the
invariant holds. The fall is one-way. The branch is reversible *without
falling*, which is the property strong connectivity alone would not have proved.
Nothing about this requires the fall to be bidirectional, and nothing about it
requires a global floor number.

---

## 6. Overlapping footprint and clearance volumes

**[Proposed]** `StairPlacementLedger`'s five `HashSet<Vector2Int>` become one
prism ledger:

```csharp
readonly struct Prism {
    Vector2Int cell;
    int minLevel;                // INCLUSIVE
    int maxLevel;                // EXCLUSIVE — the band is half-open [minLevel, maxLevel)
    PrismKind kind;
    OwnerKey owner;              // TYPED and stable — see below
}

// Stable, authored-nameable, and unique across owner families. NOT a runtime int:
// an OpenVolume's penetration allow-list is authored content and must be able to
// name its members.
readonly struct OwnerKey { OwnerFamily family; string id; }
enum OwnerFamily { Transition, Recipe, Room, Opening, Corridor, Vista, Promontory }

// The five kinds the current ledger keeps, plus three the layered model adds.
enum PrismKind {
    // --- the five that exist today ---
    Footprint, Landing, Mouth,
    FootprintClearance,          // was `requiredClearance`  — tests against footprints
    TransitionClearance,         // was `requiredTransitionClearance` — tests against mouths
    // --- added by this design ---
    Support,                     // piers, columns, buttresses, stairwell shafts (§7.1)
    Wall,                        // partitions and enclosure walls (§7.1)
    OpenVolume                   // reserved vertical void
}
```

**Three corrections to the draft, all from review finding 3.**

**(a) Half-open bands.** The draft wrote closed `[level, level + 3]` against an
upper footprint starting at `level + 3`, which intersects at the endpoint and
would reject a clearance of exactly 3 — the value today's gate accepts
(`clearance < MinHeadroomLevels` rejects, so `== 3` passes,
`DungeonLabGenerator.cs:2992`). Every band is `[min, max)`; two bands intersect
iff `a.min < b.max && b.min < a.max`.

**(b) Prisms carry an owner.** The clearance rule is stated in terms of "another
surface", so the ledger has to know whose a prism is. Without `ownerId` a
transition's own footprint blocks its own clearance.

**(c) Conflict is an asymmetric per-kind policy, not a symmetric matrix.** Round
one replaced the blanket rule with a symmetric table; round two showed that table
is also wrong, because the real relation is directional and uses five sets, not
four. **[Fact]** `ConflictsWithReservation` (`DungeonLabGenerator.cs:6477`) tests
each *incoming* kind against a specific subset of what is already registered:

| Incoming kind | Tests against the registered… | Note |
|---|---|---|
| `Footprint` | `footprint` ∪ `landing` ∪ `footprintClearance` | this union is `BlocksFootprint` |
| `Landing` | `footprint` **only** | so landing–landing and landing–clearance are **legal** |
| `Mouth` | `transitionClearance` **only** | |
| `FootprintClearance` | `footprint` **only** | so clearance–landing is **legal** |
| `TransitionClearance` | `mouth` **only** | |

Two consequences the symmetric table got wrong: **landing–clearance does not
conflict today, and neither does mouth–clearance.** And the two clearance
concepts are genuinely distinct — one guards footprints, the other guards
transition mouths — so merging them into one `Clearance` kind loses behaviour.

The model is therefore a **`blocksKinds` policy per kind**, seeded verbatim from
the table above, plus:

- **same `owner` never conflicts** — this is what makes clearance expressible at
  all, since a transition's own footprint would otherwise violate its own
  clearance. **`OpenVolume` is the one exception** (below);
- **`Support` and `Wall` block like `Footprint`**;
- **one named predicate owns headroom**, because three passages of an earlier
  revision defined three different blocker sets:

  > **`BlocksHeadroom(kind)` = `kind ∈ { Footprint, Support, Wall }`** — the
  > solid structural kinds.

  `Landing` is deliberately excluded: a landing is itself a walkable surface, and
  surface-to-surface vertical separation is `SURFACE_STACK_CLEARANCE`, a
  different rule. Counting it here would double-report the same conflict. Every
  site that talks about headroom refers to this predicate by name rather than
  restating a set;
- **`OpenVolume` blocks every solid kind — `Footprint`, `Landing`, `Support`,
  `Wall` — except owners on its penetration allow-list, and this holds for
  same-owner solids too.** An atrium that forbade everything would forbid its
  own balconies, stairs and bridges; but the plain same-owner exemption would
  let the atrium's *own* floor fill its own void, which is worse. So
  `OpenVolume` is exempt from the same-owner rule: a room's solids must appear
  on its allow-list explicitly, exactly like anyone else's.

  The allow-list is authored beside the room and names `OwnerKey`s
  (`Transition:atrium-stair-a`, `Room:great-atrium#gallery`), which is why the
  owner key must be a stable authored name and not a runtime integer. It is
  validated against what the room actually declares — an entry naming something
  the room does not own is `OPEN_VOLUME_PENETRATION_UNDECLARED` — so it cannot
  become a blanket exemption.

Today's semantics are the special case where every band is `[-∞, +∞)`, every
owner is distinct, and no `OpenVolume` or `Support` prism exists.

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
   surface S, the half-open prism `(S.cell, S.level, S.level + MinHeadroomLevels)`
   must not intersect any prism satisfying `BlocksHeadroom` that is owned by
   anything other than S.* One rule, one predicate, one call site, no side table
   dropped before the plan is published. Half-open is what makes a clearance of
   exactly `MinHeadroomLevels` pass, matching today's gate.
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

1. **Iterate boundaries, not cells — and decompose each boundary into bands.**
   The draft proposed "a surface's lateral neighbour is the surface at the
   neighbouring cell whose level is nearest to its own". **That is not a
   sufficient algorithm**, and review finding 6 is right to reject it: with
   several surfaces in both columns, nearest-matching can tie, can be
   asymmetric, and can be one-to-many. Columns `{0, 8}` and `{4, 12}` have no
   unique symmetric pairing — 4 is equidistant from 0 and 8, and 8 is
   equidistant from 4 and 12.

   **A second error, caught in round two: an occupied band is *structural*, not
   "down to whatever supports it".** Defining a surface's band as level →
   support fills stacked space with solid mass. Column A with surfaces at 0 and
   8, beside column B with a surface at 0, would give A's upper surface the band
   `[0, 8]` and emit a face across the whole 0–8 range on the A/B boundary —
   **walling off the very chamber the gallery is supposed to overlook.** That is
   the heightfield assumption reappearing one level down, and it would break C1
   in particular, which has no `OpenVolume` producer available to subtract the
   mass again.

   **Round four corrected the correction.** The first repair made the plinth
   conditional on *the neighbour being void*, which produces nothing at all for
   the commonest case in the corpus: two adjacent ground floors at levels 0 and
   4, where neither neighbour is void. **[Fact]** today that emits a retaining
   face from 0 to 4 unconditionally (`ElevationEdgeModel.cs:2389-2394`), so the
   rule as written would have deleted every retaining wall in every dungeon and
   falsified the byte-identical claim outright.

   The distinction is not "is the neighbour void" but **"is the mass under this
   surface earth, or open air"**:

   > **`IsGroundBacked(s)` = `s.kind == Floor` **and** `s` is the lowest surface
   > in its column.**

   Both conditions are needed. A bridge deck over a true gap *is* lowest in its
   column but must not become a solid pillar, which the `kind` test excludes. A
   gallery slab over its room's own lower chamber *is* a `Floor` but is not
   lowest, which the column test excludes.

   | Band source | Extent | When |
   |---|---|---|
   | **Ground** | `[abyssBase, level)` | `IsGroundBacked(s)` — the surface rests on fill |
   | **Slab** | `[level − 0.5, level)` | otherwise — a suspended deck, gallery or ledge implies only its own depth. **0.5u is measured** from the `_E_` floor family (§0.1), not a placeholder |
   | **Support** | authored prism | piers, columns, buttresses, a stairwell tower's shaft — declared, never implied |
   | **Wall** | authored prism | partitions and enclosure walls, which keep their own grammar |

   The three cases that matter, all falling out of one decomposition:

   | Configuration | Result |
   |---|---|
   | floors at 0 and 4, both ground-backed | `[abyss,0)` both solid → interior; `[0,4)` one solid → **retaining face 0→4** ✓ today's behaviour |
   | floor at 0, neighbour void | `[abyss,0)` one solid → **cliff to the abyss** ✓ today's behaviour |
   | gallery at 8 over a chamber at 0, neighbour floor at 0 | `[abyss,0)` both solid → interior; `[0, 8−t)` **neither solid → open air**; `[8−t,8)` one solid → fascia ✓ the chamber stays open |

   **Single-layer output is unaffected because every surface is a lowest-in-column
   `Floor`**, so every band is `[abyssBase, level)` and the decomposition
   reproduces today's retaining walls and abyss cliffs exactly. The change bites
   only where surfaces stack — which is the point.

   The construction then operates on the **boundary between two columns**, not
   on a surface:

   - take the two columns' *structural* bands per the table above;
   - merge the two columns' band endpoints into one sorted set of cut levels;
   - walk consecutive cut intervals bottom to top. Each interval is classified
     by which side is solid: **both solid** → interior (no face); **one solid**
     → a face on that side, typed by what sits at the interval's top on the
     open side (retaining wall if a surface is level with it, cliff otherwise);
     **neither** → open air, no geometry;
   - a face's *guard* (railing, partition, bare) is then decided per interval by
     the existing rules, scoped to the surface that owns the interval's top.

   This is a genuine algorithm with its own tests and its own failure modes, not
   a loop edit. It is the technical heart of the phase.

2. **`abyssBase` is subsumed by the plinth band — it is not a second
   mechanism.** Round three caught this prescribing geometry that contradicts
   step 1: for the `{0,8}` vs `{0}` example, the band decomposition emits only
   the upper slab's fascia, while an independent "drop to the highest surface
   below" rule would emit a face from 8 down to 0 and wall off the chamber —
   reintroducing exactly the defect step 1 removed.

   **Structural intervals are authoritative.** Face extents come from the band
   decomposition and nothing else. Full-height geometry appears only where a
   **ground**, **wall** or **support** band exists. `supportBase(surface,
   direction)` survives only as the name for *where a ground band bottoms out* —
   the abyss base, since a ground band exists only when `IsGroundBacked` holds.
   It never extends a suspended slab downward.

   **The draft's "~30 lines at one site" estimate is retracted.** It was
   predicated on the nearest-neighbour shortcut, which does not work.
3. **A soffit pass.** Every surface that is not the lowest in its column needs
   an underside. **[Fact]** this is a known open item — "the pack has no flat
   under-deck cap family measured yet — deck undersides may read open from
   below" ([`stair_forge_design.md`](stair_forge_design.md) decision 31). Today
   that is cosmetic because you can only pass under a bridge in a corridor. Once
   you can stand under a gallery inside a room, it is load-bearing. **This is
   the one place the design depends on unmeasured art content**, and it should
   be measured before Phase C commits.

~~Edge keys (`EdgeKey`, `OpenEdgeKey`, `WallEdge` at `(x, z, direction)`) gain a
level discriminator. **[Inference]** this is mechanical but wide — these types
thread through corner selection, gateway sockets, shell placement and trap
placement. Budget accordingly; it is the bulk of Phase C.~~

**Retracted 2026-07-31 in C1b, and it was wrong in both directions.** The
inference was wide where it should have been narrow and silent where it should
have spoken.

- **`WallEdge` already has one** — `lowerLevel` and `higherLevel` are its extent.
- **`EdgeKey` does not need one.** It is an unordered cell pair naming a column
  relation (transition suppression, doorways, gateway flanks), and it becomes
  ambiguous only when one column pair emits two faces — which needs two mass
  bands in a column, and Support and Wall prisms have no producer.
- **`OpenEdgeKey` does not need one either, and giving it one would break
  things.** Its producers all describe the level field, so every one of them
  would write `levels[cell]` into the key — a rename, not a discriminator — and
  several of them name cells that are not in the level field at all, where the
  added component would silently stop matching.
- **The type that actually could not name a stacked surface is not in the
  list.** The railing-only edge list is `(x, z, direction)` and derived its
  placement height by looking the cell up in the level field, so a gallery rim
  would have been railed at the chamber's height below it. It is now
  `RimEdge (x, z, level, direction)`.
- **The `(x, z, direction)` suppression sets are the second site** —
  `shellGuardEdges` and `bareLandingEdges`, both built from heightfield stages,
  so they describe column floors and must not reach a rim above one.

None of this threads through corner selection, gateway sockets, shell placement
or trap placement, because none of those sees a stacked surface in C1. It was
not the bulk of Phase C; the rim guard pass and the per-surface floor were.

**Compatibility lever:** while `field.IsSingleLayer`, `supportBase` returns the
global abyss base and the surface loop degenerates to today's cell loop. Every
existing seed renders byte-identically. That is the property to gate Phases A
and B on.

### 7.2 Collision export — probably no structural change, and here is the actual mechanism

> **MEASURED LIVE 2026-07-31. The hypothesis holds — all four behaviours, on the
> real server, against the real ground sampler.** `ops/c1-two-layer-live.sh`
> bakes the C1 episode through the production export path, publishes it, and runs
> `ops/c1-two-layer-probe.py`:
>
> | Behaviour | Measured | A failure would have read |
> |---|---|---|
> | under a gallery, on the chamber floor | `y = +0.000` | `+3.50` (captured by the soffit) |
> | on the gallery slab | `y = +4.000` | `+3.50` (standing on its own underside) |
> | walking off the aperture's bare rim | `y = +0.000`, 0.2u from the aperture cell | `−20` (the abyss base) |
> | mid-span on the aerial deck | `y = +4.000`, lower route at `+0.00` | — |
>
> **What this does and does not settle.** It settles that the `max`-selection
> hazard §7.2 reasons about does not bite at a 4-level rise: a soffit 3.5u above
> a player's feet is far outside the 1.2u window. It does NOT validate the
> remedy this section recommends — per C1a's re-measurement the `_E_` slab is a
> **convex mesh**, so what was tested is a normal-filtered 1.2u capture, not the
> box collider's unfiltered 0.35u one. The remedy was unnecessary here; whether
> it works is still unmeasured.
>
> **Two things the live leg found that no plan gate and no render digest could
> have.** Both are recorded because both cost a failed run:
>
> 1. **The outer shell pass is heightfield-only, and it walled the upper route
>    shut.** Where a ground-backed terrace at L4 met a suspended gallery at L4,
>    the shell put a 5.7u enclosure wall across the seam
>    (`shell_0_10_4_0`, `y[4.00, 9.70]`, `z[41.75, 42.25]`) and the probe player
>    could not cross it. The retaining face underneath is CORRECT and stays —
>    from the chamber below, the terrace really is a wall from 0 to 4, and its
>    top sits flush at y=4. It is the guard *on top* that was wrong. Fixed by a
>    **flush seam**: a face whose open side carries a walkable surface at the
>    face's top level suppresses its top guard, which suppresses the shell
>    courses, the railing and the railing corner columns in one move, because all
>    three already key off `WallEdge.suppressRailing`. Inert on a single-layer
>    plan by construction — a retaining face has the open side's floor at its
>    BOTTOM and a cliff has it at or below the bottom, so no single-layer face
>    can carry a surface at its top on the open side.
> 2. **The module hard-validates the dungeon door manifest as NON-EMPTY**
>    (`world_interactions.rs:930`, inside a `OnceLock` that `game_tick` touches
>    every tick). Baking geometry that owns no gateways exported an empty
>    manifest and every tick panicked, so the probe player never moved while
>    every reducer reported `Committed`. Doors and traps are deferred work and
>    the episode has none, so the bake leaves both manifests alone.
>
> **And a constraint worth stating once:** there is no runtime world selector.
> World collision is `include_str!` at compile time (`open_world_scene.rs`), so
> the only way to put a fixture in front of the real ground sampler is to bake it
> into the dungeon's own payload. `ops/c1-two-layer-live.sh` bakes, publishes and
> probes; rebuild a dungeon afterwards if you want one.

**The draft was wrong here and its central claim was unproven.** It required
that "soffits, columns and deck undersides must not be marked `walkable_top`".
**[Fact]** exported dungeon geometry carries no such field:
`GameplayCollisionBoxFile` is `{shape, center, size, rotation, rotation_y_deg}`
(`server/src/world_collision.rs:120`), and `walkable_top` belongs to the
hardcoded procedural `Collider` used by authored open-world scene profiles
(`:106`, set at `:3606`/`:3670`). Every exported box top under the step ceiling
is a ground candidate with no flag consulted (`:1522`).

**The real controls, measured.** Ground sampling in
`try_open_world_surface_height_at_y` (`:1464`) admits a surface through three
gates, and these are the numbers this whole design should be derived against:

| Control | Value | Applies to |
|---|---|---|
| Mesh capture window | `SURFACE_SNAP_UP = 1.2` (`server/src/arena.rs:932`) | mesh hulls + procedural colliders |
| Box capture window | `GAMEPLAY_BOX_STEP_UP_HEIGHT = 0.35` (`world_collision.rs:72`) | exported boxes |
| Ground-normal filter | `GAMEPLAY_MESH_GROUND_MIN_NORMAL_Y = 0.35` (`:67`) | mesh hulls only |

**[Fact]** the dungeon's floors are prefab mesh colliders, so the live capture
window is **1.2u**.

**Correction, round two — the normal filter is NOT a direction filter.** A
previous revision of this section claimed a soffit's downward-facing geometry
cannot become ground "because its normal points down". That is wrong. The server
computes `triangle_normal_y_abs` as `(cross[1] / length).abs()`
(`world_collision.rs:2820`) and the gate reads `hull.ground_normal_y_abs`
(`:2826`). **The test is on the absolute Y normal, so a down-facing triangle
passes exactly like an up-facing one.** The filter rejects *near-vertical*
surfaces, not *inverted* ones.

Four consequences, replacing the previous three:

- **Two walk surfaces closer than 1.2u are ambiguous to the ground sampler.**
  This is the missing derivation for `MinHeadroomLevels`: 3u clears 1.2u with
  margin, so the constant is defensible rather than merely inherited. Any
  proposal to lower it must clear 1.2u first. *(Unaffected by the correction —
  it comes from the window, not the normal.)*
- **The soffit hazard is narrower than previously stated, in the other
  direction.** Selection takes the **highest** eligible surface (`:1557`), so a
  soffit whose top lies below a deck that covers the same point can never win
  over the deck. And a player on the *lower* floor is 3+ levels beneath the
  soffit, far outside their 1.2u window. The previously stated failure — "a
  player on the deck could snap down to the soffit top" — does not occur.
- **The real remedy is the collider *shape*, and it is cheaper than any of the
  obvious three.** Rather than excluding soffit faces from movement collision,
  editing exported geometry, or making the server require a signed normal:
  **emit soffits as box colliders.** Boxes are not normal-tested at all, use the
  0.35u window, and obey the same `max` selection. A soffit box under a deck 3+
  levels up sits outside a lower player's 0.35u window, and for a player on the
  deck its top is below the deck's own, so `max` keeps the deck. Safe under both
  rules, with no server change.

  **Excluding the soffit from movement collision would be the wrong fix**, and
  this is why: the dungeon exports with `reuseMovementCollisionForQueries: true`,
  so its movement geometry *is* its query geometry. Dropping the soffit from
  movement collision would also drop it from LOS, and sight would pass straight
  up through a solid floor. **With that flag set, geometry cannot block sight
  without also blocking movement** — which is an argument for revisiting the flag
  for the dungeon, tracked as a deferred item below.
- **The lower surface under a deck must still emit its floor** — already handled
  for aerial decks via `bridgeFloorBlockedCells` (`ElevationEdgeModel.cs:377`);
  generalize it to all stacked surfaces.

**Status of the claim.** "Collision export needs no structural change" is a
**hypothesis, not a finding**, and it has now been wrong twice about the
mechanism. The existing stacked-crossing fixture inspects Unity collider bounds
in the editor (`DungeonLabGenerator.StackedCrossingFixture.cs:190`); it
demonstrates nothing about server movement. Phase C's live probe is what tests
it, and it must explicitly assert: a player standing on the deck stays on the
deck, a player under the deck is not captured by the soffit, and a fall through
an aperture lands on the catch surface. **Treat the reasoning above as a
prediction the probe falsifies or confirms, not as established behaviour.**

If the probe fails, the remedy order is: collider shape (box soffits) →
exporter change → **server change last**, because `triangle_normal_y_abs` is
shared with every other scene and some content may rely on inverted winding
being walkable.

**[Fact]** the dungeon collision bake is not byte-stable across rebuilds, so it
can never serve as the output-neutrality diff. Neutrality must be proved on the
plan hash and the scene, not the payload.

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

**But the layer binding cannot reach the surface field as the pipeline stands —
this is review finding 1, and it is a prerequisite, not a detail.**

**[Fact]** `RoomConnection` is `{ int fromRoom, int toRoom, List<Vector2Int> path }`
and nothing else (`DungeonLabGenerator.cs:7899`). Worse, a connection is matched
to its route edge **by room pair**:
`requirements.TryGetTransition(connection.fromRoom, connection.toRoom, out _)`
(`:2700`). So two corridors between the same two rooms at different elevations
are indistinguishable at the lookup, before their paths are ever compared. A
`fromLayer`/`toLayer` binding authored in the topology would be discarded at
exactly this point.

**[Proposed]** `RoomConnection` gains the identity it is missing. **Round two
corrected this twice over** — the first attempt keyed on a room-local layer name
and carried a layer *ordinal*, both of which are unusable:

```csharp
readonly struct RoomConnection {
    int fromRoom, toRoom;
    ConnectionSource source;    // NEW — RouteEdge | SynthesizedLoop
    string edgeId;              // NEW — the route edge, when source == RouteEdge
    string connectionId;        // NEW — stable synthetic id, ALWAYS present
    LevelBand plannedBand;      // NEW — the corridor's planned vertical extent (below)
    List<Vector2Int> path;      // unchanged: the plan shadow of the corridor
}
enum ConnectionSource { RouteEdge, SynthesizedLoop }
```

Three things this fixes:

- **`source` admits synthesized loops.** **[Fact]** `AddLevelSafeLoopConnections`
  creates `RoomConnection`s carrying no route intent at all
  (`DungeonLabGenerator.cs:1503`), and the elevation path deliberately treats the
  route requirement as *optional* — `bool hasRouteRequirement = routeRequirements
  != null && routeRequirements.TryGetTransition(…)` (`:2097`). An invariant
  demanding every connection resolve to a route edge would reject every loop
  corridor in the corpus. The invariant is instead: **a `RouteEdge` connection
  resolves to exactly one route edge; a `SynthesizedLoop` resolves to none; every
  connection has a unique `connectionId`.**
- **`connectionId` is always present**, so a loop corridor is nameable in
  diagnostics and reservations even though it has no edge.
- **`plannedBand` replaces the room-local layer key.** A `layerId` is
  room-local — one room's "gallery" is not another's — so `(cell, layerId)` is
  not a vertical identity, and an edge's two ends may bind to differently-named
  layers. A layer *ordinal* is worse: §3.1 states ordinals are local, unstable
  and never identity.

**What is globally meaningful pre-elevation is the topology's declared absolute
level.** `RouteTopologyNode.level` is authored and absolute, and `TryAssignRoomLevels`
is a deterministic copy of it — so an edge's endpoint elevations are known before
embedding, let alone before the level field. `plannedBand` is
`[min(fromLevel, toLevel), max(fromLevel, toLevel) + MinHeadroomLevels)` over
those declared levels, for a route edge; for a synthesized loop it is the band of
the two rooms it joins.

Corridor exclusivity then becomes: **two connections may share a plan cell iff
their `plannedBand`s do not intersect.** That is a global, half-open comparison
available at claim time, it degenerates to today's rule when every node sits in
one band, and it needs no room-local naming. The prism ledger later proves the
*resolved* elevations actually clear (§6); `plannedBand` is the pre-elevation
approximation that lets the claim happen at all.

`TryGetTransition` keys on `edgeId` instead of the room pair.

**Corridor exclusivity is the deeper block, and it cannot be fixed with levels.**

**[Fact]** `TryClaimCorridor` rejects any cell another connection already owns
(`RouteFirstPilot.cs:2582`), and `PathCrossesThirdRoom` rejects a path crossing
an unrelated room (`:2569`). Both run in the **layout** stage — *before*
elevation exists. There is no level to compare at claim time, so band-aware
exclusivity is not available here.

**[Proposed]** exclusivity keys on `(cell, plannedBand)` — the band derived from
the topology's declared absolute node levels, as defined above. Two corridors may
share a plan cell iff their bands are disjoint.

**This relaxation is NOT output-neutral, and it does not belong in Phase A.**
Round three caught a claim to the contrary. **[Fact]** today's rejection is
unconditional — `if (claimedCorridorCells.Contains(cell))` → fail
(`RouteFirstPilot.cs:2582`) — and existing topologies already carry widely
separated levels: `atrium-ring` spans 0 to 24, so two of its edges routinely have
disjoint bands. Relaxing the rule would therefore accept embeddings that are
rejected today, move the layout hash, and — worse — could produce two corridor
surfaces over one cell during a phase whose stated non-goal is "no second surface
anywhere".

The split is therefore:

- **Phase A carries the data only** — `source`, `edgeId`, `connectionId` and
  `plannedBand` are recorded on every connection, and `TryGetTransition` re-keys
  on `edgeId`. The exclusivity **rule stays unconditional**, so the pass is
  output-neutral and provable.
- **Phase D authorizes the relaxation** — and the distinction between
  *authorizing* and *testing* is load-bearing:

  > **Layer binding authorizes an attempt. The absolute band decides.**

  A layer name is room-local (§8.1's own warning), so "they are on different
  declared layers" can never establish vertical separation between two
  *unrelated* rooms — one room's `gallery` and another's `floor` may sit at the
  same absolute level. Any earlier phrasing to the contrary is superseded.

  The test, for both corridor sharing **and** third-room crossing, is the
  absolute, layer-offset-adjusted band:

  ```
  plannedBand = [ min(endpointAbsoluteLevels),
                  max(endpointAbsoluteLevels) + MinHeadroomLevels )
      where endpointAbsoluteLevel = node.level + layer.relativeLevel
  ```

  Two connections may share a cell only when **both** are layer-bound (the
  authorization) **and** their absolute bands are disjoint (the test). A
  connection with no declared layer keeps today's unconditional exclusivity
  forever. `PathCrossesThirdRoom` relaxes the same way and on the same absolute
  comparison — never on layer names.

  > **Shipped in D2, with two qualifications this section does not state.** The
  > third-room relaxation is **upward only** — the band must CLEAR the room's
  > ground, not merely miss it, because passing under would have to suspend the
  > room's own floor and a room floor is ground-backed by construction. And the
  > producer's surface KIND is decided by where the corridor lands in the
  > column, not by the corridor being a corridor: measured, 6 of 11 cells of one
  > crossing corridor stay ground-backed floors. See `CURRENT_STATUS.md`.

`PathCrossesThirdRoom` becomes *"must not cross a third room **on the same
layer**"*. Its own justification licenses this: the harm it names is "an
undeclared doorway and an unowned threshold", and a corridor passing *over* a
room at a different elevation creates neither. The matching topology rule — no
third node on an edge's lattice lane — relaxes the same way, and only for edges
whose declared layers differ.

Two further things worth stating plainly:

- **`RouteTransitionResolution` already carries `edgeId`** (`:8229`), so edge
  identity partly survives today — for *transitions*, not for *corridors*. This
  is the narrower half of the architecture review's **H5** ("the semantic model
  does not survive the pipeline"), and closing it is **a prerequisite of Phase D**
  rather than the opportunistic item review 2.7 called it. Multi-layer routing
  is the feature that makes H5 load-bearing.
- Two corridors sharing a plan cell at different layers is exactly the case
  `PlanShadow` (§3.1) exists to keep coherent: they contribute the same shadow
  cell once and two distinct surfaces.
- `RequiredTransitionCorridorCells`
  (`DungeonLabGenerator.TransitionReservation.cs:50`) already keys on
  `(kind, rise)` and prices larger rises without change — but the reviewed
  contract set may have no entry beyond rise 8, in which case it returns *the
  widest measured requirement* and the seed fails by name later. That is correct
  behaviour, and it means **an envelope raise to 40u does not by itself require
  bigger single rises** — it requires more of them.

### 8.2 Recipes — multi-layer authored rooms

> **Verified against the code 2026-07-31, and three of this section's mechanisms
> dissolve while a fourth blocks the phase.**
>
> 1. **`baseLevel` is not derived from port zero. It IS the node's level.**
>    `expectedRelativeLevel = node.relativeElevationLevels + port.relativeLevel`
>    (`Recipes.cs:1089`) and every port is then required to resolve at exactly
>    that (`:1794`). So `firstPortLevel − firstPortContract.relativeLevel`
>    (`:1791`) algebraically equals the node level, and every other port already
>    has to agree. The "ordering dependence" this section sets out to fix is not
>    there.
> 2. **`RECIPE_BASE_LEVEL_CONFLICT` is therefore unnecessary** — the
>    all-candidates-agree property is already enforced, and by a stronger rule.
>    The layer-aware form is just
>    `expected = nodeLevel + layer.relativeLevel + port.relativeLevel`.
> 3. **`anchorLayerId` / `anchorLevel` is unnecessary too.** A base layer with no
>    external port is not a problem: the base is the node's level whether or not
>    any port sits on it.
> 4. **THE BLOCKER. A two-layer authored room cannot be resolved at all today,
>    and it fails SILENTLY.** The resolver writes zone levels with
>    `cellLevels[cell] = baseLevel + zone.relativeLevel` (`Recipes.cs:1943`) into
>    a `Dictionary<Vector2Int,int>`. Two zones on different layers over one plan
>    cell do not stack and do not reject — the second overwrites the first. So
>    C2's authored episode is blocked on the level field becoming a
>    `SurfaceField`, which §12/2.5 lists as a Phase D prerequisite and which is
>    really a **C2** prerequisite.
>
> **That migration is smaller than "247 references" suggests.** The reader side
> is deferred by `AsHeightField()`; the WRITER side is 11 sites in 6 functions:
> `TrySetCellLevel` (`cs:6530`) and `TrySetPlannedStairCells` (`:6547`), plus
> four deliberate bypasses — `FillUnassignedFloorCells` (`:6628`, `:6638`),
> `TryResolveNamedVistaPromontory` (`:2888`) and the recipe zone write
> (`Recipes.cs:1943`). Three of the four bypasses only ever insert into cells the
> field does not yet hold; only the recipe one overwrites. Threading a single
> `SurfaceField` through `TryBuildCellLevelField` (`:1781`),
> `TryResolveConnectionTransition` (`:2122`, one caller) and
> `TryResolveNamedVistaPromontory` covers all of them.
>
> **So the order is: level field → `SurfaceField` (writer side), then this
> section, then the authored episode.**
>
> ---
>
> **DONE 2026-07-31 — the writer-side migration landed, and the map above was
> missing a producer.** There are **five** bypasses, not four.
> `TryResolveExternalConnectorPromontories` writes pier levels at
> `.CorrectiveConnections.cs:170` using `cellLevels.Add(…)` rather than the
> indexer, which is exactly why a `cellLevels[` search did not surface it — and
> §3.1 above names that same function as H2's producer of cells that reach the
> level field, so the omission is this document's, not the code's. It is an
> insert-only site like the other four; `Dictionary.Add` was already asserting
> that.
>
> The port replaces every one of those writes with a named operation on
> `SurfaceField`: `TrySetFloorLevel` (set, reject a conflict — the old
> `TrySetCellLevel`), `AddFloorLevel` (insert into an unsurfaced column — all
> five bypasses collapse onto it) and `RelevelFloor` (move an existing floor —
> the recipe zone write, and nothing else). All three are LAYER-BLIND and throw
> on a column carrying a stacked surface, which generalises L6: no layer-blind
> writer can silently truncate a stacked column, whoever writes it next.
> `TryBuildCellLevelField` now out-params the field and `TieredLevelPlan` takes
> it, so the stage's own field survives instead of being rebuilt from its
> heightfield.
>
> **Two further facts worth carrying.** (a) The recipe overwrite is a LIVE path
> — `Elevated` zones must carry `relativeLevel > 0`
> (`DungeonRecipeValidation.cs:363`) and four enabled recipes have one — so the
> gate exercised it on essentially every seed rather than stepping around it.
> (b) `ReconcilePlanShadowWithSurfaces` must iterate the field's BACKING STORE,
> not `Surfaces()`: it inserts into `layout.floorCells`, a `HashSet` whose
> enumeration order follows its insertion order and which later passes read, so
> switching to canonical order moves seeds.
>
> Gate: `ops/dungeon-port-ab.sh` density 0 — identical geometry on all 200 seeds
> with `resultHash` unmoved at `991d86e1bb577144`, i.e. byte-identical seed
> reports.
>
> ---
>
> **DONE 2026-07-31 — the layer schema below landed as written, with four
> corrections.** `DungeonRecipeLayer`, `layerId` on zones and ports, layer-scoped
> `RelativeLevelAt`, per-layer base derivation and `RECIPE_LAYER_CONNECTIVITY`
> are all in. The resolver branches at the L6 site: base storey →
> `RelevelFloor`, any other storey → `AddSurface`. Gate: identical geometry on
> 200 seeds, the only per-seed report difference anywhere being `schemaUsage`
> growing 19 → 21 rows.
>
> 1. **Transitions need `layerId` too, on BOTH ends** — this section lists it
>    only on zones and ports. A stair is what makes two storeys connected, so
>    `RECIPE_LAYER_CONNECTIVITY` cannot be evaluated without knowing which
>    storeys each transition joins, and a transition spanning layers cannot
>    inherit a single one.
> 2. **`riseLevels == 1` had to be relaxed for cross-layer stairs only.** Not
>    mentioned here, and blocking: C1's episode separates its storeys by 4u, so
>    a layered recipe was unauthorable while every recipe transition was pinned
>    to a 1u rise. Intra-layer transitions keep the exact rule, which is what
>    keeps single-layer recipes untouched.
> 3. **"Elevated zones must be allowed to go negative" is unnecessary.** A sunken
>    chamber is a LAYER at a negative `relativeLevel`; the zone still rises within
>    its own storey. `DungeonRecipeValidation.cs:363` keeps its rule unchanged,
>    and one fewer gate is relaxed.
> 4. **A recipe's canonical string is inside `hashes.canonical`.**
>    `ComputeContentDigest` → `catalogDigest` → the route-intent projection →
>    `routeIntentHash` → `canonicalHash`. An unconditional append to the recipe
>    canonical therefore moves every seed's canonical hash for a schema addition
>    that changes no geometry. Every layer field is appended only when
>    `recipe.DeclaresLayers` — the same conditional the incident-socket fields
>    already used.
>
> **The remaining C2 cost is the READER side, which this section does not
> mention at all.** A stacked field throws at the first `AsHeightField()`; there
> are 8 such sites, two of which fan out to ~27 readers between them. Most ask
> "what is the floor at this cell" and are already correct on a heightfield —
> classifying which genuinely need a surface is the work, not sweeping them.
>
> ---
>
> **DONE 2026-08-01 (D3) — the two vocabularies are equated at the SLOT, and
> that is why nothing in this section had to change.** A recipe's layer ids and a
> topology's are independent by design (§8.1: a layer id is room-local), so
> `"layers": { "gallery": "upper" }` on a slot is the only place they can meet.
> Given that mapping, the candidate gate proves the two storeys sit at the same
> relative level before the recipe is admitted — and once that holds, every
> expression in this section is already right: the port's expected elevation
> stays `nodeLevel + recipe layer offset + port.relativeLevel`, and `baseLevel`
> stays `firstPortLevel − PortLayerRelativeLevel − port.relativeLevel`. The
> mapping's job is to make two vocabularies provably describe one elevation, not
> to convert between them.
>
> Two limits the section does not state. **A port must be authored on the storey
> its edge arrives on** — every port in the catalog is on its recipe's base, so
> `episode_layered_gallery_01` can be MAPPED at its gallery but not ROUTED to
> there until a port exists (a D5 content prerequisite, not a defect). And **an
> `IncidentCardinalSockets` recipe cannot be routed to on a storey at all**: it
> binds entrances by direction, so nothing in the route can name a socket's
> storey, and the rule requires every socket on the base. That is the concrete
> shape of owner decision 9's gap.

**[Proposed]** three schema additions, all additive and defaulting to today's
behaviour. **Phase split, corrected per review finding 4:** the layer fields,
layer-scoped `RelativeLevelAt`, per-layer base derivation and
`RECIPE_LAYER_CONNECTIVITY` land in **Phase C**, because they are what a
two-layer authored episode is made of. `OpenVolume`, sunken zones, and
entrance-elevation binding across several route edges stay in **Phase D**.

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

- **`baseLevel` derivation must change — and not to "the base layer's first
  port".** Today it is `firstPortLevel - firstPortContract.relativeLevel`
  (`DungeonLabGenerator.Recipes.cs:1791`), anchoring the whole room off port
  zero. Round three rejected the first replacement for keeping that ordering
  dependence and for failing outright on a valid room whose base layer has no
  external port — a lower chamber reachable only by falling, for instance.

  **[Proposed]** every bound port yields a candidate base:

  ```
  candidate = absolutePortLevel − layer.relativeLevel − port.relativeLevel
  ```

  **All candidates must agree**, and disagreement is a rejection
  (`RECIPE_BASE_LEVEL_CONFLICT`) naming the two ports — which is a far better
  diagnostic than silently anchoring on whichever port sorted first. A recipe
  with no bound ports declares an explicit `anchorLayerId` + `anchorLevel`
  instead. Order no longer affects the result.
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
| Gateway | the **surface** on the entering side | `surfaceOwners` replaces `cellRoomIds`. The existing both-flanks rule applies **within a layer**; do not let a wall on another layer count as a flank. Do not re-litigate the chamfer ruling |
| Partition wall | the two surfaces it separates, same layer | unchanged grammar, level-banded key |
| Cliff wall | the higher surface | drop target = `supportBase` (§7), not the global abyss |
| Railing | the surface whose edge it guards | unchanged; existing suppression rules (deck-even edges, bridge ports, stair mouths) extend per layer |
| **Aperture rim** | the layer that owns the opening, **per rim edge** | `Bare` \| `Railing` \| `Wall`, authored per edge (§5). Only a `Bare` edge emits a `Fall`. A pit railed on three sides and bare on one is an ordinary and representable case; it is still an `Aperture`, with one entrance |
| Soffit / underside | the surface *above* | new; the surface below never emits a ceiling |
| Bridge deck | **the room it belongs to**, if any, else the connector | new — today a deck belongs to nobody, which is why bridges over rooms are banned |

### 8.4 Future-compatible NPC navigation

**[Proposed]** export the surface graph as a data artifact beside the collision
payload — `random_dungeon.navsurfaces.shared.json` — containing nodes
`(cell, level, owner, surfaceId, kind)` and typed edges
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
  becomes sorted `(cell, level) → {layerOrdinal, owner, kind}`. **[Proposed]**
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

| | Column-boundary band decomposition ★ | Per-sheet re-invocation | Separate overlay renderer for upper layers |
|---|---|---|---|
| Grammar preserved | yes | yes | **no** — a second grammar to keep in sync |
| Cross-layer support base | one function | needs cross-sheet query anyway | needs it anyway |
| Cost | wide, mechanical, one pass | needs §9.1 option C | least up-front, highest long-run |

**Recommend the column-boundary band decomposition** (§7.1). The overlay
approach is the trap: it looks
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
| **Single-surface generation** | Preserved exactly while `IsSingleLayer`. `AsHeightField()`, `IsGroundBacked` making every single-layer band `[abyssBase, level)` so retaining walls *and* abyss cliffs reproduce exactly (§7.1), and the old canonical projection shape make **A1 and B** output-neutral by construction — provable with `ops/dungeon-port-ab.sh`. **A2 is not**, and is deliberately rebaselined on its own. |
| **`MaxGeneratedLevel` 24 → 40** | **Not** output-neutral, and it breaks all seven topology files: `anchors.top` must equal `MaxGeneratedLevel` exactly (`DungeonRouteTopologyValidator.cs:366`). **[Proposed]** relax that rule to "the top anchor is at the topology's declared ceiling" and add an optional per-topology `ceiling` (default 24, capped at the global 40). Existing dungeons then keep their shape and a *new* topology opts into depth. Raising the constant alone would stretch every shipped dungeon and is the wrong lever. |
| **Density dial** | Fill passes must query the prism ledger; `OpenVolume` joins the authored-void exclusion list. `floorFillPercent` becomes ambiguous under stacking — **[Proposed]** keep it as *plan-cell* fill (unchanged meaning, unchanged tuning) and add `surfacesPerPlanCell` as a separate reported metric. Do not redefine the number the dial was tuned against. |
| **Recipes** | Additive fields, empty `layerId` = today's behaviour. The four enabled recipes need no edit. |
| **The seven topologies** | **Leave all seven untouched through A1, B and C** — they are the corpus that proves output-neutrality, and redrawing one destroys the baseline. They stay structurally valid because layers do not move lattice positions or footprints (§8.1). Add layered topologies as *new* files in Phase D; that is the cheap path the topology-as-data work already bought. **Measured 2026-07-29** (§10.1): the corpus is far more uniform than authorship would explain, and three of the seven are workarounds for the capability this design adds. |
| **The three required slots** | **Unresolved, not unchanged.** A two-layer *episode* is a new recipe and needs no slot change, but a vertical hub cannot be a slot at all: a slot node must have degree 2 and a topology declares exactly three slots (L10d). Owner decision 9 (§14) settles it; until then, treat an atrium as a generic multi-layer room outside the slot system. The silent slot-geometry rule ([`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md) "Slot geometry") still applies and still is not machine-checked. |
| **Existing tests** | Per `CLAUDE.md`, do not repin. `DungeonLabStackedCrossingTests` asserts `transitionCount == 1` and `stackedCoordinateCount == 1` on a hand-built fixture — extend the *fixture*, and loosen or delete assertions that pin seed-derived counts. Report the rest; do not fix unasked. |
| **`H2` (two floor representations)** | Closed by the **shadow-agreement invariant** (§3.1), *not* by deriving `floorCells` from `SurfaceField` — the dependency runs the other way, since `floorCells` is the domain the level field is computed over. A1 detects and reports the disagreement; **A2 repairs it and moves the hash**, because `floorCells` feeds `canonicalHash` (`Batch.cs:4864` → `:3437`). Do not attempt both in one gate. |
| **Server / collision** | **Hypothesis, not a finding, and the mechanism has been misstated twice** (§7.2). No change is *expected*: soffits emitted as box colliders should be safe under both the 0.35u box window and the `max` selection rule. But the server's ground-normal test is on an **absolute** Y normal, so direction filters nothing, and only Phase C's live probe settles it. Remedy order if it fails: collider shape → exporter → server last. Republish with `ops/republish-local-clear.sh` after each rebuild so server geometry matches the scene. |

---

### 10.1 Measured 2026-07-29 — what the rule table did to the topology corpus

All seven shipped topologies, measured from `Topologies/*.json`:

| Property | Result | Forced by |
|---|---|---|
| Vertical span | **0→24 in every one** | `anchors.top` must equal `MaxGeneratedLevel` (`DungeonRouteTopologyValidator.cs:366`) |
| Bridges per topology | **exactly 1**, except `twin-wing-keep` with 2 | "at least one Stair, one Bridge, one Stairwell" |
| Edge crossings | **zero across all seven** | corridor exclusivity, L10f |
| Nodes | 12–16 | the 9–20 rule with the fill floor binding |

**None of that is authorship.** Every dungeon is exactly 24u tall because a rule
says so, bridges are quota-filled rather than characteristic, and no topology can
express two routes crossing because the claim rule forbids it.

**Three of the seven are workarounds for the missing capability**, and the vacant
lattice count gives them away — they spend plan area to express verticality
horizontally:

| Topology | Vacant lattice | What it is approximating |
|---|---|---|
| `descent-shaft` | **48%** (12 cells) | a shaft, spread horizontally. Under layering a shaft is *one* footprint with N layers |
| `ridge-ravine` | 40% | a ridge "over" a ravine that is actually beside it |
| `atrium-ring` | 35% | an atrium: a vacant lattice column ringed by 13 separate rooms. A real atrium is **one room** with an `OpenVolume` and layered galleries |

This is the owner's "too much void between tiers" complaint counted differently.
The corpus buys vertical variety with plan area, and layering is the structural
answer rather than a tuning knob — it is the only thing that adds play space
without growing the 52×52 envelope.

**Three rules should become per-topology in Phase D**, all currently global:

1. **Ceiling** — already proposed above, so a dungeon can be 8u and densely
   layered or 40u and sprawling instead of always exactly 24u.
2. **The transition-kind quota** — "have one of each" becomes "declare your
   character". A bridge-heavy canopy dungeon and a bridgeless warren are both
   legitimate; today neither is expressible.
3. **Planarity** — relaxes only for layer-bound edges (L10f, §8.1).

**[Deferred]** whether the three workaround topologies get redrawn or retired
once layered ones exist. Note a naming hazard: a topology called `atrium-ring`
that contains no atrium in the new sense will confuse every future reader.

---

## 11. Failure modes and explicit rejection conditions

New rejection codes, each with a named owner phase:

| Code | Condition | Phase |
|---|---|---|
| `PLAN_SHADOW_DISAGREEMENT` | `surfaceField.PlanCells()` ≠ `planShadow.cells` at the end of planning (§3.1). **Reported in A1, enforced in A2** — repairing it moves the canonical hash | A1 report / A2 gate |
| `SURFACE_STACK_CLEARANCE` | two surfaces in one column closer than `MinHeadroomLevels` | A1 |
| `SURFACE_GRAPH_DISCONNECTED` | the **fall-free subgraph** is not connected (§3.3) — replaces the draft's strong-connectivity code | A1 |
| `CONNECTION_IDENTITY` | a duplicate `connectionId`, two `RouteEdge` connections resolving to one edge, a `RouteEdge` connection resolving to none, or a `SynthesizedLoop` resolving to one (§8.1). **Not** "every connection has an edge" — loops legitimately have none | A1 |
| `CORRIDOR_BAND_OVERLAP` | two **layer-bound** connections share a plan cell with intersecting `plannedBand`s (§8.1). Phase D only — until then corridor exclusivity stays unconditional and this cannot fire | D |
| `PRISM_CONFLICT` | an incoming prism intersects a registered prism of a different owner, with overlapping half-open bands, whose kind its `blocksKinds` policy names (§6) | B |
| `OPEN_VOLUME_VIOLATION` | geometry emitted inside a reserved volume by an owner not on its penetration allow-list | B |
| `OPEN_VOLUME_PENETRATION_UNDECLARED` | a penetration allow-list names an owner the room does not declare | D |
| `APERTURE_NO_CATCH_SURFACE` | declared pit where some cell resolves no catch surface — names the cell | C |
| `APERTURE_UNREACHABLE_RETURN` | a fall's target cannot reach its source in the fall-free subgraph | C |
| `APERTURE_FALL_TOO_SHALLOW` | fall < `MinHeadroomLevels` in some column — that is a ledge, not a pit | C |
| `VOID_OPENING_OBSTRUCTED` | declared lethal void with a surface in some cell's fall column — names the cell | C |
| `BOUNDARY_BAND_UNRESOLVED` | a column-pair boundary interval cannot be classified (§7.1) — replaces the draft's `SUPPORT_BASE_UNDEFINED` | C |
| `SOFFIT_MISSING` | a non-lowest surface with no underside geometry over a reachable surface | C |
| `RECIPE_LAYER_CONNECTIVITY` | a recipe claims to connect layers its own transitions do not | C |
| `RECIPE_BASE_LEVEL_CONFLICT` | two bound ports imply different base levels (§8.2) — names both ports | C |
| `ROOM_LAYER_CONNECTIVITY` | a placed room claims to connect layers it does not | D |
| `ENTRANCE_LAYER_BINDING` | a route edge bound to an entrance elevation the route cannot supply | D |
| `NAV_COLLISION_DISAGREEMENT` | a nav node's level ≠ the sampled collision surface at that cell | E |

**Failure modes to design against, distinct from rejections:**

- **Silent atrium fill.** The density passes will claim any plan cell they can.
  If they do not read the prism ledger, density ≥3 packs an atrium and no check
  fires.
- **Wrong-layer flank.** A gateway taking a flank from a wall on a different
  layer produces a floating arch. The both-flanks rule must be layer-scoped.
- **Soffit-as-floor.** Not via a flag — exported geometry has none — and **not**
  prevented by normal direction either: the server tests an *absolute* Y normal,
  so a down-facing triangle is as eligible as an up-facing one (§7.2). The
  controls that do work are the capture windows (0.35u for boxes, 1.2u for
  meshes) and `max` selection, which is why the design rule is **emit soffits as
  box colliders**. A mesh soffit is the hazard.
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
| 1 | Upper floor with a pit; lower chamber with its own corridors; separate return stair | One room, two `RoomLayer`s. An `Aperture` on the upper layer emitting one `Fall` edge per (rim surface, opening cell) to that column's derived catch surface; the return is an ordinary `Stair` edge. Fall-free connectivity proves the branch reversible without falling, and reports the witness path. |
| 2 | A bridge between two elevated rooms with independently traversable rooms/corridors below | The deck's cells are `Deck` surfaces at the bridge level; the cells below carry `Floor` surfaces. A `Support` prism under the deck and a `Clearance` prism above the lower surface prove ≥3u separation. |
| 3 | One room owning a lower chamber, upper gallery, bridge, openings and internal connections as one atomic composition | One recipe with three `DungeonRecipeLayer`s, its own transitions, and `RECIPE_LAYER_CONNECTIVITY` proving the claimed connections before catalog admission. |
| 4 | Multiple local layers overlapping without being one global storey | `layerOrdinal` is per-cell and room-local; no API names a storey. Two rooms' "gallery" layers can be at different absolute levels in the same dungeon. |
| 5 | A large atrium as a vertical hub — routes entering at several elevations, balconies and bridges crossing, stairs connecting selected layers, a lower floor reachable by falling or descending | One room with an `OpenVolume` prism reserving its void, N `RoomLayer`s, per-layer ports bound by `fromLayer`/`toLayer` on topology edges, `Aperture` openings for the fall route, and derived `Sees` relations across the volume. |

---

## 13. Phased path

Bounded, not a rewrite. Each phase ends in a state that is safe to sit in.

### Phase A — Surface identity in the plan — **LANDED 2026-07-31**

> **Status.** A1 = `e710d39d`, A2 = `cacc0518`, on `dungeon/layered-topology`.
> A1's exit met as written: `hashes.canonical` moved on **0/200** seeds,
> `resultHash 2731146954f3e57d` identical both legs. A2 met its exit with the
> hash moving once, to `f387ca04df49d8a7`; `planShadowDisagreementSeeds` 200 → 0;
> per-seed, only `hashes.layout` and `hashes.canonical` moved, while
> `routeIntent`, `tieredLevelPlan`, `existingTransitions`, `preservedCorePlan`,
> `preCorrectivePlan` and `recipeResolutions` were byte-identical on all 200, as
> were the accepted set, every validation result and every attempt count.
> Determinism re-verified; Render Sweep 200/200. **Two rulings below corrected
> this section as written — see "Corrected in implementation".**

**Capability.** The canonical plan can express more than one walkable surface at
a plan coordinate, and the traversal graph is promoted to first-class. Nothing
yet *produces* two surfaces.

**Systems.** `TieredLevelPlan`, `DungeonLayout`, `FloorStairPortGraph`,
`DungeonLabGenerator.Validation.cs`, the canonical projections in `.Batch.cs`.
`AsHeightField()` shim at every existing consumer.

Two things pulled forward from the draft's Phase D, both output-neutral on
single-layer seeds and both prerequisites for everything later:

- **`RoomConnection` gains `edgeId`** and `TryGetTransition` keys on it instead
  of the room pair (§8.1) — a *correctness* fix for edge lookup, not just
  plumbing.
- **`plannedBand` is recorded on every connection**, derived from the topology's
  declared absolute node levels, so it is available pre-elevation and is globally
  meaningful — unlike a room-local layer name. **The exclusivity rule itself does
  NOT change here.** `TryClaimCorridor` and `PathCrossesThirdRoom` keep their
  current unconditional behaviour, because relaxing them is not output-neutral
  (§8.1) — `atrium-ring` alone spans levels 0–24, so disjoint bands are common
  and the relaxation would accept embeddings rejected today. The **data** lands
  in A1 because corridors are claimed pre-elevation and no later phase can
  retrofit it; the **relaxation** lands in Phase D behind layer binding.

**Split A1 / A2, per review finding 13 — the draft's gate was self-contradictory.**
**[Fact]** `BuildCanonicalLayoutProjection` serializes `layout.floorCells`
(`Batch.cs:4864`), that becomes `layoutHash`, and `layoutHash` is mixed into
`canonicalHash` (`:3437`). Repairing H2 means adding the external-promontory
cells to the shadow, which *necessarily* moves the hash. A single phase cannot
both fix H2 and gate on a byte-identical `resultHash`.

- **A1 — container migration, output-neutral.** Everything below, with shadow
  agreement **detected and reported, not repaired.** Byte-identical hash.
- **A2 — the H2 repair, explicitly rebaselined.** Add the promontory cells to the
  shadow, accept a one-time hash move, and record it as deliberate. Small,
  isolated, and diffable seed by seed against A1.

**Invariants (A1).** `IsSingleLayer` holds for every seed. **Fall-free
connectivity** replaces `IsGloballyConnected` and agrees with it on every seed
(with no falls present, the two are identical). **Every connection has a unique
`connectionId`; every `RouteEdge` connection resolves to exactly one route edge;
every `SynthesizedLoop` resolves to none** — the draft's "every connection
resolves to a route edge" would have rejected every loop corridor
(`cs:1503`, `:2097`). RNG subjects that were cell tokens become surface tokens.

**Invariants (A2).** Shadow agreement holds **one-directionally**:
`surfaceField.PlanCells() ⊆ planShadow.cells`. See below for why equality is the
wrong rule.

**Evidence.** `ops/dungeon-port-ab.sh` on 200 seeds: byte-identical `resultHash`,
stashed vs restored. Two independent runs identical
(`ops/dungeon-step2-verify.sh`). **Render Sweep (200)** — Batch Validate never
builds a GameObject, so it cannot prove the renderer survived the shim.

**Non-goals.** No renderer change. No second surface anywhere. No prism ledger.

**Exit (A1).** 200/200 accepted, `hardValid 200/200`, **byte-identical
`canonicalHash` per seed** against the current commit, Render Sweep 200/200, and
`PLAN_SHADOW_DISAGREEMENT` **reported out-of-band** on the known
external-connector seeds — proving the check has teeth without moving anything
hashed.

**[Fact] the gate must be the canonical hash, not `resultHash`.**
`resultHash = ComputeSha256(seedReports.ToString(…))` (`Batch.cs:5748`) covers
the entire seed-report array, so adding *any* diagnostic field to a seed report
changes it. A1's disagreement report therefore goes to its own file under
`DungeonLabReports/`, outside the hashed array — after which `resultHash` is
stable too, and both can be asserted.

**Exit (A2).** Shadow agreement clean on 200/200; the hash moves **once**, and
every seed whose report changed differs only in `floorCells`, fill percent and
the graph summary. Anything else changing means the repair was not isolated.

#### Corrected in implementation

Two things this section got wrong, both settled by measurement:

1. **Agreement is one-directional, not equality.** The invariant above originally
   read `surfaceField.PlanCells() == planShadow.cells`. A shadow cell with no
   surface is legitimate — the gap under an external span deck stays a gap — and
   the shadow is the **domain** the level field floods within, so deleting those
   cells would change what `FillUnassignedFloorCells` and `CleanPath` operate
   over, which is a behaviour change A2 has no business making. `Agrees` is
   therefore `surfacedCellsOutsideShadow.Length == 0`. The other side is still
   counted and reported (`IsTwoSided`) so a **new** producer of unsurfaced shadow
   stays visible; it is not a defect.
2. **The repair belongs at the end of planning, not at each producer.** §3.1's
   prose said "any pass that adds a surface must add its plan cell to the shadow
   in the same step". That is not safe here:
   `BuildExternalConnectorCandidates` derives `coreExtent` and its outer-face
   test from `layout.floorCells`, so a named vista promontory added at its own
   producer moves the core's outer face and re-picks the connector anchors —
   geometry moving well beyond the shadow, which A2's isolation exit forbids.
   `ReconcilePlanShadowWithSurfaces` runs once in `TryBuildCellLevelField`,
   immediately after `TryResolveExternalConnectorPromontories` — the final plan
   mutation, hence "the end of planning", with no reader of the shadow
   downstream of the write. Sweeping every surface rather than enumerating the
   two known producers also makes the invariant true by construction instead of
   true by a list somebody has to remember to extend.

---

### Phase B — Volumetric reservation and one clearance rule — **LANDED 2026-07-31**

> **Status.** On `dungeon/layered-topology`, parent `ad9fbb33`. Exit met:
> `ops/dungeon-port-ab.sh 0` reports **identical geometry on all 200 seeds** —
> same `hashes.canonical`, same accepted set, same failure codes — and a
> field-level diff of the two 200-seed reports shows the **only** difference
> anywhere, on any seed, is one documentation string
> (`schemaUsage.fields[16].consumer`, which names the ledger class that was
> renamed). Every validation message is byte-identical, including the headroom
> gate's deck-cell count. `resultHash` therefore moved once,
> `f387ca04df49d8a7` → `991d86e1bb577144`, for that string alone.
> Determinism re-verified (two independent runs byte-identical, 200/200
> accepted, `PORT_GRAPH: 5`, all distributions unchanged); Render Sweep 200.
> All three negative fixtures behave as listed. **One ruling below corrected
> this section as written — see "Corrected in implementation".**

**Capability.** Reservations and clearance are volumes. `spanDeckLevels` and the
duplicated deck formula die.

**Systems.** `StairPlacementLedger` → prism ledger (landed as `PrismLedger` in
`DungeonLabGenerator.Prisms.cs`); `TryValidateSpanHeadroom` → the general rule;
`TryValidateAcceptedPlanHeadroom` in `.Batch.cs` deleted in favour of the shared
one; density fill passes read the ledger; the late-pass ordering (review 2.4)
fixed so the gate guards the final state.

**Invariants.** For every surface, the half-open band
`[level, level + MinHeadroomLevels)` is free of any *other owner's* prism
satisfying **`BlocksHeadroom`** (§6) — the one named predicate, not a restated
set. Every reservation carries a half-open level band and a typed `OwnerKey`.
Conflict follows the asymmetric per-kind `blocksKinds` policy (§6), seeded
verbatim from `ConflictsWithReservation`, and reproduces today's behaviour
exactly — including that landing–landing, landing–clearance and mouth–clearance
all remain legal. No validation runs before a mutation it must see.

**Evidence.** Output-neutral 200-seed A/B. Three negative fixtures, because the
draft's blanket rule got each of these wrong:

1. an artificially lowered stacked cell is still rejected — retarget the existing
   `negativeHeadroomRejected` probe in `BuildStackedCrossingFixture` rather than
   duplicating it;
2. clearance of **exactly** `MinHeadroomLevels` still **passes** — the half-open
   endpoint case, which a closed band would wrongly reject;
3. the three pairs a symmetric matrix got wrong all still **pass** —
   landing–landing, landing–clearance, and mouth–clearance across different
   owners — while a landing over another owner's **footprint** still **fails**,
   and a `TransitionClearance` over another owner's **mouth** still **fails**.
   These five cases are the whole content of the `blocksKinds` policy; if any
   flips, the port is not faithful.

**Non-goals.** No `OpenVolume` *producer* yet — only the reservation kind, its
penetration allow-list mechanism, and enforcement.

**Exit.** Byte-identical 200-seed hash; `spanDeckLevels` and the duplicated deck
formula gone; all three negative fixtures behave as listed.

#### Corrected in implementation

Two things this section and §6 got wrong, both settled by measurement. Neither
touches the architecture; both are the mechanism-level detail §0 warns about.

1. **The headroom rule needs a third qualifier: the prism must declare where it
   sits.** §6 states the rule as "no prism satisfying `BlocksHeadroom` *owned by
   anything other than S*", and that is not sufficient. Today **every**
   reservation is registered with an unbounded band — §6 says so itself — and an
   unbounded band intersects every headroom band. A stair footprint and the
   surface it carries have different owners (the surface is plain floor; the
   footprint is the transition), so the rule as written has every embedded stair
   violate the headroom of its own treads, and the corpus collapses. The missing
   piece is that `[-∞, +∞)` is not "solid from the abyss upward" — it is *"this
   reservation has never been asked for a height"*. The rule therefore reads:
   a prism obstructs only if it satisfies `BlocksHeadroom`, belongs to another
   owner, **and declares a base** (`LevelBand.DeclaresBase`). Today exactly one
   producer declares one — the external-span deck — which is why the port is
   output-neutral, and a phase that gives a producer a real band opts it into the
   rule with no further plumbing.
2. **There are two ledgers, not one, because the density fill passes run before
   elevation.** §6's invariant says "the density dial's fill passes (annex,
   mop-up, backstop) must query the prism ledger", implying the ledger the
   transitions use. They cannot: `AnnexAndMopUpLatticeVoid` runs inside
   `TryCompileRouteFirstLayout`, and the transition ledger is created in
   `TryBuildCellLevelField`, a stage later — and the two cannot be merged into
   one instance, because layout attempts and tier attempts retry independently,
   so a shared ledger would leak a failed tier attempt's reservations into the
   next. What landed is the same *type* at both stages:
   `CollectAnnexBlockedCells` now returns a `PrismLedger` and the sweeps ask
   `BlocksFill`, so a reserved volume is honoured by the same policy in both
   places. "Backstop" in that sentence is also not a fill pass — it is the
   min-fill rejection gate, which claims no cells.

Also worth recording, because it confirms rather than corrects: **§11's
late-pass hazard was real and the count was exact.** Three passes ran after the
gate — `SweepIntraRoom1uDrops`, `TryResolveNamedVistaPromontory`,
`TryResolveExternalConnectorPromontories` — and the duplicate in `.Batch.cs`
existed precisely to catch what they moved. The gate now runs after the last of
them, which is what let the duplicate go.

---

### Phase C — The two-layer authored episode ← the first real proof

**Capability.** Genuine overlapping traversal, end to end, in a built and
exported scene: an upper route with a bare-rim aperture, a directed fall to a
lower chamber with its own corridor, a return stair, and a bridge over the lower
playable route.

This is deliberately an **authored deterministic episode**, not a
generic-generation feature — the same order the project used for topologies
("abstractions are earned by a working slice").

**Split into two steps, per review finding 4.** The draft required "one new
Episode recipe with two layers" while deferring every layer-aware recipe field to
Phase D, so its first real proof could not actually be authored. C1 proves the
renderer without the schema; C2 adds the minimum schema and authors the episode
properly.

> **C1a landed 2026-07-31** (`13d03bbe`), with its instrument at `8258391c`.
> The boundary band decomposition (§7.1 step 1) replaces the level compare, and
> renders **byte-identically on all 200 seeds**.
>
> The gate needed building first. Every previous dungeon gate compares the PLAN
> (`hashes.canonical` from Batch Validate), and Batch Validate never builds a
> GameObject — so no gate in the project could see a renderer change at all, and
> §7.1's "every existing seed renders byte-identically" was unmeasurable as
> written. `Render Digest` now hashes every renderer and collider under the
> built root as (mesh, world transform, collider shape), sorted so the digest
> describes geometry rather than instantiation order. It was proven before it
> was trusted: two independent runs, identical per-seed and combined.
>
> **Why neutrality holds is a fact about the pipeline, not an assumption.**
> `TrySetPlannedStairCells` refuses to floor a span deck's or stairwell tower's
> footprint — "the gap stays a gap" — so the one suspended surface the generator
> makes never enters `cellLevels` at its own height. Every entry in the level
> field is therefore the lowest in its column, i.e. `IsGroundBacked`, so every
> column's mass is exactly `[abyssBase, level)` and the walk reproduces the three
> hand-written cases with the same extents, types and railing suppression.
>
> **The owner's fascia ruling shrank the phase.** With the slab interval emitting
> no `WallEdge` — the 0.5u underside being the `_E_` family's own closed slab —
> no fractional band ever reaches `WallEdge` (whose levels are integers, built
> from whole-level denominations), and §7.1's fascia and soffit become one
> change. The draft's "the bulk of Phase C" is considerably smaller than
> advertised.
>
> Still open after C1a: the multi-surface `SurfaceField`, the void-facing branch
> (left on its existing path — neutral in principle, but interleaved with the
> promontory skip, the `level <= 0` partition/railing handling and the
> aerial-deck evenness rule), the `_E_` swap (no application site until a
> suspended floor exists — see §0.1's re-measurement), level discriminators on
> the edge keys, and the two-layer fixture.
>
> **C1b landed 2026-07-31**, in two parts, and closed all of those except the
> void-facing branch. Part 1 gave `SurfaceField` a stacked backing store; part 2
> renders one — surfaces travel into `BuildLevelField`, a suspended surface gets
> the `_E_` slab and its own rim guards, and the whole episode exists as a
> fixture (`Tools/Dungeon Lab/Print Two-Layer Episode Fixture`). Gate: Render
> Digest 200 seeds byte-identical against the C1a baseline.
>
> **Six corrections this section needs**, all measured rather than argued:
>
> 1. **The visit key is not a collision, and `EdgeKey` needs no level.** The
>    paragraph below says the edge keys "gain a level discriminator", and the
>    obvious reading is that `visited` — keyed on a cell pair — collides once a
>    column holds two surfaces. It does not. `BuildWallEdges` dedupes on an
>    unordered cell pair, which is exactly the identity of a BOUNDARY, and step
>    1 above walks the boundary between two COLUMNS and deliberately never pairs
>    surfaces. Keying it on a surface would visit each boundary once per stacked
>    surface and emit its faces twice. `EdgeKey` is the same story: it is a
>    column relation (transition suppression, doorways, gateway flanks) and
>    needs a level only when one column pair can emit two faces — see 5.
> 2. **Stacking changes no wall face at all.** Because the slab band emits no
>    `WallEdge` (the fascia ruling) the only band a column has is the ground
>    band under its floor, which *is* `levels[cell]` once the heightfield holds
>    the column's lowest surface. `ComputeColumnMass` and `DecomposeBoundary`
>    were untouched by C1b. What a stacked surface needs is the three things a
>    column cannot answer: its own floor tile, its own rim guards, its own
>    `IsGroundBacked`.
> 3. **The discriminator that is load-bearing is on the RIM.** `WallEdge`
>    already carries `lowerLevel`/`higherLevel`. `OpenEdgeKey`'s producers —
>    transition ports, bridge span ports, internal path guards — all describe
>    the level field, so a level on that key would be `levels[cell]` written by
>    every producer (a rename, not a discriminator) and would break the
>    producers whose cells are not in the level field at all. The thing that
>    genuinely could not name a stacked surface was the railing-only edge list,
>    whose height came from `levels[cell]`; it is now
>    `RimEdge (x, z, level, direction)`. The `(x, z, direction)` suppression
>    sets (`shellGuardEdges`, `bareLandingEdges`) are the other site and are now
>    gated on the rim being at its column floor.
> 4. **§3.1's `SurfaceKind` is not optional decoration.** C1b part 1 stored bare
>    levels, which makes `IsGroundBacked` — defined here as "`kind == Floor`
>    **and** lowest in column" — inexpressible: half the predicate had no home.
>    The kind is now stored. The renderer still answers the deck half from the
>    `aerialDeckCellLevels` side table, because promoting decks to surfaces is a
>    behaviour change C1b does not make.
> 5. **`DecomposeBoundary` still cannot emit two faces for one column pair.**
>    Step 1 describes a multi-interval walk. With only ground bands — Support
>    and Wall prisms have no producer — two ground bands share the abyss floor
>    and can differ only at the top, so at most one interval is one-solid. The
>    single-face return is provably equivalent today.
> 6. **The fall-free invariant is not checkable through the port graph.**
>    `TryBuildFloorStairPortGraph` keys its nodes on the level field and cannot
>    see a stacked surface, so the fixture walks its own surfaces. Teaching the
>    port graph is §3.2's traversal work, which C1 does not do.
>
> **On "extend `BuildStackedCrossingFixture`" below:** the episode is built
> ALONGSIDE it. That fixture carries Phase B's three negative fixtures and
> asserts its bridge crosses exactly one stacked coordinate; adding a chamber, a
> gallery and a stair run to the same field would either move that count or
> force the assertion to be loosened.
>
> **C2a, C2b-1, C2b-2 and C2b-3 landed 2026-07-31**, in that order: the recipe
> layer schema, the reader side's container half, transition endpoint levels, and
> decks as surfaces. That last one is this section's "the aerial-bridge path
> promoted so a deck's cells become surfaces", and it retires correction 6 above
> — the port graph nodes every surface and the fixture no longer walks its own.
> The episode reaches **94/94 nodes**, up from 84/88.
>
> **Three corrections from the deck work:**
>
> 1. **A deck is a surface but it is NOT a floor, and §3.1 does not say so.**
>    The model here is "the surface field holds every surface, the heightfield is
>    a compatibility view", which invites putting a deck over a true gap into the
>    heightfield as its column's lowest surface — correction 4 above says exactly
>    that, and reasons that `SurfaceKind` is what keeps it from becoming a
>    pillar. The kind is enough for the RENDERER and for nothing else. The
>    heightfield is what ~50 readers mean by "the floor here": put a deck in it
>    and the flood fill seeds at deck height, a doorway opens onto thin air
>    beside a span, and the plan shadow swallows the gap the deck crosses. The
>    kind is what lets the FIELD decide where to store a surface — suspended
>    kinds live in the overlay, floored or not — rather than a flag every reader
>    must remember to consult.
> 2. **Shadow agreement is floor-scoped, not surface-scoped.** §3.1's invariant
>    ("every surface's plan cell is in the shadow", A2's one-directional form)
>    breaks on the first suspended producer: the shadow is a GROUND claim, being
>    both the domain the level field floods within and what `CleanPath` filters
>    against. A surface owes the shadow a cell exactly when it rests on fill.
> 3. **§12 scenario 2's separation mechanism does not exist and is not needed.**
>    It calls for "a `Support` prism under the deck and a `Clearance` prism above
>    the lower surface". The span deck's own `Footprint` prism declares a base
>    (Phase B), and the single headroom rule computes the band per surface;
>    `Support` still has no producer. The scenario's first sentence is now
>    literally true.

> **C2 LANDED 2026-08-01. PHASE C IS COMPLETE — owner eyeball passed the same day.**
> `episode_layered_gallery_01` + `aperture-gallery.json` (weight 0, outside the
> weighted draw on purpose): **200/200 seeds accepted on its own topology, and
> every one stacks.** Code leg `11b8da22` gated at identical geometry on all 200;
> the content leg moves every `hashes.canonical` through `catalogDigest` and
> nothing else — `hashes.layout` and `hashes.tieredLevelPlan` moved on ZERO
> seeds, and the episode is rejected at all 600 existing slots (400
> `ROLE_INELIGIBLE`, 200 `BEAT_INELIGIBLE`), so no existing seed's recipe
> selection can move.
>
> **Six corrections this section needs.** The full write-up with numbers is in
> `CURRENT_STATUS.md`; the ones that contradict text below:
>
> 1. **"The same episode as a real catalog recipe" is not achievable as
>    written, because a recipe cannot author a bridge.** `AddAerialBridges` is a
>    generator pass, capped per dungeon and forbidden over room interiors. The
>    authored episode is the OTHER half of C1's fixture — chamber, gallery,
>    aperture, return stair — and bridge-over-playable-geometry is what C2b-3
>    delivered and proved on the corpus.
> 2. **A cross-layer stair cannot be a step strip, so C2a's rise relaxation has
>    no renderer behind it.** `PlaceSeamStepStrip` admits `seam`/`dais` only at
>    delta 1 (2 for dais). Two storeys 4u apart join LATERALLY — C1's flush seam
>    — which is why `RECIPE_LAYER_CONNECTIVITY` had to stop assuming a stair.
>    §8.2's "a stair between two layers is what makes them connected" is too
>    narrow. The candidate gate was relaxed to match the validator anyway, since
>    the two disagreeing silently is worse than either answer.
> 3. **A recipe room is fenced off from the intra-room 1u sweep by
>    construction**: every `roomCells` entry is registered as a `Landing` prism
>    and a Landing blocks an incoming Footprint. A recipe owns every transition
>    inside its own footprint — there is no "declare the storeys and let a pass
>    stair them".
> 4. **A multi-step stair cannot be authored in line.** `RECIPE_CLEARANCE`
>    forbids a cell being both a footprint and a landing, and a single-file
>    flight makes every intermediate cell both. Two lanes: treads and landings.
> 5. **The vista promontory reads the target CELL's floor level, not the
>    node's**, so an authored room must keep its outward faces at the base level
>    or it fails `ROUTE_PROMONTORY` while sitting at exactly the right node level.
> 6. **§13's "Invariants" list is still ahead of the code on one item.** "Every
>    aperture cell resolves a catch surface" is authored, not derived: the
>    recipe validator proves the rim stands on its storey and faces a cell that
>    storey does not cover, and the plan re-reads it, but nothing computes what
>    a fall LANDS on. In this episode the aperture is over its own chamber by
>    construction.

**C1 — renderer proof, code-built fixture.** No recipe schema change. Extend
`BuildStackedCrossingFixture` into a hand-constructed two-layer field: upper
route, aperture, lower chamber, return stair, bridge over the lower route. This
is a *fixture*, and the document says so rather than calling it authored.

**The aperture in C1 should sit in a CORRIDOR, not in a room** (§4.1). It is the
cheaper proof — it needs no multi-layer room, no recipe schema, and no room
ownership work — and it exercises the case that matters most, since almost every
elevation change in this generator already happens between rooms rather than
inside them. A room-owned pit follows in C2 with the recipe schema.

**C2 — the authored episode.** The minimum recipe layer schema — pulled forward
from the draft's Phase D: `DungeonRecipeLayer`, `layerId` on zones and ports,
layer-scoped `RelativeLevelAt`, per-layer base derivation, and the
`RECIPE_LAYER_CONNECTIVITY` validation layer. Then the same episode as a real
catalog recipe, plus one hand-authored topology file that places it.

**Systems.** Renderer boundary-band decomposition (§7.1); `supportBase` as a
per-face query; soffit pass and its collider discipline (§7.2); band-scoped
`EdgeKey`/`OpenEdgeKey`/`WallEdge`; `surfaceOwners`; `Opening` + per-cell `Fall`
edges; the aerial-bridge path promoted so a deck's cells become surfaces; the
minimum recipe layer schema (C2 only).

**Invariants.** The fall-free subgraph is connected. Every aperture cell resolves
a catch surface, and every fall's witness return path is reported. Every
non-lowest surface has an underside whose collider cannot become ground. Every
column-pair boundary interval classifies. No cliff drops through a surface below
it. Both surfaces at the stacked coordinate carry collision; the volume between
them is clear.

**Evidence.**

1. Extend `BuildStackedCrossingFixture` to the full episode and probe colliders
   at *three* stacked coordinates (aperture rim, chamber floor under the bridge,
   deck).
2. **Render Sweep** on the fixture seed set — the episode must build, save, and
   export.
3. **Live**, not post hoc — and this is the leg that tests §7.2's hypothesis, so
   it must assert all four behaviours, not just the fall. A headless player probe
   (modelled on the committed `ops/s4-los-probe.py` …
   `ops/s9-auto-rewind-probe.py` family) that:
   - walks a player off the aperture and confirms the server lands them on the
     **chamber surface**, not the abyss;
   - walks the return stair back to the upper layer;
   - crosses the bridge and confirms the player stays on the **deck** and is not
     captured by the soffit beneath it (the 1.2u window case);
   - walks under the bridge on the chamber floor and confirms the player is
     **not** snapped up onto the soffit.

   Publish first with `ops/republish-local-clear.sh` and prove the change is live
   on the target DB before the leg runs.
4. Owner eyeball on the built scene. No hash tells you whether a two-layer room
   reads well.

**Non-goals.** No generic multi-layer rooms. No `OpenVolume` producer. No sunken
zones. No topology-level layer bindings. No lethal void mechanic. No envelope
raise. No NPC nav export. Bridges over rooms only inside this episode.

**Exit.** The episode generates deterministically across two independent runs;
the probe demonstrates all four live behaviours above; Render Sweep clean; owner
accepts the look. **If the soffit assertions fail, §7.2's hypothesis is falsified
and the phase does not exit until collider discipline or the exporter closes it.**

---

### Phase D — Multi-layer rooms in generation

> **Status. IN PROGRESS.** Slice order and evidence live in
> [`CURRENT_STATUS.md`](CURRENT_STATUS.md); **D0, D1, D2 and D3 landed
> 2026-08-01** — the transition body became a level BAND instead of a column to
> the sky (closing the defect C2 recorded as unfixed and ungateable), the
> topology layer schema parses and is carried, the corridor-exclusivity and
> third-room relaxations shipped with the stacked-corridor producer they need,
> and a bound edge now RESOLVES at its layer's elevation. Three corrections to
> this section, all measured:
>
> 1. **"`ChooseEnclosedRooms` moved into the plan so bridges may legally cross
>    rooms" names a gate that does not exist.** The aerial-bridge pass has no
>    enclosure test — Decision 30 is an unconditional `room.Contains(cell)` →
>    reject (`DungeonLabGenerator.cs:4636`), and enclosure is rolled at `:3419`,
>    long after bridges are placed at `:1942`. The move is a **prerequisite** for
>    relaxing Decision 30 (it is what lets the pass know whether the room below
>    has a roof), not the relaxation itself.
> 2. **`LevelBand.SpanningEndpoints` is the CORRIDOR band, not a body band.** It
>    pads by `MinHeadroomLevels` per §8.1. A stair body fills the plain endpoint
>    span; padding it would delete a surface 3u above the stair's top.
> 3. **§8.1's node syntax parses today; its edge syntax does not.**
>    `TryParseRouteTopologyNodes` rejects only `fields.Count < 5`, so a trailing
>    6th element is already tolerated. `TryParseRouteTopologyEdges` demands
>    exactly 3 fields, so the 4th `{ "fromLayer": … }` element is the parser
>    change this phase owes.
>
> One property makes the whole phase gateable, and it is worth stating once:
> **nothing in the shipped corpus declares a layer**, so each relaxation can be
> authorized by a binding no topology has and is therefore output-neutral by
> construction rather than by measurement luck.
>
> **D3 added two corrections of its own.** (a) D1's schema was not merely
> unconsumed, it was **unusable**: a layer-bound edge did not resolve at the
> wrong elevation, it failed the tier attempt outright, because
> `TryAssignRoomLevels` compared a rise derived from the BOUND levels against one
> measured between the NODE levels. (b) The entry level has to be **additive**
> over the zone level rather than a replacement, or the +1 raised-zone accent is
> lost — and it can never be asked to compose the two, because a node declaring a
> real storey must carry a recipe slot and a recipe-slot room is excluded from
> zone splitting outright.

**Capability.** Topologies and recipes can declare layers; routes bind to
declared entrance elevations; `OpenVolume` reserves an atrium; the atrium
archetype (scenario 5) generates.

**Systems.** Topology schema (`layers` on nodes, `fromLayer`/`toLayer` on edges)
+ `Validate Topologies`; `RoomConnection` layer binding consumed end to end
(its `edgeId` landed in Phase A); the remaining recipe schema — `OpenVolume`
zones with penetration allow-lists, and sunken/negative elevated zones;
`RoomFootprint.Overlaps` volumetric; `ChooseEnclosedRooms` moved into the plan so
bridges may legally cross rooms.

**Also in D — the slot vocabulary.** `ROUTE_TOPOLOGY_AUTHORING.md`'s rule
*"every slot node has degree 2"* means a vertical hub can never be a required
recipe slot (L10d), and a topology declares exactly three slots. An atrium is
degree 3–4 by definition, so scenario 5 needs either a degree-N slot kind or an
atrium that is a *generic* multi-layer room rather than a slot-bearing one. The
recipe schema already supports the former through `IncidentCardinalSockets`
(`connector_generic_room_01` binds one to four sockets by incidence), so the
constraint is in the **topology rule table**, not the recipe system. Raised as
owner decision 9 (§14).

**Invariants.** No global storey concept anywhere. A room's declared layer
connectivity is proven before catalog admission. `OpenVolume` survives every
density level. Room stacking requires a declared reason. Corridor exclusivity
and third-room crossing relax only for layer-bound connections, and the test is
always **disjoint absolute bands** — never a layer-name comparison, which is
room-local and cannot establish separation between unrelated rooms (§8.1).

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
2. `HashSet<Vector2Int>` reservations → half-open prisms with a typed owner and
   an asymmetric `blocksKinds` policy, adding `Support`, `Wall` and `OpenVolume`
   to the five kinds the ledger already keeps.
3. `abyssBase` (one global int) → a **column-boundary band decomposition**, where
   a **ground-backed** surface carries mass to the abyss while a suspended one
   implies only its own slab, and full-height geometry comes solely from ground,
   wall and support bands — plus a soffit pass emitted as **box** colliders.
4. `Opening { Aperture | Void }` on a layer with **derived per-cell** catch
   surfaces and **per-rim-edge** guards, `Fall` emitted only from a bare rim
   edge, and **fall-free connectivity** as the invariant that makes optional pit
   branches provably reversible.

And one prerequisite the draft missed: **`RoomConnection` must carry a `source`
discriminator, `edgeId`, `connectionId` and `plannedBand`**, because a corridor
is currently matched to its route edge by room pair and synthesized loops carry
no edge at all (§8.1).

No rewrite. `ElevationEdgeModel`'s wall/railing/corner/gateway grammar,
`StairForge`, the contract data, the recipe system, the route planner, the
canonical evidence machinery and the derived-RNG scope all survive — the same
conclusion [`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md)
reached for its own scope, for the same reason.

### Smallest credible implementation slice

**Phase A1 alone** — the `PlanShadow`/`SurfaceField` split, the surface graph,
fall-free connectivity, connection identity (`source`, `edgeId`, `connectionId`,
`plannedBand`), and shadow disagreement **detected and reported out-of-band** —
with `IsSingleLayer` true on every seed and a byte-identical canonical hash
against the current commit. It ships no new capability and is the only slice that
can be *proved* to have changed nothing, which is what makes every later one
safe.

**A2 — the shadow repair — is deliberately not in that slice**, because fixing
H2 moves the hash (`floorCells` feeds `canonicalHash`) and a single gate cannot
both repair and prove neutrality.

If a slice with something to look at is wanted, **Phase A + C1** — the code-built
fixture — which proves the renderer without touching the recipe schema. Skipping
B is not recommended: the prism ledger is what stops two transitions at one
coordinate from conflicting, and without it the fixture has to special-case its
own bridge.

### Major risks

1. **The boundary-band decomposition is the real work, and the draft
   underestimated it.** It is a new algorithm with its own failure modes, not a
   loop edit — and `EdgeKey`/`OpenEdgeKey`/`(x,z,direction)` still have to widen
   across corner selection, gateway sockets, shell placement, trap placement and
   railing suppression. This is most of Phase C and it is where a subtle railing
   or flank regression will hide. Render Sweep, not Batch Validate, is the only
   thing that catches it. **Re-estimate this phase before committing to it.**
2. ~~**Soffit art may not exist.**~~ **RETIRED, measured 2026-07-29 (§0.1).**
   The kit ships a solid-slab floor family (`_E_`, 0.5u thick, bottom face
   included) that the generator simply is not using. No new art, no flipped
   quads, no collider trick. The residual work is a per-surface prefab choice
   plus carrying 0.5u in the clearance derivation.
3. **Silent atrium fill.** The density passes will claim any plan cell. If
   `OpenVolume` is not wired into all four mechanisms, density 5 packs the
   atrium and no gate fires.
4. **Clearance is now derived, and the derivation is tight.** `MinHeadroomLevels
   = 3` must clear the **1.2u** ground-sampler capture window (§7.2) *and* a
   1.8u player. It does, with margin. But a 4u major rise leaves only ~2.2u of
   true clearance, so comfortable stacking wants 8u — which halves the number of
   layers a 40u envelope supports. This is the number to re-derive, not inherit.
5. **Hash rebaseline discipline.** Phase C moves every seed once. Compare against
   the current commit with `ops/dungeon-port-ab.sh`; never against a recorded
   value, and never assert one in a test.
6. **LOS interaction.** The dungeon exports query collision == movement
   collision, so a bridge deck will block sight to the chamber below it using
   deliberately oversized geometry. This contradicts the project LOS rule and
   gets worse as stacking increases.
7. **H5 is now on the critical path.** Multi-layer routing needs route identity
   to survive into the plan (§8.1). The architecture review treated that as
   opportunistic; here it is a prerequisite, which makes Phase A slightly wider
   than "just a container change".

### Remaining owner decisions

1. **Envelope. — DECIDED 2026-07-31: 40u, subject to change.** Ten × 4.
2. **Envelope mechanism.** Per-topology `ceiling` (existing dungeons unchanged,
   new ones opt in) versus raising the global constant (every seed stretches,
   all seven files edited). Per-topology is recommended. **STILL OPEN**, and it
   is the only one of these three that creates any code — see below.
3. **Stacking pitch. — DECIDED 2026-07-31: 4u, subject to change.** One major
   rise.

> **Measured against the code and the corpus 2026-07-31, after the ruling.**
>
> **Neither decision binds a line of code today.** There is no vertical envelope
> constant to set: `MaxLevel = 5` in `DungeonGenerationProfile` is the DENSITY
> dial, and the "ceiling" in `ResolveLatticeLaneOffsets` is a horizontal lane
> gap in cells. A dungeon's vertical extent emerges from its topology's authored
> elevation deltas. Decision 2 is what would create the mechanism, which is why
> it is now the only one of the three with work attached.
>
> **4u is compatible with every constant already in place, and with the episode
> the owner just accepted.** `MajorRiseLevels = 4` and one level is one world
> unit, so the pitch is exactly one major rise. `MinHeadroomLevels = 3` demands
> 3u of clearance under a surface; a 4u pitch minus the `_E_` slab's 0.5u
> underside (§0.1) leaves 3.5u. It fits without touching a constant. C1's
> two-layer episode already separates its storeys by exactly 4 levels, so the
> ruling matches geometry that has been rendered, live-probed and eyeballed
> rather than only reasoned about.
>
> **But "ten full major elevation changes" overstates what 40u buys, and the
> two budgets in that sentence are different budgets.** Measured over the
> accepted 200 at density 0, today's dungeons already span **24–25 levels**
> (median 24, up to 12 distinct levels per dungeon) — about six major rises,
> spent entirely on tiered terrain, because generation is single-layer. Raising
> to 40u adds ~15u of GLOBAL range.
>
> That is not the stacking budget. A gallery 4u over a chamber consumes LOCAL
> vertical space that was already inside the envelope and unused; it does not
> push the lowest point down or the highest point up. So the envelope governs how
> far the dungeon's vertical story travels, and the pitch governs how many
> surfaces a column can carry where there is local headroom. Sizing layers by
> dividing 40u by 4u would be wrong in both directions.
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
8. **Fall-only entrances.** Fall-free connectivity (§3.3) forbids a region whose
   *only* way in is a fall, even when it has a stair back out. Is that
   acceptable? Forbidding it is recommended — it guarantees no dungeon is ever
   gated behind a fall the player may not find — but it is a real expressive
   restriction and it should be a decision, not a side effect of the invariant.
9. **The slot vocabulary for a vertical hub** (L10d). A required recipe slot must
   be a degree-2 node and a topology declares exactly three slots, so an atrium
   cannot be one. Add a degree-N slot kind, or let the atrium be a generic
   multi-layer room outside the slot system? The recipe side already supports
   degree-N through `IncidentCardinalSockets`, so this is a topology-rule
   decision.

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

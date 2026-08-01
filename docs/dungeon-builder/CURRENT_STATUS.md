# Dungeon generator: current status

Last updated: 2026-08-01

This page describes what the generator is, the rules worth knowing before you
change it, and where the work stands. Keep it short — if it starts growing
per-phase evidence sections again, that evidence belongs in `DungeonLabReports/`
or `docs/archive/`, not here. *(It did grow them back, and was trimmed again on
2026-07-29; the July route-topology, RNG and rebaseline evidence is in
[`docs/archive/2026-07-dungeon-phase-log/ROUTE_TOPOLOGY_AND_RNG_LOG.md`](../archive/2026-07-dungeon-phase-log/ROUTE_TOPOLOGY_AND_RNG_LOG.md).)*

## What it does

One integer seed produces one deterministic Unity scene (`Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`) plus matching client/server collision payloads.

Generation is editor-time only, on purpose: a client-only runtime layout would disagree with the authoritative server collision.

Pipeline, in execution order:

```text
seed + density + generation profile + recipe catalog
  -> RouteIntent            semantic graph, no coordinates
  -> embedding              node centers
  -> room footprints        + recipe placements
  -> corridors              -> DungeonLayout
  -> elevation + transitions-> TieredLevelPlan
  -> ElevationEdgeModel     -> GameObjects
  -> collision export       -> Resources + server/src/world_data
```

## How to run it

| Goal | Action |
|---|---|
| Rebuild the playtest scene | **Arena > Dungeons > Rebuild Random Dungeon** |
| Reproduce a specific layout | **Arena > Dungeons > Rebuild Random Dungeon (Specific Seed)** |
| Build ONE named topology, whatever its weight | **Arena > Dungeons > Rebuild Random Dungeon (Specific Topology)** — the only way to reach a weight-0 graph, e.g. the authored `aperture-gallery` episode. Headless: `ARENA_DUNGEON_TOPOLOGY=<id>` |
| Switch density | **Arena > Dungeons > Density > 0..5** (per-user pref; `ARENA_DUNGEON_DENSITY` overrides; with neither, the profile asset's own `densityLevel`) |
| Plan only, no scene | **Tools > Dungeon Lab > Generate** |
| Batch evidence | **Tools > Dungeon Lab > Batch Validate (50 / 200 / 100 Locked Seeds)** |
| Prove the corpus RENDERS, not just plans | **Tools > Dungeon Lab > Render Sweep (50 / 200 Fixed Seeds)** — a full scene build per seed, so it is slow and separate |
| Check a topology draft | **Tools > Dungeon Lab > Validate Topologies** |
| Command line | `-executeMethod DungeonLab.Editor.RandomDungeonSceneBuilder.RebuildRandomDungeonBatch`, with `ARENA_RANDOM_DUNGEON_SEED` set |

Always publish/restart the server module after regenerating, so server movement
and spawning use the same geometry as the client scene
(`ops/republish-local-clear.sh`).

## Current shape

- Seven route topologies in the weighted draw, one per seed: processional spine, atrium ring, twin-wing keep, cataract shaft (descending), sunken basin, terraced cascade, ridge and ravine. An **eighth is authored but weight 0** — `aperture-gallery`, which exists to place the layered episode and is deliberately outside the draw. Each is one JSON file under `Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`; adding one costs no C#.
- Three required recipe slots (`required-compression`, `required-landmark`, `required-return`) filled from an explicit catalog; five enabled recipes currently, one of which (`episode_layered_gallery_01`) is eligible only on the authored topology.
- Vertical traversal from reviewed stair contracts, forged contracts, online synthesis, stairwell towers, and bridges — in that fallback order.
- One planned vista with a reserved sight corridor, plus 1–4 external connector
  promontories. Each external promontory is a straight eight-cell (32u) run,
  with at most one per cardinal direction. Its first added cell crosses the
  core dungeon's global outer face; exterior-connected concavities do not count.
- Density is a 0–5 dial. Design of record:
  [`density-scale-design-2026-07-27.md`](density-scale-design-2026-07-27.md).

**Three items in this list describe today's behaviour, not a rule, and the
layered-topology direction changes all three** — see "Where the work stands":

- **Everything rises from a shared abyss datum.** One global base level; every
  floor edge facing void drops to it. Becomes a per-face support base.
- **One plan coordinate carries one walkable surface** — *no longer true as of
  2026-07-31.* The canonical model is a `SurfaceField`: a column FLOOR plus
  whatever is suspended over it. Generation stacks in **two** places: an aerial
  span's deck cells (28 of the 200 density-0 seeds), and an authored recipe's
  upper storey — `episode_layered_gallery_01`, on 200/200 seeds of the
  `aperture-gallery` topology (2026-08-01).
- **Bridges are capped at 2 per dungeon and may not cross room interiors**, so a
  recipe cannot author one. The episode's storeys are joined by a stair and a
  flush lateral seam; the bridge-over-playable-geometry capability is the aerial
  span's, proved separately in C2b-3.

## The rules worth knowing before you change it

These earned their place by measurement. None is ceremony.

1. **Batch Validate never builds a GameObject**, so "hard-valid" is a claim about
   the PLAN. **Render Sweep** is what proves the corpus renders; it exists
   because a renderer defect hid behind 200/200.
2. **A recorded hash is a transient comparison value, never a gate.** Do not
   assert one in a test and do not re-baseline it per change — that ritual is
   what left three tests permanently red. Unrelated content drift is
   indistinguishable from a regression, because `ActiveContentDigest()` hashes
   the profile asset's bytes into every report. Compare against **the current
   commit** with `ops/dungeon-port-ab.sh`, which runs the same batch with the
   working tree stashed and restored and diffs the two reports seed by seed.
   Run-twice determinism (`ops/dungeon-step2-verify.sh`) is the other half.
3. **Every random decision draws from a stream keyed by
   `(seed, layout attempt, tier attempt, purpose, subject)`** via
   `DungeonRandomScope`. Nothing is threaded sequentially between stages, so a
   change to one decision provably cannot perturb another. Do not reintroduce a
   shared stream; it is what made stored hashes rot.
4. **The density dial is ONE table**, `DungeonDensity.Rows` in
   `DungeonGenerationProfile.cs`: slack, room-gap scale, enclosure, annex,
   mop-up and the fill backstop per level. Retune by editing six rows. Do not
   add a second seam. **Authored void is excluded from the void metric** — vista
   lane, reserved shaft windows, the gap an aerial span crosses, and a dais
   showpiece's backdrop.
5. **Two approaches were measured as wrong and reverted.** A blind pre-inflation
   stairwell-shaft reservation, and shrinking the lane pitch. Both are written
   up in the archived phase log; check before redoing either.
6. **The renderer consumes an already-valid plan and does not repair it.** One
   validation gate, `DungeonLabGenerator.Validation.cs`, runs inside the
   tier-attempt loop; renderer skips throw. A plan that fails validation cannot
   be rendered, saved or exported.
7. **`ops/dungeon-compile-gate.sh`** compile-checks both assemblies without
   Unity, so work continues while the editor holds `Temp/UnityLockfile`.

Measured cost of the dial, same seed built and exported at both ends: 2.5x the
play space (527 → 1308 floor cells) costs **2%** of the collision payload
(5.99 → 6.11 MB). A packed seam is one partition wall where an open cliff face
was a wall plus a railing plus corner kits, so boxes double while mesh instances
fall. **Tools > Dungeon Lab > Measure Density Cost** reports it; the peak is at
density 3, not 5, because a half-packed floorplan has the most boundary.

## Where the work stands

**No phased plan is in progress.** All previously planned workstreams are closed
— the route-first cutover, the topology-as-data port, the derived-RNG
rebaseline, and the density scale. Their evidence is archived:

- [`ROUTE_TOPOLOGY_AND_RNG_LOG.md`](../archive/2026-07-dungeon-phase-log/ROUTE_TOPOLOGY_AND_RNG_LOG.md)
- [`DENSITY_SCALE_PHASE_LOG.md`](../archive/2026-07-dungeon-phase-log/DENSITY_SCALE_PHASE_LOG.md)

Treat both as history, not as current constraints.

### Layered 3-D topology — PHASES A, B AND C ARE COMPLETE (2026-08-01). Phase D is in progress.

Design of record, **still a draft beyond Phase B**:
[`layered-topology-design-2026-07-29.md`](layered-topology-design-2026-07-29.md).
Branch `dungeon/layered-topology`.

**Phase A is complete (2026-07-31).** The plan can express surface identity;
nothing yet produces a second surface. *(A producer arrived in C2b-3 — see
"decks as surfaces" below. The sentence describes what Phase A shipped.)*

- **A1 — container migration, output-neutral.** `DungeonLabGenerator.Surfaces.cs`
  introduces `SurfaceKey` (cell, level), `LevelBand`, `PlanShadow` (pre-elevation
  domain) and `SurfaceField` (post-elevation surfaces), plus the shadow-agreement
  and connection-identity checks. `RoomConnection` gained `edgeId` — a
  correctness fix for edge lookup — and `plannedBand`, which is data only.
  Gate: per-seed `hashes.canonical` moved on **0/200** seeds against the parent
  commit; `resultHash 2731146954f3e57d` identical both legs.
- **A2 — the shadow repair, deliberately rebaselined.** Closes the architecture
  review's H2: promontory passes added cells to the level field and never to
  `floorCells`, so every shadow-derived metric described a dungeon missing its
  piers. `planShadowDisagreementSeeds` 200 → **0**; `resultHash` moved once, to
  `f387ca04df49d8a7`.

Two rulings worth carrying forward, both settled by measurement rather than by
the draft:

1. **Shadow agreement is one-directional: surfaces ⊆ shadow.** A shadow cell
   with no surface is legitimate — the gap under an external span deck stays a
   gap — and the shadow is the DOMAIN the level field floods within, so removing
   those cells would change what `FillUnassignedFloorCells` and `CleanPath`
   operate over. That side is counted and reported, not gated.
2. **The repair belongs at the end of planning, not at each producer**, which is
   where the draft's prose put it. `BuildExternalConnectorCandidates` derives
   `coreExtent` from `layout.floorCells`, so adding a promontory at its own
   producer moves the core's outer face and re-picks connector anchors.

A2's isolation, per-seed: only `hashes.layout` and `hashes.canonical` moved.
`routeIntent`, `tieredLevelPlan`, `existingTransitions`, `preservedCorePlan`,
`preCorrectivePlan` and `recipeResolutions` are byte-identical on all 200, as are
the accepted set, every validation result and every attempt count. The plan did
not move; only the shadow and what is computed from it did.

**Phase B is complete (2026-07-31).** Reservations and clearance are volumes.

`DungeonLabGenerator.Prisms.cs` replaces `StairPlacementLedger`'s five flat cell
sets with one `PrismLedger` of prisms, each carrying a half-open `LevelBand` and
a typed `OwnerKey`. Conflict is an asymmetric per-kind `blocksKinds` policy
seeded verbatim from the old `ConflictsWithReservation` — five kinds kept
distinct, so landing–landing, landing–clearance and mouth–clearance stay legal.
One named `BlocksHeadroom` predicate owns headroom, and `Landing` is excluded
from it on purpose. `TryValidateSpanHeadroom`, the `spanDeckLevels` side table
(16 references) and the duplicated deck formula in `.Batch.cs` are all gone: the
plan carries the ledger, so the acceptance gate re-runs the identical rule over
the identical reservations. The density annex/mop-up sweeps query a ledger
instead of a bare cell set. `OpenVolume` and its penetration allow-list exist and
are enforced, with no producer, per the phase's non-goal.

Gate: **identical geometry on all 200 seeds** against the parent commit — same
`hashes.canonical`, same accepted set, same failure codes. A field-level diff of
the two reports found the only difference anywhere to be one documentation
string naming the renamed class, so `resultHash` moved once,
`f387ca04df49d8a7` → `991d86e1bb577144`, and every validation message —
including the headroom gate's deck-cell count — is byte-identical.

Two rulings worth carrying forward:

1. **The headroom rule needs a third qualifier the design omits: the prism must
   declare a base.** "Another owner's `BlocksHeadroom` prism" alone is not
   enough, because today every reservation carries an unbounded band and an
   unbounded band intersects everything — so a stair footprint would violate the
   headroom of its own treads. `[-∞, +∞)` means "never asked for a height", not
   "solid from the abyss up". Exactly one producer declares a base today (the
   external-span deck), which is what makes the port output-neutral.
2. **There are two ledgers, because the density fill passes run a stage earlier
   than the transitions.** `AnnexAndMopUpLatticeVoid` runs during layout
   compilation; the transition ledger is built during elevation. They cannot
   share an instance — layout and tier attempts retry independently, so a shared
   one would leak a failed tier attempt's reservations forward. Same type, same
   policy, two instances.

**Phase C1 is COMPLETE (2026-07-31), all four legs.** The two-layer authored
episode is the first real proof (design §13); C2 is the authored recipe.

- **A renderer-neutrality instrument** (`8258391c`), because none existed. Every
  dungeon gate so far compares the plan, and Batch Validate never builds a
  GameObject — so a renderer change was invisible to every gate in the project.
  `Tools/Dungeon Lab/Render Digest` hashes every renderer and collider under the
  built root as (mesh, world transform, collider shape), sorted so it describes
  geometry rather than instantiation order. Proven before use: two independent
  runs identical.
- **The boundary band decomposition** (`13d03bbe`, design §7.1 step 1) — a
  boundary is decided by where solid mass sits in each column, not by comparing
  two levels. `IsGroundBacked(s)` = s is a Floor AND the lowest in its column;
  suspended surfaces contribute no wall mass, which is what keeps a chamber under
  a gallery open. **Byte-identical rendered geometry on all 200 seeds**
  (`renderDigest ea9b25ed2405324d…`).

Three things measured during C1a that the design had wrong or open:

1. **`_O_` and `_E_` floors share a pivot and footprint exactly** — the
   `OneSided/` vs `PivotEdge/` folder names suggest otherwise, and a mismatch
   would have displaced every deck. The swap is a drop-in.
2. **`_E_` gives a CONVEX MESH collider, not a box.** §7.2's soffit remedy is
   "emit soffits as box colliders" (0.35u window, no normal test); §0.1's `_E_`
   answer lands on the 1.2u window and IS normal-tested. The live probe is
   therefore testing a different thing than §7.2 prescribes — worth knowing
   before reading its result.
3. **The soffit swap has no site yet.** Bridge decks are authored set-piece
   geometry, not floor tiles, and every `FloorName` use is a ground-backed floor.
   §7.1 step 3 arrives with the first gallery surface, not before.

**C1b landed 2026-07-31**, in two parts. Part 1 gave `SurfaceField` a real
stacked backing store — the heightfield holds each column's LOWEST surface and
an overlay carries what is above it. Part 2 renders one: surfaces travel into
the renderer, a suspended surface gets its own floor tile and its own rim
guards, and the whole C1 episode exists as a fixture.

- **Surfaces reach the renderer.** `BuildLevelField` takes `levels` as the
  column FLOOR plus an `IReadOnlyCollection<StackedSurface>` of what stands over
  it. Passing null is today's plan on today's path; nothing branches on being
  single-layer, the stacked passes simply have nothing to iterate.
- **§7.1 step 3, the soffit pass, has its first site.** A surface above its
  column floor renders with `P_MOD_Floor_01_E_straight_med` — the closed slab —
  instead of the `_O_` plane, with a startup check that the two share a pivot so
  a swapped prefab cannot silently displace every deck.
- **Rim guards are per surface.** A suspended surface emits no `WallEdge` at
  all, so the wall walk cannot guard it; a new pass rails every lateral edge
  whose neighbouring column carries no surface at that level, and
  `OpenFloorEdge` gained a level so an aperture's rim can be declared bare.
- **The two-layer episode** (`Tools/Dungeon Lab/Print Two-Layer Episode
  Fixture`): upper route, a 1-cell aperture with four bare rims, a chamber under
  it, a four-strip return stair, and a bridge over the lower route. 14 stacked
  surfaces, 4 bare + 13 railed rims — and the fixture derives those counts from
  its own surface set, so the snapshot compares two independent derivations
  rather than the renderer against a typed-in number.

Six things measured during C1b that the design had wrong:

1. **`visited` needs no level discriminator, and neither does `EdgeKey`.** The
   hand-off and §7.1 both frame the cell-pair visit key as a collision. It is
   not: `BuildWallEdges` dedupes on an unordered cell pair, which is exactly the
   identity of a BOUNDARY, and §7.1's own construction walks the boundary
   between two COLUMNS and never pairs surfaces. Keying it on a surface would
   visit each boundary once per stacked surface and emit its faces twice.
2. **Stacking changes no wall face at all**, and that is a property of the band
   model rather than luck. With the slab band emitting no `WallEdge` (C1a's
   fascia ruling) the only band a column has is the ground band under its floor
   — which *is* `levels[cell]`, because part 1 put the lowest surface in the
   heightfield. `ComputeColumnMass` and `DecomposeBoundary` are untouched.
3. **The real discriminator is on the RIM.** `WallEdge` already carries its own
   extent. `OpenEdgeKey`'s producers all describe the level field, so a level on
   that key would be `levels[cell]` written by every producer — a rename, not a
   discriminator — and would break the producers whose cells are not in the
   level field. What genuinely could not name a stacked surface was the
   railing-only edge list, which looked its height up as `levels[cell]`; it is
   now `RimEdge (x, z, level, direction)`. The `(x, z, direction)` suppression
   sets are the other site, now gated on the rim being at its column floor.
4. **`SurfaceField` could not express `IsGroundBacked`.** Part 1 stored bare
   levels, so `IsLowestInColumn` answered half the predicate and nothing
   answered "is it a Floor". `SurfaceKind` closes it. The renderer still answers
   the deck half from `aerialDeckCellLevels`, because promoting decks to
   surfaces is a behaviour change C1b does not make.
5. **`DecomposeBoundary` still cannot emit two faces for one column pair.** §7.1
   describes a multi-interval walk; with only ground bands (Support and Wall
   prisms have no producer) two ground bands share the abyss floor and can
   differ only at the top, so at most one interval is one-solid. The single-face
   return is provably equivalent today and the walk is written as a walk.
6. **The fall-free-connectivity invariant is not checkable through the port
   graph.** `TryBuildFloorStairPortGraph` keys nodes on the level field, so it
   cannot see a stacked surface. The fixture walks its own surfaces instead;
   teaching the port graph is §3.2 traversal work, which C1 does not do.

**The live leg ran 2026-07-31, and §7.2's hypothesis held.**
`ops/c1-two-layer-live.sh` bakes the episode through the production export path,
publishes, and runs `ops/c1-two-layer-probe.py`. All
four §7.2 behaviours passed on the real server: a player under a gallery stays at
`y=+0.000` where a soffit capture would read `+3.50`; on the gallery they stand
at `+4.000`, not on its underside; walking off the aperture's bare rim lands them
on the chamber floor 0.2u from the aperture cell rather than the abyss 20 levels
down; and mid-span they stand on the deck with the lower route at `+0.00`
underneath. **It confirms the hazard does not bite — not that §7.2's remedy
works**, because per C1a the `_E_` slab is a convex mesh, so what was tested is
the normal-filtered 1.2u capture, not the box collider's 0.35u one.

Three things the live leg found that no plan gate and no render digest could:

1. **The outer shell pass is heightfield-only and walled the upper route shut.**
   Where a ground-backed terrace met a suspended gallery at the same level, the
   shell put a 5.7u enclosure wall across the seam and the player could not
   cross. The retaining face beneath is correct and stays; it is the guard on
   top that was wrong. Fixed by a **flush seam** — a face whose open side carries
   a walkable surface at the face's top level suppresses its top guard, which
   suppresses shell courses, railing and railing corner columns in one move.
   Inert single-layer by construction.
2. **The module panics every tick on an empty dungeon door manifest**
   (`world_interactions.rs:930`), so baking geometry with no gateways killed
   `game_tick` while every reducer still reported `Committed`. Doors and traps
   are deferred; the bake leaves both manifests alone.
3. **There is no runtime world selector** — world collision is `include_str!` at
   compile time, so a fixture can only reach the real ground sampler by being
   baked into the dungeon's own payload.

**Leg 4, the owner eyeball, PASSED 2026-07-31 — "it looks fine". Phase C1 is
complete.** No hash tells you whether a two-layer room reads well; this was the
only leg that could answer it.

**The episode does NOT survive in the scene, and this page previously implied it
did.** `ops/c1-two-layer-live.sh` does leave it there, but any later
**Rebuild Random Dungeon** overwrites it with an ordinary dungeon — which is what
happened right after the live probe on 2026-07-31 (`restore_rebuild.log`), so
the scene held a full 6225-instance dungeon rather than the episode. Re-bake it
from inside the editor with **Tools > Dungeon Lab > Bake Two-Layer Episode Into
The Dungeon Scene (TEMPORARY)**; it needs no publish and no probe for a
look-only pass. A baked episode is ~1000 prefab instances spanning
x −16..20, z −16..44 — if the scene is much bigger than that, you are looking at
a generated dungeon instead. **Baked for the eyeball 2026-07-31.**

Where to look, in world coordinates (from `two_layer_episode_probe.json`):

| What | Where |
|---|---|
| Gallery cell with the bare south rim — the aperture you walk off | (0, 4, 36) |
| Chamber floor the fall lands on, under the aperture | (0, 0, 32) |
| Chamber floor directly under a gallery slab — the `_E_` underside | (4, 0, 32) |
| Return stair, foot → top | (−12, 0, 28) → (−12, 4, 44) |
| Terrace, ground-backed at the gallery's level (the flush seam) | (0, 4, 44) |
| Span deck, with the lower route underneath at L0 | (0, 4, 4) over (0, 0, 4) |

### C2's blocker is cleared — the level field is a `SurfaceField` on the writer side

**Landed 2026-07-31.** The elevation stage builds a `SurfaceField` and hands it
to the plan; the plan no longer re-wraps a heightfield. Every write goes through
one of three named operations instead of an indexer assignment whose meaning
lived in the surrounding guard:

| Operation | Means | Callers |
|---|---|---|
| `TrySetFloorLevel` | set the column floor, reject a conflicting one | the old `TrySetCellLevel`, at all five of its call sites |
| `AddFloorLevel` | the column has no floor; give it one | both fill passes, the named vista promontory, the external connector piers |
| `RelevelFloor` | the column has a floor; MOVE it | the recipe zone write, and nothing else |

That third row is the blocker, now named rather than implied. All three are
**layer-blind** and throw on a column that carries a stacked surface, so the next
producer to stack cannot silently truncate a column to its lowest level — which
is the general form of the failure §8.2 found in the recipe resolver. Stacking
goes through `AddSurface`.

Gate: `ops/dungeon-port-ab.sh` at density 0 — **identical geometry on all 200
seeds**, and `resultHash` itself did not move (`991d86e1bb577144`, Phase B's
value), so every seed report is byte-identical including validation messages.

Three things measured during the migration that the map had wrong:

1. **The write-site map was missing a producer, and the grep that built it could
   not have found one.** §8.2 enumerates "four deliberate bypasses"; there are
   **five**. `TryResolveExternalConnectorPromontories` writes pier levels at
   `DungeonLabGenerator.CorrectiveConnections.cs:170` — with `cellLevels.Add(…)`,
   not `cellLevels[…] =`, which is why a `cellLevels[` search missed it. The same
   design doc names that exact function in §3.1 as H2's producer of cells that
   reach the level field, so the omission is internal to the doc, not to the code.
2. **The recipe overwrite is a live path, not a latent one.** `Elevated` zones are
   validated to carry `relativeLevel > 0` (`DungeonRecipeValidation.cs:363`), and
   four enabled recipes have one — `connector_corner_return_01`,
   `connector_example_01`, `connector_flexible_vestibule_01` and
   `episode_throne_twin_stairs_01`. `RelevelFloor` fires on essentially every
   seed, so the 200-seed gate exercises it rather than stepping around it.
3. **Do not sort the shadow reconcile.** `ReconcilePlanShadowWithSurfaces` adds
   surfaced cells to `layout.floorCells`, a `HashSet` whose own enumeration order
   is a function of its insertion order and which later passes enumerate. The
   canonical `Surfaces()` ordering is the tidier thing to iterate and it moves
   seeds; the port iterates the field's backing store instead, which is what
   `SurfaceField.SurfacedCells()` exists for.

### C2a — the recipe layer schema, landed 2026-07-31

A recipe can now declare storeys, and the resolver stacks them. `RelevelFloor`
kept the base storey; a zone on any other storey calls `AddSurface`. That is the
branch the whole `SurfaceField` migration existed to make possible, and it is one
`if`.

- **`DungeonRecipeLayer { layerId, relativeLevel, isBase }`**, plus `layerId` on
  zones, ports and both ends of a transition. Empty means the base layer, so a
  recipe that declares nothing behaves exactly as before — which is every recipe
  in the catalog.
- **`RelativeLevelAt` is layer-scoped.** §8.2 called its `max` over overlapping
  zones "the heightfield assumption inside the recipe schema"; scoping the max to
  one storey is the entire fix. Within a storey the max stays, because two
  Elevated zones overlapping on one floor still describe one surface.
- **Base derivation subtracts the layer offset** and nothing else. §8.2's
  replacement (all-ports-agree, `RECIPE_BASE_LEVEL_CONFLICT`, an
  `anchorLayerId`/`anchorLevel` escape hatch) stays retracted.
- **`RECIPE_LAYER_ID`, `RECIPE_LAYER_BASE`, `RECIPE_LAYER_CONNECTIVITY`.** The
  last one is an undirected reachability walk from the base over the recipe's own
  transitions — undirected because a stair is walkable both ways, and the
  directed question is the plan's fall-free invariant, not one recipe's.
- **A cross-layer stair may rise more than 1u.** Intra-layer transitions keep the
  exact `riseLevels == 1` rule, so a recipe with no layers cannot reach the
  relaxed branch. C1's episode separates its storeys by 4u, which would have been
  unauthorable otherwise.

Gate: **identical geometry on all 200 seeds.** A field-level diff of the two
reports found the ONLY per-seed difference anywhere to be `schemaUsage` growing
19 → 21 rows — the two documentation rows for the layer fields. Every hash,
every validation result and every recipe resolution is byte-identical, so
`resultHash` moved once, `991d86e1bb577144` → `385e29f388fdae7a`.

Two things worth carrying forward:

1. **A recipe's canonical string is inside `hashes.canonical`.** The chain is
   `ComputeContentDigest` → `catalogDigest` → the route-intent projection →
   `routeIntentHash` → `canonicalHash`. So an unconditional append to the recipe
   canonical moves EVERY seed's canonical hash for a schema addition that changed
   no geometry. Every layer field is therefore appended only by a recipe that
   declares layers — the same conditional the incident-socket fields already
   used, which is where the pattern came from.
2. **Sunken rooms are a negative LAYER, not a negative zone.** §8.2 wanted
   `relativeLevel <= 0` allowed on Elevated zones (`DungeonRecipeValidation.cs`).
   Unnecessary: a layer's `relativeLevel` may be negative, and an Elevated zone
   still rises within its own storey. One existing gate left untouched.

### C2b-1 — the reader side, container half, landed 2026-07-31

The reader side turned out to be **two** problems, and only one of them is a
container migration. C2b-1 is that one. A stacked field used to throw at the
first of **8** `AsHeightField()` sites; **2 remain**, plus the one property that
feeds them, and each is named for the single thing blocking it.

Readers classified into three groups, which is what the work was:

1. **Column questions** — "is this column surfaced" / "what is its floor". These
   answer identically however many surfaces a column carries, so migration was a
   parameter type: the external-connector candidate search, the stair search's
   void-only footprint tests, the whole boundary-context build, route
   requirements, the accepted-connector geometry probes.
2. **Genuinely surface-shaped** — the answer changes once a column stacks.
   Aerial-deck overfly clearance now reads the column's HIGHEST surface (a
   gallery at deck height was invisible to it); `TryValidateSurfaceHeadroom` runs
   the rule per SURFACE rather than per cell; `PlannedCellsAreCompatible` asks
   "unsurfaced, or surfaced at exactly L" instead of comparing against the floor;
   `GetLevelRange`/`CountDistinctLevels` range over surfaces, so a stacked plan
   cannot read as flat; the recipe zone check asks whether the zone's own storey
   still stands.
3. **Blocked on `TransitionEdge` having no levels** — deferred to C2b-2.

`RECIPE_LAYER_UNVERIFIABLE` is **gone**, replaced by the real check it was
standing in for: `baseLevel + layer offset + the layer-scoped rise`, verified with
`HasSurfaceAt`. `RecipeZonePlacement` gained a `layerId`, because it carried a
layer LEVEL and no layer IDENTITY — and a level cannot tell two storeys apart.

Gate: **identical geometry on all 200 seeds**, and a field-by-field diff of the
two report JSONs found exactly one difference in the whole file, the generation
timestamp. `resultHash` did not move (`385e29f388fdae7a`, C2a's value). Both
fixtures re-run green, and the two-layer episode now exercises the aerial-bridge
pass against a genuinely stacked field — it was being handed the bare heightfield,
which was the very defect this phase closes.

Five things measured that the plan or this page had wrong:

1. **This page named the wrong argument.** It said `BuildLevelField` "is passed
   `null` at `DungeonLabGenerator.cs:369` — that argument becomes
   `StackedSurfaces()`". That `null` is `reservedSetPieceCells`. The stacked-
   surface `null` is hard-coded inside the FORWARDING overload
   (`ElevationEdgeModel.cs:226`), so both production render call sites were
   routed to the full overload instead.
2. **`AerialBridgeIsRedundant` looked like C2b-2 work and is not.** Its BFS hops
   through transition links, which suggested it needed transition levels — but
   the hop only tests that the neighbour column is surfaced and never resolves a
   level. It walks `(cell, level)` now; the transition hop reaches every surface
   in the linked column, which over-states connectivity in the safe direction for
   a "reject duplicate walks" heuristic.
3. **`AsHeightField()` and `ColumnFloors()` are different questions**, so they are
   different methods. The first is the migration shim and its throw means "this
   reader believes a cell has one surface and is now wrong"; the second serves a
   reader that takes BOTH halves and reassembles the columns itself — the
   renderer. Handing the renderer the shim would throw on exactly the plans it
   exists to draw.
4. **The `SurfacedCells()` ordering trap resurfaced**, in the pre-corrective
   projection: it rebuilds a dictionary whose own enumeration order reaches a
   port-graph diagnostic string, so it iterates the backing store rather than the
   `PlanCells()` copy. Same rule as the shadow reconcile.
5. **`SweepIntraRoom1uDrops` stays floor-scoped on purpose.** It sweeps the drops
   the stage's own layer-blind leveling produces; an authored storey's internal
   drops are the recipe's to declare, not a sweep's to discover.

### C2b-2 — transition levels, landed 2026-07-31

`TransitionEdge` now carries `firstLevel`/`secondLevel`. **`AsHeightField()` has
no callers left outside its own file**, so the reader side is done: nothing
resolves an elevation by looking a transition's cell up in the level field.

The lookup was the blocker, and it broke a hard gate rather than degrading
quietly — a cross-layer stair whose upper end stands over its own lower end
resolved both endpoints to the column floor, computed a delta of 0, and was
rejected by the transition-contract gate as too shallow to be a stair.

Recording levels at construction is safe because nothing can move them
afterwards, and that is a property of C2a's three named writers rather than of
pass ordering: `TrySetFloorLevel` rejects a conflicting value instead of
overwriting, `AddFloorLevel` requires an empty column, and `RelevelFloor` — the
only mover — runs inside `TryRealizeRecipes`, which precedes every producer
including its own transitions. Six of the seven producers already had both levels
in local scope; they had to, to decide which end was raised.

- **The recipe's layer ids now reach the plan.** C2a put `lowerLayerId`/
  `upperLayerId` on the recipe ASSET, but only the authoring validator read them —
  `RecipeTransitionPlacement` dropped them, so an authored cross-layer stair
  validated and then lost its identity on the way in. A base-storey end still
  reads the field (identical to before, which is what keeps the catalog neutral);
  a stacked end comes from the layer schema, the same expression the resolver
  used to write it.
- **Landings needed nothing.** A landing was already required to sit at its
  transition's endpoint level; now that the transition states that level, the
  check is a plain `HasSurfaceAt`.
- **The port graph nodes every surface.** This retires C1b's finding 6 — the
  fixture no longer has to walk its own surfaces because the graph cannot see
  them.
- **An unleveled endpoint is RECORDED, not thrown** (`TransitionEdge.UnknownLevel`),
  so the missing-cell rejection survives verbatim. It has never fired across the
  corpus.

Gate: **identical geometry on all 200 seeds**, field-level diff of the two
reports = the generation timestamp alone, `resultHash` unmoved at
`385e29f388fdae7a`. The endpoint-level rows in the transition projection are
conditional on the plan stacking, for the same hash reason as C2a's layer fields.

**One thing C2b-2 made visible that no gate could have.** The two-layer episode
now runs the PRODUCTION port graph, and it sees every surface (88/88 including
all 14 stacked) but reports 84/88 reachable. The cause was tested, not guessed:
re-running the fixture's own walk with the single rule change of dropping
transition footprint columns reproduces the port graph's answer exactly —
disconnected, 4 unreachable. **The port graph treats a transition footprint as a
stair BODY and removes the whole column at every level; for an aerial deck the
footprint sits above the route it crosses, so a deck over playable geometry
severs that route in the traversal graph.** Pre-existing and invisible until now
(the graph could not even build on a stacked field), and harmless in production,
where a span flies over authored void rather than over a walkable route. This is
the design's "aerial-bridge path promoted so a deck's cells become surfaces"
(§13 Phase C Systems), now with evidence attached — and it is a prerequisite for
the authored episode, whose whole point is a bridge over playable geometry.

### C2b-3 — decks as surfaces, landed 2026-07-31

`SurfaceKind.Deck` has a producer. An aerial span's footprint cells are walkable
surfaces in the plan, the port graph no longer deletes the column under a
bridge, and the two-layer episode walks whole: **94/94 port-graph nodes, up from
84/88.**

The change is one sentence with one qualifier: **a deck is a surface but it is
not a floor.**

- **The heightfield means the column FLOOR** — the lowest surface *resting on
  fill* — and a suspended surface goes to the overlay whether or not the column
  has a floor beneath it. That qualifier is what makes the whole thing safe: it
  leaves `TryGetFloorLevel`, `FlooredCells()`, `FlooredPlanCells()` and
  `HasFloor()` (the old `ContainsCell`) answering exactly what they answered
  before, so the flood fill, the plan shadow, doorways and the overlook stat are
  untouched by CONSTRUCTION rather than by audit. `hashes.layout` moved on 0 of
  200 seeds.
- **The port graph's footprint rule is read off the plan.**
  `BuildTransitionBodyCellSet` still means "a stair body fills this column",
  minus any column that carries a deck. Not a placement-class test: the
  reviewed-contract corridor span shares the `externalSpan` class, may only
  cross cells proved unsurfaced, and still consumes its columns — of which there
  is nothing to consume.
- **The headroom rule needed to know who CARRIES a surface.** The deck stands on
  the very prism that declares the deck's base, so without this every
  bridge-bearing seed would fail its own headroom gate. `PrismLedger` records
  the carrier at `RegisterSpanDeck`.
- **The renderer ignores `Deck`.** Its slab, railings and undersides are the
  transition's set piece; a floor tile and rim guards over it would be a second
  deck in the same place. It is also the one surface that legitimately stands in
  a floorless column, so the skip precedes `SurfaceColumns`' floor check.

Gate: **28 of 200 seeds moved, and they are exactly the 28 that place an aerial
deck** (`synth_deck*_bridge`); the other 172 are byte-identical. A field-by-field
diff of the two reports says precisely what moved on those 28: `hashes.canonical`
and its three plan-stage components, the conditional endpoint-level rows
(**998 rows ADDED, 0 changed in value** — no transition field present on both
legs differs at all), the port-graph summary and its reachability message.
Everything derived from the floor field held on all 200: level range, transition
counts, stair usage/topology/placement class, overlook count, promontory and
connector cells, recipe resolutions, the headroom gate's message, the accepted
set (200/200 both legs) and every failure code. Rendered geometry is
byte-identical too (`renderDigest bdb90e5e8a696dd7`, 12 seeds, three of them
deck-bearing including a sloped span). Both fixtures re-run green.

Four things measured that the design doc or this page had wrong:

1. **"Spans fly over authored void" was an assumption; it is now a number.**
   Across 200 seeds the corpus carries **118 deck surfaces and 0 of them stand
   over a floor** (`tieredLevelPlan.deckSurfacesOverFloor`, new). So the severing
   defect really was latent in production and only bites an authored episode —
   and the port graph's floor-node count grew by *exactly* the deck count on
   every one of the 28 seeds, which is the same fact derived a second way.
2. **`SurfaceKind` alone does not make a deck safe in the heightfield, and the
   C1b comment in `DungeonLabGenerator.Surfaces.cs` said it did.** Its reasoning
   — "a deck over a true gap IS the lowest thing in its column", so a kind is all
   that stops it becoming a pillar — is right about the renderer and wrong about
   everything else. Put a deck in the heightfield and `TryGetFloorLevel` starts
   answering about it: the flood fill seeds at deck height, a doorway opens onto
   thin air beside a span, and the plan shadow swallows the gap the deck was
   built to cross. The kind is what lets the FIELD decide where to store a
   surface; relying on ~50 readers to consult it is the version that does not
   work.
3. **§12 scenario 2's mechanism is not what proves the separation.** It says "a
   `Support` prism under the deck and a `Clearance` prism above the lower
   surface prove ≥3u separation". Neither exists or is needed: the span deck's
   own `Footprint` prism declares a base, and the one headroom rule computes the
   band per surface. `Support` still has no producer. The scenario's first
   sentence — deck cells are `Deck` surfaces, the cells below carry `Floor` — is
   now literally true.
4. **The connectivity gate now proves every deck is reachable, for free.**
   `IsFallFreeConnected` requires EVERY node reached, so a deck chain that
   connected to nothing would reject its seed. 200/200 pass, which means each
   deck's lateral chain genuinely reaches its lower landing — including the nine
   sloped (`d1`) spans, whose cells all record the deck's flat level and so meet
   only the lower landing laterally.

Still true, and still the reason all of this exists: before C2a, two layers over
one plan cell overwrote rather than stacked.

Three §8.2 mechanisms dissolved on inspection and should not be built:
`baseLevel` is not derived from port zero, it **is** the node's level (every port
is already required to resolve at `nodeLevel + port.relativeLevel`), so
`RECIPE_BASE_LEVEL_CONFLICT` and the explicit `anchorLayerId`/`anchorLevel`
escape hatch both have nothing to do.

### C2 — the authored episode, landed 2026-08-01. PHASE C IS COMPLETE, owner-accepted.

**Generation produces an authored two-storey room.** `episode_layered_gallery_01`
is a real catalog recipe with a `base` storey and an `upper` one 4u over it, and
`aperture-gallery.json` is the hand-authored topology that places it. On its own
topology: **200/200 seeds accepted, and every one of them stacks.**

Two commits, deliberately, because the gate can only say one thing at a time.
The CODE leg has no content and must move nothing; the CONTENT leg moves every
seed's `hashes.canonical` through `catalogDigest` and can only be read field by
field.

**The code leg (`11b8da22`) — four things C2a shipped half of.**

- **A Walkable zone on a NON-BASE layer produces that storey's floor.** C2a wrote
  a level only for an `Elevated` zone, which is right on the entry storey (a
  Walkable zone there names the floor the room already has) and empty on a
  stacked one — nothing else in the pipeline puts a surface up there. A declared
  upper storey validated, resolved, and rendered as nothing.
- **`RECIPE_LAYER_CONNECTIVITY` accepts a flush lateral seam.** See finding 3.
- **Recipe `openings`** — an authored bare rim on a stacked storey, end to end:
  `DungeonRecipeOpening (cell, outward direction, layer)`, `RECIPE_OPENING`,
  the mirror/rotation transform, a plan-side read-back, and
  `BuildPlannedOpenEdges` merging them with the external connectors' throats at
  BOTH render call sites. Rims are surface-scoped; connector throats stay
  column-scoped.
- **The candidate gate now mirrors the validator on cross-layer rise.** C2a
  relaxed `riseLevels` for a cross-storey stair in `DungeonRecipeValidator` and
  ONLY there, so such a recipe validated in the authoring window and was then
  rejected as a CANDIDATE on every seed as `TRANSITION_CONTEXT_INCOMPATIBLE`.

Plus `ARENA_DUNGEON_TOPOLOGY` and **Arena > Dungeons > Rebuild Random Dungeon
(Specific Topology)** — the only way to build a weight-0 topology.

Gate: **identical geometry on all 200 seeds.** A field-by-field diff of the two
reports (520 510 leaves) found the only per-seed difference anywhere to be
`schemaUsage` growing 21 → 22 rows, the one documentation row for the opening
fields. `resultHash` moved once, `fef5c66b65cea791` → `d18d46a7c0cb0f19`. Render
Digest 12 seeds byte-identical at C2b-3's `bdb90e5e8a696dd7`; both fixtures green.

**The content leg.** `weight: 0` on the topology is not timidity, it is the gate:
adding a weighted topology moves `totalWeight` and re-rolls the topology of every
seed in the corpus, which destroys the one instrument that tells a regression
from a rebaseline. The recipe is eligible for role `landmark` and beat
`aperture`, and **no other recipe declares that beat and no other topology
declares that node** — so it is the unique candidate on its own graph and cannot
enter any other topology's candidate pool.

Evidence, all at density 0:

| What | Measured |
|---|---|
| Plans, on the forced topology | **200/200 accepted**, no rejections |
| Stacks | 200/200 — the transition projection carries endpoint levels, which is conditional on `!IsSingleLayer` |
| The gallery | 8 stacked surfaces per episode at `baseLevel + 4`, 1-cell aperture, 4 authored bare rims |
| The return stair | 4 authored `dais` steps climbing `base+0 → base+4`, with the authored landings and footprints |
| Renders | 12/12 built, `REJECTED 0` |
| The gallery is real geometry | **8 `P_MOD_Floor_01_E_` prefab instances** in the baked scene, one per gallery cell, all at `y = 12` (= the node's level 8 + the layer's 4). The closed slab has exactly one site in the renderer — a surface above its column floor — so its presence *is* the gallery. (The raw guid appears 112 times; that is 14 property references per instance, not 112 pieces.) |
| Topology rules | `Validate Topologies`: 8/8 PASS |

**Six things measured that the plan or the design had wrong.** Five of them cost
a rejected corpus first.

1. **A recipe room is fenced off from `SweepIntraRoom1uDrops` by construction,
   and the first draft of the episode depended on the opposite.**
   `TryRealizeRecipes` registers every `placement.roomCells` as a `Landing`
   prism, and `BlocksKind(Footprint, Landing)` is true — so `BlocksFootprint` is
   true for every cell of a recipe room and the intra-room 1u sweep skips the
   whole room. **A recipe owns every transition inside its own footprint.**
   Measured: a recipe that declared a stepped ramp and left the stairing to the
   sweep produced **21 unreachable port-graph nodes on every seed** — the 8
   gallery cells, the 7 terrace cells and the 3 treads, which is exactly the
   recipe's raised set — and lost 100 of 200 seeds to `PORT_GRAPH`.
2. **A multi-step stair cannot be authored IN LINE.** `RECIPE_CLEARANCE` forbids
   a cell being both a footprint and a landing anywhere in the recipe, and in a
   single-file flight every intermediate cell is both. The flight has to be TWO
   cells wide: one lane carries the treads (the footprints), the other the
   landings. Only the first step's lower landing can be in line, because it is
   the one cell below the flight.
3. **A cross-layer stair cannot be a step strip, so the C2a rise relaxation has
   no renderer behind it.** `PlaceSeamStepStrip` admits `seam`/`dais` only at
   delta 1 (2 for dais) — "which no strip family covers" — so a 4u cross-storey
   transition throws in the renderer. Two storeys 4u apart therefore join
   LATERALLY, which is what C1's proven geometry already did, and is why
   `RECIPE_LAYER_CONNECTIVITY` had to learn the flush seam. It is not a
   loosening: a 1u strip from a ground column onto a suspended slab spans a face
   with the whole storey gap open underneath it.
4. **The vista promontory reads the target CELL's floor level, not the node's.**
   `TryResolveNamedVistaPromontory` compares `surfaces.TryGetFloorLevel` at the
   two facing boundary cells, so an authored room whose face toward the vista
   source is RAISED fails `ROUTE_PROMONTORY` even when its node sits exactly the
   required 4u below. Measured: the first draft put its 4u terrace on that face
   and lost the other 100 seeds. The episode now keeps an outer ring of chamber
   at the base level on all four sides, which also makes it orientation-blind.
5. **The topology validator's vista reach falls back to
   `roomEnvelopeRadiusCells` when NO recipe is eligible for a slot**, so a
   role/beat typo reads as "this room is as big as the envelope" rather than "no
   recipe matches". That fallback is what surfaced a beat mismatch, but it
   surfaced it as a vista-clearance violation.
6. **A route-forward slot pays for BOTH of its recipe's extents.**
   `RouteTopologyWorstCaseRecipeReach` counts only the primary extent for a
   `vista-source-to-target` slot and both for route-forward, because the exit
   edge may point along either world axis. The episode's 7x7 footprint therefore
   reaches 3 and clears the vista by exactly the required 3 — **zero margin**,
   the same as `processional-spine` and `twin-wing-keep`.

**Two limits worth stating plainly.**

- **The episode has no bridge over its own lower route, and cannot.** The C1
  fixture's bridge came from `AddAerialBridges`, a generator pass that is capped
  per dungeon and may not cross room interiors, so no recipe can author one.
  Bridge-over-playable-geometry is the capability C2b-3 delivered and proved
  (fixture at 94/94 nodes, plus 118 deck surfaces across the corpus); the
  authored episode proves the OTHER half — a room that owns two storeys.
- **A gallery-carrying column consumed by a transition footprint still loses
  BOTH of its surfaces from the port graph.** C2b-3 fixed this for decks
  (`CarriesDeck`); the general form — a stair body fills `[min level, max level]`
  and not the column to the sky, which `TransitionEdge` has been able to state
  since C2b-2 — is NOT fixed. The episode authors around it by keeping its stair
  footprints out of the gallery's columns. **No gate can see this**: the node is
  simply absent, so it can never be reported unreachable.

**Order: ~~level field → `SurfaceField` (writer side)~~ → ~~the recipe layer
schema~~ → ~~the reader side, container half~~ → ~~transition levels~~ → ~~decks as
surfaces~~ → ~~the authored episode~~. ALL LANDED.** The original order had two
steps. The reader side was not visible as its own step until the writer side made
a stacked field reachable; its split into a container half and a model half was
not visible until the readers were classified; and the deck-as-surface step was
not visible until the port graph could run on a stacked plan at all.

**The owner eyeball PASSED 2026-08-01 — "it's fine". PHASE C IS COMPLETE.**
That was the last of the phase's four evidence legs and the only one no hash
could answer. The episode is baked into `RandomDungeon.unity` at seed 2026072100;
rebuild it any time with **Arena > Dungeons > Rebuild Random Dungeon (Specific
Topology)**, topology `aperture-gallery`. Its gallery sits at world
`y = 12` around `(4, 12, −64)` — the node's level 8 plus the layer's 4.

### Phase D — multi-layer rooms in GENERATION. IN PROGRESS; D0–D4 landed 2026-08-01.

Design §13. Topologies and recipes declare layers, and routes bind to them.
Four of C2's corrections are constraints on anything Phase D authors rather than
incidents: a recipe owns every transition inside its own footprint, a multi-step
stair needs two lanes, storeys 4u apart join laterally, and the vista promontory
reads the target CELL's floor level.

**The slices, in order.** Each one ends output-neutral against the previous
commit, and the neutrality is by construction rather than by luck: **nothing in
the shipped corpus declares a layer**, so every rule Phase D relaxes is gated on
a binding no topology has. That is the same weight-0 discipline `aperture-gallery`
used, moved from content into code.

| | What | Neutral because |
|---|---|---|
| **D0** ✅ | the transition body is a **band**, not a column to the sky | measured: 0 surfaces stand over a stair body anywhere in the corpus |
| **D1** ✅ | the **topology layer schema** — node `layers`, edge `fromLayer`/`toLayer` — parsed, validated by `Validate Topologies`, carried into `RouteIntent` and `plannedBand`, and consumed nowhere | no topology declares layers |
| **D2** ✅ | the **corridor-exclusivity and third-room relaxations** — *authorized* by layer binding, *decided* by disjoint absolute bands (§8.1) — **together with the stacked-corridor producer they need**, which the design separates and which measurement says cannot be separated (see D1's finding 1) | no connection is layer-bound |
| **D3** ✅ | a bound edge **resolves at its layer's elevation** — a per-`(connection, end)` entry level beside today's per-node `zoneLevels`, and a slot mapping a topology layer id to a recipe layer id | ditto |
| **D4** ✅ | **volumes** — `RoomFootprint.Overlaps` volumetric, an `OpenVolume` producer, `ChooseEnclosedRooms` moved into the plan | the volumetric test needs a declared reason, and nothing declares one |
| D5 | **content** — an atrium topology plus a room binding route edges at two elevations | weight 0, exactly as `aperture-gallery` is |

**D0 — the transition body is a band, not a column (landed 2026-08-01).**

The port graph treated a transition footprint as a stair body and deleted the
whole COLUMN at every level, so a gallery over a stair-adjacent cell lost BOTH
of its surfaces. C2b-3 fixed it for decks alone (`CarriesDeck`); the general form
has been available since C2b-2 put both endpoint levels on `TransitionEdge`. A
stair body now fills `[min endpoint level, max endpoint level]` and consumes only
the surfaces inside it.

**No gate could see the defect, and none could see the fix either** — an absent
node can never be reported unreachable. So the change ships with the number that
decides it. `tieredLevelPlan.surfacesOverTransitionBodies` counts the surfaces
the old rule deleted and the band rule keeps, and it is **0 on all 200 seeds**:
single-layer generation never stacks over a stair body, so the defect is latent
in production and bites only the multi-layer rooms Phase D exists to generate.

A **span deck stays a whole-column exemption** rather than becoming a band case.
Its footprint IS its walkable surface, so the transition consumes nothing in
those columns — not even at its own level, which a band rule would eat, because
a flat span's band is exactly the deck's level.

Gate: `ops/dungeon-port-ab.sh` at density 0 — **identical geometry on all 200
seeds**, and a field-by-field diff of the two reports (**519 227 per-seed leaves**)
found **zero leaves changed in value**; the only differences anywhere are the one
added metric and the generation timestamp. Render Digest 12 seeds byte-identical
at `bdb90e5e8a696dd7`, and both fixtures re-run green — the two-layer episode
still walks whole at 94/94 port-graph nodes with 0 unreachable surfaces.

Three things measured that the design doc had wrong:

1. **§13's "`ChooseEnclosedRooms` moved into the plan so bridges may legally
   cross rooms" describes a gate that does not exist.** The aerial-bridge pass
   has no enclosure test at all — Decision 30 is an unconditional
   `room.Contains(cell)` → reject (`DungeonLabGenerator.cs:4636`). Enclosure is
   rolled in the renderer-input stage (`:3419`), long after bridges are placed
   (`:1942`). Moving it into the plan is therefore a **prerequisite** for
   relaxing Decision 30 — it is what would let the pass know whether the room it
   flies over has a roof — and not itself the relaxation.
2. **`LevelBand.SpanningEndpoints` is the wrong band for a stair body.** It pads
   by `MinHeadroomLevels` because it describes the CORRIDOR band a connection
   claims (§8.1). A body fills the plain endpoint span; the padded one would
   delete a surface 3u above the stair's top, which is exactly the gallery the
   fix exists to keep.
3. **The topology file's node syntax already admits §8.1's 6th element; its edge
   syntax does not admit the 4th.** `TryParseRouteTopologyNodes` rejects only
   `fields.Count < 5`, so a trailing options object parses today, while
   `TryParseRouteTopologyEdges` demands exactly 3. D1's parser work is on the
   edge side.

**D1 — the topology layer schema, carried not consumed (landed 2026-08-01).**

A node declares storeys as offsets from its own level; an edge end binds one by
name; the edge's rise then derives from the two BOUND elevations, which is §8.1's
"existing derivation, one term wider". Nothing consumes the binding yet — it
reaches `RouteIntent`, `RoomConnection` and the report, and stops.

- **`RouteTopologyLayer { layerId, relativeLevel }`**, sorted by id at parse time
  because a JSON object's property order is authored and this array reaches a
  hashed projection. `RouteTopologyEdge` gains `fromLayerId`/`toLayerId`;
  `RouteTraversalIntent` gains those plus `fromAbsoluteLevel`/`toAbsoluteLevel`;
  `RoomConnection` gains the two ids and an `IsLayerBound`.
- **The loader rejects six malformed shapes**, and does so at load rather than
  reporting them, because an unresolvable binding has no elevation at all: the
  edge would silently fall back to the node's base and the graph would generate
  something other than what it says.
- **`Validate Topologies` gained the two rules the loader cannot state** — a
  declared layer no edge binds, and a layer-declaring node with no recipe slot.
  Both are about the node's relationship to the rest of the graph. The second is
  the honest statement of today's capability: only a recipe's non-base storey or
  an aerial span's deck can build a stacked surface, so a generic room's layers
  would have no producer.
- **A 16-check loader self-check runs inside `Validate Topologies`.** The schema
  has no site in the corpus — that is what makes the phase neutral — so without
  it the first exercise of the parser would be the first topology to use it,
  which is the arrangement that cost C2 two rejected corpora.

Gate: `ops/dungeon-port-ab.sh` at density 0 — **identical geometry on all 200
seeds**, and a field-by-field diff of the two reports (**519 427 per-seed
leaves**) found **zero leaves changed, added or removed**. `resultHash` did not
move either (`5a970e5560e70423`, D0's value), so the only difference in the whole
file is the generation timestamp. `Validate Topologies` 8/8 PASS plus 16/16
layer-schema checks.

Two things measured that the design had wrong:

1. **§8.1's corridor-exclusivity relaxation cannot ship without a producer, and
   the design separates them.** It presents exclusivity as "the deeper block" and
   the band test as the fix. But a corridor's cells are leveled with
   `surfaces.TrySetFloorLevel(path[i], targetLevel, …)`
   (`DungeonLabGenerator.cs:2448`), which **rejects a conflicting value rather
   than stacking**. So two corridors sharing a plan cell at different elevations
   would pass the relaxed claim and then fail the tier attempt — the relaxation
   alone buys a rejection, not a second corridor surface. **D2 must carry the
   relaxation and the stacked-corridor producer in one slice**, and its
   capability needs a fixture, because no content will reach it until D5.

   The producer is smaller than it sounds, and that is the other half of the
   measurement: **the renderer already draws it.** `SurfaceColumns` skips only
   `SurfaceKind.Deck` and hands every other stacked surface to C1b's three
   passes — floor tile, per-surface rim guards, `_E_` soffit
   (`ElevationEdgeModel.cs:12356`). So the write site changes from
   `TrySetFloorLevel` to `AddSurface(cell, level, SurfaceKind.Ledge)` — the
   `Ledge` kind exists and has no producer — and the geometry follows. One
   caveat: `SurfaceColumns` throws on a stacked surface whose column has no
   floor, which is exactly the exemption a deck needed, so a layer-bound
   corridor crossing a true gap is a span, not a ledge.
2. **The `.Batch.cs` snapshot family is orphaned** — all 18 `Build…Snapshot(int
   seed)` builders, including the topology loader's own mutation probes, have
   exactly one occurrence each: their definition. They were a test surface that
   went away. The layer self-check therefore lives in `Validate Topologies`,
   which actually runs.

**D2 — the corridor relaxations and the producer they need (landed 2026-08-01).**

Two connections may share a plan cell, and a corridor may cross a room it does
not belong to, when a layer binding **authorizes** it and the disjoint absolute
bands **decide** it. The relaxation ships with its producer because D1 measured
the two as inseparable: the claim rule alone lets two corridors share a cell and
then `TrySetFloorLevel` rejects the conflicting value, so on its own it buys a
failed tier attempt rather than a second corridor surface.

- **`CorridorClaimLedger` replaces the bare claim set.** A
  `HashSet<Vector2Int>` can only answer "is anything here", which is exactly as
  expressive as the old rule needed. It is queried by key and never enumerated,
  so its order cannot reach `hashes.layout`.
- **`SurfaceField.AddCorridorSurface` is the producer** — one named writer
  beside C2's three, and the fourth thing the field can be asked to do.
- **`PathCrossesThirdRoom` and the topology validator's lattice-lane rule relax
  through ONE predicate**, `CorridorClearsRoomVertically`. §8.1 asks the two to
  relax "the same way and on the same absolute comparison", and a rule stated
  twice in two places is exactly what cost C2 a rejected corpus.
- **A base-only layer table declares no storey**, so `Validate Topologies` no
  longer demands a recipe slot for one (`RouteTopologyNode.DeclaresStoreys`).
  That is what makes the authorization reachable outside a slot node:
  `{ "floor": 0 }` names a node's own elevation so an edge can bind it, and the
  "a layer no edge binds" rule already exempted relative level 0 for the same
  reason.

Gate: `ops/dungeon-port-ab.sh` at density 0 — **identical geometry on all 200
seeds**, `resultHash` unmoved at D1's `5a970e5560e70423`, and a leaf-by-leaf
diff of the two reports (**519 427 per-seed leaves**) found **zero leaves
changed, added or removed**; the only difference in the whole file is the
generation timestamp. Render Digest 12 seeds byte-identical at
`bdb90e5e8a696dd7`. `Validate Topologies` 8/8 PASS plus **19/19** layer-schema
checks (17 from D1, plus the two lane-rule cases). Both older fixtures re-run
green.

**The capability, measured** — `Tools > Dungeon Lab > Print Stacked Corridor
Fixture`. Eleven claim/producer cases, then a rendered crossing: a 5x5 room at
L0, a layer-bound corridor crossing it at L4, and a return stair, so the whole
thing is one component. 5 suspended surfaces, 10 railed rims, **72/72
port-graph nodes connected**, headroom open under the catwalk, `_E_` soffit
present, renderer `REJECTED 0`.

Five things measured that the design or D1 had wrong:

1. **The producer's KIND cannot be a constant, and D1's finding said it was.**
   The write site is not `AddSurface(cell, level, SurfaceKind.Ledge)`: most of a
   layer-bound corridor's cells are its own, nothing else is there, and the
   corridor IS the ground — so a constant `Ledge` suspends a surface in a
   floorless column, which `SurfaceColumns` refuses. Measured on the fixture: of
   11 corridor cells crossing a 5x5 room, **6 stay ground-backed floors and 5
   suspend**. The kind is a property of where the corridor landed in the column.
2. **The producer has to be order-independent, and nothing says so.** Which of
   two crossing corridors resolves first is the order the topology author listed
   their edges in, and the geometry must not depend on it — so a corridor
   arriving BELOW a column's floor takes the floor and suspends what it
   displaced. The fixture builds the same crossing in both orders and compares
   the whole column, kinds included.
3. **The relaxation is upward-only, and that is a limit rather than caution.**
   Passing UNDER a room would have to suspend the room's own floor, and a room
   floor is ground-backed by construction: take its mass away and the boundary
   decomposition stops giving it walls. So the band must CLEAR the room's ground
   (declared elevation 0), not merely miss it.
4. **The band test IS the headroom test, for free.** `LevelBand.SpanningEndpoints`
   pads the top by `MinHeadroomLevels`, so two disjoint bands are separated by at
   least that much. The fixture's exact/one-short pair proves the half-open
   boundary is what decides it.
5. **§8.1's justification for the third-room relaxation is now verified, not
   assumed.** It licenses the relaxation on the grounds that a corridor passing
   over a room creates "no undeclared doorway and no unowned threshold".
   `BuildDoorwayEdges` and `BuildGatewayConnectionEnds` iterate a connection's
   OWN two endpoint rooms (`DungeonLabGenerator.cs:3773`, `:3802`), so a third
   room it flies over is never offered a doorway or a gateway end.

**A limit worth stating plainly.** `AddCorridorSurface` swallows a conflict that
`TrySetFloorLevel` would have rejected, and the claim ledger only knows PATH
cells — a stair landing another connection places off-path is not in it. So a
layer-bound corridor crossing such a landing would stack over it instead of
rejecting the attempt. It cannot happen in the corpus, because nothing is
layer-bound, and **no gate can see it**; D5's content is where it would first
bite.

**D3 — a bound edge resolves at its layer's elevation (landed 2026-08-01).**

Two halves that meet in the middle of a room: a corridor now resolves at the
elevation its edge BOUND rather than at its room's own level, and a slot now says
which storey of the recipe that elevation is.

- **`ConnectionEntryLevels` is the per-`(connection, end)` table**, built once per
  tier attempt beside `zoneLevels` and read by all three sites that used to index
  `zoneLevels` at a connection endpoint — the per-edge rise check in
  `TryAssignRoomLevels`, the corridor delta gate, and
  `TryResolveConnectionTransition`. An entry level is the zone level PLUS the
  bound layer's offset. Additive, not a replacement, which is what makes it
  output-neutral by construction: an unbound end has no offset and resolves at
  exactly the number the old code read.
- **A slot maps a topology layer id to a recipe layer id** —
  `"layers": { "gallery": "upper" }` — rejected at load when malformed, carried
  into `RecipeSlotIntent` and the report, and consumed by
  `TryValidateSlotLayerBindings` before a candidate can be admitted. Four codes:
  `LAYER_BINDING_UNDECLARED`, `LAYER_BINDING_LEVEL_MISMATCH`,
  `PORT_LAYER_UNMAPPED`, `PORT_LAYER_MISMATCH`.

Gate: `ops/dungeon-port-ab.sh` at density 0 — **identical geometry on all 200
seeds**, and a leaf-by-leaf diff of the two reports (**519 627 per-seed leaves**)
found **zero leaves changed in value and none removed**; the only addition is the
new metric, and the only two differing values in the whole file are the
generation timestamp and `resultHash`. `resultHash` moved once for the added
field, `5a970e5560e70423` → `10b28a82b65fc21f`. Render Digest 12 seeds
byte-identical at `bdb90e5e8a696dd7`. `Validate Topologies` 8/8 PASS plus
**26/26** layer-schema checks (19 from D1/D2, plus 7 slot-mapping cases). All
three older fixtures re-run green.

**No gate could see this one either, so it ships with its number.**
`tieredLevelPlan.layerOffsetConnectionEnds` counts the connection ends that
resolved above their room's own level, and it is **0 on all 200 seeds** — every
entry level in production is the value the pre-D3 code read at that site.

**The capability, measured** — `Tools > Dungeon Lab > Print Layer Entry Fixture`.
A three-node probe graph parsed by the real loader, one room per node, and the
same graph run with and without the binding. 30 checks, all green. The
load-bearing ones: the bound B-C edge is accepted where the unbound one is
rejected with `[ROUTE_ELEVATION_REQUIREMENT] edge 'B-C' resolved rise 4u instead
of 0u`; the bound end resolves at 8 and the unbound end at 4; and **one room is
met at two elevations** through the real `TryResolveConnectionTransition` — the
ground-side corridor cell at 4, the gallery-side corridor cell at 8, the room's
own floor untouched at 4.

Five things measured that the design or the earlier slices had wrong:

1. **D1's schema was not merely unconsumed — it was unusable.** The pre-D3 code
   did not resolve a layer-bound edge at the wrong elevation; it failed the tier
   attempt outright, because `TryAssignRoomLevels` compared a rise derived from
   the BOUND levels against one measured between the NODE levels. The fixture
   records that message verbatim. D3 is therefore the slice that makes the layer
   schema reachable at all, not a refinement of it.
2. **The entry level must be additive, and it can never be asked to compose.**
   `zoneLevels[node] + offset` is the only form that keeps the +1 raised-zone
   accent working, and a storey offset can never meet a raised zone: a node
   declaring a real storey must carry a recipe slot (D1's validator rule) and a
   recipe-slot room is excluded from zone splitting outright
   (`DungeonLabGenerator.RouteFirstPilot.cs:668`). A base-only layer table — D2's
   authorization — may sit on a split room, and its offset is 0.
3. **The agreement rule is what lets the recipe side stay unchanged.** Every
   elevation inside a room is derived from the RECIPE's layer offset
   (`PortLayerRelativeLevel`, `zone.layerRelativeLevel`) and every elevation on
   the route from the TOPOLOGY's. Proving the two equal per candidate means
   neither derivation has to override the other, so §8.2's port-level expression
   needed no edit — the mapping's real job is to make two vocabularies provably
   describe one elevation, not to convert between them.
4. **A socket recipe cannot be routed to on a storey, and that is a limit worth
   stating.** `IncidentCardinalSockets` binds entrances by DIRECTION, so nothing
   in the route can name which storey a socket is on; the rule therefore requires
   every socket on the base layer. That is the concrete shape of owner decision
   9's gap, and where a generic multi-layer room would relax it.
5. **`episode_layered_gallery_01` cannot yet be routed to on its gallery**, and
   this is a D5 prerequisite rather than a defect. Both of its ports are on its
   base layer — measured across the whole catalog, every port of every recipe is
   — so a slot may map its `upper` storey and the agreement rule passes, while
   binding an edge to that storey is rejected `PORT_LAYER_MISMATCH` until a port
   is authored there. The fixture asserts exactly that pair.

**D4 — volumes (landed 2026-08-01).**

Three systems, and the thread joining them is *a reservation with a height*.
Phase B shipped the `OpenVolume` prism kind, its penetration allow-list and its
enforcement with **no producer at all**, so until now not one line of that
mechanism had ever executed.

- **The `OpenVolume` producer.** A recipe zone of kind `OpenVolume` declares
  `openVolumeHeightLevels` and reserves `[its layer's elevation, + that height)`.
  Its floor is its LAYER's level, not the room's: an atrium's void is the air
  above the chamber, so it belongs to the storey it opens through.
- **`RoomFootprint.Overlaps` is volumetric**, and the split is D2's restated for
  rooms: **declared layers authorize an overlap, the absolute bands decide it.**
  Both halves are load-bearing — `atrium-ring` spans 0 to 24, so the band test
  alone would let pairs of its rooms start stacking.
- **`ChooseEnclosedRooms` moved into the plan**, before the elevation stage, so
  a later slice can let the aerial-bridge pass ask whether the room it flies
  over has a roof. Only the move; nothing consumes the answer earlier yet.

Gate: `ops/dungeon-port-ab.sh` at density 0 — **identical geometry on all 200
seeds**, and a leaf-by-leaf diff of the two reports (**520 627 per-seed leaves**)
found **zero leaves changed outside `schemaUsage`**, which moved because a row
was inserted into it. `resultHash` `10b28a82b65fc21f` → `286cbcdac24a2ba8`.
Render Digest 12 seeds byte-identical at `bdb90e5e8a696dd7`. `Validate
Topologies` 8/8 PASS plus 26/26 layer-schema checks. All four older fixtures
re-run green.

**Two numbers decide the slice**, both **0 on all 200 seeds**:
`tieredLevelPlan.openVolumeCells` (no recipe declares a void, so the producer,
the fill exclusion and the penetration rule are reachable and unexercised) and
`tieredLevelPlan.stackedRoomPairs` (no two rooms share a plan cell, which is
§4.1's stated failure mode for relaxing `Overlaps` on the band alone).

**The capability, measured** — `Tools > Dungeon Lab > Print Open Volume Fixture`.
23 checks, all green: a fill sweep blocked inside the band and free one level
below its floor and at its exclusive top; a foreign transition's footprint and
its *landing* blocked, and its structure below the floor allowed; the owning
recipe admitted and another recipe refused; the two `RECIPE_OPEN_VOLUME_HEIGHT`
slips rejected; and the overlap rule's exact/one-short-of-headroom pair.

Six things measured that the design had wrong or left open:

1. **§4.1's authorization clause is not evaluable at the site it names.** It
   says two rooms may share a column when "at least one of them declares the
   shared column as part of a reserved open volume or a bridge span". But
   `OverlapsPlacedRoom` runs inside room inflation — three passes before
   `TryPlaceRouteRecipes` resolves any zone, and a whole stage before
   `AddAerialBridges` exists. Neither declaration has been made yet. What IS
   known there is the topology's declared storeys, which is the same
   pre-elevation absolute every other layered rule compares, so that is what
   authorizes the relaxation.
2. **The producer needs TWO sites, because there are two ledgers.** §6 makes
   "the density fill passes must query the prism ledger" an invariant and §11
   calls missing it the most likely first-implementation failure — but the
   invariant alone is not enough. Phase B finding 2 established that the annex
   sweep runs during LAYOUT and the transition ledger is built during
   ELEVATION, and they cannot share an instance. A volume registered only where
   the recipe realizes would already have been packed solid by the sweep that
   ran a stage earlier. So it registers twice: from the topology's declared node
   level at the layout site, and from the resolved base level at the elevation
   site. The same number by construction, derived two ways because the two
   stages know different things.
3. **§6's authored per-feature allow-list needs per-feature owners, and they do
   not exist.** It names entries like `Transition:atrium-stair-a`, but every
   prism a recipe registers — footprints, landings, room cells, transitions —
   goes in under one `Recipe:<id>` owner. An authored list could therefore only
   ever spell that single owner, which is the blanket exemption §6 exists to
   forbid, wearing a checklist's clothes. The allow-list is derived instead
   (the owning recipe, and nothing else), which admits exactly the same set and
   does not claim otherwise. `OPEN_VOLUME_PENETRATION_UNDECLARED` is *not*
   implemented: it has nothing to validate until owners get finer.
4. **The recipe content digest omitted the new field, and `zone.kind` alone was
   not enough.** Two recipes whose atria differ by four levels of air would have
   digested identically, and `catalogDigest` is how the generator notices a
   catalog changed. Closed with C2a's conditional append, so today's recipes
   hash unchanged.
5. **The enclosure STREAM had to move with the roll.** Its draw sequence is N
   room draws followed by the boundary context's own `Next()` for the dressing
   seed, on one instance — so hoisting the roll while leaving the stream behind
   would re-phase that seed and move every dressed dungeon. The array is also
   COPIED into the boundary context, because the passes there mutate it
   (subdivision resizes it, the sealed-room passes demote entries) and the
   plan's copy must stay the decision that was made.
6. **Inserting a row into `schemaUsage` shifts every row after it.** That is 30
   changed leaves in the report diff which are positional, not semantic — worth
   expecting rather than investigating. The same run is also direct evidence
   that `schemaUsage` reaches `resultHash` and *not* `hashes.canonical`: it
   changed on all 200 seeds while the canonical vector did not move.

**A tooling note, because this ritual is now six slices old.**
`ops/dungeon-report-diff.py` does the leaf-by-leaf report comparison by hand up
to now: it flattens both reports to dotted leaf paths and prints every path that
changed, was added or was removed, with counts and examples. It answers the
question `ops/dungeon-port-ab.sh` deliberately does not — that one compares a
per-seed geometry vector, which says nothing about the fields outside it.
**Do not edit the working tree while `dungeon-port-ab.sh` runs**: it stashes the
tree for the pre-port leg and pops it back, so a mid-run edit lands on the wrong
side of the stash.

The generator makes rooms at different elevations but behaves like a single
surface: one plan coordinate, one floor. The direction is multiple independently
traversable surfaces that may overlap in plan — pits that drop to a lower route,
bridges over playable geometry, and rooms owning several layers — with the
identity of a walkable place moving from `cell` to `(cell, level)`.

**This absorbs the tier-void ratio**, which used to be listed here as its own
slice. The complaint — 4–8u of rise across 36u+ lane gaps — is that the vertical
axis is trivial next to the horizontal. Stacking traversable surfaces is the
direct answer, and it is the only thing that adds play space without growing the
52x52 envelope. It is no longer a competing item.

**A correction worth recording, because the old page said the opposite.** This
page previously claimed that "bridges, balconies and overlooks are mostly a
matter of *authoring* those edge kinds in new graphs rather than building new
systems", on the grounds that `RouteTransitionKind` already has a `Bridge`
member. That is wrong, and it misled at least one design pass. `Bridge` is
vocabulary, not capability: bridges are capped at two per dungeon, are rejected
over room interiors, and are not walkable surfaces at all — they are transition
edges carrying a set piece. Balconies need a canonical model change. The
investigation is in the design doc's §1 and §2.

### Next, in order

1. ~~**Decide the three sizing questions**~~ **Two of three decided 2026-07-31
   (owner, both subject to change): vertical envelope 40u, stacking pitch 4u.**
   Neither binds any code today — there is no vertical envelope constant, and a
   dungeon's extent emerges from its topology's authored deltas. 4u is exactly
   `MajorRiseLevels`, and clears `MinHeadroomLevels = 3` with the `_E_` slab's
   0.5u underside, so nothing needs retuning; C1's episode already used it.
   **Still open: the envelope MECHANISM** — a per-topology `ceiling` (existing
   dungeons unchanged, new ones opt in) versus raising a global constant. That
   is the one with work attached. Note the corpus already spans 24–25 levels at
   density 0, so 40u adds ~15u of global range — and per design §14 that is not
   the stacking budget, which is local headroom, not envelope.
2. ~~**Measure the deck-underside art.**~~ **Done 2026-07-29 — see the design
   doc §0.1.** The kit already ships solid floor tiles: the `_E_` family is the
   `_O_` family plus a bottom face, 0.5u thick, hanging below the walk surface.
   The generator pins `FloorName` to the one-sided `_O_` plane, which is why
   deck undersides read open from below. Fix is a per-surface prefab choice, not
   new art. Phase C's only external dependency is retired.
3. **Look at the dungeons.** **Arena > Dungeons > Rebuild Random Dungeon** on a
   few seeds. No hash tells you whether a dungeon reads well. One rendered shot
   per topology is in `DungeonLabReports/step3_topology_shots/`.
4. **Teach `Validate Topologies` the slot-geometry rule.** It is the one
   authoring rule with real teeth that nothing checks, and it cost two of four
   topology drafts a redraw. See "Slot geometry" in
   [`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md).
5. **`atrium-ring` fails 16 of its 19 spacious seeds for density** —
   pre-existing; the one-line fix and why it was not folded in are in the
   archived route-topology log.
6. **Doors thin out sharply as density rises** — 12 gateways at density 0, 2 at
   density 5, while doorways double. The gateway rules want a real wall on both
   flanks and leave chamfer-framed entrances bare on purpose. Owner call on
   whether a packed keep should have more doors; a look question, not a defect.
7. **Remaining architecture-review items**, none urgent: unify the two floor
   representations (§12, 2.5), move the headroom gate after the late passes and
   delete the duplicated deck formula (2.4), carry `RouteIntent` into the plan to
   remove the `lastRouteIntent` static (2.7), and take the display strings out of
   `TieredLevelPlan` (2.9). The typed test API (2.6) is blocked on an asmdef
   migration of `Assets/Arena/Editor` as a whole — see the review's H4 note.
   *(2.5 and 2.7 stop being optional under the layered direction — the design
   doc makes both prerequisites.)*

## Read next

1. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules. Read before changing generator, measurement, contract, or placement code.
2. [`GLOSSARY.md`](GLOSSARY.md) — authoritative vocabulary (role vs. beat, room vs. recipe, zone, port, transition, reservation).
3. [`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md) — current system model and recommended work.
4. [`layered-topology-design-2026-07-29.md`](layered-topology-design-2026-07-29.md) — the proposed next direction (draft).
5. [`ROOM_AUTHORING_GUIDE_CURRENT.md`](ROOM_AUTHORING_GUIDE_CURRENT.md), [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) and [`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md) — adding content.
6. [`stair_forge_design.md`](stair_forge_design.md) — vertical traversal decision history.

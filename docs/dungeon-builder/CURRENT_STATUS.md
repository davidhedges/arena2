# Dungeon generator: current status

Last updated: 2026-07-29

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

- Seven route topologies, one drawn per seed by weight: processional spine, atrium ring, twin-wing keep, cataract shaft (descending), sunken basin, terraced cascade, ridge and ravine. Each is one JSON file under `Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies/`; adding one costs no C#.
- Three required recipe slots (`required-compression`, `required-landmark`, `required-return`) filled from an explicit catalog; four enabled recipes currently.
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
- **One plan coordinate carries one walkable surface.** The canonical elevation
  model is a heightfield (`Dictionary<Vector2Int,int>`).
- **Bridges are capped at 2 per dungeon and may not cross room interiors.**

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

### In progress: layered 3-D topology — Phases A and B landed

Design of record, **still a draft beyond Phase B**:
[`layered-topology-design-2026-07-29.md`](layered-topology-design-2026-07-29.md).
Branch `dungeon/layered-topology`.

**Phase A is complete (2026-07-31).** The plan can express surface identity;
nothing yet produces a second surface.

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

**Phase C is in progress** — the two-layer authored episode, the first real
proof (design §13). C1a landed 2026-07-31.

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

Two Phase C evidence legs cannot be run headlessly: the **live probe** needs a
running SpacetimeDB plus `ops/republish-local-clear.sh`, and leg 4 is an **owner
eyeball** — no hash tells you whether a two-layer room reads well.

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

1. **Decide the three sizing questions** in the design doc §14: the vertical
   envelope (40u vs 80u), per-topology ceiling vs a global constant, and the
   stacking pitch (4u vs 8u). They size everything downstream.
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

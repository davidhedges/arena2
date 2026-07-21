# Coherent floorplan generation plan

Status: Phases 0-5 and Phase 6a/6b/6c/6d/6e/6f/6g complete and verified

Last updated: 2026-07-21

This is the execution plan for evolving Dungeon Lab from a room-placement generator into a route-first, recipe-assisted dungeon planner. It is deliberately incremental: the existing renderer, stair forge, bridge work, verticality, distant views, and abyss construction remain useful assets rather than being replaced by a second generator.

For current progress, start with [`CURRENT_STATUS.md`](CURRENT_STATUS.md). For the workflow that becomes available after the foundation is complete, see [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md).

## 1. Outcome

The generator should produce floors that read as designed places:

- a player can understand a main journey, optional branches, loops, and a destination;
- major rooms have a purpose in that journey rather than merely occupying available space;
- entrances, thresholds, stairs, landings, focal objects, balconies, bridges, and exits form one composition;
- overlooks and promontories reveal meaningful places, including future or previously visited route beats;
- a small reviewed library of room and multi-room recipes supplies authored structure without turning the entire dungeon into prefab assembly;
- generic generation still supplies connective tissue, dimensions, dressing, and enough variation to prevent the library from feeling finite;
- a rejected plan fails before rendering, with an intelligible reason.

The objective is not to imitate the Fantastic Dungeon demo scene tile for tile. The demo is a quality reference for composed relationships: doorway-to-room transitions, coupled stairwells, landings, focal axes, galleries, and controlled sightlines.

## 2. Locked decisions

These are constraints, not matters for later optimization.

1. **Everything rises from the abyss.** Every exposed floor edge receives the required vertical support down to the shared abyss datum. A high vertical-piece count is expected and acceptable.
2. **The grid and measured contracts remain exact.** The 4-unit plan grid, explicit dimensions, traversal contracts, headroom rules, and prefab contracts remain authoritative. Names, renderer bounds, and screenshots are not semantic data.
3. **The renderer does not repair plans.** Planning and validation own connectivity, clearance, landing space, transitions, and overlap. Rendering either consumes valid canonical plan data or rejects it.
4. **Structural transitions are planned before surrounding fill.** Required stairs, paired stairwells, bridges, and their landings are anchors—not decorations fitted into leftover cells.
5. **Traversal and visibility are different graphs.** A visible room need not be adjacent or currently reachable. An overlook is successful only when it reveals an intentional target.
6. **Authored compositions stay whole.** A recipe may reference a composed prefab or motif, but its exact footprint, ports, protected space, and elevation behavior are declared explicitly.
7. **Determinism is end to end.** The same seed, generation profile, planner version, and reviewed catalog digest must produce the same route intent and canonical downstream plans.
8. **Editor-time generation remains the production path.** Runtime generation is outside this plan until client and authoritative server collision can share the same generated result.
9. **There is one downstream generation pipeline.** During migration, one short-lived comparison switch may choose the old or new layout builder at a single boundary. Both feed the existing `DungeonLayout`, `TieredLevelPlan`, validator, renderer, and collision export. The switch and old builder are deleted at cutover; `Legacy` is not a permanent product mode.
10. **Recipes are reviewed content.** Draft or stale recipes cannot silently enter normal generation.
11. **Abstractions are earned by a working slice.** No adapter, DTO, schema field, authoring feature, or extension point is added solely for anticipated future use.

[`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) remains the authority for project-wide geometry and placement rules. [`stair_forge_design.md`](stair_forge_design.md) remains the detailed decision history for vertical traversal.

## 3. What exists today

The current generator already has important pieces worth preserving:

- randomized room footprints, including rectangular and winged forms;
- a connected rooted layout;
- route-aware elevation assignment and multiple elevation archetypes;
- stairs selected from measured contracts, forged stairs, synthesized transitions, bridges, daises, showpieces, overlooks, and promontories;
- deterministic seed entry points and a synchronized baked playtest destination;
- a renderer that builds the required abyss supports.

However, the current spatial order is approximately:

1. place room bands and a hall;
2. connect each new room to a nearby existing room with a corridor path;
3. derive rooted connectivity and route information;
4. assign an elevation archetype and levels;
5. add loops and fit vertical features.

That is **room-first with route-aware later stages**, not strict route-first generation. It creates many different footprints, and it has named elevation archetypes, but it does not currently expose a catalog of named semantic macro-topologies such as a processional plan, an atrium ring, or a twin-wing keep. Those are the missing “overall floorplan variants” addressed below.

Active step-formation placement is also currently parked. That means the late pass that scans completed rooms and drops a `wall_abutting` or `interior` step formation is skipped. It does **not** disable route transitions, stair forging, synthesized stairs, bridges, seam steps, daises, showpieces, or promontories. The parked pass should not simply be switched on: useful step formations should become declared recipe motifs with reservations and compatibility checks.

The current implementation already exposes the migration seam:

```text
BuildRandomDungeonLayoutData
        -> DungeonLayout
        -> TryBuildTieredLevelPlan
        -> TieredLevelPlan
        -> TryBuildRoomBoundaryContext / ElevationEdgeModel
        -> collision export
```

`DungeonLayout` and `TieredLevelPlan` are the canonical downstream representations. They are currently private nested types and may be moved mechanically into cohesive files when a real consumer requires it, but they are not replaced by a parallel family of intent/placed/compiled DTOs.

## 4. Target planning pipeline

Route-first has a precise meaning here: the semantic route nodes and typed connections exist before room rectangles receive world positions. Spatial placement is responsible for realizing an already meaningful plan, not discovering meaning after random placement.

```text
Generation profile + reviewed catalog + seed
                     |
                     v
       Minimal ephemeral RouteIntent
  (route, branches, loop, beats, typed edges,
       elevation and vista requirements)
                     |
                     v
 Place structural anchors, recipes, rooms, and corridors
                     |
                     v
          existing DungeonLayout
                     |
                     v
 existing tier/elevation/transition planning, extended only
     where it must consume explicit route requirements
                     |
                     v
          existing TieredLevelPlan
                     |
                     v
 existing validation -> renderer -> abyss -> collision export
```

Randomness still matters, but it chooses among valid intentions and variations. It does not define architecture by a sequence of unrelated feature rolls.

## 5. Planning vocabulary

Use these terms consistently in code, reports, and review:

| Term | Meaning |
| --- | --- |
| **Macro-topology pattern** | A semantic graph template defining a main route, branch opportunities, loops, landmark slots, and vista opportunities. It has no final world coordinates. |
| **Beat** | A position in the player journey, such as arrival, compression, reveal, choice, ascent, respite, landmark, or culmination. |
| **Room recipe** | An authored spatial contract for one room or tightly coupled room episode. It declares geometry rules, ports, elevation zones, protected areas, motifs, and allowed variation. |
| **Episode** | A composition whose meaning spans more than one feature or chamber. A throne hall with a focal dais, twin stairs, side galleries, and controlled thresholds is an episode. |
| **Motif** | A reusable subordinate composition—paired stairs, a dais, a bridge landing, a gallery edge, or a step formation—with an explicit contract. |
| **Traversal edge** | A typed connection the player can use: doorway gap, open arch, corridor, stair, stairwell, bridge, or open gallery. |
| **Vista edge** | A planned line of sight between two beats. It does not imply traversal or adjacency. |
| **Route intent** | A minimal, ephemeral semantic graph containing only route, role, elevation, recipe, and vista requirements consumed by the current generation attempt. It is not a second renderer-facing plan. |
| **DungeonLayout** | The existing canonical 2D spatial result: floor cells, room footprints, connections, and room zones, with only the additional intent metadata that an existing downstream consumer actually needs. |
| **TieredLevelPlan** | The existing canonical resolved elevation/transition result consumed by boundary construction and rendering. |
| **Plan report** | A serializable diagnostic projection of an attempt. It is evidence and replay metadata, not another mutable planning model. |

## 6. Data contracts and ownership

### 6.1 Minimal route intent

Introduce one pure-data route model. `RouteIntent` is a suggested descriptive name, not a required public API. It exists only upstream of `DungeonLayout`; the diagnostic report may serialize a projection of it without turning that projection into another model used by generation.

The route intent owns:

- seed, planner version, macro-pattern ID, and stable node/edge IDs;
- the ordered main route;
- branch and loop membership;
- separate traversal and vista edges;
- required bottom and top beats;
- requested elevation story and global enclosure policy;
- no cell-level rendering data.

Each intent node owns:

- semantic role and journey beat;
- required and allowed traversal degree;
- recipe eligibility and generic fallback policy;
- elevation zone or relative elevation constraints;
- enclosure and visibility intent;
- focal direction, symmetry needs, and protected-space requirements.

Each traversal edge owns:

- exact connection type;
- width/lane count;
- relative rise and direction when known;
- landing and headroom requirements;
- required/optional status;
- compatible port types.

Do not add a field until the route-first builder or an existing downstream stage consumes it in the same change.

### 6.2 Macro-topology definitions

Use explicit serialized definitions selected by profile weights. A definition supplies graph rules and semantic slots, not prefabricated coordinates.

Patterns must compose a shared vocabulary of graph operations—spine, branch, rejoin, hub, cross-link, long return—rather than each receiving a separate hand-coded graph generator. A pattern is a constrained composition of those operations.

The first vertical slice includes only one pattern:

- **Processional spine:** strong entrance-to-landmark progression with one rejoining branch;

After that slice passes its go/no-go review, add two deliberately distinct patterns one at a time:

- **Atrium ring:** a visible central volume, ring or partial-ring circulation, and cross-atrium vistas;
- **Twin-wing keep:** a shared arrival, two differentiated wings, and a coupled culmination or return loop.

Names can change, but the patterns must differ in graph structure—not merely in height assignment or room dimensions.

### 6.3 Recipe definitions

The eventual source of truth should be a `ScriptableObject` recipe asset because it can hold explicit asset references and support a focused Unity authoring UI. Its validation and planning data must remain serializable and usable without `UnityEditor` dependencies.

Do not implement the full schema or UI first. Phase 4 expresses one throne-hall episode with the smallest explicit contract needed to place and validate it. The general schema below is hardened only after that working slice reveals which fields have real consumers.

A recipe contract includes:

- stable ID, schema version, content version, lifecycle state, and review digest;
- eligible semantic roles, beats, and traversal degrees;
- mandatory and optional typed ports;
- stair ports with rise, width, topology, headroom, and landing reservations;
- internal elevation zones and walkable/void/reserved cells;
- focal axis, protected areas, and symmetry constraints;
- enclosure and boundary-expression policy;
- vista sockets and their target categories;
- compatible motifs and explicit prefab references;
- legal rotations, mirrors, dimensional ranges, and weighted alternatives;
- incompatibilities and generic-fill permissions.

Recipes never infer these fields from prefab names or bounds.

### 6.4 Placement and compilation

The deterministic route-first builder owns:

- selecting compatible recipes for intent nodes;
- resolving rotations, mirrors, dimension ranges, and ports;
- placing transition anchors and landing reservations first;
- embedding the remaining graph without overlap;
- bounded, traceable backtracking;
- filling eligible generic nodes and corridor runs;
- producing the existing `DungeonLayout` directly or an explicit rejection.

There is no general placed-plan or compiled-plan layer. When explicit route elevation or recipe metadata must survive into tier planning, extend the `DungeonLayout` handoff—or pass a narrowly scoped companion value—only in the change that adds its consumer. Existing `TryBuildTieredLevelPlan`, stair selection/forge, transition validation, boundary construction, renderer, abyss support, and collision export remain the single implementations.

#### Phase 1 embedding algorithm

The first pilot uses a skeleton-first algorithm rather than backtracking over fully dimensioned rooms:

1. build the abstract processional route, branch, and rejoining loop;
2. embed the ordered main route as a self-avoiding orthogonal walk on a coarse lattice;
3. reserve an envelope at each route node for later room inflation and enough separation for corridors;
4. attach branch and rejoin paths with deterministic bounded grid search;
5. pin connection approach directions at each envelope;
6. inflate flexible room footprints inside their envelopes;
7. connect boundary anchors and compile directly into `DungeonLayout`;
8. reject the attempt with a stable reason when any bounded search is exhausted.

This pilot does not solve exact recipe ports. It must, however, represent reserved void, facing relationships, and an unobstructed sight volume between a vista source and target so later sightline constraints do not require replacing the embedding algorithm. Phase 3 proves one such vista; Phase 6 generalizes selection and scoring.

### 6.5 Clear subsystem boundaries

| Subsystem | Owns | Must not own |
| --- | --- | --- |
| Macro planner | route, branches, loops, beats, roles, vista intent | cell coordinates or prefab placement |
| Elevation policy | the planned height story and required vertical edges | opportunistic post-layout stair decoration |
| Recipe selector | semantic and port compatibility | repairing incompatible geometry |
| Spatial solver | positions, reservations, port alignment, bounded backtracking | changing the meaning of the route to make placement easy |
| Layout handoff | direct production of the existing `DungeonLayout` and narrowly consumed metadata | creating a parallel renderer-facing representation or adapter hierarchy |
| Validator | proof and actionable rejection reasons | mutation or repair |
| Renderer | instantiate an already valid plan and abyss support | architectural decisions |
| Decorator | non-structural dressing inside declared permissions | blocking routes, ports, focal axes, or vistas |

## 7. Anti-drift migration rules

These rules are exit-gate requirements, not cleanup aspirations:

1. **One fork, at one boundary.** The temporary comparison choice exists only where `BuildRandomDungeonLayoutData` is selected. No downstream `legacy/coherent` branches are allowed.
2. **One canonical downstream model.** Both temporary layout builders produce `DungeonLayout`; all accepted plans continue through `TieredLevelPlan` and the same renderer/collision path.
3. **No legacy adapter.** The existing layout is not wrapped in a new compiled-plan DTO merely to appear architecturally uniform.
4. **No silent fallback.** If the route-first builder rejects a seed, it reports the reason. It does not invoke the old builder and disguise the failure.
5. **No duplicated invariants.** Connectivity, headroom, transition, boundary, abyss, and collision rules each have one authoritative validator/implementation.
6. **No speculative surface area.** Every new type and field has a production producer, production consumer, and test in the same milestone.
7. **Reuse existing vertical services.** Recipes and route intent request transitions through the existing stair contracts, forge/synthesis, bridges, and `ElevationEdgeModel`; they do not get parallel implementations.
8. **Delete as part of migration.** Every phase maintains a deletion ledger naming superseded methods, flags, settings, and late feature rolls. A phase is incomplete while items scheduled for that phase remain reachable.
9. **Time-box the comparison switch.** Its removal phase and exact deletion targets are recorded when it is introduced. The final production mode is not called `Coherent`; it is simply the dungeon generator.
10. **Avoid pre-emptive cleanup.** Do not begin by redesigning the roughly 27,000 lines across the generator, elevation model, and stair forge. Extract or move code only to support the next working slice, with characterization tests around the move.

The phase ledger uses this template:

| Phase | New production symbols | Existing symbols reused | Superseded symbols deleted now | Temporary symbols and removal phase |
| --- | --- | --- | --- | --- |
| N | — | — | — | — |

## 8. Determinism and diagnostics

A seed alone is not enough if unrelated changes perturb every later random choice. Use stable random streams derived from `(seed, phase, stable node/edge ID, purpose)` for topology, recipes, placement alternatives, and dressing.

Every attempt should produce a machine-readable plan report containing:

- seed, profile, planner/schema versions, and catalog digest;
- chosen pattern and ordered route beats;
- selected recipe and motif IDs;
- resolved ports, elevations, rotations, and mirrors;
- traversal and vista graphs;
- backtracking decisions and attempt count;
- validation results and rejection reason codes;
- a stable hash of the route intent, `DungeonLayout`, and `TieredLevelPlan` summaries.

Reports make failed seeds reproducible and prevent a screenshot from becoming the only evidence of what the planner meant to build.

## 9. Phased implementation

Do these phases in order. Each phase ends in a usable generator and closes its scheduled deletion ledger. A later phase may be sketched, but production infrastructure is not built ahead of the next working slice.

### Phase 0 — Baseline and characterization

Goal: make the current generator observable without changing its output or introducing a new plan model.

Deliverables:

- one documented deterministic 200-seed range for automated baseline/reliability measurements, using the existing batch scale, with only six seeds lightly annotated as visual sentinels covering representative good, weak, and edge-case layouts;
- a batch-readable summary and stable hash of the existing `DungeonLayout`, `TieredLevelPlan`, validation results, and rejection reasons;
- editor tests for determinism and existing connectivity, vertical traversal, headroom, boundary, and collision preconditions;
- a repeatable capture process for the six visual sentinels; no hand-curated 200-seed gallery or aesthetic taxonomy;
- focused baseline measurements over an automated deterministic sample large enough for a meaningful p95 attempt count: completion rate, layout-attempt distribution, validity, route/branch/loop counts, elevation range, transitions, and visible-distant-room count;
- characterization of the exact `BuildRandomDungeonLayoutData -> DungeonLayout -> TryBuildTieredLevelPlan -> TieredLevelPlan -> renderer` call chain;
- the first deletion-ledger report, even though this phase should delete no production behavior.
- a numeric Phase 1 reliability budget locked before pilot results are seen: the floor cannot be below 95 successful seeds out of the first 100 seeds in the fixed range, and its attempt ceiling and p95 attempt target are derived from the current baseline.

Exit gate:

- the seed corpus rebuilds repeatably;
- the same seed produces the same canonical summaries;
- hard failures have stable reason categories;
- the Phase 1 completion floor, attempt ceiling, and p95 target are recorded in `CURRENT_STATUS.md`;
- no new intent/placed/compiled DTOs or adapters exist;
- no intentional generation behavior has changed.

### Phase 1 — One route-first 2D pilot

Goal: prove the new planning idea at the smallest useful seam while leaving all vertical and rendering behavior downstream unchanged.

Deliverables:

- one minimal `RouteIntent` implementation;
- one parameterized processional-spine topology with a main route, one purposeful branch, one rejoining loop, and an intentional culmination;
- deterministic spatial placement that compiles directly into the existing `DungeonLayout`;
- generic room shapes and corridors realized from route anchors rather than nearest-room center routing;
- the skeleton-first algorithm in section 6.4, including reason-coded failure at each bounded search stage;
- representation of vista source/target facing, reserved void, and an unobstructed candidate sight volume, without full line-of-sight scoring;
- a temporary comparison selector located only at layout-builder selection;
- graph and layout reports for direct comparison against Phase 0 seeds.

Not in this phase:

- recipe assets or catalogs;
- new stair, elevation, boundary, render, or collision systems;
- atrium-ring or twin-wing topology;
- a general socket-solver framework.

Exit gate:

- the route graph exists before room coordinates;
- the existing `TryBuildTieredLevelPlan`, validators, renderer, abyss support, and collision export consume the pilot layout without coherent-specific branches;
- at least 95 of the 100 fixed pilot seeds complete within the Phase 0 attempt ceiling, the p95 attempt target passes, and every failure has an explicit route-builder reason;
- every accepted layout remains hard-valid and the six-sentinel comparison shows no serious regression in usability, spatial variety, verticality, or distant-room visibility;
- the temporary selector records Phase 2 as its removal phase;
- if embedding reliability fails or the comparison shows a serious regression, stop and revise the algorithm instead of building infrastructure around it. Visible coherence improvement is evaluated after vertical intent in Phase 3, not manufactured as a Phase 1 pass condition.

### Phase 2 — Early 2D cutover and deletion

Goal: remove the first parallel path before deeper work begins.

Deliverables:

- make the route-first builder the sole layout builder;
- remove the comparison selector;
- delete `BuildRandomDungeonLayoutData`, `PlaceRoomBand`, nearest-room center-routing code, and settings used only by that path, subject to reference proof;
- retain reusable room-shape and corridor primitives that the route-first builder actually calls;
- update all current batch/menu entry points to use the single builder;
- prove there are no hidden legacy fallbacks.

Exit gate:

- one reachable layout-building path exists in production code;
- repository search and tests confirm the selector and superseded symbols are gone;
- accepted Phase 1 seeds retain canonical downstream validity;
- the deletion ledger is empty for Phase 2;
- the generator is not labeled `Legacy` or `Coherent`; it is simply the active generator.

### Phase 3 — Vertical intent through the existing tier pipeline

Goal: plan the elevation story and structural transitions earlier without creating a second elevation system.

Deliverables:

- route beats that declare relative elevation and typed transition requirements;
- structural stair, stairwell, bridge, headroom, and landing reservations before surrounding generic fill is final;
- the smallest necessary additions to the existing `DungeonLayout` handoff or a narrowly scoped companion value;
- consumption of those requirements by the existing tier/elevation planner, stair contracts, forge/synthesis, transition validators, and `ElevationEdgeModel`;
- elevation archetypes retained as policies constrained by route intent, not reimplemented algorithms;
- one required vista between named route beats, using explicit source/target facing and reserved intervening space;
- bottom-to-top route proof and reason-coded failures.

Exit gate:

- no required structural transition is chosen by an unrelated late feature roll;
- no duplicate stair or elevation implementation exists;
- additions to the handoff each have an active producer, consumer, and test;
- at least 200 fixed seeds meet the reliability budget established in Phase 0;
- the planned vista is either realized or rejects before rendering, without post-hoc carving;
- the curated review shows a material improvement in route and vertical-story readability over the Phase 0 sentinels;
- any old late logic displaced by explicit route requirements is deleted in this phase.

### Phase 4 — One throne-hall episode as a schema probe

Goal: prove authored composition with one real vertical slice before building a recipe platform.

Deliverables:

- one processional landmark slot in the route intent;
- one minimally represented throne-hall episode containing a focal axis, protected dais/focal zone, coupled twin stairs, both landing sets, side galleries, typed thresholds, and explicit allowed variation;
- deterministic placement into the active builder and direct production of canonical layout/tier data;
- use of existing stair contracts, rendering, abyss support, dressing permissions, and collision export;
- isolated and full-dungeon diagnostic views;
- a schema-usage report listing which candidate recipe fields were actually consumed.

The probe may use a deliberately small draft `ScriptableObject` or explicit internal data. It is not yet a general reviewed catalog and does not require a custom authoring window.

Exit gate:

- the entire episode places or rejects atomically; one stair or gallery cannot survive without its coupled partners;
- generic generation connects through declared ports without center-routing repair;
- the episode passes isolated and full-dungeon seed tests;
- the episode is visibly stronger than an equivalent collection of independent late feature rolls;
- unused speculative fields and helpers are deleted before proceeding;
- a go/no-go review approves generalizing the proven contract.

### Phase 5 — Harden the recipe contract and authoring workflow

Goal: generalize only what the working episode demonstrates and make it maintainable after time away.

Deliverables:

- the versioned recipe/motif schema, with every field backed by a current consumer;
- a deliberately different contrast recipe, such as a stair tower or flexible vestibule, to expose throne-hall-specific overfitting;
- lifecycle states: `Draft`, `Reviewed`, `Deprecated`; validation is a current result, not a stored lifecycle state;
- a catalog that admits only reviewed assets with matching validation digests;
- non-mutating schema, structure, variation, neighbor, and full-dungeon validators;
- only the authoring overlays and preview tools needed to execute [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md);
- deterministic preview-gallery generation.

Exit gate:

- both structurally different recipes use the same explicit contract without special-case fields named for either recipe;
- changing a reviewed recipe makes its review stale;
- invalid ports, stairs, landings, protected zones, symmetry groups, and overlaps cannot be promoted;
- the companion authoring workflow's required outcomes can be completed end to end; its illustrative menu names may differ;
- no unused editor extension point or future recipe kind remains in the implementation.

Result: complete and verified on 2026-07-21. Two reviewed recipes now share one versioned contract, placement seam, five-layer validation path, lifecycle/catalog admission rule, deterministic gallery/review workflow, and the existing canonical renderer/abyss/collision consumers. The locked corpus passed twice at 200/200 with identical result hash `765ead1a87f95732fb66dfa617b33e91d1ea921cb91f0287226309d17af46155`; all six sentinels reported `REJECTED 0`; Phase 0/1/3/4/5 fixtures passed 33/33. The throne-only production boundary and unused compatibility scaffolding were deleted, and the Phase 5 deletion ledger is empty.

### Phase 6 — Expand topologies, recipes, and planned vistas

Goal: add breadth in small increments after both planning and authoring foundations have been proven.

Progress: Phase 6a completed the identity-preserving spine/branch/rejoin composition foundation on 2026-07-21. Phase 6b completed `atrium_ring_topology_01`, and Phase 6c completed `twin_wing_topology_01`: three structurally distinct graphs/embeddings now share the existing recipes, vista contract, canonical pipeline, renderer, abyss support, and collision export. Phase 6d then completed the behavior-preserving `route_rhythm_policy_01` slice: existing ordered roles, beats, and recipe bindings reject repetition and crowding before embedding. Phase 6e completed `named_vista_promontory_01`: the inert generic random-room pass and settings are gone, and each accepted promontory is now a canonical target-bearing resolution carved only from surplus source-side cells on the existing resolved vista line. Phase 6f completed `connector_corner_return_01`: the common perpendicular `connector` / `return` node at index 12 now adds a third reviewed recipe through the existing schema, workflow, canonical plan, renderer, abyss, and collision path, with one shared named-exit orientation rule. Phase 6g completed `connector_twin_gallery_01`: the exact processional node-10 producer now carries a reviewed 7x7 clear-lane connector with two mirrored rise-1 galleries, while the turning atrium/twin connectors remain generic. Its repeat-identical final corpus is 200/200 on attempt 1 with 700 recipe resolutions, exactly 100 twin galleries, 114 named promontories, exact Phase 6f route/vista/promontory/prior-recipe preservation, and all six sentinels at `REJECTED 0`.

Deliverables, added one at a time:

- shared graph combinators for spine, branch, rejoin, hub, cross-link, and long return;
- atrium-ring and twin-wing macro-topologies composed from those operations and distinguishable by graph structure;
- a small reviewed library: arrival/vestibule, generic chamber, junction, stair tower, bridge/overlook atrium, processional hall, and throne-hall episode;
- coarse deterministic line-of-sight checks between declared vista sockets and target volumes;
- promontory selection tied to a named vista target;
- route-level spacing and repetition rules;
- useful step formations migrated as explicit motifs while the global late pass remains parked.

Each new pattern or recipe is its own reviewable increment and must reuse the same canonical pipeline. It may not introduce a pattern-specific renderer, validator, or layout representation.

Exit gate:

- all three patterns pass deterministic graph, placement, transition, render, and collision tests;
- each recipe passes isolated, neighbor, and full-dungeon integration matrices;
- required vista edges realize or reject before rendering, and an accepted promontory names its target;
- authored motifs do not receive incompatible independent late feature rolls;
- superseded generic late-placement branches are deleted as each authored responsibility replaces them.

The current plain wall gap remains a valid doorway expression. Door leaves, arches, and gateway kits are optional content work and cannot block this phase.

### Phase 7 — Production hardening

Goal: demonstrate that the single generator is dependable enough for ordinary use.

Deliverables:

- large deterministic seed sweeps and curated human-review galleries;
- measured performance, rejection, and backtracking budgets;
- collision-export parity with the baked Unity scene;
- schema migration notes and explicit failure reporting;
- final code-path, settings, and asset-reference audit;
- final deletion ledger covering temporary flags, diagnostic scaffolding, superseded feature rolls, and unreferenced content.

Exit gate:

- 100% of accepted plans pass all hard validators and collision export;
- the agreed seed-sweep success rate completes within the measured attempt budget;
- the curated review set passes the coherence rubric below;
- one production planning, validation, transition, rendering, abyss, and collision path exists;
- no migration mode, legacy adapter, silent fallback, or scheduled deletion remains.

## 10. Acceptance rubric

### Hard validity: every accepted plan

- has a connected traversable route from its declared bottom to its declared top;
- serves every elevation delta with a valid transition;
- satisfies all port, landing, headroom, walkability, and overlap contracts;
- preserves every protected route, focal, symmetry, and vista reservation;
- provides abyss support at every exposed floor boundary according to the project invariant;
- produces collision geometry consistent with the rendered plan;
- regenerates identically from seed, profile, planner version, and catalog digest.

### Structural coherence: automated checks where possible

- main-route length and branch count are inside the selected pattern's declared range;
- loops reconnect distinct route regions and are not duplicate parallel doorways;
- landmarks are separated by connective beats;
- dead ends have an assigned purpose such as reward, reveal, or optional encounter;
- required paired stairs, galleries, and axes remain coupled;
- required vista edges are realized;
- no long run of generic chambers exceeds the profile's repetition limit;
- enclosure choices follow room and pattern policy rather than unrelated coin flips.

### Human review: curated seed gallery

Reviewers score:

1. route readability;
2. entrance and threshold clarity;
3. vertical circulation legibility;
4. focal hierarchy;
5. usefulness of overlooks and distant views;
6. relationship between landmark rooms and their approaches;
7. repetition across seeds;
8. whether the floor feels intentional without feeling preassembled.

Phase 0 locks the Phase 1 reliability budget. The Phase 3 visual review, Phase 4 episode review, and early Phase 7 sweep lock their respective acceptance thresholds before the corresponding final results are judged. Do not invent a favorable percentage after seeing a result.

## 11. Existing feature migration

These are migrations inside the single pipeline. “Retain” means call the existing implementation, not copy it into route-first or recipe-specific code.

| Existing capability | Single-pipeline treatment |
| --- | --- |
| Elevation archetypes | Retain as elevation-story policies constrained by the intent graph; they are not macro-topology variants. |
| Stair prefab pool and stair forge | Retain as exact transition realizers behind typed stair ports. |
| Synthesized stairs and bridges | Retain as declared compatible realizations, never silent plan repair. |
| Stairwell fallback | Retain for eligible generic transitions; landmark stairwells are intentionally selected motifs. |
| Daises and backed showpieces | Generic rooms may opt in; authored recipes declare them and reserve their protected space. |
| Promontories | Retain, but require a planned vista target and compatible boundary policy. |
| Step formations | Keep the global late pass parked; migrate useful formations into reviewed motifs. |
| Room shape randomization | Retain inside recipe-declared ranges and generic-room families. |
| Loop carving | Move loop intent before placement; the solver realizes a loop through compatible ports. |
| Abyss supports | Preserve unchanged as a hard renderer output invariant. |

## 12. Explicit non-goals and traps

- Do not solve coherence by building hundreds of whole-room prefabs.
- Do not use wave-function collapse or a tile adjacency system as the semantic planner. It may help local fill later, but it does not define the journey.
- Do not infer ports or semantics from asset names, meshes, colliders, or bounds.
- Do not enable the parked step-formation pass as a shortcut to vertical composition.
- Do not let the renderer carve, move, or delete architecture to make an invalid plan fit.
- Do not weaken abyss construction to improve piece count or performance without changing the project invariant explicitly.
- Do not confuse a different elevation field with a different floorplan topology.
- Do not author a large recipe pool before the schema, validator, preview, and review lifecycle are stable.
- Do not introduce intent, placed, and compiled DTO families around the existing canonical plans.
- Do not retain a permanent `Legacy`/`Coherent` mode split or a silent fallback from the new builder to the old one.
- Do not build the general recipe catalog or authoring window before the throne-hall schema probe passes.
- Do not add new route-first work to the existing giant files merely to avoid a focused extraction; equally, do not refactor those files beyond what the current slice requires.
- Lock-and-key, ability-gated, and one-way traversal gameplay are not implemented by this initiative. They are known future consumers of typed traversal edges, so do not hardcode the edge model or graph algorithms to assume that every connection must always be unconditionally bidirectional. Add no serialized gate fields until gameplay has a real consumer.
- Do not implement each named macro-topology as a separate planner. Compose them from the shared graph operations proven by the processional pilot.

## 13. Definition of done

This initiative is complete when a rebuild can select a named macro-topology, emit a readable intent report, place a small reviewed set of landmark recipes and many varied generic spaces around an intentional route, realize typed traversal and vista edges in 3D, preserve all hard invariants, and reproduce or reject the result deterministically before rendering. There is one reachable production path through planning, elevation, validation, rendering, abyss support, and collision export; the old layout builder, comparison selector, adapters, superseded late rolls, and scheduled deletion items are gone. A returning developer can add, test, review, and promote a recipe using the companion workflow without reverse-engineering the generator.

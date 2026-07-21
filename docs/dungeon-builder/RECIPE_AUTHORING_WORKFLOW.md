# Dungeon recipe authoring workflow

Status: implemented and verified for the Phase 5 recipe contract
Last updated: 2026-07-21

This document is the operational checklist for creating or changing a room recipe after the planner foundation is solid. It is intentionally written for someone returning after weeks or months away.

Phase 5 implemented the required outcomes: explicit versioned contracts, five non-mutating validation layers, deterministic previews, stale-review detection, reviewed catalog admission, and an easy return-after-absence workflow. The menus below are the current implemented commands. The deliberately narrow schema contains only the recipe kinds, motif kinds, overlays, and review actions proven by the throne-hall probe and flexible-vestibule contrast recipe.

## Returning after a break

Do this before editing a recipe:

1. Read [`CURRENT_STATUS.md`](CURRENT_STATUS.md) for the active milestone, last verified commands, known failures, and next task.
2. Re-read the locked decisions in [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) and the roadmap. Everything-rises-from-the-abyss is a hard invariant.
3. Open Unity at the committed editor version and let assets finish importing.
4. Run **Arena > Dungeons > Recipes > Validate Catalog**.
5. Rebuild one known-good production seed from `CURRENT_STATUS.md` before changing content.
6. If the catalog or known seed is already failing, record that baseline and fix or isolate it before authoring.

The catalog validation report should tell you the recipe schema version, planner version, catalog digest, reviewed recipe count, and any stale review digests. If it cannot, the foundation is not complete.

## The short version

```text
Write a one-page brief
        -> Create a Draft recipe asset
        -> Declare zones, typed ports, reservations, and intent
        -> Attach only explicit compatible motifs/prefabs
        -> Validate in isolation
        -> Generate deterministic variation previews
        -> Run neighbor and full-dungeon integration matrices
        -> Review the rubric and known seeds
        -> Promote to Reviewed
        -> Update CURRENT_STATUS.md before stopping
```

Only a `Reviewed` recipe whose validation digest still matches its content is eligible for ordinary generation.

## 1. Recipe lifecycle

| State | Meaning | Eligible for generation? |
| --- | --- | --- |
| `Draft` | Work in progress or changed since review | No, except explicit authoring previews |
| `Reviewed` | Automated checks and human review pass; reviewer metadata matches the digest | Yes |
| `Deprecated` | Kept for old plans/migrations but unavailable to new selection | No |

Validation is a result attached to the current content digest, not a lifecycle state. Editing a `Reviewed` recipe automatically returns it to `Draft`; alternatively, a digest mismatch makes it mechanically stale and ineligible until that transition is saved. Never preserve review status across a content, contract, prefab-reference, or schema change.

## 2. Source-of-truth layout

Implemented locations:

```text
Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/
  Catalog/
  Rooms/
  Episodes/
DungeonLabReports/Recipes/
  <recipe_id>/
```

Motif declarations are embedded in the recipe asset and reference the existing reviewed StairForge/content libraries; Phase 5 did not create an unused standalone motif-asset family or prefab directory.

- The recipe `ScriptableObject` is the semantic source of truth.
- A composed prefab is an explicitly referenced visual payload, not a source of inferred ports or dimensions.
- Generated reports and galleries are evidence, not authoring inputs.
- Shared measured stairs, bridges, set pieces, and step formations stay in their existing content libraries and are referenced through explicit contracts.

Use stable lowercase IDs with a category prefix, for example:

```text
room_small_cross_01
connector_stair_tower_01
episode_throne_twin_stairs_01
motif_gallery_balustrade_01
```

Renaming a display label is harmless. Changing a stable ID is a migration and should not be done casually.

## 3. Start with a recipe brief

Create the recipe only after you can complete this brief. Keep the final brief in the recipe's notes or adjacent documentation.

```text
Recipe ID:
Display name:
Kind: room | connector | episode | motif
Purpose in the player journey:
Eligible roles/beats:
Required traversal sequence:
Allowed traversal degree:

Mandatory ports:
Optional ports:
Stair/bridge ports and exact rises:
Landing and headroom reservations:

Internal elevation story:
Focal axis and focal object/zone:
Protected walkable areas:
Symmetry or coupled-feature rules:
Enclosure/boundary policy:
Vista sockets and intended target types:

Required motifs/prefabs:
Optional compatible motifs:
Legal rotations/mirrors:
Allowed dimensional or content variation:
Explicit incompatibilities:

Reference scene/images:
What must still read clearly with dressing removed:
```

If the brief says only “cool room with random stairs,” it is not ready. State what the player approaches, sees, chooses, climbs, and leaves through.

## 4. Create the draft

Use **Arena > Dungeons > Recipes > Create Recipe** and choose the narrowest kind that describes the content:

- **Connector** when traversal itself is the purpose, such as a stair tower or bridge landing;
- **Episode** for multiple coupled architectural elements that must be selected and placed as a unit;

Those are the only Phase 5 recipe kinds. Subordinate `StairTransition` and `FocalVisual` motifs are embedded declarations inside a recipe, not independently selectable recipes.

The creation flow should:

1. allocate a stable ID and content version;
2. create the asset in the correct folder;
3. mark it `Draft`;
4. select it in the authoring window and asset inspector;
5. show the 4-unit cell grid and elevation legend;
6. create no implicit ports or semantics.

## 5. Author structure before decoration

Work in this order.

### 5.1 Paint spatial zones

Declare the smallest current zone set:

- `Walkable` floor at the room base;
- `Elevated` floor at an exact relative level;
- `ProtectedCirculation` that generic fill and dressing cannot occlude;
- `ProtectedFocal` that preserves the focal composition.

Transitions separately declare their exact footprint, lower/upper cells, landing arrays, climb, lane count, rise, and headroom. Phase 5 intentionally leaves recipe-specific boundary, void, and generic-fill policy out of the schema; the existing canonical room/boundary services remain authoritative.

Do not use the preview mesh as a substitute for these declarations.

### 5.2 Add typed ports

Every connection has a stable port ID and declares:

- the current `Corridor` connection type;
- mandatory or optional status;
- exact edge, orientation, width, and walkable elevation;
- approach clearance and landing depth;
- headroom volume;
- route-edge binding, which remains in `RouteIntent` rather than the reusable asset.

A stair port additionally declares rise, run topology, lane width, top and bottom landings, and permitted stair contracts. A bridge port declares span rules and both landing contracts.

Never put a port at a convenient room center and expect the corridor pass to find the architecture later.

### 5.3 Declare composition intent

Mark:

- typed port directions and the route binding that derives the primary axis;
- protected focal or circulation zones;
- symmetry pairs between declared zones;
- transition atomic-group IDs;
- explicit focal alternatives through embedded motifs;
- areas that generic fill and dressing must never occlude.

Vista endpoints, route order, and node/edge bindings remain outside the reusable asset. Phase 6 may add a new semantic only after a working slice proves its consumer.

For a throne-hall episode, the dais, throne/focal zone, twin stairs, both landings, side galleries, and their symmetry relationship belong to one contract. Do not author them as unrelated random chances.

### 5.4 Attach motifs and visuals

Reference only assets whose measured contracts are current. For each motif declare:

- required or optional;
- allowed socket or region;
- footprint and height reservation;
- compatible rotations/mirrors;
- collision and traversal effect;
- symmetry-group behavior;
- fallback behavior if optional.

Useful step formations belong here as motifs. The old global late placement pass stays parked.

## 6. Add controlled variation

Variation is allowed only inside the contract. Prefer a few meaningful choices over many independent rolls.

Good variation:

- legal rotation or mirror states;
- a declared width/length range whose ports remain valid;
- one of several compatible focal set pieces;
- optional side bays inside reserved regions;
- dressing sets that preserve protected space;
- alternate stair assets satisfying the same exact transition contract.

Bad variation:

- independently enabling one half of a paired stair composition;
- moving a port after route placement;
- changing rise without changing the stair contract;
- placing a dais, promontory, or step formation into leftover cells;
- opening a wall because the room looks enclosed;
- using a random feature that can obstruct a focal axis, vista, landing, or route.

Variation choices must use stable per-recipe random streams so adding a decoration option does not change topology or port placement for the same seed.

## 7. Validate in layers

Use **Arena > Dungeons > Recipes > Validate Current Recipe**. Validation is non-mutating: it reports errors and never repairs the asset.

### Layer A — Schema

- IDs are unique and versions are supported;
- all required fields are explicit;
- asset and contract references resolve;
- lifecycle and review metadata are internally consistent.

### Layer B — Structural composition

- walkable cells, elevations, and boundary declarations are coherent;
- mandatory ports are reachable in the declared sequence;
- every elevation delta has an eligible transition;
- landings, headroom, and approaches are clear;
- protected zones do not overlap incompatible features;
- paired/symmetric elements are complete;
- abyss-edge support can be emitted for every exposed boundary.

### Layer C — Variation sweep

- every legal rotation and mirror is valid;
- optional motif combinations stay within the contract;
- the same preview seed is deterministic.

### Layer D — Neighbor integration

Test each port against a small matrix of compatible neighbors:

- generic corridor;
- minimum and maximum supported elevation context.

Phase 5's only typed neighbor is the generic corridor at the port's exact declared level. Add generic-room, recipe-to-recipe, optional-closed, or stair/bridge neighbor states only with the Phase 6 content that consumes them.

### Layer E — Full-dungeon integration

Run the recipe through the current processional-spine pattern and eligible role using fixed seeds. The report must distinguish:

- recipe incompatibility;
- spatial-solver exhaustion;
- unrelated plan failure;
- renderer or collision-export failure.

Do not weaken the recipe contract merely to make every topology accept it. Narrow eligibility is often the correct answer.

## 8. Generate the review gallery

Use **Arena > Dungeons > Recipes > Build Review Gallery**. The gallery should include:

- an undressed contract view with grid and port labels;
- top-down and player-height views for every legal orientation;
- every legal mirror state;
- each meaningful motif alternative;
- port-to-neighbor examples;
- the recipe in the fixed full-dungeon review context;
- focal-axis and vista overlays;
- one below-floor view confirming abyss supports and transition structure.

Screenshots support review, but the validator and serialized contract determine correctness.

## 9. Human review rubric

The reviewer answers these questions:

1. From the primary approach, is the entrance and next action readable?
2. Does the room's purpose remain obvious with decoration hidden?
3. Do stairs, bridges, and landings look designed with the room rather than inserted later?
4. Is the focal hierarchy clear, and are protected areas actually protected?
5. Are symmetry and paired elements complete from all accepted approaches?
6. Does each vista reveal a meaningful target rather than accidental empty space?
7. Do enclosure and openings match the recipe's intended mood and route role?
8. Do variations preserve identity without becoming visibly identical across seeds?
9. Does the recipe integrate cleanly with generic connective tissue?
10. Does every exposed edge still read as rising from the common abyss?

Record concise review notes against the content digest. “Looks good” is not enough if an exception or limitation was accepted.

## 10. Promote to reviewed

Promotion through **Arena > Dungeons > Recipes > Promote Current Recipe** requires:

- all validation layers passing;
- the required seed matrix saved in the report;
- no unresolved critical review notes;
- reviewer, date, schema version, and content digest recorded;
- recipe version and changelog updated;
- catalog validation passing after inclusion.

Commit the recipe, authored prefab changes, contract changes, and human-readable changelog together. Generated galleries should follow the repository's eventual artifact policy; do not add large generated output by accident.

## 11. Changing an existing recipe

1. Reproduce one of its recorded known-good seeds before editing.
2. Duplicate only when creating a genuinely separate architectural identity; otherwise edit the existing recipe.
3. Make the smallest explicit contract change first.
4. Increment the content version and write the reason.
5. Confirm the asset is now `Draft`.
6. Re-run all affected validation layers; port, footprint, rise, or protected-zone changes require the full matrix.
7. Compare the old and new plan reports and gallery views.
8. Re-review and promote.

If a change breaks old serialized plans, add a migration or create a new recipe ID. Do not silently reinterpret an old ID.

## 12. Deprecating a recipe

Mark a recipe `Deprecated` instead of deleting it when existing plan reports or migrations may reference it. Record:

- reason;
- replacement ID, if any;
- last compatible planner/schema version;
- whether old baked scenes require regeneration.

Delete only after references and migration obligations are proven absent.

## 13. Troubleshooting

| Symptom | First thing to inspect |
| --- | --- |
| Recipe is never selected | lifecycle state, catalog digest, eligible roles/beats, and traversal degree |
| Solver repeatedly rejects it | mandatory-port orientation, landing reservation, footprint domain, and overly broad eligibility |
| One stair appears without its partner | symmetry/atomic group declaration; this is a contract error |
| Corridor meets an awkward wall location | typed port placement and neighbor compatibility; do not add center-routing repair |
| Promontory looks into nothing | missing or unrealized vista target |
| Dressing blocks the room | protected route/focal/vista zones and decorator permissions |
| A visual prefab fits but validation fails | trust the measured contract; fix the asset or contract explicitly |
| Vertical supports are excessive | expected under the abyss invariant; do not suppress them as a recipe fix |
| A reviewed recipe became unavailable | its content or dependency digest changed and it correctly returned to draft/stale status |

## 14. End every authoring session

Before leaving the project:

1. Run current-recipe and catalog validation.
2. Record the last known-good and known-bad seeds.
3. Record whether Unity is left with uncommitted/generated asset changes.
4. Update the active milestone, exact next action, command/menu path, and blockers in [`CURRENT_STATUS.md`](CURRENT_STATUS.md).
5. If the recipe is not reviewed, leave it in `Draft` and state which validation layer is next.

The next session should begin with a reproducible state and one concrete next action, not “continue working on the dungeon.”

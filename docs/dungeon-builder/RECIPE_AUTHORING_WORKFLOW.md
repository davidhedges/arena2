# Dungeon recipe authoring workflow

Status: current for recipe schema v1 and the completed Slice D pool proof
Last updated: 2026-07-24

This is the operational checklist for creating or changing a room recipe. Use
[`GLOSSARY.md`](GLOSSARY.md) for the authoritative definitions of recipe,
recipe slot, availability, catalog, episode, motif, role, beat, room, port, and
zone.

For the literal click-by-click procedure, complete flat-room template, every
schema-v1 field, current fixed-slot limitations, and exact error checklist, use
[`ROOM_AUTHORING_GUIDE_CURRENT.md`](ROOM_AUTHORING_GUIDE_CURRENT.md).

The current schema has only `Connector` and `Episode` recipe kinds. Validation
and deterministic previews are authoring tools. Ordinary generation admits a
recipe only when all three conditions hold:

```text
explicit catalog membership
AND disabledForGeneration == false
AND current contract validation passes
```

New recipe assets start disabled. Enabling a recipe is a direct owner action.
There is no stored approval token or formal recipe lifecycle.

## Returning after a break

1. Read [`CURRENT_STATUS.md`](CURRENT_STATUS.md) for the active milestone,
   verified commands, known failures, and next task.
2. Re-read [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) and the current
   owner-approved plan item. Everything-rises-from-the-abyss is a hard
   invariant.
3. Open Unity at the committed editor version and let assets finish importing.
4. Run **Arena > Dungeons > Recipes > Validate Catalog**.
5. Rebuild one known-good production seed from `CURRENT_STATUS.md`.
6. If the catalog or known seed already fails, record that baseline before
   authoring.

The catalog report names the schema and planner versions, active catalog
digest, cataloged/enabled/disabled counts, invalid assets, and explicit failure
reason when an enabled asset is invalid.

## The short version

```text
Write a one-page brief
    -> Create a disabled recipe asset
    -> Declare zones, typed ports, reservations, and intent
    -> Attach only explicit compatible motif implementation IDs
    -> Validate the current contract
    -> Build deterministic preview evidence
    -> Add it to the explicit catalog when intended
    -> Enable it directly only after its current validation passes
```

Production recipe slots now discover compatible enabled catalog members by
role, beat, route-edge/port, orientation, elevation, transition, landing,
headroom, and current-validation contracts. Selection is deterministic and
uniform within the compatible set. In authoring-preview scope, a disabled
previously unknown recipe is forced into one compatible existing required slot
without a production C# binding. The scope temporarily replaces the production
candidate that ordinary selection would use and is always disposed before
ordinary generation resumes.

The proven `required-compression` pool currently contains
`connector_example_01` and `connector_flexible_vestibule_01`. Both use the same
selector, placement, canonical-plan, renderer, abyss, and collision paths.
Additional recipe content requires separate explicit owner approval, one
bounded asset increment at a time.

## 1. Availability

| State | Meaning | Ordinary generation |
| --- | --- | --- |
| `disabledForGeneration: true` | Authorable and previewable, but intentionally unavailable | Excluded |
| `disabledForGeneration: false` | Intended for ordinary generation | Admitted only when cataloged and currently valid |

Editing an enabled valid recipe makes the valid edit immediately eligible.
Leaving an enabled recipe invalid makes catalog validation fail explicitly; it
is not silently skipped. Disable the recipe while doing incomplete work when
ordinary generation must remain available.

The content digest remains computed evidence for deterministic catalog
identity, diagnostics, and replay. It is not serialized back into the recipe as
an approval field.

## 2. Source-of-truth layout

```text
Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/
  Catalog/
  Rooms/
  Episodes/
DungeonLabReports/Recipes/
  <recipe_id>/
```

- The recipe `ScriptableObject` is the semantic source of truth.
- The explicit catalog asset owns membership.
- Schema v1 has no room-prefab field. A recipe cannot point at an arbitrary
  room prefab. Its motifs may resolve existing reviewed visual implementations
  by explicit string ID; ports and dimensions still come only from recipe data.
- Generated reports and galleries are evidence, not authoring inputs.
- Shared measured stairs, bridges, set pieces, and step formations stay in
  their existing content libraries and are referenced through explicit
  contracts.

Use stable lowercase IDs with a category prefix, for example:

```text
connector_stair_tower_01
episode_throne_twin_stairs_01
```

Changing a stable ID is a migration. A display-label edit is not.

## 3. Start with a recipe brief

Create the recipe only after this brief is concrete:

```text
Recipe ID:
Display name:
Recipe kind: connector | episode
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

Declared motifs and implementation IDs:
Weighted focal variations:
Legal rotations/mirrors:
Allowed dimensional or content variation:
Explicit incompatibilities:

Reference scene/images:
What must still read clearly with dressing removed:
```

State what the player approaches, sees, chooses, climbs, and leaves through.
“Cool room with random stairs” is not an implementable contract.

## 4. Create the disabled asset

Use **Arena > Dungeons > Recipes > Create Recipe** and choose:

- **Connector** when traversal itself is the purpose;
- **Episode** when multiple architectural elements must be selected and placed
  atomically.

Creation allocates the asset in the matching folder, sets its stable ID,
schema/content versions, and kind, and leaves
`disabledForGeneration: true`. It creates no implicit port or semantic.

## 5. Author structure before decoration

### 5.1 Declare spatial zones

Use the existing schema-v1 zone kinds:

- `Walkable`;
- `Elevated` at an exact relative level;
- `ProtectedCirculation`;
- `ProtectedFocal`.

Transitions separately declare exact lower/upper cells, landing arrays,
occupied footprint, climb direction, rise, lane count, headroom, and atomic
group. The canonical room and boundary services remain authoritative.

### 5.2 Add typed ports

Every connection has a stable port ID and declares its current `Corridor` type,
mandatory status, exact cell and outward direction, relative level, width,
approach depth, and headroom. Route-edge binding remains in `RouteIntent`, not
the reusable asset.

Never place a convenient approximate port and expect corridor routing or the
renderer to repair it.

### 5.3 Declare composition intent

Declare protected focal/circulation zones, symmetry pairs, transition atomic
groups, primary-axis inputs, and embedded focal alternatives explicitly.
Vista endpoints, route order, and node/edge identity remain outside the asset.

### 5.4 Attach motifs and visuals

Reference only assets whose measured contracts are current. Schema v1 supports
embedded `StairTransition` and `FocalVisual` motifs. It does not authorize
dressing sets, prop anchors, a standalone motif catalog, new recipe kinds, or
renderer-side repair.

## 6. Keep variation inside the contract

Legal rotation/mirror states and weighted alternatives inside one recipe may
vary. Variation weights do not select between recipes. Variations must preserve
ports, rise, landings, protected space, atomic groups, and the recipe identity.

Use stable per-recipe random streams so adding a visual alternative does not
perturb topology or port placement.

## 7. Validate in layers

Use **Arena > Dungeons > Recipes > Validate Current Recipe**. Validation is
non-mutating and never repairs the asset.

### Layer A — Schema

- stable unique IDs and supported versions;
- explicit required fields and resolved references;
- eligible roles/beats and legal orientations.

### Layer B — Structural composition

- coherent walkable/elevated cells;
- reachable mandatory ports;
- complete transitions, landings, headroom, approaches, protected zones,
  atomic groups, and symmetry;
- abyss support for exposed boundaries.

### Layer C — Variation sweep

- every legal rotation/mirror and alternative remains valid;
- the same preview inputs reproduce the same result.

### Layer D — Neighbor integration

Each mandatory port must satisfy its generic-corridor neighbor contract at the
declared level.

### Layer E — Full-dungeon integration

The authoring-preview seam discovers a compatible existing required slot,
forces the current recipe there for the preview scope, and runs the existing
placement, `DungeonLayout`, `TieredLevelPlan`, renderer, abyss, and collision
evidence. The recipe may remain disabled and absent from the explicit
production catalog. A recipe with no compatible required slot fails explicitly.

## 8. Build deterministic previews

Use **Arena > Dungeons > Recipes > Build Preview Gallery**. Current evidence
includes contract, top-down, player-height, below-floor, legal
orientation/mirror/alternative, generic-neighbor, and full-dungeon views. The
gallery manifest records the forced recipe ID plus the topology, recipe-slot,
and route-node context used for full-dungeon evidence.

The validator and serialized contract determine correctness. Images are
diagnostic evidence.

## 9. Enable or disable directly

Availability is the `disabledForGeneration` checkbox in the recipe inspector or
authoring window.

Before enabling:

1. ensure the asset is an explicit catalog member;
2. run current-recipe validation;
3. run catalog validation;
4. reproduce the relevant deterministic preview evidence;
5. rebuild a known production seed when an existing route slot can consume the
   enabled recipe.

After enabling, run catalog validation again. An enabled invalid catalog member
must fail with a reason code and message. Disabling excludes the member without
deleting it.

## 10. Changing an existing recipe

1. Reproduce a recorded known-good seed before editing.
2. Disable the recipe first if incomplete edits must not interrupt ordinary
   generation.
3. Make the smallest explicit contract change.
4. Increment `contentVersion` and record the reason in the change history.
5. Re-run all affected validation layers and deterministic previews.
6. Compare content/catalog digests and canonical output.
7. Re-enable directly only when the current contract is valid.

Do not silently reinterpret an old recipe ID when serialized plans need a
migration.

## 11. Troubleshooting

| Symptom | First thing to inspect |
| --- | --- |
| Recipe is absent from the active catalog | explicit membership, `disabledForGeneration`, then current validation |
| Catalog validation fails | the named enabled recipe, reason code, and validation layer |
| Solver rejects a bound recipe | mandatory-port orientation, landing reservation, footprint domain, and eligibility |
| One stair appears without its pair | symmetry/atomic-group declaration |
| Corridor meets an awkward wall | typed port and neighbor compatibility |
| Dressing blocks circulation or focus | protected zones and decorator permissions |
| A visual prefab fits but validation fails | measured contract; fix the asset or contract explicitly |
| Vertical supports look excessive | the common-abyss invariant; do not suppress them as a recipe fix |

## 12. End every authoring session

1. Run current-recipe and catalog validation.
2. Record the last known-good and known-bad seeds.
3. Record whether Unity has uncommitted/generated asset changes.
4. Update the active milestone, exact next action, menu/command path, and
   blockers in [`CURRENT_STATUS.md`](CURRENT_STATUS.md).
5. Leave incomplete content disabled and name the next validation layer.

The next session should begin from a reproducible state and one explicit
owner-approved item.

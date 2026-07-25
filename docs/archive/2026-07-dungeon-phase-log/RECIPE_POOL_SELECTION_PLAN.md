# Dungeon recipe pool selection plan

Status: proposed; documentation only; implementation requires explicit owner approval per slice  
Last updated: 2026-07-23

## 1. Purpose

Replace the current exact recipe-ID bindings with deterministic selection from
the existing recipe catalog so a solo developer can author a compatible recipe,
enable it, add it to the catalog, and allow ordinary dungeon generation to
select it without changing production C#.

This is an in-place migration of the existing route-first generator. It does not
create a second planner, recipe system, canonical plan, renderer, collision path,
or compatibility mode.

The intended production flow is:

```text
Macro-topology pattern
    -> Route intent
        -> Three required recipe slots or existing generic rooms
            -> Select one compatible enabled recipe per required slot
            -> Existing atomic recipe placement
        -> DungeonLayout
            -> TieredLevelPlan
                -> Existing canonical renderer, abyss support, and collision output
```

## 2. Locked owner decisions

- Recipe authoring has no formal human-review or promotion process.
- A recipe is available to ordinary generation only when it is an explicit
  catalog member, is not disabled, and passes current automated contract
  validation.
- New recipe assets start disabled.
- A disabled recipe remains authorable and previewable but cannot be selected by
  ordinary generation.
- Editing an enabled recipe does not require promotion or re-review. A valid edit
  is immediately eligible; an invalid enabled recipe causes an explicit catalog
  validation failure.
- The current three required recipe slots remain required. A missing compatible
  recipe rejects generation rather than silently substituting a generic room.
- Nodes without recipe slots retain the existing generic-room construction path.
- The current recipe kinds remain only `Connector` and `Episode`.
- Recipe selection begins with equal probability among compatible candidates.
  Recipe-level selection weights are not part of this work.
- Schema-v1 episodes remain atomic compositions inside one room footprint.
- Dressing and props are deliberately deferred.

## 3. Explicitly deferred scope

This plan does not authorize or design:

- dressing sets, prop sets, prop anchors, or renderer-side decoration;
- optional recipe slots or recipe-to-generic fallback policies;
- recipe opportunities on nodes that are currently generic;
- a new `Room` recipe kind;
- unequal recipe-selection weights or profile-specific recipe weights;
- multi-room episodes or route-subgraph expansion;
- new macro-topologies, vistas, motifs, ports, transition kinds, locks, gates, or
  runtime generation;
- a second `DungeonLayout`, `TieredLevelPlan`, renderer, abyss, or collision
  implementation.

Each deferred capability requires its own future owner-approved plan item.

## 4. Current implementation gap

The reusable recipe geometry and realization path already exists:

- `DungeonRecipeAsset` declares zones, ports, transitions, motifs, symmetry, legal
  orientations, and variations;
- the catalog computes a deterministic digest and filters production content;
- recipe placement is atomic and reaches the existing tier planner, renderer,
  abyss support, and collision export;
- contract, neighbor, full-dungeon, renderer, and collision validation services
  already exist.

The missing behavior is catalog selection:

- production code looks up three exact IDs through `DungeonRecipeIds`;
- each route node stores an asset identity rather than a recipe-slot identity;
- eligibility validates an already-selected asset but does not discover
  candidates;
- full-dungeon authoring evidence requires a new recipe ID to have already been
  bound by production code;
- lifecycle, reviewer metadata, promotion, and stale-review machinery impose a
  process the solo owner does not want.

## 5. Vocabulary corrections

The authoritative glossary currently says both that a recipe slot requires a
particular recipe and that recipes compete for eligible slots. Before the
selector implementation closes, update the glossary to use these meanings:

### Recipe

A versioned authored spatial contract. It declares eligible roles and beats,
zones, ports, transitions, motifs, legal orientations, symmetry, and controlled
variations. It is not a prefab.

### Recipe slot

A route-node binding that requires one compatible enabled recipe selected from
the active catalog. Current production has three required recipe slots. Nodes
without a recipe slot use generic-room construction. If a required slot has no
compatible recipe, generation rejects instead of silently substituting a generic
room.

### Recipe availability

A cataloged recipe is eligible for ordinary selection when it is not disabled
and its current contract validation passes. Disabled recipes remain available
for editing and preview but cannot enter ordinary generation. Editing does not
require promotion or human review.

### Recipe catalog

The explicit list of recipe assets eligible for catalog validation and
production admission. Only enabled, currently valid recipes enter the active
selection pool.

### Variation

A weighted alternative inside one recipe contract. Variation weights do not
control selection between recipes.

No generic term such as “dungeon component opportunity” is introduced. Use the
existing terms `recipe`, `recipe slot`, `generic room`, `connector recipe`, and
`episode`.

## 6. Slice A — simplify recipe availability

### Boundary

Replace the formal recipe review lifecycle with one disabled flag. Do not change
which recipe occupies any route slot, how any room is placed, or any generated
geometry.

### Required changes

1. Replace `DungeonRecipeLifecycle` and its `Draft`, `Reviewed`, and `Deprecated`
   states with:

   ```csharp
   public bool disabledForGeneration = true;
   ```

2. Migrate the three current production recipe assets to
   `disabledForGeneration: false`.
3. Retain content-digest computation for deterministic catalog identity,
   diagnostics, and replay evidence. Remove its use as a stored approval token.
4. Define active catalog admission as:

   ```text
   explicit catalog membership
   AND disabledForGeneration == false
   AND current contract validation passes
   ```

5. Make an enabled invalid recipe fail catalog validation with an explicit
   reason. Do not silently skip it.
6. Create new recipe assets disabled by default.
7. Keep validation and deterministic previews available as authoring tools.

### Deletion gate

Delete all production, editor, test, and current-workflow dependencies on:

- `DungeonRecipeLifecycle`;
- `reviewedDigest`;
- `reviewer`;
- `reviewedAtUtc`;
- `reviewNotes`;
- `ReviewIsCurrent`;
- `DungeonRecipeLifecycleService`;
- recipe promotion actions;
- stale-review and invalid-promotion diagnostics and tests.

Historical status evidence may describe the old implementation as history, but
current instructions and current-state summaries must not claim the removed
process is still required.

### Exit gate

- All three existing assets load as enabled and valid.
- The active catalog contains the same three assets.
- Fixed production seeds preserve their existing geometry and canonical
  downstream behavior.
- A disabled clone is excluded.
- An enabled invalid clone fails catalog validation explicitly.
- Symbol and producer/consumer audits find no reachable formal recipe-review
  machinery.
- Work stops before recipe-pool selection.

## 7. Slice B — deterministic catalog selection

### Boundary

Replace exact asset-ID selection for the existing three required recipe slots.
Do not add a recipe asset, change the number or position of slots, change generic
rooms, or add a new fallback.

### Slot identity

Give each required route slot a stable identity independent of the selected
recipe asset. Preserve the slot's existing:

- route node;
- role and beat;
- incident route-edge bindings;
- route-forward or vista-facing orientation rule;
- required elevation and traversal context.

Do not serialize a second topology or plan model. The slot remains ephemeral
route intent and resolves to the existing `RecipeSlotIntent` consumed by recipe
placement.

### Candidate discovery

For each required slot:

1. Enumerate the active catalog in stable recipe-ID order.
2. Retain candidates compatible with the existing contract:
   - eligible role and eligible beat;
   - mandatory-port and incident traversal-degree requirements;
   - exact route-edge/port binding requirements;
   - legal orientation;
   - transition, rise, landing, and headroom requirements;
   - current contract validity.
3. Record a stable rejection reason for each incompatible candidate.
4. Reject the generation attempt if no compatible candidate remains.

Do not repair a recipe, move a port, weaken eligibility, or substitute a generic
room to make a candidate fit.

### Selection

Select uniformly from compatible candidates using a stable random stream derived
from:

```text
(dungeon seed, topology ID, stable route-node ID, "recipe-selection")
```

Candidate ordering must be deterministic. Recipe selection must not consume or
perturb topology, embedding, tier, motif-variation, or generic-room random
streams.

When a slot has one compatible candidate, selection must not introduce a
behavioral change.

### Diagnostics

The plan report must record:

- stable recipe-slot ID;
- slot node, role, and beat;
- active catalog digest;
- compatible candidate IDs in selection order;
- rejected candidate IDs and reason codes;
- selected recipe ID;
- selection-stream version or identity.

The report remains diagnostic evidence, not a new mutable planning model.

### Deletion gate

Delete:

- production `DungeonRecipeIds`;
- exact-ID catalog lookups used to populate production slots;
- route-node fields that conflate slot identity with selected recipe identity;
- production validators that require particular recipe IDs.

Retain:

- the current three required slots and their topology validation;
- asset-ID assertions in narrowly scoped content fixtures when they verify a
  particular checked-in asset rather than drive production selection;
- the existing recipe placement and all canonical downstream consumers.

Do not retain an old/fixed selector, compatibility toggle, or fallback path.

### Exit gate

- Current production still resolves exactly three recipes per accepted floor.
- With the current catalog, each slot resolves the same sole compatible recipe
  as before.
- Fixed seeds preserve geometry, traversal, vista, render, abyss, and collision
  behavior.
- Repeated runs reproduce candidate lists, selections, reports, and hashes.
- No production recipe asset ID remains in route construction or selection.
- Work stops before authoring-preview repair or new content.

## 8. Slice C — repair authoring preview

### Boundary

Allow a new disabled recipe to receive isolated, neighbor, and full-dungeon
evidence without adding its ID to production C#. Do not enable it automatically
or add another generation path.

### Workflow

```text
Create disabled recipe
    -> Author explicit contract
    -> Validate current content
    -> Discover compatible existing required slots
    -> Choose a compatible preview context
    -> Force the disabled recipe as the candidate for that slot in preview scope
    -> Run the existing placement, DungeonLayout, TieredLevelPlan, renderer,
       abyss, and collision evidence
    -> Optionally add the recipe to the explicit catalog and enable it
```

Preview injection occurs only at the catalog-selector seam. It must not copy the
planner, create a preview renderer, or weaken production validation.

### Exit gate

- A previously unknown disabled recipe ID can complete deterministic
  full-dungeon preview without production C# changes.
- Preview reports name the forced recipe and preview context.
- Preview state cannot leak into ordinary generation.
- Enabling remains a direct owner action, not a promotion or review operation.
- Work stops before adding a new production recipe.

## 9. Slice D — prove the pool with content

### Boundary

After Slices A-C pass, add exactly one owner-approved recipe compatible with one
existing required slot. This new content asset is not authorized merely by this
plan; the owner must explicitly approve the asset and its intended slot before
implementation.

### Acceptance gate

- The new recipe uses only the existing `Connector` or `Episode` kind and the
  existing schema fields.
- It passes isolated, neighbor, full-dungeon, renderer, abyss, and collision
  validation through the single existing pipeline.
- Adding it requires content/catalog changes and content-specific tests only; it
  requires no production selector, topology, placement, renderer, or collision
  code change.
- A locked seed corpus selects both compatible recipes at least once.
- Two independent runs reproduce the same per-seed selections and output hashes.
- Disabling either candidate removes it from ordinary selection without breaking
  the other.
- Disabling all candidates for the required slot yields an explicit pre-render
  rejection and never a generic-room substitution.
- Existing slots and recipes outside the selected proof slot preserve their
  behavior.
- Symbol and consumer audits find one selector and one placement path.

Work stops after the pool proof. Additional recipe content requires separate
owner approval, one bounded asset increment at a time.

## 10. Validation strategy

Every implementation slice must preserve:

- one route-first production planner;
- the existing three macro-topologies;
- exact required traversal connectivity;
- stair, landing, rise, lane, and headroom contracts;
- recipe atomicity;
- required vista validity;
- protected-cell behavior;
- `DungeonLayout` and `TieredLevelPlan` as the canonical downstream plans;
- the canonical renderer, abyss-support, and collision-export implementations;
- deterministic rejection before rendering.

Tests should move from production identity assertions to behavioral assertions:

- slot count and slot placement remain topology facts;
- candidate eligibility and selection are catalog behavior;
- particular asset geometry remains content-fixture behavior;
- repeated seed output remains deterministic;
- disabled and invalid content cannot enter ordinary selection;
- no renderer or collision stage makes a recipe choice.

Each slice ends with:

1. focused unit and integration tests;
2. locked fixed-seed comparison;
3. `git diff --check`;
4. production symbol and producer/consumer audit;
5. deletion-ledger verification;
6. a stop before the next slice.

## 11. Implementation order and authority

The implementation order is:

1. Slice A — simplify recipe availability;
2. Slice B — deterministic catalog selection;
3. Slice C — repair authoring preview;
4. Slice D — prove the pool with one explicitly approved content asset.

Approval of this document does not authorize all four slices. Each slice is one
bounded implementation item and requires explicit owner approval before editing.
“Continue” authorizes only the next explicitly approved slice and never a
deferred capability or additional content asset.

The first candidate implementation item is Slice A only. Its boundary is removal
of the formal recipe-review lifecycle while preserving the current catalog
membership, exact recipe bindings, generated geometry, and canonical downstream
behavior.

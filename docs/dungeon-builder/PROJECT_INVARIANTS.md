# Dungeon builder invariants

These are the durable rules carried over from the original Dungeon Builder project.

## Grid and contracts

- The kit grid cell size is 4 Unity units.
- Placement contracts use integer grid cells plus exact local edge or port offsets.
- Names may locate candidate assets, but never define function, footprint, facing, rise, lane count, run length, or ports.
- `Assets/Arena/Content/Settings/Dungeons/RandomDungeon/package_inventory.json` is a discovery catalog only.
- Generated measurement outputs are disposable unless their measurement path is reviewed, deterministic, and gated.

## Geometry sources

Floor and stair placement contracts may use only:

- explicit authored contracts validated as data;
- deterministic mesh/grid arithmetic from Floor and Stair prefab-family children;
- existing `.meta` GUIDs and prefab references that preserve authored assets.

Do not derive those contracts from prefab names, semantic assumptions about transform pivots, bounds that include decorative overhangs, decorative children, physics probes, raycasts, rendered-surface sampling, screenshots, or renderer repair passes.

Decorative children may render, but cannot widen ports, change lane count or rise, suppress floor prefabs, or move landings.

## Stairs and planning

- Stair prefabs are structural anchors. Place them before growing surrounding floor prefabs and corridors.
- Every stair contract needs an exact footprint, entry and exit port spans, rise, lane count, and run length.
- Landings are allocated from measured port spans and must match their stair lane count.
- Ordinary stair prefabs embed into the entered floor region. Bridge behavior requires its own explicit contract.
- The renderer consumes an already-valid plan; it does not repair invalid placement.
- Production planning is route/spine first: main route, optional branches and loops, then rooms and corridors grown from valid anchors.
- Every dungeon must have at least one connected bottom-to-top route.
- Validation fails loudly for bad placement, missing ports, incorrect landing width, floor clipping, disconnected floor/stair nodes, or stale contracts.

## Set pieces

- Reuse artist-authored composed prefabs whole. New loose-part compositions must become first-class authored assets.
- StepFormations are floor compositions, not primary circulation stairs.
- Raised StepFormations sit on existing floor prefabs. Sunken features need explicit measured coverage before clearing floor cells.

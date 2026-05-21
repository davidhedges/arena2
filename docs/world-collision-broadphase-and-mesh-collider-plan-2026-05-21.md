# World Collision Broadphase and Mesh Collider Plan

Date: 2026-05-21

## Goal

Improve environment collision fidelity without making projectile, line-of-sight, or movement queries scale linearly with every authored world collider.

Authoring policy:

- Use `BoxCollider` gameplay collision where it is a close enough approximation.
- Use mesh collision only for Arena-owned environment variants where boxes are not good enough.
- Keep the workflow automatic after authoring. Designers should not manually maintain server collision files.

## Current State

Unity environment variants live under:

- `Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants`

Those variants currently add Arena-owned `ArenaGameplayCollision` children using `BoxCollider`s on the `GameplayCollision` layer. Unity editor tooling exports those colliders into server-readable JSON files such as:

- `server/src/gameplay_collision.shared.json`
- `server/src/world_data/*.collision.shared.json`

There are currently ten scene collision files under `server/src/world_data/`, plus the shared arena collision file. The current collision JSON payload is about 7.8 MB before adding mesh collision data.

A rough measurement of the current baked JSON found 18,121 exported boxes across those files. Because the current format does not preserve prefab GUIDs, exact deduplication potential is unknown; weak signatures show many repeated leaf names and repeated size/rotation combinations, so box instancing should be measured before it is deprioritized.

Several scenes also have parallel `*.heightfield.shared.json` files. Heightfields are terrain surface data and are out of scope for the first projectile/LOS mesh-collision pass, except that existing heightfield behavior must remain unchanged.

The Rust server loads collision files into in-memory collision structs. JSON is not queried at runtime.

The current exported box format is fully baked per instance: each collider record stores a hierarchy name, world center, world size, shape, and Y rotation. It does not preserve prefab identity or share repeated collider definitions across repeated prefab instances.

Projectile collision currently has a split performance profile:

- Player candidates use a spatial index in `server/src/combat/player_snapshot.rs`.
- Exported world gameplay boxes are filtered by scene/profile, then scanned linearly in `server/src/world_collision.rs`.
- Generated open-world terrain-like colliders are queried separately through `raycast_open_world_for_profile`, which uses fixed-step sampling and calls `open_world_point_hits_geometry`.
- Narrowphase for existing world boxes expands each box by projectile radius and raycasts against it.

Arrows are not special-cased in the collision algorithm. `ARROW_STANDARD` is a normal linear weapon projectile with authored speed, radius, max distance, spawn offsets, and initial line-of-sight requirements.

## Design Direction

Use separate collision purposes instead of one universal collider representation.

### Movement Collision

Movement and body blocking should continue to use simple conservative geometry:

- `GameplayCollision` layer.
- `BoxCollider`s where possible.
- Small compound box setups only when a single box causes unacceptable blocking.
- False positives are acceptable. Blocking a player slightly early is usually better than expensive or fragile geometry.

This collision is used for body movement, pushout, surface checks, and other player-scale world interaction.

### Projectile and Line-of-Sight Collision

Projectile and LOS queries should use a dedicated static query path:

- Add a dedicated authoring layer, tentatively `GameplayQueryCollision`.
- Add a child naming convention such as `ArenaGameplayQueryCollision`, parallel to `ArenaGameplayCollision`.
- Export only from `_Arena` variants and explicitly allowed layers.
- Support boxes first, then opt-in mesh colliders.
- Mesh colliders should be simplified collision meshes, not visual LOD0 meshes by default.
- The server should query baked static data, not Unity physics.

This collision is used for:

- Projectile world hits.
- Line of sight.
- Later, optionally, explosion occlusion checks.

Explosions are intentionally out of scope for the first implementation.

Decision: line of sight should use the same authored collision set as projectiles unless a concrete gameplay problem appears. That makes the initial layer a projectile/LOS query layer, not a projectile-only layer. A broader layer name such as `GameplayQueryCollision` may be clearer than `ProjectileCollision`.

## Why Not Use One Collider Set?

Movement and projectiles have different tolerances:

- Movement wants conservative, stable blocking.
- Projectiles and LOS want shape fidelity.
- False-positive movement blocking is acceptable.
- False-positive projectile hits are also acceptable, but very large box approximations can feel visibly wrong around rocks, ruins, and irregular props.

Keeping separate layers lets common movement stay cheap while allowing higher-fidelity query geometry only where it matters.

## Phase 1: Add World Broadphase for Existing Boxes

Before adding mesh support, add a broadphase for the current exported world boxes.

Requirements:

- Preserve current behavior.
- Build the index once when collision data is loaded.
- Query by swept projectile segment bounds plus projectile radius.
- Return candidate collider indices.
- Run the existing box narrowphase only on candidates.
- Fall back to full scan for invalid input or unusually large queries using an explicit threshold.
- Preserve SpacetimeDB replay determinism. Candidate emission must be deterministic and must preserve the same tie behavior as the current full scan.

Recommended implementation:

- Start with a static uniform grid over world-space X/Y/Z, or an X/Z grid with a required Y-band prune before narrowphase. A pure X/Z grid is not enough for vertically stacked interiors.
- Store collider AABBs in the grid.
- For each ray/swept-sphere query, compute segment bounds expanded by radius and query overlapping cells.
- Deduplicate candidate indices.
- Emit candidates in original `&'static [GameplayCollisionBox]` index order before narrowphase. Do not use cell-iteration order as narrowphase order.
- Exact hit selection remains the existing narrowphase.

Initial sizing heuristic:

- Pick cell size from scene data, not a hardcoded universal value. A reasonable first pass is `2x` the median collider AABB extent, clamped to a min/max such as `2m..16m`.
- Measure grid memory, cell occupancy, and max cells per collider. Scenes with many small props and a few giant structures may need loose-grid handling or a separate large-object list.
- Use a fallback threshold such as `> 256` visited cells or `> 25%` of scene colliders returned as candidates. These numbers should be measured and tuned, but they must be explicit in the implementation.

Why uniform grid first:

- Scenes are static.
- Queries are short projectile/LOS segments.
- Implementation is simpler than a BVH.
- It should be easy to instrument and validate.

Known scene-shape risk:

- Exterior scenes should benefit from spatial bins quickly.
- Vertically stacked interiors such as `great_hall_day` and some parts of `giant_skeleton` need 3D bins or Y pruning so different floors do not collapse into the same X/Z column.

Generated open-world collider path:

- The first broadphase pass targets exported gameplay boxes.
- `raycast_open_world_for_profile` still performs fixed-step sampling. Broadphase does not remove the `O(distance / OPEN_WORLD_RAYCAST_STEP)` cost.
- A follow-up pass should index generated open-world colliders used by `open_world_point_hits_geometry`, so each sampled point checks nearby generated colliders rather than every generated collider.

Metrics to add or expose:

- World collision queries per tick.
- World broadphase candidates per query.
- World narrowphase tests per query.
- Generated open-world point geometry checks per raycast, so the fixed-step path becomes visible if it becomes the next hot spot.
- Full-scan fallback count.
- Full-scan fallback ratio by scene/query type, with warnings when the ratio exceeds a threshold such as `10%` over a rolling window.
- First-query grid build time or eager-init time.
- Worst tick time under projectile load harness.

Success criteria:

- Current gameplay remains unchanged.
- Existing collision tests pass.
- Add property tests that compare full scan vs broadphase. For randomized scenes, collider boxes, ray segments, and projectile radii, the broadphase candidate set must include every collider that the full scan would hit.
- Add deterministic-order tests. For identical data and query input, broadphase candidate order entering narrowphase must match original collider index order.
- Projectile load harness shows substantially fewer world narrowphase checks in large scenes.

Initialization requirement:

- Do not surprise the first reducer query for a scene with a large grid build unless measured and accepted.
- Prefer eager initialization during scene/world-data load, or record and cap first-query latency if using `OnceLock`.

## Phase 2: Add Projectile/LOS Authoring Layer

Add an explicit Unity authoring contract for projectile/LOS collision.

Recommended rules:

- Only export from Arena-owned `_Arena` variants.
- Only export colliders on an explicit projectile/LOS query layer, tentatively `GameplayQueryCollision`.
- Ignore raw third-party prefabs outside the Arena variant tree.
- Keep `GameplayCollision` export unchanged.
- Allow `BoxCollider` first.
- Allow `MeshCollider` later once server mesh query support exists.
- Provide an editor entry point parallel to the current gameplay collision export flow, and ensure CI or validation can detect stale generated collision data.

Editor tooling should validate:

- Projectile/LOS query collision exists only on allowed prefab variants.
- Mesh collision is non-convex static collision data only.
- Mesh triangle count is within a configured budget.
- The mesh source is not accidentally a dense visual LOD0 mesh unless explicitly allowed.
- Colliders are not scaled or transformed in unsupported ways.
- Dense meshes are rejected by default. Export requires explicit override metadata plus a warning until scene and module-size budgets are proven safe.

## Phase 3: Export Simplified Mesh Collision

For assets where boxes are not close enough, export simplified mesh collision data.

Initial mesh policy:

- Opt-in only.
- Static only.
- Environment variants only.
- Intended for projectile and LOS queries, not player movement.
- Use author-provided simplified `MeshCollider` meshes when available.
- Prefer lower-detail collision meshes over visual LOD meshes.
- Run a module-size budget check before landing mesh export, because SpacetimeDB modules ship as WASM and static collision data can affect deploy and cold-start behavior.

Exported data should include:

- Scene/profile identity.
- Prototype source prefab path for debugging.
- Instance scene object path for debugging.
- Per-instance transforms recorded on instances.
- Vertices in prototype-local space, never pre-transformed for shared prototypes.
- Triangle indices.
- Prototype-local AABB.
- Optional material/surface identifier, if needed later.

Projectile and LOS queries should choose the nearest hit across all enabled world surfaces: query-collision boxes, query-collision meshes, existing terrain/heightfield behavior, and generated open-world geometry. Heightfield export is out of scope for mesh authoring, but heightfield hits remain part of final world-hit selection where they already participate.

The server should not care whether the mesh came from Unity's `MeshCollider`, a child object, or a generated asset. It should only receive baked static collision geometry.

### Mesh Cleanup and Cooking Policy

Do not depend on Unity/PhysX cooked MeshCollider output for server gameplay collision.

Unity cooking options can improve Unity's internal PhysX representation, but the server does not query that cooked data. The exporter should treat Unity as the authoring source and produce deterministic server-owned collision data.

Exporter-side cleanup should be explicit and reproducible:

- Validate finite vertices, finite transforms, sane bounds, and non-empty triangle buffers.
- Remove degenerate triangles.
- Do not weld vertices by default. Welding can introduce cracks or T-junctions if it is not part of a watertight remesh.
- Allow welding only as an explicit per-mesh authoring choice or generated-asset step, with validation that it does not introduce false-negative ray gaps.
- Preserve stable vertex/triangle ordering after cleanup.
- Report before/after vertex and triangle counts.
- Keep a debug visualization of the exact exported server mesh.

Decimation should be offline and explicit:

- Prefer author-provided simplified collision meshes.
- Do not silently decimate at export time.
- If decimation is needed, require an explicit generated asset or explicit export override, then validate the result like any other collision mesh.

V-HACD or convex decomposition is not part of the initial projectile/LOS path:

- It is useful for compound convex physics simulation.
- Projectile/LOS queries against static world geometry are better served by simplified triangle meshes plus acceleration structures.
- Revisit convex decomposition only if movement collision later needs mesh-derived compound primitives.

### Prefab Prototype and Instance Encoding

Reusable prefabs should be leveraged before mesh collision data grows large.

Recommended exported shape:

- `prototypes`: reusable collision sets for a prefab or prefab variant root. A prototype may contain multiple box colliders and mesh colliders in local space, and each collider carries its own collision purpose.
- `mesh_geometries`: unique shared mesh geometry keyed primarily by mesh asset GUID. Content hash is used for generated or overridden geometry that does not have a stable source mesh asset identity.
- `instances`: scene placements referencing a prototype id plus transform, stable scene object id, scene path/debug name, and optional placement-level purpose mask.

Exporter output should be regenerated from live scene/prefab references and should prune `mesh_geometries` entries that have no live prototype or instance references.

Content hash policy:

- Hashes cover the cleaned prototype-local vertex buffer, index buffer, collider purpose, cleanup parameters, and any baked override transform components.
- Hash input should use a documented canonical byte layout, such as little-endian IEEE-754 floats for vertex data and little-endian integer indices.
- Hashes must be computed after cleanup and before JSON formatting so whitespace or decimal formatting changes cannot affect identity.

Collision purpose model:

- Purpose is authored per collider inside the prototype, matching Unity layer authoring.
- A prototype can contain separate movement and projectile/LOS query geometry for the same prop.
- Instances may add a placement-level purpose mask only for opt-out cases, such as disabling query collision for a specific placement.
- Instances may not add a purpose that the prototype does not author. Additive purpose changes must emit an override prototype.
- Do not model the whole prototype as a single movement/projectile/both flag; that would incorrectly imply movement and projectile/LOS usually share the same geometry.

For box colliders, prototype/instance encoding is a measured optimization, not a dismissal. Before choosing the final encoding, the exporter should report how much JSON size would be saved by grouping repeated prefab collision sets.

For mesh colliders, prototype/instance encoding is required:

- Store each unique collision mesh once.
- Build one per-prototype triangle acceleration structure.
- Store many scene instances as transforms and object-level bounds.
- Query a top-level world broadphase over instances, then query the shared per-prototype mesh acceleration structure in local space.

This mirrors the usual TLAS/BLAS split:

- TLAS: scene instances and their world bounds.
- BLAS: shared collider geometry for one prefab/prototype.

Instance transform policy:

- Initial support should be translation, positive uniform scale, and Y rotation only.
- The exporter should reject negative scale, non-uniform scale, and X/Z rotation for shared prototypes, mesh or box, unless it bakes that instance as a unique prototype.
- TLAS entries must store transformed world AABBs per instance. They must not reuse local prototype AABBs.
- This matches the current box format's practical `OBB-Y` path and avoids local-space swept-sphere correctness issues under arbitrary affine transforms.
- Before Phase 3 ships, audit existing Arena environment placements for transform conformity. If too many placements require unique baking, prototype/instance encoding still works but loses much of its size and BLAS-sharing benefit; the fallback strategy should then be explicit, such as pre-baked per-instance vertices for outliers.

Prefab variants and overrides:

- If a prefab variant or scene instance changes collider geometry, disables a collider, adds a collider, or changes supported transform constraints, the exporter must either reject the override or emit a unique content-hash prototype for that overridden collision set.
- Silent sharing with the base prefab is not allowed.
- The preferred default is to emit a unique content-hash prototype and warn when overrides reduce deduplication.

Nested prefabs:

- Prototype scope should be the exported Arena variant collision set, not one prototype per individual collider.
- Nested prefab collider data may still refer to shared `mesh_geometries`, but the exported prototype should represent the collision set actually authored by the `_Arena` variant after overrides are applied.
- This keeps one scene placement pointing at one collision prototype while preserving mesh-data sharing internally.

Migration sequencing:

- Phase 1 broadphase may target today's fully baked per-instance box format.
- The broadphase should operate on encoding-agnostic `(instance_id, world_aabb, stable_order)` entries.
- Today's baked format should be converted into those entries at load time.
- Prototype/instance encoding should produce the same broadphase entries from instance transforms and prototype-local bounds.
- Instance world AABBs should be derived server-side from prototype-local bounds and instance transforms, not exported as an independent source of truth.
- During migration, property tests must compare full-scan behavior for both encodings.

Determinism requirement:

- Prototype ids and instance ids must be stable across exports.
- Use Unity editor-stable identifiers such as asset GUIDs, GlobalObjectId, scene GUID plus fileID, collider path, mesh GUID, and content hashes.
- Runtime candidate order must still resolve ties in deterministic scene-instance order.
- Do not depend on Unity runtime `GetInstanceID()`, dictionary iteration order, or asset database enumeration order.

Budget gates:

- Track exported byte size by scene.
- Track mesh collider count, vertex count, and triangle count by scene and by prefab.
- Fail or warn when a scene exceeds agreed limits.
- Require explicit override metadata for dense meshes.

## Phase 4: Mesh Query Acceleration

Mesh support needs two acceleration layers:

1. World broadphase over object-level bounds.
2. Per-mesh triangle acceleration structure.

This is a solved-problem area. The project should not hand-roll triangle mesh BVHs, ray/triangle query code, or swept-shape math unless a library fails the constraints below.

Recommended evaluation:

- Evaluate Parry (`parry3d`) for static triangle mesh narrowphase and per-mesh acceleration structures.
- Keep Arena-owned export, world broadphase, stable ordering, metrics, and gameplay semantics.
- Do not adopt a full physics engine unless the scope expands beyond static world queries.
- Treat Rapier as a fallback/evaluation candidate only if we need a broader query pipeline or later server physics.

Library acceptance criteria:

- Builds for the SpacetimeDB server target and stays within the module-size budget defined before Phase 3. The evaluation should report absolute WASM size and percent growth from the current module.
- Supports static triangle mesh ray queries, and ideally shape casts or sweep queries relevant to projectile radius.
- Allows deterministic candidate ordering at the Arena layer. Library hit results must not make replay behavior depend on hash iteration or nondeterministic traversal ties.
- Allows us to keep stable debug mapping from server hit back to exported scene object/prototype/triangle.
- Handles our transform policy cleanly: query instance world AABB in Arena broadphase, transform query into prototype-local space, run mesh narrowphase, then transform hit back to world space.
- Has acceptable cold-start cost for building per-prototype mesh acceleration structures, or supports precomputed/baked structures later. Mesh acceleration init should follow the same eager-init or measured first-query-latency policy as the box broadphase.
- Is benchmarked against a minimal in-repo triangle-BVH baseline for representative mesh/query workloads before adoption. The baseline is for sizing and risk reduction, not a commitment to hand-roll production collision math.
- Has an acceptable license footprint for server distribution; Parry's Apache-2.0/MIT licensing is expected to be compatible.

Useful references:

- Parry: https://parry.rs/
- Parry `TriMesh`: https://docs.rs/parry3d/latest/parry3d/shape/struct.TriMesh.html
- Rapier: https://rapier.rs/docs/

Recommended structure:

- Top-level world grid or BVH maps query bounds to object candidates.
- Each mesh collider has a small local BVH over triangles.
- Projectile/LOS query tests object candidates first, then triangle candidates.
- Keep a conservative box/mesh mixed path so both representations can coexist.

Narrowphase options:

- LOS can use ray vs triangle.
- Projectiles can use swept sphere/capsule vs triangle.
- A conservative first pass may inflate object and triangle AABBs by projectile radius for candidate pruning, but that is not the same as mathematically inflating triangles. Exact narrowphase behavior must be specified before relying on grazing hits.

Because false positives are acceptable, the first mesh implementation can be conservative if it avoids false negatives and remains stable.

## Phase 5: Optional Binary Bake

Do not optimize JSON first.

JSON is acceptable while it is parsed once at startup into in-memory structures. If collision data grows large enough to affect startup time, file size, or memory layout, consider a generated binary format or precomputed serialized broadphase.

This is lower priority than the broadphase and authoring split for current box data, but mesh export must include a WASM/module-size budget check before it lands.

## Non-Goals

- Do not replace Unity physics globally.
- Do not use Unity runtime physics as the authoritative server collision system.
- Do not use detailed mesh collision for player movement in the first pass.
- Do not solve explosion occlusion in the first pass.
- Do not export raw third-party prefabs directly.
- Do not require manual edits to server JSON.

## Open Questions for Review

1. Should the projectile/LOS layer be named `GameplayQueryCollision`, `ProjectileCollision`, or something else?
2. What triangle budget should be allowed per mesh collider and per scene?
3. Should arrows keep their current radius-based swept segment behavior, or should they eventually use a thinner raycast with presentation-only forgiveness?
4. Should movement ever consult mesh collision for large static structures, or should mesh collision remain strictly projectile/LOS-only?
5. How should debug visualization work for exported projectile/LOS collision in Unity and on the server?
6. Should generated open-world colliders get their own broadphase in Phase 1, or is it acceptable as a Phase 1 follow-up after exported gameplay boxes?

## Recommended Next Step

Implement Phase 1 first: a world-collider broadphase for the existing exported gameplay boxes.

That gives an immediate performance improvement, reduces the risk of future mesh support, and creates the candidate-query shape needed for mixed box and mesh collision later.

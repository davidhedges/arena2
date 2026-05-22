# World Collision Broadphase and Mesh Collider Plan

Date: 2026-05-21

## Goal

Improve environment collision fidelity without making projectile, line-of-sight, or movement queries scale linearly with every authored world collider.

Authoring policy:

- Use `BoxCollider` gameplay collision only where it is a close enough approximation.
- For detailed Arena-owned environment variants, expect mesh-derived collision to be the normal path rather than a rare exception.
- Evaluate V-HACD or an equivalent decomposition/optimization pipeline for assets where simple boxes are not usable.
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

## Implementation Status

Current branch status:

- Phase 1 server broadphase for existing exported gameplay boxes is implemented. It builds static per-scene 3D uniform grids, preserves original collider index order before narrowphase, and falls back to full scan for oversized queries or unindexed bad data.
- Collision data is preloaded during server bootstrap so the first projectile query in a scene does not pay the broadphase build cost.
- Projectile metrics now report world broadphase candidates, gameplay-box narrowphase tests, full-scan fallbacks, and generated open-world geometry point checks. Phase 1 also logs broadphase occupancy/index health at bootstrap and warns when runtime fallback ratio is high.
- The projectile load harness measurement is intentionally deferred; Phase 1 is considered wrapped without it for now.
- The Unity project now has a `GameplayQueryCollision` layer and editor menu items to audit, mark, or prepare selected `BoxCollider`/`MeshCollider` objects for future projectile/LOS query collision authoring.
- Phase 2 box-only query-collision export is implemented. The exporter writes separate `*.query_collision.shared.json` files from `GameplayQueryCollision` `BoxCollider`s. During migration, the server raycast path queries both movement boxes and query boxes, then keeps the nearest hit, so partial query authoring cannot make every non-query-authored prop stop blocking projectiles.
- `Arena/OpenWorld/Scene Prep/3c Prepare Selected Variant Collision Roles` is the preferred manual cleanup tool for selected variant assets, selected Project folders containing variants, prefab-mode roots, scene instances, or scene containers. It expands Project folder selections to prefab assets underneath, expands scene/container selections to the placed prefab instance roots underneath, edits selected prefab assets by loading/saving prefab contents, keeps visual roots/LOD objects off collision layers, copies visual/root `BoxCollider`s onto dedicated `ArenaGameplayCollision*` children, copies those same author boxes to `ArenaGameplayQueryCollision*` when they are the best available query shape, preserves the original visual/root box components on `Default` as authoring source data, and otherwise creates `ArenaGameplayQueryCollision*` children from mesh colliders on `GameplayQueryCollision`.
- Early audit of placed rocks showed ordinary X/Z tilt on environment props. Full-rotation query `BoxCollider` support is now implemented as `obb_xyz` export data with quaternion rotation and server-side full-rotation OBB raycasts. Movement export can still flatten tilted boxes to conservative AABBs.
- Mesh query geometry export and server parse/preload are partially implemented as a data path. Meshes are not used for projectile/LOS hits yet; mesh narrowphase acceleration is still pending.

## Design Direction

Use separate collision purposes instead of one universal collider representation.

### Movement Collision

Movement and body blocking should prefer cheap conservative geometry, but boxes are not sufficient for many detailed assets:

- `GameplayCollision` layer.
- `BoxCollider`s where they are close enough.
- Existing author `CapsuleCollider`s are useful source authoring data, especially for trunks, but they are not exported by the current server collision format. Either add explicit capsule export/server support, or convert them to generated box/hull collision before relying on them for authoritative movement/projectile results.
- Generated compound convex hulls, simplified hulls, or other optimized movement collision for detailed props where boxes create unacceptable blocking.
- Small hand-authored compound box setups only when they are actually practical.
- False positives are acceptable. Blocking a player slightly early is usually better than expensive or fragile geometry.

This collision is used for body movement, pushout, surface checks, and other player-scale world interaction.

Interim authoring decision for bad boxes:

- Do not delete or disable existing `GameplayCollision` boxes just because they are bad projectile/LOS approximations.
- Keep a bad box on `GameplayCollision` only as a temporary movement blocker if removing it would let players move through the asset.
- Do not add bad boxes to `GameplayQueryCollision`.
- Do not put visual roots or LOD renderer objects on either collision layer unless that exact `GameObject` owns the intended collider. Layers are per `GameObject`, not per collider component.
- For confusing existing assets, select the Arena variant, scene instance, or a scene container containing many prefab instances and run `Arena/OpenWorld/Scene Prep/3c Prepare Selected Variant Collision Roles` instead of hand-editing root layers. If an asset has both a visual/root `BoxCollider` and an `ArenaGameplayCollision` child box, the tool should preserve the visual/root box shape by copying it to dedicated movement/query children, keep the dedicated child as the movement collider owner, and leave the visual/root box on `Default` as source authoring data.
- If an asset has author `CapsuleCollider`s, keep them on `Default` for now. The cleanup tool should report them as preserved but unsupported until capsule export/server support or deterministic capsule-to-hull conversion is implemented.
- Editor query-marking tools must skip existing `GameplayCollision` objects by default so they do not accidentally move temporary movement blockers onto the query layer.
- Arena variant generation must not skip a third-party prefab solely because the source prefab already has a `BoxCollider`. Existing vendor box colliders may still need Arena-owned query collision, replacement movement collision, or explicit review.
- Treat bad movement boxes as replacement candidates for generated movement collision, such as V-HACD/compound hull output.
- Once generated movement collision exists and is exported for an asset, remove or disable the old bad `GameplayCollision` box.
- Track these as intentional debt, not as acceptable final authoring.

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
- Broadphase index health at preload time: cell count, index entries, max cell occupancy, max cells per collider, and unindexed collider count.
- Worst tick time under projectile load harness is deferred until a dedicated projectile-load measurement pass.

Success criteria:

- Current gameplay remains unchanged.
- Existing collision tests pass.
- Add property tests that compare full scan vs broadphase. For randomized scenes, collider boxes, ray segments, and projectile radii, the broadphase candidate set must include every collider that the full scan would hit.
- Add deterministic-order tests. For identical data and query input, broadphase candidate order entering narrowphase must match original collider index order.
- Projectile load harness proof is deferred by decision. Phase 1 completion relies on focused correctness tests, preload/index health metrics, fallback-ratio warnings, and existing projectile metrics.

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
- Allow `BoxCollider` first. This is now implemented for arena and open-world scene exports.
- During migration, projectile/LOS raycasts must augment the existing `GameplayCollision` set with the query set, not replace it scene-wide. Replacement is only safe later if coverage is complete or if the data model can suppress movement fallback per prefab/prototype.
- Query export supports full-rotation `BoxCollider`s as `obb_xyz` with quaternion rotation. Movement export may still flatten tilted movement boxes to conservative AABBs, but query collision should not knowingly bake huge false-positive AABBs.
- Allow `MeshCollider` later once server mesh query support exists.
- Provide an editor entry point parallel to the current gameplay collision export flow, and ensure CI or validation can detect stale generated collision data.

Editor tooling should validate:

- Projectile/LOS query collision exists only on allowed prefab variants.
- Open-world scene query export only accepts colliders sourced from Arena-owned environment variant prefabs; raw third-party prefab instances must be skipped or reported.
- Mesh collision is non-convex static collision data only.
- Mesh triangle count is within a configured budget.
- The mesh source is not accidentally a dense visual LOD0 mesh unless explicitly allowed.
- Colliders are not scaled in unsupported ways. Full rotation is expected for placed environment props; non-uniform scale and mirrored transforms need explicit support, rejection, or unique baking because projectile-radius math changes under those transforms.
- Dense meshes are rejected by default. Export requires explicit override metadata plus a warning until scene and module-size budgets are proven safe.

## Phase 3: Export Simplified Mesh Collision

For assets where boxes are not close enough, export simplified mesh collision data.

Completed setup:

- Full-rotation query boxes are supported before mesh export. This handles placed/tilted query `BoxCollider`s without converting them to oversized world AABBs.
- Server broadphase indexes `obb_xyz` boxes by derived world AABB, and projectile/LOS narrowphase transforms rays into the box's local axes for deterministic OBB ray tests.
- Invalid `obb_xyz` quaternions are load-time errors, not silently corrected to identity.
- Movement collision exports must not contain `obb_xyz` until movement pushout supports true full-rotation OBBs. Current movement helpers retain a conservative AABB fallback only for synthetic/test data.
- Query `MeshCollider` export writes validated prototype/instance mesh data into query collision JSON under `mesh_geometries` and `mesh_instances`. Mesh geometry vertices remain in mesh-local/prototype-local space, and each scene placement records a transform instance.
- Current mesh validation requires static non-trigger non-convex mesh colliders, readable mesh assets with stable AssetDatabase GUIDs, valid triangle indices, finite vertices/transforms, degenerate-triangle removal, a per-geometry budget of 512 triangles, and a per-scene budget of 50,000 unique mesh-geometry triangles.
- Degenerate-triangle filtering uses the same `|cross(edge_a, edge_b)|^2 <= 1e-12` threshold in the Unity exporter and server parser so exported JSON cannot pass editor cleanup and then fail server preload.
- Server preload parses query mesh geometries and instances, validates the exported buffers, derives local geometry bounds and per-instance world bounds server-side, and reports geometry/instance/triangle counts.
- Projectile/LOS raycasts now query a top-level broadphase over mesh instances, transform candidate rays into mesh-local space, test triangles, and merge the nearest mesh hit with existing box/heightfield/generated-world hits.
- Projectile tick metrics track mesh broadphase candidates, triangle tests, and mesh broadphase full-scan fallbacks separately from box collision counters.
- Current mesh narrowphase is exact ray-vs-triangle. It does not yet sweep projectile radius against triangles; existing box collision still applies projectile radius. Swept-sphere/capsule vs triangle remains a follow-up if arrow/projectile radius needs mesh-edge grazing coverage.

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
- Per-instance transform matrices recorded on instances.
- Vertices in prototype-local space, never pre-transformed for shared prototypes.
- Triangle indices.
- Prototype-local AABB.
- Server-derived instance world AABB, not an exported second source of truth.
- Optional material/surface identifier, if needed later.

Projectile and LOS queries should choose the nearest hit across all enabled world surfaces: query-collision boxes, query-collision meshes, existing terrain/heightfield behavior, and generated open-world geometry. Heightfield export is out of scope for mesh authoring, but heightfield hits remain part of final world-hit selection where they already participate.

The server should not care whether the mesh came from Unity's `MeshCollider`, a child object, or a generated asset. It should only receive baked static collision geometry.

### Mesh Cleanup, Decomposition, and Cooking Policy

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

V-HACD or equivalent decomposition is required to evaluate for this asset set:

- Primary use case: generate optimized movement collision where simple boxes are unusable.
- Secondary use case: generate lower-complexity query geometry if raw/simplified triangle meshes are still too heavy.
- Projectile/LOS queries against static world geometry should still prefer simplified triangle meshes plus acceleration structures when that gives the best fidelity/performance tradeoff.
- Convex decomposition should be an explicit offline/generated-asset step, not an invisible export-time mutation.
- Generated hulls must be deterministic enough for repeatable exports, versioned with the tool/settings that produced them, and validated against budgets.

Candidate generation tools/libraries to evaluate:

- V-HACD, preferably through a Unity editor integration or deterministic command-line build.
- CoACD or another modern convex-decomposition option if it is easier to automate or gives better hull counts.
- Mesh simplification/decimation tools for producing explicit collision meshes before export.
- Manual author-provided collision meshes as the quality baseline for important hero assets.

Generated collision assets should report:

- Source mesh asset GUID and import settings.
- Tool name/version and all decomposition/simplification parameters.
- Hull count, vertices per hull, triangles per generated query mesh, and byte size impact.
- Preview/debug mesh path so designers can inspect exactly what will be exported.
- Whether the result targets movement, projectile/LOS query, or both.

Acceptance criteria for the decomposition path:

- It materially improves fit compared with current boxes.
- It stays inside movement and query performance budgets.
- It avoids excessive hull counts that would make movement collision worse than the original problem.
- It can be regenerated automatically and reviewed in CI or editor validation.
- It does not require hand-editing server JSON.

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
- Store many scene instances as full transforms and object-level bounds.
- Query a top-level world broadphase over instances, transform the query into instance-local/prototype-local space, then query the shared per-prototype mesh acceleration structure.

This mirrors the usual TLAS/BLAS split:

- TLAS: scene instances and their world bounds.
- BLAS: shared collider geometry for one prefab/prototype.

Instance transform policy:

- Initial support should be translation, positive uniform scale, and arbitrary rotation.
- X/Z rotation is expected for rocks and other hand-placed environment props and should not force unique baking by itself.
- The exporter should reject or warn on negative scale, mirrored transforms, and non-uniform scale for shared prototypes, mesh or box, unless it bakes that instance as a unique prototype or the server narrowphase explicitly supports the case.
- TLAS entries must store transformed world AABBs per instance. They must not reuse local prototype AABBs.
- This keeps prototype sharing viable for tilted static props while avoiding local-space swept-sphere correctness issues under arbitrary non-uniform affine transforms.
- Before Phase 3 ships, audit existing Arena environment placements for transform conformity. If many placements use non-uniform or mirrored scale, prototype/instance encoding still works but those outliers need an explicit fallback, such as rejection, unique baking, or a more conservative narrowphase.

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

Completed setup:

- Query meshes now build a deterministic per-geometry triangle BVH at server preload.
- The BVH is shared by all mesh instances for that geometry. Runtime raycasts still use the top-level world broadphase over instance bounds first, then transform the ray into mesh-local space and traverse the shared per-geometry BVH.
- Mesh metrics now distinguish world mesh instance candidates, BVH node tests, triangle tests, and mesh broadphase full-scan fallbacks.
- Server bootstrap logs total mesh BVH node count so the in-memory acceleration structure size is visible alongside geometry and triangle counts.
- Tests compare BVH ray results against the previous linear triangle scan and verify that the BVH prunes triangle tests for representative local queries.

This is a solved-problem area. The in-repo BVH is the deterministic baseline for current static ray queries and for evaluating whether a library is worth the module-size/runtime tradeoff. The project should not hand-roll swept-shape math or broader physics behavior unless a library fails the constraints below.

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
- Handles our transform policy cleanly: query instance world AABB in Arena broadphase, transform query into prototype-local space using the inverse instance transform, run mesh narrowphase, then transform hit back to world space.
- Has acceptable cold-start cost for building per-prototype mesh acceleration structures, or supports precomputed/baked structures later. Mesh acceleration init should follow the same eager-init or measured first-query-latency policy as the box broadphase.
- Is benchmarked against a minimal in-repo triangle-BVH baseline for representative mesh/query workloads before adoption. The baseline is for sizing and risk reduction, not a commitment to hand-roll production collision math.
- The in-repo baseline now exists for exact ray-vs-triangle queries. Any library adoption should beat or materially simplify this baseline while preserving deterministic Arena-level hit behavior.
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
- Do not use raw detailed triangle meshes for player movement. Movement collision should use boxes where acceptable, or generated simplified/compound convex collision where boxes are not acceptable.
- Do not solve explosion occlusion in the first pass.
- Do not export raw third-party prefabs directly.
- Do not require manual edits to server JSON.

## Open Questions for Review

1. Should the projectile/LOS layer be named `GameplayQueryCollision`, `ProjectileCollision`, or something else?
2. What triangle budget should be allowed per query mesh collider and per scene?
3. What hull-count and vertices-per-hull budget should be allowed for generated movement collision?
4. Should arrows keep their current radius-based swept segment behavior, or should they eventually use a thinner raycast with presentation-only forgiveness?
5. Should author `CapsuleCollider`s be exported as first-class server capsule primitives, or converted during export/tooling into boxes or generated hulls?
6. Should movement collision use generated compound convex hulls only, or also allow carefully budgeted simplified concave/static meshes if the server library supports them deterministically?
7. How should debug visualization work for exported projectile/LOS collision, generated movement collision, and server hit results?
8. Should generated open-world colliders get their own broadphase in Phase 1, or is it acceptable as a Phase 1 follow-up after exported gameplay boxes?

## Recommended Next Step

Continue Phase 4 validation: measure representative projectile/LOS query workloads with exported mesh-heavy scenes, then decide whether the in-repo BVH is sufficient or whether Parry/swept-shape support is needed before implementing projectile-radius-aware mesh hits.

That gives an immediate performance improvement, reduces the risk of future mesh support, and creates the candidate-query shape needed for mixed box and mesh collision later.

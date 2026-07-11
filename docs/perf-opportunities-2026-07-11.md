# Performance Opportunities — 2026-07-11

Current performance backlog after re-checking the static survey against the live repository. This is deliberately split into work that is safe now, work that needs profiler evidence, and work that should not be pursued under its original rationale.

Except where an item is marked complete, rankings remain hypotheses until measured. Row and byte sizes are estimates.

## Recommended order

1. Capture one representative busy-fight client profile and one NPC-pack server profile.
2. Land narrow, contract-preserving reductions in known allocation/write churn.
3. Choose larger rendering or netcode projects from measured bottlenecks rather than static rankings.

## Worth doing

### 1. Change-gate `PlayerIntent` writes — complete

Completed 2026-07-11 in `server/src/game_loop.rs` and `server/src/player_intent.rs`.

The table remains the server's authoritative retained movement fallback for movement simulation, stationary-cast validation/cancellation, and facing-sensitive combat behavior. The normal and special-movement tick paths now update it only when retained `forward`, `strafe`, or `yaw` changes. Jump remains queue-only and persistently false. `input_tick` and `updated_at` now identify when the retained fallback last changed; every-tick acknowledgement and command-buffer feedback remain on `PlayerPhysics`.

Lifecycle and explicit reset paths remain unconditional. The existing `writes_player_intent` counter therefore records real row writes rather than attempted per-tick refreshes. This change does not attribute the prior 31 GB local commitlog incident to any one table; table-level byte growth is still unmeasured.

### 2. Pool floating combat text — complete

Completed 2026-07-11 in `Assets/Arena/Runtime/Presentation/FloatingCombatText.cs`.

Damage, healing, and lifecycle labels now reuse a bounded pool instead of creating and destroying a GameObject, `TextMesh`, and animation component for every event. The existing presentation remains unchanged; switching to TMP was intentionally kept out of the performance fix. The owner also resolves the active main camera once and shares it with live text instances rather than each instance querying `Camera.main` every frame.

### 3. Remove repeated NPC template lookup allocations

`npc_template(...)` normalizes the ID, linearly searches the immutable catalog, and clones the complete template (`server/src/npcs.rs:494-508`). That remains on the hostile-NPC tick path even though decision selection itself is now cadence-gated.

- Direction: index normalized template IDs and cache effective immutable templates, including compile-time measurement overrides.
- Verification: compare `npc_combat` CPU and allocation behavior on the NPC-pack fixture before and after. Treat this as a small cleanup unless the capture shows material cost.

### 4. Cache `LocalPlayerMotor` in `PlayerAnimator` — complete

Completed 2026-07-11 in `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs`.

`PlayerAnimator` now resolves `LocalPlayerMotor` lazily through its existing cache. The lazy fallback is required because local-player setup adds the motor after `PlayerAnimator.Initialize()`; once resolved, both grounded-state paths reuse the cached component instead of calling `GetComponent<LocalPlayerMotor>()` every frame.

## Worth doing only if profiling confirms the bottleneck

### Character LODs

Arena-owned modular characters do not currently author `LODGroup`s. Character LODs are likely useful at crowd scale, but they should be evaluated separately from mesh combining.

Mesh combining is a larger compatibility project: the runtime can rebuild an existing avatar when appearance/equipment changes, and combining meshes does not collapse multiple material/submesh passes into one draw call without compatible material consolidation or atlasing. Measure skinning time, draw calls, triangles, and material passes by distance before choosing the implementation.

### HUD canvas isolation

The main HUD uses one root canvas, while cast-bar and cooldown fills change every combat frame (`HUDController.cs:1507,2459`). If a representative capture shows material `Canvas.BuildBatch`/UI rebuild time, isolate animated regions behind nested canvases or evaluate shader-driven fills. Do not restructure the HUD from static inference alone.

### Area-spell aim cursor hot path — complete

Completed 2026-07-11 in `TargetSelector.cs` and `AimIndicator.cs`.

Spell aim now owns the unlocked cursor without also running the entity-hover scan. `SpellInputHandler` is the single aim-point refresh owner, eliminating the duplicate per-frame surface ray. The terrain-conforming indicator retains its 144-segment/32-band presentation mesh, but reuses persistent mesh buffers and topology while sampling four radial terrain rings and interpolating the remaining visual bands. This removes per-moving-frame mesh-array churn and reduces ground-height queries from 4,785 to 576 per rebuild without flattening the indicator onto uneven ground.

### Melee interruption ghost pooling

`MeleeAnimationGhostLayer` bakes the current animated pose into new mesh/material resources for interruption ghosts. If allocation captures identify this path, reuse mesh and material containers. A fresh `BakeMesh` operation is still required to capture each distinct pose; one cached static baked mesh is not a correct replacement.

### VFX Graph projectile pooling

`ProjectileVfxPool` intentionally bypasses prefabs containing `VisualEffect` because their state is not currently reset safely (`ProjectileVfxPool.cs:117-123`). Add an explicit reset/reinitialization contract only if those prefabs produce meaningful instantiate/destroy spikes. Cache component arrays inside each rental if rent/return allocations appear in the capture.

### SpacetimeDB `FrameTick` and binding deserialization

`NetworkManager.Update` dispatches pending network messages through `FrameTick()` on the main thread (`NetworkManager.cs:490-499`). Capture callback counts, deserialization time, and row types before attempting generated-binding or subscription changes.

## Deferred projects

### Idle `PlayerPhysics` write gating

`PlayerPhysics` is still written every tick and is the main replicated movement row. It also carries the local prediction acknowledgment, command-consumption truth, buffer occupancy, and interpolation timestamp. Change-gating position therefore requires a deliberate split or replacement contract for those every-tick signals, plus changes to the client's `SourceRowCadence` handling. Do not treat this as a simple idle-row guard.

### Distance/relevance interest management

Subscriptions are scoped by world/instance rather than distance (`GameplaySubscriptionPlanner.cs:108-151`). Distance or relevance filtering matters for larger populations and competitive information exposure, but it needs a feasible SpacetimeDB subscription design within the two-table-semijoin constraint. Defer until scale or exposure makes it a priority.

### Aura and status scan cleanup

`tick_auras` and `StatusRuntimeView::collect` perform repeated scans, but they are already visible in tick profiling and are likely small at current populations. Optimize only when the corresponding subphase/counters are material.

## Completed or rejected recommendations

### NPC decision throttling — complete

The earlier survey said `decision_interval_ms` and `perception_radius` were unused. That became stale immediately after the survey. NPC combat now preserves committed targets/actions until `next_decision_at`, uses the authored interval and deterministic variation, and queries candidates through a shared perception index (`server/src/npcs.rs:1352-1455,1592-1652`). Keep chase/facing execution per tick; do not add a second throttle path.

### Blanket `MaterialPropertyBlock` conversion — rejected

Do not convert material instances to `MaterialPropertyBlock` under an “SRP batching” rationale. In Unity 6 URP, MPBs remove SRP Batcher compatibility. Material work must distinguish:

- implicit material cloning through `Renderer.material` getters;
- explicitly owned procedural VFX materials;
- ordinary draw-call batching and GPU instancing;
- SRP Batcher shader/material compatibility.

Profile the actual renderer/shader path before changing it. Shared material variants, instanced shader data, or pooled owned materials may each be appropriate in different sites.

### Disable additional-light shadows globally — rejected

The PC render-pipeline asset enables additional-light shadows, and shipped scenes such as `Adventure_Island.unity` contain shadow-casting point lights. Disabling the global setting is a visual-quality change, not a free memory cleanup. Audit the active scene lights and shadow atlas in a capture before changing per-light or pipeline settings.

### Remove `PlayerIntent` — rejected

Clients do not subscribe to the table, but server simulation and spell rules read it. Only its redundant updates are candidates for removal.

### Shared `Camera.main` provider as a performance project — rejected

Modern Unity caches MainCamera-tagged objects internally. Local caching in components with hot repeated access is fine, but a new global provider is not justified from static analysis.

### Broad TMP migration for floating text — rejected

Pooling removes the known GameObject/component lifetime churn without coupling the fix to a visual/text-system migration. Evaluate TMP separately if text rendering quality, glyph behavior, or measured rendering cost warrants it.

## Measurement gates

### Client

Capture a representative busy fight and record:

- main-thread and render-thread frame time;
- GC allocation rate and allocation call stacks;
- `Canvas.BuildBatch`/`WillRenderCanvases`;
- skinning/animator cost, character draw calls, triangles, and material passes;
- `FrameTick` plus callback/deserialization cost;
- interruption ghost and projectile instantiation paths.

### Server

Use an `ARENA_PROFILE_TICKS` build and the NPC-pack fixture. Record:

- `npc_combat` p95/max and `npc_target_pairs_scanned`;
- table-write counters, especially `player_intent` and `player_physics`;
- status/aura subphase time and scan counts;
- allocation evidence where the local runtime exposes it.

### Commitlog

Measure GB/hour by workload and, if possible, attribute bytes by table. Existing counters report write counts rather than serialized byte volume, and `npc_physics` is not currently among the instrumented write kinds.

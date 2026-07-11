# Performance Opportunities — 2026-07-11

Full-stack survey (client rendering, client CPU, server tick loop, netcode data volume). All rankings are from static analysis — **nothing here has been measured live yet**; see "Measure first" at the end. Row/byte sizes are estimates.

## Client wins (ranked)

### 1. Merge modular character meshes + add LODs

Players are assembled from body/head/face/hair/eyes/outfit/equipment parts as **separate SkinnedMeshRenderers** — `NHAvatar.Compile()` only reassigns `sharedMaterials`, it never merges meshes (`Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Scripts/NHAvatar.cs:141-159`, assembly at `CharacterAvatarAssembler.cs:105-111`). That is N skinning passes + N draw calls per character, multiplied by crowd size. There are **zero LODGroups** in the project, so distant avatars cost the same as near ones.

- Fix: mesh-combine on `Compile()` (or a skinned-mesh combiner), and add character LODs. Biggest structural GPU/draw-call lever in the project.

### 2. Pool floating combat text

Every damage/heal number allocates a new GameObject + legacy `TextMesh` + `FloatingTextAnimation` + interpolated strings (`Assets/Arena/Runtime/Presentation/FloatingCombatText.cs:87,123-137`), calls `Camera.main` (a tagged-object search) every frame while alive (`:168`), then `Destroy`s itself (`:160`). Dominant GC-spike source during fights.

- Fix: pool instances, switch to TMP, cache the camera reference.

### 3. Stop whole-HUD canvas rebuilds during combat

The HUD is one canvas with no nested sub-canvases (`Assets/Arena/Runtime/UI/HUDController.cs:281`). Cast-bar fill (`:2459`) and cooldown/GCD sweeps (`:1507`) mutate `Image.fillAmount` every combat frame, dirtying the entire canvas each frame. The existing `SetTextIfChanged`/`SetActiveIfChanged` guards (`:2757,2769`) can't help against a moving fill.

- Fix: move the animated fills onto a nested sub-canvas, or drive fills via material/shader instead of `fillAmount`.

### 4. Fix SRP-batch breakers and material cloning

Zero `MaterialPropertyBlock` usage anywhere; 12 `.material` (instance-clone) sites: `Match/MatchController.cs:258`, `Presentation/WorldHealthBar.cs:54,66`, `Presentation/MeleeRangeGuideIndicator.cs:92`, and all procedural VFX (`FireballVFX.cs:70,82`, `IcicleVFX.cs:65,75`, `BeamVFX.cs:112`, `NegateVFX.cs:73`, `ImpactBurstVFX.cs:31`, `VFXUtils.cs:185`). Each clone breaks SRP batching and allocates.

Worst case: `Presentation/MeleeAnimationGhostLayer.cs:144-168` — every interrupted melee does `SkinnedMeshRenderer.BakeMesh` into a **new Mesh** plus **new Material per submaterial**, no caching, up to 3 ghosts, destroyed after 0.75 s, with per-frame `SetColor` on the clones (`:359-375`). (`AnimatedAutoAttackGhostLayer` caches its clone correctly — use it as the model.)

- Fix: MPB for tints/fades; cache ghost meshes/materials like the auto-attack layer does.

### 5. Smaller cleanups

- `ProjectileVfxPool` silently bypasses pooling for any prefab containing a `VisualEffect` — those Instantiate/Destroy every cast (`Assets/Arena/Runtime/Presentation/VFX/ProjectileVfxPool.cs:120-123`). Rent path also allocates via `GetComponentsInChildren` (`:127,134,141`).
- `Camera.main` per frame in ~8 live components (`VFXUtils.cs:354-357` billboards, `SelectedTargetIndicator.cs:176`, `AimIndicator.cs:116`, `LocalPlayerCamera.cs:52`, `CameraOrbitController.cs:100`, `TargetSelector.cs:168,198`). Cache in a shared provider.
- `TargetSelector` hover scan: full players+NPCs scan with 4–5 `WorldToScreenPoint` per entity per frame while the cursor is unlocked (`Combat/TargetSelector.cs:196-223`), plus `RefreshTargetingPresentation()` every frame (`:136`). Gate/throttle the recompute.
- `PlayerAnimator.Update` calls `GetComponent<LocalPlayerMotor>()` every frame (`PlayerAnimator.cs:2708`) — local player only, but free to cache.
- Additional-light shadows are enabled with a 2048 atlas on the PC renderer (`PC_RPAsset.asset:47-50`) but the only runtime point lights (projectile VFX) have shadows off — disable to reclaim memory/prefiltering.

### Unknown pending profiler

SpacetimeDB `FrameTick()` callback dispatch + generated-binding row deserialization (`Network/NetworkManager.cs:494`) scales with moving-entity count × 30 Hz and is invisible to static analysis. Most likely hidden client cost; capture before trusting the ranking above.

## Server wins (ranked)

### 1. Wire up the NPC decision throttle that already exists

`decision_interval_ms` and `perception_radius` are authored, validated, and persisted on NPC templates (`server/src/npcs.rs:141-142`, validation `:564-572`) but **never read at runtime**. Every hostile NPC re-scans targets and re-selects actions at the full 30 Hz (`tick_npc_combat`, `npcs.rs:1235`) — O(NPCs × alive players) per tick. Chase motion must stay per-tick for smoothness; re-acquisition/selection does not.

- Fix: gate re-scan/selection on the authored cadence. Payoff is already measurable via `npc_target_pairs_scanned` (`npcs.rs:1481`) and the `npc_combat` subphase timer.

### 2. Kill per-tick template clones and world-context allocations

- `npc_template(...).clone()` clones the whole template — including the `Vec<NpcActionKitEntry>` of 6 Strings each — once per hostile NPC per tick (`npcs.rs:1262`, struct `:477`). Runtime-immutable data; cache or borrow it.
- `resolve_player_world_context` per NPC does a wasted `player_world().find()` miss then an `npc_instance().find()`, returning an owned scene-name String per NPC and per candidate every tick (`server/src/arena.rs:978-1010`, callers `npcs.rs:1432,1464`).
- `normalize_id` allocates via `to_ascii_uppercase` on the template-lookup and action-selection paths (`npcs.rs:2358`).

### 3. Stop persisting `PlayerIntent` at 30 Hz

Written every tick for every live player (`server/src/game_loop.rs:1923-1924`, also `:1706`) but present in **no client subscription** (absent from `GameplaySubscriptionPlanner`) — pure commitlog churn, roughly doubling per-player row writes for zero wire benefit. Likely a major contributor to the 31 GB commitlog incident (2026-07-05, `docs/netcode-open-items.md:99-103`).

- Fix: gate on change or drop it from the persistent path entirely.

### 4. Gate idle-player `PlayerPhysics` writes

`commit_player_physics` always `.update()`s at 30 Hz per live player even when standing still (`game_loop.rs:1945`, `server/src/player_physics.rs:162`) — the top combined wire + commitlog stream (~80 B × 30 Hz × players). Harder than the NPC gate because `last_processed_tick` (the input ack) genuinely changes every tick — gating requires splitting the ack out of the position row. The client already tolerates lower cadence: `RemotePresentationBuffer` handles change-gated NPCs today (`SourceRowCadence`, `Assets/Arena/Runtime/Simulation/RemotePresentationBuffer.cs:75-89`), so players would move off `EveryTick`.

### 5. Distance/relevance interest filtering (deferred, known)

Subscriptions are scoped by world/instance but not by range — every entity in a scene replicates to everyone at full rate (`GameplaySubscriptionPlanner.cs:108-151`; already tracked in `docs/netcode-open-items.md:85-87`). Matters as scene populations grow, not before. Mind the SpacetimeDB two-table-semijoin limit.

### 6. Minor tick-loop trims

- `tick_auras` runs unconditionally every tick with 3 full scans and no empty early-out (`server/src/combat.rs:1217-1234`).
- `StatusRuntimeView::collect` full-scans `status_effect` twice per tick (pre/post mutation, `combat.rs:5220`, `game_loop.rs:853,1032`) — already instrumented; likely negligible until status counts grow.

## Already well-optimized — don't redo

- `world_collision.rs`: parse-once `OnceLock` geometry, uniform-grid broadphase + BVH, thread-local scratch, fallback-scan warnings at init. Best subsystem in the codebase.
- Single batched `game_tick` with anchor-based rescheduling + watchdog; no per-entity scheduler rows; `has_active_*`/`has_due_*` gating keeps idle load low; T1–T4 tick-audit optimizations landed.
- NPC AI fires zero raycasts (targeting is pure distance); NPC physics/facing writes are change-gated.
- Client: `RemotePresentationBuffer` 12-slot ring interpolation with zero per-push allocation; no per-row GameObject churn; projectile VFX pooling; scoped (not subscribe-all) subscriptions; hot/cold table split already done server-side.

## Measure first

Nothing above has live numbers. Before spending implementation effort:

1. **Client:** Unity profiler capture during a busy fight — canvas rebuild (`Canvas.BuildBatch`/`WillRenderCanvases`), GC alloc rate, animator eval per avatar (incl. ghost clones), `FrameTick` deserialization.
2. **Server:** `ARENA_PROFILE_TICKS` build + NPC-pack fixture (aggro knobs `ARENA_NPC_AGGRO_RADIUS`/`ARENA_NPC_TANKY`, `npcs.rs:451,948`), reading `npc_combat` subphase p95/max and `npc_target_pairs_scanned`.
3. **Commitlog:** GB/hour growth split by table has never been measured; write counters exist (`tick_metrics.rs:132-154`) but `npc_physics` isn't among them and byte volume isn't tracked.

## Quick wins vs projects

- Near-free: FCT pooling (#C2), `PlayerIntent` gating (#S3), `Camera.main` caching, template-clone caching (#S2).
- Big projects: character mesh merging + LODs (#C1), NPC decision throttling (#S1), idle `PlayerPhysics` gating (#S4), interest filtering (#S5).

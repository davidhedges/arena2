# Client/Server Sync Risk Fixes - 2026-05-11

## Purpose

This note explains three client/server sync risks found in the current Arena codebase and proposes fixes for each. The goal is to remove hidden dual ownership: any value that changes gameplay, movement prediction, authoritative scene selection, or network-visible timing should have one clear source of truth.

## 1. Open-World Scene Membership Is Current World Context

### Original Problem

The server already stored open-world scene selection per player through `player_open_world_scene`, falling back to `Oasis_Day` when no row existed. That meant the intended model was not "one global open-world scene." Different players could be in different open-world scenes, and players who chose the same scene should still see and interact with each other.

The previous world-context model did not consistently carry that scene identity. `PlayerWorld` distinguished only `OPEN` versus `INSTANCE(instance_id)`, and `players_share_world_context` treated any two `OPEN` players as sharing context, regardless of their `player_open_world_scene` rows.

That could produce a real runtime split:

- two players in different open-world scenes can pass server world-context checks because both are just `OPEN`
- if client scene loading/prediction and server membership drift, the client can appear to collide with or present data from the wrong open-world context
- combat, projectiles, scene queries, subscriptions, and presentation can include actors from the wrong open-world scene

### Completed Fix

Make open-world scene name first-class in current world context:

```text
WorldContext =
  OPEN(scene_name)
  INSTANCE(instance_id)
```

Players in the same open-world scene share context. Players in different open-world scenes do not. Arena instances keep the existing instance-id rule.

`PlayerWorld` now carries the current open-world scene while `world_kind = OPEN`, and stores an empty scene string while `world_kind = INSTANCE`. `player_open_world_scene` remains as sticky scene preference/return memory so entering and leaving an instance preserves the user's last chosen open-world scene.

All `PlayerWorld` writes now go through `upsert_player_world`, which enforces those invariants. The client uses `PlayerWorld.open_world_scene_name` for current subscription scope, while `PlayerOpenWorldScene.scene_name` remains sticky preference/return memory. Unity scene loading and open-world movement prediction remain tied to the active Unity scene, preserving the original client ownership boundary.

### Status

Complete. Implemented work:

- Added `open_world_scene_name` to `PlayerWorld` and regenerated SpacetimeDB C# bindings.
- Kept `player_open_world_scene` as sticky preference. `SetOpenWorldScene(sceneName)` updates that preference and, if the player is currently open-world, updates `PlayerWorld.open_world_scene_name` too.
- Routed `PlayerWorld` writes through `upsert_player_world`, deriving the current scene from `player_open_world_scene` for `OPEN` rows and clearing it for `INSTANCE` rows.
- Updated `players_share_world_context` so two `OPEN` players share context only when their current `PlayerWorld.open_world_scene_name` values match.
- Updated gameplay subscription scope from generic `OpenWorld` to `OpenWorld(scene_name)`, filtering directly through `PlayerWorld.open_world_scene_name`.
- Scoped client subscriptions use the typed `PlayerWorld JOIN <scoped table>` query-builder form; avoid raw `IN (SELECT ...)` subqueries because the SpacetimeDB subscription path rejects that expression shape.
- Kept `PlayerWorld.open_world_scene_name` as a non-null string because the C# query builder emits plain string literals; nullable `Option<String>` columns require option literals that the generated binding does not express correctly in subscription filters.
- Kept `LocalWorldRuntimeCoordinator` from loading open-world scenes while in Hub/non-gameplay scenes; Hub destination buttons own direct Unity scene loading after sending `SetOpenWorldScene(sceneName)`.
- Kept open-world prediction on `SceneManager.GetActiveScene().name`, so the collision profile follows the Unity scene that is actually loaded.
- Updated `LocalWorldRuntimeCoordinator` so sticky `PlayerOpenWorldScene` preference updates no longer trigger scene refreshes when an authoritative `PlayerWorld` row is already hydrated. This prevents a destination change from loading the new scene, then immediately reloading the stale old scene while waiting for the `PlayerWorld` update.
- Removed generated gameplay collision from the ToonEnchantedMeadow background mountain variants, updated the variant-generation settings so that background category is skipped, and removed the already-exported `Background/*` boxes from Docks/Idol shared collision data. The client/server runtime no longer has a special `Background/*` collision filter.
- Changed open-world spawn resolution to start from terrain/profile ground and only accept low step-up surfaces, so high gameplay boxes cannot become spawn ground.
- Changed the client default open-world scene to match the server default, `Oasis_Day`.
- Restored the existing single shared open-world dummy sync path: `SetOpenWorldScene(sceneName)` moves the dummy to that scene and updates the dummy's `PlayerWorld.open_world_scene_name`, so scoped subscriptions include it in the selected open world.
- Avoided reading `PlayerPrefs` from `NetworkManager` field initialization; Unity rejects that API during MonoBehaviour construction.
- Guarded melee action dispatch against local or selected-target `PlayerEntity` references whose Unity `GameObject` was destroyed during a scene/world transition.

Current row invariant:

```text
OPEN => instance_id = null, open_world_scene_name = scene_name
INSTANCE => instance_id = instance_id, open_world_scene_name = ""
```

Non-blocking follow-up:

1. Consider adding a timeout/error affordance if `SetOpenWorldScene(sceneName)` fails after Hub has optimistically loaded the selected scene.
2. Replace the single open-world dummy with scene-scoped dummies or scene-scoped practice actors if simultaneous practice targets are needed in every open-world scene.
3. Add a cross-language or source-parsing parity check so client registered scenes stay matched with `KNOWN_OPEN_WORLD_SCENES` in `server/src/open_world_scene.rs`.

### Validation

- Added Rust tests for world-context comparison: same open scene returns true, different open scenes returns false, same instance returns true, different instances returns false.
- Ran `spacetime generate --yes --lang csharp --module-path server --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB`.
- Ran `cargo check --manifest-path server/Cargo.toml`.
- Ran `cargo test --manifest-path server/Cargo.toml context_requires`.
- Ran `dotnet build Assembly-CSharp.csproj --no-restore`.
- Ran `dotnet build Arena.EditModeTests.csproj --no-restore`.
- Updated editor coverage to assert open-world scoped queries include `PlayerWorld.open_world_scene_name`, use join SQL, and do not emit `IN (SELECT ...)`.
- Added an edit-mode test where a sticky preference update arrives for a new open-world scene while the authoritative `PlayerWorld` row still points at the old scene; the coordinator must not reload the stale old scene.
- Added an edit-mode test where Hub does not load open-world scenes from `LocalWorldRuntimeCoordinator`; Hub UI owns that direct scene load.
- Added decider coverage that an already loaded registered open-world scene is preserved even if the authoritative row changes during transition.
- Added server regression coverage that Docks/Idol/Great Hall/Temple spawn resolution ignores high gameplay box tops.
- Added edit-mode regression coverage that ToonEnchantedMeadow background variants do not carry generated `ArenaGameplayCollision` children and shared open-world collision data does not contain `Background/*` gameplay boxes.
- Verified shared Docks/Idol collision JSON stays byte-identical between `server/src/world_data` and `Assets/Arena/Resources/SharedData/Worlds`.
- Ran `cargo test --manifest-path server/Cargo.toml open_world`.
- Re-ran `dotnet build Assembly-CSharp.csproj --no-restore` after transition destroyed-entity guards.
- Re-ran `dotnet build Arena.EditModeTests.csproj --no-restore` after transition destroyed-entity guards.

## 2. Weapon Projectile Defaults Can Drift On Export

### Problem

Weapon projectile authoring says `0 uses the projectile catalog value`, and the Rust server does that: projectile fields deserialize as optional values, and `0`/missing values fall back to `CombatProjectileDefinition`.

`ARROW_STANDARD` on the server has `update_interval_seconds = 0.10`. Before this fix, Unity export resolved unset projectile values through fallback helpers; `ProjectileUpdateIntervalSecondsOrDefault` returned `0.05`, so an unset Unity field could become a concrete manifest override.

The previous `server/src/melee_manifest.shared.json` contained mixed rows: some arrows used `0` and got the server fallback, while others explicitly used `0.05`.

### Completed Fix

Preserve `0` as "use projectile catalog" in exports. Unity authoring should not materialize fallback defaults into `melee_manifest.shared.json` unless the designer authored a positive override.

### Status

Complete. Implemented work:

- Changed `CombatAnimationSet.BuildExportStrike(...)` so projectile numeric fields export positive authored overrides only; unset, zero, negative, NaN, and infinity export as `0`.
- Kept `ProjectileIdOrDefault` because a missing projectile id still needs a stable catalog id.
- Removed the export use of resolved fallback helpers such as `ProjectileUpdateIntervalSecondsOrDefault`, so the server catalog owns default cadence.
- Normalized `server/src/melee_manifest.shared.json` so Archer projectile rows export numeric override fields as `0`, inheriting `ARROW_STANDARD`.
- Removed stale generated projectile blocks from non-archer manifest profiles and from `ARCHER_RAIN_SHOT`, matching the current authored animation set assets.

### Validation

- Added edit-mode coverage that Archer projectile export keeps numeric projectile override fields at `0` so they inherit catalog values.
- Added Rust coverage that missing, zero, negative, and NaN projectile override values inherit the catalog value while positive values remain explicit overrides.
- Added Rust coverage that checked-in Archer manifest projectile rows reference `ARROW_STANDARD` and keep numeric overrides at `0`.
- Verified non-Archer manifest profiles do not author projectile delivery.
- Ran `cargo test --manifest-path server/Cargo.toml projectile -- --nocapture`.
- Ran `cargo test --manifest-path server/Cargo.toml non_archer_profiles_do_not_author_projectile_delivery -- --nocapture`.
- Ran `dotnet build Assembly-CSharp.csproj --no-restore`.
- Ran `dotnet build Arena.EditModeTests.csproj --no-restore`.

## 3. Cast-Speed Scaling Can Break Release Presentation Timing

### Problem

Server cast duration is not always `gameplay.cast_time_ms`. It is scaled by derived and temporary cast-speed multipliers. The client schedules release presentation using:

```text
releaseStartMs = activeCast.endsAtMs - authoredReleaseOffsetMs
```

That works when the scaled cast duration is at least as long as the authored release offset. The editor validator only checks the release offset against unscaled `gameplay.cast_time_ms`, so a high cast-speed multiplier can make the scaled cast shorter than the animation's release offset.

In that case the client starts the release immediately or late relative to the actual server release, causing presentation to drift from the authoritative `ActiveCast.ends_at` / `COMBAT_RELEASE` timing.

`UNTIL_RELEASE_EVENT` now fixes lifetime cleanup for cast VFX that should persist until server release, but it does not fix release animation scheduling. `SpellCastPresentationController` still needs to clamp or scale the authored release offset against `ActiveCast.started_at` / `ActiveCast.ends_at`.

### Completed Fix

Release scheduling now uses the authoritative active cast window. The authored release point still comes from normalized clip timing in `CombatAnimationSet`, but the resolved offset is clamped against `ActiveCast.started_at` / `ActiveCast.ends_at` before scheduling.

```text
castDurationMs = max(0, active.EndsAtMs - active.StartedAtMs)
effectiveReleaseOffsetMs = min(authoredReleaseOffsetMs, castDurationMs)
releaseStartMs = active.EndsAtMs - effectiveReleaseOffsetMs
```

This preserves normal authored timing when the scaled cast duration has enough room, while preventing high cast speed from scheduling release animation before the server cast began.

### Status

Complete. Implemented work:

- Added `SpellCastPresentationController.ComputeReleaseStartMs(...)` to centralize the server-window clamp.
- Added comments around the timing conversion: normalized clip timing is authoring data; `ActiveCast` timestamps are authoritative scaled server timing.
- Reused the existing `CombatAnimationRemoteTiming` catch-up policy for spell release playback, capped before the authored release point so late/clamped starts do not skip the hand-release pose.
- Extended full-body spell playback to start at a normalized offset when catch-up is applied; the existing trigger path remains unchanged for normal zero-offset starts.
- Kept `COMBAT_RELEASE` as the authoritative gameplay/VFX release fact. This change affects animation presentation timing only.

Remaining follow-up:

1. Extend `CombatVFXAuthoringValidator` with a worst-case cast-speed assumption from progression stat scaling, or add a simple minimum-duration guard. The validator should warn when a release offset has no room under plausible cast-speed scaling.
2. Consider server-publishing the scaled cast duration or cast-speed multiplier only if future presentation needs more than `started_at`/`ends_at`. Today those timestamps are enough to compute the real duration.

### Validation

- Added edit-mode coverage for normal release scheduling where the authored offset fits inside the cast window.
- Added edit-mode coverage where `ends_at - started_at` is shorter than the authored release offset and scheduling clamps to `started_at`, not earlier.
- Added edit-mode coverage that invalid authored offsets schedule immediate release at `ends_at`.
- Added remote-timing coverage that spell release requests can use catch-up before the authored release point.
- Ran `dotnet build Assembly-CSharp.csproj --no-restore`.
- Ran `dotnet build Arena.EditModeTests.csproj --no-restore`.

## Cross-Cutting Guardrails

- Keep copied shared JSON files byte-identical between `server/src` and `Assets/Arena/Resources/SharedData`.
- Add a CI/editor check for open-world scene profile parity, because scene names, data keys, spawn positions, and fallback-collider flags are currently hardcoded in both C# and Rust.
- Treat Unity-authored manifests as generated server inputs. Export tools should preserve "inherit catalog default" sentinels instead of writing resolved fallback values.
- Prefer server-published rows over local preferences for anything that affects prediction, scene selection, ability behavior, or authoritative presentation timing.

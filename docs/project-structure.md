# Project Structure

This Unity project keeps first-party game work separate from imported vendor content.

## Assets

```text
Assets/
  Arena/
    Runtime/        First-party runtime C# code.
    Editor/         First-party Unity editor tools and editor-only authoring inputs.
    Tests/          Unity edit-mode tests.
    Content/        First-party authored Unity content.
    Resources/      Runtime-loaded first-party assets used by Resources.Load.

  ThirdParty/
    AssetStore/     Imported Asset Store packs, grouped by domain.
    Unity/          Unity sample/package content kept in Assets.

  Recovery/         Unity recovery scenes and crash/session recovery artifacts.
  _Recovery/        Additional Unity recovery artifacts.
```

## First-Party Content

```text
Assets/Arena/Content/
  Animation/        Animator controllers, masks, and slot clips.
  Art/              First-party generated or authored art.
  Input/            Input action assets.
  Prefabs/          First-party authored prefabs.
  Scenes/           Build, open-world, and development scenes.
    Authoring/      Authoring/demo scenes (shader demos, art review). Excluded
                    from ArenaRuntimeSceneGate, so play mode there does not
                    boot the networked Arena runtime. Never added to builds.
  Settings/         Render pipeline and project content settings.
  Shaders/          First-party shaders and shader graphs.
  UI/               UI Toolkit design-system assets: Art/ (generated theme
                    art, ops/gen_ui_theme_art.py) and Fonts/ (OFL serif faces).
```

### Multiplayer map identity

`Assets/Arena/Content/Scenes/Arena_Map_01.unity` is an authored map, not a
game mode. Its stable catalog identity is `ARENA_MAP_01` in `ArenaMapCatalog`.
Competitive PvP and the temporary Survival mode currently select the same map
asset, but retain separate server rules, lifecycle, actors, and subscriptions.
Removing Survival must therefore not remove or rename the map.

The retired `ArenaMatch` example scene is not a runtime or authoring dependency.
The map's immutable runtime inputs are checked in under
`server/src/map_data/arena_map_01.*.shared.json` and mirrored under
`Assets/Arena/Resources/SharedData/Maps/`. The layout describes a flat movement
surface but imposes no invisible deck-edge boundary. The authored deck is 60 by
60 metres and its four entrances extend past that footprint. Movement is
blocked only by explicit exported collision; its movement and query exports
currently contain no blockers. The old global `arena_layout` and
`gameplay_collision` files have been retired.

The Hub assignment, provisioner bootstrap, match configuration, and
`ArenaInstance` all carry the selected stable map ID. Unity resolves that ID to
the scene and the matching layout/collision resources through `ArenaMapCatalog`.
To add another authored map, add matching entries to the Rust and Unity map
catalogs and check in its `<data-key>.layout`, `<data-key>.collision`, and
`<data-key>.query_collision` files; do not add a mode-specific scene switch.

Authored arena collision uses the same deterministic shared bake as Random
Dungeon. Only explicit objects on the `GameplayCollision` layer are exported;
ordinary visual colliders are not runtime authority. `Arena_Map_01` deliberately
has no such proxies today. Rebuilding it records a collision revision and emits
the paired server/client files. The editor command
`Arena/Maps/Repair Arena Map 01 Collision From Saved Scene` re-exports a saved
scene after a stale revision is detected. Movement and query files stay
separate so a future map can use cheap movement hulls and richer line-of-sight
geometry without changing the runtime contract. The local Play gate checks the
revision and repairs/publishes to the direct-local database before entering Play
when needed. It does not refresh the Hub or cached disposable-match artifacts;
use the local multiplayer setup workflow below for Hub-created sessions.

UI Toolkit runtime assets (UXML/USS/theme, loaded via `Resources.Load`) live in
`Assets/Arena/Resources/UI/Toolkit/`; web prototype specs live in
`docs/ui-prototypes/`. See `docs/ui-toolkit-workflow.md` for the pipeline.

## Runtime Data

`Assets/Arena/Resources/` is intentionally narrow. Put assets here only when runtime code loads them through `Resources.Load` or `Resources.LoadAll`.

Current important resource folders:

```text
ActionProfiles/
CharacterAppearance/
CharacterAvatarBases/
CombatAnimationSets/
CombatVFX/
SharedData/
UI/
```

## Movement Runtime

Verified against production source on 2026-09-04. This describes the implemented
movement path; the two [original migration](../plans/movement-netcode-architecture.md)
and [follow-up](../plans/movement-netcode-followup-plan.md) plans are historical,
not an outstanding implementation queue.

Ordinary player locomotion uses a **33 ms fixed simulation step**. The server
constant is `FIXED_TICK_MILLIS` in [movement.rs](../server/src/movement.rs);
[MovementNetcodeConfig](../Assets/Arena/Runtime/Input/MovementNetcodeConfig.cs)
mirrors it for client prediction and replay. These are separate Rust/C#
definitions, not one generated constant. Rendering runs at frame rate.

| Boundary | Current implementation |
| --- | --- |
| Local input | [LocalPlayerMotor](../Assets/Arena/Runtime/Input/LocalPlayerMotor.cs) samples input/facing and retains a jump press until tick sampling. It reads predicted grounded state; it does not integrate position with `CharacterController.Move`. |
| Command creation and prediction | [LocalMovementPredictionDriver](../Assets/Arena/Runtime/Input/LocalMovementPredictionDriver.cs) authors commands with increasing `InputTick` values in the bounded [MovementCommandBuffer](../Assets/Arena/Runtime/Input/MovementCommandBuffer.cs), then calls `MovementPrediction.Step` for each authored tick. |
| Transport and pacing | [MovementNetDriver](../Assets/Arena/Runtime/Input/MovementNetDriver.cs) sends unsent commands and prunes acknowledged ticks. It feeds command-consumption and buffer-occupancy feedback into [InputLeadController](../Assets/Arena/Runtime/Input/InputLeadController.cs); the prediction driver uses that lead to pace authoring. |
| Server receive and simulation | `send_movement_intent` in [movement.rs](../server/src/movement.rs) validates and buffers commands in [player_input.rs](../server/src/player_input.rs). `tick_player` in [game_loop.rs](../server/src/game_loop.rs) advances one input tick during ordinary locomotion and publishes `PlayerPhysics.last_processed_tick` with consumption/buffer feedback. |
| Authoritative correction | [LocalMovementPredictor.Rebuild](../Assets/Arena/Runtime/Input/LocalMovementPredictor.cs) starts from the authoritative pose and ack, then replays buffered commands with later input ticks using the fixed step and movement-context history. |
| Local presentation | [EntityRegistry.SetupLocalPlayer](../Assets/Arena/Runtime/Entity/EntityRegistry.cs) wires prediction separately from `LocalPresentationDriver` in [LocalPlayerCamera.cs](../Assets/Arena/Runtime/Presentation/LocalPlayerCamera.cs). [PlayerEntity](../Assets/Arena/Runtime/Entity/PlayerEntity.cs) creates the presentation root and moves the camera target beneath it. Small reconciliation displacements become a decaying visual offset; larger ones clear that offset. |
| Remote presentation | [PlayerView](../Assets/Arena/Runtime/Presentation/PlayerView.cs) uses `ClientSimulationState` and [RemotePresentationBuffer](../Assets/Arena/Runtime/Simulation/RemotePresentationBuffer.cs) to interpolate ordinary movement snapshots with bounded velocity extrapolation. Special-movement tracks have a separate sampling path; remote players do not run local input replay. |

The tick contract includes deliberate fallback behavior. If the next command
is absent, `tick_player` advances the ack using retained axes/facing with
`jump = false`; [PlayerIntent](../server/src/player_intent.rs) is that fallback
state, not a replacement for the command queue. The receive path can also
preserve a just-late jump by moving or merging its edge into the next tick.
Special movement and lifecycle transitions have explicit handoff/reset paths;
the ordinary locomotion description is not an exclusive-writer claim for all
physics updates.

Movement blocking, speed multipliers and capsule dimensions reach prediction
through [ClientSimulationState.GetMovementContextForTick](../Assets/Arena/Runtime/Simulation/ClientSimulationState.cs).
It selects authoritative context by tick and composes predicted restrictions.
Default unblocked/1.0-speed values remain as a fallback when context history is
missing, rather than being the normal hardcoded prediction context.

The kinematic implementations remain separate: server
`simulate_non_dummy_player_kinematics` in `game_loop.rs` and client
[MovementPrediction.Step](../Assets/Arena/Runtime/Input/MovementPrediction.cs).
[LocalMovementWorldContext](../Assets/Arena/Runtime/Input/LocalMovementWorldContext.cs)
selects the client's world-specific collision environment. Shared collision
inputs and mirrored constants do not by themselves prove parity in every scene.

[MovementRegressionTests](../Assets/Arena/Tests/Editor/MovementRegressionTests.cs)
include checks for mirrored constants, tick-scoped movement restrictions, and
same-tick reconciliation preserving the rendered pose;
[RemotePresentationBufferTests](../Assets/Arena/Tests/Editor/RemotePresentationBufferTests.cs)
exercise interpolation/extrapolation. Those checks cover specific behavior,
not all live collision cases or smoothness under jitter. This documentation
review did not run a player session. The old follow-up's tick protocol,
modifier-input and presentation-separation work is present; its broader
ownership-cleanup and parity proposals require fresh evidence and scope before
being treated as tasks. Keeping the motor, prediction driver and transport
driver as separate classes is not itself evidence of an unfinished migration.

## Imported Content

Do not mix imported packs into `Assets/Arena`. Keep vendor packages under:

```text
Assets/ThirdParty/AssetStore/Animation/
Assets/ThirdParty/AssetStore/Audio/
Assets/ThirdParty/AssetStore/Characters/
Assets/ThirdParty/AssetStore/Environments/
Assets/ThirdParty/AssetStore/VFX/
Assets/ThirdParty/Unity/
```

If first-party gameplay needs a vendor asset, prefer making a small authored prefab/material/profile under `Assets/Arena/Content` or `Assets/Arena/Resources` that references the vendor source by GUID.

## Generated Code

`Assets/Arena/Runtime/Generated/SpacetimeDB/` contains generated SpacetimeDB bindings. Do not hand-edit it unless a task explicitly calls out a known generated-code workaround. The canonical shape includes the `projectile_load_harness` feature surface (netcode audit R5): bindings are always generated from a harness-featured wasm so the two regen paths (manual and `ops/republish-local-clear.sh`) produce identical output. The extra harness reducers are unused-but-harmless against a default-features module. After server schema changes, regenerate from the repo root:

```bash
cargo build --manifest-path server/Cargo.toml --target wasm32-unknown-unknown --release --features projectile_load_harness
spacetime generate --yes --lang csharp --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB
```

Do not generate with `--module-path server` — that builds default features and drops the harness surface from the generated output.

`hub-server/` is the separate persistent Hub control-plane module. It has no
gameplay simulation loop. Its generated Unity bindings live in
`Assets/Arena/Runtime/Generated/HubSpacetimeDB/` under the distinct
`Arena.HubDb` C# namespace. Build and regenerate them with:

```bash
ops/build-hub-spacetimedb.sh
```

Publish the Hub to the local data-preserving development database with:

```bash
ops/republish-local-hub.sh
```

The defaults target `arena-hub-local`, preserve existing data, and start the
local SpacetimeDB process when needed. They never publish to the remote host.

`match-server/` is the disposable PvP match module. It reuses authoritative
gameplay code from `server/` while excluding survival, training/playground,
Hub-party, random-dungeon interaction/trap schemas, and embedded open-world
collision payloads. Its private
`match_contract.rs` state captures the module owner, one-shot provisioned 2v2
configuration, and the reserved player identity. A freshly published match
database stays inert until its owner bootstraps it. The existing direct-local
workflow publishes the full `server/` module to one database (`arena` by default,
overridden by `ARENA_DATABASE`) with a temporary explicit compatibility mode:

```bash
# Existing local direct-connect workflow (the script default).
ops/republish-local-clear.sh

# Fresh inert database intended for owner bootstrap.
ARENA_ENABLE_LOCAL_DIRECT_MODE=0 ops/republish-local-clear.sh
```

The compatibility switch is refused for non-local servers. The bare script
defaults to clearing the target database; set `ARENA_DELETE_DATA=never` for a
data-preserving manual publish.

The editor's `LocalSpacetimeDbSharedDataPublisher` calls this same direct-local
script when shared-data JSON is imported. It forces `ARENA_SERVER=local` and
`ARENA_DELETE_DATA=never`, honors `ARENA_DATABASE` (default `arena`), and skips
binding generation and .NET verification. Its `PASS` verifies shared-data
contracts for that database only. It does not publish `hub-server/` or rebuild
the optimized PvP/open-world artifacts used by the provisioner.

`match_provisioner/` is the external, local-only control-plane worker. It
subscribes to a provisioner-only Hub wakeup view, queries private tickets after
a wakeup, publishes the already-built match WASM, invokes the one-shot match
bootstrap, and deletes the exact database after termination or timeout. A
slower reconciliation sweep covers restarts, missed events, leases, and
cleanup. Its small SQLite recovery ledger defaults to the ignored
`Library/ArenaMatchProvisioner/` directory. Configuration and safety behavior
are documented in `match_provisioner/README.md` and
`match_provisioner/local.env.example`.

The canonical local multiplayer entry point—intended for developers and LLM
agents—is:

```bash
ops/setup-local-multiplayer.sh setup
```

It publishes the local Hub with data preservation by default, rebuilds both
cached PvP and open-world artifacts with their provenance, and restarts the
managed provisioner in the required order. Run it after changing server code or
baked shared data to make those changes available to newly created Hub matches
and open-world instances. Existing instances retain their published module;
start a new session to exercise the rebuilt artifacts. Editor auto-publish
success does not establish freshness of these artifacts.

Its `status` and `stop` subcommands
manage the same ignored PID/log state under `Library/ArenaLocalMultiplayer/`.
Prefer it over reproducing the lower-level commands manually.

Run it continuously or for one reconciliation cycle with:

```bash
ops/run-local-match-provisioner.sh run
ops/run-local-match-provisioner.sh run --once
ops/run-local-match-provisioner.sh status
```

The runner uses the current local SpacetimeDB CLI identity unless
`ARENA_PROVISIONER_TOKEN` is supplied explicitly. It never compiles per match.

Build its guarded, size-optimized cached artifact and dedicated `Arena.MatchDb`
bindings with:

```bash
ops/build-match-spacetimedb.sh
```

The Phase 4 Unity connection boundary is split across:

- `HubNetworkManager.cs`: persistent Hub transport, two caller-only view
  subscriptions, idempotent request submission, and schema-free UI snapshots;
- `MatchHandoffCoordinator.cs`: assignment validation, Hub/match overlap,
  timeout/rollback, and disposable-match return;
- `NetworkManager.cs`: the dynamically assigned gameplay database, contract
  checks, gameplay subscriptions, callbacks, clocks, and runtime caches.

Identity tokens are keyed by SpacetimeDB host/cluster in
`NetworkEnvironmentConfig.cs`, not by database. Keep that invariant when
adding databases: a match may reuse a token only after its assignment is
validated as the same cluster.

## Development Build Cleanup

Repeated native server tests can accumulate large hard-linked Cargo debug and
incremental artifacts under the `server/target`, `hub-server/target`, and
`match-server/target` trees. Periodically inspect the physical disk usage and
clean those generated artifacts with:

```bash
ops/cleanup-server-build-artifacts.sh --dry-run
ops/cleanup-server-build-artifacts.sh
```

The cleanup preserves every module's release artifacts, including the gameplay,
Hub, and optimized disposable-match WASM files consumed by local publishing.
It is separate from `ops/cleanup-local-spacetimedb-data.sh`, which deletes all
local SpacetimeDB databases/caches plus the now-invalid match-provisioner ledger
and managed runtime files. Preview that destructive cleanup with
`ops/cleanup-local-spacetimedb-data.sh --dry-run`; after it runs, use
`ops/setup-local-multiplayer.sh` to publish a fresh local environment.

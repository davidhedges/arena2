# Independent Vertical-Slice Audit

Date: 2026-07-10

## Scope and method

This audit used only implementation sources, authored data, tests, project settings, and operational scripts as sources of truth. Prior audits, `docs/`, plans, READMEs, and design documents were deliberately excluded.

## Executive verdict

The core authoritative gameplay implementation is generally thoughtful and heavily unit-tested. The non-deferred findings from this audit have now been remediated and validated at the source/build level. The project is still not safe for an untrusted public multiplayer deployment because the explicitly deferred production trust-boundary, matchmaking, dormant-slice, diagnostic, and infrastructure work remains open.

## Follow-up disposition

Implemented after this audit:

- Finding 1: finite movement validation, signed yaw normalization, bounded input lead/queue occupancy, inherited queue/cursor repair, duplicate-tick cleanup, and fail-closed loot distances.
- Finding 7: `combat_rule_catalog` plus player/NPC source/target-scoped `combat_effect_event` subscriptions, with reconnect-safe floating combat text ownership.
- Finding 8: shared-data stamps now gate the gameplay connection and local-player subscription; missing or mismatched contracts disconnect with an explicit incompatibility banner.
- Connection/presentation lifecycle: hub travel waits for a committed reducer result; identity tokens migrate out of plaintext `PlayerPrefs` into macOS Keychain, with non-persistent in-memory fallback on unsupported platforms.
- Loot/world policy: corpse containers reserve loot for the killing player and their current party; every open/move path still enforces world and finite range checks; legacy corpses recover the player spawner as their reservation owner.

Intentionally deferred by product decision:

- Findings 2, 3, 4, 5, and 6.
- Dormant player-build slices.
- Broken diagnostic feature.
- Delivery and infrastructure.

The detailed findings below are retained as the audit record. Each affected section states its current disposition.

## Release blockers

### 1. Movement input can corrupt authoritative state and bypass distance checks

**Disposition: Fixed.**

`server/src/movement.rs::send_movement_intent` accepts non-finite `forward`, `strafe`, and `yaw`. Rust's `clamp` does not turn `NaN` into a valid number, so a custom client can propagate `NaN` into velocity and authoritative position.

This is security-relevant, not merely visual: loot distance checks such as `server/src/inventory.rs::validate_world_container_access` fail open for `NaN`, because `NaN > maximum` is false. A poisoned player can therefore interact with any world-loot container in the same scene.

The same reducer accepts arbitrarily future, strictly increasing input ticks and inserts each into `PlayerCommand`. There is no maximum lead, queue cap, or age-based expiry, allowing persistent queue growth and increasingly expensive per-player scans.

Required remediation:

- Reject every non-finite numeric input before mutation.
- Normalize yaw.
- Fail closed on every non-finite distance calculation.
- Restrict input ticks relative to authoritative `last_processed_tick`.
- Enforce a small per-player queue cap and unique `(identity, input_tick)` contract.

Evidence:

- `server/src/movement.rs:65-170`
- `server/src/player_input.rs:13-99`
- `server/src/inventory.rs:1472-1525`
- `server/src/inventory.rs:3590-3631`

### 2. A production HUD exposes an unlimited NPC and loot generator

**Disposition: Deferred intentionally.**

`HUDController` always installs the "playground-only" panel; it is not editor/development guarded. That panel exposes NPC spawning directly through `SpawnNpc`.

The server reducer has no per-owner or world cap and inserts a new NPC on every call. Every authoritative tick then materializes and iterates all NPCs.

Spawned NPCs participate in normal corpse equipment generation. This provides both:

- An unlimited equipment-farming path.
- A straightforward server CPU/state exhaustion path.

The UI and reducers should be development-feature-gated, and production NPC creation must come from authoritative world spawning with quotas.

Evidence:

- `Assets/Arena/Runtime/UI/HUDController.cs:297`
- `Assets/Arena/Runtime/UI/PlaygroundTargetsPanel.cs:70-76`
- `Assets/Arena/Runtime/UI/PlaygroundTargetsPanel.cs:164-176`
- `server/src/npcs.rs:374-452`
- `server/src/npcs.rs:585-603`
- `server/src/inventory.rs:2281-2385`

### 3. Operational and global reducers have no authorization

**Disposition: Deferred intentionally.**

Any connected identity can currently invoke:

- Global lag-compensation configuration through `set_lag_comp_config`.
- Full progression, spell, item, and affix catalog republishing.
- The unconditional status runtime harness.

Catalog synchronization unconditionally updates existing public rows, so repeated calls amplify into replication traffic for every subscribed client.

These reducers need a server-enforced operator authorization mechanism or must be absent from production schemas.

Evidence:

- `server/src/combat/position_history.rs:123-155`
- `server/src/progression.rs:1461-1464`
- `server/src/progression.rs:2861-2877`
- `server/src/spells/mod.rs:583-585`
- `server/src/inventory.rs:1364-1372`
- `server/src/combat.rs:7100-7115`

### 4. Official subscription filtering is not a security boundary

**Disposition: Deferred intentionally.**

The official client filters subscription queries for bandwidth and runtime ownership, but custom clients can query public tables directly.

There are 65 public tables, including complete item instances, affixes, inventory containers, inventory slots, equipment loadouts, known spells, action-bar assignments, resources, and cooldown-related state.

Official-client SQL filtering does not prevent inventory inspection, opponent-build scraping, or metadata harvesting. Owner-private data needs private tables with authorized server views or row-level security.

Evidence:

- `Assets/Arena/Runtime/Network/NetworkManager.cs:23-28`
- `server/src/inventory.rs:184-302`
- `server/src/spells/mod.rs:147-318`
- `server/src/progression.rs:1144-1368`

## High-severity functional failures

### 5. Matches can become permanently stuck when a participant leaves

**Disposition: Deferred intentionally while match design is incomplete.**

Instance removal only decrements `player_count`. It does not cancel a countdown or conclude a live match.

Countdowns transition unconditionally into `IN_PROGRESS` after three seconds, even if only one participant remains. Match conclusion is called only following lethal damage.

The client fallback also refuses to conclude after the disconnected player disappears because it requires at least two currently visible players.

Required behavior:

- A countdown with fewer than two players should return to `WAITING`.
- An in-progress match with at most one eligible participant should conclude immediately.

Evidence:

- `server/src/arena.rs:346-380`
- `server/src/game_loop.rs:2053-2072`
- `server/src/combat.rs:3778-3787`
- `server/src/combat.rs:4865-4882`
- `Assets/Arena/Runtime/Match/MatchController.cs:157-184`

### 6. Match creation, discovery, and start authorization are inconsistent

**Disposition: Deferred intentionally while match design is incomplete.**

`create_instance` creates an empty persistent row but does not join the creator or enforce one outstanding lobby per owner. The official button remains enabled because the creator still has no local instance, allowing repeated creation.

Every arena row is globally subscribed, amplifying empty-lobby spam.

Additionally:

- `start_match` verifies phase and player count, but not that the caller belongs to the instance.
- `ensure_open_arena_exists` treats any under-capacity arena as open, including practice, countdown, in-progress, or ended arenas.
- This can prevent creation of a genuinely joinable waiting arena.

Evidence:

- `server/src/arena.rs:181-228`
- `server/src/arena.rs:446-480`
- `server/src/arena.rs:513-522`
- `Assets/Arena/Runtime/UI/LobbyController.cs:174-187`
- `Assets/Arena/Runtime/UI/LobbyController.cs:313-332`

### 7. Combat floating text is disconnected from its data source

**Disposition: Fixed.**

The server emits damage/heal rows through `combat_effect_event`, and `FloatingCombatText` listens for them.

However, neither the static nor scoped plans subscribe `combat_effect_event`. Consequently, normal damage/heal numbers cannot arrive.

The static plan also omits `combat_rule_catalog`, despite client GCD prediction reading it. It silently uses the 1500 ms fallback forever, drifting if authored tuning changes.

Evidence:

- `server/src/combat.rs:4213-4231`
- `Assets/Arena/Runtime/Presentation/FloatingCombatText.cs:30-44`
- `Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs:12-42`
- `Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs:105-141`
- `Assets/Arena/Runtime/Combat/GameplayContracts.cs:1721-1729`

### 8. Shared collision-contract mismatches fail open

**Disposition: Fixed.**

`ContractVersionGuard` correctly detects missing or mismatched collision/heightfield files, but it only logs warnings/errors and allows gameplay to continue.

Because these files drive client prediction and authoritative collision parity, a mismatch should disable input/gameplay and present an explicit incompatibility screen.

Evidence:

- `Assets/Arena/Runtime/Network/ContractVersionGuard.cs:21-51`
- `Assets/Arena/Runtime/Network/NetworkManager.cs:373-377`

## Medium-severity findings

### Connection and presentation lifecycle

**Disposition: Fixed.**

- `FloatingCombatText` tracks only a boolean subscription. After reconnect or environment switching it remains attached to the dead connection and never subscribes to the replacement connection.
- Hub travel persists the local choice, calls the reducer, and immediately loads the scene without awaiting acknowledgement. A rejected or unavailable call can leave the loaded scene, subscription scope, and authoritative world inconsistent.
- Identity bearer tokens are stored directly in `PlayerPrefs`.

Evidence:

- `Assets/Arena/Runtime/Presentation/FloatingCombatText.cs:17-40`
- `Assets/Arena/Runtime/UI/HubController.cs:216-224`
- `Assets/Arena/Runtime/Network/NetworkEnvironmentConfig.cs:121-139`

### Loot and world interaction policy

**Disposition: Fixed with killing-player/current-party reservation semantics.**

- Corpse-loot entitlement is proximity-only. There is no killer, contribution, party, reservation, or ownership check.
- Nearby dead NPC containers are clustered into the primary loot interaction, which makes the lack of entitlement broader than a single corpse.

Evidence:

- `server/src/inventory.rs:1376-1397`
- `server/src/inventory.rs:1400-1469`
- `server/src/inventory.rs:1472-1525`

### Dormant player-build slices

**Disposition: Deferred intentionally.**

- Character creation exists as a scene/controller but is absent from build settings.
- Training Ground is explicitly disabled in build settings.
- The server creates default appearance rows as already complete, so character creation is not part of current onboarding.

Evidence:

- `ProjectSettings/EditorBuildSettings.asset:7-52`
- `server/src/appearance.rs:153-168`
- `Assets/Arena/Runtime/UI/CharacterCreationController.cs:212-223`

### Broken diagnostic feature

**Disposition: Deferred intentionally.**

The spellcasting terminal harness is currently uncompilable: its `PlayerPhysics` initializer lacks `last_tick_consumed_command` and `buffered_command_count`. The supplied terminal harness script therefore cannot run.

Evidence:

- `server/src/spells/casting.rs:4203-4215`
- `ops/run-spellcasting-terminal-harness.sh:22-27`

### Delivery and infrastructure

**Disposition: Deferred intentionally.**

- The remote deployment script builds and publishes without running server tests or client compilation.
- Infrastructure has no backup/snapshot schedule, alerting, automated restore proof, or application-level connection/reducer rate limits.
- SSH defaults to `0.0.0.0/0`, with the operator receiving passwordless unrestricted sudo.
- SpacetimeDB installation executes an unpinned remote script through `curl | sh`.

Evidence:

- `ops/deploy-spacetimedb.sh:23-38`
- `infrastructure/hetzner-spacetimedb/variables.tf:61-70`
- `infrastructure/hetzner-spacetimedb/cloud-init.yaml.tftpl:15-25`
- `infrastructure/hetzner-spacetimedb/cloud-init.yaml.tftpl:182-185`
- `infrastructure/hetzner-spacetimedb/main.tf:66-102`

## Granular vertical-slice results

"Clear" means no material defect was found through static tracing and the available tests. It does not imply live multiplayer verification.

| Vertical slice | Result |
|---|---|
| Runtime scene gating | Clear |
| Initial network bootstrap | Clear |
| Endpoint/environment selection | Clear; auth tokens no longer persist in plaintext |
| Connection teardown/cache reset | Clear |
| Reconnect-dependent floating text | Fixed: connection-owned resubscription and teardown |
| Static catalog subscriptions | Fixed: combat rules included |
| Owner-local subscriptions | Clear for official-client bandwidth |
| World/instance scoped subscriptions | Clear for bandwidth; not security |
| Public-table confidentiality | Release blocker |
| Server clock/RTT estimation | Clear |
| Shared-data hashing | Fixed: detection now gates gameplay and fails closed |
| Entity hydration and row-order handling | Clear |
| Local input sampling | Clear |
| Client command history/lead controller | Clear |
| Movement reducer validation | Fixed: finite validation and normalized yaw |
| Server command queue bounds | Fixed: 12-tick lead/cap with inherited-state repair |
| Scheduled game-loop/watchdog | Clear |
| Ground/jump/landing simulation | Clear under finite inputs |
| Horizontal collision sweeping | Clear under finite inputs |
| Special movement handoff | Clear |
| Local prediction/replay | Clear |
| Remote presentation buffering | Clear |
| Open-world collision/heightfield parity | Strong authored contract; fail-open guard |
| Open-world travel acknowledgement | Fixed: scene transition follows committed reducer result |
| Arena creation | Deferred: broken and unbounded |
| Lobby discovery | Deferred: broken phase predicate |
| Instance join validation | Clear |
| Match-start authorization | Deferred |
| Countdown lifecycle | Deferred: broken on membership loss |
| Disconnect/leave lifecycle | Deferred: broken for active matches |
| Lethal death/elimination | Clear |
| Winner/stat snapshot | Clear only when the normal lethal path runs |
| Client match fallback | Deferred: broken for disconnects |
| Practice instance server lifecycle | Clear |
| Practice scene player reachability | Dormant/disabled |
| Party invite authorization | Clear |
| Party caps/leadership/expiry | Clear; thin direct test coverage |
| Target relationship rules | Clear |
| Action-bar assignment authorization | Clear |
| Spell learning | Deliberately unrestricted |
| Spell-cast authorization | Clear |
| Cast start/release/cancel | Clear |
| Cooldown/resource authority | Clear server-side; GCD client tuning omission |
| Projectile simulation/collision | Clear |
| Projectile load harness feature | Compiles |
| Status/periodic effects | Clear |
| Status runtime harness exposure | Unsafe production reducer |
| Melee/combo/gap close | Clear |
| Auto-attack/replacement | Clear |
| Block/parry/dodge | Clear |
| Lag-compensation algorithms | Well tested |
| Lag configuration authority | Release blocker |
| NPC spawn authority | Release blocker |
| NPC AI tick scalability | Unsafe without spawn caps |
| Damage/heal event creation | Clear |
| Damage/heal client presentation | Fixed: visible player/NPC effect subscriptions |
| Combat VFX routing/lifecycle | Strong test coverage |
| Animation resolution/playback | Strong test coverage |
| Inventory movement/stack/equip mutation | Strong owner validation |
| Inventory read confidentiality | Release blocker |
| Corpse-loot distance | Fixed: non-finite distances fail closed |
| Corpse-loot entitlement | Fixed: killing player and current party reservation |
| Equipment presentation mirror | Clear |
| Discipline/loadout switching | Clear |
| Appearance validation | Clear but intentionally narrow |
| Character-creation player flow | Deferred: dormant/non-shippable |
| Action-bar/spellbook UI | Clear |
| Escape/system-menu routing | Clear |
| Playground HUD | Unsafe in production |
| Authored catalog validation | Strong |
| Catalog publishing authority | Release blocker |
| Generated bindings | Compile; include optional harness surface |
| Spellcasting terminal harness | Deferred: broken compile |
| Dependency pinning | Good: Cargo, Unity, and Terraform locks present |
| Remote deployment gate | Insufficient |
| Backups/disaster recovery | Missing |
| Monitoring/alerting | Missing |

## Verification performed

- `cargo test`: 448 default-feature tests passed.
- `cargo test --all-features`: failed compilation on the terminal harness.
- WASM check with `projectile_load_harness`: passed with six existing warnings.
- Runtime, editor, and EditMode test C# projects compiled successfully.
- `cargo fmt --check`: passed.
- All shell scripts passed `bash -n`.
- `git diff --check`: passed before this report was added.
- Strict `cargo clippy --all-targets -- -D warnings`: fails with substantial existing lint debt, mostly argument-count/style findings plus some dead code.
- The Unity test runner was unavailable, so the 261 EditMode test attributes were compiled but not executed.
- There are no PlayMode tests and no CI configuration.
- Terraform formatting/validation could not run because Terraform is not installed.

### Follow-up implementation verification

- `cargo test --lib`: 452 tests passed after the fixes.
- `cargo check --target wasm32-unknown-unknown`: passed with six existing warnings.
- `cargo fmt --check`: passed.
- `dotnet build Assembly-CSharp.csproj --no-restore`: passed with existing third-party deprecation warnings only.
- `dotnet build Assembly-CSharp-Editor.csproj --no-restore`: passed with existing warnings only.
- `dotnet build Arena.EditModeTests.csproj --no-restore`: passed with no warnings.
- `git diff --check`: passed.
- The Unity runner remains unavailable, so EditMode tests were compiled but not executed.

## Remaining deferred backlog

1. Remove or authorize production debug reducers and NPC spawning.
2. Enforce private-data access at the server boundary.
3. Flesh out match creation, membership, countdown, disconnect, and conclusion behavior when match development resumes.
4. Decide whether to ship or remove dormant character-creation/training slices.
5. Repair or remove the terminal spellcasting diagnostic harness.
6. Establish deployment gates, rate limiting, backups, restore proof, monitoring, and infrastructure hardening.

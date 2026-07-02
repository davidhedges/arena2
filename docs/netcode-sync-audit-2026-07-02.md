# Client-Server Sync/Netcode Audit — 2026-07-02

Architecture audit of the sync slice: authority, prediction, replication, persistence,
generated bindings, reducer contracts, subscriptions, and performance observability.
Each recommendation is scoped as a bounded implementation slice.

## Implementation status (updated 2026-07-02)

- **R1 — implemented.** `clear_transient_actor_state` in
  `server/src/actor_lifecycle.rs` is the canonical teardown owner (statuses,
  engagement, stacking passives, active/pending casts, cast-prediction
  correlation, channel/special-movement runtime, defense state, pending melee
  impacts/timed movements/projectile releases, queued followups, pending area
  impacts, bespoke spells, auto-attack state, caster-owned projectiles +
  target-state rows, pending player commands). Called from
  `despawn_actor_bundle` and the reconnect branch of `client_connected`.
  `client_disconnected` and `despawn_actor_bundle` now log-and-continue on
  ancillary cleanup errors instead of aborting the transaction. Cooldown/GCD
  rows are deliberately kept. Guarded by three tests in
  `actor_lifecycle.rs::tests` (coverage list, no-cooldown-delete, both call
  sites) — a source-scan guard, since the crate has no ReducerContext harness.
- **R3 — implemented (first slice).** Server: per-window `[TICK_PROFILE_SCAN]`
  log line (see `server/src/tick_metrics.rs`) with write counters for
  `player_physics`, `player_intent`, `player_resource`,
  `fixed_action_charge_state`, `npc_combat_runtime`,
  `combat_stacking_passive_runtime`, plus scan counters and populations —
  log-only, behind `ARENA_PROFILE_TICKS`. Client: per-table row-callback
  counters (`NetcodeReceiveCounters`, bound in `NetworkCallbackBinder`) with
  rows/sec + predicted-result-by-kind lines in `NetcodeDebugOverlay`.
  Not yet done: initial-sync row counts per subscription query.
- **R2 — implemented (2026-07-02).** Schema: `ActionRejectReason` enum (20
  variants: cooldown/GCD, resource, target/range/facing/LOS, combo, aerial,
  snapshot, busy/dead/disabled, charges, gap-close, cancel, unspecified) and a
  `reject_reason` field on `PredictedActionResult`
  (`server/src/action_prediction.rs`). Every reject site populates it:
  `MeleeAttackDispatch::Rejected` now carries the reason (29 sites in
  `melee.rs`; unmapped `Err` paths record `Unspecified`),
  `record_spell_prediction_result` takes the reason (38 sites in
  `spells/casting.rs`; `process_spell_cast` returns
  `Option<ActionRejectReason>` so target/facing/LOS/range failures survive to
  the row), and the defense/movement helpers thread it (7 + 6 sites).
  Client: rollback paths log the reason (`ActionBarTrace`),
  `LocalCombatState.PredictionRejected` now fires with
  `(actionKind, ActionRejectReason)` as the HUD toast hook (still no HUD
  surface), and `NetcodeDebugOverlay` shows `lastReject=family:reason`.
  (b) Spell resource-kind unified with the melee catalog path on both sides:
  `resources.rs` dropped the SPELL→MANA carve-out (spells resolve
  `effective_resource_kind_for_ability` like melee — all 45 authored spell
  abilities say MANA today, so no behavior change), and
  `SpellInputHandler.HasResourceForSpell`/`RecordPredictedSpellStart` use the
  catalog-driven `SpellResourceKind` instead of hard-coded `"MANA"`. The only
  remaining MANA literal is the server fallback for off-bar casts the client
  never predicts (commented in `casting.rs`). Pre-checks remain advisory:
  no new client-side denial capability was added.
- **R4 — not started. R5 — regen-mode pin done; version stamp not started.**
- **R5 correction (2026-07-02):** the "no live drift today" finding missed a
  regen-mode split. The projectile-load-harness surface is feature-gated
  (`#[cfg(feature = "projectile_load_harness")]`), and the two regen paths
  disagreed about it: the old canonical `--module-path` generate builds
  default features (harness **excluded**), while `ops/republish-local-clear.sh`
  — the default local workflow, `ARENA_PROJECTILE_LOAD_HARNESS=1` — builds with
  the feature and generates from that wasm via `--bin-path` (its comment even
  assumed the checked-in bindings include the harness; they did not). A
  harness-mode regen on 2026-07-02 added the four missing files
  (`Reducers/Run|CleanupProjectileLoadHarness`,
  `Types/ProjectileLoadHarnessActor|Run`) plus their two dispatch lines in
  `SpacetimeDBClient.g.cs`, and nothing else; they are committed as the
  canonical shape (matching the local publish default; the extra reducers are
  unused-but-harmless against a default-features prod module).
  **Pin (2026-07-02): done.** `ops/republish-local-clear.sh` now always
  generates from the harness-featured wasm regardless of publish mode (built
  after `spacetime publish -p`, which rewrites the wasm with default
  features), and `docs/project-structure.md` documents the canonical two-step
  command (cargo build `--features projectile_load_harness` +
  `spacetime generate --bin-path`) with an explicit warning that
  `--module-path` regen is non-canonical. The R2 regen used this mode; diff
  contained exactly the expected additions (`Types/ActionRejectReason.g.cs`
  plus the `RejectReason` field) and zero harness churn. The `ContractVersion`
  stamp table and shared-JSON sync guard remain open.

## Executive Summary

The overall architecture is in better shape than the planning docs suggest: the
tick-based movement protocol from `plans/movement-netcode-followup-plan.md` is
implemented (ack via `last_processed_tick` at `server/src/player_physics.rs:60`,
rollback+replay in `Assets/Arena/Runtime/Input/LocalMovementPredictionDriver.cs`),
prediction is cleanly separated from confirmed state, and constants flow through
replicated catalog tables rather than duplicated literals.

The real weaknesses are:

1. **Row lifecycle** — scattered teardown, stale transient rows rehydrating on reconnect
2. **A validation-drift pattern that already bit once** (commit `115393b9`)
3. **Five unfiltered inventory subscriptions** replicating every player's items to every client
4. **Zero replication-volume observability** (tick *time* is measured; row volume is not)

Two findings from the initial sweep were verified and corrected:

- The "critically stale bindings" alarm is a **false positive today** — no `#[table]`
  or `#[reducer]` surface changed since the last regen (`abb257f7`, 2026-06-24); the
  8-day gap is catalog-data churn only. The *process* risk is real (see R5).
- The stale-persistence risk is real, but the realistic trigger is **module
  republish/host restart** and a **transaction-abort bug** in `client_disconnected`,
  not client crashes.

---

## 1. Recommendations (priority order)

### R1 — Unify transient-state teardown; make disconnect un-abortable; reuse it on reconnect

**Classification: correctness fix**

**Evidence.**
`despawn_actor_bundle` (`server/src/actor_lifecycle.rs:117-163`) is nominally the
single teardown owner, but `clear_player_combat_state` (`server/src/combat.rs:252-256`)
only clears statuses, engagement, and stacking passives. Never deleted on disconnect:

- `active_cast` / `channel_cast_runtime` / `special_movement_runtime`
  (only deleted via `server/src/spells/casting.rs:3563-3570`)
- `defense_state` (deletion scattered across `server/src/defense.rs`, e.g. line 290)
- `queued_melee_followup` / `pending_melee_impact` / `pending_melee_timed_movement`
  (only in the stagger-interrupt path, `server/src/combat.rs:4460-4475`)
- `cast_prediction_correlation`
- in-flight `active_combat_projectile` rows keyed by caster

Two paths rehydrate these stale rows:

1. The reconnect branch (`server/src/player.rs:59-75`) does zero transient cleanup —
   and it runs after every module republish/host restart, since SpacetimeDB persists
   tables while connections drop (see `ops/deploy-spacetimedb.sh` cadence).
2. `server/src/player.rs:120` propagates `?` from playground-target cleanup, so one
   error aborts the whole `client_disconnected` transaction and leaves *every* row alive.

Identity is stable per credentials, so orphans re-attach to the same player next
session — a mid-cast row can fire into a fresh session.

**Better contract.**
One function (`clear_transient_actor_state`) owns "delete every transient row for
this identity," called from both `despawn_actor_bundle` and the reconnect branch.
Explicit policy split:

- Presentation/action state (casts, defense, pending melee, prediction correlation,
  projectiles): **delete**.
- Timestamp-anchored anti-abuse state (`spell_cooldown`, `global_cooldown`):
  **deliberately keep** — deleting cooldowns on disconnect creates a relog-to-reset
  exploit; they expire naturally.

`client_disconnected` logs-and-continues on ancillary cleanup errors instead of `?`.

**Why it improves the system.**
Turns "did we remember to delete X?" into a one-place review. Transient tables are
being added steadily (daggers/stealth, disciplines); without a canonical owner, every
new feature re-creates this bug class.

**Implementation slice.**
Server-only, no schema change, no binding regen. Add the function, wire two call
sites, swap `?` for logged errors, plus a Rust test that populates each transient
table and asserts teardown leaves none.

**Files/surfaces.**
`server/src/actor_lifecycle.rs`, `player.rs`, small helpers in `combat.rs`,
`defense.rs`, `melee.rs`, `spells/casting.rs`, `combat/projectiles.rs`.

**Verification.**
`cargo test` in `server/`; runtime check: kill a client mid-cast, republish the
module, reconnect — no cast bar, no buff carryover, no scheduled melee impact fires.

**Risks / non-goals.**
Do not delete inventory (intentionally recreated), party membership (handled by
`remove_player_from_party_state`), or cooldowns. Not in scope: changing spawn logic.

---

### R2 — Put rejection reasons on `PredictedActionResult`; demote client pre-checks to advisory

**Classification: architecture improvement**

**Evidence.**
The result table (`server/src/action_prediction.rs:57-73`) carries only a result
kind. Server reject sites log rich reasons (`server/src/melee.rs:2654-2663`) but the
client receives a bare `Rejected` and shows the player nothing
(`Assets/Arena/Runtime/Input/MeleeInputHandler.cs:528-532`). That opacity pressures
the client to mirror full validation — the exact pattern that drifted in commit
`115393b9` (a hard-coded MANA check silently bypassed cost validation for stamina
melees). The same latent drift still exists for spells:
`Assets/Arena/Runtime/Input/SpellInputHandler.cs:550-556` and
`server/src/spells/casting.rs:143-147` both hard-code MANA while melee correctly
reads the catalog's `ResourceKind`.

**Better contract.**
Reducers are the *only* validators; they explain denials with a machine-readable
`reason` field on the existing `PredictedActionResult` row (no parallel deny table).
Client pre-checks remain as cheap UX gating only, and any client/server disagreement
becomes immediately visible as a reason code instead of a silent mispredict.

**Why it improves the system.**
Removes the root incentive for validation duplication, makes future deny bugs
observable at one chokepoint, and gives the HUD honest feedback ("insufficient
stamina" vs "on cooldown").

**Implementation slice.**
(a) Add `reason` enum field + populate at reject sites (mechanical sweep of
`record_predicted_action_result` callers); regen bindings; client logs reason + one
HUD toast hook. (b) Unify spell resource-kind resolution to read the catalog like
melee on both sides.

**Files/surfaces.**
`server/src/action_prediction.rs`, reject sites in `melee.rs` / `spells/casting.rs`
/ `movement_actions.rs`; regenerated `Assets/Arena/Runtime/Generated/SpacetimeDB/`;
`LocalCombatState.cs`, `SpellInputHandler.cs`, `MeleeInputHandler.cs`, small HUD
surface.

**Verification.**
Binding regen diff contains only expected additions; force a rejection and
observe the reason client-side; `cargo test`. Correction (2026-07-02): "cast
at 0 mana" does not work as the forced rejection — out-of-resource presses
are gated by the client's advisory pre-check and never reach the server (for
any resource kind). Force a disagreement instead: cast a targeted spell at a
target behind a wall (client does not mirror LOS →
`lastReject=SpellCast:LineOfSightBlocked` in the Backslash overlay), or
temporarily disable the `HasResourceForSpell` early-return to see
`InsufficientResource` end-to-end.

**Risks / non-goals.**
This is a schema change — it deliberately exercises the R5 pipeline; do it after
R5's guard or with the documented command. Non-goal: building any second deny/sync
path.

---

### R3 — Row-write / row-receive counters before any replication optimization

**Classification: instrumentation before optimization**

**Evidence.**
The server tick profiler measures *time* only (`server/src/game_loop.rs:284-345`,
env-gated `ARENA_PROFILE_TICKS`); nothing anywhere measures rows/sec, per-table
volume, or bandwidth. Meanwhile the hot loop writes unconditionally:
`commit_player_physics` every tick per player (`server/src/game_loop.rs:1412-1418`),
`PlayerIntent` and `PlayerResource` likewise (~lines 1396-1397). The
"~117-150 rows/tick at 32 players" figure from the sweep is an **estimate, not a
measurement**. A good pattern already exists to copy: `CombatProjectileTickMetrics`
(`server/src/combat.rs:538-595`) plus its client overlay
(`Assets/Arena/Runtime/Debug/ProjectileLoadHarnessOverlay.cs`).

**Better contract.**
The existing 5-second profile window also reports per-table write counts;
`EntityRegistry` counts per-table callbacks client-side and surfaces one line in
`NetcodeDebugOverlay`; every subsequent perf PR must cite these numbers.

**Implementation slice.**
Server: counters in the existing profile window (log-only — don't add replicated
churn to measure churn). Client: per-table ints in
`Assets/Arena/Runtime/Entity/EntityRegistry.cs` reset per window, one overlay line,
plus initial-sync row counts per subscription query to measure scope-change cost.

**Verification.**
Run with `ARENA_PROFILE_TICKS=1`, two clients, confirm counts appear and match
intuition (physics ≈ players × 30 Hz).

**Risks / non-goals.**
No schema changes, no new tables. Non-goal: acting on the numbers in the same slice.

---

### R4 — Owner-filter the five inventory/item subscriptions

**Classification: architecture improvement (with a privacy/cheat-surface correctness angle)**

**Evidence.**
`Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs:63-67`:
`InventoryContainer`, `InventorySlot`, `ItemInstance`, `ItemSpell`,
`ItemAffixInstance` are subscribed full-table — inside the "local player" query
group where every neighboring query filters by identity. Every client receives every
player's full inventory; it grows with population and is inspectable in client
memory.

**Better contract.**
The local group filters these by owner like its neighbors (semijoin through
`InventoryContainer` if `ItemInstance` lacks a direct owner column — verify the
schema first). World-visible loot already has its own scoped `LootContainer` query.

**Why it improves the system.**
Consistency with the planner's own architecture, removes an information-exposure
surface, and cuts unbounded cold-data replication — independent of any perf
measurement.

**Implementation slice.**
Client-only query changes (server indexes likely already exist — verify); then
confirm trade/vendor/loot/equip flows still receive the rows they read.

**Verification.**
Two clients: assert client A's cache contains zero of B's item rows; inventory UI,
pickup, and equip still work.

**Risks / non-goals.**
A flow may silently depend on other players' item rows (gear inspection — though
`EquipmentLoadout` is already owner-filtered, so visuals must come from
`CharacterAppearance`; check before shipping). Non-goal: spatial filtering of entity
tables.

---

### R5 — Version-stamp the contract surfaces (bindings + shared JSON)

**Classification: architecture improvement (guardrail)**

**Evidence.**
Regen is manual (`docs/project-structure.md:70-74`; only
`ops/republish-local-clear.sh` automates it). Last regen: commit `abb257f7`
(2026-06-24). Verified: **no live drift today** — no table/reducer surface changed
since — but 69 server commits vs 16 regen commits in six weeks with no guard is
luck, not process. The shared-JSON side is weaker: the server `include_str!`s
6 core + 28 world files, the client reads *copies* under
`Assets/Arena/Resources/SharedData/` synced by
`GameplayCollisionExporter.SyncSharedMovementData()` — which isn't wired to any menu
item, has no checksum, and guards the exact collision-parity data the movement plan
says must match exactly.

**Better contract.**
A tiny static `ContractVersion` table (module schema hash + shared-JSON content
hashes) written at `init`; client asserts on connect and hard-warns in dev builds.
Editor-side: wire the sync method into a menu item and build preprocessing.

**Implementation slice.**
(a) Wire sync + build hook (editor-only); (b) add the stamp table + client connect
assert (one small schema change, one regen); (c) optional CI step diffing
`spacetime generate` output.

**Verification.**
Mutate a shared JSON without syncing → client warns; clean tree → regen produces
zero diff.

**Risks / non-goals.**
Keep the stamp table static (no churn). Non-goal: auto-regenerating bindings at
build time on developer machines.

---

## 2. Safest First Slice

**R1a:** extend `despawn_actor_bundle` to delete the missed transient tables (casts,
channel/special-movement runtime, defense state, pending melee rows, prediction
correlation, caster-owned projectiles), change `server/src/player.rs:120` from `?`
to log-and-continue, call the same cleanup from the reconnect branch, and add the
teardown-completeness Rust test. Zero schema change, zero client change, zero
binding regen, fully verifiable with `cargo test` — and it fixes the
highest-severity live bug class.

## 3. Minimal Instrumentation Plan

1. **Server, ~half day:** per-table write counters (`physics`, `intent`, `resource`,
   `status_effect`, `projectile`, `combat_event`) accumulated in the existing
   `TickProfileWindowState` and printed in the existing 5s `[TICK_PROFILE]` line.
   Log-only, behind the existing `ARENA_PROFILE_TICKS` gate.
2. **Client, ~half day:** per-table callback counters in `EntityRegistry`, one
   summary line in `NetcodeDebugOverlay` (rows/sec by table), plus a one-shot log of
   initial-sync row counts per subscription query on scope change.
3. **Movement/spell diagnostics already exist** (correction error, replay depth,
   projectile harness) — don't duplicate them.
4. **Gate:** dirty-checking `PlayerPhysics`/`PlayerResource`, the `StatusEffect`
   hot/cold split, and any spatial AoI are **deferred until these numbers exist**.
   Design caution to record now: the local player's `PlayerPhysics` row doubles as
   the prediction ack channel (`last_processed_tick`), so a naive "skip write if
   position unchanged" could stall replay acks — any dirty-check design must
   preserve ack delivery.

## 4. Implementation Review Checklist

For every slice, verify:

- [ ] `cargo test` passes in `server/` and the module builds
      (`spacetime build` / wasm target).
- [ ] If any `#[table]` or `#[reducer]` surface changed: bindings regenerated with
      exactly the canonical harness-featured regen from the repo root
      (`cargo build --manifest-path server/Cargo.toml --target wasm32-unknown-unknown --release --features projectile_load_harness`
      then
      `spacetime generate --yes --lang csharp --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB`
      — see `docs/project-structure.md`; never `--module-path`), committed in
      the same commit; no hand edits under `Runtime/Generated/`.
- [ ] One source of truth preserved: no new client-side check that can *deny* an
      action on its own; client checks are advisory; server reducers remain sole
      validators. No parallel deny/sync tables.
- [ ] Any new per-identity transient table is added to the unified teardown function.
- [ ] Cooldown/GCD rows are never deleted on disconnect (relog exploit).
- [ ] No new full-table subscription; new queries go through
      `GameplaySubscriptionPlanner` with an identity/scope filter.
- [ ] Shared JSON edits go through the sync method, never hand-copied into `Assets/`.
- [ ] Performance changes cite before/after numbers from the R3 counters in the PR
      description — no optimization without a measurement.
- [ ] CLAUDE.md constraints hold: no new responsibilities in `PlayerAnimator`;
      hit-reaction presentation stays in `CombatStatusReactionController`; no combat
      keybinds bypassing action-bar resolution.

## Deferred / Speculative (do not implement yet)

- **Spatial interest management** — the O(n²)-per-zone concern is real in shape but
  unmeasured and depends on target player counts.
- **Subscription delta re-planning on scope change.**
- **`StatusEffect` hot/cold table split.**

All blocked on R3 data.

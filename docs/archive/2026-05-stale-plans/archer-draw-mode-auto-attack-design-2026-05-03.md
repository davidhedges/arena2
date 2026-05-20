# Archer Draw Mode Auto Attack Design

Date: 2026-05-03

## Goal

Archer should support two bow firing modes for auto attacks:

- `SHORT_DRAW`: the Archer can move while auto attacking, but auto attacks have lower damage and shorter range.
- `FULL_DRAW`: the Archer gets stronger, longer-range auto attacks, but voluntary movement resets the auto attack cadence before the shot starts.

This should affect only Archer auto attacks in V1. It should not change selectable abilities, spell casts, melee combo attacks, dodge, resources, defense, or generic combat stats yet.

The design should still leave a clean foundation for future class stances or combat modes that can affect more systems later.

## Current System Facts

Relevant code:

- `server/src/auto_attack.rs`
- `server/src/melee.rs`
- `server/src/progression.rs`
- `server/src/progression_catalog.shared.json`
- `server/src/game_loop.rs`
- `server/src/movement.rs`
- `server/src/player_physics.rs`
- `server/src/player_state.rs`

Important observations:

- Auto attacks already resolve through `auto_attack_catalog`, which owns damage, range, cooldown, parry/block behavior, airborne targeting, and stagger.
- `AutoAttackState` currently stores owner, target, combat profile, strike id, `next_swing_at`, and `pending_due`.
- Archer projectile auto attacks already resolve damage from auto attack gameplay and capture it into projectile release state.
- `game_tick` is the only writer of authoritative movement/physics state.
- `tick_auto_attacks` runs after player simulation in the game loop, so auto attack cadence can consume authoritative movement state without trusting client prediction.
- Movement input and physics are separate: yaw can change without movement, and roots can block movement even if input is pressed.

## Design Principle

Add a small, explicit combat mode layer, but make Archer auto attack the only V1 consumer.

Do not implement this as:

- Two fake Archer auto attack actions, such as `ARCHER_SHORT_DRAW_AUTO_ATTACK` and `ARCHER_FULL_DRAW_AUTO_ATTACK`.
- A status effect that secretly changes auto attack damage/range.
- A hard-coded `if ARCHER_BOW` branch scattered through projectile or melee code.
- A second Archer combat profile.

The clean boundary is:

- Combat profile decides which modes exist.
- Active mode records the player's current preference/state.
- Auto attack resolution opts into mode-aware gameplay rows.
- Other systems ignore mode until they intentionally opt in.

## Proposed Data Model

### Combat Mode Catalog

Add a public catalog table for modes supported by a combat profile:

```rust
#[table(accessor = combat_mode_catalog, public)]
pub struct CombatModeCatalog {
    #[primary_key]
    pub key: String, // combat_profile_id:mode_id
    pub combat_profile_id: String,
    pub mode_id: String,
    pub display_name: String,
    pub is_default: bool,
    pub sort_order: u32,
}
```

Seed only Archer modes in V1:

```json
{
  "combat_profile_id": "ARCHER_BOW",
  "mode_id": "SHORT_DRAW",
  "display_name": "Short Draw",
  "is_default": false,
  "sort_order": 10
}
```

```json
{
  "combat_profile_id": "ARCHER_BOW",
  "mode_id": "FULL_DRAW",
  "display_name": "Full Draw",
  "is_default": true,
  "sort_order": 20
}
```

Non-Archer profiles do not need mode rows yet. If a profile has no mode rows, auto attack resolution should behave exactly as it does today.

### Active Combat Mode

Add a server-owned active mode table:

```rust
#[table(accessor = active_combat_mode, public)]
pub struct ActiveCombatMode {
    #[primary_key]
    pub owner: Identity,
    pub combat_profile_id: String,
    pub mode_id: String,
    pub changed_at: Timestamp,
}
```

Add a reducer:

```rust
#[reducer]
pub fn set_combat_mode(ctx: &ReducerContext, mode_id: String) -> Result<(), String>
```

Validation:

- Resolve the owner's current derived combat profile.
- Require that `(combat_profile_id, mode_id)` exists in `combat_mode_catalog`.
- Upsert `active_combat_mode`.

Do not allow clients to set arbitrary mode strings that are not authored for the current combat profile.

Do not add mode versioning in V1, and do not reset cadence inside `set_combat_mode`. The reducer should only validate and upsert the active row. `tick_auto_attacks` is the single owner of auto attack cadence mutation; it will detect that the scheduled mode differs from the current mode, preserve the existing cadence progress, and recompute readiness against the new mode's gameplay.

### Auto Attack Gameplay Rows

Extend `auto_attack_catalog` to optionally include `mode_id` and movement policy:

```rust
#[table(accessor = auto_attack_catalog, public)]
pub struct AutoAttackCatalog {
    #[primary_key]
    pub key: String, // normalized COMBAT_PROFILE:MODE:ACTION, with mode omitted for legacy rows
    pub combat_profile_id: String,
    pub mode_id: String,
    pub action_id: String,
    pub base_damage: i32,
    pub range: f32,
    pub cooldown_ms: u64,
    pub movement_policy: String,
    pub uses_global_cooldown: bool,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub airborne_targeting_mode: String,
    pub applies_stagger: bool,
}
```

Movement policies:

- `ALLOW_MOVING`: voluntary movement does not reset cadence.
- `RESET_CADENCE_ON_VOLUNTARY_MOVE`: voluntary movement resets the cadence before the shot starts.

V1 Archer rows:

```json
{
  "combat_profile_id": "ARCHER_BOW",
  "mode_id": "SHORT_DRAW",
  "action_id": "AUTO_ATTACK_1",
  "base_damage": 14,
  "range": 11.0,
  "cooldown_ms": 1000,
  "movement_policy": "ALLOW_MOVING",
  "uses_global_cooldown": false,
  "parry_behavior": "PARRYABLE",
  "block_behavior": "BLOCKABLE",
  "airborne_targeting_mode": "ANY_TARGET",
  "applies_stagger": false
}
```

```json
{
  "combat_profile_id": "ARCHER_BOW",
  "mode_id": "FULL_DRAW",
  "action_id": "AUTO_ATTACK_1",
  "base_damage": 22,
  "range": 18.0,
  "cooldown_ms": 1000,
  "movement_policy": "RESET_CADENCE_ON_VOLUNTARY_MOVE",
  "uses_global_cooldown": false,
  "parry_behavior": "PARRYABLE",
  "block_behavior": "BLOCKABLE",
  "airborne_targeting_mode": "ANY_TARGET",
  "applies_stagger": false
}
```

Existing non-mode rows can remain valid:

```json
{
  "combat_profile_id": "SWORD_AND_SHIELD",
  "action_id": "AUTO_ATTACK_1",
  "base_damage": 25,
  "range": 2.5,
  "cooldown_ms": 900,
  "movement_policy": "ALLOW_MOVING"
}
```

For compatibility, missing `mode_id` should deserialize to `""`, and missing `movement_policy` should deserialize to `ALLOW_MOVING`.

### Auto Attack State

Extend `AutoAttackState` with mode and movement schedule context:

```rust
pub struct AutoAttackState {
    pub owner: Identity,
    pub target: Identity,
    pub combat_profile_id: String,
    pub mode_id: String,
    pub strike_id: String,
    pub cadence_started_at: Timestamp,
    pub next_swing_at: Timestamp,
    pub pending_due: bool,
    pub movement_epoch_at_schedule: u64,
}
```

This records the conditions under which the next swing was scheduled. `cadence_started_at` is required because mode changes preserve current draw progress. `next_swing_at` alone is not enough to recompute readiness after switching between modes with different cadence values.

## Movement Tracking

Add one authoritative movement epoch field to `PlayerState`:

```rust
pub struct PlayerState {
    // existing fields...
    pub voluntary_move_epoch: u64,
}
```

`PlayerState` already carries tick-scoped movement facts such as `movement_blocked`, `move_speed_multiplier`, and `movement_context_tick`. Keeping the epoch there avoids a new table and keeps the writer ownership aligned with the existing movement context sync.

Definition of accepted voluntary locomotion intent:

- Forward input above movement deadband counts.
- Strafe input above movement deadband counts.
- Jump input counts if it is accepted as movement.
- Yaw-only input does not count.
- Forced movement from charge, knockback, shove, or root correction does not count in V1.
- Movement input while rooted should not increment the epoch. Full draw should not punish a player for pressing movement while hard-rooted and unable to move.

Update this field during the authoritative movement tick after input is consumed and movement blocking is known. Use the same movement input deadband as `velocity_from_intent` so cadence reset behavior matches actual movement intent.

Do not make `auto_attack.rs` read raw client command tables. Auto attack should consume the compact authoritative movement context.

## Runtime Resolution

### Mode Lookup

Add helpers:

```rust
resolved_auto_attack_mode_for_owner(ctx, owner, combat_profile_id) -> String
```

Rules:

- If the profile has no combat modes, return `""`.
- If an active mode exists and is valid for the current profile, return it.
- Otherwise return the profile's authored default mode if present.
- If no default is present, use the lowest `sort_order` mode for that profile.

V1 should initialize Archer to `FULL_DRAW` when the Archer combat profile is applied. The fallback rule is there to keep runtime behavior deterministic if an active row is missing, not to replace explicit initialization.

This fallback also makes rollout safe for existing Archer players without a backfill migration. If they do not have an `ActiveCombatMode` row yet, lookup selects the authored default `FULL_DRAW`; only if a mode-enabled profile has no default should it fall back to lowest `sort_order`.

### Gameplay Lookup

Update auto attack gameplay lookup from:

```rust
auto_attack_gameplay_for_profile_mode_action(ctx, combat_profile, mode_id, action_id)
```

to an internal mode-aware path:

```rust
auto_attack_gameplay_for_profile_mode_action(ctx, combat_profile, mode_id, action_id)
```

Lookup order:

1. Exact `(combat_profile_id, mode_id, action_id)`.
2. Legacy fallback `(combat_profile_id, "", action_id)`.

For Archer V1, require exact mode rows for `ARCHER_BOW` so the two modes cannot accidentally share stale fallback tuning.

### Scheduling

When arming or rescheduling auto attack:

1. Resolve combat profile.
2. Resolve active auto attack mode.
3. Resolve mode-aware auto attack gameplay.
4. Read current `PlayerState.voluntary_move_epoch`.
5. Store `mode_id` and `movement_epoch_at_schedule`.
6. Set `cadence_started_at = from_time`.
7. Set `next_swing_at = from_time + cadence`.

### Ticking

At the start of each `tick_auto_attacks` row:

1. Resolve current mode.
2. If mode differs from the scheduled row's `mode_id`, recompute cadence without discarding progress:
   - Resolve gameplay for the new mode.
   - Compute `elapsed = now - cadence_started_at`.
   - Compute the new mode's cadence from its gameplay and current attack speed.
   - If `elapsed >= new_cadence`, mark the swing due now.
   - Otherwise set `next_swing_at = cadence_started_at + new_cadence`.
   - Update stored `mode_id` to the current mode.
   - Capture current `voluntary_move_epoch` as the movement epoch for the new mode, so movement before switching into `FULL_DRAW` does not retroactively reset the draw.
3. Resolve gameplay for the current mode.
4. If gameplay movement policy is `RESET_CADENCE_ON_VOLUNTARY_MOVE`, compare current `voluntary_move_epoch` to `movement_epoch_at_schedule`.
5. If movement epoch changed, reset cadence from `now`.
6. Continue normal target/range/due-swing checks.

This makes `FULL_DRAW` feel like a draw that must be held steady. Moving before the shot starts starts the draw again. It does not clear the target.

Mode switching is not movement. Switching from `FULL_DRAW` to `SHORT_DRAW` should fire immediately if enough cadence time has already elapsed for the short-draw row. Switching from `SHORT_DRAW` to `FULL_DRAW` should count the current elapsed cadence progress toward full draw; if not enough time has elapsed, the shot waits only for the remaining full-draw time.

### Pending Due And Out Of Range

Keep existing pending-due semantics for target temporarily out of range.

However, mode-specific range matters:

- `SHORT_DRAW` out-of-range threshold uses short range.
- `FULL_DRAW` out-of-range threshold uses full range.
- If the target is out of range, mark pending due as today.
- If the player moves while full draw is pending due, cadence resets.

That last rule prevents a full-draw Archer from charging a due shot out of range, moving into range, and firing instantly after movement.

## Projectile Interaction

Mode-specific auto attack range must affect projectile range.

For Archer auto attack:

- Target validation uses resolved auto attack `range`.
- Projectile release uses resolved auto attack `damage`.
- Projectile max distance should clamp to resolved auto attack `range` for this shot.

Do not let `SHORT_DRAW` acquire at 11m but still spawn an arrow with a 35m gameplay distance. The projectile instance should carry the shot's resolved max distance.

For V1, set projectile `max_distance` to `min(projectile_definition.max_distance, gameplay.range)` when resolving projectile delivery for auto attacks. Do not add `max_projectile_distance_override` yet. Promote to an explicit override field later only if selectable Archer shots need projectile distance tuning that differs from action range.

## Client UX

V1 client requirements:

- Show the current Archer draw mode somewhere near the action bar/loadout UI.
- Provide an Archer class-specific loadout ability, `ARCHER_DRAW_MODE_TOGGLE`, that appears on the action bar and uses that slot's keybind.
- The UI should not imply that non-Archer classes have modes unless their combat profile has mode catalog rows.
- When mode changes, local prediction should preserve existing auto attack cadence progress and re-evaluate readiness against the new mode.

Presentation should keep action identity stable:

- Keep `AUTO_ATTACK_1` as the action id.
- Do not create fake action ids for short/full draw.
- Add `combat_mode_id` to combat events later only if VFX or animation needs mode-specific presentation.

For V1, identical animation for both modes is acceptable. The gameplay distinction is enough to validate the system.

## Sync-Time Checks

The current catalog sync path upserts rows; it is not a separate startup validation framework. Add checks in catalog sync helpers and focused tests for the contracts this feature depends on:

- `combat_mode_catalog` keys are unique.
- Every `auto_attacks[]` row with non-empty `mode_id` references an authored mode for that combat profile.
- `ARCHER_BOW` has both `SHORT_DRAW` and `FULL_DRAW` auto attack rows for `AUTO_ATTACK_1`.
- `ARCHER_BOW` defaults to `FULL_DRAW`.
- `movement_policy` is one of the supported enum values.
- Mode-aware duplicate keys are rejected.
- Non-mode auto attack rows remain valid for legacy profiles.

Runtime validation should enforce:

- `set_combat_mode` rejects modes not authored for the current combat profile.
- If a player changes class/profile, the class/combat-profile apply path owns active mode normalization. It should clear invalid active mode rows or initialize the new profile default, including `FULL_DRAW` for Archer.
- Auto attack lookup never silently uses an Archer legacy fallback if `ARCHER_BOW` is mode-enabled.

## Implementation Phases

### Phase 1: Server Gameplay Slice

- Add combat mode catalog data to `progression_catalog.shared.json`.
- Add `CombatModeCatalog` and `ActiveCombatMode` tables.
- Add sync-time checks for combat modes and mode-aware auto attack rows.
- Add `set_combat_mode`.
- Update the class/combat-profile apply path to initialize Archer `FULL_DRAW` and clear or normalize invalid mode rows when profiles change.
- Add `PlayerState.voluntary_move_epoch`.
- Update `game_tick` to maintain voluntary movement epoch on accepted voluntary movement intent.
- Use movement input deadband and movement-blocking state.
- Add `mode_id` and `movement_policy` to auto attack catalog rows.
- Update auto attack keying to include mode when present.
- Add Archer `SHORT_DRAW` and `FULL_DRAW` rows.
- Keep non-Archer rows mode-free.
- Update auto attack gameplay lookup to resolve exact mode-aware rows for mode-enabled combat profiles.
- Extend `AutoAttackState` with mode and movement schedule context.
- On schedule, capture current mode and movement epoch.
- On tick, preserve cadence progress when active mode changed.
- On tick, reset cadence when movement policy is `RESET_CADENCE_ON_VOLUNTARY_MOVE` and movement epoch changed.
- Ensure mode switch preserves draw progress while full-draw movement resets cadence in `tick_auto_attacks` without clearing target.
- Ensure Archer auto attack projectile max distance resolves from the active mode's auto attack range.
- Add tests for:
  - default mode resolution and invalid mode rejection,
  - yaw-only input does not increment epoch,
  - forward/strafe accepted movement increments epoch,
  - blocked movement does not increment epoch,
  - forced/special movement does not count as voluntary movement in V1,
  - non-Archer auto attacks resolve exactly as before,
  - short draw moving while firing,
  - full draw movement reset,
  - switching from full draw to short draw fires immediately when elapsed progress satisfies short-draw cadence,
  - switching from short draw to full draw preserves elapsed progress and waits only for remaining full-draw cadence,
  - full draw out-of-range pending-due target re-enters range without movement and fires immediately,
  - full draw out-of-range pending-due target re-enters range after movement and waits for a reset cadence,
  - `SHORT_DRAW` projectile carries short max distance,
  - `FULL_DRAW` projectile carries full max distance,
  - damage captured into projectile release differs by mode.

This phase should land as one vertical slice. Catalog rows, movement epoch tracking, lookup, cadence reset, and projectile range clamp are tightly coupled enough that landing them separately would create infrastructure with no observable behavior.

### Phase 2: Client Controls

- Expose combat modes in generated bindings.
- Add `ARCHER_DRAW_MODE_TOGGLE` as a `COMBAT_MODE_TOGGLE` ability with a default Archer action-bar slot assignment.
- Dispatch `COMBAT_MODE_TOGGLE` abilities through the existing action-bar input path; do not bind a hard-coded class key.
- Add Archer-only mode UI/feedback separately from the reducer path; V1 input does not require the UI to own gameplay state.
- Add reducer call for mode changes.
- Make local auto attack presentation preserve cadence progress after mode changes.

## Testing Requirements

Server tests:

- Archer defaults to the authored default draw mode.
- `set_combat_mode` rejects invalid modes.
- Sword and Greatsword auto attacks are unchanged by mode infrastructure.
- `SHORT_DRAW` Archer auto attack can start while voluntary movement epoch changes.
- `FULL_DRAW` Archer auto attack resets cadence when voluntary movement epoch changes before swing start.
- Yaw-only input does not reset full draw cadence.
- Rooted movement input does not reset full draw cadence unless accepted movement actually occurs.
- Switching between short draw and full draw preserves elapsed cadence progress in `tick_auto_attacks`, not in `set_combat_mode`.
- Switching from full draw to short draw fires immediately when elapsed progress satisfies short-draw cadence.
- Switching from short draw to full draw preserves elapsed progress and waits only for remaining full-draw cadence.
- Full draw out-of-range pending-due target re-enters range without movement and fires immediately.
- Full draw out-of-range pending-due target re-enters range after movement and waits for a reset cadence.
- Short draw resolves lower damage and shorter range.
- Full draw resolves higher damage and longer range.
- Archer projectile release captures mode-specific damage and max distance.

Unity/client tests:

- Draw mode UI appears only when current combat profile has modes.
- Archer has a class-specific draw-mode ability on the action bar.
- Triggering the draw-mode action-bar slot calls `set_combat_mode`.
- UI updates when `ActiveCombatMode` changes.
- Existing non-Archer loadout UI does not show draw controls.

## Pinned V1 Decisions

- Archer defaults to `FULL_DRAW` so existing expected range/damage remains the baseline.
- The Archer draw-mode toggle is a class-specific action-bar ability, not a hard-coded key.
- Accepted jump input counts as voluntary movement and resets `FULL_DRAW` cadence.
- Forced movement does not reset `FULL_DRAW`; this mode is about player voluntary movement. Revisit after playtest.
- Selectable Archer abilities do not read draw mode in V1. Draw mode affects Archer auto attacks only.

## Recommendation

Implement combat modes as reusable state, but make Archer auto attack the only V1 gameplay consumer.

This keeps the current request tightly scoped while avoiding a bespoke Archer-only flag. The important abstraction is not "Archer can move while shooting." The important abstraction is "auto attack gameplay can be selected by the current combat mode, and movement can reset cadence through an authored policy."

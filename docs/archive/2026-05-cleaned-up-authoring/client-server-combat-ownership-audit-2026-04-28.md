# Client/Server Combat Ownership Audit - 2026-04-28

This document is historical. Use `docs/combat-authoring-contract.md` for the current combat authoring contract.

2026-05-02 update: stamina, block movement slow, and `BLOCK_MOVE_SPEED_MULTIPLIER` were removed. Block and parry now share the same defensive gameplay shape and differ primarily by presentation.

This note tracks combat-specific client code that appears to own gameplay policy, duplicate server rules, or route actions through one-off branches. These are not all immediate bugs. The purpose is to identify likely drift points before adding more combat features.

## Highest-Value Cleanup Candidates

### 1. Block Move Speed Multiplier Was Duplicated

Status: removed on 2026-05-02.

The duplicated rule no longer exists in client code, server combat movement modifiers, or the shared progression catalog.

Why this matters:

Historical context: block movement speed used to be authoritative gameplay tuning. It was removed when block stopped applying movement slow.

Implemented fix:

Superseded by removal of block movement slow and the local block movement-restriction bridge.

### 2. Default Global Cooldown Duration Is Duplicated

Status: fixed on 2026-04-28.

Client:

- `Assets/Arena/Runtime/Simulation/LocalCombatState.cs`
- `Assets/Arena/Runtime/Input/MeleeInputHandler.cs`
- Melee prediction reads `GameplayTuning.ResolveDefaultGlobalCooldownDurationMs(...)` from `CombatRuleCatalog`.

Server:

- `server/src/spells/cooldowns.rs`
- `stamp_global_cooldown(...)` resolves `DEFAULT_GLOBAL_COOLDOWN_MS` through the shared progression combat rules.
- Authored in `server/src/progression_catalog.shared.json`.

Why this matters:

The server owns actual cooldown stamping. The client only predicts cooldown overlays/rejection. If GCD duration changes server-side, melee prediction can drift.

Implemented fix:

Published `DEFAULT_GLOBAL_COOLDOWN_MS` through the existing combat-rule table. The server uses it when stamping default global cooldown rows, and the client uses it for melee GCD prediction. `1500ms` remains only as a fallback if catalog data is unavailable during startup.

## Medium-Risk Drift Points

### 3. Fixed Charge Still Has Client Routing Policy

Client:

- `Assets/Arena/Runtime/Input/FixedActionDispatcher.cs`
- Explicitly branches `CHARGE`, resolves the subclass fixed-action ability, then sends normal `CastRequest` for that ability's spell id.
- Owns visibility/enabled checks such as selected-target requirement, cooldown key resolution, and GCD gating.

Server:

- `server/src/spells/casting.rs` routes `CHARGE` spell behavior through the special movement/cast path.
- `server/src/progression.rs` publishes subclass fixed-action bindings.

Why this matters:

This is currently understandable, but it is still a one-off fixed-action branch. More charge-like fixed actions would likely create more client policy branches unless action routing becomes behavior-driven.

Likely fix:

Expose fixed-action dispatch behavior through catalog data so the client routes by authored behavior/capability, not by hardcoded fixed action id.

### 4. Fixed Action Availability Is Still Hardcoded Around Dodge/Charge

Client:

- `Assets/Arena/Runtime/Input/FixedActionDispatcher.cs`
- Explicit branches for `DODGE` and `CHARGE`.
- Owns visibility, enabled state, cooldown key resolution, target requirement, charge-count handling, and dispatch routing.

Server:

- `server/src/movement_actions.rs`
- Owns authoritative fixed action validation and execution.
- `server/src/progression.rs` publishes fixed-action bindings.

Why this matters:

This is acceptable while there are only two fixed actions, but it will not scale cleanly. More fixed actions will probably add more client branches.

Likely fix:

Create a fixed-action behavior/catalog row with fields like dispatch kind, requires target, cooldown action id, uses GCD, and resource/charge behavior.

## Prediction That Should Be Watched

### 6. Defense Prediction Applies Local State Before Server Acceptance

Client:

- `Assets/Arena/Runtime/Input/DefenseInputHandler.cs`
- Parry triggers local animation immediately.
- Block sets local blocking state before server acceptance.
- The old `Assets/Arena/Runtime/Input/LocalBlockMovementRestriction.cs` helper was removed.

Server:

- `server/src/defense.rs`
- Can reject parry/block due to disabling status or defense recovery.

Why this matters:

This is intentional-feeling prediction, but the prediction is not driven by a shared defense availability contract. If defense rules change, client presentation/prediction can become misleading.

Likely fix:

Publish defense action tuning/availability rules or add a client resolver that consumes server-published defense rules and local replicated state.

## General Recommendation

Prioritize small replicated-rule wins before larger action routing refactors:

1. Move block move speed multiplier into replicated combat rules.
2. Move default GCD duration into replicated combat rules.
3. Then revisit fixed-action dispatch and Shield Charge routing as one design pass.

The goal should not be to remove prediction. The goal is to make prediction consume server-owned facts rather than embedding gameplay policy in input handlers.

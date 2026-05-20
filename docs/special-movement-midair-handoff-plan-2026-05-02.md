# Special Movement Mid-Air Handoff Plan

## Problem And Principle

Special-movement abilities such as dodge, charge, and gap-closers currently bypass the normal locomotion state machine during their authored movement window, then return control inconsistently when the movement ends.

The visible bug: using dodge in the air keeps server position roughly correct, but the rig snaps into a planted ground idle pose mid-air while physics is still falling, then lands normally. The snap is presentation forcing a ground state, not the server teleporting the body to ground.

The design principle to restore:

- `grounded` has one authority: normal physics jump and landing transitions in `game_tick`.
- Special movements are terminal: they own position, velocity, and yaw during their window, then hand control back to normal locomotion.
- Animator routing after special movement is state-machine driven by `Grounded` and `FreeFall`, not hardcoded special-case exits.

## Server Layer

Touch:

- `server/src/game_loop.rs`
- `tick_special_movement_runtimes`
- `sample_special_movement_pose`
- Normal landing path in `simulate_non_dummy_player_kinematics`, especially `game_loop.rs:1005-1022`
- `server/src/player_physics.rs`
- Preserve `resolve_player_physics_commit` gating

Current issue:

- `tick_special_movement_runtimes` writes `physics.grounded = (resolved_y - ground_y).abs() <= 0.001` every tick and on the final tick.
- That conflicts with the invariant in `server/src/player_physics.rs` that `grounded` changes only through explicit jump and land transitions in `game_tick`.
- The existing comment in `server/src/game_loop.rs:1113-1121` says the final reclassification was added to prevent residual authored-movement velocity from carrying into normal physics, because airborne horizontal velocity lock could otherwise slide the player past the destination.

Target changes:

1. Stop reclassifying `physics.grounded` from terrain inside `tick_special_movement_runtimes`.

2. During active special movement, update only:
   - `pos_x`
   - `pos_y`
   - `pos_z`
   - `yaw`
   - runtime-derived velocity
   - `updated_at`

3. On special movement finish:
   - Write the final sampled pose.
   - Zero velocity if needed to prevent residual authored movement from carrying into normal physics.
   - Delete `special_movement_runtime`.
   - Do not snap or terrain-classify `grounded`.

4. Let the next normal `tick_player` run the standard physics branch:
   - If above ground and already airborne, the airborne branch applies gravity.
   - If crossing ground while descending, the existing landing check marks grounded.
   - If ending over a ledge, the player remains airborne and falls.

5. Preserve fixed-Y semantics during the special movement itself.
   - `SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y` still holds aerial height during dodge or other fixed-height movement.
   - Only the post-runtime handoff changes.

6. Keep the zero-velocity UX intentional.
   - Aerial dodge ending mid-flight should drop mostly straight down after the authored dodge window instead of carrying dodge horizontal momentum indefinitely; this matches the current WoW-style stop-at-end feel.

## Grounded Handoff Rationale

The final terrain reclassification was guarding a real charge problem: if charge ends and leaves nonzero horizontal velocity while the player is airborne, the airborne velocity lock can slide the player beyond the intended endpoint. The design fix preserves the useful part of that behavior by zeroing velocity at runtime end, but removes terrain-based `grounded` writes from the special-movement reducer.

The stale-grounded case must be handled deliberately:

- For ground-following special movements, such as grounded charge using `SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK`, the runtime samples `resolved_y` from terrain every tick. If the movement starts grounded, carrying `grounded == true` into the post-runtime tick is acceptable because final `pos_y` should already equal final terrain height. The normal grounded branch can continue without a snap.
- For fixed-Y special movements, carrying stale `grounded == true` is not acceptable if final `pos_y` is above or below terrain. The current normal physics pre-step stabilization snaps grounded players to sampled ground before it can detect a fall. That would recreate the same class of teleport/snap bug.
- Therefore, fixed-Y special movement must preserve an airborne handoff state; even one tick of stale `grounded == true` can run grounded pre-step stabilization and snap vertically before the normal airborne check gets a chance to run.

This means the first implementation should preserve the existing policy split:

- Grounded charge and grounded linear gap-closers use ground-following collision policy and may carry `grounded == true`.
- Air dodge, airborne charge, leap-style gap-closers, and any fixed-Y movement carry `grounded == false` through the runtime and into the normal post-runtime tick.

If a future grounded leap is desired, add an explicit launch/airborne transition before beginning the fixed-Y runtime. Do not add another terrain reclassification at runtime end.

## Charge And Gap-Closer Semantics

Charge is not identical to dodge and needs separate expectations.

For grounded charge:

- Expected runtime policy is ground-following, not fixed-Y.
- Uphill charge should end with `pos_y` at the uphill terrain height and `grounded == true` carried through because the sampled final pose is on terrain.
- Downhill charge should end with `pos_y` at the downhill terrain height and `grounded == true` carried through because the sampled final pose is on terrain.
- Charge onto a platform should end grounded only if the baked path and final terrain sampling place the caster on that platform.
- Charge over a ledge should either be rejected/baked short by collision/path rules or hand off as airborne if the movement policy explicitly allows leaving ground.

For airborne or fixed-Y charge:

- The ability should carry `grounded == false` through the runtime.
- On end, velocity is zeroed and the next normal tick applies gravity/landing.
- No end-of-runtime terrain classification should mark the player grounded just because final XZ has terrain below.

For gap-closers:

- The gap-close plan in `docs/gap-closer-authoring-runtime-plan-2026-05-01.md` is consistent with this design because it already treats `special_movement_runtime` as the shared authoritative movement transport and distinguishes movement kinds such as `LINEAR`, `LEAP`, and `TELEPORT`.
- `LINEAR` grounded gap-closers should use ground-following movement and preserve grounded state only when final pose is on terrain.
- `LEAP` or fixed-height gap-closers should enter/maintain airborne state and hand off to normal falling/landing.
- `TELEPORT` and `TELEPORT_BEHIND` need destination validation; if the destination is on walkable terrain, grounded state can be carried only if the teleport was authored as grounded. If authored as an aerial teleport, normal falling resumes.

Removing per-tick `grounded` writes must not weaken block-on-terrain behavior:

- Path baking and horizontal collision still use terrain/collision queries while the runtime is active.
- Grounded linear charge/gap-closers still sample terrain for `resolved_y`.
- Rejection/fizzle behavior for blocked gap-closers remains in the gap-close validation/bake layer, not in grounded classification.

## Animator Layer

Touch:

- `Assets/Arena/Content/Animation/Arena_Character.controller`
- State: `Dodge` around line `8759`
- Compare with `JumpStart`, `JumpStartCombat`, `InAir`, `InAirCombat`, `JumpLand`, and `JumpLandCombat`

Current issue:

- `Dodge` has two outgoing transitions:
  - `Dodge -> IdleCombat`
  - `Dodge -> Idle Walk Run Blend`
- Both fire at exit time `0.92`.
- They branch only on `InCombat`.
- They do not check `Grounded` or `FreeFall`.

Target transition pattern:

1. Constrain existing ground exits:

   - `Dodge -> IdleCombat`
     - `InCombat == true`
     - `Grounded == true`
     - exit time `0.92`

   - `Dodge -> Idle Walk Run Blend`
     - `InCombat == false`
     - `Grounded == true`
     - exit time `0.92`

2. Add airborne exits:

   - `Dodge -> InAirCombat`
     - `InCombat == true`
     - `FreeFall == true`
     - exit time `0.92`

   - `Dodge -> InAir`
     - `InCombat == false`
     - `FreeFall == true`
     - exit time `0.92`

3. Apply the same pattern to other special-movement-driven transient states.
   - Charge and gap-closer presentation should not hard-exit to planted ground poses unless `Grounded == true`.

`Grounded == true` and `FreeFall == false` are redundant because `PlayerAnimator.Update` sets them as inverses in `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs:1947-1949`. Use `Grounded == true` for ground exits and `FreeFall == true` for air exits to keep the controller readable.

## Client C# Layer

Touch:

- `Assets/Arena/Runtime/Input/LocalMovementPredictionDriver.cs`
  - `DriveLocalSpecialMovement`
  - `ResetAfterSpecialMovement`
- `Assets/Arena/Runtime/Input/MovementPrediction.cs`
- `Assets/Arena/Runtime/Simulation/ClientSimulationState.cs`
  - `SeedLocalAuthoritativePositionFromSpecialMovementEnd`
  - `SeedRemoteInterpolationFromSpecialMovementEnd`
- `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs`
  - `TryRecoverDodgeLocomotionFromCompletedRuntime`
  - `TryResolveLocomotionRecovery`

Target changes:

1. Keep local prediction aligned with the server.
   - `DriveLocalSpecialMovement` can continue sampling fixed-Y movement during the runtime.
   - Remove the instantaneous fixed-Y grounded classification in `Assets/Arena/Runtime/Input/LocalMovementPredictionDriver.cs:275-285`.
   - `_currentPredictedState.Grounded` at the special-movement boundary should mirror the carried server/predicted grounded state, not a fresh height query.

2. After runtime clear, re-enter normal prediction.
   - The next prediction tick should use `MovementPrediction.Step`.
   - Gravity and landing should resolve through the same path as jump and fall.
   - If the runtime carried `grounded == false`, the next predicted step falls.
   - If the runtime carried `grounded == true` because it was ground-following and ended on terrain, the next predicted step remains grounded.

3. Confirm `ClientSimulationState` seed behavior.
   - `SeedLocalAuthoritativePositionFromSpecialMovementEnd` updates server/render position, zero velocity, yaw, snapshot time, and version, but does not write `_grounded`.
   - `SeedRemoteInterpolationFromSpecialMovementEnd` appends a snapshot using the existing `_grounded`.
   - That is consistent with the design. Do not add special-movement terrain classification there.

4. Remove duplicate dodge recovery once controller routing is correct.
   - Delete or collapse `TryRecoverDodgeLocomotionFromCompletedRuntime`.
   - Delete or collapse the `DodgeStateHash` branch in `TryResolveLocomotionRecovery`.

5. Keep only generic transient recovery if needed.
   - Any remaining C# recovery should be state-agnostic and conservative.
   - It should not decide ground versus air independently of animator parameters.

## Migration Order

1. Animator routing as the first design step.
   - This is not a throwaway band-aid. Without it, the server can be correct while Unity still exits `Dodge` into planted idle.
   - Manual verification:
     1. Enter play mode in a training/open-world scene where jump and dodge are available.
     2. Jump.
     3. Press dodge before landing.
     4. Confirm the dodge clip exits to `InAir` or `InAirCombat`, not `IdleCombat` or `Idle Walk Run Blend`.
     5. Confirm the character remains visually airborne until the normal landing transition.
     6. Repeat dodge while grounded and confirm it still exits to grounded idle/locomotion.
   - Automated editor coverage to add if feasible: `Assets/Arena/Editor/CombatAnimatorControllerUpgrader.cs` or a new edit-mode test should assert that `Dodge` has grounded exits gated by `Grounded` and airborne exits gated by `FreeFall`.

2. Server grounded ownership change.
   - Modify `tick_special_movement_runtimes` so it no longer terrain-reclassifies `grounded`.
   - Add focused Rust tests in `server/src/player_physics.rs` near the existing physics tests:
     - `special_movement_handoff_preserves_airborne_state_for_fixed_y_runtime`
     - `special_movement_handoff_preserves_grounded_state_for_ground_following_runtime`
     - `special_movement_handoff_does_not_snap_fixed_y_airborne_to_ground`
   - If the direct runtime code is hard to unit test because it needs `ReducerContext`, extract a pure handoff helper and test that helper in `player_physics.rs`.
   - Manual verification:
     1. Jump.
     2. Dodge mid-air.
     3. Observe that server/debug position continues falling after dodge end.
     4. Confirm no vertical teleport occurs at the runtime-end tick.

3. Local prediction alignment.
   - Remove client fixed-Y grounded classification from `DriveLocalSpecialMovement`.
   - Add or update play-mode/manual verification:
     1. Enable the netcode debug overlay.
     2. Jump and dodge mid-air as the local player.
     3. Watch correction distance on the special-movement clear frame.
     4. Confirm no large correction spike and no visual snap.
     5. Repeat as an observing remote client if multiplayer test setup is available.
   - Automated test suggestion if play-mode tests exist: add a predictor fixture around `MovementPrediction.Step` named `prediction_after_fixed_y_special_movement_uses_carried_airborne_state`.

4. Charge-specific verification.
   - Manual verification:
     1. Charge a target on flat terrain from grounded.
     2. Charge a target uphill.
     3. Charge a target downhill.
     4. Charge toward a target near a ledge or platform edge.
     5. Confirm charge stops at the authored endpoint, does not slide past the target, and only falls when the movement policy actually leaves ground.
   - Add Rust test suggestions near charge/gap-close tests:
     - `grounded_charge_handoff_remains_grounded_on_sampled_terrain`
     - `airborne_charge_handoff_resumes_falling_without_endpoint_slide`
     - `gap_close_linear_handoff_preserves_grounded_on_valid_destination`
     - `gap_close_leap_handoff_preserves_airborne_until_landing`

5. C# cleanup.
   - Remove duplicate dodge recovery paths after controller, server, and prediction behavior are stable.
   - Manual verification:
     1. Re-run grounded dodge.
     2. Re-run aerial dodge.
     3. Re-run dodge while moving and while stationary.
     4. Confirm no stuck `Dodge` state and no forced mid-air idle.

## Risk And Edge Cases

- Charge ending mid-air should enter normal falling flow instead of snapping grounded.
- Grounded charge ending uphill/downhill should remain grounded only because the runtime followed terrain, not because runtime end reclassified it.
- Gap-closer ending on a ledge should fall if the movement policy allows the final XZ to be over empty space.
- Dodge starting grounded but ending over a drop should hand off to normal physics and fall; if this uses fixed-Y, it must become airborne before or during the runtime.
- Dodge interrupted by stagger or disabling status should clean up runtime without forcing ground.
- Prediction divergence on the transition tick must be monitored; client and server should carry the same post-runtime grounded state.
- Fixed-Y dodge over rising terrain should preserve authored height; collision/path baking should block or shorten invalid paths rather than elevating the player.
- Remote interpolation should not seed a fake grounded snapshot after special movement ends.
- Verify ground-idle transitions do not fire when `Grounded == false`.

## Cleanup Targets

Delete or collapse after the refactor:

- `PlayerAnimator.TryRecoverDodgeLocomotionFromCompletedRuntime`
- `PlayerAnimator.TryResolveLocomotionRecovery` special `DodgeStateHash` branch, unless retained as a generic stuck-state fallback
- Any fixed-Y special-movement logic that computes `grounded` directly from `resolved_y - ground_y`
- Any dodge-only post-runtime animator recovery that targets ground idle
- Any charge or gap-closer equivalent that bypasses `Grounded` / `FreeFall` animator routing
- The fixed-Y grounded classification branch in `LocalMovementPredictionDriver.DriveLocalSpecialMovement`

Keep:

- Fixed-Y collision policy during active runtime
- `player_physics` commit gating
- Normal landing checks in `game_tick`
- Velocity zeroing at special-movement end unless movement design explicitly changes to carry momentum

## Interim Landing Target If Server Work Slips

If the full server and prediction refactor cannot land immediately, the controller routing change can be shipped first because it is also part of the final design:

- Edit `Assets/Arena/Content/Animation/Arena_Character.controller`.
- Gate existing `Dodge -> IdleCombat` and `Dodge -> Idle Walk Run Blend` transitions with `Grounded == true`.
- Add `Dodge -> InAirCombat` with `FreeFall == true`, `InCombat == true`, and exit time `0.92`.
- Add `Dodge -> InAir` with `FreeFall == true`, `InCombat == false`, and exit time `0.92`.

Benefit:

- Fixes the visible planted-pose snap if physics is already airborne.

Cost:

- Does not restore server ownership of `grounded`.
- Leaves special movement and normal locomotion with overlapping state authority.
- Leaves duplicate C# dodge recovery paths in place.
- Does not fully address charge or gap-closer mid-air endings.

This is an acceptable interim landing target only because it is also the first step of the design fix, not a separate dodge-only workaround.

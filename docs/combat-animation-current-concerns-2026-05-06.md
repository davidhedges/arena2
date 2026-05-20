# Combat Animation Current Concerns - 2026-05-06

## Context

This note captures the animation design debt that remains after adding `INTIMIDATE`, cleaning up the failed out-of-combat weapon toggle experiment, and returning the weapon presentation model to two states:

- out of combat: weapon stowed, normal locomotion
- in combat: weapon drawn, combat locomotion/stance

The removed third state, "weapon drawn but not in combat", was not supported by the authored greatsword asset set. Keeping it would have forced code to fake a pose the animation set does not actually provide.

## Current Shape

`PlayerAnimator` remains the owner of character-level animation orchestration. The largest shared playback policies have been moved out, but the class still intentionally owns the scene-state decisions that should not be split across multiple components:

- combat stance transitions and immediate stance snaps
- weapon visual handoff timing during draw/sheath/enter/exit transitions
- spell playback layer selection
- spell and melee entry decisions that depend on grounded state, locomotion, combat stance, and weapon visuals

Animation-set slot binding is handled by `CombatAnimationSetBinder`. Hit reactions, stagger, knockdown, and hard crowd-control presentation are handled by `CombatStatusReactionController`. Shared melee, spell, and phased playback state is handled by `CombatActionPlaybackController`. `PlayerAnimator` still exposes the compatibility methods used by entities, but those methods delegate to the focused controllers.

This is more workable, but it still means new stance or weapon-handoff features can land in `PlayerAnimator` by default unless the ownership boundary is enforced.

The reassessed animation-refactor plan treated the earlier cleanup as the mechanical pass, not the end state. The shared combat action playback core has now been extracted together: melee, spell, and phased playback share one owner for bank slots, active presentation records, layer-recovery, lower-body unlock, visual-interrupt, and preemption rules.

This does not supersede `docs/archive/2026-05-stale-plans/spell-system-unification-plan-2026-05-05.md`. The spell-system unification plan remains the active feature track; action playback extraction should only happen before that plan if it directly blocks a spell-unification step.

## Concerns

### 1. Spell playback stance policy is now authored data

`PlaySpellAnimation` no longer decides whether to enter combat stance from local playback mode details alone. `WeaponSpellAnimationEntry` now authors:

- `requiresCombatStance`
- `combatEntryMode: None | Immediate | AnimatedAfterCast | ImmediateForFullBodyAnimatedAfterUpperBody`
- `playbackLayer: UpperBodyWhileMoving | FullBody | UpperBody | LeftGesture`

The intentionally long mixed combat-entry mode exists to preserve the previous Warrior buff behavior: full-body playback snaps into combat before the spell; upper-body-while-moving playback starts the spell and then requests animated combat entry.

### 2. Immediate combat entry is necessary but sharp

`EnterCombatImmediate()` is still valid for actions where waiting on a draw animation breaks gameplay or hides the intended action, such as Charge, Parry, and local melee attacks from a stowed state.

The risk is accidental use. It bypasses the authored draw/enter transition by design. Any new caller should have to justify why the action must snap directly to combat stance.

### 3. Shared action playback now has one owner

Static animation-set slot binding has been extracted to `CombatAnimationSetBinder`. Status and hit reaction presentation has been extracted to `CombatStatusReactionController`.

`CombatActionPlaybackController` now owns the shared runtime playback core that melee, spell, and phased playback need in common:

- reusable Animator bank slots
- upper-body recovery slots
- layer weights
- lower-body unlock timing
- visual interrupt priority
- cancellation/preemption rules

`LeftGesture` is a masked spell/cast playback route for body-authored left-side gestures that should preserve locomotion without letting the full-body spell layer take over. It uses one `LeftGesture` layer in both stationary and moving states. The mask includes pelvis/spine plus the left shoulder/arm/hand, so the source clip keeps enough posture to read while running without animating the legs or right side. It is a generic authored playback route, not an `INTIMIDATE` special case.

`PlayerAnimator` intentionally keeps the remaining action entry orchestration when it depends on live character and scene state: current grounded state, locomotion speed, combat stance, weapon visual readiness, upper-body state entry, VFX, and trace context. Do not move `PlaySpellAnimation` or `PlayMeleeAnimation` wholesale into `CombatActionPlaybackController`; that would force the playback controller to know about stance and weapon handoff, recreating the double-ownership problem this plan is avoiding.

The aligned sequence is:

1. Done: author spell stance/playback policy as data.
2. Done: extract static animation-set binding to `CombatAnimationSetBinder`.
3. Done: extract hit/status reaction presentation to `CombatStatusReactionController`.
4. Done: migrate hard-CC Animator naming to `HardCrowdControl`.
5. Done: extract the shared combat action playback core to `CombatActionPlaybackController`.
6. Later: extract stance/weapon handoff only if it remains a distinct pressure point after action playback moves out.

This keeps the work aligned with the original "stop and reassess" warning: do not split the character orchestration core blindly, but do not mistake the mechanical cleanup pass for the final architecture.

### 4. Hard crowd control is generic in runtime and controller naming

`INTIMIDATED` intentionally reuses one hard crowd-control loop path and swaps the loop override to the typed status reaction clip. That avoids adding parallel animator booleans for every hard crowd-control type.

The controller migration has been done: the path now uses `IsHardCrowdControlled`, `TriggerHardCrowdControl`, `Base Layer/HardCrowdControlLoop`, and `slot_hard_crowd_control_loop`. Runtime code exposes `SetHardCrowdControl(statusKind)` rather than stun-specific APIs, and `CombatStatusReactionController` owns the parameter/trigger/slot policy.

If more hard CC types arrive, add status reaction data first. Do not add new Animator paths unless the controller actually needs distinct topology.

### 5. Controller transitions and code-driven transitions must not diverge

The controller should not also contain automatic bool-driven draw/sheath transitions for the same states that `PlayerAnimator.SetInCombat` drives. Double ownership was the source of replay/hiccup behavior.

The current direction is: code owns stance transition entry, controller owns transition exits back to idle/combat/empty states.

That contract should be preserved.

## Guardrails

- Do not reintroduce a "drawn but not combat" runtime state unless there is an authored relaxed drawn idle/locomotion set.
- Do not add another debug key that directly mutates weapon mounts or animator stance.
- Do not add dedicated combat action keys that bypass the action bar. Parry, charge, dodge, spells, and selectable abilities should dispatch through loadout/action-bar resolution.
- Do not add a new animator bool per crowd-control type unless the controller actually needs distinct state-machine topology.
- Put hit, stagger, knockdown, and hard crowd-control presentation changes in `CombatStatusReactionController`, not directly in `PlayerAnimator`.
- Put new melee/spell/phased playback machinery behind the shared action playback extraction, not directly in `PlayerAnimator` and not in three prematurely separated controllers.
- Do not move whole `PlaySpellAnimation` or `PlayMeleeAnimation` orchestration into `CombatActionPlaybackController`; extract only pure playback decisions that do not require stance, weapon visuals, live grounded state, or upper-body state entry.
- Prefer authored animation policy rows over inference inside `PlayerAnimator`.
- Keep one owner for stance transition entry. Right now that owner is `PlayerAnimator.SetInCombat`.

## Not Currently A Problem

- `INTIMIDATED` using the stun controller slot is acceptable because the runtime selects the loop clip by status kind.
- `EnterCombatImmediate()` is acceptable for immediate gameplay actions, as long as it remains intentionally scoped.
- Non-looping stance transition clips are correct for draw/sheath/enter/exit transitions.
- Removing the `O` keybind was correct. It was a debugging affordance that pushed the system toward an unsupported third state.
